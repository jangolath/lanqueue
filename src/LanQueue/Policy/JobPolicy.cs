using LanQueue.Config;

namespace LanQueue.Policy;

public sealed record PolicyResult(bool Ok, string? Reason)
{
    public static PolicyResult Allow { get; } = new(true, null);

    public static PolicyResult Deny(string reason) => new(false, reason);
}

/// <summary>Command and directory allowlists, plus the ban on permission-skipping agent flags.</summary>
public static class JobPolicy
{
    private static readonly string[] ForbiddenFragments =
    [
        "dangerously",
        "skip-permissions",
        "bypasspermissions",
        "--yolo",
        "bypass-approvals",
        "danger-full-access",
        "approval_policy=never",
        "approval-policy=never",
        "ask-for-approval never",
        "ask-for-approval=never",
    ];

    public static PolicyResult Validate(PolicyConfig policy, IReadOnlyList<string> argv, string directory)
    {
        if (argv.Count == 0)
        {
            return PolicyResult.Deny("No command given.");
        }

        foreach (var arg in argv)
        {
            if (arg.Any(char.IsControl))
            {
                return PolicyResult.Deny("Arguments must not contain control characters or newlines.");
            }

            var flag = ForbiddenFragments.FirstOrDefault(f => arg.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (flag is not null)
            {
                return PolicyResult.Deny($"Permission-skipping flag rejected ('{flag}'). Jobs must never bypass agent permission prompts.");
            }
        }

        // Also check the joined, quote-stripped command so "--ask-for-approval" "never" and approval_policy="never" are caught.
        var joined = string.Join(' ', argv).Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
        var joinedFlag = ForbiddenFragments.FirstOrDefault(f => joined.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (joinedFlag is not null)
        {
            return PolicyResult.Deny($"Permission-skipping flag rejected ('{joinedFlag}'). Jobs must never bypass agent permission prompts or sandboxes.");
        }

        if (!CommandAllowed(policy, argv))
        {
            return PolicyResult.Deny($"Command '{argv[0]}' is not on policy.allowed_commands.");
        }

        return DirectoryAllowed(policy, directory)
            ? PolicyResult.Allow
            : PolicyResult.Deny($"Directory '{directory}' is not on policy.allowed_directories.");
    }

    /// <summary>An allowlist entry like "dotnet test" matches argv whose leading tokens equal the entry's tokens.</summary>
    public static bool CommandAllowed(PolicyConfig policy, IReadOnlyList<string> argv) =>
        policy.AllowedCommands.Any(entry =>
        {
            var tokens = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0
                && argv.Count >= tokens.Length
                && tokens.Zip(argv).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
        });

    public static bool DirectoryAllowed(PolicyConfig policy, string directory)
    {
        var dir = Normalize(directory);
        if (dir is null)
        {
            return false;
        }

        return policy.AllowedDirectories.Select(Normalize).Any(a => a is not null
            && (a == dir || (a != "." && dir.StartsWith(a + "/", StringComparison.Ordinal))));
    }

    /// <summary>Relative, forward-slash, no '..', not rooted. Null when unacceptable.</summary>
    internal static string? Normalize(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        if (p.Length == 0 || p.StartsWith('/') || p.Contains(':', StringComparison.Ordinal) || p.Any(char.IsControl))
        {
            return null;
        }

        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (parts.Contains(".."))
        {
            return null;
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }
}
