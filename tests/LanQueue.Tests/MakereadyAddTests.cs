using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq makeready --ssh user@host --os X --add --name NAME: a READY worker added to the local machines.yml as text (the lines shown are the
/// lines written), never anything else. The starter config from lanq init is the fixture.
/// </summary>
public sealed class MakereadyAddTests : IDisposable
{
    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\nmem_total_gb=8\ncores=4\n";

    private const string Target = "build@10.20.30.41";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-add-" + Guid.NewGuid().ToString("N"));

    public MakereadyAddTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, Cli.StarterConfig);
    }

    public void Dispose() => TestTree.Delete(_dir);

    private string ConfigPath => Path.Combine(_dir, "machines.yml");

    private static string Report(bool ready = true) => ready ? MakereadyTests.Report() : MakereadyTests.Report(git: "FAIL", archive: "FAIL");

    private static string WindowsReport() =>
        MakereadyTests.Report().Replace("\"os\": \"linux\"", "\"os\": \"windows\"", StringComparison.Ordinal).Replace("/srv/lq", "C:/lq", StringComparison.Ordinal)
            .Replace("\"linux\"]", "\"windows\"]", StringComparison.Ordinal);

    private sealed record Result(int Code, string Out, string Err, FakeSsh Ssh, int Asked);

    private async Task<Result> Add(
        string[] args, string? report = null, bool? answer = null, Action? beforeWrite = null, SshResult? scan = null, Dictionary<string, string>? env = null,
        bool withConfig = true)
    {
        var ssh = new FakeSsh
        {
            Handler = (_, _) => new SshResult(0, Healthy),
            InputHandler = (_, _, _) => scan ?? new SshResult(0, report ?? Report()),
        };
        var asked = 0;
        var host = new MakereadyHost(
            name => env?.GetValueOrDefault(name),
            Windows: false,
            () => null,
            answer is { } yes ? _ => { asked++; return yes; }
        : null,
            beforeWrite);
        var o = new StringWriter();
        var e = new StringWriter();
        string[] all = ["makeready", .. args, .. withConfig ? new[] { "--config", ConfigPath } : []];
        var code = await Cli.Makeready(ParsedArgs.Parse(all), () => throw new InvalidOperationException("--add reads machines.yml itself"), ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None, host);
        return new(code, o.ToString(), e.ToString(), ssh, asked);
    }

    private static string[] AddArgs(string name = "build-01", string target = Target, string os = "linux", params string[] more) =>
        ["--ssh", target, "--os", os, "--add", "--name", name, .. more];

    private string Config() => File.ReadAllText(ConfigPath);

    [Theory]
    [InlineData("--os", "linux", "--add", "--name", "build-01")] // no --ssh
    [InlineData("--ssh", Target, "--add", "--name", "build-01")] // no --os
    [InlineData("--ssh", Target, "--os", "linux", "--add")] // no --name
    [InlineData("build-01", "--ssh", Target, "--os", "linux", "--add", "--name", "build-01")] // a configured machine too
    [InlineData("--ssh", Target, "--os", "linux", "--add", "--name", "bad name!")]
    [InlineData("--ssh", "-oProxyCommand=x", "--os", "linux", "--add", "--name", "build-01")]
    public async Task AddNeedsSshOsNameAndAConfig(params string[] args)
    {
        var before = Config();
        var r = await Add([.. args, "--yes"]);
        Assert.Equal(2, r.Code);
        Assert.Empty(r.Ssh.InputRuns);
        Assert.Equal(before, Config());

        // No config anywhere: refused before any scan, nothing created.
        r = await Add(AddArgs(more: "--yes"), withConfig: false, env: new() { ["HOME"] = _dir, ["XDG_CONFIG_HOME"] = Path.Combine(_dir, "none") });
        Assert.Equal(2, r.Code);
        Assert.Contains("No config found", r.Err, StringComparison.Ordinal);
        Assert.Empty(r.Ssh.InputRuns);

        // A config that does not load.
        await File.WriteAllTextAsync(ConfigPath, "machines: 12\n");
        r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(2, r.Code);
        Assert.Empty(r.Ssh.InputRuns);
        Assert.Equal("machines: 12\n", Config());
    }

    [Theory]
    [InlineData("EXAMPLE-01", Target)] // the name, case-insensitive like ConfigLoader
    [InlineData("build-01", "other@10.20.30.11")] // the host part of example-01's ssh, another user
    [InlineData("build-01", "x@10.20.30.11")]
    public async Task AddRefusesADuplicateNameOrHostBeforeScanning(string name, string target)
    {
        var r = await Add(AddArgs(name, target, more: "--yes"));
        Assert.Equal(2, r.Code);
        Assert.Empty(r.Ssh.InputRuns);
        Assert.Empty(r.Ssh.Calls);
        Assert.Contains("example-01", r.Err, StringComparison.Ordinal);
        Assert.Equal(Cli.StarterConfig, Config());
    }

    [Fact]
    public async Task AddAppendsAfterTheLastMachineKeepingEveryCommentByteForByte()
    {
        var r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(0, r.Code);
        var after = Config();
        var lines = after.Split('\n').ToList();
        var original = Cli.StarterConfig.Split('\n').ToList();

        // The new item sits right after example-01's last line, before init's commented examples, at the same dash column.
        var transfer = original.FindIndex(l => l.StartsWith("    transfer: git", StringComparison.Ordinal));
        Assert.Equal("  - name: \"build-01\"", lines[transfer + 1]);
        var added = lines.Skip(transfer + 1).TakeWhile(l => l.Length > 0).ToList();
        Assert.Equal(original, [.. lines.Take(transfer + 1), .. lines.Skip(transfer + 1 + added.Count)]); // every other line, byte for byte
        Assert.Contains("    ssh: \"build@10.20.30.41\"", added);
        Assert.Contains("    work_root: \"/srv/lq\"", added);
        Assert.True(after.IndexOf("build-01", StringComparison.Ordinal) < after.IndexOf("# - name: micro-win-01", StringComparison.Ordinal));

        var config = ConfigLoader.Parse(after);
        Assert.Equal(["example-01", "build-01"], config.Machines.Select(m => m.Name));
        Assert.Equal(Target, config.Machines[1].Ssh);
        Assert.Equal(MachineOs.Linux, config.Machines[1].Os);
    }

    [Fact]
    public async Task AddKeepsCrlfAndBom()
    {
        var crlf = Cli.StarterConfig.Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllBytesAsync(ConfigPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(crlf)]);
        var r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(0, r.Code);
        var bytes = await File.ReadAllBytesAsync(ConfigPath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(0xEF, bytes[0]);
        Assert.NotEqual(0xEF, bytes[3]); // one BOM, not two
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal); // every line ends in CRLF
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\r\n  - name: \"build-01\"\r\n", text, StringComparison.Ordinal);
        Assert.Equal(2, ConfigLoader.Load(ConfigPath).Machines.Count);

        // LF without a BOM stays that way.
        await File.WriteAllTextAsync(ConfigPath, Cli.StarterConfig);
        r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(0, r.Code);
        bytes = await File.ReadAllBytesAsync(ConfigPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    public static TheoryData<string> Unusual() =>
    [
        "policy:\n  allowed_commands: [\"dotnet test\"]\nmachines: [{name: a-01, os: linux, ssh: a@10.20.30.12, work_root: /srv/lq}]\n",
        "policy:\n  allowed_commands: [\"dotnet test\"]\nmachines: &m\n  - name: a-01\n    os: linux\n    ssh: a@10.20.30.12\n    work_root: /srv/lq\n",
        "policy:\n  allowed_commands: [\"dotnet test\"]\nmachines:\n  - name: a-01\n    os: linux\n    ssh: a@10.20.30.12\n    work_root: /srv/lq\t# a tab\n",
    ];

    [Theory]
    [MemberData(nameof(Unusual))]
    public async Task AddRefusesFlowStyleMachines(string yaml)
    {
        await File.WriteAllTextAsync(ConfigPath, yaml);
        var r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(2, r.Code);
        Assert.Contains("paste its entry by hand", r.Err, StringComparison.Ordinal);
        Assert.Empty(r.Ssh.InputRuns); // refused before the scan
        Assert.Equal(yaml, Config());
    }

    [Theory]
    [InlineData("machines:\n  - name: a\n---\nmachines:\n  - name: b\n", "more than one YAML document")]
    [InlineData("policy: {}\n...\n", "more than one YAML document")]
    [InlineData("policy:\n  load_threshold: 0.8\n", "no top-level machines:")]
    [InlineData("machines:\n  # nothing yet\npolicy: {}\n", "no machine to line the new one up with")]
    [InlineData("machines:\n  - name: a\nmachines:\n  - name: b\n", "more than once")]
    public void InsertRefusesWhatItCannotEditSafely(string yaml, string why) =>
        Assert.Contains(why, Assert.Throws<InsertRefusedException>(() => MachinesYaml.Insert(yaml, ["  - name: x"])).Message, StringComparison.Ordinal);

    [Fact]
    public void InsertLinesUpWithTheFilesOwnDashColumnAndKeepsTheRest()
    {
        const string Yaml = "# top\nmachines:\n- name: a   # first\n  os: linux\n\n# trailing comment\npolicy:\n  load_threshold: 0.8";
        var ins = MachinesYaml.Insert(Yaml, ["  - name: b", "    os: linux"]);
        Assert.Equal(["- name: b", "  os: linux"], ins.Lines);
        Assert.Equal(4, ins.AfterLine);
        Assert.Equal("# top\nmachines:\n- name: a   # first\n  os: linux\n- name: b\n  os: linux\n\n# trailing comment\npolicy:\n  load_threshold: 0.8", ins.Text);

        // At the very end of a file without a final line break.
        ins = MachinesYaml.Insert("machines:\n    - name: a", ["  - name: b"]);
        Assert.Equal("machines:\n    - name: a\n    - name: b\n", ins.Text);
    }

    [Fact]
    public async Task AddRefusesWhenTheResultWouldNotLoadAndWritesNothing()
    {
        // A Linux worker whose report suggests a Windows work_root: the entry would not load, so it is printed to paste by hand.
        var report = Report().Replace("\"work_root\": \"/srv/lq\"", "\"work_root\": \"C:/lq\"", StringComparison.Ordinal);
        var r = await Add(AddArgs(more: "--yes"), report);
        Assert.Equal(2, r.Code);
        Assert.Contains("would not load", r.Err, StringComparison.Ordinal);
        Assert.Contains("Paste this entry under machines: by hand:", r.Out, StringComparison.Ordinal);
        Assert.Contains("  - name: \"build-01\"", r.Out, StringComparison.Ordinal);
        Assert.Equal(Cli.StarterConfig, Config());
        Assert.Empty(r.Ssh.Calls); // no probe of a machine that was not added
        Assert.Empty(Directory.GetFiles(_dir, "*.lanq-tmp"));
    }

    [Fact]
    public void AddChecksThereIsExactlyOneMoreMachine()
    {
        var before = ConfigLoader.Parse(Cli.StarterConfig);
        string Item(string name, string host) => $"  - name: {name}\n    os: linux\n    ssh: build@{host}\n    work_root: /srv/lq\n";
        var good = Cli.StarterConfig.Replace("\n\n  # A Windows", "\n" + Item("build-01", "10.20.30.41") + "\n  # A Windows", StringComparison.Ordinal);
        Assert.Equal(2, Cli.VerifyAdded(before, good, "build-01", Target, MachineOs.Linux, "/srv/lq", null).Machines.Count);

        // Nothing added, two added, an old machine changed, the policy changed, or the new one not as shown: all refused.
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, Cli.StarterConfig, "build-01", Target, MachineOs.Linux, "/srv/lq", null));
        var two = good.Replace("\n\n  # A Windows", "\n" + Item("build-02", "10.20.30.42") + "\n  # A Windows", StringComparison.Ordinal);
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, two, "build-01", Target, MachineOs.Linux, "/srv/lq", null));
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, good.Replace("max_jobs: 1 ", "max_jobs: 2 ", StringComparison.Ordinal), "build-01", Target, MachineOs.Linux, "/srv/lq", null));
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, good.Replace("load_threshold: 0.80", "load_threshold: 0.90", StringComparison.Ordinal), "build-01", Target, MachineOs.Linux, "/srv/lq", null));
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, good, "build-01", Target, MachineOs.Linux, "/srv/lq", "LANQ_KEY"));
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, good, "build-01", Target, MachineOs.Windows, "/srv/lq", null));
        Assert.Throws<InsertRefusedException>(() => Cli.VerifyAdded(before, good + "bogus: 1\n", "build-01", Target, MachineOs.Linux, "/srv/lq", null));
    }

    /// <summary>The comparison covers every machine setting: a new property on Machine must be added to Cli.Describe too.</summary>
    [Fact]
    public void TheMachineComparisonCoversEveryProperty() =>
        Assert.Equal(14, typeof(Machine).GetProperties().Length);

    [Fact]
    public async Task AddNeedsYesWithoutATerminalAndWithJson()
    {
        // Not a terminal (no confirm callback) and no --yes.
        var r = await Add(AddArgs());
        Assert.Equal(2, r.Code);
        Assert.Contains("--yes", r.Err, StringComparison.Ordinal);
        Assert.Empty(r.Ssh.InputRuns);

        // --json never asks, even on a terminal.
        r = await Add(AddArgs(more: "--json"), answer: true);
        Assert.Equal(2, r.Code);
        Assert.Equal(0, r.Asked);
        Assert.Empty(r.Ssh.InputRuns);
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(Cli.StarterConfig, Config());

        // --yes without --add means nothing.
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", Target, "--os", "linux", "--yes"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", Target, "--os", "linux", "--name", "build-01"]));
    }

    [Fact]
    public async Task ADeclinedAddWritesNothing()
    {
        var r = await Add(AddArgs(), answer: false);
        Assert.Equal(1, r.Code);
        Assert.Equal(1, r.Asked);
        Assert.Contains("Not added", r.Err, StringComparison.Ordinal);
        Assert.Equal(Cli.StarterConfig, Config());
        Assert.Empty(r.Ssh.Calls);
        Assert.Empty(Directory.GetFiles(_dir, "*.lanq-tmp"));

        // Said yes on the terminal: written.
        r = await Add(AddArgs(), answer: true);
        Assert.Equal(0, r.Code);
        Assert.Equal(1, r.Asked);
        Assert.Equal(2, ConfigLoader.Load(ConfigPath).Machines.Count);
    }

    [Fact]
    public async Task AddNeverWritesThroughALinkOrOverAFileChangedMeanwhile()
    {
        // Changed between the read and the write: nothing written, the other change kept.
        const string Theirs = "# edited by someone else meanwhile\n";
        var r = await Add(AddArgs(more: "--yes"), beforeWrite: () => File.AppendAllText(ConfigPath, Theirs));
        Assert.Equal(70, r.Code);
        Assert.Contains("changed while lanq was adding", r.Err, StringComparison.Ordinal);
        Assert.Equal(Cli.StarterConfig + Theirs, Config());
        Assert.Empty(Directory.GetFiles(_dir, "*.lanq-tmp"));
        Assert.Empty(r.Ssh.Calls);

        // A link at the config path: never written through (the file it points at stays as it was).
        var real = Path.Combine(_dir, "real.yml");
        await File.WriteAllTextAsync(real, Cli.StarterConfig);
        var link = Path.Combine(_dir, "link.yml");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the symlink privilege: a junction is a link too (and not a file lanq could read).
            FetchReceiverTests.MakeDirLink(link, _dir);
        }

        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Healthy), InputHandler = (_, _, _) => new SshResult(0, Report()) };
        var e = new StringWriter();
        var code = await Cli.Makeready(
            ParsedArgs.Parse(["makeready", .. AddArgs(more: "--yes"), "--config", link]), () => throw new InvalidOperationException(), ssh, new HealthProbe(ssh, TimeProvider.System),
            new StringWriter(), e, CancellationToken.None, new MakereadyHost(_ => null, false, () => null, null));
        Assert.Equal(2, code);
        Assert.Empty(ssh.InputRuns);
        Assert.Equal(Cli.StarterConfig, await File.ReadAllTextAsync(real));
    }

    [Fact]
    public async Task AddOnlyAddsAReadyWorker()
    {
        var r = await Add(AddArgs(more: "--yes"), Report(ready: false));
        Assert.Equal(1, r.Code);
        Assert.Contains("NOT READY", r.Err, StringComparison.Ordinal);
        Assert.Contains("[FAIL] OpenSSH Server", r.Out, StringComparison.Ordinal); // why, for the PC's owner
        Assert.DoesNotContain("these lines go into", r.Out, StringComparison.Ordinal);
        Assert.Equal(Cli.StarterConfig, Config());
        Assert.Single(r.Ssh.InputRuns);
        Assert.Empty(r.Ssh.Calls); // no probe

        // The next step is the rescan, for the developer: never --add or --yes.
        Assert.Contains("Next: (for the developer) the PC is NOT READY", r.Err, StringComparison.Ordinal);
        Assert.Contains(": lanq makeready --ssh build@10.20.30.41 --os linux", r.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("--add", r.Err.Split('\n').Where(l => l.StartsWith("Next:", StringComparison.Ordinal)).Aggregate("", string.Concat), StringComparison.Ordinal);

        // An ssh failure: 70, nothing written.
        r = await Add(AddArgs(more: "--yes"), scan: new SshResult(255, "") { ErrorOutput = "build@10.20.30.41: Permission denied (publickey)." });
        Assert.Equal(70, r.Code);
        Assert.Equal(Cli.StarterConfig, Config());
        Assert.Contains("--setup", r.Err, StringComparison.Ordinal); // the one-time setup, for the developer
    }

    [LinuxFact]
    public async Task AddPreservesTheUnixFileMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // [LinuxFact] already skips it; this tells the platform analyzer
        }

        foreach (var mode in new[] { UnixFileMode.UserRead | UnixFileMode.UserWrite, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead })
        {
            await File.WriteAllTextAsync(ConfigPath, Cli.StarterConfig);
            File.SetUnixFileMode(ConfigPath, mode);
            var r = await Add(AddArgs(more: "--yes"));
            Assert.Equal(0, r.Code);
            Assert.Equal(mode, File.GetUnixFileMode(ConfigPath));
        }
    }

    [Fact]
    public async Task AddWarnsAboutTheInitPlaceholder()
    {
        var r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(0, r.Code);
        Assert.Contains("warning: example-01, the example machine from lanq init, is still in", r.Err, StringComparison.Ordinal);
        Assert.Contains("Next: example-01, the example from lanq init, is still in machines.yml; delete it", r.Err, StringComparison.Ordinal);

        // A config without it: no warning.
        await File.WriteAllTextAsync(ConfigPath, T.Yaml + "\n");
        r = await Add(AddArgs("build-03", "build@10.20.30.43", more: "--yes"));
        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("example-01", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAlsoWarnsAboutAStarterWrittenByOlderLanq()
    {
        // lanq init 0.5.x wrote work_root /srv/lanq for example-01; it is still the placeholder.
        await File.WriteAllTextAsync(ConfigPath, Cli.StarterConfig.Replace("work_root: /srv/lq ", "work_root: /srv/lanq", StringComparison.Ordinal));
        Assert.Equal("/srv/lanq", ConfigLoader.Load(ConfigPath).Machines[0].WorkRoot);

        var r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(0, r.Code);
        Assert.Contains("warning: example-01, the example machine from lanq init, is still in", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProbesTheNewMachineOnceAndNeverWakes()
    {
        // The report suggests Wake-on-LAN: the entry keeps it commented, and the one probe never sends a packet (there is no waker at all).
        var report = Report().Replace("\"allowed_hours\": \"00:00-07:00\"", "\"allowed_hours\": \"00:00-07:00\", \"wake\": { \"mac\": \"aa:bb:cc:dd:ee:ff\" }", StringComparison.Ordinal);
        var r = await Add(AddArgs(more: "--yes"), report);
        Assert.Equal(0, r.Code);
        var probe = Assert.Single(r.Ssh.Calls);
        Assert.Equal("build-01", probe.Machine);
        Assert.Contains("health: build-01", r.Out, StringComparison.Ordinal);
        Assert.Matches(@"^health: build-01 +(OK|HOLD) +load 0\.10", r.Out.Split('\n').Single(l => l.StartsWith("health:", StringComparison.Ordinal))); // HOLD outside its hours
        Assert.Contains("    #   mac: \"aa:bb:cc:dd:ee:ff\"", Config(), StringComparison.Ordinal);
        Assert.Null(ConfigLoader.Load(ConfigPath).Machines[1].Wake);
        Assert.Contains("Next: check the new machine and this computer's setup: lanq doctor --remote --config", r.Err, StringComparison.Ordinal);

        // A machine that does not answer the probe was still added: the probe only reports.
        var down = new FakeSsh { Handler = (_, _) => new SshResult(255, "ssh: connect to host 10.20.30.44 port 22: No route to host"), InputHandler = (_, _, _) => new SshResult(0, Report()) };
        var o = new StringWriter();
        var code = await Cli.Makeready(
            ParsedArgs.Parse(["makeready", .. AddArgs("build-04", "build@10.20.30.44", more: "--yes"), "--config", ConfigPath]), () => throw new InvalidOperationException(), down,
            new HealthProbe(down, TimeProvider.System), o, new StringWriter(), CancellationToken.None, new MakereadyHost(_ => null, false, () => null, null));
        Assert.Equal(0, code);
        Assert.Contains("DOWN", o.ToString(), StringComparison.Ordinal);
        Assert.Single(down.Calls);
    }

    [Fact]
    public async Task TheAppendedLinesAreTheLinesShown()
    {
        var r = await Add(AddArgs(more: "--yes"));
        Assert.Equal(0, r.Code);
        var shownAt = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var header = shownAt.FindIndex(l => l.StartsWith("lanq makeready --add: these lines go into", StringComparison.Ordinal));
        Assert.True(header >= 0);
        var shown = shownAt.Skip(header + 1).TakeWhile(l => l.StartsWith(' ')).ToList();
        Assert.NotEmpty(shown);

        // The file is the original with exactly those lines inserted.
        var original = Cli.StarterConfig.Split('\n').ToList();
        var now = Config().Split('\n').ToList();
        var at = now.IndexOf(shown[0]);
        Assert.Equal(shown, now.Skip(at).Take(shown.Count));
        Assert.Equal(original, [.. now.Take(at), .. now.Skip(at + shown.Count)]);
        Assert.Contains($"after line {at}:", shownAt[header], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddJsonPrintsOneObject()
    {
        var r = await Add(AddArgs(more: ["--yes", "--json"]));
        Assert.Equal(0, r.Code);
        using (var doc = JsonDocument.Parse(r.Out))
        {
            var root = doc.RootElement;
            Assert.Equal("Added", root.GetProperty("status").GetString());
            Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
            Assert.Equal(ConfigPath, root.GetProperty("path").GetString());
            Assert.Equal("build-01", root.GetProperty("machine").GetString());
            var entry = root.GetProperty("entry").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal("  - name: \"build-01\"", entry[0]);
            Assert.All(entry, line => Assert.Contains(line, Config().Split('\n')));
            Assert.Equal("build-01", root.GetProperty("health").GetProperty("name").GetString());
            Assert.Contains(root.GetProperty("health").GetProperty("state").GetString(), new[] { "ok", "hold" }); // hold outside its allowed hours
            Assert.Equal(JsonValueKind.Null, root.GetProperty("health").GetProperty("localTime").ValueKind); // null members are written
            Assert.Equal("lanq-makeready/v1", root.GetProperty("report").GetProperty("schema").GetString());
            var doctor = root.GetProperty("nextSteps").EnumerateArray().First();
            Assert.Equal("run_doctor", doctor.GetProperty("action").GetString());
            Assert.True(doctor.GetProperty("safeToRunUnattended").GetBoolean());
            Assert.Equal(["lanq", "doctor", "--remote", "--json", "--config", ConfigPath], doctor.GetProperty("argv").EnumerateArray().Select(a => a.GetString())); // --add had --json (0.7.0)
        }

        Assert.DoesNotContain("lanq makeready", r.Out.Split('\n')[1..].Aggregate("", string.Concat), StringComparison.Ordinal); // only the object on stdout

        // Every other path is one object too, with nulls where there is nothing yet.
        foreach (var (args, report, status, code) in new (string[], string?, string, int)[]
        {
            (AddArgs("build-05", "build@10.20.30.45", more: ["--yes", "--json"]), Report(ready: false), "NotAdded", 1),
            (AddArgs("build-01", more: ["--yes", "--json"]), null, "Rejected", 2), // build-01 was added above
            (["--ssh", Target, "--os", "linux", "--add", "--yes", "--json"], null, "Rejected", 2),
            (AddArgs("build-06", "build@10.20.30.46", more: ["--yes", "--json"]), "not json", "InfraError", 70),
        })
        {
            r = await Add(args, report);
            Assert.Equal(code, r.Code);
            using var doc = JsonDocument.Parse(r.Out);
            Assert.Equal(status, doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(code, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("health").ValueKind);
            Assert.True(doc.RootElement.GetProperty("nextSteps").GetArrayLength() >= 1);
            Assert.All(
                doc.RootElement.GetProperty("nextSteps").EnumerateArray().Where(s => s.TryGetProperty("argv", out _)),
                s => Assert.DoesNotContain("--add", s.GetProperty("argv").EnumerateArray().Select(a => a.GetString())));
        }
    }

    [Fact]
    public async Task KeyEnvIsUsedForTheScanAndWrittenToTheEntry()
    {
        var r = await Add(AddArgs(os: "windows", more: ["--key-env", "LANQ_KEY_NEW", "--yes"]), WindowsReport());
        Assert.Equal(0, r.Code);
        var scan = Assert.Single(r.Ssh.InputRuns);
        Assert.Equal("LANQ_KEY_NEW", scan.Machine.KeyEnv);
        Assert.Equal(MachineOs.Windows, scan.Machine.Os);
        var added = ConfigLoader.Load(ConfigPath).Machines[1];
        Assert.Equal("LANQ_KEY_NEW", added.KeyEnv);
        Assert.Equal("C:/lq", added.WorkRoot);
        Assert.Equal("build-01", Assert.Single(r.Ssh.Calls).Machine); // the one probe, of the machine as written (key_env included)

        // Plain lanq makeready --ssh --key-env scans with that key and shows it in the suggested entry.
        var ssh = new FakeSsh { InputHandler = (_, _, _) => new SshResult(0, Report()) };
        var o = new StringWriter();
        Assert.Equal(0, await Cli.Makeready(ParsedArgs.Parse(["makeready", "--ssh", Target, "--os", "linux", "--key-env", "LANQ_KEY"]), T.Config, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None));
        Assert.Equal("LANQ_KEY", Assert.Single(ssh.InputRuns).Machine.KeyEnv);
        Assert.Contains("    key_env: LANQ_KEY", o.ToString(), StringComparison.Ordinal);
        Assert.Contains("IdentitiesOnly=yes", SshArgs.Ssh(ssh.InputRuns[0].Machine, "/keys/k", "x"), StringComparer.Ordinal);

        // --key-env is a variable NAME, and only with --ssh.
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", Target, "--os", "linux", "--key-env", "$(evil)"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", Target, "--os", "linux", "--key-env", "~/.ssh/id_ed25519"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "build-01", "--key-env", "LANQ_KEY"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--all", "--key-env", "LANQ_KEY"]));
    }
}
