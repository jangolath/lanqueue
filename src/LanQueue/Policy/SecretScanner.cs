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
        ("quoted-credential", Make("(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\\w*\\s*[:=]\\s*[\"'][A-Za-z0-9/+_=.-]{12,}[\"']")),
    ];

    private static readonly string[] SecretFileNames =
        [".env", "id_rsa", "id_ed25519", "id_ecdsa", "secrets.json", "credentials.json", ".npmrc", ".pypirc"];

    private static readonly string[] SecretFileSuffixes = [".pem", ".pfx", ".p12", ".key", ".ppk", ".local.yml", ".local.yaml"];

    public static IReadOnlyList<SecretFinding> ScanText(string text, string where)
    {
        var found = new List<SecretFinding>();
        foreach (var (name, pattern) in Rules)
        {
            if (pattern.IsMatch(text))
            {
                found.Add(new SecretFinding(name, where));
            }
        }

        return found;
    }

    public static IReadOnlyList<SecretFinding> ScanFileNames(IEnumerable<string> paths)
    {
        var found = new List<SecretFinding>();
        foreach (var path in paths)
        {
            var file = path.Replace('\\', '/').Split('/')[^1];
            var isEnvVariant = file.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".sample", StringComparison.OrdinalIgnoreCase);
            if (SecretFileNames.Contains(file, StringComparer.OrdinalIgnoreCase)
                || isEnvVariant
                || SecretFileSuffixes.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new SecretFinding("secret-file-name", path));
            }
        }

        return found;
    }

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
}
