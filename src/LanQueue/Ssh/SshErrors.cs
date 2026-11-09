namespace LanQueue.Ssh;

/// <summary>
/// Turns the ssh failures people hit on their first run into one actionable line that still quotes ssh's own words. Only ssh's error text
/// is read (and the caller sanitises the result); an unrecognised failure, or a localised ssh, gives ssh's first line unchanged.
/// </summary>
internal static class SshErrors
{
    public static string Explain(string output, string target)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = target[(target.LastIndexOf('@') + 1)..];
        string? Line(string marker) => lines.FirstOrDefault(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (Line("REMOTE HOST IDENTIFICATION HAS CHANGED") is not null)
        {
            return $"the host key of {host} has CHANGED since it was saved in known_hosts. If the machine was reinstalled, confirm the new "
                + $"fingerprint with its owner, then run: ssh-keygen -R {host} and connect once with: ssh {target}. Otherwise do not connect "
                + $"(ssh: {Line("Host key verification failed") ?? "REMOTE HOST IDENTIFICATION HAS CHANGED"})";
        }

        if ((Line("Host key verification failed") ?? Line("host key is known for")) is { } unknown)
        {
            return $"{host} is not in your known_hosts yet, and lanq only connects to known hosts (StrictHostKeyChecking=yes). Run "
                + $"ssh {target} once in a terminal, check the fingerprint with the machine's owner, answer yes, then try again (ssh: {unknown})";
        }

        if (Line("are too open") is { } tooOpen)
        {
            return "ssh ignored your private key because other users can read it. Linux/macOS: chmod 600 on the key file; Windows: "
                + $"remove every account but your own from the file's Security tab (ssh: {tooOpen})";
        }

        if (Line("Permission denied (") is { } denied)
        {
            return $"{target} refused your key. Check that your key is loaded (ssh-add -l; add it with ssh-add) or that key_env names it, and "
                + "that its public key is in that user's .ssh/authorized_keys on the worker (for a Windows administrator account: "
                + @"C:\ProgramData\ssh\administrators_authorized_keys instead). Test with: ssh -o BatchMode=yes "
                + $"{target} true (ssh: {denied})";
        }

        if (Line("Could not resolve hostname") is { } unresolved)
        {
            return $"the host name {host} could not be resolved: check the ssh: value in machines.yml, or use the machine's IP address (ssh: {unresolved})";
        }

        if (lines.FirstOrDefault(l => l.StartsWith("ssh: connect to host ", StringComparison.OrdinalIgnoreCase)
            && l.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)) is { } refused)
        {
            return $"{host} refused the connection: its SSH server is not running, or a firewall rejects the ssh port (Windows: Start-Service sshd "
                + $"and Set-Service sshd -StartupType Automatic; Linux: sudo systemctl enable --now ssh) (ssh: {refused})";
        }

        if (Line("Could not start ssh") is { } missing)
        {
            return $"ssh was not found on this computer: install the OpenSSH client and make sure ssh is on PATH ({missing})";
        }

        return lines.FirstOrDefault() ?? "no output";
    }
}
