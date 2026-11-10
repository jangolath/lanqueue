using System.Security.Cryptography;
using System.Text;
using LanQueue.Output;
using LanQueue.Policy;

namespace LanQueue.Jobs;

/// <summary>
/// What identifies "the same command" across runs in jobs.jsonl: the pair (<see cref="Key"/>, <see cref="Dir"/>). --needs and --mem are not
/// part of it (they choose a machine, not what runs).
/// </summary>
public static class JobHistory
{
    /// <summary>Keys longer than this are cut and end in a hash of the whole key.</summary>
    public const int MaxKeyChars = 1000;

    /// <summary>How much of a long key is kept before "#" and the hash.</summary>
    internal const int LongKeyPrefixChars = 960;

    /// <summary>
    /// The command's identity, from its argv (for a shard: the TEMPLATE argv, so {shard} and {shards} stay literal): each token bare when it is
    /// made only of plain characters (<see cref="CommandLine.IsBare"/>), otherwise POSIX single-quoted, joined by one space. Token boundaries
    /// therefore survive ("a b" and "a" "b" differ). A key over <see cref="MaxKeyChars"/> characters keeps its first
    /// <see cref="LongKeyPrefixChars"/> characters, then "#" and the first 16 hex digits of the SHA-256 of the whole key.
    /// </summary>
    public static string Key(IReadOnlyList<string> argv)
    {
        var key = string.Join(' ', argv.Select(t => CommandLine.IsBare(t) ? t : ShellQuote.Posix(t)));
        if (key.Length <= MaxKeyChars)
        {
            return key;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var cut = char.IsHighSurrogate(key[LongKeyPrefixChars - 1]) ? LongKeyPrefixChars - 1 : LongKeyPrefixChars;
        return key[..cut] + "#" + hash;
    }

    /// <summary>The job's --dir as one spelling: backslashes become "/", leading "./" and trailing "/" are dropped, and nothing left is ".".</summary>
    public static string Dir(string directory)
    {
        var d = directory.Replace('\\', '/');
        while (d.StartsWith("./", StringComparison.Ordinal))
        {
            d = d[2..];
        }

        d = d.TrimEnd('/');
        return d.Length == 0 ? "." : d;
    }
}
