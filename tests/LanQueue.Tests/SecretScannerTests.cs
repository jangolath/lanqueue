using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

public class SecretScannerTests
{
    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("key = AKIAABCDEFGHIJKLMNOP")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("export K=sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("password = \"hunter2hunter2hunter2\"")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456")]
    public void ObviousSecretsAreFound(string text) =>
        Assert.NotEmpty(SecretScanner.ScanText(text, "x"));

    [Theory]
    [InlineData("var token = GetToken();")]
    [InlineData("password: string")]
    [InlineData("dotnet test --filter Category=Unit")]
    public void OrdinaryCodeIsNotFlagged(string text) =>
        Assert.Empty(SecretScanner.ScanText(text, "x"));

    [Fact]
    public void FindingsDoNotEchoTheSecret()
    {
        var f = SecretScanner.ScanText("AKIAABCDEFGHIJKLMNOP", "command line").Single();
        Assert.DoesNotContain("AKIA", f.Rule + f.Where, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("certs/server.pem")]
    [InlineData("home/.ssh/id_rsa")]
    [InlineData("machines.local.yml")]
    public void SecretFileNamesAreFound(string path) =>
        Assert.NotEmpty(SecretScanner.ScanFileNames([path]));

    [Theory]
    [InlineData(".env.example")]
    [InlineData("src/Program.cs")]
    public void NormalFileNamesPass(string path) =>
        Assert.Empty(SecretScanner.ScanFileNames([path]));
}
