using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// A pool of Linux workers that behaves like the real scripts where sharding depends on it: a slot is claimed only while one is free
/// (SLOT=FULL otherwise), cleanup releases it, and the probe reports the slots in use. Job runs go to <see cref="OnExecute"/>, which tests
/// make wait on events (never on time windows).
/// </summary>
internal sealed partial class PoolSsh : ISshClient
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Machine, string Command)> Calls { get; } = new();

    public ConcurrentQueue<string> Uploads { get; } = new();

    /// <summary>Every job run, in the order they started.</summary>
    public ConcurrentQueue<Exec> Executes { get; } = new();

    /// <summary>Most slots each machine had in use at once.</summary>
    public ConcurrentDictionary<string, int> Peak { get; } = new(StringComparer.Ordinal);

    public Func<Exec, CancellationToken, Task<SshResult>> OnExecute { get; set; } = (e, _) => Task.FromResult(e.Exit(0));

    /// <summary>True makes this slot claim answer SLOT=FULL although a slot is free (the probe said free, the machine said full).</summary>
    public Func<Machine, bool> RefuseSlot { get; set; } = _ => false;

    /// <summary>The result of each upload; tests block or fail it here.</summary>
    public Func<Machine, CancellationToken, Task<SshResult>> OnUpload { get; set; } = (_, _) => Task.FromResult(new SshResult(0, ""));

    /// <summary>The result of each prepare step (git bundle fetch or archive extract), or null for success.</summary>
    public Func<Machine, SshResult?> OnPrepare { get; set; } = _ => null;

    /// <summary>The result of each cleanup; a failed cleanup keeps the slot taken, as on a machine that stopped answering.</summary>
    public Func<Machine, SshResult> OnCleanup { get; set; } = _ => new SshResult(0, "");

    /// <summary>Extra probe lines per machine (memory, cores), added to load, disk, slots and tool.</summary>
    public Func<Machine, string> ProbeExtra { get; set; } = _ => "mem_free_gb=3";

    public int Used(string machine)
    {
        lock (_gate)
        {
            return _used.GetValueOrDefault(machine);
        }
    }

    public sealed record Exec(Machine Machine, string JobId, string Command)
    {
        public int? Shard => Regex.Match(Command, "LANQ_SHARD=([0-9]+)", RegexOptions.None, TimeSpan.FromSeconds(5)) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

        /// <summary>The wrapper's exit marker as this run's script prints it (with the run's nonce), before the exit code.</summary>
        public string Marker => T.ExitMarkerOf(Command);

        /// <summary>What the job wrapper prints when the job exits with <paramref name="code"/>: its output, then the final exit marker.</summary>
        public SshResult Exit(int code) => new(code, $"output of {JobId}\n{Marker}{code.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    [GeneratedRegex("LANQ_EXIT=([A-Za-z0-9_-]+):", RegexOptions.None, 5000)]
    private static partial Regex ExitMarker();

    public async Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Enqueue((machine.Name, remoteCommand));
        SshResult r;
        if (ExitMarker().Match(remoteCommand) is { Success: true } job)
        {
            var exec = new Exec(machine, job.Groups[1].Value, remoteCommand);
            Executes.Enqueue(exec);
            r = await OnExecute(exec, ct);
        }
        else if (remoteCommand.Contains("disk_free_gb", StringComparison.Ordinal))
        {
            r = new SshResult(0, $"load=0.1\ndisk_free_gb=50\nslots_used={Used(machine.Name)}\n{ProbeExtra(machine)}\ntool=ok");
        }
        else if (remoteCommand.Contains("SLOT=", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                var used = _used.GetValueOrDefault(machine.Name);
                if (used < machine.MaxJobs && !RefuseSlot(machine))
                {
                    _used[machine.Name] = used + 1;
                    Peak.AddOrUpdate(machine.Name, used + 1, (_, p) => Math.Max(p, used + 1));
                    r = new SshResult(0, "SLOT=" + (used + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    r = new SshResult(0, "SLOT=FULL");
                }
            }
        }
        else if (remoteCommand.Contains("/locks/slot-", StringComparison.Ordinal))
        {
            // Cleanup (git or archive): releases the slot unless it failed.
            r = OnCleanup(machine);
            if (r.Ok)
            {
                lock (_gate)
                {
                    _used[machine.Name] = _used.GetValueOrDefault(machine.Name) - 1;
                }
            }
        }
        else if (remoteCommand.Contains("bundle verify", StringComparison.Ordinal) || remoteCommand.Contains("unsafe archive entry", StringComparison.Ordinal))
        {
            r = OnPrepare(machine) ?? new SshResult(0, "");
        }
        else
        {
            r = new SshResult(0, "");
        }

        foreach (var l in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            onLine?.Invoke(l);
        }

        return r;
    }

    public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct)
    {
        Uploads.Enqueue(machine.Name + ":" + remotePath);
        return OnUpload(machine, ct);
    }

    public Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(machine, remoteCommand, onStdout, timeout, ct);
}

/// <summary>Counts events and completes once a number has been reached: a test waits for "all N started" instead of for time.</summary>
internal sealed class Countdown(int target)
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public Task Reached => _reached.Task;

    public void Signal()
    {
        if (Interlocked.Increment(ref _count) >= target)
        {
            _reached.TrySetResult();
        }
    }
}

public sealed class ShardTests : IDisposable
{
    private const int TestTimeout = 60_000;

    internal const string PoolYaml = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npx jest", "dotnet test1", "dotnet test3"]
          allowed_directories: ["."]
        machines:
          - name: tiny-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [node, dotnet]
            max_jobs: 1
          - name: big-01
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [node, dotnet, docker]
            max_jobs: 4
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-shard-" + Guid.NewGuid().ToString("N"));

    private readonly ConcurrentQueue<string> _output = new();

    public ShardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (JobRunner Runner, PoolSsh Ssh, RunnerTests.FakePayloads Payloads) Pool(string yaml = PoolYaml)
    {
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");
        var runner = new JobRunner(
            ConfigLoader.Parse(yaml), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), Path.Combine(_dir, "tmp"),
            _output.Enqueue, delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), bases: new BaseStore(null));
        return (runner, ssh, payloads);
    }

    private JobRequest Jest(TimeSpan? wait = null) => new(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", _dir, wait ?? TimeSpan.Zero);

    /// <summary>Each shard runs once with its own job id, {shard}/{shards} substituted in its arguments and LANQ_SHARD / LANQ_SHARDS set.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task EachShardGetsItsIndexInTheArgumentsAndTheEnvironment()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(4), CancellationToken.None);

        Assert.Equal(("Completed", 0, 4), (r.Status, r.ExitCode, r.Count));
        Assert.Equal([1, 2, 3, 4], r.Shards.Select(s => s.Index));
        Assert.Equal(4, ssh.Executes.Count);
        foreach (var e in ssh.Executes)
        {
            var i = e.Shard!.Value;
            Assert.Contains($"--shard={i}/4", e.Command, StringComparison.Ordinal);
            Assert.Contains("LANQ_SHARDS=4", e.Command, StringComparison.Ordinal);
            Assert.Contains("LANQ_JOB=" + e.JobId, e.Command, StringComparison.Ordinal);
            Assert.DoesNotContain("{shard", e.Command, StringComparison.Ordinal);
            Assert.Equal(e.JobId, r.Shards[i - 1].JobId);
        }

        Assert.Equal(4, ssh.Executes.Select(e => e.JobId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, ssh.Executes.Select(e => e.Shard).Distinct().Count());
        Assert.Contains(_output, l => l.StartsWith("[2/4 ", StringComparison.Ordinal) && l.Contains("output of", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaceholdersAreLiteralTokensOnly()
    {
        var s = new ShardInfo(3, 12);
        Assert.Equal(
            ["--shard=3/12", "3", "12", "x3y12z", "{SHARD}", "{ shard }", "$3x", "$(echo 12)", "{3}", "%shard%", "{shard"],
            s.Substitute(["--shard={shard}/{shards}", "{shard}", "{shards}", "x{shard}y{shards}z", "{SHARD}", "{ shard }", "${shard}x", "$(echo {shards})", "{{shard}}", "%shard%", "{shard"]));
    }

    [Fact]
    public void TheShardEnvironmentSitsNextToLanqJobOnBothSystems()
    {
        var linux = RemoteScripts.Execute(T.Linux(), "j1", ".", ["npx", "jest"], null, new ShardInfo(3, 8));
        Assert.Contains("'LANQ_JOB=j1' 'LANQ_SHARD=3' 'LANQ_SHARDS=8' ", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("LANQ_SHARD", RemoteScripts.Execute(T.Linux(), "j1", ".", ["npx", "jest"]), StringComparison.Ordinal);

        var windows = RemoteScripts.Execute(T.Windows(), "j1", ".", ["npx", "jest"], null, new ShardInfo(3, 8));
        var job = windows.IndexOf("$env:LANQ_JOB = 'j1'", StringComparison.Ordinal);
        Assert.True(job > 0);
        Assert.True(windows.IndexOf("$env:LANQ_SHARD = '3'", StringComparison.Ordinal) > job);
        Assert.Contains("$env:LANQ_SHARDS = '8'", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("LANQ_SHARD", RemoteScripts.Execute(T.Windows(), "j1", ".", ["npx", "jest"]), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1", ".", ["npx"], null, new ShardInfo(5, 4)));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Windows(), "j1", ".", ["npx"], null, new ShardInfo(65, 65)));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1", ".", ["npx"], null, new ShardInfo(0, 4)));
    }

    /// <summary>The largest shard environment still leaves the Windows job and prepare scripts under the 7200-character guard.</summary>
    [Fact]
    public void TheWindowsScriptsWithTheShardEnvironmentStayUnderTheGuard()
    {
        const string Job = "20261109-221503-a1b2c3";
        var m = T.Windows();
        m.Env["NODE_OPTIONS"] = "--max-old-space-size=4096";
        m.PathPrepend.Add("C:/Program Files/nodejs");
        var execute = RemoteScripts.Wrap(m, RemoteScripts.Execute(m, Job, "src", ["npx", "jest", "--shard=64/64"], TimeSpan.FromHours(4), new ShardInfo(64, 64))).Length;
        var prepare = RemoteScripts.Wrap(m, RemoteScripts.Prepare(m, Job, T.RepoId, "20261109-221500-ffffff")).Length;
        Assert.True(execute <= 7200, $"execute {execute}");
        Assert.True(prepare <= 7200, $"prepare {prepare}");
    }

    /// <summary>The generated Linux job script really hands LANQ_SHARD and LANQ_SHARDS to the job.</summary>
    [LinuxFact]
    public void ARealShJobSeesItsShard()
    {
        var root = Path.Combine(_dir, "worker");
        Directory.CreateDirectory(Path.Combine(root, "jobs", "j1"));
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        var m = T.Linux();
        m.WorkRoot = root;
        var output = T.JobOutput(WorkerHealthTests.Capture("sh", "-c", RemoteScripts.Execute(m, "j1", ".", ["sh", "-c", "printf '%s/%s' \"$LANQ_SHARD\" \"$LANQ_SHARDS\""], null, new ShardInfo(2, 5))));
        Assert.Equal("2/5", output);
    }

    /// <summary>The generated Windows job script really hands LANQ_SHARD and LANQ_SHARDS to the job (and its child processes).</summary>
    [WindowsFact]
    public void ARealPowerShellJobSeesItsShard()
    {
        var root = Path.Combine(_dir, "worker").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(root, "jobs", "j1"));
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        var m = T.Windows();
        m.WorkRoot = root;
        var script = RemoteScripts.Execute(m, "j1", ".", ["hostname"], null, new ShardInfo(2, 5))
            .Replace("& 'hostname'", "[Console]::Out.Write($env:LANQ_SHARD + '/' + $env:LANQ_SHARDS)", StringComparison.Ordinal);
        var file = Path.Combine(_dir, "shard.ps1");
        File.WriteAllText(file, "\ufeff" + script);
        Assert.Equal("2/5", T.JobOutput(WorkerHealthTests.Capture("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file)));
    }

    /// <summary>One shard's substituted command line breaking policy refuses the whole run before anything is built or sent.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task OneShardRefusedByPolicyRefusesTheRunBeforeAnythingIsSent()
    {
        var (runner, ssh, payloads) = Pool();
        var r = await runner.RunShardsAsync(new JobRequest(["dotnet"], ["dotnet", "test{shard}"], ".", _dir, TimeSpan.Zero), new ShardOptions(3), CancellationToken.None);

        Assert.Equal(("Rejected", 2), (r.Status, r.ExitCode));
        Assert.Contains("shard 2/3", r.Message, StringComparison.Ordinal);
        Assert.Contains("allowed_commands", r.Message, StringComparison.Ordinal);
        Assert.Empty(r.Shards);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);

        // Shards 1 and 3 alone are fine.
        var ok = await runner.RunShardsAsync(new JobRequest(["dotnet"], ["dotnet", "test{shard}"], ".", _dir, TimeSpan.Zero), new ShardOptions(1), CancellationToken.None);
        Assert.Equal("Completed", ok.Status);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("a\n{shard}")]
    [InlineData("--dangerously-skip-permissions={shard}")]
    [InlineData("TOKEN=ghp_0123456789abcdefghijklmnopqrstuvwxyzAB{shard}")]
    public async Task UnsafeArgumentsAreStillRefusedWithPlaceholders(string arg)
    {
        var (runner, ssh, payloads) = Pool();
        var r = await runner.RunShardsAsync(new JobRequest(["node"], ["npx", "jest", arg], ".", _dir, TimeSpan.Zero), new ShardOptions(2), CancellationToken.None);
        Assert.Equal(("Rejected", 2), (r.Status, r.ExitCode));
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("-1")]
    [InlineData("+3")]
    [InlineData(" 3")]
    [InlineData("three")]
    [InlineData("1e1")]
    public void AShardCountOutsideOneToSixtyFourIsRefused(string n)
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--shards", n, "--", "npx", "jest"]));
        Assert.Contains("1 to 64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShardOptionsParse()
    {
        Assert.Equal(new ShardOptions(1), ParsedArgs.Parse(["run", "--shards", "1", "--", "x"]).Shards);
        Assert.Equal(new ShardOptions(64, 3, true), ParsedArgs.Parse(["run", "--shards", "64", "--shard-retries", "3", "--fail-fast", "--", "x"]).Shards);
        Assert.Equal(new ShardOptions(null), ParsedArgs.Parse(["run", "--shards", "auto", "--", "x"]).Shards);
        Assert.Equal(new ShardOptions(null, TargetMinutes: 1440), ParsedArgs.Parse(["run", "--shards", "auto", "--target-minutes", "1440", "--", "x"]).Shards);
        Assert.Null(ParsedArgs.Parse(["run", "--", "x"]).Shards);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--shards", "2", "--shard-retries", "4", "--", "x"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--shard-retries", "1", "--", "x"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--fail-fast", "--", "x"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--shards", "2"]));
    }

    /// <summary>A bad --shards with --json still prints exactly one object, with an empty shards array.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ABadShardCountWithJsonPrintsOneObject()
    {
        var stdout = new StringWriter();
        var code = await Cli.RunAsync(["run", "--shards", "65", "--json", "--", "npx", "jest"], stdout, new StringWriter(), CancellationToken.None);
        Assert.Equal(2, code);
        var line = Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("shards").GetArrayLength());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ACountAboveTheCapIsRefusedByTheRunnerToo()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(65), CancellationToken.None);
        Assert.Equal(("Rejected", 2), (r.Status, r.ExitCode));
        Assert.Empty(ssh.Calls);
    }

    /// <summary>
    /// The snapshot is taken once and each bundle at most once for the whole run; every shard's prepare fetches the same shared ref into its
    /// own; the files are deleted at the end.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ThePayloadIsBuiltOnceAndSharedByEveryShard()
    {
        var (runner, ssh, payloads) = Pool();
        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(5), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.Equal(1, payloads.Built);
        Assert.Equal(payloads.BundleBases.Count, payloads.BundleBases.Distinct().Count()); // no bundle built twice
        Assert.True(payloads.BundleBases.Count <= 2, string.Join(",", payloads.BundleBases)); // a full one, then one incremental on HEAD
        var refs = ssh.Calls.Select(c => Regex.Match(c.Command, @"B='\\''([A-Za-z0-9_-]+)'\\''", RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(5, refs.Count);
        var shared = Assert.Single(refs.Distinct(StringComparer.Ordinal));
        Assert.DoesNotContain(shared, r.Shards.Select(s => s.JobId));
        Assert.Equal(5, ssh.Uploads.Count);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "tmp")));
    }

    /// <summary>
    /// Five shards on a 1-slot box and a 4-slot box, equally loaded: all five run at once (each job waits until all five have started, so a
    /// scheduler that placed fewer would never finish), one on the small box and four on the big one.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardsFillEverySlotOneOnTheSmallBoxAndFourOnTheBigOne()
    {
        var (runner, ssh, _) = Pool();
        var started = new Countdown(5);
        ssh.OnExecute = async (e, ct) =>
        {
            started.Signal();
            await started.Reached.WaitAsync(ct);
            return e.Exit(0);
        };

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(5), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.Equal(1, r.Shards.Count(s => s.Machine == "tiny-01"));
        Assert.Equal(4, r.Shards.Count(s => s.Machine == "big-01"));
        Assert.Equal(1, ssh.Peak["tiny-01"]);
        Assert.Equal(4, ssh.Peak["big-01"]);
        Assert.Equal(0, ssh.Used("big-01"));
    }

    /// <summary>More shards than slots: the rest wait for a sibling to finish (even with --wait 0), and each later takes a freed slot.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardsBeyondTheFreeSlotsWaitForASiblingNotForWait()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(12), CancellationToken.None);

        Assert.Equal(("Completed", 0), (r.Status, r.ExitCode));
        Assert.Equal(12, ssh.Executes.Count);
        Assert.True(ssh.Peak["big-01"] <= 4 && ssh.Peak["tiny-01"] <= 1);
    }

    /// <summary>One failing shard does not stop the others; the run exits with the failing shard's own code.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AFailingShardDecidesTheExitCodeAndTheOthersStillRun()
    {
        var (runner, ssh, _) = Pool();
        ssh.OnExecute = (e, _) => Task.FromResult(e.Exit(e.Shard == 2 ? 3 : 0));

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(4), CancellationToken.None);

        Assert.Equal(("Failed", 3), (r.Status, r.ExitCode));
        Assert.Equal(4, ssh.Executes.Count);
        Assert.Equal(["Completed", "Failed", "Completed", "Completed"], r.Shards.Select(s => s.Status));
        Assert.Contains("3 of 4 shard(s) completed", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverallStatusIsTheMostTellingShard()
    {
        static ShardResult S(int i, string status, int code) => new(i, status, code, "m", "j" + i, TimeSpan.Zero, 1, "");

        Assert.Equal(("Completed", 0), Pick(JobRunner.Aggregate(2, [S(1, "Completed", 0), S(2, "Completed", 0)], false)));
        Assert.Equal(("Failed", 1), Pick(JobRunner.Aggregate(3, [S(1, "InfraError", 70), S(2, "Failed", 1), S(3, "Failed", 4)], false)));
        Assert.Equal(("TimedOut", 124), Pick(JobRunner.Aggregate(2, [S(1, "NoCapacity", 75), S(2, "TimedOut", 124)], false)));
        Assert.Equal(("InfraError", 70), Pick(JobRunner.Aggregate(2, [S(1, "NoCapacity", 75), S(2, "InfraError", 70)], false)));
        Assert.Equal(("NoCapacity", 75), Pick(JobRunner.Aggregate(2, [S(1, "Completed", 0), S(2, "NoCapacity", 75)], false)));
        Assert.Equal(("Failed", 2), Pick(JobRunner.Aggregate(2, [S(1, "Cancelled", 130), S(2, "Failed", 2)], false)));
        Assert.Equal(("Cancelled", 130), Pick(JobRunner.Aggregate(2, [S(1, "Failed", 1), S(2, "Cancelled", 130)], true)));

        static (string, int) Pick(ShardRunResult r) => (r.Status, r.ExitCode);
    }

    /// <summary>--json prints exactly one object: overall status and exit code, the count, and one RunJson-shaped entry per shard.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task JsonIsOneObjectWithAShardsArray()
    {
        var ssh = new PoolSsh { OnExecute = (e, _) => Task.FromResult(e.Exit(e.Shard == 3 ? 1 : 0)) };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var p = ParsedArgs.Parse(["run", "--needs", "node", "--shards", "3", "--json", "--fetch", "junit.xml", "--fetch-dir", Path.Combine(_dir, "out"), "--", "npx", "jest", "--shard={shard}/{shards}"]);
        var code = await Cli.Run(p, ConfigLoader.Parse(PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, code);
        var line = Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("Failed", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(3, root.GetProperty("shardCount").GetInt32());
        Assert.True(root.TryGetProperty("durationSeconds", out _));
        Assert.True(root.TryGetProperty("message", out _));
        var shards = root.GetProperty("shards").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], shards.Select(s => s.GetProperty("shard").GetInt32()));
        foreach (var s in shards)
        {
            foreach (var field in new[] { "status", "exitCode", "machine", "jobId", "durationSeconds", "message", "attempts", "fetched", "fetchSkipped" })
            {
                Assert.True(s.TryGetProperty(field, out _), field);
            }
        }

        Assert.Equal("Failed", shards[2].GetProperty("status").GetString());
        Assert.Contains("[lanq] shard", stderr.ToString(), StringComparison.Ordinal); // the table goes with the job output (stderr under --json)
        Assert.Contains("[lanq] Failed:", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Without --json the job output and the summary table go to stdout, the status line to stderr.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TheSummaryTableListsEveryShard()
    {
        var ssh = new PoolSsh();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var p = ParsedArgs.Parse(["run", "--needs", "node", "--shards", "2", "--", "npx", "jest", "--shard={shard}/{shards}"]);
        var code = await Cli.Run(p, ConfigLoader.Parse(PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, code);
        var text = stdout.ToString();
        Assert.Contains("[lanq] shard   machine          status        exit  duration", text, StringComparison.Ordinal);
        Assert.Matches(@"\[lanq\] 1/2 +(tiny-01|big-01) +Completed +0 +[0-9.]+s", text);
        Assert.Matches(@"\[lanq\] 2/2 +(tiny-01|big-01) +Completed +0 +[0-9.]+s", text);
        Assert.Contains("[lanq] Completed: 2 of 2 shard(s) completed", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A shard whose machine stopped answering (ssh 255, no exit marker) is re-run as a new job when --shard-retries allows it.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ALostConnectionIsRetriedOnlyWithShardRetries()
    {
        var lost = 0;
        Task<SshResult> LoseShard2Once(PoolSsh.Exec e, CancellationToken _) =>
            Task.FromResult(e.Shard == 2 && Interlocked.Increment(ref lost) == 1 ? new SshResult(255, "Connection to 10.20.30.12 closed by remote host.") : e.Exit(0));

        var (runner, ssh, _) = Pool();
        ssh.OnExecute = LoseShard2Once;
        var retried = await runner.RunShardsAsync(Jest(), new ShardOptions(3, Retries: 1), CancellationToken.None);
        Assert.Equal(("Completed", 0), (retried.Status, retried.ExitCode));
        Assert.Equal([1, 2, 1], retried.Shards.Select(s => s.Attempts));
        Assert.Equal(4, ssh.Executes.Count);
        Assert.Contains(_output, l => l.Contains("--shard-retries", StringComparison.Ordinal));

        lost = 0;
        var (noRetry, ssh2, _) = Pool();
        ssh2.OnExecute = LoseShard2Once;
        var r = await noRetry.RunShardsAsync(Jest(), new ShardOptions(3), CancellationToken.None);
        Assert.Equal(("InfraError", 70), (r.Status, r.ExitCode));
        Assert.Equal(3, ssh2.Executes.Count);
    }

    /// <summary>A test failure is never retried, nor is a job that itself exited 255 (its exit marker says so).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AJobsOwnFailureIsNeverRetried()
    {
        var (runner, ssh, _) = Pool();
        ssh.OnExecute = (e, _) => Task.FromResult(e.Shard switch { 1 => e.Exit(1), 2 => e.Exit(255), _ => e.Exit(0) });

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(3, Retries: 3), CancellationToken.None);

        Assert.Equal(("Failed", 1), (r.Status, r.ExitCode));
        Assert.Equal([1, 1, 1], r.Shards.Select(s => s.Attempts));
        Assert.Equal(["Failed", "Failed", "Completed"], r.Shards.Select(s => s.Status));
        Assert.Equal(3, ssh.Executes.Count);
    }

    /// <summary>Cancelling stops every running shard and still runs each one's cleanup (every slot is released).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task CancellingStopsEveryShardAndCleansEachUp()
    {
        var (runner, ssh, _) = Pool();
        var started = new Countdown(3);
        ssh.OnExecute = async (e, ct) =>
        {
            started.Signal();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return e.Exit(0);
        };
        using var cts = new CancellationTokenSource();
        var run = runner.RunShardsAsync(Jest(), new ShardOptions(3), cts.Token);
        await started.Reached;
        await cts.CancelAsync();
        var r = await run;

        Assert.Equal(("Cancelled", 130), (r.Status, r.ExitCode));
        Assert.All(r.Shards, s => Assert.Equal("Cancelled", s.Status));
        Assert.Equal(3, ssh.Calls.Count(c => c.Command.Contains("worktree prune", StringComparison.Ordinal)));
        Assert.Equal(0, ssh.Used("tiny-01") + ssh.Used("big-01"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "tmp")));
    }

    /// <summary>--fail-fast: the first shard that fails cancels the rest (cleaned up), and the run exits with that shard's code.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task FailFastCancelsTheOtherShards()
    {
        var (runner, ssh, _) = Pool();
        var others = new Countdown(2);
        ssh.OnExecute = async (e, ct) =>
        {
            if (e.Shard == 1)
            {
                await others.Reached.WaitAsync(ct);
                return e.Exit(5);
            }

            others.Signal();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return e.Exit(0);
        };

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(3, FailFast: true), CancellationToken.None);

        Assert.Equal(("Failed", 5), (r.Status, r.ExitCode));
        Assert.Equal(["Failed", "Cancelled", "Cancelled"], r.Shards.Select(s => s.Status));
        Assert.Equal(3, ssh.Calls.Count(c => c.Command.Contains("worktree prune", StringComparison.Ordinal)));
    }

    /// <summary>With no machine able to take a shard and none of the run's shards running, --wait 0 ends the waiting shards NoCapacity (75).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task NoMachineAtAllEndsNoCapacity()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(new JobRequest(["gpu"], ["npx", "jest"], ".", _dir, TimeSpan.Zero), new ShardOptions(2), CancellationToken.None);
        Assert.Equal(("NoCapacity", 75), (r.Status, r.ExitCode));
        Assert.All(r.Shards, s => Assert.Null(s.JobId));
        Assert.Empty(ssh.Executes);
    }

    /// <summary>--shards auto: the free slots of the machines that can take the job now (here --mem holds the small box), printed with why.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AutoUsesTheFreeSlotsOfEligibleMachines()
    {
        var (runner, ssh, _) = Pool();
        ssh.ProbeExtra = m => m.Name == "tiny-01" ? "mem_free_gb=1.5" : "mem_free_gb=12";
        var r = await runner.RunShardsAsync(Jest() with { MinMemGb = 2 }, new ShardOptions(null), CancellationToken.None);

        Assert.Equal(4, r.Count);
        Assert.All(r.Shards, s => Assert.Equal("big-01", s.Machine));
        Assert.Contains("[lanq] --shards auto: 4 shard(s) (free slots: big-01 4)", _output);

        var (all, _, _) = Pool();
        Assert.Equal(5, (await all.RunShardsAsync(Jest(), new ShardOptions(null), CancellationToken.None)).Count);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AutoWithNothingFreeRunsOneShard()
    {
        // Every machine has the capability but too little free memory now (a capability no machine has ends before the auto probe).
        var (runner, ssh, _) = Pool();
        ssh.ProbeExtra = _ => "mem_free_gb=1";
        var r = await runner.RunShardsAsync(new JobRequest(["node"], ["npx", "jest"], ".", _dir, TimeSpan.Zero, MinMemGb: 4), new ShardOptions(null), CancellationToken.None);
        Assert.Equal(1, r.Count);
        Assert.Contains(_output, l => l.StartsWith("[lanq] --shards auto: 1 shard(s) (no machine can take the job right now", StringComparison.Ordinal));
    }

    /// <summary>--fetch: each shard's files land in its own job-id folder.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task EachShardFetchesIntoItsOwnJobFolder()
    {
        var (runner, _, _) = Pool();
        var fetch = new FetchRequest(["junit.xml"], Path.Combine(_dir, "out"), new Fetch.FetchLimits(10, 1 << 20, 1 << 20));
        var r = await runner.RunShardsAsync(Jest() with { Fetch = fetch }, new ShardOptions(3), CancellationToken.None);

        var dirs = r.Shards.Select(s => s.Fetch!.Directory).ToList();
        Assert.Equal(3, dirs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(r.Shards, s => Assert.EndsWith(s.JobId!, s.Fetch!.Directory, StringComparison.Ordinal));
    }

    /// <summary>--target-minutes: N from the history in jobs.jsonl (see <see cref="ShardTimingTests.SeedHistory"/>), not the free slots.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TargetMinutesUsesHistoryFromTheJobLog()
    {
        ShardTimingTests.SeedHistory(new JobLog(Path.Combine(_dir, "state")), DateTimeOffset.UtcNow);
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(null, TargetMinutes: 5), CancellationToken.None);

        Assert.Equal(("Completed", 4), (r.Status, r.Count));
        Assert.Equal(4, ssh.Executes.Count);
        var t = r.Timing!;
        Assert.Equal((ShardTiming.History, 3, 10.7, 30.0, "tiny-01", 1.62, 4, 5, 4), (t.Basis, t.RunsUsed, t.WorkMinutes, t.OverheadSeconds, t.SlowestMachine, t.SpeedFactor, t.Wanted, t.FreeSlots, t.Picked));
        Assert.Equal((ShardTimingTests.JestKey, ".", 4.8), (t.Command, t.Dir, t.ExpectedShardMinutes));

        // A longer target needs fewer shards; a much shorter one is capped by the free slots.
        Assert.Equal(2, (await Pool().Runner.RunShardsAsync(Jest(), new ShardOptions(null, TargetMinutes: 10), CancellationToken.None)).Count);
        var capped = await Pool().Runner.RunShardsAsync(Jest(), new ShardOptions(null, TargetMinutes: 2), CancellationToken.None);
        Assert.Equal((5, ShardTiming.CappedByFreeSlots), (capped.Count, capped.Timing!.Capped));
    }

    /// <summary>Plain --shards auto keeps its meaning (N = the free slots) and its exact line, whatever the job log holds.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AutoWithoutTargetMinutesIsUnchangedEvenWithHistory()
    {
        ShardTimingTests.SeedHistory(new JobLog(Path.Combine(_dir, "state")), DateTimeOffset.UtcNow);
        var (runner, _, _) = Pool();
        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(null), CancellationToken.None);

        Assert.Equal(5, r.Count);
        Assert.Null(r.Timing);
        Assert.Equal("[lanq] --shards auto: 5 shard(s) (free slots: big-01 4, tiny-01 1)", Assert.Single(_output, l => l.Contains("--shards auto", StringComparison.Ordinal)));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TargetMinutesPrintsTheReasoning()
    {
        // No history yet: the free slots decide, and the line says so.
        var (empty, _, _) = Pool();
        Assert.Equal(5, (await empty.RunShardsAsync(Jest(), new ShardOptions(null, TargetMinutes: 5), CancellationToken.None)).Count);
        Assert.Contains(
            "[lanq] --shards auto: 5 shard(s) (--target-minutes 5: no completed run of this command in the last 90 days; --shards auto falls back to the free slots; "
            + "free slots: big-01 4, tiny-01 1)",
            _output);

        // That run took about no time and is history now; seed a real history beside it (the newest 10 runs count, medians decide).
        File.Delete(Path.Combine(_dir, "state", "jobs.jsonl"));
        ShardTimingTests.SeedHistory(new JobLog(Path.Combine(_dir, "state")), DateTimeOffset.UtcNow);
        var (runner, _, _) = Pool();
        await runner.RunShardsAsync(Jest(), new ShardOptions(null, TargetMinutes: 5), CancellationToken.None);
        Assert.Contains($"[lanq] --shards auto: 4 shard(s) ({ShardTimingTests.SeededWhy}; free slots: big-01 4, tiny-01 1)", _output);
    }

    [Theory]
    [InlineData("--target-minutes goes with --shards auto (it picks N from earlier runs of the same command)", "--target-minutes", "5")]
    [InlineData("--target-minutes goes with --shards auto (it picks N from earlier runs of the same command)", "--shards", "4", "--target-minutes", "5")]
    [InlineData("--target-minutes must be a whole number of minutes from 1 to 1440.", "--shards", "auto", "--target-minutes", "0")]
    [InlineData("--target-minutes must be a whole number of minutes from 1 to 1440.", "--shards", "auto", "--target-minutes", "1441")]
    public async Task TargetMinutesNeedsShardsAuto(string message, params string[] options)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", .. options, "--", "npx", "jest"], o, e, CancellationToken.None));
        Assert.StartsWith(message, e.ToString(), StringComparison.Ordinal);
        Assert.Equal("", o.ToString());

        // With --json still exactly one object.
        o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--json", .. options, "--", "npx", "jest"], o, new StringWriter(), CancellationToken.None));
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal(("Rejected", message), (doc.RootElement.GetProperty("status").GetString(), doc.RootElement.GetProperty("message").GetString()));
    }
}
