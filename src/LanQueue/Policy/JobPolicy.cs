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

    /// <summary>
    /// Refused in any argument for a Windows worker: the double quote that PowerShell 5.1 native-argument passing re-splits on, and the
    /// characters cmd.exe interprets for redirection, escaping and variables.
    /// </summary>
    private static readonly char[] WindowsAlwaysUnsafeChars = [(char)34, '<', '>', '^', '%'];

    /// <summary>
    /// The cmd.exe command separators. Harmless to a native .exe started by CreateProcess (dotnet test --filter "A|B" needs them), so they
    /// are allowed for Windows workers only when the program is a native .exe (<see cref="LooksNative"/> here, and the worker checks again
    /// what the program actually resolves to before starting it).
    /// </summary>
    internal static readonly char[] WindowsShimUnsafeChars = ['&', '|'];

    /// <summary>
    /// Programs that hand their command line to cmd.exe even when found as an .exe (cmd itself; npm, yarn and friends, also as the native
    /// shims some version managers install): never given &amp; or | on a Windows worker. Lower case, without extension.
    /// </summary>
    internal static readonly string[] WindowsShimPrograms = ["cmd", "npm", "npx", "pnpm", "pnpx", "yarn", "yarnpkg", "corepack"];

    /// <summary>
    /// Extra rule for Windows workers. PowerShell 5.1 rebuilds a native command line without escaping embedded double quotes, and a
    /// .cmd/.bat shim (npm.cmd, yarn.cmd and similar) re-parses that line through cmd.exe, so an argument like "&amp;calc" would run a
    /// second command. Arguments containing any of " &lt; &gt; ^ % are therefore always refused for Windows machines, and arguments containing
    /// &amp; or | are refused unless the program (argv[0]) is a native .exe: a bare name or path with no extension or .exe, not cmd or a
    /// package-manager shim. The worker then checks that the name really resolves to an .exe before it starts anything.
    /// </summary>
    public static PolicyResult ValidateForWindows(IReadOnlyList<string> argv)
    {
        var bad = argv.FirstOrDefault(a => a.IndexOfAny(WindowsAlwaysUnsafeChars) >= 0);
        if (bad is not null)
        {
            return PolicyResult.Deny(
                $"Argument '{bad}' contains one of \" < > ^ %, which Windows workers refuse: PowerShell 5.1 passes embedded quotes "
                + "through unescaped, and cmd.exe (which runs .cmd shims such as npm.cmd) interprets the others, which could run another command.");
        }

        var shim = argv.FirstOrDefault(a => a.IndexOfAny(WindowsShimUnsafeChars) >= 0);
        if (shim is null || argv.Count == 0)
        {
            return PolicyResult.Allow;
        }

        return NotNativeBecause(argv[0]) is { } why
            ? PolicyResult.Deny(
                $"Argument '{shim}' contains & or |, which Windows workers pass only to a native .exe, and '{argv[0]}' {why}: .cmd/.bat shims such as "
                + "npm.cmd re-parse the command line through cmd.exe, which could run another command. Call the .exe directly (for example dotnet, "
                + "not a .cmd wrapper) or put the command in a script.")
            : PolicyResult.Allow;
    }

    /// <summary>True when any argument holds &amp; or |: the Windows job wrapper then checks what the program resolves to before starting it.</summary>
    public static bool HasShimUnsafe(IReadOnlyList<string> argv) => argv.Any(a => a.IndexOfAny(WindowsShimUnsafeChars) >= 0);

    /// <summary>True when <paramref name="program"/> looks like a native executable: see <see cref="NotNativeBecause"/>.</summary>
    public static bool LooksNative(string program) => NotNativeBecause(program) is null;

    /// <summary>
    /// Why <paramref name="program"/> (argv[0]) may not be a native .exe, or null when it looks like one: a bare command name or a path whose
    /// file name has no extension or .exe, holds no &amp; | or wildcard character, and is not cmd or a package-manager shim (npm, yarn, ...).
    /// </summary>
    private static string? NotNativeBecause(string program)
    {
        if (program.Length == 0 || program.IndexOfAny(['&', '|', '*', '?', '[', ']']) >= 0)
        {
            return "is not a plain program name";
        }

        var name = program[(program.LastIndexOfAny(['/', (char)92]) + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        if (extension.Length > 0 && !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"is a {extension.ToLowerInvariant()} file, not an .exe";
        }

        return WindowsShimPrograms.Contains(stem.ToLowerInvariant(), StringComparer.Ordinal)
            ? "runs through cmd.exe (a cmd or package-manager shim)"
            : null;
    }

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
        policy.AllowedCommands.Any(entry => Matches(entry, argv));

    /// <summary>The allowlist entry that lets <paramref name="argv"/> run (the longest, when several match), or null when none does.</summary>
    public static string? AllowedBy(PolicyConfig policy, IReadOnlyList<string> argv) =>
        policy.AllowedCommands.Where(entry => Matches(entry, argv)).MaxBy(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    private static bool Matches(string entry, IReadOnlyList<string> argv)
    {
        var tokens = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0
            && argv.Count >= tokens.Length
            && tokens.Zip(argv).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
    }

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
