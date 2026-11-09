using System.Collections.Concurrent;
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

    /// <summary>True for the limited broadcast, 10/8, 172.16/12, 192.168/16, 169.254/16 and loopback (a directed broadcast of anything else could leave the LAN).</summary>
    public static bool IsLanAddress(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return a.Equals(IPAddress.Broadcast) || IPAddress.IsLoopback(a)
            || b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>A warning when the machine's broadcast address is not in a private or link-local range; null when it is fine (or invalid, which config validation refuses).</summary>
    public static string? BroadcastWarning(Machine m) =>
        m.Wake is { } w && TryParseBroadcast(w.Broadcast, out var a) && !IsLanAddress(a)
            ? $"wake.broadcast {w.Broadcast} is not a private or link-local address: the magic packet could leave your LAN. Use your subnet's broadcast (for example 10.20.30.255) or leave it out"
            : null;
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

public enum WakeDecision
{
    Wake,
    NoWakeConfig,
    NotAsleep,
    Paused,
    OutsideHours,
    AlreadyWoken,
}

/// <summary>
/// Decides whether to wake a machine, sends the packet, and remembers per lanq invocation which machines it has already woken (at most
/// once each) and which are still coming up. Waits use <see cref="TimeProvider"/>, so tests need no real time.
/// </summary>
public sealed class WakeService(IMagicPacketSender sender, TimeProvider clock)
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);

    private static readonly string[] AsleepMarkers =
        ["Connection timed out", "Operation timed out", "No route to host", "Connection refused", "Network is unreachable", "Host is down"];

    private static readonly string[] NotAsleepMarkers =
        ["Permission denied", "Host key verification failed", "REMOTE HOST IDENTIFICATION HAS CHANGED", "Too many authentication failures", "Could not resolve hostname"];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _woken = new(StringComparer.Ordinal);

    /// <summary>Unreachable in the connection sense: ssh exit 255 with a timeout, no-route or refused error. Never an auth or host-key failure.</summary>
    public static bool LooksAsleep(SshResult r) =>
        r.ExitCode == 255 && !r.TimedOut
        && AsleepMarkers.Any(m => r.Output.Contains(m, StringComparison.OrdinalIgnoreCase))
        && !NotAsleepMarkers.Any(m => r.Output.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The decision table for the automatic wake. <paramref name="localNow"/> is this computer's clock: a sleeping worker cannot report its own,
    /// so allowed_hours is judged here.
    /// </summary>
    public WakeDecision Evaluate(Machine machine, MachineHealth h, DateTime localNow)
    {
        if (machine.Wake is null)
        {
            return WakeDecision.NoWakeConfig;
        }

        if (h.Paused)
        {
            return WakeDecision.Paused;
        }

        if (h.HoldKind != HoldKind.Unreachable || !h.LooksAsleep)
        {
            return WakeDecision.NotAsleep;
        }

        if (HoursWindow.ParseAll(machine.AllowedHours) is { } windows && HoursWindow.Remaining(windows, localNow) is null)
        {
            return WakeDecision.OutsideHours;
        }

        return _woken.ContainsKey(machine.Name) ? WakeDecision.AlreadyWoken : WakeDecision.Wake;
    }

    /// <summary>Evaluates, and when the answer is Wake claims the machine (so it is never woken twice in this invocation).</summary>
    public WakeDecision TryClaim(Machine machine, MachineHealth h, DateTime localNow)
    {
        var d = Evaluate(machine, h, localNow);
        return d == WakeDecision.Wake && !_woken.TryAdd(machine.Name, DateTimeOffset.MaxValue) ? WakeDecision.AlreadyWoken : d;
    }

    /// <summary>Sends the packet and records that the machine is coming up until its wait_seconds run out.</summary>
    public async Task SendAsync(Machine machine, CancellationToken ct)
    {
        var w = machine.Wake ?? throw new InvalidOperationException($"Machine '{machine.Name}' has no wake: setting.");
        if (!MagicPacket.TryParseMac(w.Mac, out var mac) || !MagicPacket.TryParseBroadcast(w.Broadcast, out var broadcast))
        {
            throw new InvalidOperationException($"Machine '{machine.Name}': wake settings are invalid.");
        }

        _woken[machine.Name] = clock.GetUtcNow() + TimeSpan.FromSeconds(w.WaitSeconds);
        await sender.SendAsync(MagicPacket.Build(mac), broadcast, w.Port, ct).ConfigureAwait(false);
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

    /// <summary>Calls <paramref name="isUp"/> after 5 s, 10 s, then every 20 s until it is true or wait_seconds have passed.</summary>
    public async Task<bool> WaitForUpAsync(Machine machine, Func<CancellationToken, Task<bool>> isUp, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(machine.Wake?.WaitSeconds ?? 0);
        var start = clock.GetUtcNow();
        var delay = FirstDelay;
        while (true)
        {
            var left = wait - (clock.GetUtcNow() - start);
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(delay < left ? delay : left, clock, ct).ConfigureAwait(false);
            if (await isUp(ct).ConfigureAwait(false))
            {
                MarkUp(machine);
                return true;
            }

            delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
        }
    }
}
