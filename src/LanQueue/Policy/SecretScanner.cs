using System.Text.RegularExpressions;

namespace LanQueue.Policy;

public sealed record SecretFinding(string Rule, string Where);

/// <summary>Refuses obvious secrets. Findings never echo the matched value.</summary>
public static class SecretScanner
{
    private static readonly (string Name, Regex Pattern)[] Rules =
    [
        ("private-key-block", Make(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY")),
        ("aws-access-key", Make(@"\bAKIA[0-9A-Z]{16}\b")),
        ("github-token", Make(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})")),
        ("slack-token", Make(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")),
        ("api-key-sk", Make(@"\bsk-[A-Za-z0-9_-]{20,}")),
        ("jwt", Make(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}")),
        ("bearer-token", Make(@"\bBearer\s+[A-Za-z0-9._~+/-]{20,}")),
        ("quoted-credential", Make("""(?:(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\w{0,40}|(?<![a-z0-9])pass(?:phrase|wd)?)["']?\s*[:=]\s*(?:"(?!\$\{|\$\(|<|%|\{\{)[^"\s]{8,}"|'(?!\$\{|\$\(|<|%|\{\{)[^'\s]{8,}')""")),
        ("unquoted-credential", Make("""^[ \t+\-]{0,40}(?:export[ \t]+)?["']?[\w.-]{0,60}?(?:(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\w{0,40}|(?<![a-z0-9])pass(?:phrase|wd)?)["']?\s*[:=]\s*(?!\$|<|%|\{\{|(?:this|self|process|os|env|config|options|request|req|args|params|input)[.\[])[^\s"'#;,(){}<>]{8,}[ \t\r]*(?:#.*)?$""")),
        ("url-credential", Make("""://[^/\s:@]{1,100}:(?![$<%{])[^/\s@]{3,100}@""")),
        ("connection-string-password", Make("""(?:;\s*(?:password|pwd)=(?![$<%{])[^;"'\s]{3,})|(?:(?:^|["'])[ \t]*(?:password|pwd)=(?![$<%{])[^;"'\s]{3,};\s*\w+=)""")),
    ];

    private static readonly string[] SecretFileNames =
    [
        ".env", "id_rsa", "id_ed25519", "id_ecdsa", "secrets.json", "credentials.json", ".npmrc", ".pypirc",
        ".git-credentials", "terraform.tfstate", "terraform.tfstate.backup",
    ];

    private static readonly string[] SecretFileSuffixes =
        [".pem", ".pfx", ".p12", ".key", ".ppk", ".local.yml", ".local.yaml", ".tfvars", ".jks", ".keystore"];

    private const string AwsCredentials = ".aws/credentials";

    /// <summary>Rule name reported when a pattern times out: the scan is incomplete, so the payload is refused.</summary>
    public const string TimeoutRule = "scan-timed-out (refusing: the payload could not be fully checked)";

    public static IReadOnlyList<SecretFinding> ScanText(string text, string where) => ScanText(text, where, Rules);

    internal static IReadOnlyList<SecretFinding> ScanText(string text, string where, IEnumerable<(string Name, Regex Pattern)> rules)
    {
        var found = new List<SecretFinding>();
        foreach (var (name, pattern) in rules)
        {
            try
            {
                if (pattern.IsMatch(text))
                {
                    found.Add(new SecretFinding(name, where));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail closed: an unfinished scan must never read as "clean".
                found.Add(new SecretFinding(TimeoutRule, where));
            }
        }

        return found;
    }

    public static IReadOnlyList<SecretFinding> ScanFileNames(IEnumerable<string> paths)
    {
        var found = new List<SecretFinding>();
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            var file = normalized.Split('/')[^1];
            var isAwsCredentials = normalized.Equals(AwsCredentials, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + AwsCredentials, StringComparison.OrdinalIgnoreCase);
            var isEnvVariant = file.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".sample", StringComparison.OrdinalIgnoreCase);
            if (isAwsCredentials
                || SecretFileNames.Contains(file, StringComparer.OrdinalIgnoreCase)
                || isEnvVariant
                || SecretFileSuffixes.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new SecretFinding("secret-file-name", path));
            }
        }

        return found;
    }

    public const string CommittedFileRule = "committed-secret-file-name";

    public const string CommittedConfigRule = "committed-credential";

    /// <summary>Largest committed package-manager config that is read for the credential scan; a bigger one is refused unread.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    private static readonly string[] NpmCredentialKeys = ["_authToken", "_auth", "_password", "password"];

    /// <summary>Committed package-manager configs are checked by content, not refused by name: registry-only files are common.</summary>
    public static bool IsPackageConfig(string path) =>
        path.Replace('\\', '/').Split('/')[^1] is var f
        && (f.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) || f.Equals(".pypirc", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Refuses a committed <c>.npmrc</c> with a non-empty <c>_authToken</c>, <c>_auth</c>, <c>_password</c> or <c>password</c> (also
    /// registry-scoped, <c>//host/:_authToken=</c>) unless the value is just an environment reference such as <c>${NPM_TOKEN}</c>, and a
    /// committed <c>.pypirc</c> with a non-empty <c>password</c>. Null content (over <see cref="MaxConfigBytes"/>) is refused unchecked.
    /// Findings name the path and key, never the value.
    /// </summary>
    public static IReadOnlyList<SecretFinding> ScanCommittedConfig(string path, string? content)
    {
        if (content is null)
        {
            return [new SecretFinding(CommittedConfigRule + " (file over 64 KB, not checked)", path)];
        }

        var npm = path.Replace('\\', '/').Split('/')[^1].Equals(".npmrc", StringComparison.OrdinalIgnoreCase);
        var keys = npm ? NpmCredentialKeys : ["password"];
        var found = new List<SecretFinding>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var sep = npm ? line.IndexOf('=', StringComparison.Ordinal) : line.IndexOfAny(['=', ':']);
            if (line.Length == 0 || line[0] is '#' or ';' or '[' || sep <= 0)
            {
                continue;
            }

            var key = line[..sep].Trim();
            key = npm ? key[(key.LastIndexOf(':') + 1)..] : key; // "//registry.example/:_authToken" -> "_authToken"
            var value = line[(sep + 1)..].Trim().Trim('"', '\'').Trim();
            var envReference = npm && value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}') && value.IndexOf('}') == value.Length - 1;
            var canonical = keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null && value.Length > 0 && !envReference)
            {
                found.Add(new SecretFinding($"{CommittedConfigRule} (key {canonical})", path));
            }
        }

        return found.DistinctBy(f => f.Rule).ToList();
    }

    private static readonly string[] CommittedSecretNames = [".env", "id_rsa", "id_ed25519", "credentials.json"];

    private static readonly string[] CommittedSecretSuffixes = [".pem", ".pfx", ".p12", ".key", ".kdbx"];

    private static readonly string[] SampleFolders = ["fixtures", "testdata"];

    /// <summary>
    /// Scans the names of committed files (every path of the HEAD tree): <c>.env</c>, <c>.env.*</c>, <c>id_rsa</c>, <c>id_ed25519</c>,
    /// <c>*.pem</c>, <c>*.pfx</c>, <c>*.p12</c>, <c>*.key</c>, <c>credentials.json</c>, <c>*.kdbx</c> (<c>.npmrc</c> and <c>.pypirc</c> are
    /// checked by content instead, see <see cref="ScanCommittedConfig"/>).
    /// Obvious samples are allowed: names ending in <c>.example</c> or <c>.sample</c>, names containing <c>.example.</c>, and anything
    /// under a folder named <c>fixtures</c> or <c>testdata</c>. Names only: content and older commits are not looked at.
    /// </summary>
    public static IReadOnlyList<SecretFinding> ScanCommittedFileNames(IEnumerable<string> paths)
    {
        var found = new List<SecretFinding>();
        foreach (var path in paths)
        {
            var segments = path.Replace('\\', '/').Split('/');
            var file = segments[^1];
            var secretLike = CommittedSecretNames.Contains(file, StringComparer.OrdinalIgnoreCase)
                || file.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                || CommittedSecretSuffixes.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            var sample = file.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)
                || file.Contains(".example.", StringComparison.OrdinalIgnoreCase)
                || segments[..^1].Any(d => SampleFolders.Contains(d, StringComparer.OrdinalIgnoreCase));
            if (secretLike && !sample)
            {
                found.Add(new SecretFinding(CommittedFileRule, path));
            }
        }

        return found;
    }

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
}
