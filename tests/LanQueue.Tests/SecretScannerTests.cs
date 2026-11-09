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
    [InlineData("DB_PASS=Sup3rS3cret")]
    [InlineData("passphrase=correct-horse-battery")]
    [InlineData("pass: hunter2hunter2")]
    [InlineData("{ \"DB_PASS\": \"hunter2hunter2\" }")]
    [InlineData("url = postgres://app:Sup3rS3cret@db.internal/app")]
    [InlineData("git clone https://bob:ghpass1234@example.com/x.git")]
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
    [InlineData("bypass = something_long_here")]
    [InlineData("PasswordAuthentication=no")]
    [InlineData("pass = request.Value;")]
    [InlineData("passthrough=enabled-for-all")]
    [InlineData("see http://localhost:8080/health and https://example.com/a")]
    [InlineData("url = postgres://app:${DB_PASSWORD}@db/app")]
    [InlineData("git@github.com:owner/repo.git ssh://git@host:22/repo")]
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
    [InlineData(".npmrc")]
    [InlineData("tools/.pypirc")]
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

    public static TheoryData<string, string> Hostile => new()
    {
        { "blank lines", new string((char)10, 200_000) },
        { "whitespace lines", string.Concat(Enumerable.Repeat(" " + (char)9 + " " + (char)10, 70_000)) },
        { "dashes", new string('-', 200_000) },
        { "repeated secret", string.Concat(Enumerable.Repeat("secret", 40_000)) },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void LargeHostileInputScansQuicklyWithNoTimeoutAndNoFinding(string label, string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = SecretScanner.ScanText(text, label);
        sw.Stop();
        Assert.Empty(found);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{label}: {sw.Elapsed}");
    }
    [Theory]
    [InlineData(".env")]
    [InlineData("app/.env")]
    [InlineData(".env.production")]
    [InlineData("deploy/.ENV.local")]
    [InlineData("id_rsa")]
    [InlineData("home/.ssh/id_ed25519")]
    [InlineData("certs/server.pem")]
    [InlineData("signing/app.pfx")]
    [InlineData("signing/app.P12")]
    [InlineData("tls/server.key")]
    [InlineData("credentials.json")]
    [InlineData("vault/passwords.kdbx")]
    [InlineData("fixturesX/server.pem")]
    public void CommittedSecretLookingNamesAreFound(string path)
    {
        var finding = Assert.Single(SecretScanner.ScanCommittedFileNames([path]));
        Assert.Equal(SecretScanner.CommittedFileRule, finding.Rule);
        Assert.Equal(path, finding.Where);
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData("app/.env.sample")]
    [InlineData(".env.local.example")]
    [InlineData("config/server.example.pem")]
    [InlineData("tests/fixtures/server.pem")]
    [InlineData("pkg/testdata/id_rsa")]
    [InlineData("tests/Fixtures/.env")]
    [InlineData("src/environment.ts")]
    [InlineData("docs/keys.md")]
    [InlineData("src/keyboard.cs")]
    [InlineData("monkey")]
    [InlineData(".envrc")]
    [InlineData("package.json")]
    [InlineData("web/.npmrc")]
    [InlineData(".pypirc")]
    public void SamplesFixturesAndOrdinaryNamesAreNotFound(string path) =>
        Assert.Empty(SecretScanner.ScanCommittedFileNames([path]));

    [Theory]
    [InlineData(".npmrc", "registry=https://registry.example.com/\n@acme:registry=https://npm.acme.dev/\nalways-auth=true\n")]
    [InlineData(".npmrc", "//registry.npmjs.org/:_authToken=${NPM_TOKEN}\n")]
    [InlineData(".npmrc", "_authToken = \"${NPM_TOKEN}\"\n")]
    [InlineData(".npmrc", "//npm.acme.dev/:_authToken=\n; _authToken=commented-out-token\n# password=commented\n")]
    [InlineData("tools/.pypirc", "[distutils]\nindex-servers = pypi\n\n[pypi]\nusername = __token__\n")]
    [InlineData(".pypirc", "[pypi]\nusername = me\npassword =\n")]
    public void PackageConfigsWithoutLiteralCredentialsPass(string path, string content) =>
        Assert.Empty(SecretScanner.ScanCommittedConfig(path, content));

    [Theory]
    [InlineData(".npmrc", "//registry.npmjs.org/:_authToken=npm_SuperSecretValue123", "_authToken")]
    [InlineData("web/.npmrc", "registry=https://r.example/\n_auth = dXNlcjpTdXBlclNlY3JldFZhbHVlMTIz\n", "_auth")]
    [InlineData(".npmrc", "//npm.acme.dev/:_password=\"SuperSecretValue123\"", "_password")]
    [InlineData(".npmrc", "password=SuperSecretValue123", "password")]
    [InlineData(".pypirc", "[pypi]\nusername = __token__\npassword = pypi-SuperSecretValue123\n", "password")]
    [InlineData(".pypirc", "[testpypi]\r\nPassword: SuperSecretValue123\r\n", "password")]
    public void PackageConfigsWithALiteralCredentialAreRefusedWithoutEchoingIt(string path, string content, string key)
    {
        var finding = Assert.Single(SecretScanner.ScanCommittedConfig(path, content));
        Assert.Equal(path, finding.Where);
        Assert.Contains("key " + key, finding.Rule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SuperSecretValue123", finding.Rule + finding.Where, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcj", finding.Rule + finding.Where, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageConfigTooLargeToCheckIsRefused() =>
        Assert.Single(SecretScanner.ScanCommittedConfig(".npmrc", null));
}
