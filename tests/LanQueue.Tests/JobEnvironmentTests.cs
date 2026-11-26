using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Per-machine env: and path_prepend: reach the job process only, validated at load and quoted safely in both shells.</summary>
public sealed class JobEnvironmentTests : IDisposable
{
    // Unique job ids per test (review B-L6): a real cleanup stops every process of this uid that carries LANQ_JOB=<id>.
    private readonly string _j1 = T.NewJobId();

    private const char NL = (char)10;

    /// <summary>Tries to break out of the quoting in sh and PowerShell; it must arrive in the job as exactly these characters.</summary>
    private const string Hostile = "a'; touch PWNED; echo 'b $(touch PWNED2) `touch PWNED3` \"q\" $HOME ${X} \\ end";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-env-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public JobEnvironmentTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "jobs", _j1));
        Directory.CreateDirectory(Path.Combine(_root, "locks"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Linux(string lines) => T.Yaml.Replace("max_jobs: 2", "max_jobs: 2" + NL + lines, StringComparison.Ordinal);

    private static string Windows(string lines) => T.Yaml.Replace("    allowed_hours:", lines + NL + "    allowed_hours:", StringComparison.Ordinal);

    private static string ManyEnv(int count, int valueLength) =>
        "    env:" + string.Concat(Enumerable.Range(0, count).Select(i => NL + "      VAR_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + new string('x', valueLength)));

    /// <summary>
    /// A Windows machine whose env: and path_prepend: alone push the job (or doctor) script past the encoded command-line cap would fail
    /// every job with a misleading "shorten the arguments"; the config is refused at load instead, naming the machine, the settings and
    /// how much is over.
    /// </summary>
    [Fact]
    public void AWindowsMachineWhoseEnvAlonePassesTheCommandLineCapIsRefusedAtLoad()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Windows(ManyEnv(30, 120))));
        Assert.Contains("build-02", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env: (30 entries, 3770 characters)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("work_root (7 characters)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("path_prepend", ex.Message, StringComparison.Ordinal);
        Assert.Matches("[0-9]+ over the 8000", ex.Message);

        var paths = "    path_prepend: [" + string.Join(", ", Enumerable.Range(0, 40).Select(i => "C:/tools/" + new string('p', 60) + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        var pex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Windows(paths)));
        Assert.Contains("path_prepend: (40 entries, ", pex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("env:", pex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The load check measures the job script at its largest: a job given &amp; or | carries the native .exe check too, and it is measured as
    /// the post: step's script (the same wrapper with its limit line, 0.13.0 review M1). An env: just long enough that the post: script fits
    /// without that check but not with it is refused at load, not on the first such job.
    /// </summary>
    [Fact]
    public void TheLoadCheckCountsTheNativeExeCheckOfAJobGivenAmpersandOrBar()
    {
        var m = ConfigLoader.Parse(Windows(ManyEnv(1, 10))).Machines[1];
        int Size(string[] argv, string kind = "post") => RemoteScripts.WindowsCommand(RemoteScripts.Execute(
            m, "20260101-000000-000000", ".", argv, JobRequest.DefaultTimeout, new ShardInfo(ShardInfo.MaxShards, ShardInfo.MaxShards),
            new string('x', LanQueue.Plan.PlanFileLoader.MaxNameLength), new string('0', 16), pidKind: kind)).Length;
        var n = Enumerable.Range(10, 4000).First(i =>
        {
            m.Env["VAR_0"] = new string('x', i);
            return Size(["x", "|"]) > RemoteScripts.MaxWindowsCommandLength;
        });

        Assert.True(Size([]) <= RemoteScripts.MaxWindowsCommandLength, "the plain post: script should still fit at " + n);
        Assert.True(Size(["x", "|"], "job") < Size(["x", "|"]), "the post: script is the larger one");
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Windows(ManyEnv(1, n))));
        Assert.Matches("[0-9]+ over the 8000", ex.Message);
        Assert.Single(ConfigLoader.Parse(Windows(ManyEnv(1, n - 1))).Machines[1].Env);
    }

    /// <summary>
    /// Loading measures the job script only, at its largest (a sharded plan job given &amp; or |, as ConfigLoader measures it). That is today
    /// larger than the doctor script with every tool check, so a doctor script cannot overflow on its own yet; doctor still checks its own
    /// size (DoctorTests) in case that changes.
    /// </summary>
    [Fact]
    public void TheJobScriptIsTheOneThatGatesLoading()
    {
        var m = ConfigLoader.Parse(Windows(ManyEnv(4, 60))).Machines[1];
        m.Capabilities = [.. RemoteScripts.ToolChecks.Keys];
        var job = RemoteScripts.WindowsCommand(RemoteScripts.Execute(
            m, "20260101-000000-000000", ".", ["x", "|"], JobRequest.DefaultTimeout, new ShardInfo(ShardInfo.MaxShards, ShardInfo.MaxShards),
            new string('x', LanQueue.Plan.PlanFileLoader.MaxNameLength), new string('0', 16))).Length;
        var doctor = RemoteScripts.WindowsCommand(RemoteScripts.Doctor(m)).Length;
        Assert.True(doctor < job, $"doctor {doctor}, job {job}");
    }

    private static string WindowsRoot(string root) => T.Yaml.Replace("work_root: D:/lanq", "work_root: " + root, StringComparison.Ordinal);

    private static int Occurrences(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// 0.13.0 smoke: the Windows job script spelled work_root out four times (pid file, worktree, folder, helper path), so a work_root of about
    /// 95 characters passed the encoded cap. It is written once now (every other path is built from it on the worker), and each further
    /// character of work_root costs only its own encoded size.
    /// </summary>
    [Fact]
    public void TheWindowsJobScriptWritesTheWorkRootOnce()
    {
        var root = "D:/" + string.Join('/', Enumerable.Repeat("a-rather-long-folder", 9)); // 191 characters, under the 200 cap
        var m = ConfigLoader.Parse(WindowsRoot(root)).Machines[1];
        foreach (var dir in new[] { ".", "src" })
        {
            foreach (var kind in new[] { "job", "post" })
            {
                var script = RemoteScripts.Execute(m, "20260101-000000-000000", dir, ["dotnet", "test"], JobRequest.DefaultTimeout, pidKind: kind);
                Assert.Equal(1, Occurrences(script, root));
            }
        }

        Assert.True(RemoteScripts.WorstCaseWindowsCommandLength(m) <= RemoteScripts.MaxWindowsCommandLength);
        var shortRoot = ConfigLoader.Parse(WindowsRoot("D:/l")).Machines[1];
        var perCharacter = (double)(RemoteScripts.WorstCaseWindowsCommandLength(m) - RemoteScripts.WorstCaseWindowsCommandLength(shortRoot)) / (root.Length - 4);
        Assert.True(perCharacter < 3, $"each work_root character costs {perCharacter:0.00} encoded characters");
    }

    /// <summary>
    /// A Windows work_root over the length cap (0.13.0 review M1) is refused for its length alone, naming the cap and its length. Within the cap
    /// a work_root can still share the room with env:: when both pass the command-line cap, the message names each with its size, the
    /// work_root included, not "env: (0 entries) and path_prepend: (0 entries)".
    /// </summary>
    [Fact]
    public void AWorkRootTooLongForTheCapIsRefusedNamingTheWorkRoot()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WindowsRoot("D:/" + new string('r', 1500))));
        Assert.Contains("build-02", ex.Message, StringComparison.Ordinal);
        Assert.Contains("this one is 1503", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0 entries", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("env:", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("path_prepend", ex.Message, StringComparison.Ordinal);

        var root = WindowsWorkRootCapTests.RootOf(ConfigLoader.MaxWindowsWorkRootLength);
        var both = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WindowsRoot(root).Replace("    allowed_hours:", "    env:" + NL + "      A_B: " + new string('x', 400) + NL + "    allowed_hours:", StringComparison.Ordinal)));
        Assert.Contains($"work_root ({root.Length} characters)", both.Message, StringComparison.Ordinal);
        Assert.Contains("env: (1 entry, 403 characters)", both.Message, StringComparison.Ordinal);
        Assert.Matches("[0-9]+ over the 8000", both.Message);
        Assert.DoesNotContain("path_prepend", both.Message, StringComparison.Ordinal);
    }

    /// <summary>The run-time refusal of a too-long Windows command names work_root too, next to env: and path_prepend:.</summary>
    [Fact]
    public void TheRunTimeLengthErrorNamesTheWorkRoot()
    {
        var script = RemoteScripts.Execute(T.Windows(), "j", ".", ["dotnet", "test", new string('a', 4000)]);
        var ex = Assert.Throws<InvalidOperationException>(() => RemoteScripts.Wrap(T.Windows(), script));
        Assert.Contains("work_root (7 characters)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(RemoteScripts.EncodedCostNote, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("path_prepend", ex.Message, StringComparison.Ordinal);

        // 0.13.0 review L1: hygiene: is named too, as at load and in doctor (all from WindowsScriptInputs).
        var m = T.Windows();
        m.Hygiene = [Hygiene.Dotnet];
        var withHygiene = Assert.Throws<InvalidOperationException>(() => RemoteScripts.Wrap(m, RemoteScripts.Execute(m, "j", ".", ["dotnet", "test", new string('a', 4000)])));
        Assert.Contains("hygiene: (3 variables)", withHygiene.Message, StringComparison.Ordinal);
    }

    /// <summary>The job script's paths, built on the worker from the one work_root, keep their meaning: quoting-hostile folders stay literal.</summary>
    [Fact]
    public void TheWindowsJobScriptBuildsItsPathsFromOneQuotedWorkRoot()
    {
        var script = RemoteScripts.Execute(T.Windows(), "20260101-000000-000000", "src/$(x)'y", ["dotnet", "test"]);
        Assert.Contains("$rt='D:/lanq'", script, StringComparison.Ordinal);
        Assert.Contains("$wt=$rt+'/jobs/20260101-000000-000000'", script, StringComparison.Ordinal);
        Assert.Contains("$pf=$rt+'/locks/job-20260101-000000-000000.pid'", script, StringComparison.Ordinal);
        Assert.Contains("Set-Location -LiteralPath ($wt+'/src/$(x)''y')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AModestWindowsEnvLoadsAndTheSameEnvOnLinuxIsNotLimited()
    {
        Assert.Equal(5, ConfigLoader.Parse(Windows(ManyEnv(5, 40))).Machines[1].Env.Count);
        Assert.Equal(30, ConfigLoader.Parse(Linux(ManyEnv(30, 120))).Machines[0].Env.Count);
    }

    /// <summary>The run-time error for long arguments says the limit and the actual size.</summary>
    [Fact]
    public void TheArgumentLengthErrorSaysTheLimitAndTheSize()
    {
        var script = RemoteScripts.Execute(T.Windows(), "j", ".", ["dotnet", "test", new string('a', 4000)]);
        var ex = Assert.Throws<InvalidOperationException>(() => RemoteScripts.Wrap(T.Windows(), script));
        Assert.Matches("is [0-9]+ characters once encoded; the limit is 8000", ex.Message);
        Assert.Contains("arguments", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvAndPathPrependAreReadFromConfig()
    {
        var c = ConfigLoader.Parse(Linux("    env:\n      NODE_OPTIONS: --max-old-space-size=2048\n      CARGO_BUILD_JOBS: 2\n    path_prepend: [/home/u/.nvm/bin, /opt/x]"));
        Assert.Equal("--max-old-space-size=2048", c.Machines[0].Env["NODE_OPTIONS"]);
        Assert.Equal("2", c.Machines[0].Env["CARGO_BUILD_JOBS"]);
        Assert.Equal(["/home/u/.nvm/bin", "/opt/x"], c.Machines[0].PathPrepend);
        Assert.Empty(T.Linux().Env);
        Assert.Empty(T.Linux().PathPrepend);
    }

    [Theory]
    [InlineData("1ABC")]
    [InlineData("A-B")]
    [InlineData("\"A B\"")]
    [InlineData("\"A=B\"")]
    [InlineData("\"\"")]
    [InlineData("\"A\\n\"")] // a trailing newline must not slip past the name check
    public void BadEnvNamesAreRefused(string name)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    env:\n      {name}: x")));
        Assert.Contains("env", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>LANQ_ is lanq's own prefix: env: could otherwise override LANQ_JOB, the marker the Linux kill relies on.</summary>
    [Theory]
    [InlineData("LANQ_JOB")]
    [InlineData("lanq_job")]
    [InlineData("Lanq_Anything")]
    public void TheLanqPrefixIsReserved(string name)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    env:\n      {name}: x")));
        Assert.Contains($"'{name}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Belt and braces: on Linux the marker comes after the machine's env words, so it would win even if one named it.</summary>
    [Fact]
    public void TheJobMarkerComesAfterTheMachinesEnvOnLinux()
    {
        var m = WithEnv(T.Linux(), ("FOO", "1"));
        Assert.Contains("env 'FOO=1' 'LANQ_JOB=" + _j1 + "' /bin/sh -c", RemoteScripts.Execute(m, _j1, ".", ["npm", "test"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("ld_preload")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("LD_AUDIT")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("DYLD_LIBRARY_PATH")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("PROMPT_COMMAND")]
    [InlineData("PSModulePath")]
    [InlineData("PSMODULEPATH")]
    [InlineData("COMSPEC")]
    [InlineData("ComSpec")]
    [InlineData("PATHEXT")]
    [InlineData("LD_BIND_NOW")]
    [InlineData("LD_DEBUG_OUTPUT")]
    [InlineData("ld_anything")]
    [InlineData("SHELLOPTS")]
    [InlineData("BASHOPTS")]
    [InlineData("PS4")]
    [InlineData("GLIBC_TUNABLES")]
    public void LoaderAndShellHijackNamesAreRefused(string name)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    env:\n      {name}: x")));
        Assert.Contains($"'{name}'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("Path")]
    public void PathIsNotSetThroughEnv(string name)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    env:\n      {name}: /usr/bin")));
        Assert.Contains("path_prepend", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"a\\nb\"")]
    [InlineData("\"a\\tb\"")]
    [InlineData("\"\\e[31mred\"")]
    [InlineData("\"a\\x7fb\"")]
    [InlineData("\"a\\x85b\"")]
    [InlineData("~")]
    public void EnvValuesWithControlCharactersOrNoValueAreRefused(string value)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    env:\n      FOO: {value}")));
        Assert.Contains("'FOO'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows PowerShell 5.1 cannot set an empty variable ($env:X = '' deletes it) while Linux env would set it empty, so an empty value is
    /// refused on both, with an error that says so.
    /// </summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("''")]
    public void EmptyEnvValuesAreRefusedOnEveryPlatform(string value)
    {
        var linux = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    env:\n      FOO: {value}")));
        Assert.Contains("'FOO'", linux.Message, StringComparison.Ordinal);
        Assert.Contains("empty", linux.Message, StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Windows($"    env:\n      FOO: {value}")));
    }

    [Fact]
    public void EnvNamesThatDifferOnlyInCaseAreRefused() =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux("    env:\n      Foo: a\n      FOO: b")));

    [Theory]
    [InlineData("relative/bin")]
    [InlineData("\"\"")]
    [InlineData("/opt/a:/opt/b")]
    [InlineData("\"/opt/a\\nb\"")]
    [InlineData("C:/tools")]
    public void BadLinuxPathPrependEntriesAreRefused(string entry)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Linux($"    path_prepend: [{entry}]")));
        Assert.Contains("path_prepend", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("/opt/a")]
    [InlineData("\"C:/a;C:/b\"")]
    [InlineData("\"C:/a\\tb\"")]
    [InlineData("\"\\\\\\\\server\\\\share\"")]
    public void BadWindowsPathPrependEntriesAreRefused(string entry)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Windows($"    path_prepend: [{entry}]")));
        Assert.Contains("path_prepend", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsDrivePathsAreAccepted() =>
        Assert.Equal(["C:/Users/b/.cargo/bin", "D:\\tools"], ConfigLoader.Parse(Windows("    path_prepend: [C:/Users/b/.cargo/bin, 'D:\\tools']")).Machines[1].PathPrepend);

    private static Machine WithEnv(Machine m, params (string K, string V)[] env)
    {
        foreach (var (k, v) in env)
        {
            m.Env[k] = v;
        }

        return m;
    }

    [Fact]
    public void LinuxJobGetsEnvAndPathThroughEnvBeforeTheJobShell()
    {
        var m = WithEnv(T.Linux(), ("NODE_OPTIONS", "--max-old-space-size=2048"));
        m.PathPrepend.AddRange(["/home/u/.nvm/bin", "/opt/x"]);
        var s = RemoteScripts.Execute(m, _j1, ".", ["npm", "test"]);
        Assert.Contains("env 'NODE_OPTIONS=--max-old-space-size=2048' 'LANQ_JOB=" + _j1 + "' 'PATH=/home/u/.nvm/bin:/opt/x'\"${PATH:+:$PATH}\" /bin/sh -c", s, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxJobWithoutEnvGetsOnlyTheJobMarker() =>
        Assert.Contains(" env 'LANQ_JOB=" + _j1 + "' /bin/sh -c", RemoteScripts.Execute(T.Linux(), _j1, ".", ["npm", "test"]), StringComparison.Ordinal);

    [Fact]
    public void AnUnsafeJobIdNeverReachesTheEnvironment() =>
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1'; touch x", ".", ["npm", "test"]));

    [Fact]
    public void LinuxHostileValueIsOneSingleQuotedWord()
    {
        var s = RemoteScripts.Execute(WithEnv(T.Linux(), ("FOO", Hostile)), _j1, ".", ["npm", "test"]);
        Assert.Contains("'FOO=a'\\''; touch PWNED; echo '\\''b $(touch PWNED2)", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsJobGetsEnvAndPathBeforeTheCommand()
    {
        var m = WithEnv(T.Windows(), ("NODE_OPTIONS", "--max-old-space-size=2048"), ("Q", "it\u2019s"));
        m.PathPrepend.AddRange(["C:/Users/b/.cargo/bin", "D:\\tools"]);
        var s = RemoteScripts.Execute(m, _j1, ".", ["npm", "test"]);
        Assert.Contains("$env:NODE_OPTIONS = '--max-old-space-size=2048'", s, StringComparison.Ordinal);
        Assert.Contains("$env:Q = 'it\u2019\u2019s'", s, StringComparison.Ordinal);
        Assert.Contains("$env:Path = 'C:\\Users\\b\\.cargo\\bin;D:\\tools' + $(if ($env:Path) { ';' + $env:Path } else { '' })", s, StringComparison.Ordinal);
        Assert.True(s.IndexOf("$env:Path", StringComparison.Ordinal) < s.IndexOf("& 'npm' 'test'", StringComparison.Ordinal));
    }

    /// <summary>Runs the generated Linux script in a real sh: the job sees the hostile value verbatim and nothing else runs.</summary>
    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void RealShJobSeesTheHostileValueVerbatimAndThePrependedTool()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "lanq-test-tool"), "#!/bin/sh\nprintf '%s|' \"$FOO\"\n");
        File.SetUnixFileMode(Path.Combine(bin, "lanq-test-tool"), UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        var m = WithEnv(T.Linux(), ("FOO", Hostile));
        m.WorkRoot = _root;
        m.PathPrepend.Add(bin);
        var output = T.JobOutput(WorkerHealthTests.Capture("sh", "-c", RemoteScripts.Execute(m, _j1, ".", ["lanq-test-tool"])));
        Assert.Equal(Hostile + "|", output);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "jobs", _j1)));
    }

    /// <summary>Runs the generated Windows script in a real Windows PowerShell: same guarantees.</summary>
    [WindowsFact]
    public void RealPowerShellJobSeesTheHostileValueVerbatimAndThePrependedTool()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "lanqtesttool.cmd"), "@echo found\r\n");
        var m = WithEnv(T.Windows(), ("FOO", Hostile + " \u2018x\u2019 \u201A \u201B"));
        m.WorkRoot = _root;
        m.PathPrepend.Add(bin.Replace('\\', '/'));
        var script = RemoteScripts.Execute(m, _j1, ".", ["lanqtesttool"])
            .Replace("& 'lanqtesttool'", "& 'lanqtesttool'; [Console]::Out.Write(($env:FOO -ceq " + LanQueue.Policy.ShellQuote.PowerShell(m.Env["FOO"]) + "))", StringComparison.Ordinal);
        var file = Path.Combine(Path.GetTempPath(), "lanq-env-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, "\ufeff" + script);
        try
        {
            var output = T.JobOutput(WorkerHealthTests.Capture("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file));
            Assert.Equal("found" + Environment.NewLine + "True", output);
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "jobs", _j1)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, exe)));
}
