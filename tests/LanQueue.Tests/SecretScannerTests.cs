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
    [InlineData("{ \"Password\": \"hunter2hunter2\" }")]
    [InlineData("{\"ConnectionStrings\":{\"Db\":\"Server=db;User Id=sa;Password=Sup3rS3cret;Encrypt=true\"}}")]
    [InlineData("Server=db;Database=app;Password=Sup3rS3cret")]
    [InlineData("Server=db;Database=app;Pwd=Sup3rS3cret;")]
    [InlineData("db:\n  password: hunter2hunter2")]
    [InlineData("+  password: hunter2hunter2")]
    [InlineData("DB_PASSWORD=Sup3rS3cret!")]
    [InlineData("password = \"p@ss;w0rd!#$%^&*\"")]
    [InlineData("client_secret: 'abc!def?ghi#jkl'")]
    public void ObviousSecretsAreFound(string text) =>
        Assert.NotEmpty(SecretScanner.ScanText(text, "x"));

    [Theory]
    [InlineData("var token = GetToken();")]
    [InlineData("password: string")]
    [InlineData("password = request.Password;")]
    [InlineData("password: ${DB_PASSWORD}")]
    [InlineData("var password = GetPassword();")]
    [InlineData("Server=db;Database=app;Integrated Security=true")]
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
    [InlineData("home/.git-credentials")]
    [InlineData("infra/prod.tfvars")]
    [InlineData("infra/x.auto.tfvars")]
    [InlineData("keys/release.jks")]
    [InlineData("infra/terraform.tfstate")]
    [InlineData("infra/terraform.tfstate.backup")]
    [InlineData("home/.aws/credentials")]
    public void SecretFileNamesAreFound(string path) =>
        Assert.NotEmpty(SecretScanner.ScanFileNames([path]));

    [Theory]
    [InlineData(".env.example")]
    [InlineData("src/Program.cs")]
    public void NormalFileNamesPass(string path) =>
        Assert.Empty(SecretScanner.ScanFileNames([path]));

    [Fact]
    public void RegexTimeoutFailsClosedWithAClearRuleAndNoValue()
    {
        var slow = new System.Text.RegularExpressions.Regex("(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var hostile = new string('a', 60) + "!";
        var f = SecretScanner.ScanText(hostile, "uncommitted changes", [("slow", slow)]).Single();
        Assert.Equal(SecretScanner.TimeoutRule, f.Rule);
        Assert.Contains("refusing", f.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRuleHasNoCatastrophicBacktrackingOnLongHostileInput()
    {
        var hostile = string.Concat(Enumerable.Repeat("password" + new string(' ', 200) + "=" + new string('x', 5000) + "\n", 20));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = SecretScanner.ScanText(hostile, "x");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}
