using System.Diagnostics;
using System.Text;
using LanQueue.Config;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Runs only where the local git can create a reftable repository (git 2.45 or later); otherwise reported as skipped.</summary>
public sealed class ReftableGitFactAttribute : FactAttribute
{
    private static readonly bool Supported = Probe();

    public ReftableGitFactAttribute()
    {
        if (!Supported)
        {
            Skip = "This git cannot create a reftable repository (needs git 2.45 or later).";
        }
    }

    private static bool Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanq-reftable-" + Guid.NewGuid().ToString("N"));
        try
        {
            return Shells.Exec("git", ["init", "-q", "--bare", "--ref-format=reftable", dir]).Code == 0;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

/// <summary>The shells a test can run generated worker scripts with, found once; a test that needs a missing one is reported as skipped.</summary>
internal static class Shells
{
    /// <summary>A POSIX sh on PATH (on Windows, the one Git for Windows ships), or null.</summary>
    public static readonly string? Sh = OnPath("sh") ? "sh" : null;

    /// <summary>powershell.exe on Windows, pwsh elsewhere when it is on PATH, or null.</summary>
    public static readonly string? PowerShell = OperatingSystem.IsWindows() ? "powershell.exe" : OnPath("pwsh") ? "pwsh" : null;

    public const string NoSh = "No POSIX sh on this machine.";

    public const string NoPowerShell = "No PowerShell on this machine (powershell.exe on Windows, or pwsh on PATH).";

    /// <summary>Runs a generated script: "linux" in sh -c, "windows" from a temporary .ps1 file (with BOM) in PowerShell. Output + error.</summary>
    public static (int Code, string Output) Run(string shell, string script, IReadOnlyDictionary<string, string>? env = null)
    {
        if (shell == "linux")
        {
            return Exec(Sh ?? throw new InvalidOperationException(NoSh), ["-c", script], env);
        }

        var ps1 = Path.Combine(Path.GetTempPath(), "lanq-script-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(ps1, script, new UTF8Encoding(true));
        try
        {
            return Exec(PowerShell ?? throw new InvalidOperationException(NoPowerShell), ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1], env);
        }
        finally
        {
            File.Delete(ps1);
        }
    }

    /// <summary>
    /// A test-level guard: no generated script may run longer than this. A script that hangs (a FIFO read, a link loop, a kill that did not
    /// happen) is killed with its tree and the test fails with a TimeoutException instead of spinning for an hour.
    /// </summary>
    public static readonly TimeSpan Guard = TimeSpan.FromMinutes(5);

    public static (int Code, string Output) Exec(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(Guard) || !Task.WhenAll(output, err).Wait(Guard))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited in the meantime
            }

            throw new TimeoutException($"{file} ran longer than the {Guard.TotalMinutes} minute test guard; killed.");
        }

        p.WaitForExit();
        return (p.ExitCode, output.Result + err.Result);
    }

    internal static bool OnPath(string exe)
    {
        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe + ".cmd" } : new[] { exe };
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => names.Any(n => File.Exists(Path.Combine(d, n))));
    }
}

/// <summary>Runs only where a POSIX sh is on PATH; otherwise reported as skipped.</summary>
public sealed class ShFactAttribute : FactAttribute
{
    public ShFactAttribute()
    {
        if (Shells.Sh is null)
        {
            Skip = Shells.NoSh;
        }
    }
}

/// <summary>Runs only where PowerShell is available; otherwise reported as skipped.</summary>
public sealed class PowerShellFactAttribute : FactAttribute
{
    public PowerShellFactAttribute()
    {
        if (Shells.PowerShell is null)
        {
            Skip = Shells.NoPowerShell;
        }
    }
}

/// <summary>Runs only on Linux (real process groups, setsid, a case-sensitive file system); otherwise reported as skipped.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only (the Linux CI leg and the sdk container run it).";
        }
    }
}

/// <summary>Runs only on Windows (real Windows PowerShell 5.1 and taskkill); otherwise reported as skipped.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only (the Windows CI leg runs it).";
        }
    }
}

internal static class T
{
    public const string RepoId = "0123456789abcdef";

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

    /// <summary>The timeout of every streaming stdin run, in order.</summary>
    public List<TimeSpan> StreamingTimeouts { get; } = [];

    /// <summary>
    /// A split run (the fetch) goes through <see cref="Handler"/> and <see cref="Calls"/> like any command; with stdin (the Windows fetch) as
    /// "STDIN:" plus the script it carries (decoded from the base64 UTF-16 input when it is that). Output lines starting with "STDERR:" go to
    /// <paramref name="onStderr"/> (without the marker), all others to <paramref name="onStdout"/>. <see cref="RunAsync"/> instead delivers
    /// every line, markers included, to one callback, as ssh merging both streams would.
    /// </summary>
    public Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct)
    {
        var command = remoteCommand;
        if (input is { } bytes)
        {
            var text = Encoding.ASCII.GetString(bytes.Span).Trim();
            try
            {
                text = Encoding.Unicode.GetString(Convert.FromBase64String(text));
            }
            catch (FormatException)
            {
                // not base64: keep the raw text
            }

            StreamingTimeouts.Add(timeout);
            command = "STDIN:" + text;
        }

        return RunAsync(machine, command, l =>
        {
            if (l.StartsWith("STDERR:", StringComparison.Ordinal))
            {
                onStderr(l["STDERR:".Length..]);
            }
            else
            {
                onStdout(l);
            }
        }, timeout, ct);
    }

    /// <summary>Every stdin run: the target, the remote command, the bytes sent, the output limit and the timeout.</summary>
    public List<(Machine Machine, string Command, byte[] Input, int MaxOutputChars, TimeSpan Timeout)> InputRuns { get; } = [];

    public Func<Machine, string, byte[], SshResult> InputHandler { get; set; } = (_, _, _) => new SshResult(0, "");

    public Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct)
    {
        var bytes = input.ToArray();
        lock (InputRuns)
        {
            InputRuns.Add((machine, remoteCommand, bytes, maxOutputChars, timeout));
        }

        return Task.FromResult(InputHandler(machine, remoteCommand, bytes));
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

/// <summary>A fixed instant; the local zone is UTC unless a (fixed-offset, DST-free) zone is given.</summary>
internal sealed class FakeClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
}

/// <summary>Test folders that may hold symlinks, junctions and symlink loops.</summary>
internal static class TestTree
{
    /// <summary>
    /// Removes every link first (the link itself, never what it points at; Windows refuses a recursive delete through a junction made by
    /// mklink /J), walking without ever descending into one, so a planted symlink loop cannot make the walk endless. Then deletes the rest.
    /// </summary>
    public static void Delete(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.Count > 0)
        {
            foreach (var e in pending.Pop().EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
            {
                if (e.LinkTarget is not null || e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (e is DirectoryInfo)
                    {
                        Directory.Delete(e.FullName);
                    }
                    else
                    {
                        File.Delete(e.FullName);
                    }
                }
                else if (e is DirectoryInfo d)
                {
                    pending.Push(d);
                }
            }
        }

        Directory.Delete(root, recursive: true);
    }
}
