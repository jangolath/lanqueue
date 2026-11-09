using System.Text.RegularExpressions;

namespace LanQueue.Ssh;

/// <summary>
/// Turns the ssh failures people hit on their first run into one actionable line that still quotes ssh's own words. Callers use it only
/// when ssh itself failed (exit 255). Only whole lines in ssh's own formats are recognised, each matched from the start of the line, so text a
/// remote command printed inside a line explains nothing; an unrecognised failure, or a localised ssh, gives the first line unchanged.
/// The caller sanitises the result.
/// </summary>
internal static partial class SshErrors
{
    // "user@host: Permission denied (publickey)." as ssh prints it: one user@host token, then the message.
    [GeneratedRegex(@"^[^\s@]+@[^\s:]+: Permission denied \(", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PermissionDeniedRegex();

    // "No ED25519 host key is known for HOST and you have requested strict checking."
    [GeneratedRegex(@"^No [A-Z0-9-]+ host key is known for ", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UnknownHostKeyRegex();

    // "Permissions for 'FILE' are too open." (OpenSSH, after its UNPROTECTED PRIVATE KEY FILE banner)
    [GeneratedRegex(@"^Permissions for '.*' are too open\.\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TooOpenRegex();

    /// <summary>The first non-empty line, trimmed, or "no output".</summary>
    public static string FirstLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output";

    public static string Explain(string output, string target)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = target[(target.LastIndexOf('@') + 1)..];
        string? Line(Func<string, bool> match) => lines.FirstOrDefault(match);
        string? Starts(string prefix) => Line(l => l.StartsWith(prefix, StringComparison.Ordinal));
        var verificationFailed = Line(l => l == "Host key verification failed.");

        if (Line(l => l.StartsWith('@') && l.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal)) is not null)
        {
            return $"the host key of {host} has CHANGED since it was saved in known_hosts. If the machine was reinstalled, confirm the new "
                + $"fingerprint with its owner, then run: ssh-keygen -R {host} and connect once with: ssh {target}. Otherwise do not connect "
                + $"(ssh: {verificationFailed ?? "REMOTE HOST IDENTIFICATION HAS CHANGED"})";
        }

        if ((verificationFailed ?? Line(l => UnknownHostKeyRegex().IsMatch(l))) is { } unknown)
        {
            return $"{host} is not in your known_hosts yet, and lanq only connects to known hosts (StrictHostKeyChecking=yes). Run "
                + $"ssh {target} once in a terminal, check the fingerprint with the machine's owner, answer yes, then try again (ssh: {unknown})";
        }

        if (Line(l => TooOpenRegex().IsMatch(l)) is { } tooOpen)
        {
            return "ssh ignored your private key because other users can read it. Linux/macOS: chmod 600 on the key file; Windows: "
                + $"remove every account but your own from the file's Security tab (ssh: {tooOpen})";
        }

        if (Line(l => PermissionDeniedRegex().IsMatch(l)) is { } denied)
        {
            return $"{target} refused your key. Check that your key is loaded (ssh-add -l; add it with ssh-add) or that key_env names it, and "
                + "that its public key is in that user's .ssh/authorized_keys on the worker (for a Windows administrator account: "
                + @"C:\ProgramData\ssh\administrators_authorized_keys instead). Test with: ssh -o BatchMode=yes "
                + $"{target} true (ssh: {denied})";
        }

        if (Starts("ssh: Could not resolve hostname ") is { } unresolved)
        {
            return $"the host name {host} could not be resolved: check the ssh: value in machines.yml, or use the machine's IP address (ssh: {unresolved})";
        }

        if (Line(l => l.StartsWith("ssh: connect to host ", StringComparison.Ordinal) && l.EndsWith(": Connection refused", StringComparison.Ordinal)) is { } refused)
        {
            return $"{host} refused the connection: its SSH server is not running, or a firewall rejects the ssh port (Windows: Start-Service sshd "
                + $"and Set-Service sshd -StartupType Automatic; Linux: sudo systemctl enable --now ssh) (ssh: {refused})";
        }

        if (Starts("Could not start ssh") is { } missing)
        {
            return $"ssh was not found on this computer: install the OpenSSH client and make sure ssh is on PATH ({missing})";
        }

        return lines.FirstOrDefault() ?? "no output";
    }
}
