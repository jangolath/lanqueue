using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Jobs;

namespace LanQueue;

/// <summary>What lanq makeready --setup and --add use from this computer; tests replace each part.</summary>
/// <param name="Env">Environment variables (HOME, the --key-env variable, LANQ_CONFIG, APPDATA, ...).</param>
/// <param name="Windows">True on Windows (the per-user config path).</param>
/// <param name="AgentKeys">The output of <c>ssh-add -L</c> (public keys only), or null when there is no agent or it failed.</param>
/// <param name="Confirm">Asks a yes/no question on the terminal; null when stdin is not a terminal (then --add needs --yes).</param>
/// <param name="BeforeWrite">Tests: runs just before lanq makeready --add replaces the file.</param>
internal sealed record MakereadyHost(Func<string, string?> Env, bool Windows, Func<string?> AgentKeys, Func<string, bool>? Confirm, Action? BeforeWrite = null)
{
    /// <summary>This computer: the real environment, ssh-add -L, and a prompt on stderr only when stdin is a terminal.</summary>
    public static MakereadyHost Real(TextWriter stderr) => new(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows(),
        Cli.AgentPublicKeys,
        Console.IsInputRedirected
            ? null
            : question =>
            {
                stderr.Write(question + " [y/N] ");
                stderr.Flush();
                return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
            });
}

public static partial class Cli
{
    /// <summary>The public key types a worker's authorized_keys line may start with (no certificates).</summary>
    private static readonly string[] PublicKeyTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com",
    ];

    [GeneratedRegex(@"^[A-Za-z0-9+/]{16,16384}={0,2}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KeyBlobRegex();

    /// <summary>A key comment kept in the printed line: nothing a shell or a single-quoted string would treat specially.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.@+-]{1,64}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KeyCommentRegex();

    /// <summary>A user name lanq prints into commands that create the user (Windows and Linux both accept these).</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]{0,19}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BuildUserRegex();

    /// <summary>The longest .pub file lanq reads (an RSA 16384-bit key is about 2.8 KB).</summary>
    private const int MaxPubBytes = 16 * 1024;

    /// <summary>Where the public key in the setup came from, which decides how the first connection names it.</summary>
    internal enum KeySource
    {
        Agent,
        KeyEnv,
        LanqFile,
        IdFile,
        None,
    }

    /// <summary>The public key line to authorize (validated) and where it came from; <paramref name="Line"/> is null when none was found.</summary>
    internal sealed record SetupKey(string? Line, KeySource Source, string Why);

    /// <summary>
    /// One authorized_keys line, or null: <c>type base64 [comment]</c> with a known key type and a base64 blob that decodes to that same
    /// type. A comment with anything but plain characters is dropped (the line is printed inside single quotes for bash and PowerShell).
    /// Never anything else from the text: a private key or any other content gives null.
    /// </summary>
    internal static string? PublicKeyLine(string text)
    {
        var parts = text.Trim().Split((char[])[' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !PublicKeyTypes.Contains(parts[0], StringComparer.Ordinal) || !KeyBlobRegex().IsMatch(parts[1]))
        {
            return null;
        }

        try
        {
            // The blob starts with its own type as an SSH string (uint32 length, then the name).
            var blob = Convert.FromBase64String(parts[1]);
            var length = blob.Length >= 4 ? (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3] : -1;
            if (length != parts[0].Length || blob.Length < 4 + length || Encoding.ASCII.GetString(blob, 4, length) != parts[0])
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        return parts.Length == 3 && KeyCommentRegex().IsMatch(parts[2].Trim()) ? $"{parts[0]} {parts[1]} {parts[2].Trim()}" : $"{parts[0]} {parts[1]}";
    }

    /// <summary>
    /// The public key for the setup. With --key-env only the .pub file beside the key it names (lanq then connects with that key alone,
    /// IdentitiesOnly). Without it: the first valid key ssh-agent lists, else ~/.ssh/lanq_ed25519.pub, else ~/.ssh/id_ed25519.pub. Only
    /// .pub files are ever opened (the key file's own path plus ".pub", never the key file itself), and at most 16 KB of one.
    /// </summary>
    internal static SetupKey FindSetupKey(string? keyEnv, MakereadyHost host)
    {
        if (keyEnv is not null)
        {
            if (host.Env(keyEnv) is not { Length: > 0 } keyPath)
            {
                return new(null, KeySource.KeyEnv, $"{keyEnv} is not set; set it to your private key file's path (the key itself is never read)");
            }

            return ReadPub(keyPath + ".pub") is { } line
                ? new(line, KeySource.KeyEnv, $"from the .pub file beside the key that {keyEnv} names")
                : new(null, KeySource.KeyEnv, $"no valid public key in the .pub file beside the key that {keyEnv} names");
        }

        foreach (var agentLine in (host.AgentKeys() ?? "").Split('\n'))
        {
            if (PublicKeyLine(agentLine) is { } line)
            {
                return new(line, KeySource.Agent, "from ssh-agent (ssh-add -L, its first key)");
            }
        }

        var ssh = Path.Combine(HomeDir(host.Env), ".ssh");
        if (ReadPub(Path.Combine(ssh, "lanq_ed25519.pub")) is { } lanq)
        {
            return new(lanq, KeySource.LanqFile, "from ~/.ssh/lanq_ed25519.pub");
        }

        return ReadPub(Path.Combine(ssh, "id_ed25519.pub")) is { } id
            ? new(id, KeySource.IdFile, "from ~/.ssh/id_ed25519.pub")
            : new(null, KeySource.None, "no key in ssh-agent, and no valid ~/.ssh/lanq_ed25519.pub or ~/.ssh/id_ed25519.pub");
    }

    /// <summary>The first line of a .pub file when it is a valid public key; null otherwise (missing, too large, a folder, anything else).</summary>
    private static string? ReadPub(string path)
    {
        if (!path.EndsWith(".pub", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxPubBytes)
            {
                return null;
            }

            using var reader = new StreamReader(path, Encoding.UTF8);
            return reader.ReadLine() is { } first ? PublicKeyLine(first) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>ssh-add -L (public keys only; nothing is connected to): its output, or null when ssh-add is missing, fails or takes over 5 s.</summary>
    internal static string? AgentPublicKeys()
    {
        try
        {
            var start = new ProcessStartInfo("ssh-add") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-L");
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && output.Wait(1000) ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// lanq makeready --ssh user@host --os windows|linux --setup [--key-env VAR]: prints the worker's full one-time setup, with the user from
    /// user@host and this computer's public key filled in, and connects to nothing. Text only; exit 0, or 2 for bad arguments.
    /// </summary>
    internal static async Task<int> MakereadySetup(ParsedArgs p, MakereadyHost host, TextWriter stdout, TextWriter stderr)
    {
        if (p.SshTarget is not { } target || p.Os is not { } os || p.Target is not null || p.All)
        {
            await stderr.WriteLineAsync("Usage: lanq makeready --ssh user@host --os windows|linux --setup [--key-env VAR]").ConfigureAwait(false);
            return 2;
        }

        var at = target.LastIndexOf('@');
        var user = at > 0 ? target[..at] : "";
        var hostName = target[(at + 1)..];
        if (!ConfigLoader.IsValidSshTarget(target) || at <= 0 || hostName.Length == 0 || !BuildUserRegex().IsMatch(user))
        {
            await stderr.WriteLineAsync(JobRunner.SafeLine(
                "--setup needs --ssh user@host with the build user to create: a name of letters, digits, _, . and - (at most 20), such as build@10.20.30.21."))
                .ConfigureAwait(false);
            return 2;
        }

        if (user.Equals("root", StringComparison.OrdinalIgnoreCase) || user.Equals("administrator", StringComparison.OrdinalIgnoreCase))
        {
            await stderr.WriteLineAsync($"--setup creates a dedicated build user without admin rights; pick a new name instead of {user} (for example build@{hostName}).")
                .ConfigureAwait(false);
            return 2;
        }

        var key = FindSetupKey(p.KeyEnv, host);
        foreach (var line in SetupText(target, user, hostName, os, key, p.KeyEnv, p.ConfigPath))
        {
            await stdout.WriteLineAsync(line).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>The setup text. Every value in it is validated: the user and host by their patterns, the key by <see cref="PublicKeyLine"/>.</summary>
    private static IEnumerable<string> SetupText(string target, string user, string hostName, MachineOs os, SetupKey key, string? keyEnv, string? config)
    {
        var windows = os == MachineOs.Windows;
        var keyLine = key.Line ?? "<your public key: the one line of lanq_ed25519.pub>";
        var keyEnvArg = keyEnv is null ? "" : " --key-env " + keyEnv;
        var configArg = config is null ? "" : " --config " + (config.Contains(' ', StringComparison.Ordinal) ? "\"" + config + "\"" : config);
        yield return $"lanq makeready --ssh {target} --os {(windows ? "windows" : "linux")} --setup: the one-time setup of a new {(windows ? "Windows 10/11" : "Linux")} worker.";
        yield return "Nothing was sent: lanq connected to nothing. lanq never changes a worker: the PC's owner runs the worker part on that PC.";
        yield return "";
        if (key.Line is null)
        {
            yield return $"No public key found ({key.Why}). Make one on this computer first, then run this again:";
            yield return "  ssh-keygen -t ed25519 -f $HOME/.ssh/lanq_ed25519 -C lanq";
            yield return "  ssh-add $HOME/.ssh/lanq_ed25519";
            yield return "Until then the commands below hold a placeholder instead of the key.";
        }
        else
        {
            yield return $"Public key to authorize ({key.Why}):";
            yield return "  " + keyLine;
            if (key.Source == KeySource.LanqFile)
            {
                yield return "lanq uses the keys in ssh-agent: load it with ssh-add $HOME/.ssh/lanq_ed25519 (or pass --key-env with a variable holding its path).";
            }
        }

        yield return "";
        var steps = windows ? WindowsSetup(user, keyLine) : LinuxSetup(user, hostName, keyLine);
        foreach (var line in steps)
        {
            yield return line;
        }

        yield return "";
        yield return "On this computer (the orchestrator), once the worker part above is done:";
        yield return "";
        yield return "# 7. Connect once, key only. ssh shows the worker's host key fingerprint: compare the fingerprint before answering yes";
        yield return "#    (it must match step 6 exactly; if it differs, answer no and ask the PC's owner). lanq only connects to hosts accepted here.";
        switch (key.Source)
        {
            case KeySource.KeyEnv:
                yield return $"ssh -o PasswordAuthentication=no -i \"$env:{keyEnv}\" {target} exit     # PowerShell";
                yield return $"ssh -o PasswordAuthentication=no -i \"${keyEnv}\" {target} exit     # bash";
                break;
            case KeySource.LanqFile or KeySource.None:
                yield return $"ssh -o PasswordAuthentication=no -i $HOME/.ssh/lanq_ed25519 {target} exit";
                break;
            default:
                yield return $"ssh -o PasswordAuthentication=no {target} exit";
                break;
        }

        yield return "#    It returns without a password prompt when the key works; \"Permission denied (publickey)\" means it does not.";
        yield return "";
        yield return "On the worker again, only after step 7 logged in with the key:";
        yield return "";
        foreach (var line in windows ? WindowsPasswordsOff() : LinuxPasswordsOff())
        {
            yield return line;
        }

        yield return "";
        yield return "Then, on this computer: check the worker and add it to machines.yml once it is ready (it asks before writing):";
        yield return $"  lanq makeready --ssh {target} --os {(windows ? "windows" : "linux")} --add --name NAME{keyEnvArg}{configArg}";
        yield return "Every step is explained in docs/TEAM-QUICKSTART.md, section 3 (https://github.com/jangolath/lanqueue).";
    }

    /// <summary>Worker side, Windows (from docs/TEAM-QUICKSTART.md, Windows 10/11 worker), with every grant by SID.</summary>
    private static IEnumerable<string> WindowsSetup(string user, string keyLine) =>
    [
        "On the worker, in Windows PowerShell opened with \"Run as administrator\" (one window for steps 1 to 6):",
        "",
        "# 1. OpenSSH Server, started now and at every boot",
        "Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0",
        "Start-Service sshd",
        "Set-Service -Name sshd -StartupType 'Automatic'",
        "",
        "# 2. The firewall: ssh only on private networks, only from the local subnet (the last line must show Private for the LAN adapter)",
        "if (!(Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {",
        "    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22",
        "}",
        "Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Profile Private -RemoteAddress LocalSubnet",
        "Get-NetConnectionProfile | Select-Object InterfaceAlias, NetworkCategory",
        "",
        $"# 3. The build user {user}, not an administrator (for an administrator Windows reads only C:\\ProgramData\\ssh\\administrators_authorized_keys),",
        $"#    and its profile. Do NOT create C:\\Users\\{user} or its .ssh by hand first: sshd would then never see the key.",
        $"New-LocalUser -Name {user} -Password (Read-Host -AsSecureString -Prompt 'Password for {user}') -PasswordNeverExpires",
        $"Add-LocalGroupMember -SID S-1-5-32-545 -Member {user}     # the built-in Users group, by SID (any Windows language)",
        $"runas /user:{user} \"cmd /c exit\"                          # type the password: logs on once, which creates the profile",
        $"$sid = (Get-LocalUser {user}).SID.Value",
        "$p = (Get-CimInstance Win32_UserProfile | Where-Object SID -eq $sid).LocalPath",
        $"$p                                                       # the profile folder, normally C:\\Users\\{user}",
        "Test-Path \"$p\\NTUSER.DAT\"                                 # must print True; if not, check the password and run the runas line again",
        "",
        $"# 4. The public key, readable only by {user}, SYSTEM (*S-1-5-18) and Administrators (*S-1-5-32-544)",
        "New-Item -ItemType Directory -Force \"$p\\.ssh\" | Out-Null",
        $"Set-Content -Path \"$p\\.ssh\\authorized_keys\" -Value '{keyLine}' -Encoding ascii",
        "icacls \"$p\\.ssh\\authorized_keys\" /inheritance:r /grant \"*${sid}:F\" /grant '*S-1-5-18:F' /grant '*S-1-5-32-544:F'",
        "",
        $"# 5. The work folder (work_root C:/lq), writable only by {user}, SYSTEM and Administrators. If lanq makeready later suggests",
        "#    another drive (for example D:/lq), make that folder the same way.",
        "New-Item -ItemType Directory -Force C:\\lq | Out-Null",
        "icacls C:\\lq /inheritance:r /grant \"*${sid}:(OI)(CI)M\" /grant '*S-1-5-18:(OI)(CI)F' /grant '*S-1-5-32-544:(OI)(CI)F'",
        "",
        "# 6. The host key fingerprint: tell it to the developer, who compares it in step 7",
        "ssh-keygen -lf C:\\ProgramData\\ssh\\ssh_host_ed25519_key.pub",
    ];

    private static IEnumerable<string> WindowsPasswordsOff() =>
    [
        "# 8. Password logins off: PasswordAuthentication no at the top of sshd_config (the first value sshd reads wins), checked, then sshd restarted",
        "$cfg = \"$env:ProgramData\\ssh\\sshd_config\"",
        "Copy-Item $cfg \"$cfg.bak\"",
        "Set-Content -Path $cfg -Value (@('PasswordAuthentication no') + (Get-Content $cfg)) -Encoding ascii",
        "& \"$env:WINDIR\\System32\\OpenSSH\\sshd.exe\" -t                 # prints nothing when the file is valid",
        "Restart-Service sshd",
        "#    If sshd.exe -t prints an error, put the old file back before restarting: Copy-Item \"$cfg.bak\" $cfg -Force",
    ];

    /// <summary>Worker side, Linux (from docs/TEAM-QUICKSTART.md, Linux worker).</summary>
    private static IEnumerable<string> LinuxSetup(string user, string hostName, string keyLine)
    {
        // The firewall rule names the worker's /24 when the target is an IPv4 address; otherwise a placeholder the owner fills in.
        var ipv4 = IPAddress.TryParse(hostName, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && hostName.Count(c => c == '.') == 3;
        var subnet = ipv4 ? string.Join('.', hostName.Split('.').Take(3)) + ".0/24" : "<your-LAN>";
        return
        [
            "On the worker, in bash (Debian/Ubuntu shown; elsewhere use the distribution's package manager):",
            "",
            "# 1. OpenSSH server, started now and at every boot (git is optional with transfer: archive)",
            "sudo apt update",
            "sudo apt install openssh-server git",
            "sudo systemctl enable --now ssh",
            "",
            $"# 2. The build user {user}: no sudo, and no password (so no password login)",
            $"sudo useradd -m -s /bin/bash {user}",
            "",
            $"# 3. The public key: .ssh 700 and authorized_keys 600, owned by {user}",
            $"sudo install -d -m 700 -o {user} -g {user} /home/{user}/.ssh",
            $"echo '{keyLine}' | sudo tee /home/{user}/.ssh/authorized_keys >/dev/null",
            $"sudo chown {user}:{user} /home/{user}/.ssh/authorized_keys",
            $"sudo chmod 600 /home/{user}/.ssh/authorized_keys",
            "",
            $"# 4. The work folder (work_root /srv/lq), owned by {user}",
            $"sudo install -d -o {user} -g {user} /srv/lq",
            "",
            "# 5. The firewall: ssh only from your LAN. Only if ufw is active (sudo ufw status); remove any wider rule for port 22 it lists",
            ipv4 ? $"#    ({subnet} is {hostName}'s /24; use your LAN's own range if it differs)" : "#    Replace <your-LAN> with your LAN's range, for example 10.20.30.0/24",
            $"sudo ufw allow from {subnet} to any port 22 proto tcp",
            "",
            "# 6. The host key fingerprint: tell it to the developer, who compares it in step 7",
            "ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub",
        ];
    }

    private static IEnumerable<string> LinuxPasswordsOff() =>
    [
        "# 8. Password logins off for every account (the file sorts first in sshd_config.d, and the first value sshd reads wins)",
        "printf 'PasswordAuthentication no\\nKbdInteractiveAuthentication no\\n' | sudo tee /etc/ssh/sshd_config.d/10-lanq.conf >/dev/null",
        "sudo sshd -t && sudo systemctl restart ssh",
    ];
}
