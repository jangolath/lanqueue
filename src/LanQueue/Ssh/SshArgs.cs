using LanQueue.Config;

namespace LanQueue.Ssh;

/// <summary>Builds ssh/scp argument lists. Always key-only and non-interactive; there is no password option.</summary>
public static class SshArgs
{
    public static List<string> Options(string? keyPath)
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no",
            "-o", "PreferredAuthentications=publickey",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=10",
        };
        if (!string.IsNullOrEmpty(keyPath))
        {
            args.AddRange(["-o", "IdentitiesOnly=yes", "-i", keyPath]);
        }

        return args;
    }

    public static List<string> Ssh(Machine machine, string? keyPath, string remoteCommand)
    {
        RequireSafeTarget(machine);
        var args = Options(keyPath);
        args.AddRange(["--", machine.Ssh, remoteCommand]);
        return args;
    }

    public static List<string> Scp(Machine machine, string? keyPath, string localPath, string remotePath)
    {
        RequireSafeTarget(machine);
        var args = new List<string> { "-B", "-q" };
        args.AddRange(Options(keyPath));
        args.AddRange(["--", localPath, $"{machine.Ssh}:{remotePath}"]);
        return args;
    }

    private static void RequireSafeTarget(Machine machine)
    {
        if (machine.Ssh.StartsWith('-') || machine.Ssh.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException($"Unsafe ssh target for machine '{machine.Name}'.");
        }
    }
}
