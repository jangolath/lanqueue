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
    [InlineData("keys/id_dsa")]
    [InlineData(".netrc")]
    [InlineData("home/_netrc")]
    [InlineData(".envrc")]
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
    [InlineData(".git-credentials")]
    [InlineData("infra/terraform.tfstate")]
    [InlineData("infra/terraform.tfstate.backup")]
    [InlineData("config/secrets.json")]
    [InlineData("keys/id_ecdsa")]
    [InlineData("keys/id_dsa")]
    [InlineData(".netrc")]
    [InlineData("home/_netrc")]
    [InlineData(".envrc")]
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
    [InlineData(".npmrc", "//registry.npmjs.org/:_authToken=${NPM_TOKEN?}\n")] // npm's optional form: still just a reference
    public void PackageConfigsWithoutLiteralCredentialsPass(string path, string content) =>
        Assert.Empty(SecretScanner.ScanCommittedConfig(path, content));

    [Theory]
    [InlineData(".npmrc", "//registry.npmjs.org/:_authToken=npm_SuperSecretValue123", "_authToken")]
    [InlineData("web/.npmrc", "registry=https://r.example/\n_auth = dXNlcjpTdXBlclNlY3JldFZhbHVlMTIz\n", "_auth")]
    [InlineData(".npmrc", "//npm.acme.dev/:_password=\"SuperSecretValue123\"", "_password")]
    [InlineData(".npmrc", "password=SuperSecretValue123", "password")]
    [InlineData(".npmrc", "//r.example/:_authToken=${NPM_TOKEN:-SuperSecretValue123}", "_authToken")]
    [InlineData(".npmrc", "//r.example/:_authToken=${NPM_TOKEN}?SuperSecretValue123", "_authToken")]
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

    /// <summary>The uncommitted and committed name checks come from one list: a name one of them refuses, the other refuses too.</summary>
    [Theory]
    [InlineData(".env")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("id_ecdsa")]
    [InlineData("id_dsa")]
    [InlineData("secrets.json")]
    [InlineData("credentials.json")]
    [InlineData(".git-credentials")]
    [InlineData("terraform.tfstate")]
    [InlineData("terraform.tfstate.backup")]
    [InlineData(".netrc")]
    [InlineData("_netrc")]
    [InlineData(".envrc")]
    [InlineData("a.pem")]
    [InlineData("a.pfx")]
    [InlineData("a.p12")]
    [InlineData("a.key")]
    [InlineData("a.ppk")]
    [InlineData("a.kdbx")]
    [InlineData("a.tfvars")]
    [InlineData("a.jks")]
    [InlineData("a.keystore")]
    public void UncommittedAndCommittedNameChecksAgree(string name)
    {
        Assert.NotEmpty(SecretScanner.ScanFileNames([name]));
        Assert.NotEmpty(SecretScanner.ScanCommittedFileNames([name]));
    }

    [Fact]
    public void NpmrcWithOnlyCarriageReturnLineEndingsIsStillChecked()
    {
        var content = "registry=https://r.example/" + (char)13 + "//r.example/:_authToken=npm_SuperSecretValue123" + (char)13;
        Assert.Contains("key _authToken", Assert.Single(SecretScanner.ScanCommittedConfig(".npmrc", content)).Rule, StringComparison.Ordinal);
        Assert.Single(SecretScanner.ScanCommittedConfig(".pypirc", "[pypi]" + (char)13 + "password = SuperSecretValue123" + (char)13));
    }

    /// <summary>Test contents use '|' for a line break.</summary>
    private static string Yaml(string text) => text.Replace('|', (char)10);

    [Theory]
    [InlineData(".yarnrc.yml", "npmAuthToken: npm_SuperSecretValue123|")]
    [InlineData("web/.yarnrc.yml", "npmRegistryServer: 'https://r.example'|npmScopes:|  acme:|    npmAuthToken: 'SuperSecretValue123'|")]
    [InlineData(".yarnrc.yml", "npmAuthToken: ${NPM_TOKEN:-SuperSecretValue123}|")] // a default value is a literal
    [InlineData(".yarnrc.yml", "npmAuthToken: '${NPM_TOKEN}SuperSecretValue123'|")]
    [InlineData(".yarnrc.yml", "npmScopes: {acme: {npmAuthToken: SuperSecretValue123}}|")] // flow style
    [InlineData(".yarnrc.yml", "npmAuthToken: SuperSecretValue123 # ci|")] // a comment does not hide a literal
    [InlineData(".yarnrc.yml", "npmAuthToken: ${NPM_TOKEN?SuperSecretValue123}|")] // an error message is a literal too
    [InlineData(".yarnrc.yml", "npmAuthToken: SuperSecret#Value123|")] // '#' without whitespace before it is part of the value
    [InlineData(".yarnrc.yml", "npmScopes: {acme: {npmAlwaysAuth: true, npmAuthToken: '${NPM_TOKEN}'}}|")] // flow style, even with a reference
    [InlineData(".yarnrc.yml", "\"npmAuthToken\": \"SuperSecretValue123\"|")]
    [InlineData(".yarnrc.yml", "npmAuthToken:|  npm_SuperSecretValue123|")] // the value continues on a deeper line
    [InlineData(".yarnrc.yml", "npmAuthToken: # see CI|  npm_SuperSecretValue123|")]
    [InlineData(".yarnrc.yml", "npmScopes:|  acme:|    npmAuthToken:||      # a comment line between|      npm_SuperSecretValue123|")]
    [InlineData(".yarnrc.yml", "npmAuthToken: >|  npm_SuperSecretValue123|")] // block scalars (the literal '|' form is a Fact below)
    [InlineData(".yarnrc.yml", "npmAuthToken: >-|  npm_SuperSecretValue123|")]
    [InlineData(".yarnrc.yml", "npmAuthIdent:|\tdXNlcjpTdXBlclNlY3JldFZhbHVlMTIz|")]
    public void YarnrcWithALiteralNpmAuthTokenIsRefused(string path, string content)
    {
        Assert.True(SecretScanner.IsPackageConfig(path));
        var finding = Assert.Single(SecretScanner.ScanCommittedConfig(path, Yaml(content)));
        Assert.Contains(content.Contains("npmAuthIdent", StringComparison.Ordinal) ? "key npmAuthIdent" : "key npmAuthToken", finding.Rule, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecretValue123", finding.Rule + finding.Where, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("npmAuthToken: '${NPM_TOKEN}'|")]
    [InlineData("npmAuthToken: ${NPM_TOKEN}|")]
    [InlineData("npmRegistryServer: 'https://r.example'|# npmAuthToken: old-token-value|")]
    [InlineData("npmAuthToken: ''|")]
    [InlineData("npmAuthToken: ${NPM_TOKEN} # ci|")] // a YAML comment after the reference
    [InlineData("npmScopes:|  acme:|    npmAuthToken: '${NPM_TOKEN}'   # set in CI|")]
    [InlineData("npmAuthToken: ${NPM_TOKEN?}|")]
    [InlineData("npmScopes:|  acme:|    npmAuthToken:|    npmAlwaysAuth: true|")] // empty, then a sibling at the same level
    [InlineData("npmAuthToken:|npmRegistryServer: 'https://r.example'|")]
    [InlineData("npmAuthToken: # set in CI||# nothing more|")]
    public void YarnrcWithoutALiteralTokenPasses(string content) =>
        Assert.Empty(SecretScanner.ScanCommittedConfig(".yarnrc.yml", Yaml(content)));

    /// <summary>A double-quoted YAML key with an escape can spell npmAuthToken without containing it: any key with a backslash is refused.</summary>
    [Theory]
    [InlineData("\"npm\\x41uthToken\": npm_SuperSecretValue123\n")]
    [InlineData("\"npm\\u0041uthToken\": npm_SuperSecretValue123\n")]
    [InlineData("npmScopes:\n  acme:\n    \"npmAuth\\x54oken\": npm_SuperSecretValue123\n")]
    public void AYarnKeyWithAnEscapeIsRefused(string content)
    {
        var finding = Assert.Single(SecretScanner.ScanCommittedConfig(".yarnrc.yml", content));
        Assert.Contains("backslash", finding.Rule, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecretValue123", finding.Rule, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cacheFolder: 'C:\\yarn\\cache'\n")]
    [InlineData("npmRegistryServer: \"https://r.example/a\\\\b\"\nnpmAuthToken: ${NPM_TOKEN}\n")]
    public void ABackslashInAnOrdinaryYarnValueIsFine(string content) =>
        Assert.Empty(SecretScanner.ScanCommittedConfig(".yarnrc.yml", content));

    [Fact]
    public void AYarnLiteralBlockScalarTokenIsRefused()
    {
        var finding = Assert.Single(SecretScanner.ScanCommittedConfig(".yarnrc.yml", "npmAuthToken: |\n  npm_SuperSecretValue123\n"));
        Assert.Contains("key npmAuthToken", finding.Rule, StringComparison.Ordinal);
    }

    /// <summary>Flow style is refused even for a plain reference, and the refusal says why (and how to write it instead).</summary>
    [Fact]
    public void AFlowStyleYarnTokenIsRefusedWithAnExplanation()
    {
        var finding = Assert.Single(SecretScanner.ScanCommittedConfig(".yarnrc.yml", "npmScopes: {acme: {npmAuthToken: ${NPM_TOKEN}}}\n"));
        Assert.Contains("flow style", finding.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageConfigTooLargeToCheckIsRefused() =>
        Assert.Single(SecretScanner.ScanCommittedConfig(".npmrc", null));
}
