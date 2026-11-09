using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Per-machine env: and path_prepend: reach the job process only, validated at load and quoted safely in both shells.</summary>
public sealed class JobEnvironmentTests : IDisposable
{
    private const char NL = (char)10;

    /// <summary>Tries to break out of the quoting in sh and PowerShell; it must arrive in the job as exactly these characters.</summary>
    private const string Hostile = "a'; touch PWNED; echo 'b $(touch PWNED2) `touch PWNED3` \"q\" $HOME ${X} \\ end";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-env-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public JobEnvironmentTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "jobs", "j1"));
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
        Assert.Contains("env", ex.Message, StringComparison.Ordinal);
        Assert.Contains("path_prepend", ex.Message, StringComparison.Ordinal);
        Assert.Matches("[0-9]+ over the 8000", ex.Message);

        var paths = "    path_prepend: [" + string.Join(", ", Enumerable.Range(0, 40).Select(i => "C:/tools/" + new string('p', 60) + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Windows(paths)));
    }

    /// <summary>
    /// Loading measures the job script only. Its fixed part is today larger than the doctor script's with every tool check, so a doctor
    /// script cannot overflow on its own yet; doctor still checks its own size (DoctorTests) in case that changes.
    /// </summary>
    [Fact]
    public void TheJobScriptIsTheOneThatGatesLoading()
    {
        var m = ConfigLoader.Parse(Windows(ManyEnv(4, 60))).Machines[1];
        m.Capabilities = [.. RemoteScripts.ToolChecks.Keys];
        var job = RemoteScripts.WindowsCommand(RemoteScripts.Execute(m, "20260101-000000-000000", ".", [], JobRequest.DefaultTimeout)).Length;
        var doctor = RemoteScripts.WindowsCommand(RemoteScripts.Doctor(m)).Length;
        Assert.True(doctor < job, $"doctor {doctor}, job {job}");
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
        Assert.Contains("env 'FOO=1' 'LANQ_JOB=j1' /bin/sh -c", RemoteScripts.Execute(m, "j1", ".", ["npm", "test"]), StringComparison.Ordinal);
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
        var s = RemoteScripts.Execute(m, "j1", ".", ["npm", "test"]);
        Assert.Contains("env 'NODE_OPTIONS=--max-old-space-size=2048' 'LANQ_JOB=j1' 'PATH=/home/u/.nvm/bin:/opt/x'\"${PATH:+:$PATH}\" /bin/sh -c", s, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxJobWithoutEnvGetsOnlyTheJobMarker() =>
        Assert.Contains(" env 'LANQ_JOB=j1' /bin/sh -c", RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"]), StringComparison.Ordinal);

    [Fact]
    public void AnUnsafeJobIdNeverReachesTheEnvironment() =>
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1'; touch x", ".", ["npm", "test"]));

    [Fact]
    public void LinuxHostileValueIsOneSingleQuotedWord()
    {
        var s = RemoteScripts.Execute(WithEnv(T.Linux(), ("FOO", Hostile)), "j1", ".", ["npm", "test"]);
        Assert.Contains("'FOO=a'\\''; touch PWNED; echo '\\''b $(touch PWNED2)", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsJobGetsEnvAndPathBeforeTheCommand()
    {
        var m = WithEnv(T.Windows(), ("NODE_OPTIONS", "--max-old-space-size=2048"), ("Q", "it\u2019s"));
        m.PathPrepend.AddRange(["C:/Users/b/.cargo/bin", "D:\\tools"]);
        var s = RemoteScripts.Execute(m, "j1", ".", ["npm", "test"]);
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
        var output = WorkerHealthTests.Capture("sh", "-c", RemoteScripts.Execute(m, "j1", ".", ["lanq-test-tool"]));
        Assert.Equal(Hostile + "|", output);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "jobs", "j1")));
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
        var script = RemoteScripts.Execute(m, "j1", ".", ["lanqtesttool"])
            .Replace("& 'lanqtesttool'", "& 'lanqtesttool'; [Console]::Out.Write(($env:FOO -ceq " + LanQueue.Policy.ShellQuote.PowerShell(m.Env["FOO"]) + "))", StringComparison.Ordinal);
        var file = Path.Combine(Path.GetTempPath(), "lanq-env-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, "\ufeff" + script);
        try
        {
            var output = WorkerHealthTests.Capture("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file);
            Assert.Equal("found" + Environment.NewLine + "True", output);
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "jobs", "j1")));
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
