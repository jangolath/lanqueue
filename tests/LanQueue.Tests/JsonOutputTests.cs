using System.Text.Json;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq health --json and lanq run --json: what agents parse.</summary>
public sealed class JsonOutputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-json-" + Guid.NewGuid().ToString("N"));

    public JsonOutputTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static bool IsProbe(Config.Machine m, string cmd) => Decoded(m, cmd).Contains("disk_free_gb", StringComparison.Ordinal);

    private static string Decoded(Config.Machine m, string cmd) =>
        m.Os == Config.MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    [Fact]
    public void JsonFlagIsParsedAndRefusedWhereItMeansNothing()
    {
        Assert.True(ParsedArgs.Parse(["health", "--json"]).Json);
        Assert.True(ParsedArgs.Parse(["run", "--json", "--", "dotnet", "test"]).Json);
        Assert.False(ParsedArgs.Parse(["health"]).Json);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["stats", "--json"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--needs", "dotnet"]));
    }

    [Fact]
    public async Task HealthJsonIsOneArrayWithStateReasonsAndNumbers()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        config.Machines.Add(new Config.Machine { Name = "build-03", Os = Config.MachineOs.Linux, Ssh = "user@build-03.lan", WorkRoot = "/srv/lanq" });
        var ssh = new FakeSsh
        {
            Handler = (m, _) => m.Name switch
            {
                "build-01" => new SshResult(0, "load=0.25\ndisk_free_gb=120.5\nslots_used=1\nmem_free_gb=3.5\nlocal_time=2026-01-05T22:15:00\ntool=ok\npaused=0"),
                "build-02" => new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=1"),
                _ => new SshResult(255, "ssh: connect to host build-03.lan port 22: Connection refused\u001b[2J"),
            },
        };
        var o = new StringWriter();
        var code = await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None, json: true);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o.ToString());
        var a = doc.RootElement;
        Assert.Equal(3, a.GetArrayLength());

        var ok = a[0];
        Assert.Equal("build-01", ok.GetProperty("name").GetString());
        Assert.Equal("ok", ok.GetProperty("state").GetString());
        Assert.Equal(0, ok.GetProperty("reasons").GetArrayLength());
        Assert.Equal(0.25, ok.GetProperty("load").GetDouble());
        Assert.Equal(120.5, ok.GetProperty("diskFreeGb").GetDouble());
        Assert.Equal(3.5, ok.GetProperty("memFreeGb").GetDouble());
        Assert.Equal(1, ok.GetProperty("slotsUsed").GetInt32());
        Assert.Equal(2, ok.GetProperty("maxJobs").GetInt32());
        Assert.Equal("2026-01-05T22:15:00", ok.GetProperty("localTime").GetString());

        var held = a[1];
        Assert.Equal("hold", held.GetProperty("state").GetString());
        Assert.Equal("paused on the machine", held.GetProperty("reasons")[0].GetString());
        Assert.Equal(JsonValueKind.Null, held.GetProperty("memFreeGb").ValueKind);
        Assert.Equal(JsonValueKind.Null, held.GetProperty("localTime").ValueKind);

        var down = a[2];
        Assert.Equal("down", down.GetProperty("state").GetString());
        var reason = down.GetProperty("reasons")[0].GetString()!;
        Assert.Contains("Connection refused", reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', reason); // sanitised like the text output
    }

    [Fact]
    public async Task RunJsonPrintsOnlyOneObjectOnStdoutAndTheJobOutputOnStderr()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => IsProbe(m, cmd)
                ? new SshResult(0, $"load={(m.Name == "build-01" ? "0.5" : "0.1")}\ndisk_free_gb=50\nslots_used=0\ntool=ok")
                : Decoded(m, cmd).Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "job says hello"),
        };
        var o = new StringWriter();
        var e = new StringWriter();
        var p = ParsedArgs.Parse(["run", "--json", "--needs", "dotnet", "--", "dotnet", "test"]);
        var code = await Cli.Run(
            p, config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")),
            _dir, _dir, o, e, CancellationToken.None);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o.ToString()); // stdout is exactly one JSON value
        var r = doc.RootElement;
        Assert.Equal("Completed", r.GetProperty("status").GetString());
        Assert.Equal(0, r.GetProperty("exitCode").GetInt32());
        Assert.Equal("build-02", r.GetProperty("machine").GetString());
        Assert.False(string.IsNullOrEmpty(r.GetProperty("jobId").GetString()));
        Assert.True(r.GetProperty("durationSeconds").GetDouble() >= 0);
        Assert.Equal("exit 0", r.GetProperty("message").GetString());
        Assert.Contains("job says hello", e.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("job says hello", o.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunJsonReportsARefusalWithItsExitCode()
    {
        var ssh = new FakeSsh();
        var o = new StringWriter();
        var p = ParsedArgs.Parse(["run", "--json", "--", "rm", "-rf", "/"]);
        var code = await Cli.Run(
            p, T.Config(), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")),
            _dir, _dir, o, new StringWriter(), CancellationToken.None);

        Assert.Equal(2, code);
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("machine").ValueKind);
    }
}
