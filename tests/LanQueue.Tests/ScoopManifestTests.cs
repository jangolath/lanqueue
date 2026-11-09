using System.Text.Json;
using Xunit;

namespace LanQueue.Tests;

/// <summary>scripts/scoop-manifest.sh builds the lanq.json Scoop manifest attached to each release from that release's SHA256SUMS.</summary>
public sealed class ScoopManifestTests : IDisposable
{
    private static readonly string X64 = new('a', 64);
    private static readonly string Arm64 = new('b', 64);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-scoop-" + Guid.NewGuid().ToString("N"));

    public ScoopManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Code, string Output) Run(string version, string sums)
    {
        var file = Path.Combine(_dir, "SHA256SUMS");
        File.WriteAllText(file, sums);
        var script = Path.Combine(T.RepoRoot, "scripts", "scoop-manifest.sh").Replace('\\', '/');
        return Shells.Exec(Shells.Sh!, [script, version, file.Replace('\\', '/')]);
    }

    private static string Sums(params string[] lines) => string.Join('\n', lines) + "\n";

    [ShFact]
    public void ManifestPinsTheVersionedUrlsAndHashesOfBothWindowsZips()
    {
        var (code, output) = Run("0.5.0", Sums(
            $"{X64}  lanq-win-x64.zip",
            $"{Arm64}  lanq-win-arm64.zip",
            $"{new string('c', 64)}  lanq-linux-x64.tar.gz",
            $"{new string('d', 64)}  LanQueue.0.5.0.nupkg"));
        Assert.True(code == 0, output);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("0.5.0", root.GetProperty("version").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());
        Assert.Equal("https://github.com/jangolath/lanqueue", root.GetProperty("homepage").GetString());
        Assert.Equal("lanq.exe", root.GetProperty("bin").GetString());
        var arch = root.GetProperty("architecture");
        Assert.Equal("https://github.com/jangolath/lanqueue/releases/download/v0.5.0/lanq-win-x64.zip", arch.GetProperty("64bit").GetProperty("url").GetString());
        Assert.Equal(X64, arch.GetProperty("64bit").GetProperty("hash").GetString());
        Assert.Equal("https://github.com/jangolath/lanqueue/releases/download/v0.5.0/lanq-win-arm64.zip", arch.GetProperty("arm64").GetProperty("url").GetString());
        Assert.Equal(Arm64, arch.GetProperty("arm64").GetProperty("hash").GetString());
        Assert.Equal(["64bit", "arm64"], arch.EnumerateObject().Select(p => p.Name));
    }

    [ShFact]
    public void BinaryModeSumsLinesAndPrereleaseVersionsWork()
    {
        var (code, output) = Run("1.2.3-rc.1", Sums($"{X64} *lanq-win-x64.zip", $"{Arm64} *lanq-win-arm64.zip"));
        Assert.True(code == 0, output);
        using var doc = JsonDocument.Parse(output);
        Assert.EndsWith("/v1.2.3-rc.1/lanq-win-arm64.zip", doc.RootElement.GetProperty("architecture").GetProperty("arm64").GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [ShFact]
    public void AZipThatIsMissingOrListedTwiceIsRefused()
    {
        var (code, output) = Run("0.5.0", Sums($"{X64}  lanq-win-x64.zip"));
        Assert.NotEqual(0, code);
        Assert.Contains("lanq-win-arm64.zip", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"version\"", output, StringComparison.Ordinal);

        (code, _) = Run("0.5.0", Sums($"{X64}  lanq-win-x64.zip", $"{X64}  lanq-win-x64.zip", $"{Arm64}  lanq-win-arm64.zip"));
        Assert.NotEqual(0, code);

        (code, _) = Run("0.5.0", Sums($"{X64}  lanq-win-x64.zip.sig", $"{Arm64}  lanq-win-arm64.zip")); // a name that only starts the same
        Assert.NotEqual(0, code);

        (code, _) = Run("0.5.0", Sums($"{X64[..63]}  lanq-win-x64.zip", $"{Arm64}  lanq-win-arm64.zip")); // not a sha256
        Assert.NotEqual(0, code);
    }

    [ShTheory]
    [InlineData("")]
    [InlineData("v0.5.0")]
    [InlineData("0.5")]
    [InlineData("0.5.0\"")]
    [InlineData("0.5.0/../x")]
    [InlineData("0.5.0 ")]
    public void AVersionThatIsNotPlainSemverIsRefused(string version)
    {
        var (code, output) = Run(version, Sums($"{X64}  lanq-win-x64.zip", $"{Arm64}  lanq-win-arm64.zip"));
        Assert.NotEqual(0, code);
        Assert.DoesNotContain("\"version\"", output, StringComparison.Ordinal);
    }
}
