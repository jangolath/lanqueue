using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Ssh;

namespace LanQueue.Health;

/// <summary>The Wake-on-LAN magic packet and the rules for the values that go into it.</summary>
public static partial class MagicPacket
{
    // \z, not $: "$" would also match just before a trailing newline. One separator style per address, never mixed.
    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\z|^(?:[0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}\z", RegexOptions.None, 1000)]
    private static partial Regex MacRegex();

    // Four dotted decimals without leading zeros (IPAddress.TryParse alone also accepts "1", "127.1" and octal-looking forms).
    [GeneratedRegex(@"^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}\z", RegexOptions.None, 1000)]
    private static partial Regex Ipv4Regex();

    /// <summary>aa:bb:cc:dd:ee:ff or aa-bb-cc-dd-ee-ff (any case); the all-zero and all-ones addresses are refused.</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = [];
        if (text is null || !MacRegex().IsMatch(text))
        {
            return false;
        }

        var bytes = Convert.FromHexString(text.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal));
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF))
        {
            return false;
        }

        mac = bytes;
        return true;
    }

    /// <summary>6 x 0xFF, then the MAC 16 times: 102 bytes.</summary>
    public static byte[] Build(ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("A MAC address is 6 bytes.", nameof(mac));
        }

        var packet = new byte[6 + (16 * 6)];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            mac.CopyTo(packet.AsSpan(6 + (i * 6)));
        }

        return packet;
    }

    /// <summary>An IPv4 literal only (no host names, no IPv6).</summary>
    public static bool TryParseBroadcast(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text is null || !Ipv4Regex().IsMatch(text) || !IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// The only broadcast addresses config accepts: the limited broadcast 255.255.255.255, or a private (10/8, 172.16/12, 192.168/16) or
    /// link-local (169.254/16) address. Public, multicast, 0.0.0.0 and loopback addresses are refused: this is a LAN tool.
    /// </summary>
    public static bool IsAllowedBroadcast(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return a.Equals(IPAddress.Broadcast)
            || b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}

/// <summary>Sends one magic packet; behind an interface so tests do not need a network.</summary>
public interface IMagicPacketSender
{
    Task SendAsync(byte[] packet, IPAddress broadcast, int port, CancellationToken ct);
}

/// <summary>UDP with SO_BROADCAST.</summary>
public sealed class UdpMagicPacketSender : IMagicPacketSender
{
    public async Task SendAsync(byte[] packet, IPAddress broadcast, int port, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        await udp.SendAsync(packet, new IPEndPoint(broadcast, port), ct).ConfigureAwait(false);
    }
}

/// <summary>A sender that never sends: lanq run --dry-run only asks the wake rules (<see cref="WakeService.Evaluate"/>), and a packet would be a bug.</summary>
public sealed class RefusingMagicPacketSender : IMagicPacketSender
{
    public Task SendAsync(byte[] packet, IPAddress broadcast, int port, CancellationToken ct) =>
        throw new InvalidOperationException("A dry run never sends a Wake-on-LAN packet.");
}

public enum WakeDecision
{
    Wake,
    NoWakeConfig,
    NotAsleep,
    Paused,
    OutsideHours,

    /// <summary>Inside its hours, but too little of the window is left to wake it, wait for it and still start a job there.</summary>
    WindowClosing,
    AlreadyWoken,
}

/// <summary>A wake decision and, for people, why.</summary>
public readonly record struct WakeVerdict(WakeDecision Decision, string Reason);

/// <summary>
/// Decides whether to wake a machine, sends the packet, and remembers per lanq invocation which machines it has already woken (at most
/// once each) and which are still coming up. Waits use <see cref="TimeProvider"/>, so tests need no real time.
/// </summary>
public sealed class WakeService(IMagicPacketSender sender, TimeProvider clock)
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);

    // ssh's own connection-phase error line: "ssh: connect to host H port P: REASON" (OpenSSH, also Win32-OpenSSH). Only that line is read, never
    // anything a remote command printed, and "Connection refused" is deliberately absent: a refusing host is awake (sshd stopped or firewalled).
    private const string ConnectPrefix = "ssh: connect to host ";

    private static readonly string[] AsleepReasons =
    [
        "Connection timed out",
        "Operation timed out",
        "No route to host",
        "Host is unreachable",
        "Network is unreachable",
        "Host is down",
        // Windows (Winsock error text, shown when the OpenSSH build does not map it to a POSIX one):
        "A socket operation was attempted to an unreachable host",
        "A socket operation was attempted to an unreachable network",
        "A connection attempt failed because the connected party did not properly respond",
        "connected host has failed to respond",
    ];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _woken = new(StringComparer.Ordinal);

    /// <summary>
    /// Unreachable in the connection sense: ssh exit 255 and ssh's own "connect to host ..." line gives a timeout or an unreachable
    /// host or network. Never an authentication, host-key or refused failure. A localized ssh does not match, which fails safe (no wake).
    /// </summary>
    public static bool LooksAsleep(SshResult r) =>
        r.ExitCode == 255 && !r.TimedOut
        && r.Output.Split('\n').Select(l => l.Trim()).Any(l =>
            l.StartsWith(ConnectPrefix, StringComparison.OrdinalIgnoreCase) && AsleepReasons.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The decision table for the automatic wake (lanq run and lanq health --wake; never the explicit lanq wake). <paramref name="localNow"/> is
    /// this computer's clock: a sleeping worker cannot report its own, so allowed_hours is judged here. A machine is woken only when its
    /// window still has at least policy.min_window_minutes plus wake.wait_seconds left: job selection holds a machine with less than
    /// min_window_minutes left, so a PC woken any later could never be used and would just be left awake.
    /// </summary>
    public WakeVerdict Evaluate(Machine machine, MachineHealth h, DateTime localNow, PolicyConfig policy)
    {
        if (machine.Wake is not { } wake)
        {
            return new(WakeDecision.NoWakeConfig, "no wake: setting");
        }

        if (h.Paused)
        {
            return new(WakeDecision.Paused, "paused on the machine");
        }

        if (h.HoldKind != HoldKind.Unreachable || !h.LooksAsleep)
        {
            return new(WakeDecision.NotAsleep, "does not look asleep");
        }

        if (HoursWindow.ParseAll(machine.AllowedHours) is { } windows)
        {
            if (HoursWindow.Remaining(windows, localNow) is not { } left)
            {
                return new(WakeDecision.OutsideHours, "outside allowed hours");
            }

            var needed = TimeSpan.FromMinutes(policy.MinWindowMinutes) + TimeSpan.FromSeconds(wake.WaitSeconds);
            if (left < needed)
            {
                return new(WakeDecision.WindowClosing, string.Create(
                    CultureInfo.InvariantCulture,
                    $"asleep, not woken: {Math.Ceiling(left.TotalMinutes):0} min left of its allowed hours, less than policy.min_window_minutes ({policy.MinWindowMinutes}) + wake.wait_seconds ({wake.WaitSeconds} s), so it could not take a job once awake"));
            }
        }

        return _woken.ContainsKey(machine.Name) ? new(WakeDecision.AlreadyWoken, "already woken by this lanq") : new(WakeDecision.Wake, "asleep and inside its hours");
    }

    /// <summary>Evaluates, and when the answer is Wake claims the machine (so it is never woken twice in this invocation).</summary>
    public WakeVerdict TryClaim(Machine machine, MachineHealth h, DateTime localNow, PolicyConfig policy)
    {
        var v = Evaluate(machine, h, localNow, policy);
        return v.Decision == WakeDecision.Wake && !_woken.TryAdd(machine.Name, DateTimeOffset.MinValue) ? new(WakeDecision.AlreadyWoken, "already woken by this lanq") : v;
    }

    /// <summary>Sends the packet; the machine counts as coming up (pending) until its wait_seconds run out, but only after a successful send.</summary>
    public async Task SendAsync(Machine machine, CancellationToken ct)
    {
        var w = machine.Wake ?? throw new InvalidOperationException($"Machine '{machine.Name}' has no wake: setting.");
        if (!MagicPacket.TryParseMac(w.Mac, out var mac) || !MagicPacket.TryParseBroadcast(w.Broadcast, out var broadcast))
        {
            throw new InvalidOperationException($"Machine '{machine.Name}': wake settings are invalid.");
        }

        await sender.SendAsync(MagicPacket.Build(mac), broadcast, w.Port, ct).ConfigureAwait(false);

        // Pending only once the packet really went out: a failed send leaves the machine claimed (no retry storm) but not waiting.
        _woken[machine.Name] = clock.GetUtcNow() + TimeSpan.FromSeconds(w.WaitSeconds);
    }

    /// <summary>True while a machine woken in this invocation may still come up (so a waiting job should keep waiting).</summary>
    public bool AnyPending => _woken.Values.Any(d => d > clock.GetUtcNow());

    public void MarkUp(Machine machine)
    {
        if (_woken.ContainsKey(machine.Name))
        {
            _woken[machine.Name] = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Calls <paramref name="isUp"/> after 5 s, 10 s, then every 20 s until it is true or wait_seconds have passed. Time passed is the larger of
    /// the clock's reading and the delays slept, so a clock that does not move (or jumps back) can never make the wait endless.
    /// </summary>
    public async Task<bool> WaitForUpAsync(Machine machine, Func<CancellationToken, Task<bool>> isUp, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(machine.Wake?.WaitSeconds ?? 0);
        var start = clock.GetUtcNow();
        var slept = TimeSpan.Zero;
        var delay = FirstDelay;
        while (true)
        {
            var byClock = clock.GetUtcNow() - start;
            var left = wait - (byClock > slept ? byClock : slept);
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            var step = delay < left ? delay : left;
            await Task.Delay(step, clock, ct).ConfigureAwait(false);
            slept += step;
            if (await isUp(ct).ConfigureAwait(false))
            {
                MarkUp(machine);
                return true;
            }

            delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
        }
    }
}
