using System.Globalization;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Ssh;

namespace LanQueue.Health;

/// <summary>Why a machine is held, for code that must act on the reason (the text in <see cref="MachineHealth.Hold"/> is for people).</summary>
public enum HoldKind
{
    None,
    Unreachable,
    Unreadable,
    ToolMissing,
    Paused,
    OutsideHours,
    WindowClosing,
    LowDisk,
    LowMemory,
    MemoryPressure,
    Load,

    /// <summary>A hold the runner adds for one job (arguments not safe for Windows, --mem).</summary>
    Job,
}

/// <param name="Hold">Null when the machine may take work; otherwise why it should be skipped.</param>
public sealed record MachineHealth(
    string Name,
    bool Reachable,
    double LoadRatio,
    double DiskFreeGb,
    int SlotsUsed,
    string? Hold,
    string? Error)
{
    /// <summary>Available memory in GB, or null when the worker did not report it (older or unusual hosts).</summary>
    public double? MemFreeGb { get; init; }

    /// <summary>Free swap in GB (Linux), or null when not reported.</summary>
    public double? SwapFreeGb { get; init; }

    /// <summary>Linux PSI memory "some avg10" (percent), or null when the kernel does not report it.</summary>
    public double? MemPressure { get; init; }

    /// <summary>True when a PAUSE file exists in the machine's work_root.</summary>
    public bool Paused { get; init; }

    /// <summary>True when the hours were checked against the worker's own clock; false when it did not report one (orchestrator clock used).</summary>
    public bool WorkerClock { get; init; }

    /// <summary>When the machine's current allowed-hours window ends (UTC), or null when it has no allowed_hours.</summary>
    public DateTimeOffset? WindowEndsUtc { get; init; }

    /// <summary>Why the machine is held (<see cref="HoldKind.None"/> when it is not), for code that acts on the reason.</summary>
    public HoldKind HoldKind { get; init; }

    /// <summary>The worker's own local time as it reported it (yyyy-MM-ddTHH:mm:ss), or null when it did not.</summary>
    public string? LocalTime { get; init; }

    /// <summary>True when the probe failed the way a sleeping or powered-off host does (connect timeout, no route or unreachable host); never for an auth or host-key failure.</summary>
    public bool LooksAsleep { get; init; }
}

/// <summary>Whether a probe may wake a machine that looks asleep.</summary>
public enum WakeMode
{
    /// <summary>Never send anything.</summary>
    None,

    /// <summary>Send the packet and return at once.</summary>
    Send,

    /// <summary>Send the packet and wait (with backoff) for ssh.</summary>
    SendAndWait,
}

/// <param name="waker">Wake-on-LAN for machines with a wake: setting; null (doctor, makeready, tests) never sends a packet.</param>
public sealed class HealthProbe(ISshClient ssh, TimeProvider clock, WakeService? waker = null)
{
    /// <summary>Machines woken, or still coming up, in this lanq invocation; null when this probe never wakes anything.</summary>
    public WakeService? Waker => waker;

    /// <summary>
    /// Probes the machine once and, only when asked (<paramref name="wake"/>), wakes it as <see cref="WakeAsync"/> describes. The default
    /// never sends anything, so a plain probe (health, doctor, makeready, run's first look) stays read-only and quick.
    /// </summary>
    public async Task<MachineHealth> ProbeAsync(Machine machine, PolicyConfig policy, CancellationToken ct, WakeMode wake = WakeMode.None)
    {
        var h = await ProbeOnceAsync(machine, policy, ct).ConfigureAwait(false);
        if (waker is not null && h.Reachable)
        {
            waker.MarkUp(machine);
        }

        return wake == WakeMode.None ? h : await WakeAsync(machine, policy, h, wake == WakeMode.SendAndWait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Given a probe result: when the machine looks asleep, has a wake: setting and is inside its allowed hours (this computer's clock), sends
    /// one magic packet (at most one per machine per invocation, also across parallel calls). With <paramref name="wait"/> it then probes
    /// again with backoff until the machine answers or its wake.wait_seconds run out; without it, it returns at once saying the machine is
    /// waking. A send that fails for any reason is reported on this machine and never thrown (one machine cannot fail a whole fleet probe).
    /// Whether the machine is paused cannot be known while it sleeps; a reachable paused machine is never woken.
    /// </summary>
    public async Task<MachineHealth> WakeAsync(Machine machine, PolicyConfig policy, MachineHealth h, bool wait, CancellationToken ct)
    {
        if (waker is null || machine.Wake is not { } wake || waker.TryClaim(machine, h, clock.GetLocalNow().DateTime) != WakeDecision.Wake)
        {
            return h;
        }

        try
        {
            await waker.SendAsync(machine, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return h with { Error = $"asleep, but the magic packet could not be sent: {JobRunner.SafeLine(ex.Message)}" };
        }

        if (!wait)
        {
            return h with { Error = $"waking: magic packet sent, waiting up to {wake.WaitSeconds} s for ssh" };
        }

        var latest = h;
        var up = await waker.WaitForUpAsync(machine, async token =>
        {
            latest = await ProbeOnceAsync(machine, policy, token).ConfigureAwait(false);
            return latest.Reachable;
        }, ct).ConfigureAwait(false);
        return up ? latest : h with { Error = $"magic packet sent, no ssh answer within {wake.WaitSeconds} s (is Wake-on-LAN enabled in the BIOS and the network card driver?)" };
    }

    public async Task<MachineHealth> ProbeOnceAsync(Machine machine, PolicyConfig policy, CancellationToken ct)
    {
        SshResult result;
        try
        {
            result = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Probe(machine)), null, TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new MachineHealth(machine.Name, false, 0, 0, 0, "unreachable", ex.Message) { HoldKind = HoldKind.Unreachable };
        }

        if (!result.Ok)
        {
            return new MachineHealth(machine.Name, false, 0, 0, 0, "unreachable", FirstLine(result.Output)) { HoldKind = HoldKind.Unreachable, LooksAsleep = WakeService.LooksAsleep(result) };
        }

        var values = Parse(result.Output);
        values.TryGetValue("tool", out var tool);
        if (tool is not ("ok" or "missing"))
        {
            return new MachineHealth(machine.Name, true, 0, 0, 0, "unreadable health output (tool check missing or invalid)", null) { HoldKind = HoldKind.Unreadable };
        }

        if (!TryNum(values, "load", out var load) || !TryNum(values, "disk_free_gb", out var disk) || !TryNum(values, "slots_used", out var slotsRaw)
            || load < 0 || disk < 0 || slotsRaw < 0)
        {
            // A missing or garbled key must never read as "idle machine with space": hold instead.
            return new MachineHealth(machine.Name, true, 0, 0, 0, "unreadable health output (load, disk_free_gb or slots_used missing or invalid)", null)
            {
                HoldKind = HoldKind.Unreadable,
            };
        }

        // Hours are the worker's own: its local clock when it reports one, else (older probe output) the orchestrator's local clock.
        var workerNow = values.TryGetValue("local_time", out var lt)
            && DateTime.TryParseExact(lt, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t
                : (DateTime?)null;
        var windows = HoursWindow.ParseAll(machine.AllowedHours);
        // The worker's UTC offsets for the next 36 h place the window end across a DST change (only together with its own clock).
        var offsets = workerNow is null ? null : UtcOffsetTable.TryParse(values.GetValueOrDefault("now_epoch"), values.GetValueOrDefault("tz_start"), values.GetValueOrDefault("tz_offsets"));
        var remaining = windows is null ? null : HoursWindow.Remaining(windows, workerNow ?? clock.GetLocalNow().DateTime, offsets);
        var health = new MachineHealth(machine.Name, true, load, disk, (int)slotsRaw, null, null)
        {
            MemFreeGb = OptionalNum(values, "mem_free_gb"),
            SwapFreeGb = OptionalNum(values, "swap_free_gb"),
            MemPressure = OptionalNum(values, "mem_pressure"),
            Paused = values.TryGetValue("paused", out var paused) && paused == "1",
            WorkerClock = workerNow is not null,
            LocalTime = workerNow?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            WindowEndsUtc = remaining is { } left ? clock.GetUtcNow() + left : null,
        };
        var (kind, hold) = tool == "missing"
            ? (HoldKind.ToolMissing, machine.Transfer == TransferMode.Archive
                ? "tar is missing or unsupported on the machine (transfer: archive needs GNU tar or bsdtar plus gzip on Linux, or a Windows built-in tar.exe that accepts --no-acls --no-xattrs --no-fflags --no-same-owner --no-same-permissions)"
                : "git is not installed on the machine (transfer: git needs git; install it or set transfer: archive)")
            : EvaluateHold(machine, policy, health, remaining);
        return health with { Hold = hold, HoldKind = kind };
    }

    /// <summary>
    /// "Hold short" rules: a PAUSE file, outside allowed hours, a window about to close, low disk, low memory, memory pressure, or load
    /// above the threshold. Memory that the worker did not report (null) never holds: older kernels and odd hosts simply do not say.
    /// </summary>
    /// <param name="windowRemaining">Time left in the machine's current allowed-hours window; null when outside (or when it has none).</param>
    public static string? Evaluate(Machine machine, PolicyConfig policy, MachineHealth h, TimeSpan? windowRemaining) =>
        EvaluateHold(machine, policy, h, windowRemaining).Text;

    /// <inheritdoc cref="Evaluate"/>
    public static (HoldKind Kind, string? Text) EvaluateHold(Machine machine, PolicyConfig policy, MachineHealth h, TimeSpan? windowRemaining)
    {
        if (h.Paused)
        {
            return (HoldKind.Paused, "paused on the machine");
        }

        if (machine.AllowedHours is { Count: > 0 })
        {
            if (windowRemaining is not { } left)
            {
                return (HoldKind.OutsideHours, "outside allowed hours");
            }

            if (left < TimeSpan.FromMinutes(policy.MinWindowMinutes))
            {
                return (HoldKind.WindowClosing, string.Create(CultureInfo.InvariantCulture, $"window closing ({Math.Ceiling(left.TotalMinutes):0} min left)"));
            }
        }

        if (h.DiskFreeGb < policy.MinFreeDiskGb)
        {
            return (HoldKind.LowDisk, string.Create(CultureInfo.InvariantCulture, $"disk free {h.DiskFreeGb:0.#} GB below minimum {policy.MinFreeDiskGb:0.#} GB"));
        }

        if (h.MemFreeGb < (machine.MinFreeMemGb ?? policy.MinFreeMemGb))
        {
            return (HoldKind.LowMemory, string.Create(CultureInfo.InvariantCulture, $"low memory ({h.MemFreeGb:0.0} GB free)"));
        }

        if (h.MemPressure > policy.MaxMemPressure)
        {
            return (HoldKind.MemoryPressure, string.Create(CultureInfo.InvariantCulture, $"memory pressure (some avg10 {h.MemPressure:0.#}% above {policy.MaxMemPressure:0.#}%)"));
        }

        return h.LoadRatio > policy.LoadThreshold
            ? (HoldKind.Load, string.Create(CultureInfo.InvariantCulture, $"load {h.LoadRatio:0.00} above threshold {policy.LoadThreshold:0.00}"))
            : (HoldKind.None, null);
    }

    public static Dictionary<string, string> Parse(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i > 0)
            {
                map[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }

        return map;
    }

    private static bool TryNum(Dictionary<string, string> v, string key, out double value)
    {
        value = 0;
        return v.TryGetValue(key, out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>A non-negative finite number, or null when the key is missing or garbled (treated as unknown).</summary>
    private static double? OptionalNum(Dictionary<string, string> v, string key) =>
        TryNum(v, key, out var value) && value >= 0 ? value : null;

    private static string FirstLine(string text) => JobRunner.SafeLine(
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output");
}
