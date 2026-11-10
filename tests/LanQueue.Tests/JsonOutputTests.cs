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
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--dir", "x"])); // health takes --needs since 0.6.0, never --dir
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

    /// <summary>A cancellation nobody asked for (ct not cancelled), for example an HttpClient-style timeout, is an infrastructure error.</summary>
    private sealed class CancellingPayloads : Payload.IPayloadBuilder
    {
        public Task<Payload.JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, Payload.PayloadKinds kinds, CancellationToken ct) =>
            throw new TaskCanceledException("a git call was cancelled");

        public Task<Payload.ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) => throw new IOException("no");

        public Task<Payload.BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) =>
            throw new IOException("no");
    }

    [Fact]
    public async Task RunJsonPrintsOneObjectForACancellationNobodyAskedFor()
    {
        var ssh = new FakeSsh();
        var o = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "dotnet", "--", "dotnet", "test"]), T.Config(), ssh, new CancellingPayloads(),
            new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir, o, new StringWriter(), CancellationToken.None);

        Assert.Equal(70, code);
        OneObject(o.ToString(), "InfraError", 70);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("health")]
    public async Task JsonPrintsOneObjectWhenTheConfigCannotBeRead(string command)
    {
        var o = new StringWriter();
        string[] args = command == "run" ? ["run", "--json", "--", "dotnet", "test"] : ["health", "--json"];
        var code = await Cli.RunAsync(args, o, new StringWriter(), CancellationToken.None, () => throw new UnauthorizedAccessException("Access to the path is denied."));
        Assert.Equal(2, code);
        Assert.Contains("denied", OneObject(o.ToString(), "Rejected", 2).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Probes that notice Ctrl+C.</summary>
    private sealed class CancelAwareSsh : ISshClient
    {
        public Task<SshResult> RunAsync(Config.Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok"));
        }

        public Task<SshResult> UploadAsync(Config.Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new SshResult(0, ""));

        /// <summary>A split run (the fetch) goes through RunAsync with stdout as the line callback, so this fake serves --fetch runs too.</summary>
        public Task<SshResult> RunSplitAsync(
            Config.Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
            RunAsync(machine, remoteCommand, onStdout, timeout, ct);
    }

    private async Task<(int Code, JsonElement Json, string Stderr)> RunJson(Config.FleetConfig config, Func<Config.Machine, string, SshResult> handler, params string[] args)
    {
        var ssh = new FakeSsh { Handler = handler };
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", .. args]), config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System),
            new JobLog(Path.Combine(_dir, "state")), _dir, _dir, o, e, CancellationToken.None);
        using var doc = JsonDocument.Parse(o.ToString());
        return (code, doc.RootElement.Clone(), e.ToString());
    }

    private static SshResult Probe(Config.Machine m, string cmd, string probe) =>
        IsProbe(m, cmd) ? new SshResult(0, probe) : Decoded(m, cmd).Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=FULL") : new SshResult(0, "");

    [Fact]
    public async Task NoCapacityJsonCarriesRetryableAndExcluded()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var (code, r, _) = await RunJson(
            config,
            (m, cmd) => Probe(m, cmd, m.Name == "build-01" ? "load=0.1\ndisk_free_gb=50\nslots_used=2\ntool=ok" : "load=0.95\ndisk_free_gb=50\nslots_used=0\ntool=ok"),
            "--needs", "dotnet", "--", "dotnet", "test");

        Assert.Equal(75, code);
        Assert.Equal("NoCapacity", r.GetProperty("status").GetString());
        Assert.True(r.GetProperty("retryable").GetBoolean());
        var excluded = r.GetProperty("excluded");
        Assert.Equal(2, excluded.GetArrayLength());
        Assert.Equal("build-01", excluded[0].GetProperty("machine").GetString());
        Assert.Equal("busy", excluded[0].GetProperty("reason").GetString());
        Assert.Equal("busy (slots 2/2)", excluded[0].GetProperty("why").GetString());
        Assert.False(excluded[0].GetProperty("permanent").GetBoolean());
        Assert.Equal("load", excluded[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task RetryableAndExcludedAppearOnlyOnNoCapacity()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var (code, done, _) = await RunJson(
            config,
            (m, cmd) => IsProbe(m, cmd) ? new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok")
                : Decoded(m, cmd).Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "ran"),
            "--needs", "dotnet", "--", "dotnet", "test");
        Assert.Equal(0, code);
        Assert.False(done.TryGetProperty("retryable", out _));
        Assert.False(done.TryGetProperty("excluded", out _));

        var (refusedCode, refused, _) = await RunJson(T.Config(), (_, _) => new SshResult(0, ""), "--", "rm", "-rf", "/");
        Assert.Equal(2, refusedCode);
        Assert.False(refused.TryGetProperty("retryable", out _));
        Assert.False(refused.TryGetProperty("excluded", out _));
    }

    [Fact]
    public async Task BothKindsExit75()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var busy = await RunJson(config, (m, cmd) => Probe(m, cmd, "load=0.1\ndisk_free_gb=50\nslots_used=2\ntool=ok"), "--needs", "dotnet", "--", "dotnet", "test");
        var never = await RunJson(config, (m, cmd) => Probe(m, cmd, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok"), "--needs", "gpu", "--", "dotnet", "test");

        Assert.Equal((75, true), (busy.Code, busy.Json.GetProperty("retryable").GetBoolean()));
        Assert.Equal((75, false), (never.Code, never.Json.GetProperty("retryable").GetBoolean()));
        Assert.Equal("NoCapacity", never.Json.GetProperty("status").GetString());
        Assert.All(never.Json.GetProperty("excluded").EnumerateArray(), x =>
        {
            Assert.Equal("missing_capability", x.GetProperty("reason").GetString());
            Assert.True(x.GetProperty("permanent").GetBoolean());
        });
        Assert.StartsWith("No eligible machine", never.Json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("[lanq] NoCapacity: ", never.Stderr.TrimEnd().Split('\n')[^1], StringComparison.Ordinal); // the status line stays last
    }

    [Fact]
    public async Task ShardNoCapacityJsonCarriesRetryableAndExcludedAtTheTopOnly()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var (code, r, _) = await RunJson(config, (m, cmd) => Probe(m, cmd, "load=0.1\ndisk_free_gb=50\nslots_used=2\ntool=ok"), "--needs", "dotnet", "--shards", "2", "--", "dotnet", "test");

        Assert.Equal(75, code);
        Assert.True(r.GetProperty("retryable").GetBoolean());
        Assert.Equal(2, r.GetProperty("excluded").GetArrayLength());
        Assert.All(r.GetProperty("shards").EnumerateArray(), s => Assert.False(s.TryGetProperty("retryable", out _) || s.TryGetProperty("excluded", out _)));
    }

    [Fact]
    public async Task HealthJsonPrintsOneObjectWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ssh = new CancelAwareSsh();
        var o = new StringWriter();
        var code = await Cli.Health(T.Config(), new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), cts.Token, json: true);
        Assert.Equal(130, code);
        OneObject(o.ToString(), "Cancelled", 130);
    }
}
