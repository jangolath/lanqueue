using System.Diagnostics;
using System.Text;
using LanQueue.Config;

namespace LanQueue.Ssh;

/// <summary>
/// Best-effort check for private key files that have no passphrase. Only OpenSSH-format keys are inspected
/// (header "BEGIN OPENSSH PRIVATE KEY"); hardware-backed "-sk" keys are skipped because their file is only a handle.
/// Anything uncertain returns false: no warning is better than a wrong one.
/// </summary>
public static class KeyCheck
{
    private const string Header = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string Footer = "-----END OPENSSH PRIVATE KEY-----";

    public static async Task<bool> IsUnencryptedAsync(string path, Func<string, Task<bool>> acceptsEmptyPassphrase)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!IsOpenSshKeyFile(text) || IsHardwareBacked(text))
        {
            return false;
        }

        return await acceptsEmptyPassphrase(path).ConfigureAwait(false);
    }

    internal static bool IsOpenSshKeyFile(string text) => text.TrimStart().StartsWith(Header, StringComparison.Ordinal);

    internal static bool IsHardwareBacked(string text)
    {
        var start = text.IndexOf(Header, StringComparison.Ordinal);
        var end = text.IndexOf(Footer, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            return false;
        }

        try
        {
            var b64 = string.Concat(text.AsSpan(start + Header.Length, end - start - Header.Length)
                .ToString().Where(c => !char.IsWhiteSpace(c)));
            var raw = Encoding.Latin1.GetString(Convert.FromBase64String(b64));
            return raw.Contains("sk-ssh-ed25519@openssh.com", StringComparison.Ordinal)
                || raw.Contains("sk-ecdsa-sha2-nistp256@openssh.com", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Runs <c>ssh-keygen -y -P "" -f path</c>; exit 0 means the key opened with an empty passphrase.</summary>
    public static async Task<bool> SshKeygenAcceptsEmptyPassphrase(string path)
    {
        var psi = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-y", "-P", "", "-f", path })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            proc.StandardInput.Close();
            var drain = Task.WhenAll(proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                proc.Kill(entireProcessTree: true);
                return false;
            }

            await drain.ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>One warning per distinct key file in use, for machines that configure a key.</summary>
    public static async Task<List<string>> WarningsAsync(
        FleetConfig config, Func<string, string?> getEnv, Func<string, Task<bool>> acceptsEmptyPassphrase)
    {
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in config.Machines.Where(m => !string.IsNullOrEmpty(m.KeyEnv)))
        {
            var path = getEnv(m.KeyEnv!);
            if (string.IsNullOrEmpty(path) || !seen.Add(path))
            {
                continue;
            }

            if (await IsUnencryptedAsync(path, acceptsEmptyPassphrase).ConfigureAwait(false))
            {
                warnings.Add($"warning: the key file named by {m.KeyEnv} has no passphrase. "
                    + "Prefer a passphrase-protected key loaded into ssh-agent and drop key_env (see README, \"Keeping your keys safe\").");
            }
        }

        return warnings;
    }
}
