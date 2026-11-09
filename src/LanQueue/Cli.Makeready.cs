using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Makeready;
using LanQueue.Ssh;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>The lanq-makeready release embedded in this build (see scripts/sync-makeready.sh).</summary>
    internal const string MakereadyVersion = "0.1.0";

    internal static readonly TimeSpan MakereadyTimeout = TimeSpan.FromSeconds(120);

    internal const string WorkerUntouched = "lanq never changes a worker. Fixes are for the PC's owner to run on that PC.";

    /// <summary>Capabilities lanq-makeready can detect; any other configured name is reported as not checked.</summary>
    private static readonly string[] MakereadyKnownCapabilities = ["dotnet", "node", "python", "java", "go", "rust", "docker"];

    private static readonly string[] OsTags = ["windows", "linux"];

    [GeneratedRegex("^[a-z0-9][a-z0-9_.+-]{0,39}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CapabilityNameRegex();

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]-([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HoursRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MachineNameRegex();

    /// <summary>The embedded script, byte for byte as released (pinned by a test).</summary>
    internal static byte[] MakereadyScript(MachineOs os)
    {
        using var stream = typeof(Cli).Assembly.GetManifestResourceStream(os == MachineOs.Windows ? "lanq-makeready.ps1" : "lanq-makeready.sh")
            ?? throw new InvalidOperationException("The embedded lanq-makeready script is missing from this build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The script arrives on stdin and runs from memory; it prints only the JSON report (redacted) on stdout.</summary>
    internal static string MakereadyCommand(MachineOs os) => os == MachineOs.Windows
        ? "powershell -NoProfile -NonInteractive -Command \"& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Json - -Redact\""
        : "sh -s -- --json - --redact";

    /// <summary>
    /// lanq makeready &lt;machine&gt; | --ssh user@host --os windows|linux: runs the read-only lanq-makeready check on a worker and reads its
    /// report. Exit 0 worker-ready, 1 not ready, 2 bad arguments, 70 ssh or an unusable report, 75 machine paused or outside its allowed
    /// hours (unless --force).
    /// </summary>
    internal static async Task<int> Makeready(
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        Machine machine;
        Machine? configured = null;
        if ((p.Target is null) == (p.SshTarget is null) || (p.SshTarget is null) != (p.Os is null))
        {
            await stderr.WriteLineAsync("Usage: lanq makeready <machine> | lanq makeready --ssh user@host --os windows|linux [--json] [--force]").ConfigureAwait(false);
            return 2;
        }

        if (p.SshTarget is { } target)
        {
            if (!ConfigLoader.IsValidSshTarget(target))
            {
                await stderr.WriteLineAsync("--ssh must be user@host (no leading '-', no spaces).").ConfigureAwait(false);
                return 2;
            }

            machine = new Machine { Name = target, Os = p.Os!.Value, Ssh = target, WorkRoot = p.Os == MachineOs.Windows ? "C:/lq" : "/srv/lq" };
        }
        else
        {
            FleetConfig config;
            try
            {
                config = loadConfig();
            }
            catch (Exception ex) when (ex is ConfigException or IOException)
            {
                await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 2;
            }

            if (FindMachine(config, p.Target, "makeready", stderr) is not { } m)
            {
                return 2;
            }

            machine = configured = m;
            if (!p.Force)
            {
                // A scan is light, but a family PC's PAUSE file and hours are respected by default.
                var h = await probe.ProbeAsync(m, config.Policy, ct).ConfigureAwait(false);
                if (!h.Reachable)
                {
                    await stderr.WriteLineAsync($"{m.Name}: unreachable: {h.Error}").ConfigureAwait(false);
                    return 70;
                }

                var held = h.Paused ? "paused (a PAUSE file is in its work_root)"
                    : m.AllowedHours is { Count: > 0 } && h.Hold == "outside allowed hours" ? "outside allowed hours"
                    : null;
                if (held is not null)
                {
                    await stderr.WriteLineAsync($"{m.Name} is {held}; not scanning it now. Add --force to scan anyway.").ConfigureAwait(false);
                    return 75;
                }
            }
        }

        SshResult r;
        try
        {
            r = await ssh.RunWithInputAsync(machine, MakereadyCommand(machine.Os), MakereadyScript(machine.Os), MakereadyReport.MaxChars, MakereadyTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 70;
        }

        var name = configured?.Name ?? machine.Ssh;
        if (r.TimedOut || r.ExitCode == 255)
        {
            var why = r.TimedOut ? "timed out after " + ProcessSshClient.Describe(MakereadyTimeout) : "ssh failed: " + FirstLine(r.ErrorOutput);
            await stderr.WriteLineAsync(JobRunner.SafeLine($"{name}: {why}")).ConfigureAwait(false);
            return 70;
        }

        MakereadyReport report;
        try
        {
            report = r.OutputTruncated ? throw new FormatException("report larger than 1 MB") : MakereadyReport.Parse(r.Output);
        }
        catch (FormatException ex)
        {
            var detail = FirstLine(r.ErrorOutput);
            await stderr.WriteLineAsync(JobRunner.SafeLine(
                $"{name}: unusable lanq-makeready report ({ex.Message}; script exit {r.ExitCode.ToString(CultureInfo.InvariantCulture)})"
                + (detail.Length > 0 ? ": " + detail : ""))).ConfigureAwait(false);
            return 70;
        }

        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, MakereadyJson.Default.MakereadyReport)).ConfigureAwait(false);
        }
        else
        {
            foreach (var line in MakereadyText(report, name, configured, p.SshTarget))
            {
                await stdout.WriteLineAsync(JobRunner.SafeLine(line)).ConfigureAwait(false);
            }
        }

        return report.WorkerReady ? 0 : 1;
    }

    /// <summary>The human report. Every string from the worker is sanitised by the caller, line by line; nothing from it is executed.</summary>
    private static IEnumerable<string> MakereadyText(MakereadyReport report, string name, Machine? configured, string? sshTarget)
    {
        var v = report.Verdicts!;
        yield return $"lanq makeready {name} (lanq-makeready {report.Tool?.Version ?? "?"}, read-only; host {report.Host?.Os} {report.Host?.Arch})";
        yield return "";
        yield return "Verdicts";
        foreach (var (key, value) in new[] { ("worker_git", v.WorkerGit), ("worker_archive", v.WorkerArchive), ("dotnet10", v.Dotnet10), ("docker", v.Docker), ("overnight", v.Overnight) })
        {
            yield return $"  {key,-15} {value}";
        }

        var problems = report.Checks!.Where(c => c.Status is "WARN" or "FAIL").ToList();
        if (problems.Count > 0)
        {
            yield return "";
            yield return "Checks to look at";
            foreach (var c in problems)
            {
                yield return $"  [{c.Status,-4}] {c.Title}: {c.Detail}";
                if (!string.IsNullOrWhiteSpace(c.Advice))
                {
                    yield return $"         advice: {c.Advice}";
                }
            }
        }

        var detected = report.Capabilities!.Where(c => CapabilityNameRegex().IsMatch(c) && !OsTags.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        if (configured is not null)
        {
            var mine = configured.Capabilities.Select(c => c.ToLowerInvariant()).Where(c => !OsTags.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
            yield return "";
            yield return "Capabilities (machines.yml vs detected and working)";
            yield return "  detected but not configured: " + List(detected.Except(mine));
            yield return "  configured but not detected: " + List(mine.Where(c => MakereadyKnownCapabilities.Contains(c)).Except(detected));
            yield return "  configured, not checked by lanq-makeready: " + List(mine.Where(c => !MakereadyKnownCapabilities.Contains(c)));
        }

        yield return "";
        yield return "Suggested machines.yml entry (confirm every value):";
        foreach (var line in SuggestedEntry(report.SuggestedMachine!, configured, sshTarget))
        {
            yield return line;
        }

        yield return "";
        yield return report.WorkerReady ? "Ready: at least one transfer mode works." : "Not ready yet: fix the FAIL items above on that PC, then run lanq makeready again.";
        yield return WorkerUntouched;
    }

    private static string List(IEnumerable<string> items) => string.Join(", ", items) is { Length: > 0 } s ? s : "none";

    /// <summary>A machines.yml entry built from the report's suggestion; values that do not look right are left as placeholders.</summary>
    private static IEnumerable<string> SuggestedEntry(MakereadySuggestion s, Machine? configured, string? sshTarget)
    {
        if (s.Os is not ("windows" or "linux"))
        {
            yield return "# This OS cannot be a lanq worker (workers are Windows or Linux).";
            yield break;
        }

        var caps = (s.Capabilities ?? []).Where(c => c is not null && CapabilityNameRegex().IsMatch(c)).Distinct(StringComparer.Ordinal);
        var name = configured?.Name ?? (s.Name is { } n && MachineNameRegex().IsMatch(n) ? n : "old-pc");
        var ssh = configured?.Ssh ?? sshTarget;
        yield return "machines:";
        yield return $"  - name: {Yaml(name)}";
        yield return $"    os: {s.Os}";
        yield return ssh is not null ? $"    ssh: {Yaml(ssh)}" : $"    ssh: {Yaml(s.Ssh ?? "user@host.lan")}   # placeholder: confirm user and host name";
        yield return $"    work_root: {Yaml(configured?.WorkRoot ?? s.WorkRoot ?? "")}";
        yield return $"    capabilities: [{string.Join(", ", caps)}]";
        yield return $"    max_jobs: {Math.Clamp(s.MaxJobs, 1, 64).ToString(CultureInfo.InvariantCulture)}";
        yield return s.Transfer is "git" or "archive" ? $"    transfer: {s.Transfer}" : "    # transfer: no transfer mode works yet (needs git, or tar for archive)";
        if (s.AllowedHours is { } hours && HoursRegex().IsMatch(hours))
        {
            yield return $"    allowed_hours: {Yaml(hours)}";
        }
    }

    private static string Yaml(string value) =>
        "\"" + value.Replace(Backslash, Backslash + Backslash, StringComparison.Ordinal).Replace("\"", Backslash + "\"", StringComparison.Ordinal) + "\"";

    private static readonly string Backslash = ((char)92).ToString();
}
