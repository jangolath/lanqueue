using System.Text;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.2 (B2): scripts/smoke-workers.ps1, the release-candidate smoke on real workers. Only its pure parts are tested: the arguments, the
/// refusal of the real config, the identity check, and (statically) that it writes only under its temporary folder and sets LANQ_HOME.
/// Nothing here ever lets it reach gh or a worker: every run below is refused before that.
/// </summary>
public sealed class SmokeScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-smoketest-" + Guid.NewGuid().ToString("N"));

    public SmokeScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestTree.Delete(_dir);

    private static string Script => Path.Combine(T.RepoRoot, "scripts", "smoke-workers.ps1");

    private const string Real = """
        policy:
          allowed_commands: ["dotnet test"]
        machines:
          - name: build-01
            os: linux
            ssh: build@10.20.30.11
            work_root: /srv/lanq
            capabilities: [dotnet, linux]
            max_jobs: 2
          - name: win-01   # a comment
            os: windows
            ssh: "build@10.20.30.12"
            work_root: 'C:/lq'
            max_jobs: 1
        """;

    private string File(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>The script run as a file with <paramref name="args"/>, LANQ_CONFIG pointing at <paramref name="realConfig"/>.</summary>
    private static (int Code, string Output) RunScript(string realConfig, params string[] args) =>
        Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, .. args], new Dictionary<string, string> { ["LANQ_CONFIG"] = realConfig });

    /// <summary>The script's functions loaded (-LoadOnly, nothing runs), then <paramref name="code"/>.</summary>
    private static (int Code, string Output) WithFunctions(string code) =>
        Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $". '{Script}' -LoadOnly; {code}"]);

    [PowerShellFact]
    public void SmokeArgumentsAreChecked()
    {
        var config = File("machines.smoke.yml", Real);
        var (code, output) = WithFunctions(
            $"Test-SmokeArgs '' 'x'; Test-SmokeArgs '12a' 'x'; Test-SmokeArgs '123' ''; Test-SmokeArgs '123' '{_dir}\\none.yml'; " +
            $"if ($null -eq (Test-SmokeArgs '123' '{config}')) {{ 'OK' }}");
        Assert.Equal(0, code);
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("give -Run", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("-Run must be a run id", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("give -Config", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("-Config: no file", lines[3], StringComparison.Ordinal);
        Assert.Equal("OK", lines[4]);

        // As a script: refused with exit 2 before anything.
        var (exit, said) = RunScript(config, "-Run", "abc", "-Config", config);
        Assert.Equal(2, exit);
        Assert.Contains("-Run must be a run id", said, StringComparison.Ordinal);
    }

    /// <summary>The real config is refused by resolved full path: as given, in another case (Windows), through .., and through a link to it.</summary>
    [PowerShellFact]
    public void SmokeScriptRefusesTheRealConfig()
    {
        var real = File("machines.yml", Real);
        var variants = new List<string> { real, Path.Combine(_dir, "sub", "..", "machines.yml") };
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        if (OperatingSystem.IsWindows())
        {
            variants.Add(real.ToUpperInvariant());
        }

        var link = Path.Combine(_dir, "link.yml");
        try
        {
            System.IO.File.CreateSymbolicLink(link, real);
            variants.Add(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // creating a symbolic link needs Developer Mode or admin on Windows: that variant is left out
        }

        var linkedDir = Path.Combine(_dir, "linked");
        try
        {
            Directory.CreateSymbolicLink(linkedDir, _dir);
            variants.Add(Path.Combine(linkedDir, "machines.yml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // as above
        }

        foreach (var config in variants)
        {
            var (code, output) = RunScript(real, "-Run", "123", "-Config", config);
            Assert.True(code == 2, config + ": " + output);
            Assert.Contains("is your real config", output, StringComparison.Ordinal);
        }

        // A separate file with the same content is not the real config (it gets as far as the identity check, which passes, so the
        // function is called directly instead of letting the script reach gh).
        var copy = File("machines.smoke.yml", Real);
        var (c2, o2) = WithFunctions($"if ($null -eq (Test-NotRealConfig '{copy}' '{real}')) {{ 'NOT-REAL' }}");
        Assert.Equal(0, c2);
        Assert.Contains("NOT-REAL", o2, StringComparison.Ordinal);
    }

    [PowerShellFact]
    public void SmokeIdentityCheckComparesSshWorkRootAndMaxJobs()
    {
        var real = File("machines.yml", Real);
        string Compare(string smokeYaml)
        {
            var smoke = File("smoke-" + Guid.NewGuid().ToString("N")[..6] + ".yml", smokeYaml);
            var (code, output) = WithFunctions(
                $"try {{ $p = Compare-MachineIdentities (Read-MachineIdentities '{smoke}') (Read-MachineIdentities '{real}'); if ($p.Count -eq 0) {{ 'MATCH' }} else {{ $p }} }} catch {{ 'REFUSED: ' + $_.Exception.Message }}");
            Assert.Equal(0, code);
            return output.Replace("\r", "", StringComparison.Ordinal).Trim();
        }

        Assert.Equal("MATCH", Compare(Real.Replace("max_jobs: 2", "max_jobs: 2   # same", StringComparison.Ordinal)));
        Assert.Equal("MATCH", Compare(Real[..Real.IndexOf("  - name: win-01", StringComparison.Ordinal)])); // a subset is fine
        Assert.Contains("work_root is '/srv/other'", Compare(Real.Replace("/srv/lanq", "/srv/other", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("max_jobs is '4'", Compare(Real.Replace("max_jobs: 2", "max_jobs: 4", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("ssh is", Compare(Real.Replace("build@10.20.30.11", "other@10.20.30.11", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("is not in the real config", Compare(Real.Replace("build-01", "build-09", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", Compare("machines:\n  - {name: build-01, ssh: x, work_root: y}\n"), StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", Compare("machines: [x]\n"), StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", Compare("machines:\n  - name: build-01\n    os: linux\n"), StringComparison.Ordinal); // no ssh line
        Assert.Contains("lists no machines", Compare("policy: {}\n"), StringComparison.Ordinal);

        // As a script: a mismatch is refused with exit 2 before anything runs.
        var smoke = File("machines.smoke.yml", Real.Replace("/srv/lanq", "/srv/other", StringComparison.Ordinal));
        var (exit, said) = RunScript(real, "-Run", "123", "-Config", smoke);
        Assert.Equal(2, exit);
        Assert.Contains("must equal the real ones", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every file write, delete or folder creation is in Write-SmokeFile, Remove-SmokeFile or Remove-SmokeTemp (which stay under the
    /// smoke folder); no cmdlet that writes files and no redirection appears anywhere.
    /// </summary>
    [PowerShellFact]
    public void SmokeScriptWritesOnlyUnderItsTemp()
    {
        const string Dump = """
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$null)
            function Owner($n) { while ($n -and $n -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $n = $n.Parent }; if ($n) { $n.Name } else { '-' } }
            foreach ($c in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)) { 'CMD ' + $c.GetCommandName() + ' ' + (Owner $c) }
            foreach ($m in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)) { 'MEMBER ' + $m.Member.Extent.Text + ' ' + (Owner $m) }
            foreach ($r in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FileRedirectionAst] }, $true)) { 'REDIRECT ' + $r.Extent.Text + ' ' + (Owner $r) }
            """;
        var dump = File("dump.ps1", Dump);
        var (code, output) = Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", dump, Script]);
        Assert.True(code == 0, output);
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToList();
        Assert.Contains(lines, l => l[0] == "MEMBER" && l[1] == "WriteAllText"); // the dump works

        string[] writers = ["Set-Content", "Add-Content", "Out-File", "New-Item", "Remove-Item", "Copy-Item", "Move-Item", "Rename-Item", "Clear-Content", "Export-Csv", "Tee-Object"];
        Assert.DoesNotContain(lines, l => l[0] == "CMD" && writers.Contains(l[1], StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l[0] == "REDIRECT" && !l[1].StartsWith("2>&1", StringComparison.Ordinal));

        string[] fileMembers = ["WriteAllText", "WriteAllBytes", "WriteAllLines", "AppendAllText", "Delete", "CreateDirectory", "Move", "Copy"];
        string[] owners = ["Write-SmokeFile", "Remove-SmokeFile", "Remove-SmokeTemp"];
        Assert.All(lines.Where(l => l[0] == "MEMBER" && fileMembers.Contains(l[1], StringComparer.Ordinal)), l => Assert.True(owners.Contains(l[2]) || (l[1] == "CreateDirectory" && l[2] == "-"), string.Join(' ', l)));

        // The one top-level CreateDirectory makes the smoke folder itself; Remove-SmokeTemp only removes a folder named lanq-smoke-*.
        var text = System.IO.File.ReadAllText(Script);
        Assert.Contains("[void][System.IO.Directory]::CreateDirectory($script:SmokeTemp)", text, StringComparison.Ordinal);
        Assert.Contains("('lanq-smoke-' + [guid]::NewGuid().ToString('N'))", text, StringComparison.Ordinal);
        Assert.Contains("(Split-Path -Leaf $script:SmokeTemp) -like 'lanq-smoke-*'", text, StringComparison.Ordinal);
    }

    /// <summary>The smoke-policy snippet in RELEASING.md loads (with its example machine uncommented) and allows exactly the smoke's commands.</summary>
    [Fact]
    public void SmokePolicySnippetLoads()
    {
        var doc = System.IO.File.ReadAllText(Path.Combine(T.RepoRoot, "RELEASING.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = doc.IndexOf("   # machines.smoke.yml:", StringComparison.Ordinal);
        var end = doc.IndexOf("   ```", start, StringComparison.Ordinal);
        var yaml = string.Join('\n', doc[start..end].Split('\n').Select(l => l.Length >= 3 ? l[3..] : l).Select(l => l.StartsWith("  # ", StringComparison.Ordinal) && !l.Contains("machines.yml", StringComparison.Ordinal) ? "  " + l[4..] : l));

        var config = LanQueue.Config.ConfigLoader.Parse(yaml);

        Assert.Equal("build-01", Assert.Single(config.Machines).Name);
        Assert.Equal(["SMOKE_VALUE"], config.Policy.AllowedEnv);
        Assert.Equal("sh smoke/check.sh", LanQueue.Policy.JobPolicy.AllowedBy(config.Policy, ["sh", "smoke/check.sh", "run", "1", "0"]));
        Assert.Equal(
            "powershell -NoProfile -ExecutionPolicy Bypass -File smoke/check.ps1",
            LanQueue.Policy.JobPolicy.AllowedBy(config.Policy, ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "smoke/check.ps1", "list-slots"]));
        Assert.Null(LanQueue.Policy.JobPolicy.AllowedBy(config.Policy, ["sh", "smoke/other.sh"])); // scenario 2a's refusal
    }

    /// <summary>LANQ_HOME is a fresh folder under the smoke folder before lanq is first run, and is restored at the end.</summary>
    [Fact]
    public void SmokeScriptSetsLanqHome()
    {
        var text = System.IO.File.ReadAllText(Script);
        var set = text.IndexOf("$env:LANQ_HOME = Join-Path $script:SmokeTemp 'home'", StringComparison.Ordinal);
        Assert.True(set > 0);
        var firstLanq = text.IndexOf("Invoke-Smoke $lanq", StringComparison.Ordinal);
        Assert.True(firstLanq > set, "lanq runs before LANQ_HOME is set");
        Assert.Contains("$env:LANQ_HOME = $savedHome", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Test-NotRealConfig $Config $realConfig", StringComparison.Ordinal) < set, "the real-config refusal comes first");
        Assert.True(text.IndexOf("Compare-MachineIdentities $smokeMachines $realMachines", StringComparison.Ordinal) < set, "the identity check comes first");
        Assert.True(text.IndexOf("& gh run download", StringComparison.Ordinal) > set);

        // Every lanq call goes through the smoke config, every job has --timeout 5, and the throwaway repo has a fixed identity and dates.
        Assert.Contains("'--config', $configFull", text, StringComparison.Ordinal);
        Assert.Contains("$common = @('--json', '--timeout', '5'", text, StringComparison.Ordinal);
        Assert.Contains("$env:GIT_AUTHOR_DATE = '2026-01-01T00:00:00Z'", text, StringComparison.Ordinal);
        Assert.Contains("$env:GIT_COMMITTER_DATE = '2026-01-01T00:00:00Z'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.", text, StringComparison.Ordinal);
    }
}
