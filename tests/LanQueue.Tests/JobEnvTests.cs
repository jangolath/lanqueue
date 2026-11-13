using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Plan;
using LanQueue.Policy;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq run --env NAME=VALUE and lanq.yml env: (0.9.0): parsed and deny-checked on the command line, gated by machines.yml
/// policy.allowed_env, secret-scanned, refused when a machine's env: sets the same name or the Windows command would be too long, injected
/// after the machine's env and before lanq's own variables, and only ever logged and reported by name.
/// </summary>
public sealed class JobEnvTests : IDisposable
{
    private const int TestTimeout = 60_000;

    /// <summary>A value that tries to break out of the quoting in sh and PowerShell (quotes, typographic quotes, $, backtick, spaces).</summary>
    private const string Tricky = "it's \"q\" \u2018x\u2019 \u201A \u201B $HOME $(touch PWNED) ${X} `touch PWNED2` a  b \u00fcn\u00efcode \\ end";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-jobenv-" + Guid.NewGuid().ToString("N"));

    public JobEnvTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "jobs", "j1"));
        Directory.CreateDirectory(Path.Combine(_root, "locks"));
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
    }

    public void Dispose() => TestTree.Delete(_root);

    private string Repo => Path.Combine(_root, "repo");

    private JobLog Log => new(Path.Combine(_root, "state"));

    /// <summary>The shard pool (two Linux machines) with policy.allowed_env set.</summary>
    private static string Pool(string allowed = "[CONFIGURATION, TEST_FILTER, \"MYAPP_*\"]") =>
        ShardTests.PoolYaml.Replace("  allowed_directories: [\".\"]", "  allowed_directories: [\".\"]\n  allowed_env: " + allowed, StringComparison.Ordinal);

    /// <summary>T.Yaml (build-01 linux, build-02 windows) with policy.allowed_env.</summary>
    private static FleetConfig Mixed(params string[] allowed)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        config.Policy.AllowedEnv.AddRange(allowed);
        return config;
    }

    private JobRunner Runner(FleetConfig config, FakeSsh ssh) =>
        new(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_root, "tmp"), _ => { }, (_, _) => Task.CompletedTask);

    private JobRequest Req(params JobEnvVar[] env) => new(["dotnet"], ["dotnet", "test"], ".", Repo, TimeSpan.Zero) { Env = env };

    private static JobEnvVar V(string name, string value) => new(name, value);

    private static JsonElement Json(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.Clone();
    }

    /// <summary>Both appear in the (ssh-wrapped) command, the first before the second.</summary>
    private static bool Before(string command, string first, string second) =>
        command.IndexOf(first, StringComparison.Ordinal) is var a and >= 0 && command.IndexOf(second, StringComparison.Ordinal) is var b && a < b;

    private static string[] Names(JsonElement e) => [.. e.GetProperty("env").EnumerateArray().Select(x => x.GetString()!)];

    // ---- the command line ----

    [Fact]
    public void EnvFlagsAreParsedInOrderAndSplitAtTheFirstEquals()
    {
        var p = ParsedArgs.Parse(["run", "--env", "CONFIGURATION=Release", "--env", "TEST_FILTER=Category=Unit&Fast", "--", "dotnet", "test"]);
        Assert.Equal([V("CONFIGURATION", "Release"), V("TEST_FILTER", "Category=Unit&Fast")], p.Env);
        Assert.Empty(ParsedArgs.Parse(["run", "--", "dotnet", "test"]).Env);
        Assert.Equal("CONFIGURATION", V("CONFIGURATION", "secret-ish").ToString()); // a record's text never carries the value
    }

    [Theory]
    [InlineData("CONFIGURATION=")]
    [InlineData("CONFIGURATION=a\nb")]
    [InlineData("CONFIGURATION=a\tb")]
    [InlineData("CONFIGURATION=\u001b[31m")]
    public void EmptyValueRefused(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--env", flag, "--", "dotnet", "test"]));
        Assert.Contains("--env CONFIGURATION", ex.Message, StringComparison.Ordinal);
        Assert.Contains(flag.EndsWith('=') ? "empty" : "control characters", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hunter2hunter2")] // no '=': it may be a value
    [InlineData("1ABC=hunter2hunter2")]
    [InlineData("A-B=hunter2hunter2")]
    [InlineData("A B=hunter2hunter2")]
    [InlineData("=hunter2hunter2")]
    [InlineData("A\n=hunter2hunter2")]
    public void BadNamesAreRefusedWithoutEchoingAnything(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--env", flag, "--", "dotnet", "test"]));
        Assert.StartsWith("--env ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameGivenTwiceIgnoringCaseIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--env", "Configuration=a", "--env", "CONFIGURATION=b", "--", "dotnet", "test"]));
        Assert.Contains("twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvGoesWithRunShardsAndDryRunOnlyAndNeverWithPlan()
    {
        Assert.Single(ParsedArgs.Parse(["run", "--shards", "2", "--dry-run", "--env", "CONFIGURATION=Release", "--", "npx", "jest"]).Env);
        Assert.Contains("lanq health does not take --env", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--env", "A=b"])).Message, StringComparison.Ordinal);
        var plan = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--env", "CONFIGURATION=Release"]));
        Assert.Contains("with --plan each job's --env comes from lanq.yml", plan.Message, StringComparison.Ordinal);
        Assert.Contains("--env", ParsedArgs.PlanFileOptions);
    }

    /// <summary>A re-run suggestion edits a copy of the command line: --env and its value stay, and the value is never read as an option.</summary>
    [Fact]
    public void ARerunKeepsTheEnvAndParses()
    {
        var p = ParsedArgs.Parse(["run", "--env", "TEST_FILTER=--wait", "--dry-run", "--", "dotnet", "test"]);
        var again = SuggestedCommand.Rerun(p, dropDryRun: true, waitSeconds: 120)!;
        Assert.Equal(["lanq", "run", "--env", "TEST_FILTER=--wait", "--", "dotnet", "test"], again.Argv.Where(a => a is not ("--wait" or "120" or "--no-wake")));
        Assert.Equal([V("TEST_FILTER", "--wait")], ParsedArgs.Parse([.. again.Argv.Skip(1)]).Env);
        Assert.NotNull(SuggestedCommand.DryRun(ParsedArgs.Parse(["run", "--env", "CONFIGURATION=Release", "--", "dotnet", "test"])));
    }

    // ---- the deny table and policy.allowed_env ----

    public static TheoryData<string> DeniedNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in JobEnv.JobDeniedNames)
        {
            data.Add(name);
            data.Add(name.ToLowerInvariant());
        }

        foreach (var prefix in JobEnv.JobDeniedPrefixes)
        {
            data.Add(prefix + "X");
        }

        data.Add("GIT_SSH_COMMAND");
        data.Add("MSBuildSDKsPath");
        data.Add("LANQ_JOB");
        data.Add("CORECLR_PROFILER");
        return data;
    }

    /// <summary>
    /// Every entry of the hard deny table, and a name under every prefix, is refused on the command line (naming it, without the value), by
    /// the precheck for a request built some other way even when allowed_env lists it, and as an exact allowed_env pattern in machines.yml.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeniedNames))]
    public async Task HardDenyBeatsAllowList(string name)
    {
        Assert.True(JobEnv.IsJobDenied(name));
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--env", name + "=hunter2hunter2", "--", "dotnet", "test"]));
        Assert.Equal($"--env {name} can make the command run other code or move where tools keep their files; lanq never sets it for a job.", ex.Message);

        var ssh = new FakeSsh();
        var r = await Runner(Mixed(name, name[..1] + "*"), ssh).RunAsync(Req(V(name, "x")), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Policy, 2), (r.Status, r.RejectKind, r.CliExitCode));
        Assert.Contains($"--env {name} can make", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);

        var config = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("  allowed_directories: [\".\", \"src\", \"tests\"]", $"  allowed_directories: [\".\", \"src\", \"tests\"]\n  allowed_env: [\"{name}\"]", StringComparison.Ordinal)));
        Assert.Contains("allowed_env", config.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMachineDenyListIsPartOfTheJobDenyList()
    {
        Assert.All(JobEnv.MachineDeniedNames, n => Assert.Contains(n, JobEnv.JobDeniedNames));
        Assert.All(JobEnv.MachineDeniedPrefixes, n => Assert.Contains(n, JobEnv.JobDeniedPrefixes));
        Assert.False(JobEnv.IsJobDenied("CONFIGURATION"));
        Assert.False(JobEnv.IsJobDenied("NODE_ENV"));
        Assert.False(JobEnv.IsJobDenied("DOTNET_CLI_TELEMETRY_OPTOUT"));
    }

    [Fact]
    public async Task EnvNotInAllowedEnvIsRejected()
    {
        foreach (var allowed in new[] { Array.Empty<string>(), ["CONFIGURATION", "TEST_*"] })
        {
            var ssh = new FakeSsh();
            var r = await Runner(Mixed(allowed), ssh).RunAsync(Req(V("CONFIGURATION", "Release"), V("NODE_ENV", "test")), CancellationToken.None);
            Assert.Equal((JobStatus.Rejected, RejectKind.Policy, 2), (r.Status, r.RejectKind, r.CliExitCode));
            Assert.Equal(
                allowed.Length == 0
                    ? "--env CONFIGURATION is not in policy.allowed_env (your machines.yml); the developer adds names there if jobs may set them"
                    : "--env NODE_ENV is not in policy.allowed_env (your machines.yml); the developer adds names there if jobs may set them",
                r.Message);
            Assert.Empty(ssh.Calls);

            var step = Assert.Single(NextSteps.ForRun(ParsedArgs.Parse(["run", "--json", "--env", "CONFIGURATION=Release", "--", "dotnet", "test"]), NextSteps.Outcome(r)));
            Assert.Equal("ask_developer", step.Action);
            Assert.Null(step.Argv);
        }
    }

    [Theory]
    [InlineData("MYAPP_*", "MYAPP_MODE", true)]
    [InlineData("MYAPP_*", "myapp_mode", true)]
    [InlineData("myapp_*", "MYAPP_MODE", true)]
    [InlineData("MYAPP_*", "MYAPPX", false)]
    [InlineData("MYAPP_*", "MYAPP_", true)]
    [InlineData("MYAPP", "MYAPP_MODE", false)]
    [InlineData("CONFIGURATION", "configuration", true)]
    [InlineData("CONFIG*", "CONFIGURATION", true)]
    public void PrefixPatternAllows(string pattern, string name, bool allowed) =>
        Assert.Equal(allowed, JobEnv.IsAllowed([pattern], name));

    [Fact]
    public async Task APrefixPatternLetsAJobRun()
    {
        var ssh = new FakeSsh { Handler = (m, cmd) => cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok") };
        var config = Mixed("MYAPP_*");
        var r = await Runner(config, ssh).RunAsync(Req(V("MyApp_Mode", "fast")) with { On = ["build-01"] }, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Contains(ssh.Calls, c => Before(c.Command, "MyApp_Mode=fast", "LANQ_JOB="));
    }

    [Theory]
    [InlineData("\"*\"")]
    [InlineData("\"MY-APP\"")]
    [InlineData("\"MY*APP\"")]
    [InlineData("\"1ABC\"")]
    [InlineData("\"A\\n\"")]
    [InlineData("LANQ_X")]
    [InlineData("\"lanq_*\"")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("Path")]
    [InlineData("\"\"")]
    public void BadAllowedEnvPatternsAreRefusedAtLoad(string pattern)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Pool($"[{pattern}]")));
        Assert.Contains("allowed_env", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedEnvDefaultsToEmptyAndAcceptsNamesAndPrefixes()
    {
        Assert.Empty(ConfigLoader.Parse(ShardTests.PoolYaml).Policy.AllowedEnv);
        Assert.Equal(["CONFIGURATION", "TEST_FILTER", "MYAPP_*"], ConfigLoader.Parse(Pool()).Policy.AllowedEnv);

        // A prefix may cover denied names (GIT_* would cover GIT_SSH_COMMAND): the deny table still wins for those.
        Assert.Equal(["NODE_*"], ConfigLoader.Parse(Pool("[\"NODE_*\"]")).Policy.AllowedEnv);
    }

    // ---- secrets and clashes ----

    [Theory]
    [InlineData("MYAPP_API_KEY", "abcdef0123456789")]
    [InlineData("CONFIGURATION", "AKIAABCDEFGHIJKLMNOP")]
    [InlineData("DB_PASSWORD", "correcthorsebattery")]
    public async Task EnvValueWithCredentialIsRejectedWithoutEcho(string name, string value)
    {
        var ssh = new FakeSsh();
        var r = await Runner(Mixed("MYAPP_*", "CONFIGURATION", "DB_*"), ssh).RunAsync(Req(V(name, value)), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Secret), (r.Status, r.RejectKind));
        Assert.Contains("--env " + name, r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value[..6], r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
    }

    /// <summary>The same through lanq run --json: the object, its nextSteps and stderr never carry the value.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ARefusedSecretIsNeverEchoedByTheCli()
    {
        foreach (var extra in new[] { Array.Empty<string>(), ["--dry-run"], ["--shards", "2"] })
        {
            var ssh = new PoolSsh();
            var o = new StringWriter();
            var e = new StringWriter();
            var code = await Cli.Run(
                ParsedArgs.Parse(["run", "--json", .. extra, "--env", "MYAPP_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789", "--needs", "node", "--", "npx", "jest"]),
                ConfigLoader.Parse(Pool()), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, _root, Repo, o, e, CancellationToken.None);
            Assert.Equal(2, code);
            Assert.Equal("Rejected", Json(o.ToString()).GetProperty("status").GetString());
            Assert.DoesNotContain("ghp_", o.ToString() + e.ToString(), StringComparison.Ordinal);
            Assert.Contains("MYAPP_TOKEN", o.ToString(), StringComparison.Ordinal);
            Assert.Empty(ssh.Calls);
        }
    }

    [Fact]
    public async Task MachineEnvConflictRejected()
    {
        var config = Mixed("CONFIGURATION");
        config.Machines[0].Env["Configuration"] = "Debug";
        var ssh = new FakeSsh();
        var r = await Runner(config, ssh).RunAsync(Req(V("CONFIGURATION", "Release")), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Arguments, 2), (r.Status, r.RejectKind, r.CliExitCode));
        Assert.Equal("--env CONFIGURATION: build-01's env in machines.yml sets it too; drop one", r.Message);
        Assert.Empty(ssh.Calls);

        // Only candidates count: --on another machine has no clash.
        var pinned = await Runner(config, new FakeSsh()).RunAsync(Req(V("CONFIGURATION", "Release")) with { On = ["build-02"] }, CancellationToken.None);
        Assert.NotEqual(JobStatus.Rejected, pinned.Status);
    }

    [Fact]
    public async Task WindowsLengthGuardCountsJobEnv()
    {
        var config = Mixed("BIG");
        var big = V("BIG", new string('x', 6000));
        var ssh = new FakeSsh();
        var r = await Runner(config, ssh).RunAsync(Req(big), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Arguments), (r.Status, r.RejectKind));
        Assert.Matches("^the --env values make the Windows command [0-9]{1,3},[0-9]{3} characters \\(limit 8,000\\) on build-02$", r.Message);
        Assert.Empty(ssh.Calls);

        // The same variable fits on Linux, and a short one fits on Windows; the machine-only check at load is unchanged.
        Assert.NotEqual(JobStatus.Rejected, (await Runner(config, new FakeSsh()).RunAsync(Req(big) with { On = ["build-01"] }, CancellationToken.None)).Status);
        Assert.NotEqual(JobStatus.Rejected, (await Runner(config, new FakeSsh()).RunAsync(Req(V("BIG", "x")), CancellationToken.None)).Status);
        Assert.True(RemoteScripts.WorstCaseWindowsCommandLength(T.Windows(), [big]) > RemoteScripts.MaxWindowsCommandLength);
        Assert.True(RemoteScripts.WorstCaseWindowsCommandLength(T.Windows()) < RemoteScripts.MaxWindowsCommandLength);
    }

    // ---- injection ----

    [Fact]
    public void InjectedAfterMachineEnvBeforeLanq()
    {
        var env = new[] { V("CONFIGURATION", "Release"), V("TEST_FILTER", "a b'c") };
        var linux = T.Linux();
        linux.Env["MACHINE_ONLY"] = "1";
        Assert.Contains(
            "env 'MACHINE_ONLY=1' 'CONFIGURATION=Release' 'TEST_FILTER=a b'\\''c' 'LANQ_JOB=j1' 'LANQ_SHARD=1' 'LANQ_SHARDS=2' 'LANQ_PLAN_JOB=unit' /bin/sh -c",
            RemoteScripts.Execute(linux, "j1", ".", ["npm", "test"], shard: new ShardInfo(1, 2), planJob: "unit", env: env),
            StringComparison.Ordinal);

        var windows = T.Windows();
        windows.Env["MACHINE_ONLY"] = "1";
        windows.PathPrepend.Add("C:/tools");
        var script = RemoteScripts.Execute(windows, "j1", ".", ["npm", "test"], shard: new ShardInfo(1, 2), planJob: "unit", env: env);
        var lines = script.Split('\n').Select(l => l.Trim()).ToList();
        var order = new[]
        {
            "$env:MACHINE_ONLY = '1'", "$env:Path = 'C:\\tools'", "$env:CONFIGURATION = 'Release'", "$env:TEST_FILTER = 'a b''c'", "$env:LANQ_JOB = 'j1'",
            "$env:LANQ_SHARD = '1'", "$env:LANQ_PLAN_JOB = 'unit'",
        }.Select(prefix => lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);

        // Without --env the scripts are exactly as before.
        Assert.Equal(RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"]), RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], env: []));
        Assert.Equal(RemoteScripts.Execute(T.Windows(), "j1", ".", ["npm", "test"]), RemoteScripts.Execute(T.Windows(), "j1", ".", ["npm", "test"], env: []));
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("A B")]
    [InlineData("A=B")]
    public void AnUnsafeNameNeverReachesAScript(string name)
    {
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], env: [V(name, "x")]));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Windows(), "j1", ".", ["npm", "test"], env: [V(name, "x")]));
    }

    /// <summary>Runs the generated Linux script in a real sh: the job sees each value verbatim, and nothing in it runs.</summary>
    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void RealShJobSeesTheValue()
    {
        var m = T.Linux();
        m.WorkRoot = _root;
        var script = RemoteScripts.Execute(m, "j1", ".", ["sh", "-c", "printf '%s|%s|%s' \"$TEST_FILTER\" \"$CONFIGURATION\" \"$LANQ_JOB\""], env: [V("TEST_FILTER", Tricky), V("CONFIGURATION", "Release")]);
        Assert.Equal(Tricky + "|Release|j1", T.JobOutput(WorkerHealthTests.Capture("sh", "-c", script)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "jobs", "j1")));
    }

    /// <summary>Runs the generated Windows script in a real Windows PowerShell: the job's child process sees each value verbatim.</summary>
    [WindowsFact]
    public void RealPowerShellJobSeesTheValue()
    {
        var m = T.Windows();
        m.WorkRoot = _root;
        var check = "[Console]::Out.Write(($env:TEST_FILTER -ceq " + ShellQuote.PowerShell(Tricky) + ").ToString() + '|' + $env:CONFIGURATION + '|' + $env:LANQ_JOB)";
        var script = RemoteScripts.Execute(m, "j1", ".", ["lanqtesttool"], env: [V("TEST_FILTER", Tricky), V("CONFIGURATION", "Release")])
            .Replace("& 'lanqtesttool'", check, StringComparison.Ordinal);
        var file = Path.Combine(Path.GetTempPath(), "lanq-jobenv-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, "\ufeff" + script);
        try
        {
            var output = T.JobOutput(WorkerHealthTests.Capture("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file));
            Assert.Equal("True|Release|j1", output);
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "jobs", "j1")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- lanq.yml ----

    [Fact]
    public void PlanEnvExpandsToEnvFlags()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            jobs:
              unit:
                command: dotnet test
                env:
                  CONFIGURATION: Release
                  RETRIES: 3
                  VERBOSE: true
                  TEST_FILTER: "Category=Unit|Fast"
            """);
        var job = plan.Job("unit")!;
        Assert.Equal(
            ["run", "--env", "CONFIGURATION=Release", "--env", "RETRIES=3", "--env", "VERBOSE=true", "--env", "TEST_FILTER=Category=Unit|Fast", "--", "dotnet", "test"],
            job.RunArgs);
        Assert.Equal([V("CONFIGURATION", "Release"), V("RETRIES", "3"), V("VERBOSE", "true"), V("TEST_FILTER", "Category=Unit|Fast")], job.Args.Env);

        // Parity: the expanded line parses to the same env, and the CLI's own --env rules judge each, with the file's prefix.
        Assert.Equal(job.Args.Env, ParsedArgs.Parse([.. job.RunArgs]).Env);
        foreach (var (yaml, expected) in new[]
        {
            ("NODE_OPTIONS: --inspect", "--env NODE_OPTIONS can make the command run other code"),
            ("EMPTY: \"\"", "--env EMPTY is empty"),
            ("BAD: \"a\\nb\"", "--env BAD needs a value without control characters"),
        })
        {
            var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse($"version: 1\njobs:\n  unit:\n    command: dotnet test\n    env:\n      {yaml}\n"));
            Assert.StartsWith("lanq.yml: job 'unit' (line 3): " + expected, ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("env: [A, B]", "env: must be a mapping")]
    [InlineData("env: CONFIGURATION=Release", "env: must be a mapping")]
    [InlineData("env: {A: [1, 2]}", "env: A must be a single value")]
    [InlineData("env: {A: {b: c}}", "env: A must be a single value")]
    [InlineData("env: {\"1A\": x}", "env: a variable name must be letters")]
    [InlineData("env: {\"hunter2 x\": x}", "env: a variable name must be letters")]
    [InlineData("env: {Foo: a, FOO: b}", "env: FOO is listed twice")]
    [InlineData("env: {A: }", "has no value")]
    public void PlanEnvMustBeAMappingOfNamesToSingleValues(string line, string expected)
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse($"version: 1\njobs:\n  unit:\n    command: dotnet test\n    {line}\n"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanDefaultsMergeByName()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            defaults:
              env:
                CONFIGURATION: Debug
                TEST_FILTER: Category=Unit
            jobs:
              unit:
                command: dotnet test
              release:
                command: dotnet test
                env:
                  configuration: Release
                  MYAPP_MODE: fast
              plain:
                command: dotnet build
                env: {}
            """);
        Assert.Equal([V("CONFIGURATION", "Debug"), V("TEST_FILTER", "Category=Unit")], plan.Job("unit")!.Args.Env);
        Assert.Equal([V("configuration", "Release"), V("TEST_FILTER", "Category=Unit"), V("MYAPP_MODE", "fast")], plan.Job("release")!.Args.Env);
        Assert.Equal([V("CONFIGURATION", "Debug"), V("TEST_FILTER", "Category=Unit")], plan.Job("plain")!.Args.Env);
    }

    [Fact]
    public void EnvIsAPlanKeyAndMachineKeysStillGetTheirHint()
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse("version: 1\njobs:\n  unit:\n    command: dotnet test\n    path_prepend: [/opt/bin]\n"));
        Assert.Contains("belong in your machines.yml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env", ex.Message, StringComparison.Ordinal); // listed among the keys allowed here
        var top = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse("version: 1\nenv: {A: b}\njobs:\n  unit:\n    command: dotnet test\n"));
        Assert.Contains("unknown key 'env' at the top", top.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("belong in your machines.yml", top.Message, StringComparison.Ordinal);
    }

    // ---- what is reported and recorded ----

    [Fact(Timeout = TestTimeout)]
    public async Task JsonListsNamesOnly()
    {
        string[] env = ["--env", "CONFIGURATION=ReleaseValue1", "--env", "MYAPP_MODE=FastValue2"];
        foreach (var extra in new[] { Array.Empty<string>(), ["--shards", "2"], ["--dry-run"], ["--dry-run", "--shards", "2"] })
        {
            var ssh = new PoolSsh();
            var o = new StringWriter();
            var e = new StringWriter();
            var code = await Cli.Run(
                ParsedArgs.Parse(["run", "--json", .. extra, .. env, "--needs", "node", "--", "npx", "jest"]),
                ConfigLoader.Parse(Pool()), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, _root, Repo, o, e, CancellationToken.None);
            Assert.Equal(0, code);
            var json = Json(o.ToString());
            Assert.Equal(["CONFIGURATION", "MYAPP_MODE"], Names(json));
            if (!extra.Contains("--dry-run"))
            {
                Assert.DoesNotContain("Value1", o.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("Value2", o.ToString(), StringComparison.Ordinal);
                Assert.All(ssh.Executes, x => Assert.True(Before(x.Command, "CONFIGURATION=ReleaseValue1", "MYAPP_MODE=FastValue2") && Before(x.Command, "MYAPP_MODE=FastValue2", "LANQ_JOB=")));
                Assert.Equal(extra.Length == 0 ? 1 : 2, ssh.Executes.Count);
            }
            else
            {
                // The dry run shows the flags (after the secret scan), and its proceed step is the same command with them.
                Assert.Contains("--env CONFIGURATION=ReleaseValue1 MYAPP_MODE=FastValue2", e.ToString(), StringComparison.Ordinal);
                var proceed = json.GetProperty("nextSteps")[0].GetProperty("argv").EnumerateArray().Select(a => a.GetString()).ToList();
                Assert.Contains("CONFIGURATION=ReleaseValue1", proceed);
            }
        }

        // Without --env the key is absent.
        var plain = new StringWriter();
        var none = new PoolSsh();
        await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "node", "--", "npx", "jest"]), ConfigLoader.Parse(Pool()), none, new RunnerTests.FakePayloads(""),
            new HealthProbe(none, TimeProvider.System), Log, _root, Repo, plain, new StringWriter(), CancellationToken.None);
        Assert.False(Json(plain.ToString()).TryGetProperty("env", out _));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanJsonListsNamesOnly()
    {
        File.WriteAllText(Path.Combine(Repo, "lanq.yml"), """
            version: 1
            defaults:
              needs: [node]
              env: {CONFIGURATION: ReleaseValue1}
            jobs:
              unit:
                command: npx jest
              sharded:
                command: [npx, jest, "--shard={shard}/{shards}"]
                shards: 2
                env: {MYAPP_MODE: FastValue2}
              bare:
                command: npx jest --ci
                env: {}
            """);
        foreach (var dry in new[] { false, true })
        {
            var ssh = new PoolSsh();
            var o = new StringWriter();
            var code = await Cli.RunPlanFile(
                ParsedArgs.Parse(["run", "--plan", "--json", .. (dry ? new[] { "--dry-run" } : [])]), ConfigLoader.Parse(Pool()), ssh, new RunnerTests.FakePayloads(""),
                new HealthProbe(ssh, TimeProvider.System), Log, _root, Repo, o, new StringWriter(), CancellationToken.None);
            Assert.Equal(0, code);
            var jobs = Json(o.ToString()).GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!);
            Assert.Equal(["CONFIGURATION"], Names(jobs["unit"]));
            Assert.Equal(["CONFIGURATION", "MYAPP_MODE"], Names(jobs["sharded"]));
            Assert.Equal(["CONFIGURATION"], Names(jobs["bare"]));
            if (!dry)
            {
                Assert.DoesNotContain("Value1", o.ToString(), StringComparison.Ordinal);
                Assert.Equal(4, ssh.Executes.Count);
                Assert.All(ssh.Executes, x => Assert.Contains("'CONFIGURATION=ReleaseValue1'", x.Command, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task NeverInJobsJsonlValues()
    {
        var ssh = new FakeSsh { Handler = (m, cmd) => cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok") };
        var r = await Runner(Mixed("CONFIGURATION", "MYAPP_*"), ssh).RunAsync(Req(V("CONFIGURATION", "ReleaseValue1"), V("MYAPP_MODE", "FastValue2")) with { On = ["build-01"] }, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        var record = Assert.Single(Log.ReadAll());
        Assert.Equal(["CONFIGURATION", "MYAPP_MODE"], record.EnvNames!);
        var text = File.ReadAllText(Log.FilePath);
        Assert.Contains("\"envNames\":[\"CONFIGURATION\",\"MYAPP_MODE\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Value1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Value2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Value1", File.ReadAllText(r.LogPath!), StringComparison.Ordinal);

        // A job without --env writes the line as 0.8 did.
        await Runner(Mixed(), ssh).RunAsync(Req() with { On = ["build-01"] }, CancellationToken.None);
        Assert.DoesNotContain("envNames", File.ReadAllLines(Log.FilePath)[^1], StringComparison.Ordinal);
    }
}
