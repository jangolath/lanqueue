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

    /// <summary>stdout must be exactly one JSON object with this status and exit code, whatever went wrong.</summary>
    private static JsonElement OneObject(string stdout, string status, int exitCode)
    {
        using var doc = JsonDocument.Parse(stdout); // throws unless stdout is exactly one JSON value
        var r = doc.RootElement.Clone();
        Assert.Equal(JsonValueKind.Object, r.ValueKind);
        Assert.Equal(status, r.GetProperty("status").GetString());
        Assert.Equal(exitCode, r.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrEmpty(r.GetProperty("message").GetString()));
        return r;
    }

    [Theory]
    [InlineData("run", "--json", "--config", "does-not-exist.yml", "--", "dotnet", "test")]
    [InlineData("run", "--json", "--timeout", "0", "--", "dotnet", "test")]
    [InlineData("run", "--json", "--bogus", "--", "dotnet", "test")]
    public async Task RunJsonPrintsOneObjectForConfigAndArgumentErrors(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(args, o, e, CancellationToken.None));
        var r = OneObject(o.ToString(), "Rejected", 2);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("jobId").ValueKind);
        Assert.NotEqual("", e.ToString()); // the people-readable message still goes to stderr
    }

    [Fact]
    public async Task HealthJsonPrintsOneObjectForAConfigError()
    {
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["health", "--json", "--config", "does-not-exist.yml"], o, new StringWriter(), CancellationToken.None));
        Assert.Contains("not found", OneObject(o.ToString(), "Rejected", 2).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunJsonPrintsOneObjectWhenCancelled()
    {
        var config = T.Config();
        var ssh = new FakeSsh { Handler = (m, cmd) => IsProbe(m, cmd) ? new SshResult(0, "load=0.99\ndisk_free_gb=50\nslots_used=0\ntool=ok") : new SshResult(0, "") };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var o = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--wait", "600", "--needs", "dotnet", "--", "dotnet", "test"]), config, ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir, o, new StringWriter(), cts.Token);

        Assert.Equal(130, code);
        OneObject(o.ToString(), "Cancelled", 130);
    }

    private sealed class ThrowingPayloads : Payload.IPayloadBuilder
    {
        public Task<Payload.JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, Payload.PayloadKinds kinds, CancellationToken ct) =>
            throw new IOException("disk on fire");

        public Task<Payload.ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) => throw new IOException("no");

        public Task<Payload.BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) =>
            throw new IOException("no");
    }

    [Fact]
    public async Task RunJsonPrintsOneObjectForAnUnexpectedError()
    {
        var ssh = new FakeSsh();
        var o = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "dotnet", "--", "dotnet", "test"]), T.Config(), ssh, new ThrowingPayloads(),
            new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir, o, new StringWriter(), CancellationToken.None);

        Assert.Equal(70, code);
        Assert.Contains("disk on fire", OneObject(o.ToString(), "InfraError", 70).GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
