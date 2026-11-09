using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The release workflow's logic lives in small POSIX sh scripts so it can be tested here: the version check and classification, the
/// runtime-pin guard, and the draft/publish steps (against a fake gh that records its calls).
/// </summary>
public sealed class ReleaseScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-release-" + Guid.NewGuid().ToString("N"));

    public ReleaseScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Script(string name) => Path.Combine(T.RepoRoot, "scripts", name).Replace('\\', '/');

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path.Replace('\\', '/');
    }

    // ---- scripts/release-version.sh <ref name> <is tag> <run number>

    [ShTheory]
    [InlineData("v0.5.0", "true", "7", "0.5.0", "false")]
    [InlineData("v1.20.3", "true", "7", "1.20.3", "false")]
    [InlineData("v0.5.0-rc.1", "true", "7", "0.5.0-rc.1", "true")]
    [InlineData("v0.5.0-beta-2", "true", "7", "0.5.0-beta-2", "true")]
    [InlineData("main", "false", "42", "0.0.0-dryrun.42", "true")]
    [InlineData("v0.5.0", "false", "42", "0.0.0-dryrun.42", "true")] // a manual run is never a real version, whatever its ref
    public void VersionIsDerivedAndClassified(string refName, string isTag, string run, string version, string prerelease)
    {
        var (code, output) = Shells.Exec(Shells.Sh!, [Script("release-version.sh"), refName, isTag, run]);
        Assert.True(code == 0, output);
        Assert.Equal($"version={version}\nprerelease={prerelease}\n", output.Replace("\r", "", StringComparison.Ordinal));
    }

    [ShTheory]
    [InlineData("0.5.0", "true", "7")] // no v
    [InlineData("v0.5", "true", "7")]
    [InlineData("v01.5.0", "true", "7")] // leading zero
    [InlineData("v0.5.0+build.1", "true", "7")] // build metadata: refused (it would end up in URLs)
    [InlineData("v0.5.0-", "true", "7")]
    [InlineData("v0.5.0-rc..1", "true", "7")]
    [InlineData("v0.5.0\";curl x|sh;\"", "true", "7")]
    [InlineData("v0.5.0 ", "true", "7")]
    [InlineData("v0.5.0/x", "true", "7")]
    [InlineData("main", "false", "4x")]
    [InlineData("main", "maybe", "4")]
    public void AnythingButStrictSemverIsRefused(string refName, string isTag, string run)
    {
        var (code, output) = Shells.Exec(Shells.Sh!, [Script("release-version.sh"), refName, isTag, run]);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain("version=", output, StringComparison.Ordinal);
    }

    // ---- scripts/check-runtime-pin.sh installed|latest <input file> <csproj>

    private string Project(string pin) =>
        Write("p.csproj", $"<Project>\n  <PropertyGroup>\n    <LanqRuntimePackVersion>{pin}</LanqRuntimePackVersion>\n  </PropertyGroup>\n</Project>\n");

    private const string Runtimes = """
        Microsoft.AspNetCore.App 9.0.11 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 8.0.22 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
        Microsoft.NETCore.App 10.0.2 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
        Microsoft.NETCore.App {0} [/usr/share/dotnet/shared/Microsoft.NETCore.App]
        Microsoft.NETCore.App 11.0.0-preview.3.26201.1 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
        """;

    [ShTheory]
    [InlineData("10.0.14", "10.0.14", true)]
    [InlineData("10.0.14", "10.0.9", true)] // older installed runtime: the packs are pinned anyway
    [InlineData("10.0.14", "10.0.15", false)]
    [InlineData("10.0.9", "10.0.10", false)] // numeric, not string, comparison
    public void InstalledRuntimeNewerThanThePinFails(string pin, string installed, bool ok)
    {
        var (code, output) = Shells.Exec(Shells.Sh!, [Script("check-runtime-pin.sh"), "installed", Write("rt.txt", string.Format(System.Globalization.CultureInfo.InvariantCulture, Runtimes, installed)), Project(pin)]);
        Assert.True(ok == (code == 0), output);
        if (!ok)
        {
            Assert.Contains($"bump LanqRuntimePackVersion from {pin} to {installed}", output, StringComparison.Ordinal);
        }
    }

    [ShTheory]
    [InlineData("10.0.14", "10.0.14", true)]
    [InlineData("10.0.14", "10.0.15", false)]
    [InlineData("10.0.9", "10.0.10", false)]
    public void LatestReleaseNewerThanThePinFails(string pin, string latest, bool ok)
    {
        var json = $"{{\n  \"channel-version\": \"10.0\",\n  \"latest-release\": \"{latest}\",\n  \"latest-runtime\": \"{latest}\",\n  \"latest-sdk\": \"10.0.403\",\n  \"releases\": []\n}}\n";
        var (code, output) = Shells.Exec(Shells.Sh!, [Script("check-runtime-pin.sh"), "latest", Write("releases.json", json), Project(pin)]);
        Assert.True(ok == (code == 0), output);
        if (!ok)
        {
            Assert.Contains($"bump LanqRuntimePackVersion from {pin} to {latest}", output, StringComparison.Ordinal);
        }
    }

    [ShTheory]
    [InlineData("installed", "Microsoft.AspNetCore.App 10.0.14 [x]\n", "10.0.14")] // no Microsoft.NETCore.App 10.0 at all
    [InlineData("latest", "{ \"releases\": [] }\n", "10.0.14")] // no latest-runtime
    [InlineData("installed", Runtimes, "10.0")] // a pin that is not 10.0.N
    [InlineData("installed", Runtimes, "10.0.14; rm -rf /")]
    [InlineData("other", Runtimes, "10.0.14")]
    public void UnreadableInputsFailClosed(string mode, string input, string pin)
    {
        var (code, output) = Shells.Exec(Shells.Sh!, [Script("check-runtime-pin.sh"), mode, Write("in.txt", input.Replace("{0}", "10.0.14", StringComparison.Ordinal)), Project(pin)]);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain("OK", output, StringComparison.Ordinal);
    }

    [ShFact]
    public void TheRealProjectHasAReadablePin()
    {
        var (code, output) = Shells.Exec(Shells.Sh!,
            [Script("check-runtime-pin.sh"), "installed", Write("rt.txt", "Microsoft.NETCore.App 10.0.0 [x]\n"), Path.Combine(T.RepoRoot, "src", "LanQueue", "LanQueue.csproj").Replace('\\', '/')]);
        Assert.True(code == 0, output);
    }

    // ---- scripts/release-draft.sh draft|publish <tag> <prerelease> [<assets dir>], with a fake gh

    /// <summary>
    /// Runs release-draft.sh with a fake gh. <paramref name="state"/> is what "gh release view" reports: "none" (no release), "draft",
    /// "draft-empty" (a draft with no assets) or "published"; <paramref name="draftSums"/> is the SHA256SUMS the existing draft holds
    /// (null: none). Returns the exit code, the output and the gh calls made.
    /// </summary>
    private (int Code, string Output, string[] Calls) Draft(string mode, string tag, string prerelease, string state, string? draftSums, string sums = "aa  lanq-win-x64.zip\n")
    {
        var assets = Path.Combine(_dir, "release");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "SHA256SUMS"), sums);
        File.WriteAllText(Path.Combine(assets, "lanq-win-x64.zip"), "zip");
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        var log = Write("calls.txt", "");
        if (draftSums is not null)
        {
            Write("draft-sums", draftSums);
        }

        var gh = Path.Combine(bin, "gh");
        File.WriteAllText(gh, string.Join('\n',
            "#!/bin/sh",
            "echo \"$*\" >> \"$FAKE_LOG\"",
            "case \"$1 $2 $*\" in",
            "  'release view'*isDraft*) case \"$FAKE_STATE\" in none) echo 'release not found' >&2; exit 1 ;; published) echo false ;; *) echo true ;; esac ;;",
            "  'release view'*assets*) case \"$FAKE_STATE\" in draft-empty) ;; *) echo SHA256SUMS; echo lanq-win-x64.zip ;; esac ;;",
            "  'release download'*) [ -f \"$FAKE_DIR/draft-sums\" ] || exit 1; dir=.; while [ $# -gt 0 ]; do [ \"$1\" = -D ] && dir=$2; shift; done; cp \"$FAKE_DIR/draft-sums\" \"$dir/SHA256SUMS\" ;;",
            "  *) ;;",
            "esac",
            ""));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var env = new Dictionary<string, string>
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_LOG"] = log,
            ["FAKE_STATE"] = state,
            ["FAKE_DIR"] = _dir.Replace('\\', '/'),
        };
        string[] args = mode == "draft" ? [Script("release-draft.sh"), mode, tag, prerelease, assets.Replace('\\', '/')] : [Script("release-draft.sh"), mode, tag, prerelease];
        var (code, output) = Shells.Exec(Shells.Sh!, args, env);
        return (code, output, File.ReadAllLines(log).Where(l => l.Length > 0).ToArray());
    }

    [ShFact]
    public void ANewStableReleaseIsCreatedAsADraft()
    {
        var (code, output, calls) = Draft("draft", "v0.5.0", "false", "none", null);
        Assert.True(code == 0, output);
        var create = Assert.Single(calls, c => c.StartsWith("release create", StringComparison.Ordinal));
        Assert.Contains("v0.5.0", create, StringComparison.Ordinal);
        Assert.Contains("--draft", create, StringComparison.Ordinal);
        Assert.Contains("--verify-tag", create, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", create, StringComparison.Ordinal);
        Assert.DoesNotContain("--prerelease", create, StringComparison.Ordinal);
    }

    [ShFact]
    public void APrereleaseIsNeverMarkedLatest()
    {
        var (code, output, calls) = Draft("draft", "v0.5.0-rc.1", "true", "none", null);
        Assert.True(code == 0, output);
        var create = Assert.Single(calls, c => c.StartsWith("release create", StringComparison.Ordinal));
        Assert.Contains("--prerelease", create, StringComparison.Ordinal);
        Assert.Contains("--latest=false", create, StringComparison.Ordinal);

        (code, output, calls) = Draft("publish", "v0.5.0-rc.1", "true", "draft", null);
        Assert.True(code == 0, output);
        var edit = Assert.Single(calls, c => c.StartsWith("release edit", StringComparison.Ordinal));
        Assert.Contains("--draft=false", edit, StringComparison.Ordinal);
        Assert.Contains("--latest=false", edit, StringComparison.Ordinal);
    }

    [ShFact]
    public void AStableReleaseIsPublishedAsLatest()
    {
        var (code, output, calls) = Draft("publish", "v0.5.0", "false", "draft", null);
        Assert.True(code == 0, output);
        var edit = Assert.Single(calls, c => c.StartsWith("release edit", StringComparison.Ordinal));
        Assert.Contains("--draft=false", edit, StringComparison.Ordinal);
        Assert.Contains("--latest", edit, StringComparison.Ordinal);
        Assert.DoesNotContain("--latest=false", edit, StringComparison.Ordinal);
    }

    [ShFact]
    public void ARerunReusesItsOwnDraftOnlyWhenTheSumsMatch()
    {
        var (code, output, calls) = Draft("draft", "v0.5.0", "false", "draft", "aa  lanq-win-x64.zip\n");
        Assert.True(code == 0, output);
        Assert.Contains(calls, c => c.StartsWith("release upload", StringComparison.Ordinal) && c.Contains("--clobber", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("release create", StringComparison.Ordinal));

        (code, output, calls) = Draft("draft", "v0.5.0", "false", "draft", "bb  lanq-win-x64.zip\n"); // a draft from another build
        Assert.NotEqual(0, code);
        Assert.Contains("SHA256SUMS", output, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, c => c.StartsWith("release upload", StringComparison.Ordinal));

        (code, _, calls) = Draft("draft", "v0.5.0", "false", "draft", null); // assets but no SHA256SUMS: cannot tell, refuse
        Assert.NotEqual(0, code);
        Assert.DoesNotContain(calls, c => c.StartsWith("release upload", StringComparison.Ordinal));

        (code, output, calls) = Draft("draft", "v0.5.0", "false", "draft-empty", null); // an empty draft: nothing to protect
        Assert.True(code == 0, output);
        Assert.Contains(calls, c => c.StartsWith("release upload", StringComparison.Ordinal));
    }

    [ShFact]
    public void APublishedReleaseIsNeverTouched()
    {
        var (code, output, calls) = Draft("draft", "v0.5.0", "false", "published", "aa  lanq-win-x64.zip\n");
        Assert.NotEqual(0, code);
        Assert.Contains("already published", output, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, c => c.StartsWith("release upload", StringComparison.Ordinal) || c.StartsWith("release create", StringComparison.Ordinal));
    }

    [ShTheory]
    [InlineData("v0.5.0", "maybe")]
    [InlineData("v0.5.0-rc.1", "false")] // the flag must agree with the tag
    [InlineData("v0.5.0", "true")]
    [InlineData("0.5.0", "false")]
    public void BadArgumentsAreRefusedBeforeCallingGh(string tag, string prerelease)
    {
        var (code, _, calls) = Draft("draft", tag, prerelease, "none", null);
        Assert.NotEqual(0, code);
        Assert.Empty(calls);
    }
}
