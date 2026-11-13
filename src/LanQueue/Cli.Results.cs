using System.Globalization;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Results;

namespace LanQueue;

/// <summary>lanq run --results / lanq.yml results: (0.11.0): the results object in --json and the "no tests ran" warning.</summary>
public static partial class Cli
{
    /// <summary>The most failed tests a results object lists (failedTestsMore counts the rest).</summary>
    internal const int MaxFailedTestsListed = 50;

    /// <summary>
    /// The results object of a run, shard or plan job (sanitised), or null (the key left out) when results were not asked for or no fetch
    /// report exists (the job never ran).
    /// </summary>
    internal static TestResultsJson? ResultsJsonOf(TestResults? r) => r is null ? null : new TestResultsJson(
        r.Format == ResultsFormat.Trx ? "trx" : "junit",
        JobRunner.SafeLine(r.Path),
        r.Files,
        r.FilesRead,
        r.Total,
        r.Executed,
        r.Passed,
        r.Failed,
        r.Skipped,
        r.NoTests,
        [.. r.Warnings.Select(JobRunner.SafeLine)],
        [.. r.FailedTests.Take(MaxFailedTestsListed).Select(f => new FailedTestJson(TestResultsParser.SafeName(f.Name)) { DurationSeconds = f.DurationSeconds, Shard = f.Shard })])
    {
        DurationSeconds = r.DurationSeconds is { } d ? Math.Round(d, 3) : null,
        NoTestsReason = r.NoTestsReason,
        FailedTestsMore = r.FailedKnown > MaxFailedTestsListed ? r.FailedKnown - MaxFailedTestsListed : null,
        Truncated = Flag(r.Truncated),
        ShardsWithNoTests = r.ShardsWithNoTests.Count > 0 ? r.ShardsWithNoTests : null,
    };

    /// <summary>
    /// The results of a job that completed and executed no test (a merged shard run is judged as a whole), or null. Only then lanq warns and
    /// adds the ask_developer step; never for another status, and the status never changes.
    /// </summary>
    internal static TestResults? NoTestsRan(string status, TestResults? r) => status == nameof(JobStatus.Completed) && r is { NoTests: true } ? r : null;

    /// <summary>"[lanq] warning: no tests ran in job unit: 0 executed in 1 results file(s) (results path TestResults/*.trx)", sanitised.</summary>
    internal static string NoTestsLine(string? job, TestResults r) => JobRunner.SafeLine(string.Create(
        CultureInfo.InvariantCulture,
        $"[lanq] warning: no tests ran{(job is null ? "" : " in job " + job)}: 0 executed in {r.Files} results file(s) (results path {r.Path})"));

    /// <summary>A sharded job's results: its shards' merged (<see cref="TestResultsParser.Merge"/>), or null when no shard has any.</summary>
    internal static TestResults? MergedResults(ShardRunResult r) => TestResultsParser.Merge([.. r.Shards.Select(s => (s.Index, s.Results))]);

    /// <summary>A plan job's results: the single job's, or its shards' merged; null without any.</summary>
    internal static TestResults? PlanJobResults(PlanJobOutcome j) => j.Sharded is { } s ? MergedResults(s) : j.Single?.Results;

    /// <summary>The plan's resultsTotal: the sums over the jobs with results, and the completed jobs that ran no test; null when no job has results.</summary>
    internal static ResultsTotalJson? ResultsTotalOf(PlanRunResult result)
    {
        var jobs = result.Jobs.Select(j => (Job: j, Results: PlanJobResults(j))).Where(x => x.Results is not null).ToList();
        return jobs.Count == 0 ? null : new ResultsTotalJson(
            jobs.Count,
            jobs.Sum(x => x.Results!.Total),
            jobs.Sum(x => x.Results!.Executed),
            jobs.Sum(x => x.Results!.Passed),
            jobs.Sum(x => x.Results!.Failed),
            jobs.Sum(x => x.Results!.Skipped),
            [.. jobs.Where(x => NoTestsRan(x.Job.Status, x.Results) is not null).Select(x => x.Job.Name)]);
    }

    /// <summary>The steps of a run whose job completed but ran no test: the ask_developer step added (see <see cref="NextSteps.WithNoTests"/>).</summary>
    internal static List<NextStepJson> WithNoTestsStep(List<NextStepJson> steps, IReadOnlyList<string> jobs) =>
        jobs.Count == 0 ? steps : NextSteps.WithNoTests(steps, jobs.Count == 1 ? $"job {jobs[0]}" : "jobs " + string.Join(", ", jobs));
}
