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
        var files = WorkflowFiles(Path.Combine(Root, ".github", "workflows"));
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

    private static IEnumerable<(string Job, YamlMappingNode Step)> Steps(YamlMappingNode workflow) =>
        Map(workflow, "jobs").Children.SelectMany(c =>
            ((YamlMappingNode)c.Value).Children.TryGetValue(new YamlScalarNode("steps"), out var s)
                ? ((YamlSequenceNode)s).Cast<YamlMappingNode>().Select(step => (((YamlScalarNode)c.Key).Value!, step))
                : []);

    /// <summary>Every workflow file in a folder: GitHub reads both .yml and .yaml.</summary>
    internal static string[] WorkflowFiles(string folder) =>
        [.. Directory.GetFiles(folder, "*.yml").Concat(Directory.GetFiles(folder, "*.yaml")).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Workflows() =>
        new(WorkflowFiles(Path.Combine(FindRoot(), ".github", "workflows")).Select(f => Path.GetFileName(f)));

    [Fact]
    public void WorkflowGuardsSeeYmlAndYamlFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanq-wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.yml"), "");
            File.WriteAllText(Path.Combine(dir, "b.yaml"), "");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "");
            Assert.Equal(["a.yml", "b.yaml"], WorkflowFiles(dir).Select(f => Path.GetFileName(f)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>No ${{ }} inside a run: block: a ref name or step output there is pasted into the shell as code. Pass values via env:.</summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void NoExpressionIsInterpolatedIntoAShellScript(string workflow)
    {
        foreach (var (job, step) in Steps(Workflow(workflow)))
        {
            Assert.False(Scalar(step, "run")?.Contains("${{", StringComparison.Ordinal) == true, $"{workflow}: a run: step of job {job} interpolates an expression; pass it through env:");
        }
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void EveryCheckoutDropsItsCredentials(string workflow)
    {
        foreach (var (job, step) in Steps(Workflow(workflow)).Where(s => Scalar(s.Step, "uses")?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true))
        {
            Assert.True(Scalar(Map(step, "with"), "persist-credentials") == "false", $"{workflow}: checkout in job {job} must set persist-credentials: false");
        }
    }

    [Fact]
    public void ReleaseRunsAreSerialisedPerRefAndNeverCancelled()
    {
        var concurrency = Map(Workflow("release.yml"), "concurrency");
        Assert.Equal("release-${{ github.ref }}", Scalar(concurrency, "group"));
        Assert.Equal("false", Scalar(concurrency, "cancel-in-progress"));
    }

    [Fact]
    public void TheVersionIsValidatedAndPrereleasesAreClassifiedByScript()
    {
        var workflow = Workflow("release.yml");
        var ver = Steps(workflow).Single(s => s.Job == "pack" && Scalar(s.Step, "id") == "ver").Step;
        Assert.Contains("scripts/release-version.sh", Scalar(ver, "run"), StringComparison.Ordinal);
        var outputs = Map(Map(Map(workflow, "jobs"), "pack"), "outputs");
        Assert.Equal("${{ steps.ver.outputs.version }}", Scalar(outputs, "version"));
        Assert.Equal("${{ steps.ver.outputs.prerelease }}", Scalar(outputs, "prerelease"));
        var publishRuns = string.Join('\n', Steps(workflow).Where(s => s.Job == "publish").Select(s => Scalar(s.Step, "run") ?? ""));
        Assert.Contains("scripts/release-draft.sh draft", publishRuns, StringComparison.Ordinal);
        Assert.Contains("scripts/release-draft.sh publish", publishRuns, StringComparison.Ordinal);
    }

    /// <summary>The release notes come from CHANGELOG.md, reach the package and the GitHub release, and stay out of the release assets.</summary>
    [Fact]
    public void ReleaseNotesComeFromTheChangelogAndReachThePackageAndTheRelease()
    {
        var steps = Steps(Workflow("release.yml")).ToList();
        var packRuns = string.Join('\n', steps.Where(s => s.Job == "pack").Select(s => Scalar(s.Step, "run") ?? ""));
        Assert.Contains("scripts/release-notes.sh \"$VERSION\" CHANGELOG.md", packRuns, StringComparison.Ordinal);
        Assert.Contains("-p:LanqReleaseNotesFile=", packRuns, StringComparison.Ordinal);
        Assert.Contains("unzip -p", packRuns, StringComparison.Ordinal);
        Assert.Contains(steps, s => s.Job == "pack" && Scalar(Map(s.Step, "with"), "name") == "release-notes");
        Assert.Contains(steps, s => s.Job == "publish" && Scalar(s.Step, "uses")?.StartsWith("actions/download-artifact@", StringComparison.Ordinal) == true
            && Scalar(Map(s.Step, "with"), "name") == "release-notes");
        Assert.DoesNotContain(steps, s => s.Job == "assemble" && Scalar(s.Step, "uses")?.StartsWith("actions/download-artifact@", StringComparison.Ordinal) == true
            && Scalar(Map(s.Step, "with"), "name") == "release-notes");
        var publishRuns = string.Join('\n', steps.Where(s => s.Job == "publish").Select(s => Scalar(s.Step, "run") ?? ""));
        Assert.Contains("scripts/release-draft.sh draft \"v$VERSION\" \"$PRERELEASE\" release notes/release-notes.md", publishRuns, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("release.yml", "pack")]
    [InlineData("ci.yml", "aot-linux")]
    public void BuildsFailWhenTheInstalledRuntimeIsNewerThanThePin(string workflow, string job)
    {
        var runs = string.Join('\n', Steps(Workflow(workflow)).Where(s => s.Job == job).Select(s => Scalar(s.Step, "run") ?? ""));
        Assert.Contains("dotnet --list-runtimes", runs, StringComparison.Ordinal);
        Assert.Contains("scripts/check-runtime-pin.sh installed", runs, StringComparison.Ordinal);
    }

    [Fact]
    public void AWeeklyWorkflowComparesThePinWithTheLatestDotnetRelease()
    {
        Assert.True(File.Exists(Path.Combine(Root, ".github", "workflows", "runtime-pin.yml")), "runtime-pin.yml is missing");
        var workflow = Workflow("runtime-pin.yml");
        Assert.True(Map(workflow, "on").Children.TryGetValue(new YamlScalarNode("schedule"), out var schedule) && schedule is YamlSequenceNode { Children.Count: > 0 });
        var permissions = Map(workflow, "permissions");
        Assert.Equal("read", Scalar(permissions, "contents"));
        Assert.Single(permissions.Children);
        var runs = string.Join('\n', Steps(workflow).Select(s => Scalar(s.Step, "run") ?? ""));
        Assert.Contains("releases.json", runs, StringComparison.Ordinal);
        Assert.Contains("scripts/check-runtime-pin.sh latest", runs, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Workflows))]
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

        // A manual (dry) run builds everything above and publishes nothing. It takes no inputs: only the tag push gates publishing, so an
        // input such as dry-run would be a switch nothing reads.
        Assert.True(Map(Workflow("release.yml"), "on").Children.TryGetValue(new YamlScalarNode("workflow_dispatch"), out var dispatch));
        Assert.False(dispatch is YamlMappingNode d && d.Children.ContainsKey(new YamlScalarNode("inputs")), "release.yml workflow_dispatch has inputs");
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

        var publish = Steps(Workflow("release.yml")).Where(s => s.Job == "publish").Select(s => Scalar(s.Step, "run") ?? "").ToList();
        var draft = publish.FindIndex(r => r.Contains("scripts/release-draft.sh draft", StringComparison.Ordinal));
        var push = publish.FindIndex(r => r.Contains("dotnet nuget push", StringComparison.Ordinal));
        var undraft = publish.FindIndex(r => r.Contains("scripts/release-draft.sh publish", StringComparison.Ordinal));
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

    /// <summary>Dependabot has no docker ecosystem here, so the Docker worker's base image moves only by a deliberate digest bump.</summary>
    [Fact]
    public void DockerWorkerBaseImageIsPinnedByDigest()
    {
        var dockerfile = File.ReadAllLines(Path.Combine(Root, "scripts", "docker-linux-worker", "Dockerfile"));
        var froms = dockerfile.Where(l => l.TrimStart().StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(froms);
        Assert.Matches(@"^FROM mcr\.microsoft\.com/dotnet/sdk:10\.0-noble@sha256:[0-9a-f]{64}$", froms[0].Trim());
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
    /// Runs a copy of scripts/sync-makeready.sh in a scratch tree with a fake gh (which "downloads" the four scripts and the given SHA256SUMS
    /// and accepts every attestation), so only the SHA256SUMS check decides. <paramref name="sums"/> writes the two scanner lines; the two tune
    /// lines (0.5.0+) follow unless <paramref name="withTune"/> is false. Returns the exit code, output and whether the scripts were copied
    /// (all four, or none).
    /// </summary>
    private static (int Code, string Output, bool Copied) RunSync(Func<string, string, string> sums, bool withTune = true)
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
            var tune = withTune ? $"{Sha("tps1\n")}  lanq-makeready-tune.ps1\n{Sha("tsh\n")}  lanq-makeready-tune.sh\n" : "";
            File.WriteAllText(sumsFile, sums(Sha("ps1\n"), Sha("sh\n")) + tune);
            var gh = Path.Combine(bin, "gh");
            File.WriteAllText(gh, string.Join('\n',
                "#!/bin/sh",
                "case \"$1 $2\" in",
                "  'release download') printf 'ps1\\n' > lanq-makeready.ps1; printf 'sh\\n' > lanq-makeready.sh;"
                    + " printf 'tps1\\n' > lanq-makeready-tune.ps1; printf 'tsh\\n' > lanq-makeready-tune.sh; cp \"$FAKE_SUMS\" SHA256SUMS ;;",
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
            string[] names = ["lanq-makeready.ps1", "lanq-makeready.sh", "lanq-makeready-tune.ps1", "lanq-makeready-tune.sh"];
            var copied = names.Count(n => File.Exists(Path.Combine(dest, n)));
            Assert.True(copied is 0 or 4, "the sync copies all four scripts or none");
            return (code, output, copied == 4);
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

        // 0.5.0+: the tune scripts must be listed too.
        (code, output, copied) = RunSync((ps1, sh) => $"{ps1}  lanq-makeready.ps1\n{sh}  lanq-makeready.sh\n", withTune: false);
        Assert.NotEqual(0, code);
        Assert.Contains("lanq-makeready-tune.ps1", output, StringComparison.Ordinal);
        Assert.False(copied);
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
