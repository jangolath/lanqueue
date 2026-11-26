using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Plan;
using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// samples/monorepo (0.13.0): the public fixture every pool-facing change is tried on. Its plan files load with no warnings beyond the shard-script one, the
/// Windows and Linux plans pick their own shard script (lanq.yml has no conditions), every command is on the README's allowlist, the
/// solution lists every non-opt-in test project, and nothing in it trips lanq's own secret scan (a finding there would make every
/// lanq run --plan of this repository Rejected).
/// </summary>
public sealed class SampleMonorepoTests : IDisposable
{
    private static readonly string Sample = Path.Combine(T.RepoRoot, "samples", "monorepo");

    /// <summary>The allowlist the sample's README tells people to add (and nothing else).</summary>
    internal const string Policy = """
        policy:
          load_threshold: 0.8
          allowed_commands:
            - "dotnet build"
            - "dotnet test"
            - "npm run"
            - "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/lanq/dotnet-test-shard.ps1"
            - "sh scripts/lanq/dotnet-test-shard.sh"
          allowed_directories: [".", "samples/monorepo/web"]
        machines:
          - name: build-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [dotnet, linux, node]
            max_jobs: 3
          - name: build-02
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [dotnet, linux, node]
            max_jobs: 3
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-sample-" + Guid.NewGuid().ToString("N"));

    public SampleMonorepoTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestTree.Delete(_root);

    private static PlanFile Load(string name) => PlanFileLoader.Load(Path.Combine(Sample, name));

    [Theory]
    [InlineData("lanq.yml")]
    [InlineData("lanq.linux.yml")]
    [InlineData("lanq.extras.yml")]
    public void EveryPlanFileLoadsWithNoWarningsAndOnlyAllowedCommands(string file)
    {
        var plan = Load(file);
        var config = ConfigLoader.Parse(Policy);

        // No warnings (0.13.0 smoke): the shard-script jobs say shard_env: true, since the scripts read LANQ_SHARD instead of a {shard} argument.
        Assert.Empty(plan.Warnings);
        Assert.All(plan.Jobs, j =>
        {
            Assert.True(JobPolicy.AllowedBy(config.Policy, j.Args.Argv) is not null, $"{file}: {j.Name} is not on the README's allowlist");
            Assert.True(JobPolicy.DirectoryAllowed(config.Policy, j.Args.Directory), $"{file}: {j.Name}'s dir is not on the README's allowed_directories");
            Assert.Null(j.Args.On); // a sample never names machines
        });
    }

    [Fact]
    public void DefaultPlansHaveTheSameGraphAndEachPicksItsOwnShardScript()
    {
        var windows = Load("lanq.yml");
        var linux = Load("lanq.linux.yml");
        string[] names = ["build", "unit", "integration", "serial-tail", "web-test", "web-types"];
        Assert.Equal(names, windows.Jobs.Select(j => j.Name));
        Assert.Equal(names, linux.Jobs.Select(j => j.Name));

        foreach (var (plan, os, script) in new[] { (windows, "windows", "scripts/lanq/dotnet-test-shard.ps1"), (linux, "linux", "scripts/lanq/dotnet-test-shard.sh") })
        {
            Assert.Equal(["dotnet", os], plan.Job("build")!.Args.Needs);
            Assert.Equal(["build"], plan.Job("unit")!.After);
            Assert.Equal(["build"], plan.Job("integration")!.After);
            Assert.Equal(["integration"], plan.Job("serial-tail")!.After);
            Assert.True(plan.Job("serial-tail")!.Args.Exclusive);
            Assert.Equal(3, plan.Job("unit")!.Args.Shards!.Count);
            Assert.Equal(2, plan.Job("integration")!.Args.Shards!.Count);
            Assert.Equal(2, plan.Job("web-test")!.Args.Shards!.Count);
            foreach (var sharded in new[] { "unit", "integration" })
            {
                var args = plan.Job(sharded)!.Args;
                Assert.Contains(script, args.Argv);
                Assert.Equal(["dotnet", os], args.Needs); // a .ps1 never lands on Linux, an .sh never on Windows
                Assert.StartsWith("trx:samples/monorepo/tests/", args.Results!.Text, StringComparison.Ordinal);
            }

            Assert.All(new[] { "web-test", "web-types" }, w => Assert.Equal("samples/monorepo/web", plan.Job(w)!.Args.Directory));
            Assert.Equal(["node"], plan.Job("web-test")!.Args.Needs);
            Assert.Equal("junit:test-results/*.xml", plan.Job("web-test")!.Args.Results!.Text);
            Assert.All(plan.Jobs, j => Assert.Equal(2.0, j.Args.MemGb));
            Assert.All(plan.Jobs, j => Assert.True(JobPolicy.ValidateForWindows(j.Args.Argv).Ok));
        }
    }

    [Fact]
    public void ExtrasAreTheOptInFixtures()
    {
        var extras = Load("lanq.extras.yml");
        Assert.Equal(["hang", "zero-tests", "docker"], extras.Jobs.Select(j => j.Name));
        Assert.Equal(3, extras.Job("hang")!.Args.TimeoutMinutes);
        Assert.Contains("Category=DoesNotExist", extras.Job("zero-tests")!.Args.Argv);
        Assert.NotNull(extras.Job("zero-tests")!.Args.Results); // so 0.11's "no tests ran" warning shows
        Assert.Contains("docker", extras.Job("docker")!.Args.Needs);
    }

    [Fact(Timeout = 60_000)]
    public async Task LinuxPlanDryRunDealsTheFirstWave()
    {
        var repo = Path.Combine(_root, "repo");
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "samples", "monorepo", "web"));
        Directory.CreateDirectory(home);
        File.Copy(Path.Combine(Sample, "lanq.linux.yml"), Path.Combine(repo, "lanq.yml"));
        var ssh = new PoolSsh();
        var o = new StringWriter();
        var e = new StringWriter();

        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--dry-run", "--json"]), ConfigLoader.Parse(Policy), ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(home, "state")), home, repo, o, e, CancellationToken.None);

        Assert.True(code == 0, e.ToString());
        using var doc = JsonDocument.Parse(o.ToString());
        var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j, StringComparer.Ordinal);
        Assert.Equal("WouldRun", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("now", jobs["build"].GetProperty("start").GetString());
        Assert.Equal("now", jobs["web-test"].GetProperty("start").GetString());
        Assert.NotEqual("now", jobs["unit"].GetProperty("start").GetString()); // after: [build]
        Assert.NotEqual("now", jobs["serial-tail"].GetProperty("start").GetString());
        Assert.Empty(ssh.Executes);
    }

    [Fact]
    public void SolutionListsEveryNonOptInTestProject()
    {
        var slnx = File.ReadAllText(Path.Combine(Sample, "Sample.slnx"));
        var listed = Regex.Matches(slnx, "Path=\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var projects = Directory.GetFiles(Path.Combine(Sample, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Sample, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(Sample, p).Replace('\\', '/'))
            .ToList();
        string[] optIn = ["tests/Sample.HangTests/Sample.HangTests.csproj", "tests/Sample.DockerTests/Sample.DockerTests.csproj"];

        Assert.All(optIn, p => Assert.Contains(p, projects));
        Assert.All(projects.Except(optIn), p => Assert.Contains(p, listed));
        Assert.All(optIn, p => Assert.DoesNotContain(p, listed));
        Assert.All(projects, p => Assert.True(File.Exists(Path.Combine(Sample, Path.GetDirectoryName(p)!, "packages.lock.json")), "missing packages.lock.json for " + p));
    }

    /// <summary>The sample stands alone: its props do not import lanq's, and lanq's solution does not include it.</summary>
    [Fact]
    public void SampleIsStandalone()
    {
        var props = File.ReadAllText(Path.Combine(Sample, "Directory.Build.props"));
        Assert.DoesNotContain("Import", props, StringComparison.Ordinal);
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("samples/", File.ReadAllText(Path.Combine(T.RepoRoot, "LanQueue.slnx")), StringComparison.Ordinal);
        var package = File.ReadAllText(Path.Combine(Sample, "web", "package.json"));
        using var doc = JsonDocument.Parse(package);
        var deps = doc.RootElement.GetProperty("devDependencies").EnumerateObject().Select(p => p.Name)
            .Concat(doc.RootElement.TryGetProperty("dependencies", out var d) ? d.EnumerateObject().Select(p => p.Name) : [])
            .Order(StringComparer.Ordinal);
        Assert.Equal(["@types/react", "@types/react-dom", "react", "react-dom", "typescript", "vitest"], deps);
        Assert.True(File.Exists(Path.Combine(Sample, "web", "package-lock.json")));
    }

    /// <summary>The opt-in Docker image is pinned by digest (no floating tag reaches a worker).</summary>
    [Fact]
    public void DockerTestImageIsPinnedByDigest()
    {
        var text = string.Join('\n', Directory.GetFiles(Path.Combine(Sample, "tests", "Sample.DockerTests"), "*.cs").Select(File.ReadAllText));
        var images = Regex.Matches(text, "\"([a-z0-9./-]+(?::[A-Za-z0-9._-]+)?(?:@sha256:[0-9a-f]{64})?)\"").Select(m => m.Groups[1].Value)
            .Where(v => v.StartsWith("nginx:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, i => Assert.Matches("@sha256:[0-9a-f]{64}$", i));
    }

    /// <summary>lanq's own scan, over every file of the sample as if it were all uncommitted: no finding.</summary>
    [Fact]
    public void SamplePassesLanqsOwnSecretScan()
    {
        var files = Directory.GetFiles(Sample, "*", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj|node_modules|TestResults|test-results)/"))
            .ToList();
        Assert.NotEmpty(files);
        var relative = files.Select(f => Path.GetRelativePath(T.RepoRoot, f).Replace('\\', '/')).ToList();

        var findings = files.SelectMany(f => SecretScanner.ScanText(File.ReadAllText(f), f))
            .Concat(SecretScanner.ScanFileNames(relative))
            .Concat(SecretScanner.ScanCommittedFileNames(relative))
            .Concat(relative.Where(SecretScanner.IsPackageConfig).SelectMany(r => SecretScanner.ScanCommittedConfig(r, File.ReadAllText(Path.Combine(T.RepoRoot, r)))))
            .ToList();

        Assert.True(findings.Count == 0, string.Join('\n', findings.Select(f => f.Rule + " " + f.Where)));
    }
}
