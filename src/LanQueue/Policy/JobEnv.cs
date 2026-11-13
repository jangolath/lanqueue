using System.Globalization;
using System.Text.RegularExpressions;
using LanQueue.Config;

namespace LanQueue.Policy;

/// <summary>
/// One variable a job sets (lanq run --env NAME=VALUE, lanq.yml env:). <see cref="ToString"/> gives the name only, and the JSON env field
/// and jobs.jsonl list names only. The values are not secret-safe: the dry run shows the --env flags as typed (after the secret scan),
/// suggested commands in nextSteps repeat the command line with them, and on the worker they are in the job's process environment, visible
/// to other users of that machine.
/// </summary>
public sealed record JobEnvVar(string Name, string Value)
{
    /// <summary>The name only: a record's default text would print the value.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The rules for variables: machines.yml env: (the developer's own, checked by <see cref="ConfigLoader"/>) and a job's --env (from the
/// command line or a repository's lanq.yml, so untrusted). A job may set a name only when machines.yml policy.allowed_env lists it (exactly,
/// or by a prefix ending in *), and never a name on the hard deny table (<see cref="IsJobDenied"/>), which wins over allowed_env. The table
/// is a guard against the obvious ways a variable can make an allowlisted command run other code or move where tools keep their files; it
/// is not a sandbox.
/// </summary>
public static partial class JobEnv
{
    /// <summary>
    /// Names that would let a value hijack the dynamic loader or a shell's startup or tracing; refused in machines.yml env: too, as is every
    /// name starting with <see cref="MachineDeniedPrefixes"/>. PATH is refused there with its own message (path_prepend: sets it).
    /// </summary>
    internal static readonly string[] MachineDeniedNames =
        ["BASH_ENV", "ENV", "PROMPT_COMMAND", "SHELLOPTS", "BASHOPTS", "PS4", "GLIBC_TUNABLES", "PSModulePath", "COMSPEC", "PATHEXT"];

    /// <summary>The prefixes of loader variables (ld.so, dyld) refused in machines.yml env: and for jobs.</summary>
    internal static readonly string[] MachineDeniedPrefixes = ["LD_", "DYLD_"];

    /// <summary>
    /// The hard deny table for a job's --env (compared ignoring case): the machines.yml list and PATH, plus the variables that make a runtime
    /// load other code (Node, .NET, Java, Python, Perl, Ruby), the profile and system folders tools keep their files in, and IFS / CDPATH.
    /// </summary>
    internal static readonly string[] JobDeniedNames =
    [
        .. MachineDeniedNames,
        "PATH",

        // Node
        "NODE_OPTIONS", "NODE_PATH",

        // .NET (machine env: and path_prepend: own where the runtime lives)
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X86",

        // Java
        "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH",

        // Python, Perl, Ruby
        "PYTHONSTARTUP", "PYTHONPATH", "PYTHONHOME", "PYTHONUSERBASE", "PYTHONWARNINGS", "PYTHONBREAKPOINT", "PYTHONPLATLIBDIR",
        "PERL5OPT", "PERL5LIB", "PERLLIB", "RUBYOPT", "RUBYLIB",

        // Go (toolchain, module sources and the C compilers cgo runs; GOOS and GOARCH stay allowed)
        "GOFLAGS", "GOROOT", "GOTOOLCHAIN", "GOPROXY", "GOSUMDB", "GONOSUMDB", "GONOSUMCHECK", "GOINSECURE", "GOENV", "GOPATH", "GOBIN", "CC", "CXX",

        // Rust
        "RUSTFMT", "RUSTUP_TOOLCHAIN",

        // Native libraries that load modules from a path (OpenSSL, glibc iconv)
        "OPENSSL_CONF", "OPENSSL_ENGINES", "OPENSSL_MODULES", "GCONV_PATH",

        // Profiles and system folders (git and other tools read their config from these)
        "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "SYSTEMROOT", "WINDIR", "XDG_CONFIG_HOME", "HOMEDRIVE", "HOMEPATH",
        "HOMESHARE",

        // Shell
        "IFS", "CDPATH",
    ];

    /// <summary>The prefixes on the hard deny table: the loader ones, .NET profilers, git, ssh, npm and yarn settings, MSBuild, cgo, and lanq's own.</summary>
    internal static readonly string[] JobDeniedPrefixes =
        [.. MachineDeniedPrefixes, "CORECLR_", "COR_", "GIT_", "SSH_", "NPM_CONFIG_", "YARN_", "MSBUILD", "CGO_", "LANQ_"];

    /// <summary>The longest value one --env may have (characters).</summary>
    public const int MaxValueLength = 4096;

    /// <summary>The most all of a job's --env may add up to (the sum of NAME=VALUE lengths, in characters).</summary>
    public const int MaxTotalLength = 16384;

    // \z, not $: "$" would also match just before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\*?\z", RegexOptions.None, 1000)]
    private static partial Regex PatternRegex();

    /// <summary>A variable name: letters, digits and _, not starting with a digit.</summary>
    public static bool IsValidName(string name) => ConfigLoader.IsValidEnvName(name);

    /// <summary>On the machines.yml deny list (PATH aside): a loader or shell startup variable.</summary>
    public static bool IsMachineDenied(string name) => Denied(name, MachineDeniedNames, MachineDeniedPrefixes);

    /// <summary>On the hard deny table for jobs (ignoring case); allowed_env can never let one through.</summary>
    public static bool IsJobDenied(string name) => Denied(name, JobDeniedNames, JobDeniedPrefixes);

    private static bool Denied(string name, string[] names, string[] prefixes) =>
        names.Contains(name, StringComparer.OrdinalIgnoreCase) || prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal for a name on the hard deny table (the value is never shown).</summary>
    public static string DeniedMessage(string name) =>
        $"--env {name} can make the command run other code or move where tools keep their files; lanq never sets it for a job.";

    /// <summary>The refusal for a name policy.allowed_env does not list.</summary>
    public static string NotAllowedMessage(string name) =>
        $"--env {name} is not in policy.allowed_env (your machines.yml); the developer adds names there if jobs may set them";

    /// <summary>
    /// One --env NAME=VALUE, split at the first '='. The name must be a valid variable name and not on the hard deny table; the value must
    /// be non-empty (Windows PowerShell 5.1 cannot set an empty variable) and hold no control characters. The value is never in a message,
    /// and neither is a name that is not valid (it may be part of a value typed without its '=').
    /// </summary>
    /// <exception cref="ArgumentException">The first problem.</exception>
    public static JobEnvVar Parse(string text)
    {
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            throw new ArgumentException("--env must be NAME=VALUE (for example --env CONFIGURATION=Release).");
        }

        var name = text[..eq];
        var value = text[(eq + 1)..];
        if (!IsValidName(name))
        {
            throw new ArgumentException("--env NAME=VALUE: the name must be letters, digits and _ and not start with a digit (for example --env CONFIGURATION=Release).");
        }

        if (IsJobDenied(name))
        {
            throw new ArgumentException(DeniedMessage(name));
        }

        if (value.Length == 0)
        {
            throw new ArgumentException($"--env {name} is empty. Empty values are not supported (Windows cannot set one); leave the variable out instead.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException($"--env {name} needs a value without control characters (no newlines, tabs or escapes).");
        }

        if (value.Length > MaxValueLength)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"--env {name}: the value is {value.Length:N0} characters; at most {MaxValueLength:N0}."));
        }

        return new JobEnvVar(name, value);
    }

    /// <summary>Every --env of one command line, in the order given (<see cref="Parse"/> each); a name given twice (ignoring case) is refused.</summary>
    /// <exception cref="ArgumentException">The first problem.</exception>
    public static IReadOnlyList<JobEnvVar> ParseAll(IEnumerable<string> texts)
    {
        var list = new List<JobEnvVar>();
        foreach (var text in texts)
        {
            var v = Parse(text);
            if (list.Any(x => string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"--env {v.Name} is given twice (names are compared ignoring case, as on Windows).");
            }

            list.Add(v);
        }

        var total = list.Sum(v => v.Name.Length + 1 + v.Value.Length);
        if (total > MaxTotalLength)
        {
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture, $"--env: the variables add up to {total:N0} characters (NAME=VALUE each); at most {MaxTotalLength:N0} in all."));
        }

        return list;
    }

    /// <summary>
    /// What is wrong with a policy.allowed_env pattern, or null when it is fine: it must be a name or a prefix ending in *; an exact name on the
    /// hard deny table and anything starting with LANQ_ are refused (they could never be set, so listing them is a mistake).
    /// </summary>
    public static string? PatternProblem(string pattern)
    {
        if (!PatternRegex().IsMatch(pattern))
        {
            return $"policy.allowed_env entry '{pattern}' must be a variable name (letters, digits and _, not starting with a digit) or a prefix ending in *, such as MYAPP_*.";
        }

        if (pattern.StartsWith("LANQ_", StringComparison.OrdinalIgnoreCase))
        {
            return $"policy.allowed_env entry '{pattern}' is refused: names starting with LANQ_ are reserved for lanq itself.";
        }

        // An exact denied name, or a prefix every name of which a deny prefix covers (GIT_*, LD_*, GIT_SSH*): a job could never set any of
        // them. A broader prefix that only partly overlaps (NODE_*, MY*) is fine: the deny table still wins for the names it covers.
        var fullyDenied = pattern.EndsWith('*')
            ? JobDeniedPrefixes.Any(p => pattern[..^1].StartsWith(p, StringComparison.OrdinalIgnoreCase))
            : IsJobDenied(pattern);
        if (fullyDenied)
        {
            return $"policy.allowed_env entry '{pattern}' is on lanq's deny list (it can make a command run other code or move where tools keep their files), so a job can never set it; remove it.";
        }

        return null;
    }

    /// <summary>Whether policy.allowed_env lets a job set <paramref name="name"/> (ignoring case; MYAPP_* allows names starting with MYAPP_). The deny table is separate and wins.</summary>
    public static bool IsAllowed(IEnumerable<string> patterns, string name) =>
        patterns.Any(p => p.EndsWith('*')
            ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
}
