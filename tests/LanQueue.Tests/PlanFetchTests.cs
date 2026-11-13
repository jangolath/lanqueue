using LanQueue.Fetch;
using LanQueue.Plan;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// Plan fetch limits and folder (0.10.0): lanq.yml fetch_max / fetch_max_files may only lower the defaults and there is no fetch_dir key; the
/// developer's --fetch-dir / --fetch-max / --fetch-max-files with --plan apply to every fetching job (Plan/PlanFetch.cs).
/// </summary>
public sealed partial class PlanFileRunTests
{
    /// <summary>unit and e2e fetch their results; lint fetches nothing.</summary>
    private const string Fetching = """
        version: 1
        defaults:
          needs: [node]
        jobs:
          unit:
            command: npm run unit
            fetch: "TestResults/*.trx"
            fetch_max: 50M
          e2e:
            command: npm run e2e
            fetch: [report/**]
          lint:
            command: npm run lint
        """;

    private static string Job(string keys) => $"version: 1\njobs:\n  unit:\n    command: [dotnet, test]\n    {keys}\n";

    [Fact]
    public void FetchMaxKeysExpandToFlags()
    {
        var job = PlanFileLoader.Parse(Job("fetch: \"*.trx\"\n    fetch_max: 50M\n    fetch_max_files: 20")).Jobs[0];
        var typed = ParsedArgs.Parse(["run", "--fetch", "*.trx", "--fetch-max", "50M", "--fetch-max-files", "20", "--", "dotnet", "test"]);

        Assert.Equal(["run", "--fetch", "*.trx", "--fetch-max", "50M", "--fetch-max-files", "20", "--", "dotnet", "test"], job.RunArgs);
        Assert.Equal((typed.FetchMaxBytes, typed.FetchMaxFiles), (job.Args.FetchMaxBytes, job.Args.FetchMaxFiles));

        // defaults: applies to the jobs that fetch only; a job's own limit without fetch: is refused with the CLI's own words.
        var plan = PlanFileLoader.Parse("version: 1\ndefaults:\n  fetch_max: 100M\n  fetch_max_files: 10\njobs:\n  unit:\n    command: dotnet test\n    fetch: \"*.trx\"\n  lint:\n    command: dotnet build\n");
        Assert.Equal(["run", "--fetch", "*.trx", "--fetch-max", "100M", "--fetch-max-files", "10", "--", "dotnet", "test"], plan.Job("unit")!.RunArgs);
        Assert.Equal(["run", "--", "dotnet", "build"], plan.Job("lint")!.RunArgs);
        var alone = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(Job("fetch_max: 50M")));
        Assert.Equal("lanq.yml: job 'unit' (line 3): --fetch-dir, --fetch-max and --fetch-max-files need at least one --fetch pattern.", alone.Message);
        Assert.Contains("--fetch-max must be a size from 1K to 2G", Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(Job("fetch: x\n    fetch_max: lots"))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fetch: x\n    fetch_max: 201M")]
    [InlineData("fetch: x\n    fetch_max: 1G")]
    [InlineData("fetch: x\n    fetch_max_files: 501")]
    public void FileValueAboveDefaultRefused(string keys)
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(Job(keys)));

        Assert.Equal("lanq.yml: job 'unit' (line 3): lanq.yml may lower fetch limits (at most 200M / 500 files); a larger limit is the developer's choice: lanq run --plan --fetch-max 1G", ex.Message);

        // The defaults themselves, too, and even when no job fetches.
        var inDefaults = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse("version: 1\ndefaults:\n  fetch_max: 2G\njobs:\n  lint:\n    command: dotnet build\n"));
        Assert.Equal("lanq.yml: defaults: fetch_max (line 3): " + PlanFetch.RaiseRefused, inDefaults.Message);

        // At the defaults is fine.
        Assert.Equal(200L * 1024 * 1024, PlanFileLoader.Parse(Job("fetch: x\n    fetch_max: 200M\n    fetch_max_files: 500")).Jobs[0].Args.FetchMaxBytes);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CliRaisesUpToCeilingAndOverridesFile()
    {
        Plan(Fetching);

        var (code, stdout, _) = await Run(new PoolSsh(), "--plan", "--dry-run", "--fetch-max", "1G", "--fetch-max-files", "2000", "--json");

        Assert.Equal(0, code);
        var jobs = Json(stdout).GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j.GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()!).ToList());
        Assert.Equal(["lanq", "run", "--needs", "node", "--fetch", "TestResults/*.trx", "--fetch-max", "1G", "--fetch-max-files", "2000", "--", "npm", "run", "unit"], jobs["unit"]);
        Assert.Equal(["lanq", "run", "--needs", "node", "--fetch", "report/**", "--fetch-max", "1G", "--fetch-max-files", "2000", "--", "npm", "run", "e2e"], jobs["e2e"]);
        Assert.Equal(["lanq", "run", "--needs", "node", "--", "npm", "run", "lint"], jobs["lint"]);

        // The request each job makes carries the limits; above the 2 GB ceiling the command line itself is refused.
        var unit = PlanFetch.Apply(PlanFileLoader.Load(Path.Combine(Repo, "lanq.yml")).Job("unit")!, null, 2L * 1024 * 1024 * 1024, 5000);
        var request = Cli.PlanJobRequest(unit, ParsedArgs.Parse(["run", "--plan"]), Repo);
        Assert.Equal((5000, FetchLimits.CeilingBytes), (request.Fetch!.Limits.MaxFiles, request.Fetch.Limits.MaxTotalBytes));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--fetch-max", "3G"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--fetch-max-files", "5001"]));
        Assert.Equal("2G", PlanFetch.SizeText(FetchLimits.CeilingBytes));
        Assert.Equal("1536K", PlanFetch.SizeText(1536L * 1024));
    }

    [Theory]
    [InlineData("version: 1\nfetch_dir: out\njobs:\n  unit:\n    command: dotnet test\n", "lanq.yml: fetch_dir is not a lanq.yml key (line 2)")]
    [InlineData("version: 1\ndefaults:\n  fetch_dir: out\njobs:\n  unit:\n    command: dotnet test\n", "lanq.yml: defaults: fetch_dir is not a lanq.yml key (line 3)")]
    [InlineData("version: 1\njobs:\n  unit:\n    command: dotnet test\n    fetch: x\n    fetch_dir: /tmp/out\n", "lanq.yml: job 'unit' (line 3): fetch_dir is not a lanq.yml key (line 6)")]
    public void FetchDirKeyRefusedWithHint(string yaml, string start)
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(yaml));

        Assert.StartsWith(start, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("never the repository's; pass it on the command line: lanq run --plan --fetch-dir DIR", ex.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CliFetchDirAppliesToEveryJob()
    {
        Plan(Fetching);
        var outDir = Path.Combine(_root, "fetched");

        var (code, stdout, _) = await Run(new PoolSsh(), "--plan", "--dry-run", "--fetch-dir", outDir, "--json");

        Assert.Equal(0, code);
        var jobs = Json(stdout).GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j.GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()!).ToList());
        Assert.Equal(["lanq", "run", "--needs", "node", "--fetch", "TestResults/*.trx", "--fetch-max", "50M", "--fetch-dir", outDir, "--", "npm", "run", "unit"], jobs["unit"]);
        Assert.Contains(outDir, jobs["e2e"]);
        Assert.DoesNotContain("--fetch-dir", jobs["lint"]);
        Assert.False(Directory.Exists(outDir)); // a dry run creates no folder

        var plan = PlanFileLoader.Load(Path.Combine(Repo, "lanq.yml"));
        foreach (var name in new[] { "unit", "e2e" })
        {
            var request = Cli.PlanJobRequest(PlanFetch.Apply(plan.Job(name)!, outDir, null, null), ParsedArgs.Parse(["run", "--plan"]), Repo);
            Assert.Equal((outDir, outDir), (request.Fetch!.OutputRoot, request.Fetch.Anchor));
        }

        Assert.Equal(50L * 1024 * 1024, Cli.PlanJobRequest(PlanFetch.Apply(plan.Job("unit")!, outDir, null, null), ParsedArgs.Parse(["run", "--plan"]), Repo).Fetch!.Limits.MaxTotalBytes);
        Assert.Same(plan.Job("lint"), PlanFetch.Apply(plan.Job("lint")!, outDir, FetchLimits.CeilingBytes, 10));
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("--fetch-max", "1G")]
    [InlineData("--fetch-max-files", "10")]
    [InlineData("--fetch-dir", "out")]
    public async Task FetchFlagsWithoutFetchingJobsRefused(string option, string value)
    {
        Plan(Fetching);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");

        var (code, stdout, stderr) = await Run(ssh, payloads, ["--plan", "--only", "lint", option, value, "--json"]);

        Assert.Equal(2, code);
        var json = Json(stdout);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
        Assert.Equal(PlanFetch.NoFetchingJob, json.GetProperty("message").GetString());
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
        Assert.Equal("[lanq] Rejected: " + PlanFetch.NoFetchingJob, Lines(stderr)[^1]);
    }

    [Fact]
    public void FetchOptionsAreTakenWithPlanOnly()
    {
        var p = ParsedArgs.Parse(["run", "--plan", "--fetch-dir", "out", "--fetch-max", "1G", "--fetch-max-files", "2000"]);

        Assert.Equal(("out", 1024L * 1024 * 1024, 2000), (p.FetchDir, p.FetchMaxBytes!.Value, p.FetchMaxFiles!.Value));

        // Without --plan they still need a --fetch pattern, and --fetch itself still comes from lanq.yml with --plan.
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--fetch-max", "1G", "--", "npm", "test"]));
        Assert.StartsWith("with --plan each job's --fetch comes from lanq.yml", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--fetch", "x"])).Message, StringComparison.Ordinal);
    }
}
