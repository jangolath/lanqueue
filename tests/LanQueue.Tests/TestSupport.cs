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

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
