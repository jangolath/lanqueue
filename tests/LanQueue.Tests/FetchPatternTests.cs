using LanQueue.Fetch;
using Xunit;

namespace LanQueue.Tests;

/// <summary>--fetch patterns and options: what is refused before anything runs, and what the wildcards mean.</summary>
public sealed class FetchPatternTests
{
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("c:foo")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("./x")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("-rf")]
    [InlineData("-")]
    [InlineData("a\nb")]
    [InlineData("a\u0000b")]
    [InlineData("a\u0085b")]
    [InlineData("a**b")]
    [InlineData("**x")]
    [InlineData("a/***/b")]
    [InlineData("$(id)")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a[b]")]
    [InlineData("a;b")]
    [InlineData("a`b")]
    [InlineData("")]
    public void UnsafePatternsAreRefused(string pattern)
    {
        Assert.NotNull(FetchPatterns.Problem(pattern));
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--fetch", pattern, "--", "dotnet", "test"]));
        Assert.Contains("--fetch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TestResults/**/*.trx")]
    [InlineData("**/coverage.cobertura.xml")]
    [InlineData("bin/Release/*.nupkg")]
    [InlineData("out/report.html")]
    [InlineData("*.log")]
    [InlineData("a b/c-d_e+f@g=h,i~j.?")]
    [InlineData("**")]
    [InlineData("dist/**")]
    [InlineData(".hidden/x")]
    public void SafePatternsAreAccepted(string pattern) => Assert.Null(FetchPatterns.Problem(pattern));

    [Fact]
    public void TooManyOrTooLongPatternsAreRefused()
    {
        Assert.NotNull(FetchPatterns.Problem(new string('a', 201)));
        var many = Enumerable.Range(0, 17).SelectMany(i => new[] { "--fetch", $"f{i}.txt" }).ToArray();
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", .. many, "--", "dotnet", "test"]));
    }

    [Theory]
    [InlineData("*.trx", "a.trx", true)]
    [InlineData("*.trx", "sub/a.trx", false)] // * stays within one name
    [InlineData("*.trx", "a.trxx", false)]
    [InlineData("*.trx", "aXtrx", false)] // the dot is literal
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("?.txt", "/.txt", false)]
    [InlineData("**/*.trx", "a.trx", true)] // ** is zero or more folders
    [InlineData("**/*.trx", "x/y/z/a.trx", true)]
    [InlineData("TestResults/**/*.trx", "TestResults/a.trx", true)]
    [InlineData("TestResults/**/*.trx", "TestResults/r1/x/a.trx", true)]
    [InlineData("TestResults/**/*.trx", "Other/a.trx", false)]
    [InlineData("dist/**", "dist/a/b.js", true)]
    [InlineData("dist/**", "dist", false)]
    [InlineData("a+b.txt", "a+b.txt", true)]
    [InlineData("a+b.txt", "aab.txt", false)] // + is literal
    [InlineData("out/report.html", "out/report.html", true)]
    [InlineData("out/report.html", "out/report.html.bak", false)]
    public void WildcardsMeanWhatTheySay(string pattern, string path, bool match)
    {
        Assert.Equal(match, FetchPatterns.Matcher([pattern], ignoreCase: false).IsMatch(path));
        Assert.Equal(match, System.Text.RegularExpressions.Regex.IsMatch(path, FetchPatterns.ToEre([pattern]), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TheGeneratedExpressionsHaveNoBackslashOrQuote()
    {
        string[] patterns = ["TestResults/**/*.trx", "a b/c-d_e+f@g=h,i~j.?", "dist/**"];
        foreach (var re in new[] { FetchPatterns.ToEre(patterns), FetchPatterns.ToDotNet(patterns).Replace(@"\z", "", StringComparison.Ordinal) })
        {
            Assert.DoesNotContain('\\', re);
            Assert.DoesNotContain('\'', re);
            Assert.DoesNotContain('"', re);
            Assert.DoesNotContain('$', re[..^1]);
        }
    }

    [Fact]
    public void WalkRootsAreTheLiteralLeadingFolders()
    {
        Assert.Equal(["."], FetchPatterns.WalkRoots(["*.log", "TestResults/*.trx"]));
        Assert.Equal(["."], FetchPatterns.WalkRoots(["**/x"]));
        Assert.Equal(["TestResults", "out"], FetchPatterns.WalkRoots(["TestResults/**/*.trx", "TestResults/sub/*.xml", "out/report.html"]));
        Assert.Equal(["a/b"], FetchPatterns.WalkRoots(["a/b/*/c.txt"]));
    }

    [Fact]
    public void FetchOptionsAreParsed()
    {
        var p = ParsedArgs.Parse(["run", "--fetch", "a/*.trx", "--fetch", "b.xml", "--fetch", "a/*.trx", "--fetch-dir", "out", "--fetch-max", "500M", "--fetch-max-files", "20", "--", "dotnet", "test"]);
        Assert.Equal(["a/*.trx", "b.xml"], p.Fetch);
        Assert.Equal("out", p.FetchDir);
        Assert.Equal(500L * 1024 * 1024, p.FetchMaxBytes);
        Assert.Equal(20, p.FetchMaxFiles);
        Assert.Equal(["dotnet", "test"], p.Argv);
        Assert.Empty(ParsedArgs.Parse(["run", "--", "dotnet", "test"]).Fetch);
    }

    [Theory]
    [InlineData("200M", 200L * 1024 * 1024)]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    [InlineData("2gb", 2L * 1024 * 1024 * 1024)]
    [InlineData("64k", 64L * 1024)]
    [InlineData("1048576", 1048576L)]
    public void FetchMaxSizes(string text, long bytes) => Assert.Equal(bytes, ParsedArgs.ParseSize(text));

    [Theory]
    [InlineData("3G")] // over the 2 GB ceiling
    [InlineData("999999999999G")] // would overflow
    [InlineData("100")] // under 1K
    [InlineData("-5M")]
    [InlineData("1.5G")]
    [InlineData("M")]
    [InlineData("")]
    public void FetchMaxHasAHardCeiling(string text) => Assert.Throws<ArgumentException>(() => ParsedArgs.ParseSize(text));

    [Theory]
    [InlineData("--fetch-dir", "out")]
    [InlineData("--fetch-max", "10M")]
    [InlineData("--fetch-max-files", "5")]
    public void FetchSettingsNeedAPattern(string option, string value) =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", option, value, "--", "dotnet", "test"]));

    [Theory]
    [InlineData("0")]
    [InlineData("5001")]
    [InlineData("x")]
    public void FetchMaxFilesIsBounded(string value) =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--fetch", "*.trx", "--fetch-max-files", value, "--", "dotnet", "test"]));

    [Fact]
    public void FetchIsARunOption() =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--fetch", "*.trx"]));

    [Fact]
    public void TheDefaultFolderIsDotLanqArtifactsInTheRepositoryAndIgnoredByGit()
    {
        var repo = Path.Combine(Path.GetTempPath(), "lanq-fetchroot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Directory.CreateDirectory(Path.Combine(repo, "src", "app"));
            var root = Cli.FetchRoot(null, Path.Combine(repo, "src", "app"));
            Assert.Equal(Path.Combine(Path.GetFullPath(repo), ".lanq", "artifacts"), root);
            Assert.Equal("*\n", File.ReadAllText(Path.Combine(repo, ".lanq", ".gitignore")));
            Assert.Equal(Path.GetFullPath("elsewhere"), Cli.FetchRoot("elsewhere", repo));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
