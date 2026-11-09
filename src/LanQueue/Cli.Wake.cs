using System.Net.Sockets;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>
    /// lanq wake: sends one magic packet to a machine that has a wake: setting. It ignores allowed_hours on purpose (you asked for this machine
    /// by name). With --wait it then waits, with backoff, until ssh answers or wake.wait_seconds run out. With --json stdout carries exactly one
    /// object on every path and the human lines go to stderr. Exit 0 sent (and answering), 1 no answer within the wait, 2 bad arguments or no
    /// wake: setting, 70 could not send, 130 cancelled.
    /// </summary>
    internal static async Task<int> Wake(ParsedArgs p, FleetConfig config, WakeService waker, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var info = p.Json ? stderr : stdout;
        var started = System.Diagnostics.Stopwatch.StartNew();

        async Task<int> Fail(string status, int code, string message)
        {
            message = JobRunner.SafeLine(message);
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            if (p.Json)
            {
                await stdout.WriteLineAsync(JsonSerializer.Serialize(new ErrorJson(status, code, message), LanqJson.Default.ErrorJson)).ConfigureAwait(false);
            }

            return code;
        }

        var machine = config.Machines.FirstOrDefault(x => string.Equals(x.Name, p.Target, StringComparison.Ordinal));
        if (machine is null)
        {
            return await Fail(
                "Rejected",
                2,
                (p.Target is null ? "lanq wake needs a machine name." : $"No machine named '{p.Target}' in the config.") + " Configured: " + string.Join(", ", config.Machines.Select(x => x.Name))).ConfigureAwait(false);
        }

        if (machine.Wake is not { } wake)
        {
            return await Fail("Rejected", 2, $"Machine '{machine.Name}' has no wake: setting in the config (see the README, \"Wake-on-LAN\").").ConfigureAwait(false);
        }

        try
        {
            await info.WriteLineAsync(
                $"[lanq] {machine.Name}: sending a Wake-on-LAN packet for {wake.Mac} to {wake.Broadcast}:{wake.Port}"
                + (machine.AllowedHours is { Count: > 0 } ? " (sent even outside allowed hours: you asked for it)" : "")).ConfigureAwait(false);
            await waker.SendAsync(machine, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await Fail("Cancelled", 130, "cancelled (Ctrl+C) before the packet was sent").ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await Fail("InfraError", 70, $"{machine.Name}: could not send the magic packet: {ex.Message}").ConfigureAwait(false);
        }

        if (!p.WaitForSsh)
        {
            var sent = $"{machine.Name}: magic packet sent. It only helps if Wake-on-LAN is enabled in the BIOS/UEFI and the network card driver (usually wired Ethernet only); add --wait to wait for ssh.";
            await info.WriteLineAsync("[lanq] " + sent).ConfigureAwait(false);
            if (p.Json)
            {
                await stdout.WriteLineAsync(JsonSerializer.Serialize(new WakeJson("Sent", 0, machine.Name, true, null, Math.Round(started.Elapsed.TotalSeconds, 1), sent), LanqJson.Default.WakeJson)).ConfigureAwait(false);
            }

            return 0;
        }

        await info.WriteLineAsync($"[lanq] {machine.Name}: waiting up to {wake.WaitSeconds} s for ssh").ConfigureAwait(false);
        bool up;
        try
        {
            up = await waker.WaitForUpAsync(machine, async token => (await probe.ProbeOnceAsync(machine, config.Policy, token).ConfigureAwait(false)).Reachable, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await Fail("Cancelled", 130, $"cancelled (Ctrl+C) while waiting for {machine.Name}").ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await Fail("InfraError", 70, $"{machine.Name}: waiting for ssh failed: {ex.Message}").ConfigureAwait(false);
        }

        var message = up
            ? $"{machine.Name}: answering ssh"
            : $"{machine.Name}: no ssh answer within {wake.WaitSeconds} s. Check that Wake-on-LAN is enabled in the BIOS/UEFI and the network card driver, and that the machine is on wired Ethernet (docs/WORKERS.md)";
        await (up ? info : stderr).WriteLineAsync("[lanq] " + message).ConfigureAwait(false);
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(
                new WakeJson(up ? "Awake" : "NoAnswer", up ? 0 : 1, machine.Name, true, up, Math.Round(started.Elapsed.TotalSeconds, 1), message), LanqJson.Default.WakeJson)).ConfigureAwait(false);
        }

        return up ? 0 : 1;
    }
}
