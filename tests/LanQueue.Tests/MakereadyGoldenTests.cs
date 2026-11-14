using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// Byte-for-byte goldens of lanq makeready without --tune (captured with lanq 0.11.0, before 0.12.0 changed anything): the single-machine
/// text and JSON, --all text and JSON, the error objects and an unconfigured PC. The config has no allowed_hours, so the scanner command
/// (which carries -Hours since 0.12.0) and everything printed stay as they were. The embedded scanner's version in the --all header is
/// normalised. A missing or different golden writes the actual output to tests/LanQueue.Tests/TestResults/goldens/ for review.
/// </summary>
public class MakereadyGoldenTests
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
            capabilities: [dotnet, node]
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

    private static SshResult Probe(Machine m) => m.Name switch
    {
        "charlie" => new SshResult(0, Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal)),
        "delta" => new SshResult(255, "ssh: connect to host 10.20.30.14 port 22: No route to host"),
        _ => new SshResult(0, Healthy),
    };

    private static SshResult Scan(Machine m) => new(0, m.Os == MachineOs.Windows ? MakereadySummaryTests.V020 : MakereadyTests.Report());

    private static async Task<string> Run(params string[] args)
    {
        var ssh = new FakeSsh { Handler = (m, _) => Probe(m), InputHandler = (m, _, _) => Scan(m) };
        var o = new StringWriter();
        var e = new StringWriter();
        var config = ConfigLoader.Parse(Fleet);
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", .. args]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None);
        var commands = string.Join("\n", ssh.InputRuns.Select(r => r.Machine.Name + ": " + r.Command).Order(StringComparer.Ordinal));
        return Normalise($"exit={code}\n--- commands\n{commands}\n--- stdout\n{o}\n--- stderr\n{e}");
    }

    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("lanq-makeready " + Cli.MakereadyVersion, "lanq-makeready <embedded>", StringComparison.Ordinal);

    public static TheoryData<string, string[]> Cases() => new()
    {
        { "single-linux-text", ["alpha"] },
        { "single-linux-json", ["alpha", "--json"] },
        { "single-windows-text", ["bravo"] },
        { "single-windows-json", ["bravo", "--json"] },
        { "held-text", ["charlie"] },
        { "held-json", ["charlie", "--json"] },
        { "unreachable-json", ["delta", "--json"] },
        { "unknown-json", ["echo", "--json"] },
        { "adhoc-text", ["--ssh", "me@new-pc.lan", "--os", "linux"] },
        { "adhoc-json", ["--ssh", "me@new-pc.lan", "--os", "windows", "--json"] },
        { "all-text", ["--all"] },
        { "all-json", ["--all", "--json"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OutputWithoutTuneIsByteIdenticalToTheGolden(string name, string[] args)
    {
        var actual = await Run(args);
        var golden = Path.Combine(T.RepoRoot, "tests", "LanQueue.Tests", "Goldens", "makeready", name + ".txt");
        var expected = File.Exists(golden) ? Normalise(await File.ReadAllTextAsync(golden)) : null;
        if (expected != actual)
        {
            var dir = Path.Combine(T.RepoRoot, "tests", "LanQueue.Tests", "TestResults", "goldens");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, name + ".txt"), actual);
        }

        Assert.True(expected is not null, $"no golden for {name}; the actual output is in TestResults/goldens");
        Assert.Equal(expected, actual);
    }
}
