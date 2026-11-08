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

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
}
