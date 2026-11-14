using System.Globalization;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Makeready;
using LanQueue.Output;
using LanQueue.Ssh;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>
    /// lanq makeready --tune (0.12.0): runs the read-only lanq-makeready-tune scan on the worker, the same way as the scanner (the script on
    /// ssh stdin, run from memory, nothing written to its disk), and reads its report as untrusted input (<see cref="TuneReport.Parse"/>).
    /// A configured machine gets -Hours (<see cref="ScanHours"/>) and -WorkRoot (<see cref="TuneWorkRoot"/>); an ad-hoc --ssh PC neither.
    /// Never throws for a worker problem and never changes the exit code: every outcome is a <see cref="MakereadyTuneJson.Status"/>.
    /// lanq never embeds, sends or runs lanq-makeready-fix.
    /// </summary>
    /// <param name="again">The suggested lanq makeready ... --tune for a failed tune scan (never safe unattended).</param>
    private static async Task<MakereadyTuneJson> TuneAsync(Machine machine, bool configured, SuggestedCommand? again, ISshClient ssh, CancellationToken ct)
    {
        var name = configured ? machine.Name : machine.Ssh;
        MakereadyTuneJson Failed(string status, string error) =>
            new(status, JobRunner.SafeLine(error), null, Steps(NextSteps.ForTune(status, 0, name, again)));

        SshResult r;
        try
        {
            var command = TuneCommand(machine.Os, configured ? ScanHours(machine) : null, configured ? TuneWorkRoot(machine) : null);
            r = await ssh.RunWithInputAsync(machine, command, TuneScript(machine.Os), TuneReport.MaxChars, TuneTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Failed("Unusable", ex.Message);
        }

        if (r.TimedOut)
        {
            return Failed("TimedOut", "timed out after " + ProcessSshClient.Describe(TuneTimeout));
        }

        if (r.ExitCode == 255)
        {
            return Failed("Unreachable", "ssh failed: " + SshErrors.Explain(r.ErrorOutput, machine.Ssh));
        }

        try
        {
            var report = (r.OutputTruncated ? throw new FormatException("report larger than 256 KB") : TuneReport.Parse(r.Output)).ForJson(machine.Os);
            var fixable = report.Items!.Count(i => i.FixId is not null);
            return new MakereadyTuneJson("Ok", null, report, Steps(NextSteps.ForTune("Ok", fixable, name, again)));
        }
        catch (FormatException ex)
        {
            // The reason is lanq's own words; nothing the worker sent (stdout or stderr) is echoed.
            return Failed("Unusable", $"unusable lanq-makeready-tune report ({ex.Message}; script exit {r.ExitCode.ToString(CultureInfo.InvariantCulture)})");
        }

        static List<TuneNextStepJson> Steps(List<NextStepJson> steps) =>
            [.. steps.Select(s => new TuneNextStepJson(s.Action, s.Reason, s.Command, s.Argv, s.SafeToRunUnattended))];
    }

    /// <summary>The tune section of lanq makeready --tune's text, after the report: the counts, WARN then INFO items with advice and fix ids, SKIP as a count.</summary>
    private static IEnumerable<string> TuneText(MakereadyTuneJson tune, MachineOs os)
    {
        yield return "";
        if (tune.Report is not { } report)
        {
            yield return $"Tune: {tune.Status}: {tune.Error} (the readiness result above stands)";
            yield break;
        }

        var s = report.Summary!;
        yield return $"Tune (lanq-makeready-tune {report.Tool?.Version ?? "?"}, read-only; nothing here changes whether the PC is worker-ready)";
        yield return $"  {Count(s.Warn)} WARN, {Count(s.Info)} INFO, {Count(s.Ok)} OK, {Count(s.Skip)} SKIP (not shown)";
        foreach (var i in report.Items!.Where(i => i.Status == "WARN").Concat(report.Items!.Where(i => i.Status == "INFO")))
        {
            yield return $"  [{i.Status,-4}] {i.Title}: {i.Detail}{(i.Security ? " (security)" : "")}";
            if (!string.IsNullOrWhiteSpace(i.Advice))
            {
                yield return $"         advice: {i.Advice}";
            }

            if (i.FixId is { } fix)
            {
                yield return $"         fix id: {fix}";
            }
        }

        if (report.Items!.Any(i => i.FixId is not null))
        {
            yield return "";
            yield return TuneFixLine(os);
        }

        static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The closing line of the tune text when an item has a fix id: the fix script is the PC owner's, run at that PC.</summary>
    internal static string TuneFixLine(MachineOs os) =>
        "Its owner can preview the tune fixes at that PC: "
        + (os == MachineOs.Windows ? "lanq-makeready-fix.ps1 -Tune -WhatIf" : "sudo sh lanq-makeready-fix.sh --tune --dry-run")
        + "; local and interactive only. lanq never runs it.";

    /// <summary>The --all --tune table's TUNE cell: "2 WARN 3 INFO", or the status of a tune scan that failed, or "-" without one.</summary>
    private static string TuneCell(MakereadyTuneJson? tune) => tune switch
    {
        null => "-",
        { Report.Summary: { } s } => $"{s.Warn.ToString(CultureInfo.InvariantCulture)} WARN {s.Info.ToString(CultureInfo.InvariantCulture)} INFO",
        _ => tune.Status switch
        {
            "TimedOut" => "timed out",
            "Unreachable" => "unreachable",
            _ => "unusable",
        },
    };
}
