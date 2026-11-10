using LanQueue.Jobs;
using LanQueue.Plan;
using Xunit;

namespace LanQueue.Tests;

/// <summary>--only selection, cycles and the launch order of a plan file's jobs (pure: no network, the job log only read).</summary>
public sealed class PlanGraphTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 22, 0, 0, TimeSpan.Zero);

    private static PlanFile Example => PlanFileLoader.Parse(PlanFileLoaderTests.Example);

    [Fact]
    public void OnlySelectsAndDropsEdgesToUnselected()
    {
        var all = PlanGraph.Select(Example, null);
        Assert.Equal(["build", "unit", "integration", "serial"], all.Jobs.Select(j => j.Name));
        Assert.Equal(["integration"], all.After["serial"]);
        Assert.Empty(all.Notes);

        // In file order whatever order --only names them, matched ignoring case, file spelling kept.
        var some = PlanGraph.Select(Example, ["SERIAL", "build"]);
        Assert.Equal(["build", "serial"], some.Jobs.Select(j => j.Name));
        Assert.Empty(some.After["serial"]);
        Assert.Equal(["--only: serial starts without waiting for integration (not selected)"], some.Notes);
        Assert.Equal(["integration"], some.Jobs[1].After); // the job itself is unchanged; only the selection drops the edge

        var both = PlanGraph.Select(Example, ["integration", "serial"]);
        Assert.Equal(["integration"], both.After["serial"]);
        Assert.Empty(both.Notes);
    }

    [Fact]
    public void OnlyUnknownJobIsRefused()
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanGraph.Select(Example, ["build", "lint", "e2e"]));
        Assert.Equal("--only names a job that is not in lanq.yml: lint, e2e. Jobs: build, unit, integration, serial.", ex.Message);
    }

    [Fact]
    public void CyclesAreFoundAndReported()
    {
        Assert.Null(PlanGraph.FindCycle([("a", []), ("b", ["a"]), ("c", ["a", "b"])]));
        Assert.Equal(["a", "b", "a"], PlanGraph.FindCycle([("a", ["b"]), ("b", ["a"])]));
        Assert.Equal(["b", "c", "d", "b"], PlanGraph.FindCycle([("a", ["b"]), ("b", ["c"]), ("c", ["d"]), ("d", ["b"])]));
        Assert.Equal(["a", "a"], PlanGraph.FindCycle([("a", ["a"])]));
    }

    /// <summary>
    /// Ready jobs with history go first, the longest expected work first; jobs without history follow in file order; a job never comes before
    /// what it waits for.
    /// </summary>
    [Fact]
    public void LongestFirstFromHistoryThenFileOrder()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            jobs:
              lint:
                command: npm test
              build:
                command: dotnet build
              unit:
                command: [npx, jest, "--shard={shard}/{shards}"]
                shards: 2
              integration:
                command: dotnet test
                dir: tests
              serial:
                command: dotnet test
                after: [build]
              docs:
                command: npm test
                dir: docs
            """);
        var selection = PlanGraph.Select(plan, null);

        // No history at all: file order (serial after build).
        Assert.Equal(["lint", "build", "unit", "integration", "serial", "docs"], PlanGraph.Order(selection, _ => null).Select(j => j.Name));

        // History from jobs.jsonl: build ran 600 s, unit 2 shards of 300 s + 400 s, integration 1800 s, serial 7200 s (but it waits for build).
        var records = new List<JobRecord>
        {
            Record("j1", "build-01", "dotnet build", ".", 600),
            Record("j2", "micro-01", JobHistory.Key(["npx", "jest", "--shard={shard}/{shards}"]), ".", 300, run: "r1", shard: 1, shards: 2),
            Record("j3", "micro-02", JobHistory.Key(["npx", "jest", "--shard={shard}/{shards}"]), ".", 400, run: "r1", shard: 2, shards: 2),
            Record("j4", "big-01", "dotnet test", "tests", 1800),
            Record("j5", "big-01", "dotnet test", ".", 7200),
            Record("j6", "big-01", "npm test", "docs", 50, status: "Failed"), // only completed runs count
        };
        var expected = PlanGraph.ExpectedFromHistory(records, Now);
        Assert.Equal(600, expected(plan.Job("build")!));
        Assert.Equal(700, expected(plan.Job("unit")!));
        Assert.Null(expected(plan.Job("docs")!));
        Assert.Null(expected(plan.Job("lint")!));

        Assert.Equal(["integration", "unit", "build", "serial", "lint", "docs"], PlanGraph.Order(selection, expected).Select(j => j.Name));

        // --only: the order of what is selected, by the same rule.
        Assert.Equal(["serial", "lint"], PlanGraph.Order(PlanGraph.Select(plan, ["lint", "serial"]), expected).Select(j => j.Name));
    }

    [Fact]
    public void ExpectedFromAJobLogThatCannotBeReadIsNoHistory()
    {
        var home = Path.Combine(Path.GetTempPath(), "lanq-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expected = PlanGraph.ExpectedFromHistory(new JobLog(home), Now);
            Assert.Null(expected(Example.Jobs[0]));
            Assert.False(Directory.Exists(Path.Combine(home, "logs"))); // read only: nothing created
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    private static JobRecord Record(string id, string machine, string key, string dir, double runSeconds, string status = "Completed", string? run = null, int? shard = null, int? shards = null) =>
        new(id, machine, key, Now.AddDays(-1), runSeconds + 5, status == "Completed" ? 0 : 1, status)
        {
            V = 2,
            Key = key,
            Dir = dir,
            RunId = run,
            Shard = shard,
            Shards = shards,
            RunSeconds = runSeconds,
        };
}
