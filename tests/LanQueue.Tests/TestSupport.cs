using LanQueue.Config;
using LanQueue.Ssh;

namespace LanQueue.Tests;

internal static class T
{
    public const string Yaml = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["dotnet build", "dotnet test", "npm test"]
          allowed_directories: [".", "src", "tests"]
        machines:
          - name: build-01
            os: linux
            ssh: user@build-01.lan
            key_env: LANQ_KEY_BUILD01
            work_root: /srv/lanq
            capabilities: [dotnet, node]
            max_jobs: 2
          - name: build-02
            os: windows
            ssh: user@build-02.lan
            work_root: D:/lanq
            capabilities: [dotnet]
            allowed_hours: "22:00-07:00"
        """;

    public static FleetConfig Config() => ConfigLoader.Parse(Yaml);

    public static Machine Linux() => Config().Machines[0];

    public static Machine Windows() => Config().Machines[1];
}

internal sealed class FakeSsh : ISshClient
{
    public List<(string Machine, string Command)> Calls { get; } = [];

    public List<string> Uploads { get; } = [];

    public Func<Machine, string, SshResult> Handler { get; set; } = (_, _) => new SshResult(0, "");

    public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((machine.Name, remoteCommand));
        var r = Handler(machine, remoteCommand);
        foreach (var l in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            onLine?.Invoke(l);
        }

        return Task.FromResult(r);
    }

    public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct)
    {
        Uploads.Add(machine.Name + ":" + remotePath);
        return Task.FromResult(new SshResult(0, ""));
    }
}

/// <summary>
/// Health probes of build-01 block until build-02's probe has started, then finish after it. Sequential probing (build-01 first)
/// cannot get past build-01 and fails with a TimeoutException; concurrent probing completes, with build-02 finishing first.
/// </summary>
internal sealed class GatedProbeSsh : ISshClient
{
    private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Finished { get; } = [];

    public async Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        if (IsProbe(machine, remoteCommand))
        {
            if (machine.Name == "build-01")
            {
                await _secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                await Task.Delay(50, ct);
            }
            else
            {
                _secondStarted.TrySetResult();
            }

            lock (Finished)
            {
                Finished.Add(machine.Name);
            }

            var load = machine.Name == "build-01" ? "0.5" : "0.1";
            return new SshResult(0, string.Join((char)10, "load=" + load, "disk_free_gb=50", "slots_used=0", "tool=ok"));
        }

        return Decode(machine, remoteCommand).Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "");
    }

    public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(new SshResult(0, ""));

    private static bool IsProbe(Machine m, string cmd) => Decode(m, cmd).Contains("disk_free_gb", StringComparison.Ordinal);

    private static string Decode(Machine m, string cmd) =>
        m.Os == MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
