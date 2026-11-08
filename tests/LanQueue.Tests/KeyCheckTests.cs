using System.Text;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

public class KeyCheckTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("lanq-keys").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string name, string body)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    private static string OpenSsh(string payload) =>
        "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(Encoding.Latin1.GetBytes(payload)) + "\n-----END OPENSSH PRIVATE KEY-----\n";

    [Fact]
    public async Task OpenSshKeyThatOpensWithEmptyPassphraseIsFlagged() =>
        Assert.True(await KeyCheck.IsUnencryptedAsync(Write("a", OpenSsh("openssh-key-v1 none")), _ => Task.FromResult(true)));

    [Fact]
    public async Task PassphraseProtectedKeyIsNotFlagged() =>
        Assert.False(await KeyCheck.IsUnencryptedAsync(Write("a", OpenSsh("openssh-key-v1 aes256-ctr")), _ => Task.FromResult(false)));

    [Fact]
    public async Task HardwareBackedKeyIsNeverFlagged() =>
        Assert.False(await KeyCheck.IsUnencryptedAsync(Write("a", OpenSsh("sk-ssh-ed25519@openssh.com")), _ => Task.FromResult(true)));

    [Fact]
    public async Task NonOpenSshOrMissingFilesAreSkippedWithoutRunningKeygen()
    {
        var called = false;
        Task<bool> Spy(string _)
        {
            called = true;
            return Task.FromResult(true);
        }

        Assert.False(await KeyCheck.IsUnencryptedAsync(Write("a", "not a key"), Spy));
        Assert.False(await KeyCheck.IsUnencryptedAsync(Path.Combine(dir, "missing"), Spy));
        Assert.False(called);
    }

    [Fact]
    public async Task WarningIsRaisedOncePerKeyFileAndNotForAgentMachines()
    {
        var path = Write("shared", OpenSsh("x"));
        var yaml = T.Yaml.Replace("os: windows\n", "os: windows\n    key_env: LANQ_KEY_B2\n", StringComparison.Ordinal);
        var config = LanQueue.Config.ConfigLoader.Parse(yaml);
        var warnings = await KeyCheck.WarningsAsync(config, _ => path, _ => Task.FromResult(true));
        Assert.Single(warnings);
        Assert.Contains("ssh-agent", warnings[0], StringComparison.Ordinal);

        var agentOnly = await KeyCheck.WarningsAsync(T.Config(), _ => null, _ => Task.FromResult(true));
        Assert.Empty(agentOnly);
    }
}
