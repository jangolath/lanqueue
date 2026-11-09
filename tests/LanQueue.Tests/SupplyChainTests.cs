using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace LanQueue.Tests;

/// <summary>Guards for the repository's own supply chain: locked restores, pinned actions, scoped release permissions.</summary>
public sealed partial class SupplyChainTests
{
    private static readonly string Root = FindRoot();

    [GeneratedRegex(@"^\s*-?\s*uses:\s*(?<ref>\S+)", RegexOptions.Multiline | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Uses();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$", RegexOptions.None, 1000)]
    private static partial Regex PinnedAction();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LanQueue.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (LanQueue.slnx) not found.");
    }

    [Fact]
    public void EveryProjectHasACommittedLockFileAndCiRestoresInLockedMode()
    {
        var projects = Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .ToList();
        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), "missing packages.lock.json for " + project);
        }

        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Matches(@"<RestoreLockedMode Condition=""[^""]*'\$\(CI\)' == 'true'[^""]*"">true</RestoreLockedMode>", props);
    }

    [Fact]
    public void EveryWorkflowActionIsPinnedToAFullCommitSha()
    {
        var files = Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            foreach (Match m in Uses().Matches(File.ReadAllText(file)))
            {
                Assert.True(PinnedAction().IsMatch(m.Groups["ref"].Value), $"{Path.GetFileName(file)}: '{m.Groups["ref"].Value}' is not pinned to a commit sha");
            }
        }
    }

    [Fact]
    public void ReleaseAttestsThePackageWithWritePermissionsOnlyInTheAttestJob()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"))));
        var doc = (YamlMappingNode)yaml.Documents[0].RootNode;
        Assert.Equal("read", Scalar(Map(doc, "permissions"), "contents"));
        Assert.Null(Scalar(Map(doc, "permissions"), "id-token"));

        var jobs = Map(doc, "jobs");
        var attest = Map(jobs, "attest");
        Assert.Equal("write", Scalar(Map(attest, "permissions"), "id-token"));
        Assert.Equal("write", Scalar(Map(attest, "permissions"), "attestations"));
        var steps = ((YamlSequenceNode)attest.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
        var step = Assert.Single(steps, s => Scalar(s, "uses")?.StartsWith("actions/attest-build-provenance@", StringComparison.Ordinal) == true);
        Assert.Contains(".nupkg", Scalar(Map(step, "with"), "subject-path"), StringComparison.Ordinal);

        foreach (var (name, job) in jobs.Children.Select(c => (((YamlScalarNode)c.Key).Value, (YamlMappingNode)c.Value)))
        {
            if (name != "attest")
            {
                Assert.Null(Scalar(Map(job, "permissions"), "attestations"));
            }
        }

        Assert.Contains("attest", ((YamlSequenceNode)Map(jobs, "publish").Children[new YamlScalarNode("needs")]).Select(n => ((YamlScalarNode)n).Value));
    }

    private static YamlMappingNode Workflow(string name)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "workflows", name))));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static List<string> Needs(YamlMappingNode job) =>
        job.Children.TryGetValue(new YamlScalarNode("needs"), out var n)
            ? n is YamlSequenceNode s ? s.Select(x => ((YamlScalarNode)x).Value!).ToList() : [((YamlScalarNode)n).Value!]
            : [];

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void EveryJobHasATimeout(string workflow)
    {
        foreach (var (name, job) in Map(Workflow(workflow), "jobs").Children.Select(c => (((YamlScalarNode)c.Key).Value, (YamlMappingNode)c.Value)))
        {
            Assert.True(int.TryParse(Scalar(job, "timeout-minutes"), out var t) && t is > 0 and <= 30, $"{workflow}: job {name} needs a timeout-minutes of at most 30");
        }
    }

    [Fact]
    public void ReleasePublishesNothingUntilEveryNativeBinaryIsBuiltSmokedAndAttested()
    {
        var jobs = Map(Workflow("release.yml"), "jobs");
        Assert.Contains("pack", Needs(Map(jobs, "native")));
        Assert.Equal(["native", "pack"], Needs(Map(jobs, "smoke")).Order(StringComparer.Ordinal));
        Assert.Equal(["native", "pack", "smoke"], Needs(Map(jobs, "assemble")).Order(StringComparer.Ordinal));
        Assert.Contains("assemble", Needs(Map(jobs, "attest")));
        Assert.Contains("assemble", Needs(Map(jobs, "publish")));
        Assert.Contains("attest", Needs(Map(jobs, "publish")));

        // A manual (dry) run builds everything above and publishes nothing.
        foreach (var name in new[] { "pack", "native", "smoke", "assemble" })
        {
            Assert.Null(Scalar(Map(jobs, name), "if"));
        }

        foreach (var name in new[] { "attest", "publish" })
        {
            Assert.Equal("startsWith(github.ref, 'refs/tags/v') && github.event_name == 'push'", Scalar(Map(jobs, name), "if"));
        }

        // Only publish (behind the nuget-release environment) writes; the release is created as a draft and made public last.
        Assert.Equal("nuget-release", Scalar(Map(jobs, "publish"), "environment"));
        foreach (var (name, job) in jobs.Children.Select(c => (((YamlScalarNode)c.Key).Value, (YamlMappingNode)c.Value)).Where(j => j.Value != "publish"))
        {
            Assert.True(Scalar(Map(job, "permissions"), "contents") is null or "read", $"job {name} must not write contents");
        }

        var publish = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        var draft = publish.IndexOf("--draft --title", StringComparison.Ordinal);
        var push = publish.IndexOf("dotnet nuget push", StringComparison.Ordinal);
        var undraft = publish.IndexOf("--draft=false", StringComparison.Ordinal);
        Assert.True(draft > 0 && push > draft && undraft > push, "publish must draft the release, then push to nuget.org, then make the release public");

        // The attestation covers every asset users download, not only the package.
        var subjects = Scalar(Map(((YamlSequenceNode)Map(jobs, "attest").Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>()
            .Single(s => Scalar(s, "uses")?.StartsWith("actions/attest-build-provenance@", StringComparison.Ordinal) == true), "with"), "subject-path");
        foreach (var subject in new[] { "*.nupkg", "*.zip", "*.tar.gz", "lanq.json", "SHA256SUMS" })
        {
            Assert.Contains(subject, subjects, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LinuxBinariesAreLinkedAgainstAnOldGlibcSysrootInAPinnedImageAndSmokedOnUbuntu1804()
    {
        var jobs = Map(Workflow("release.yml"), "jobs");
        var targets = ((YamlSequenceNode)Map(Map(Map(jobs, "native"), "strategy"), "matrix").Children[new YamlScalarNode("include")]).Cast<YamlMappingNode>().ToList();
        Assert.Equal(["linux-arm64", "linux-x64", "osx-arm64", "win-arm64", "win-x64"], targets.Select(t => Scalar(t, "rid")).Order(StringComparer.Ordinal));
        foreach (var linux in targets.Where(t => Scalar(t, "rid")!.StartsWith("linux-", StringComparison.Ordinal)))
        {
            Assert.Matches(@"^mcr\.microsoft\.com/dotnet-buildtools/prereqs:azurelinux-[0-9.]+-net10\.0-cross-(amd|arm)64@sha256:[0-9a-f]{64}$", Scalar(linux, "container"));
            Assert.Contains("-p:SysRoot=/crossrootfs/", Scalar(linux, "publish-args"), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(targets, t => Scalar(t, "runner")!.Contains("latest", StringComparison.Ordinal));
        Assert.Matches(@"^ubuntu:18\.04@sha256:[0-9a-f]{64}$", Scalar(Map(Map(jobs, "smoke"), "env"), "OLD_DISTRO"));
    }

    [Fact]
    public void CiPublishesAndSmokesANativeBinaryOnEveryPullRequest()
    {
        var job = Map(Map(Workflow("ci.yml"), "jobs"), "aot-linux");
        var runs = string.Join('\n', ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().Select(s => Scalar(s, "run") ?? ""));
        Assert.Contains("-p:LanqNative=true", runs, StringComparison.Ordinal);
        Assert.Contains("scripts/smoke-native.sh", runs, StringComparison.Ordinal);
    }

    /// <summary>The NuGet tool stays framework-dependent: NativeAOT is switched on only by -p:LanqNative=true on dotnet publish.</summary>
    [Fact]
    public void NativeAotIsPublishOnlyAndTheProjectIsAotCompatible()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(Root, "src", "LanQueue", "LanQueue.csproj"));
        Assert.Equal("true", project.Descendants("IsAotCompatible").Single().Value);
        foreach (var name in new[] { "PublishAot", "RuntimeFrameworkVersion", "RuntimeIdentifiers", "NuGetLockFilePath", "StripSymbols" })
        {
            var element = project.Descendants(name).Single();
            Assert.Equal("'$(LanqNative)' == 'true'", element.Parent!.Attribute("Condition")?.Value);
        }

        Assert.True(File.Exists(Path.Combine(Root, "src", "LanQueue", "packages.native.lock.json")), "the native publish restores in locked mode too");
    }

    [Fact]
    public void DependabotWatchesActionsAndNuGetWeeklyInGroups()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "dependabot.yml"))));
        var updates = ((YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("updates")]).Cast<YamlMappingNode>().ToList();
        Assert.Equal(["github-actions", "nuget"], updates.Select(u => Scalar(u, "package-ecosystem")).Order(StringComparer.Ordinal));
        Assert.All(updates, u =>
        {
            Assert.Equal("weekly", Scalar(Map(u, "schedule"), "interval"));
            Assert.NotEmpty(Map(u, "groups").Children);
        });
    }

    /// <summary>
    /// Runs a copy of scripts/sync-makeready.sh in a scratch tree with a fake gh (which "downloads" two scripts and the given SHA256SUMS and
    /// accepts every attestation), so only the SHA256SUMS check decides. Returns the exit code, output and whether the scripts were copied.
    /// </summary>
    private static (int Code, string Output, bool Copied) RunSync(Func<string, string, string> sums)
    {
        var tree = Path.Combine(Path.GetTempPath(), "lanq-sync-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(tree, "fake-bin");
        var dest = Path.Combine(tree, "src", "LanQueue", "Makeready");
        Directory.CreateDirectory(Path.Combine(tree, "scripts"));
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(dest);
        try
        {
            File.Copy(Path.Combine(Root, "scripts", "sync-makeready.sh"), Path.Combine(tree, "scripts", "sync-makeready.sh"));
            static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var sumsFile = Path.Combine(tree, "sums.txt");
            File.WriteAllText(sumsFile, sums(Sha("ps1\n"), Sha("sh\n")));
            var gh = Path.Combine(bin, "gh");
            File.WriteAllText(gh, string.Join('\n',
                "#!/bin/sh",
                "case \"$1 $2\" in",
                "  'release download') printf 'ps1\\n' > lanq-makeready.ps1; printf 'sh\\n' > lanq-makeready.sh; cp \"$FAKE_SUMS\" SHA256SUMS ;;",
                "  'attestation verify') exit 0 ;;",
                "  *) exit 9 ;;",
                "esac",
                ""));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var env = new Dictionary<string, string>
            {
                ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["FAKE_SUMS"] = sumsFile.Replace('\\', '/'),
            };
            var (code, output) = Shells.Exec(Shells.Sh!, [Path.Combine(tree, "scripts", "sync-makeready.sh").Replace('\\', '/'), "v0.1.0"], env);
            return (code, output, File.Exists(Path.Combine(dest, "lanq-makeready.ps1")));
        }
        finally
        {
            Directory.Delete(tree, recursive: true);
        }
    }

    [ShFact]
    public void SyncMakereadyNeedsExactlyOneSumsLinePerScript()
    {
        var (code, output, copied) = RunSync((ps1, _) => $"{ps1}  lanq-makeready.ps1\n{ps1}  lanq-makeready.ps1\n"); // two lines, but not one of each
        Assert.NotEqual(0, code);
        Assert.Contains("exactly once each", output, StringComparison.Ordinal);
        Assert.False(copied);

        (code, output, copied) = RunSync((ps1, sh) => $"{ps1}  lanq-makeready.ps1\n{sh}  lanq-makeready.sh\n{sh}  lanq-makeready.sh\n");
        Assert.NotEqual(0, code);
        Assert.False(copied);

        (code, output, copied) = RunSync((ps1, sh) => $"{ps1}  lanq-makeready.ps1\n{sh} *lanq-makeready.sh\n0000000000000000000000000000000000000000000000000000000000000000  other.zip\n");
        Assert.True(code == 0, output);
        Assert.True(copied);
    }

    [Fact]
    public void SyncMakereadyExitsNonZeroWhenInterrupted()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "sync-makeready.sh"));
        Assert.Contains("trap 'rm -rf \"$work\"' EXIT", script, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -rf \"$work\"; exit 130' INT", script, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -rf \"$work\"; exit 143' TERM", script, StringComparison.Ordinal);
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlMappingNode m ? m : new YamlMappingNode();

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;
}
