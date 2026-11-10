using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Makeready;
using LanQueue.Output;
using LanQueue.Ssh;
using AddOutcome = LanQueue.Output.NextSteps.AddOutcome;

namespace LanQueue;

public static partial class Cli
{
    private const string AddUsage = "Usage: lanq makeready --ssh user@host --os windows|linux --add --name NAME [--key-env VAR] [--yes] [--config FILE] [--json]";

    /// <summary>init's live example machine (see <see cref="StarterConfig"/>): name, ssh and work_root together.</summary>
    private static bool IsInitPlaceholder(Machine m) => m.Name == "example-01" && m.Ssh == "build@10.20.30.11" && m.WorkRoot == "/srv/lanq";

    /// <summary>
    /// lanq makeready --ssh user@host --os windows|linux --add --name NAME [--key-env VAR] [--yes] [--config FILE] [--json]: scans the PC
    /// (read-only, as lanq makeready --ssh) and, only when it is READY, adds it to the local machines.yml as text: the lines shown are the
    /// lines written, after the last machine, every other byte kept. It asks first when stdin is a terminal; otherwise (and always with
    /// --json) --yes is required. Then one read-only probe of the new machine (never a wake). Writes nothing anywhere else and never changes
    /// a worker. Exit 0 added; 1 NOT READY or declined (nothing written); 2 arguments, config, duplicate, or a file lanq does not edit by
    /// itself; 70 ssh, an unusable report, or the write failed; 130 Ctrl+C before writing.
    /// </summary>
    internal static async Task<int> MakereadyAdd(ParsedArgs p, MakereadyHost host, ISshClient ssh, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var people = p.Json ? stderr : stdout;
        var state = new AddState();
        var rescan = p.SshTarget is not null && p.Os is not null ? SuggestedCommand.Makeready(null, null, p.SshTarget, p.Os, p.KeyEnv) : null;

        async Task<int> Done(string status, int code, AddOutcome outcome, string message, SuggestedCommand? setup = null, bool placeholder = false)
        {
            // Added was already said where people read; everything else is said on stderr (with --json too).
            message = JobRunner.SafeLine(message);
            if (code != 0)
            {
                await stderr.WriteLineAsync(message).ConfigureAwait(false);
            }

            var steps = NextSteps.ForMakereadyAdd(outcome, message, p.ConfigPath, rescan, setup, placeholder);
            await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
            if (p.Json)
            {
                var json = new MakereadyAddJson(status, code, state.Path, p.Name is { } n ? JobRunner.SafeLine(n) : null, state.Entry, state.AfterLine, state.Health, state.Report, message)
                {
                    NextSteps = steps,
                };
                await stdout.WriteLineAsync(JsonSerializer.Serialize(json, MakereadyAllJsonContext.Default.MakereadyAddJson)).ConfigureAwait(false);
            }

            return code;
        }

        try
        {
            return await AddCore(p, host, ssh, people, stderr, state, Done, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await Done("Cancelled", 130, AddOutcome.Cancelled, state.Written ? "cancelled (Ctrl+C) after the machine was added" : "cancelled (Ctrl+C); nothing was written")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await Done("InfraError", 70, AddOutcome.Unexpected, $"unexpected error: {ex.GetType().Name}: {ex.Message}" + (state.Written ? " (the machine was added)" : "; nothing was written"))
                .ConfigureAwait(false);
        }
    }

    /// <summary>What lanq makeready --add knows so far, for its JSON object.</summary>
    private sealed class AddState
    {
        public string? Path { get; set; }

        public IReadOnlyList<string>? Entry { get; set; }

        public int? AfterLine { get; set; }

        public HealthJson? Health { get; set; }

        public MakereadyReport? Report { get; set; }

        public bool Written { get; set; }
    }

    private delegate Task<int> AddDone(string status, int code, AddOutcome outcome, string message, SuggestedCommand? setup = null, bool placeholder = false);

    private static async Task<int> AddCore(
        ParsedArgs p, MakereadyHost host, ISshClient ssh, TextWriter people, TextWriter stderr, AddState state, AddDone done, CancellationToken ct)
    {
        // Every check that needs no network first: nothing is scanned for a command line or a file lanq would refuse anyway.
        if (p.SshTarget is not { } target || p.Os is not { } os || p.Name is not { } name || p.Target is not null || p.All)
        {
            return await done("Rejected", 2, AddOutcome.Arguments, AddUsage).ConfigureAwait(false);
        }

        if (!ConfigLoader.IsValidSshTarget(target))
        {
            return await done("Rejected", 2, AddOutcome.Arguments, "--ssh must be user@host (no leading '-', no spaces).").ConfigureAwait(false);
        }

        if (!MachineNameRegex().IsMatch(name))
        {
            return await done("Rejected", 2, AddOutcome.Arguments, "--name must be letters, digits, - and _ (at most 63, starting with a letter or digit), for example build-01.")
                .ConfigureAwait(false);
        }

        if (!p.Yes && (p.Json || host.Confirm is null))
        {
            return await done(
                "Rejected", 2, AddOutcome.Arguments,
                (p.Json ? "--add --json" : "--add without a terminal") + " cannot ask before writing machines.yml; add --yes to write it without asking.").ConfigureAwait(false);
        }

        string path;
        byte[] original;
        string text;
        bool bom;
        FleetConfig config;
        try
        {
            path = state.Path = Path.GetFullPath(ResolveConfig(p.ConfigPath, host.Env, host.Windows, File.Exists));
            if (LinkTarget(path) is { } link)
            {
                return await done("Rejected", 2, AddOutcome.Config, $"{path} is a link (to {link}); lanq makeready --add never writes through a link. Add the entry by hand.")
                    .ConfigureAwait(false);
            }

            original = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            bom = original.AsSpan().StartsWith(Utf8Bom);
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
            config = ConfigLoader.Parse(text);
        }
        catch (DecoderFallbackException)
        {
            return await done("Rejected", 2, AddOutcome.Config, $"{state.Path} is not UTF-8 text; lanq makeready --add edits only UTF-8.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            return await done("Rejected", 2, AddOutcome.Config, ex.Message).ConfigureAwait(false);
        }

        var hostName = HostOf(target);
        if (config.Machines.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is { } sameName)
        {
            return await done("Rejected", 2, AddOutcome.Duplicate, $"{path} already has a machine named {sameName.Name}; pick another --name.").ConfigureAwait(false);
        }

        if (config.Machines.FirstOrDefault(m => string.Equals(HostOf(m.Ssh), hostName, StringComparison.OrdinalIgnoreCase)) is { } sameHost)
        {
            return await done("Rejected", 2, AddOutcome.Duplicate, $"{path} already has {hostName}: machine {sameHost.Name} (ssh: {sameHost.Ssh}). One machine, one entry.")
                .ConfigureAwait(false);
        }

        try
        {
            MachinesYaml.Insert(text, ["  - name: probe"]);
        }
        catch (InsertRefusedException ex)
        {
            return await done("Rejected", 2, AddOutcome.Unsafe, $"lanq makeready --add does not edit {path} by itself: {ex.Message}. Run lanq makeready --ssh {target} --os {OsName(os)} and paste its entry by hand.")
                .ConfigureAwait(false);
        }

        // The scan: read-only, the script on ssh stdin, exactly as lanq makeready --ssh.
        var scan = await ScanAsync(AdHocMachine(target, os, p.KeyEnv), target, null, false, ssh, new HealthProbe(ssh, TimeProvider.System), ct).ConfigureAwait(false);
        if (scan.Report is not { } report)
        {
            var setup = scan.SetupWouldHelp ? SuggestedCommand.Setup(target, os, p.KeyEnv) : null;
            return await done("InfraError", 70, scan.Unusable ? AddOutcome.Unusable : AddOutcome.Unreachable, (scan.Message ?? "no report") + "; nothing was written", setup)
                .ConfigureAwait(false);
        }

        state.Report = report;
        if (!p.Json)
        {
            foreach (var line in MakereadyText(report, target, null, target, name, p.KeyEnv, withEntry: false))
            {
                await people.WriteLineAsync(JobRunner.SafeLine(line)).ConfigureAwait(false);
            }

            await people.WriteLineAsync().ConfigureAwait(false);
        }

        if (!report.WorkerReady)
        {
            return await done("NotAdded", 1, AddOutcome.NotReady, $"{target} is NOT READY, so it was not added; nothing was written to {path}.").ConfigureAwait(false);
        }

        if (SuggestedItem(report.SuggestedMachine!, null, target, name, p.KeyEnv) is not { } item || report.SuggestedMachine!.Os != OsName(os))
        {
            return await done("InfraError", 70, AddOutcome.Unusable, $"the report suggests a machine for os {report.SuggestedMachine!.Os}, not {OsName(os)}; nothing was written.")
                .ConfigureAwait(false);
        }

        // The report comes from the worker: a value with a line break or another control character is never written to machines.yml.
        if (item.Any(l => l.Any(char.IsControl) || JobRunner.SafeLine(l) != l))
        {
            return await done("InfraError", 70, AddOutcome.Unusable, "the report's suggested entry holds a control character; nothing was written.").ConfigureAwait(false);
        }

        // The lines shown are the lines written: the insertion, then a check that the result loads with exactly this one machine more.
        MachinesYaml.Insertion insertion;
        FleetConfig after;
        try
        {
            insertion = MachinesYaml.Insert(text, item);
            state.Entry = [.. insertion.Lines.Select(JobRunner.SafeLine)];
            state.AfterLine = insertion.AfterLine;
            after = VerifyAdded(config, insertion.Text, name, target, os, report.SuggestedMachine.WorkRoot, p.KeyEnv);
        }
        catch (InsertRefusedException ex)
        {
            state.Entry ??= [.. item.Select(JobRunner.SafeLine)];
            await PrintEntry(people, item, "Paste this entry under machines: by hand:").ConfigureAwait(false);
            return await done("Rejected", 2, AddOutcome.Unsafe, $"lanq makeready --add did not write {path}: {ex.Message}. Paste the entry above by hand.").ConfigureAwait(false);
        }

        await PrintEntry(people, insertion.Lines, $"lanq makeready --add: these lines go into {path} after line {insertion.AfterLine.ToString(CultureInfo.InvariantCulture)}:")
            .ConfigureAwait(false);
        if (!p.Yes && !host.Confirm!($"Add {name} to {path}?"))
        {
            return await done("NotAdded", 1, AddOutcome.Declined, $"Not added: nothing was written to {path}.").ConfigureAwait(false);
        }

        var newText = insertion.Text.EndsWith('\n') ? insertion.Text : insertion.Text + (insertion.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        var updated = bom ? [.. Utf8Bom, .. Encoding.UTF8.GetBytes(newText)] : Encoding.UTF8.GetBytes(newText);
        var (writeCode, writeError) = ReplaceConfig(path, original, updated, host.BeforeWrite);
        if (writeError is not null)
        {
            return await done(writeCode == 2 ? "Rejected" : "InfraError", writeCode, writeCode == 2 ? AddOutcome.Unsafe : AddOutcome.WriteFailed, writeError).ConfigureAwait(false);
        }

        state.Written = true;
        await people.WriteLineAsync($"Added {name} to {path}.").ConfigureAwait(false);

        // One read-only look at the new machine, printed as lanq health prints it. No waker: lanq never wakes a machine here.
        var added = after.Machines.Single(m => m.Name == name);
        MachineHealth health;
        try
        {
            health = await new HealthProbe(ssh, TimeProvider.System).ProbeOnceAsync(added, after.Policy, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await done("Added", 0, AddOutcome.Added, $"Added {name} to {path} (its health probe was cancelled).", placeholder: after.Machines.Any(IsInitPlaceholder))
                .ConfigureAwait(false);
        }

        state.Health = HealthRow(added, health) with { NextSteps = NextSteps.ForHealthRow(added, health, null, p.ConfigPath) };
        await people.WriteLineAsync("health: " + JobRunner.SafeLine(HealthLine(added, health))).ConfigureAwait(false);
        var placeholder = after.Machines.Any(IsInitPlaceholder);
        if (placeholder)
        {
            await stderr.WriteLineAsync(
                $"warning: example-01, the example machine from lanq init, is still in {path}. Delete it: it never answers, so it shows DOWN in lanq health and in every NoCapacity message.")
                .ConfigureAwait(false);
        }

        return await done("Added", 0, AddOutcome.Added, $"Added {name} to {path}.", placeholder: placeholder).ConfigureAwait(false);
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static string OsName(MachineOs os) => os == MachineOs.Windows ? "windows" : "linux";

    /// <summary>The host part of user@host (after the last @), as ssh reads it.</summary>
    private static string HostOf(string ssh) => ssh[(ssh.LastIndexOf('@') + 1)..];

    private static async Task PrintEntry(TextWriter people, IEnumerable<string> lines, string title)
    {
        await people.WriteLineAsync(title).ConfigureAwait(false);
        foreach (var line in lines)
        {
            await people.WriteLineAsync(JobRunner.SafeLine(line)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The new text must load, hold exactly one machine more, keep every old machine unchanged and in order (and the policy), and the new one
    /// must be the expected name, ssh, os, work_root and key_env. Returns the new config.
    /// </summary>
    /// <exception cref="InsertRefusedException">The result is not exactly that.</exception>
    internal static FleetConfig VerifyAdded(FleetConfig before, string newText, string name, string ssh, MachineOs os, string? workRoot, string? keyEnv)
    {
        FleetConfig after;
        try
        {
            after = ConfigLoader.Parse(newText);
        }
        catch (ConfigException ex)
        {
            throw new InsertRefusedException("the file would not load with the entry added (" + ex.Message + ")");
        }

        if (after.Machines.Count != before.Machines.Count + 1)
        {
            throw new InsertRefusedException($"the file would hold {after.Machines.Count} machines, not {before.Machines.Count + 1}");
        }

        if (!before.Machines.Select(Describe).SequenceEqual(after.Machines.Take(before.Machines.Count).Select(Describe)) || Describe(before.Policy) != Describe(after.Policy))
        {
            throw new InsertRefusedException("the existing machines or the policy would read differently with the entry added");
        }

        var added = after.Machines[^1];
        if (added.Name != name || added.Ssh != ssh || added.Os != os || added.WorkRoot != workRoot || added.KeyEnv != keyEnv)
        {
            throw new InsertRefusedException("the added machine would not read back as the entry shown");
        }

        return after;
    }

    /// <summary>Every setting of a machine as one string, to tell whether it reads the same (keep in step with <see cref="Machine"/>; a test counts its properties).</summary>
    internal static string Describe(Machine m) => string.Join(
        "\u001f",
        m.Name,
        m.Os,
        m.Ssh,
        m.KeyEnv ?? "-",
        m.WorkRoot,
        string.Join(',', m.Capabilities),
        m.MaxJobs.ToString(CultureInfo.InvariantCulture),
        m.MinFreeMemGb?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
        string.Join(',', m.Env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value)),
        string.Join(',', m.PathPrepend),
        m.Priority,
        m.AllowedHours is null ? "-" : string.Join(',', m.AllowedHours),
        m.Transfer,
        m.Wake is { } w ? string.Create(CultureInfo.InvariantCulture, $"{w.Mac}/{w.Broadcast}/{w.Port}/{w.WaitSeconds}") : "-");

    private static string Describe(PolicyConfig p) => string.Join(
        "\u001f",
        string.Create(CultureInfo.InvariantCulture, $"{p.LoadThreshold:R}/{p.MinFreeDiskGb:R}/{p.MinFreeMemGb:R}/{p.MaxMemPressure:R}/{p.WindowGraceMinutes}/{p.MinWindowMinutes}"),
        string.Join(',', p.AllowedCommands),
        string.Join(',', p.AllowedDirectories));

    /// <summary>
    /// Replaces machines.yml with <paramref name="updated"/>: a new file in the same folder (CreateNew, the original's Unix mode), then, when
    /// the path is still not a link and still holds exactly <paramref name="original"/>, File.Replace (atomic on one volume; on Windows it
    /// keeps the file's ACL). Returns (0, null) when written, else (2 or 70, why); the original is untouched on every failure.
    /// </summary>
    private static (int Code, string? Error) ReplaceConfig(string path, byte[] original, byte[] updated, Action? beforeWrite)
    {
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..12] + ".lanq-tmp");
        var created = false;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            UnixFileMode? mode = null;
            if (!OperatingSystem.IsWindows())
            {
                mode = File.GetUnixFileMode(path);
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var file = new FileStream(temp, options))
            {
                created = true;
                file.Write(updated);
                file.Flush(flushToDisk: true);
            }

            if (mode is { } m && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, m);
            }

            beforeWrite?.Invoke();
            if (LinkTarget(path) is { } link)
            {
                return (2, $"{path} became a link (to {link}) while lanq was adding; nothing was written.");
            }

            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
            {
                return (70, $"{path} changed while lanq was adding the machine; nothing was written. Run the command again.");
            }

            File.Replace(temp, path, null);
            created = false;
            return (0, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return (70, $"could not write {path}: {ex.Message}; nothing was written.");
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort: a stray .lanq-tmp file next to machines.yml, never machines.yml itself
                }
            }
        }
    }
}
