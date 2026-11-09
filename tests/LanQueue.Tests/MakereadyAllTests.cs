using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq makeready --all: every configured machine, a few at a time, one summary table (or one JSON object).</summary>
public class MakereadyAllTests
{
    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n";

    private const string Fleet = """
        policy:
          allowed_commands: ["dotnet test"]
          allowed_directories: ["."]
        machines:
          - name: alpha
            os: linux
            ssh: build@10.20.30.11
            work_root: /srv/lanq
            capabilities: [dotnet]
          - name: bravo
            os: windows
            ssh: build@10.20.30.12
            work_root: C:/lanq
            capabilities: [dotnet]
          - name: charlie
            os: linux
            ssh: build@10.20.30.13
            work_root: /srv/lanq
            capabilities: [dotnet]
          - name: delta
            os: linux
            ssh: build@10.20.30.14
            work_root: /srv/lanq
            capabilities: [dotnet]
        """;

    /// <summary>A report whose every check is OK.</summary>
    private static string Clean() => MakereadyTests.Report().Replace("\"status\": \"WARN\"", "\"status\": \"OK\"", StringComparison.Ordinal);

    private static async Task<(int Code, string Out, string Err, FakeSsh Ssh)> Run(
        string[] args, Func<Machine, SshResult> probe, Func<Machine, SshResult> report, string yaml = Fleet)
    {
        var ssh = new FakeSsh { Handler = (m, _) => probe(m), InputHandler = (m, _, _) => report(m) };
        var o = new StringWriter();
        var e = new StringWriter();
        var config = ConfigLoader.Parse(yaml);
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", "--all", .. args]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString(), ssh);
    }

    /// <summary>alpha clean, bravo one WARN, charlie paused, delta unreachable.</summary>
    private static Task<(int Code, string Out, string Err, FakeSsh Ssh)> Mixed(params string[] args) => Run(
        args,
        m => m.Name switch
        {
            "charlie" => new SshResult(0, Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal)),
            "delta" => new SshResult(255, "ssh: connect to host 10.20.30.14 port 22: No route to host"),
            _ => new SshResult(0, Healthy),
        },
        m => new SshResult(0, m.Name == "alpha" ? Clean() : MakereadyTests.Report()));

    private static string Row(string text, string machine) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith(machine + " ", StringComparison.Ordinal));

    [Fact]
    public async Task EveryMachineGetsOneRowWithItsOsVerdictAndTopProblem()
    {
        var (code, o, _, ssh) = await Mixed();

        Assert.Equal(70, code); // delta is unreachable
        Assert.Contains("MACHINE", o, StringComparison.Ordinal);
        Assert.Matches(@"^alpha\s+linux\s+OK\s*$", Row(o, "alpha"));
        Assert.Matches(@"^bravo\s+windows\s+WARN 1\s+Pending reboot: a restart is pending$", Row(o, "bravo"));
        Assert.Matches(@"^charlie\s+linux\s+held\s+paused \(a PAUSE file is in its work_root\)", Row(o, "charlie"));
        Assert.Matches(@"^delta\s+linux\s+unreachable\s+ssh: connect to host 10\.20\.30\.14 port 22: No route to host$", Row(o, "delta"));
        Assert.Contains("lanq makeready <machine>", o, StringComparison.Ordinal);

        // Held and unreachable machines are never scanned; the others get the script on stdin as usual.
        Assert.Equal(["alpha", "bravo"], ssh.InputRuns.Select(r => r.Machine.Name).Order(StringComparer.Ordinal));
        Assert.All(ssh.InputRuns, r => Assert.Equal(Cli.MakereadyScript(r.Machine.Os), r.Input));
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task AFailCountsTheFailingChecksAndShowsTheFirstFailure()
    {
        var (code, o, _, _) = await Run([], _ => new SshResult(0, Healthy), _ => new SshResult(0, MakereadyTests.Report(git: "FAIL", archive: "FAIL")));
        Assert.Equal(1, code);
        Assert.Matches(@"^alpha\s+linux\s+FAIL 1\s+OpenSSH Server: sshd$", Row(o, "alpha"));
    }

    /// <summary>Warnings stay in the table but do not fail the run, as in the single-machine mode.</summary>
    [Fact]
    public async Task WarningsAloneExitZero()
    {
        var (code, o, _, _) = await Run([], _ => new SshResult(0, Healthy), _ => new SshResult(0, MakereadyTests.Report()));
        Assert.Equal(0, code);
        Assert.Matches(@"^alpha\s+linux\s+WARN 1", Row(o, "alpha"));
    }

    [Fact]
    public async Task UnreachableOutranksFailAndFailOutranksHeld()
    {
        var fail = MakereadyTests.Report(git: "FAIL", archive: "FAIL");
        var (code, _, _, _) = await Run(
            [],
            m => m.Name == "delta" ? new SshResult(255, "ssh: connect to host 10.20.30.14 port 22: No route to host") : new SshResult(0, Healthy),
            _ => new SshResult(0, fail));
        Assert.Equal(70, code);

        (code, _, _, _) = await Run(
            [],
            m => new SshResult(0, m.Name == "charlie" ? Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal) : Healthy),
            m => new SshResult(0, m.Name == "alpha" ? fail : Clean()));
        Assert.Equal(1, code);

        (code, _, _, _) = await Run(
            ["--json"],
            m => new SshResult(0, m.Name == "charlie" ? Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal) : Healthy),
            _ => new SshResult(0, MakereadyTests.Report()));
        Assert.Equal(75, code); // held plus warnings only
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void TruncationNeverSplitsASurrogatePair(int width)
    {
        var text = new string('a', 7) + string.Concat(Enumerable.Repeat("😀", 6));
        var cut = Cli.Truncate(text, width);
        Assert.True(cut.Length <= width);
        Assert.EndsWith("...", cut, StringComparison.Ordinal);
        for (var i = 0; i < cut.Length; i++)
        {
            if (char.IsHighSurrogate(cut[i]))
            {
                Assert.True(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]), $"lone high surrogate at {i} for width {width}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(cut[i]), $"lone low surrogate at {i} for width {width}");
            }
        }

        Assert.Equal("short", Cli.Truncate("short", width));
    }

    [Fact]
    public async Task ForceScansHeldMachinesWithoutAProbe()
    {
        var (code, _, _, ssh) = await Run(["--force"], _ => new SshResult(0, Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal)), _ => new SshResult(0, Clean()));
        Assert.Equal(0, code);
        Assert.Empty(ssh.Calls);
        Assert.Equal(4, ssh.InputRuns.Count);
    }

    [Fact]
    public async Task ExitCodeIsZeroOnlyWhenEveryMachineIsOk()
    {
        var (code, _, _, _) = await Run([], _ => new SshResult(0, Healthy), _ => new SshResult(0, Clean()));
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task UnreachableWithoutWarningsIsSeventyAndHeldAloneIsSeventyFive()
    {
        var (code, o, _, _) = await Run(
            [],
            m => m.Name == "delta" ? new SshResult(255, "ssh: connect to host 10.20.30.14 port 22: Connection timed out") : new SshResult(0, Healthy),
            _ => new SshResult(0, Clean()));
        Assert.Equal(70, code);
        Assert.Matches(@"^delta\s+linux\s+unreachable", Row(o, "delta"));

        (code, o, _, _) = await Run(
            [],
            m => new SshResult(0, m.Name == "charlie" ? Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal) : Healthy),
            _ => new SshResult(0, Clean()));
        Assert.Equal(75, code);
        Assert.Matches(@"^charlie\s+linux\s+held", Row(o, "charlie"));
    }

    [Fact]
    public async Task AnUnusableReportIsAnErrorRow()
    {
        var (code, o, _, _) = await Run([], _ => new SshResult(0, Healthy), m => new SshResult(0, m.Name == "bravo" ? "not json" : Clean()));
        Assert.Equal(70, code);
        Assert.Matches(@"^bravo\s+windows\s+error\s+unusable lanq-makeready report \(not valid JSON", Row(o, "bravo"));
    }

    [Fact]
    public async Task JsonIsOneObjectWithEveryMachineAndTheValidatedReports()
    {
        var (code, o, _, _) = await Mixed("--json");
        Assert.Equal(70, code);

        using var doc = JsonDocument.Parse(o);
        var root = doc.RootElement;
        Assert.Equal("InfraError", root.GetProperty("status").GetString());
        Assert.Equal(70, root.GetProperty("exitCode").GetInt32());
        var machines = root.GetProperty("machines").EnumerateArray().ToList();
        Assert.Equal(["alpha", "bravo", "charlie", "delta"], machines.Select(m => m.GetProperty("machine").GetString()));
        Assert.Equal(["OK", "WARN", "held", "unreachable"], machines.Select(m => m.GetProperty("verdict").GetString()));
        Assert.Equal([0, 1, 0, 0], machines.Select(m => m.GetProperty("warnings").GetInt32()));
        Assert.Equal([0, 0, 0, 0], machines.Select(m => m.GetProperty("failures").GetInt32()));
        Assert.Equal([0, 0, 75, 70], machines.Select(m => m.GetProperty("exitCode").GetInt32()));
        Assert.Equal("windows", machines[1].GetProperty("os").GetString());
        Assert.Equal("Pending reboot: a restart is pending", machines[1].GetProperty("problem").GetString());

        // The report is the same validated lanq-makeready/v1 object `lanq makeready <machine> --json` prints, field for field.
        var report = machines[1].GetProperty("report");
        Assert.Equal("lanq-makeready/v1", report.GetProperty("schema").GetString());
        Assert.Equal("OK", report.GetProperty("verdicts").GetProperty("worker_git").GetString());
        Assert.Equal("/srv/lq", report.GetProperty("suggestedMachine").GetProperty("work_root").GetString());
        Assert.False(machines[2].TryGetProperty("report", out _));
        Assert.False(machines[3].TryGetProperty("report", out _));
        Assert.Contains("No route to host", machines[3].GetProperty("problem").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonStatusFollowsTheExitCode()
    {
        var (code, o, _, _) = await Run(["--json"], _ => new SshResult(0, Healthy), _ => new SshResult(0, Clean()));
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("Ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("machines").GetArrayLength());
    }

    [Fact]
    public async Task JsonPrintsOneErrorObjectWhenTheConfigCannotBeLoaded()
    {
        var o = new StringWriter();
        var ssh = new FakeSsh();
        var code = await Cli.Makeready(
            ParsedArgs.Parse(["makeready", "--all", "--json"]), () => throw new ConfigException("No machines configured."), ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);
        Assert.Equal(2, code);
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("--ssh", "me@10.20.30.40", "--os", "linux")]
    public async Task AllTakesNoMachineAndNoSsh(params string[] args)
    {
        var (code, _, e, ssh) = await Run(args, _ => new SshResult(0, Healthy), _ => new SshResult(0, Clean()));
        Assert.Equal(2, code);
        Assert.Contains("--all", e, StringComparison.Ordinal);
        Assert.Empty(ssh.InputRuns);
    }

    [Fact]
    public async Task ScansRunAFewAtATime()
    {
        var yaml = Fleet + string.Concat(Enumerable.Range(5, 6).Select(i => $"""

              - name: m{i}
                os: linux
                ssh: build@10.20.30.{i + 20}
                work_root: /srv/lanq
                capabilities: [dotnet]
            """));
        var running = 0;
        var peak = 0;
        var ssh = new SlowInputSsh(async () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return new SshResult(0, Clean());
        });
        var config = ConfigLoader.Parse(yaml);
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", "--all", "--force"]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal(10, ssh.Count);
        Assert.InRange(peak, 2, Cli.MakereadyParallelism);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class SlowInputSsh(Func<Task<SshResult>> report) : ISshClient
    {
        private int _count;

        public int Count => _count;

        public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new SshResult(0, Healthy));

        public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public Task<SshResult> RunSplitAsync(
            Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return await report().ConfigureAwait(false);
        }
    }
}
