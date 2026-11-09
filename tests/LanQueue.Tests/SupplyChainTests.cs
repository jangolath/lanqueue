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

    private static YamlMappingNode Map(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlMappingNode m ? m : new YamlMappingNode();

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;
}
