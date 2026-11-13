using System.Text;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Plan;
using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.2 (B1): lanq's own lanq.yml and the .NET shard scripts it runs (scripts/lanq), which are also the recipe in docs/SHARDING.md. A class
/// goes to shard cksum(class) mod N + 1, identical in sh and PowerShell on every locale.
/// </summary>
public sealed partial class TestSplitTests : IDisposable
{
    /// <summary>The exact policy.allowed_commands lines the owner adds for lanq.yml (README "Developing lanq"): Windows, then the optional Linux job.</summary>
    internal static readonly string[] AllowLines =
    [
        "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/lanq/dotnet-test-shard.ps1",
        "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/lanq/check-test-split.ps1",
        "sh scripts/lanq/dotnet-test-shard.sh",
        "sh scripts/lanq/check-test-split.sh",
    ];

    private static readonly (string Name, long Cksum)[] Golden =
    [
        ("LanQueue.Tests.JobEnvTests", 825328188),
        ("LanQueue.Tests.JobEnvironmentTests", 1308070396),
        ("LanQueue.Tests.PlanFileTests", 3273765563),
        ("", 4294967295),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-split-" + Guid.NewGuid().ToString("N"));

    public TestSplitTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestTree.Delete(_dir);

    private static string Script(string name) => Path.Combine(T.RepoRoot, "scripts", "lanq", name);

    private string ClassFile(IEnumerable<string> classes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Join("\n", classes) + "\n", new UTF8Encoding(false));
        return path;
    }

    /// <summary>"cksum shard class" lines from the sh script's --assign, run with <paramref name="locale"/> as LC_ALL.</summary>
    private static List<(long Cksum, int Shard, string Name)> AssignSh(string file, int n, string locale)
    {
        var (code, output) = Shells.Exec(Shells.Sh!, [Script("dotnet-test-shard.sh").Replace('\\', '/'), "--assign", n.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Replace('\\', '/')], new Dictionary<string, string> { ["LC_ALL"] = locale, ["LANG"] = locale });
        Assert.True(code == 0, output);
        return Parse(output);
    }

    /// <summary>The same from the PowerShell script's -AssignFile, with the thread culture set to <paramref name="culture"/> first.</summary>
    private static List<(long Cksum, int Shard, string Name)> AssignPs(string file, int n, string culture)
    {
        var command = $"[System.Threading.Thread]::CurrentThread.CurrentCulture = [cultureinfo]'{culture}'; [System.Threading.Thread]::CurrentThread.CurrentUICulture = [cultureinfo]'{culture}'; "
            + $"& '{Script("dotnet-test-shard.ps1")}' -AssignFile '{file}' -Shards {n}";
        var (code, output) = Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command]);
        Assert.True(code == 0, output);
        return Parse(output);
    }

    private static List<(long Cksum, int Shard, string Name)> Parse(string output) =>
        [.. output.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Where(l => l.Length > 0)
            .Select(l => l.Split(' ', 3))
            .Select(f => (long.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), f.Length > 2 ? f[2] : ""))];

    /// <summary>Every test class of this assembly (the real names the split sees), plus names that differ only in case or carry non-ASCII.</summary>
    private static List<string> Classes() =>
        [.. typeof(TestSplitTests).Assembly.GetTypes().Where(t => t.IsPublic && t.Name.EndsWith("Tests", StringComparison.Ordinal)).Select(t => t.FullName!).Order(StringComparer.Ordinal),
            "Ns.iTests", "Ns.ITests", "Ns.İTests", "Ns.ÇalışmaTests", "Ns.StraßeTests"];

    [GeneratedRegex("```(?:sh|powershell)\\n(.*?)\\n```", RegexOptions.Singleline, 5000)]
    private static partial Regex FenceRegex();

    /// <summary>docs/SHARDING.md shows the three scripts exactly as lanq's repository runs them.</summary>
    [Fact]
    public void ShardScriptsMatchTheDocs()
    {
        var doc = File.ReadAllText(Path.Combine(T.RepoRoot, "docs", "SHARDING.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = FenceRegex().Matches(doc).Select(m => m.Groups[1].Value).ToList();
        foreach (var name in new[] { "dotnet-test-shard.sh", "dotnet-test-shard.ps1", "check-test-split.sh", "check-test-split.ps1" })
        {
            var script = File.ReadAllText(Script(name)).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
            Assert.Contains(script, blocks);
        }

        // The old position split is gone from the recipe, and its sorts are ordinal now.
        Assert.DoesNotContain(blocks, b => b.Contains("Sort-Object -Unique", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Contains("| sort -u", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Contains("$t.Keys | Sort-Object", StringComparison.Ordinal));
    }

    [Fact]
    public void CksumGoldenVectorsShAndPowerShell()
    {
        var file = ClassFile(Golden.Select(g => g.Name));
        var ran = 0;
        if (Shells.Sh is not null)
        {
            Assert.Equal(Golden.Select(g => (g.Cksum, g.Name)), AssignSh(file, 4, "C").Select(r => (r.Cksum, r.Name)));
            ran++;
        }

        if (Shells.PowerShell is not null)
        {
            Assert.Equal(Golden.Select(g => (g.Cksum, g.Name)), AssignPs(file, 4, "").Select(r => (r.Cksum, r.Name)));
            ran++;
        }

        Assert.True(ran > 0 || !OperatingSystem.IsWindows(), "neither sh nor PowerShell found");
    }

    /// <summary>The shard depends only on the name and N: the same under LC_ALL=C and en_US.UTF-8 in sh, and InvariantCulture and tr-TR in PowerShell.</summary>
    [Fact]
    public void SameClassSameShardAcrossLocales()
    {
        var classes = Classes();
        var file = ClassFile(classes);
        var results = new List<List<(long Cksum, int Shard, string Name)>>();
        if (Shells.Sh is not null)
        {
            results.Add(AssignSh(file, 4, "C"));
            results.Add(AssignSh(file, 4, "en_US.UTF-8"));
        }

        if (Shells.PowerShell is not null)
        {
            results.Add(AssignPs(file, 4, ""));
            results.Add(AssignPs(file, 4, "tr-TR"));
        }

        Assert.All(results, r => Assert.Equal(classes.Count, r.Count));
        // Names are compared by position (a console may re-encode non-ASCII names on the way back); the numbers must match exactly.
        Assert.All(results, r => Assert.Equal(results[0].Select(x => (x.Cksum, x.Shard)), r.Select(x => (x.Cksum, x.Shard))));
        Assert.All(results[0], r => Assert.InRange(r.Shard, 1, 4));
        Assert.All(results[0], r => Assert.Equal((int)(r.Cksum % 4) + 1, r.Shard));
    }

    /// <summary>A fake dotnet (first on PATH) that logs its arguments and lists <paramref name="listed"/> for --list-tests.</summary>
    private Dictionary<string, string> FakeDotnet(string listed, out string log)
    {
        var bin = Path.Combine(_dir, "fake-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(bin);
        log = Path.Combine(bin, "calls.log");
        var list = Path.Combine(bin, "list.txt");
        File.WriteAllText(list, listed, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(bin, "dotnet"), "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$FAKE_LOG\"\ncase \"$*\" in *--list-tests*) cat \"$FAKE_LIST\" ;; esac\nexit 0\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(bin, "dotnet"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(
            Path.Combine(bin, "dotnet.ps1"),
            "Add-Content -LiteralPath $env:FAKE_LOG -Value ($args -join ' ')\nif ($args -contains '--list-tests') { [IO.File]::ReadAllLines($env:FAKE_LIST, [Text.Encoding]::UTF8) }\nexit 0\n",
            new UTF8Encoding(true));
        return new Dictionary<string, string>
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_LOG"] = log,
            ["FAKE_LIST"] = list,
        };
    }

    private static (int Code, string Output) Shard(string shell, Dictionary<string, string> env, string shard, string shards, params string[] args)
    {
        env["LANQ_SHARD"] = shard;
        env["LANQ_SHARDS"] = shards;
        return shell == "sh"
            ? Shells.Exec(Shells.Sh!, [Script("dotnet-test-shard.sh").Replace('\\', '/'), .. args], env)
            : Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script("dotnet-test-shard.ps1"), .. args.Select(a => a == "--list" ? "-List" : a).Prepend("-Project").Take(args.Length + 1)], env);
    }

    private static IEnumerable<string> ShardShells() =>
        new[] { Shells.Sh is null ? null : "sh", Shells.PowerShell is null ? null : "ps" }.OfType<string>();

    /// <summary>A bad LANQ_SHARD/LANQ_SHARDS (or --assign N) exits 2 before dotnet is ever called.</summary>
    [Fact]
    public void ShardScriptsRefuseBadShardNumbers()
    {
        foreach (var shell in ShardShells())
        {
            foreach (var (shard, shards) in new[] { ("0", "4"), ("5", "4"), ("abc", "4"), ("1", "0"), ("1", "abc"), ("", "4"), ("1", ""), ("-1", "4"), ("1", "1000") })
            {
                var env = FakeDotnet("    A.BTests.M\n", out var log);
                var (code, output) = Shard(shell, env, shard, shards, "tests/App.Tests.csproj");
                Assert.True(code == 2, $"{shell} {shard}/{shards}: exit {code}: {output}");
                Assert.False(File.Exists(log), $"{shell} {shard}/{shards}: dotnet was called");
            }
        }

        var file = ClassFile(["A.BTests"]);
        if (Shells.Sh is not null)
        {
            Assert.Equal(2, Shells.Exec(Shells.Sh, [Script("dotnet-test-shard.sh").Replace('\\', '/'), "--assign", "0", file.Replace('\\', '/')]).Code);
        }

        if (Shells.PowerShell is not null)
        {
            Assert.Equal(2, Shells.Exec(Shells.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script("dotnet-test-shard.ps1"), "-AssignFile", file, "-Shards", "abc"]).Code);
        }
    }

    /// <summary>No test classes at all fails (1); a class name that is not printable ASCII is refused (2); a run passes TreatNoTestsAsError.</summary>
    [Fact]
    public void ShardScriptsFailClosed()
    {
        foreach (var shell in ShardShells())
        {
            var env = FakeDotnet("The following Tests are available:\n", out _);
            Assert.Equal(1, Shard(shell, env, "1", "1", "tests/App.Tests.csproj").Code);

            env = FakeDotnet("    A.CaféTests.M\n    A.BTests.M\n", out var log);
            var (code, output) = Shard(shell, env, "1", "1", "tests/App.Tests.csproj");
            Assert.True(code == 2, shell + ": " + output);
            Assert.Contains("not printable ASCII", output, StringComparison.Ordinal);
            Assert.DoesNotContain(File.ReadAllLines(log), l => l.Contains("--filter", StringComparison.Ordinal)); // no test ran

            env = FakeDotnet("    A.BTests.M\n    A.CTests.M(x: 1)\n", out log);
            (code, output) = Shard(shell, env, "1", "1", "tests/App.Tests.csproj");
            Assert.True(code == 0, shell + ": " + output);
            var run = File.ReadAllLines(log).Last();
            Assert.Contains("--filter FullyQualifiedName~A.BTests.|FullyQualifiedName~A.CTests.", run, StringComparison.Ordinal);
            Assert.EndsWith("-- RunConfiguration.TreatNoTestsAsError=true", run, StringComparison.Ordinal);

            // --list: no build, and a list with the same filter (no TreatNoTestsAsError).
            env = FakeDotnet("    A.BTests.M\n", out log);
            Assert.Equal(0, Shard(shell, env, "1", "1", "tests/App.Tests.csproj", "--list").Code);
            var calls = File.ReadAllLines(log);
            Assert.DoesNotContain(calls, l => l.StartsWith("build", StringComparison.Ordinal));
            Assert.Contains("--list-tests -nologo --filter FullyQualifiedName~A.BTests.", calls.Last(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AddingAClassMovesNoOtherClass()
    {
        var before = Classes();
        List<string> after = [.. before.Take(10), "LanQueue.Tests.BrandNewTests", .. before.Skip(10), "AAA.FirstTests"];
        Func<string, int, List<(long Cksum, int Shard, string Name)>> assign = Shells.Sh is not null
            ? (f, n) => AssignSh(f, n, "C")
            : (f, n) => AssignPs(f, n, "");

        // By position (the output may re-encode non-ASCII names): line k of "after" is before[k] for k < 10, before[k - 1] after that.
        var was = assign(ClassFile(before), 4).Select(r => r.Shard).ToList();
        var now = assign(ClassFile(after), 4).Select(r => r.Shard).ToList();

        Assert.Equal(after.Count, now.Count);
        Assert.Equal(was, [.. now.Take(10), .. now.Skip(11).Take(before.Count - 10)]);
    }

    private static readonly string PolicyYaml = $$"""
        policy:
          allowed_commands: [{{string.Join(", ", AllowLines.Select(l => "\"" + l + "\""))}}]
          allowed_directories: ["."]
        machines:
          - name: build-01
            os: windows
            ssh: user@10.20.30.11
            work_root: C:/lq
            capabilities: [dotnet, windows]
            max_jobs: 2
        """;

    /// <summary>The plan runs on Windows only (a Windows-only pool can run all of it): the guard with the same N as the shards.</summary>
    [Fact]
    public void RepoLanqYmlLoadsAndExpands()
    {
        var plan = PlanFileLoader.Load(Path.Combine(T.RepoRoot, "lanq.yml"));
        Assert.Equal(["guard", "unit-windows"], plan.Jobs.Select(j => j.Name));
        var config = ConfigLoader.Parse(PolicyYaml);

        var guard = plan.Jobs[0].Args;
        var windows = plan.Jobs[1].Args;
        Assert.Equal(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/lanq/check-test-split.ps1", "-Project", "tests/LanQueue.Tests/LanQueue.Tests.csproj", "-Shards", "2"],
            guard.Argv);
        Assert.Equal(2, windows.Shards!.Count);
        Assert.Equal(guard.Argv[^1], $"{windows.Shards.Count}"); // the guard checks the N the shards use
        Assert.Null(guard.Shards);
        Assert.All(plan.Jobs, j => Assert.Equal(["dotnet", "windows"], j.Args.Needs));
        Assert.Equal(AllowLines[0], JobPolicy.AllowedBy(config.Policy, windows.Argv));
        Assert.Equal(AllowLines[1], JobPolicy.AllowedBy(config.Policy, guard.Argv));
        Assert.All(plan.Jobs, j => Assert.True(j.Args.Baseline));
        Assert.All(plan.Jobs, j => Assert.Equal(30, j.Args.TimeoutMinutes));
        Assert.Equal(["tests/LanQueue.Tests/TestResults/*.trx"], windows.Fetch);
        Assert.All(plan.Jobs, j => Assert.True(JobPolicy.ValidateForWindows(j.Args.Argv).Ok));
    }

    /// <summary>The commented Linux block, uncommented, loads too, with its guard's N matching its shards and the two sh allowlist lines.</summary>
    [Fact]
    public void RepoLanqYmlLinuxExampleLoads()
    {
        var text = File.ReadAllText(Path.Combine(T.RepoRoot, "lanq.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var uncommented = string.Join('\n', text.Split('\n').Select(l => l.StartsWith("  #   ", StringComparison.Ordinal) || l.StartsWith("  # unit-linux:", StringComparison.Ordinal) || l.StartsWith("  # guard-linux:", StringComparison.Ordinal) ? "  " + l[4..] : l));
        var plan = PlanFileLoader.Parse(uncommented);
        Assert.Equal(["guard", "unit-windows", "unit-linux", "guard-linux"], plan.Jobs.Select(j => j.Name));
        var config = ConfigLoader.Parse(PolicyYaml);
        var linux = plan.Jobs[2].Args;
        var guard = plan.Jobs[3].Args;
        Assert.Equal(4, linux.Shards!.Count);
        Assert.Equal(guard.Argv[^1], $"{linux.Shards.Count}");
        Assert.Equal(["dotnet", "linux"], linux.Needs);
        Assert.Equal(["dotnet", "linux"], guard.Needs);
        Assert.Equal(AllowLines[2], JobPolicy.AllowedBy(config.Policy, linux.Argv));
        Assert.Equal(AllowLines[3], JobPolicy.AllowedBy(config.Policy, guard.Argv));
    }

    [Fact]
    public void RepoLanqYmlNamesNoMachines()
    {
        var text = File.ReadAllText(Path.Combine(T.RepoRoot, "lanq.yml"));
        var plan = PlanFileLoader.Load(Path.Combine(T.RepoRoot, "lanq.yml"));

        Assert.All(plan.Jobs, j => Assert.Null(j.Args.On));
        Assert.DoesNotMatch(@"(?m)^\s*on\s*:", text);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", text);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.All(plan.Jobs, j => Assert.DoesNotContain("--on", j.RunArgs));
    }
}
