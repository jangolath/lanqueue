using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq health --needs a,b [--mem N]: which machines could take such a job now, judged exactly as lanq run judges them.</summary>
public sealed class HealthNeedsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-health-needs-" + Guid.NewGuid().ToString("N"));

    public HealthNeedsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>build-01 (linux, dotnet + node, 2 slots) answers; build-02 (windows, dotnet, 1 slot, no hours) answers too unless <paramref name="down"/>.</summary>
    private static (FleetConfig Config, FakeSsh Ssh) Fleet(bool down = false)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var ssh = new FakeSsh
        {
            Handler = (m, _) => m.Name == "build-01"
                ? new SshResult(0, "load=0.10\ndisk_free_gb=50\nslots_used=0\nmem_free_gb=10\nmem_total_gb=16\ncores=8\ntool=ok")
                : down
                    ? new SshResult(255, "ssh: connect to host build-02.lan port 22: Connection refused")
                    : new SshResult(0, "load=0.20\ndisk_free_gb=40\nslots_used=0\nmem_free_gb=3\nmem_total_gb=8\ncores=4\ntool=ok"),
        };
        return (config, ssh);
    }

    private static async Task<(int Code, string Stdout)> Health(FleetConfig config, ISshClient ssh, bool json, JobNeeds? needs)
    {
        var o = new StringWriter();
        var code = await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None, json, needs: needs);
        return (code, o.ToString());
    }

    [Fact]
    public void HealthTakesNeedsAndMem()
    {
        var p = ParsedArgs.Parse(["health", "--needs", "dotnet, gpu", "--mem", "4", "--json"]);
        Assert.Equal(["dotnet", "gpu"], p.Needs);
        Assert.Equal(4, p.MemGb);
        Assert.True(p.Json);
        Assert.Equal(2.5, ParsedArgs.Parse(["health", "--mem", "2.5"]).MemGb);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--mem", "0"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--needs", " , "])); // --needs with no capability at all
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--dir", "x"]));
    }

    [Fact]
    public async Task HealthNeedsJsonMarksEachMachineEligibleOrWhy()
    {
        var (config, ssh) = Fleet();
        var (code, stdout) = await Health(config, ssh, json: true, new JobNeeds(["node"], null, false));

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var rows = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(2, rows.GetArrayLength());

        var ok = rows[0];
        Assert.Equal("build-01", ok.GetProperty("name").GetString());
        Assert.True(ok.GetProperty("eligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("why").ValueKind);
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("reason").ValueKind);
        Assert.False(ok.GetProperty("permanent").GetBoolean());
        Assert.Equal(2, ok.GetProperty("jobSlots").GetInt32());
        Assert.Equal("ok", ok.GetProperty("state").GetString()); // the plain row is all still there

        var no = rows[1];
        Assert.False(no.GetProperty("eligible").GetBoolean());
        Assert.Equal("missing_capability", no.GetProperty("reason").GetString());
        Assert.Equal("lacks node (capabilities: dotnet)", no.GetProperty("why").GetString());
        Assert.True(no.GetProperty("permanent").GetBoolean());
        Assert.Equal(0, no.GetProperty("jobSlots").GetInt32());
    }

    [Fact]
    public async Task HealthWithoutNeedsPrintsExactlyTheOldRows()
    {
        var (config, ssh) = Fleet();
        var (plainCode, plainJson) = await Health(config, ssh, json: true, null);
        var (_, plainText) = await Health(config, ssh, json: false, null);
        var (_, needsText) = await Health(config, ssh, json: false, new JobNeeds(["dotnet"], null, false));

        Assert.Equal(0, plainCode);
        using var doc = JsonDocument.Parse(plainJson);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(
                ["name", "state", "reasons", "load", "diskFreeGb", "memFreeGb", "slotsUsed", "maxJobs", "localTime", "cores", "memTotalGb", "freeSlots", "size", "suitedFor"],
                row.EnumerateObject().Select(p => p.Name));
        }

        Assert.DoesNotContain("->", plainText, StringComparison.Ordinal);
        Assert.DoesNotContain("eligible", plainText, StringComparison.Ordinal);

        // --needs only appends to each line: everything before the arrow is the plain line, byte for byte.
        var plain = plainText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var withNeeds = needsText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(plain.Length, withNeeds.Length);
        for (var i = 0; i < plain.Length; i++)
        {
            Assert.StartsWith(plain[i].TrimEnd('\r') + "  -> ", withNeeds[i], StringComparison.Ordinal);
        }
    }

    public static TheoryData<string[], double?> NeedsCases => new()
    {
        { ["dotnet"], null },
        { ["node"], null },
        { ["dotnet"], 4 },
        { ["dotnet"], 12 },
        { ["dotnet"], 32 },
        { [], 2 },
        { ["gpu"], null },
    };

    [Theory]
    [MemberData(nameof(NeedsCases))]
    public async Task HealthNeedsMatchesSchedulerAssessForTheSameProbes(string[] caps, double? mem)
    {
        foreach (var down in new[] { false, true })
        {
            var (config, ssh) = Fleet(down);
            var needs = new JobNeeds(caps, mem, false);
            var probe = new HealthProbe(ssh, TimeProvider.System);
            var probes = await Task.WhenAll(config.Machines.Select(m => probe.ProbeAsync(m, config.Policy, CancellationToken.None)));
            var expected = Scheduler.Assess(config.Machines.Zip(probes), needs);

            var (code, stdout) = await Health(config, ssh, json: true, needs);

            using var doc = JsonDocument.Parse(stdout);
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(expected.Count, rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.Equal(expected[i].Machine.Name, rows[i].GetProperty("name").GetString());
                Assert.Equal(expected[i].Eligible, rows[i].GetProperty("eligible").GetBoolean());
                Assert.Equal(expected[i].Code, rows[i].GetProperty("reason").GetString());
                Assert.Equal(expected[i].Why is { } why ? JobRunner.SafeLine(why) : null, rows[i].GetProperty("why").GetString());
                Assert.Equal(expected[i].Permanent, rows[i].GetProperty("permanent").GetBoolean());
                Assert.Equal(expected[i].JobSlots, rows[i].GetProperty("jobSlots").GetInt32());
            }

            Assert.Equal(expected.Any(a => a.Eligible) ? 0 : 75, code);
        }
    }

    /// <summary>jobSlots is what --shards places on the machine: lanq run --shards auto picks their sum.</summary>
    [Fact]
    public async Task HealthNeedsMemJobSlotsMatchShardPlacement()
    {
        var config = ConfigLoader.Parse(ShardTests.PoolYaml);
        var ssh = new PoolSsh { ProbeExtra = m => m.Name == "big-01" ? "mem_free_gb=10\nmem_total_gb=32" : "mem_free_gb=5\nmem_total_gb=8" };
        var (code, stdout) = await Health(config, ssh, json: true, new JobNeeds(["node"], 4, false));

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var slots = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("jobSlots").GetInt32());
        Assert.Equal(1, slots["tiny-01"]); // max_jobs 1, 5 GB free covers one --mem 4
        Assert.Equal(2, slots["big-01"]); // max_jobs 4, but 10 GB free covers only two

        var o = new StringWriter();
        var run = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "node", "--mem", "4", "--shards", "auto", "--", "npx", "jest", "--shard={shard}/{shards}"]),
            config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir,
            o, new StringWriter(), CancellationToken.None);
        Assert.Equal(0, run);
        using var shards = JsonDocument.Parse(o.ToString());
        Assert.Equal(slots.Values.Sum(), shards.RootElement.GetProperty("shardCount").GetInt32());
    }

    [Fact]
    public async Task HealthNeedsExits75WhenNothingIsEligible()
    {
        var (config, ssh) = Fleet();
        Assert.Equal(75, (await Health(config, ssh, json: true, new JobNeeds(["gpu"], null, false))).Code);
        Assert.Equal(75, (await Health(config, ssh, json: false, new JobNeeds(["dotnet"], 64, false))).Code);
        Assert.Equal(0, (await Health(config, ssh, json: false, new JobNeeds(["dotnet"], 4, false))).Code);

        // Plain health stays 0 whatever the machines look like.
        var (downConfig, downSsh) = Fleet(down: true);
        Assert.Equal(0, (await Health(downConfig, downSsh, json: false, null)).Code);
    }

    [Fact]
    public async Task HealthNeedsTextAppendsEligibility()
    {
        var (config, ssh) = Fleet();
        var (code, stdout) = await Health(config, ssh, json: false, new JobNeeds(["node"], null, false));

        Assert.Equal(0, code);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("build-01", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("  -> eligible (2 slots)", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("build-02", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("  -> not eligible: lacks node (capabilities: dotnet)", lines[1], StringComparison.Ordinal);

        var (_, one) = await Health(config, ssh, json: false, new JobNeeds(["dotnet"], 6, false));
        Assert.EndsWith("  -> eligible (1 slot)", one.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("health", "--needs", "dotnet", "--mem", "0", "--json")]
    [InlineData("health", "--needs", "dotnet", "--json", "--dir", "x")]
    [InlineData("health", "--json", "--needs")]
    public async Task HealthNeedsBadArgumentStillPrintsOneJsonObject(params string[] args)
    {
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(args, o, new StringWriter(), CancellationToken.None));
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("exitCode").GetInt32());
    }
}
