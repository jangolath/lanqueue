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

    /// <summary>
    /// The script run as a file with <paramref name="args"/>, LANQ_CONFIG pointing at <paramref name="realConfig"/> and the per-user folder
    /// (APPDATA, XDG_CONFIG_HOME) inside the test folder, so a developer's own config is never looked at.
    /// </summary>
    private (int Code, string Output) RunScript(string realConfig, params string[] args) =>
        Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, .. args], Env(realConfig));

    private Dictionary<string, string> Env(string? realConfig)
    {
        var env = new Dictionary<string, string> { ["APPDATA"] = Path.Combine(_dir, "appdata"), ["XDG_CONFIG_HOME"] = Path.Combine(_dir, "xdg") };
        env["LANQ_CONFIG"] = realConfig ?? "";
        return env;
    }

    /// <summary>The script's functions loaded (-LoadOnly, nothing runs), then <paramref name="code"/>.</summary>
    private (int Code, string Output) WithFunctions(string code, string? realConfig = null) =>
        Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $". '{Script}' -LoadOnly; {code}"], Env(realConfig));

    private static string[] Lines(string output) => output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [PowerShellFact]
    public void SmokeArgumentsAreChecked()
    {
        var config = File("machines.smoke.yml", Real);
        var (code, output) = WithFunctions(
            $"Test-SmokeArgs '' 'x' 'release/0.9.2'; Test-SmokeArgs '12a' 'x' 'release/0.9.2'; Test-SmokeArgs '123' 'x' ''; Test-SmokeArgs '123' 'x' 'main'; " +
            $"Test-SmokeArgs '123' 'x' '-x'; Test-SmokeArgs '123' 'x' 'a..b'; Test-SmokeArgs '123' '' 'v0.9.2'; Test-SmokeArgs '123' '{_dir}\\none.yml' 'v0.9.2'; " +
            $"if ($null -eq (Test-SmokeArgs '123' '{config}' 'v0.9.2')) {{ 'OK' }}");
        Assert.Equal(0, code);
        var lines = Lines(output);
        Assert.Equal(9, lines.Length);
        Assert.StartsWith("give -Run", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("-Run must be a run id", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("give -Branch", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("-Branch is the default branch", lines[3], StringComparison.Ordinal);
        Assert.Contains("is not a branch name", lines[4], StringComparison.Ordinal);
        Assert.Contains("is not a branch name", lines[5], StringComparison.Ordinal);
        Assert.StartsWith("give -Config", lines[6], StringComparison.Ordinal);
        Assert.StartsWith("-Config: no file", lines[7], StringComparison.Ordinal);
        Assert.Equal("OK", lines[8]);

        // As a script: refused with exit 2 before anything.
        var (exit, said) = RunScript(config, "-Run", "abc", "-Branch", "v0.9.2", "-Config", config);
        Assert.Equal(2, exit);
        Assert.Contains("-Run must be a run id", said, StringComparison.Ordinal);
        (exit, said) = RunScript(config, "-Run", "123", "-Config", config);
        Assert.Equal(2, exit);
        Assert.Contains("give -Branch", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real config is refused: as given, in another case (Windows), through .., through a link, by a relative LANQ_CONFIG, a copy or
    /// hard link (same SHA-256), and the per-user file too when LANQ_CONFIG points elsewhere.
    /// </summary>
    [PowerShellFact]
    public void SmokeScriptRefusesTheRealConfig()
    {
        var real = File("machines.yml", Real);
        var variants = new List<string> { real, Path.Combine(_dir, "sub", "..", "machines.yml"), File("copy.yml", Real) };
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
            // creating a symbolic link needs Developer Mode or admin on Windows: that variant is left out (the hash check still catches it)
        }

        foreach (var config in variants)
        {
            var (code, output) = RunScript(real, "-Run", "123", "-Branch", "v0.9.2", "-Config", config);
            Assert.True(code == 2, config + ": " + output);
            Assert.True(output.Contains("is your real config", StringComparison.Ordinal) || output.Contains("same content as your real config", StringComparison.Ordinal), output);
        }

        // A relative LANQ_CONFIG is resolved from the current folder, once, like any path.
        var (c1, o1) = Shells.Exec(
            Shells.PowerShell!,
            ["-NoProfile", "-NonInteractive", "-Command", $"Set-Location '{_dir}'; . '{Script}' -LoadOnly; Get-RealConfigPath"],
            Env("machines.yml"));
        Assert.Equal(0, c1);
        Assert.Equal(real, Lines(o1)[^1], ignoreCase: OperatingSystem.IsWindows());

        // The per-user file is refused even when LANQ_CONFIG names another one.
        var perUser = OperatingSystem.IsWindows() ? Path.Combine(_dir, "appdata", "lanq", "machines.yml") : Path.Combine(_dir, "xdg", "lanq", "machines.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(perUser)!);
        System.IO.File.WriteAllText(perUser, Real.Replace("max_jobs: 1", "max_jobs: 1   # per-user", StringComparison.Ordinal));
        var (c2, o2) = RunScript(real, "-Run", "123", "-Branch", "v0.9.2", "-Config", perUser);
        Assert.Equal(2, c2);
        Assert.Contains("is your real config", o2, StringComparison.Ordinal);

        // A different smoke config is not refused by this check.
        var smoke = File("machines.smoke.yml", Real + "# smoke\n");
        var (c3, o3) = WithFunctions($"if ($null -eq (Test-NotRealConfig '{smoke}' (Get-RealConfigPaths))) {{ 'NOT-REAL' }}", real);
        Assert.Equal(0, c3);
        Assert.Contains("NOT-REAL", o3, StringComparison.Ordinal);
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
        Assert.Contains("its work_root differs from the real config's", Compare(Real.Replace("/srv/lanq", "/srv/other", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("its max_jobs differs", Compare(Real.Replace("max_jobs: 2", "max_jobs: 4", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("its ssh differs", Compare(Real.Replace("build@10.20.30.11", "other@10.20.30.11", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("is not in the real config", Compare(Real.Replace("build-01", "build-09", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", Compare("machines:\n  - {name: build-01, ssh: x, work_root: y}\n"), StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", Compare("machines: [x]\n"), StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", Compare("machines:\n  - name: build-01\n    os: linux\n"), StringComparison.Ordinal); // no ssh line
        Assert.StartsWith("REFUSED", Compare("machines:\n  - name: build-01\n    os: darwin\n    ssh: a@b\n    work_root: /x\n"), StringComparison.Ordinal);
        Assert.Contains("lists no machines", Compare("policy: {}\n"), StringComparison.Ordinal);

        // os defaults to windows and max_jobs to 1, as in lanq.
        var plain = File("plain.yml", "machines:\n  - name: m1\n    ssh: a@10.20.30.31\n    work_root: C:/lq\n");
        var (c, o) = WithFunctions($"$m = Read-MachineIdentities '{plain}'; $m['m1'].os; $m['m1'].max_jobs");
        Assert.Equal(0, c);
        Assert.Equal(["windows", "1"], Lines(o));

        // As a script: a mismatch is refused with exit 2 before anything runs.
        var smokeFile = File("machines.smoke.yml", Real.Replace("/srv/lanq", "/srv/other", StringComparison.Ordinal));
        var (exit, said) = RunScript(real, "-Run", "123", "-Branch", "v0.9.2", "-Config", smokeFile);
        Assert.Equal(2, exit);
        Assert.Contains("must equal the real ones", said, StringComparison.Ordinal);
    }

    /// <summary>A config shaped like a real one (flow lists, nested lists and maps, comments, key_env, quoting) parses; nested keys never count.</summary>
    private const string RealShaped = """
        # machines.yml
        policy:
          allowed_commands:
            - "dotnet test"
          allowed_env: [CONFIGURATION]
        machines:
          - name: build-01            # the big one
            os: windows
            ssh: "build@10.20.30.11"   # quoted
            key_env: LANQ_KEY_BUILD01
            work_root: 'D:/lq'
            capabilities: [dotnet, windows, node]
            max_jobs: 2
            path_prepend:
              - D:/build/dotnet
              - D:/tools/bin
            env:
              DOTNET_ROOT: D:/build/dotnet
              name: not-a-machine
            wake:
              mac: "aa:bb:cc:dd:ee:ff"
            notes:
              - name: also-not-a-machine
                ssh: nobody@10.20.30.99
          - name: micro-01
            ssh: build@10.20.30.21
            work_root: C:/lq
            capabilities:
            - dotnet
            - windows
        """;

    [PowerShellFact]
    public void SmokeParsesARealShapedConfig()
    {
        var file = File("machines.yml", RealShaped);
        var (code, output) = WithFunctions(
            $"$m = Read-MachineIdentities '{file}'; $m.Keys -join ','; foreach ($k in $m.Keys) {{ '{{0}}|{{1}}|{{2}}|{{3}}|{{4}}' -f $m[$k].name, $m[$k].os, $m[$k].ssh, $m[$k].work_root, $m[$k].max_jobs }}");
        Assert.True(code == 0, output);
        Assert.Equal(
            ["build-01,micro-01", "build-01|windows|build@10.20.30.11|D:/lq|2", "micro-01|windows|build@10.20.30.21|C:/lq|1"],
            Lines(output));

        // Entries at column 0 under machines: are read too.
        var flat = File("flat.yml", "machines:\n- name: m1\n  ssh: a@10.20.30.31\n  work_root: /srv/lq\n  os: linux\n");
        (code, output) = WithFunctions($"(Read-MachineIdentities '{flat}')['m1'].os");
        Assert.Equal((0, "linux"), (code, output.Trim()));
    }

    /// <summary>Every refusal names the file, the line number, the key and the machine name only: never line text, values, hosts, users or paths.</summary>
    [PowerShellFact]
    public void SmokeRefusalsNeverEchoConfigContent()
    {
        const string Secretish = "zz-private-host-77";
        string[] bad =
        [
            $"machines:\n  - name: m1\n    ssh: [{Secretish}@10.20.30.77]\n    work_root: /srv/{Secretish}\n",
            $"machines:\n  - ssh: {Secretish}@10.20.30.77\n    name: m1\n",
            $"machines: [{Secretish}]\n",
            $"machines:\n  - name: m1\n    ssh: \"{Secretish}\\x\"\n    work_root: /x\n",
            $"machines:\n  - name: m1\n    ssh: a@b\n   work_root: /srv/{Secretish}\n",
            $"machines:\n  - name: m1\n    work_root: /srv/{Secretish}\n",
            $"machines:\n  - {{name: m1, ssh: {Secretish}}}\n",
        ];
        for (var k = 0; k < bad.Length; k++)
        {
            var file = File($"bad{k}.yml", bad[k]);
            var (code, output) = WithFunctions($"try {{ [void](Read-MachineIdentities '{file}'); 'PARSED' }} catch {{ 'REFUSED: ' + $_.Exception.Message }}");
            Assert.Equal(0, code);
            Assert.StartsWith("REFUSED: bad" + k + ".yml", output.Trim(), StringComparison.Ordinal);
            Assert.DoesNotContain(Secretish, output, StringComparison.Ordinal);
            Assert.DoesNotContain("10.20.30.77", output, StringComparison.Ordinal);
        }

        // Differences are named by key, never shown.
        var real = File("machines.yml", Real);
        var smoke = File("machines.smoke.yml", Real.Replace("/srv/lanq", "/srv/" + Secretish, StringComparison.Ordinal));
        var (exit, said) = RunScript(real, "-Run", "123", "-Branch", "v0.9.2", "-Config", smoke);
        Assert.Equal(2, exit);
        Assert.Contains("its work_root differs", said, StringComparison.Ordinal);
        Assert.DoesNotContain(Secretish, said, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/lanq", said, StringComparison.Ordinal);

        // Notes and messages hide every ssh target, host and work_root of the machines.
        var (c2, o2) = WithFunctions(
            $"Set-HiddenValues @((Read-MachineIdentities '{real}')); Hide-ConfigValues 'ssh: connect to host 10.20.30.11 port 22 as build@10.20.30.11 in /srv/lanq/jobs/x'");
        Assert.Equal(0, c2);
        Assert.Equal("ssh: connect to host <host of build-01> port 22 as <ssh of build-01> in <work_root of build-01>/jobs/x", o2.Trim());
    }

    /// <summary>
    /// Assert-LanqResult on real-shaped outcomes (the status lines of the second real smoke): the last stderr line must start with
    /// "[lanq] Status" as plain text (a -like pattern took "[lanq]" for a character class), with the exit code, status and one JSON object.
    /// </summary>
    [PowerShellFact]
    public void SmokeAssertsTheLanqContract()
    {
        string Check(int code, string status, string stdout, string stderr, int wantCode, string wantStatus, bool timedOut = false)
        {
            var r = $"@{{ Code = {code}; TimedOut = ${(timedOut ? "true" : "false")}; Stdout = '{stdout.Replace("'", "''", StringComparison.Ordinal)}'; Stderr = \"{stderr}\" }}";
            var (exit, output) = WithFunctions($"try {{ $j = Assert-LanqResult ({r}) {wantCode} '{wantStatus}'; 'PASS ' + $j.status }} catch {{ 'FAIL ' + $_.Exception.Message }}");
            Assert.Equal(0, exit);
            return output.Trim();
        }

        static string Json(string status, int code) => $"{{\"status\":\"{status}\",\"exitCode\":{code},\"message\":\"m\",\"jobId\":\"j1\"}}";

        Assert.Equal("PASS Completed", Check(0, "Completed", Json("Completed", 0), "SMOKE_START 1`n[lanq] Completed: exit 0`n", 0, "Completed"));
        Assert.Equal("PASS Rejected", Check(2, "Rejected", Json("Rejected", 2), "[lanq] Rejected: Refused: possible secrets in payload (...). Nothing was sent.`n", 2, "Rejected"));
        Assert.Equal("PASS Failed", Check(1, "Failed", Json("Failed", 1), "x`n[lanq] baseline: new`n[lanq] Failed: exit 1`n", 1, "Failed"));
        Assert.Equal("PASS WouldRun", Check(0, "WouldRun", Json("WouldRun", 0), "[lanq] WouldRun: would run on build-01`n", 0, "WouldRun"));

        Assert.StartsWith("FAIL the last stderr line", Check(0, "Completed", Json("Completed", 0), "[lanq] Failed: exit 1`n", 0, "Completed"), StringComparison.Ordinal);
        Assert.StartsWith("FAIL the last stderr line", Check(0, "Completed", Json("Completed", 0), "q Completed: exit 0`n", 0, "Completed"), StringComparison.Ordinal); // not a pattern
        Assert.StartsWith("FAIL the last stderr line", Check(0, "Completed", Json("Completed", 0), "", 0, "Completed"), StringComparison.Ordinal);
        Assert.StartsWith("FAIL exit code 1", Check(1, "Completed", Json("Completed", 0), "[lanq] Completed: exit 0`n", 0, "Completed"), StringComparison.Ordinal);
        Assert.StartsWith("FAIL status Failed", Check(1, "Failed", Json("Failed", 1), "[lanq] Failed: exit 1`n", 1, "Completed"), StringComparison.Ordinal);
        Assert.StartsWith("FAIL stdout is not", Check(0, "Completed", Json("Completed", 0) + Json("Completed", 0), "[lanq] Completed: exit 0`n", 0, "Completed"), StringComparison.Ordinal);
        Assert.StartsWith("FAIL lanq did not finish", Check(-1, "Completed", "", "", 0, "Completed", timedOut: true), StringComparison.Ordinal);
    }

    /// <summary>The run must be a successful release workflow_dispatch run on the branch, at its head; the binary must carry +headSha.</summary>
    [PowerShellFact]
    public void SmokeRunAndBinaryAreBound()
    {
        var sha = new string('a', 40);
        const string Ok = "@{ workflowName = 'release'; event = 'workflow_dispatch'; conclusion = 'success'; headBranch = 'v0.9.2'; headSha = 'SHA' }";
        string Check(string view, string branch = "v0.9.2", string remote = "SHA", string defaultBranch = "main")
        {
            var (code, output) = WithFunctions(
                $"$v = [pscustomobject]({view.Replace("SHA", sha, StringComparison.Ordinal)}); $p = Test-SmokeRun $v '{branch}' '{remote.Replace("SHA", sha, StringComparison.Ordinal)}' '{defaultBranch}'; if ($null -eq $p) {{ 'OK' }} else {{ $p }}");
            Assert.Equal(0, code);
            return output.Trim();
        }

        Assert.Equal("OK", Check(Ok));
        Assert.Contains("not release", Check(Ok.Replace("'release'", "'ci'", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("push run", Check(Ok.Replace("'workflow_dispatch'", "'push'", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("not success", Check(Ok.Replace("'success'", "'failure'", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("dispatched on 'main'", Check(Ok.Replace("'v0.9.2'", "'main'", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("default branch", Check(Ok.Replace("'v0.9.2'", "'trunk'", StringComparison.Ordinal), branch: "trunk", defaultBranch: "trunk"), StringComparison.Ordinal);
        Assert.Contains("dispatch a new dry run", Check(Ok, remote: new string('b', 40)), StringComparison.Ordinal);

        string Version(string result)
        {
            var (code, output) = WithFunctions($"$p = Test-SmokeVersion ({result}) '{sha}'; if ($null -eq $p) {{ 'OK' }} else {{ $p }}");
            Assert.Equal(0, code);
            return output.Trim();
        }

        Assert.Equal("OK", Version($"@{{ Code = 0; TimedOut = $false; Stdout = 'lanq 0.0.0-dryrun.7+{sha}' }}"));
        Assert.Contains("exited 1", Version($"@{{ Code = 1; TimedOut = $false; Stdout = 'lanq 0.0.0-dryrun.7+{sha}' }}"), StringComparison.Ordinal);
        Assert.Contains("does not carry", Version("@{ Code = 0; TimedOut = $false; Stdout = 'lanq 0.9.2+0123' }"), StringComparison.Ordinal);
    }

    /// <summary>Exit 0 only when something passed and nothing failed; SKIP fails unless -AllowSkip; N/A never counts.</summary>
    [PowerShellFact]
    public void SmokeExitCodeCountsSkipsButNotNA()
    {
        string Exit(string results, string allow = "$false")
        {
            var (code, output) = WithFunctions($"Get-SmokeExitCode @({results}) {allow}");
            Assert.Equal(0, code);
            return output.Trim();
        }

        static string R(string outcome) => $"[pscustomobject]@{{ Result = '{outcome}' }}";
        Assert.Equal("0", Exit($"{R("PASS")}, {R("N/A")}"));
        Assert.Equal("1", Exit($"{R("PASS")}, {R("SKIP")}"));
        Assert.Equal("0", Exit($"{R("PASS")}, {R("SKIP")}", "$true"));
        Assert.Equal("1", Exit($"{R("PASS")}, {R("FAIL")}", "$true"));
        Assert.Equal("1", Exit($"{R("N/A")}, {R("N/A")}")); // nothing ran at all
    }

    /// <summary>
    /// Every file write, delete or folder creation is in Write-SmokeFile, Remove-SmokeFile, Expand-SmokeZip or Remove-SmokeTemp (which stay
    /// under the smoke folder); no cmdlet that writes files and no redirection appears anywhere.
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
        var lines = Lines(output).Select(l => l.Split(' ')).ToList();
        Assert.Contains(lines, l => l[0] == "MEMBER" && l[1] == "WriteAllText"); // the dump works

        string[] writers = ["Set-Content", "Add-Content", "Out-File", "New-Item", "Remove-Item", "Copy-Item", "Move-Item", "Rename-Item", "Clear-Content", "Export-Csv", "Tee-Object", "Expand-Archive"];
        Assert.DoesNotContain(lines, l => l[0] == "CMD" && writers.Contains(l[1], StringComparer.OrdinalIgnoreCase));
        Assert.All(lines.Where(l => l[0] == "REDIRECT"), l => Assert.True(l[1] == "2>&1", string.Join(' ', l)));

        string[] fileMembers = ["WriteAllText", "WriteAllBytes", "WriteAllLines", "AppendAllText", "Delete", "CreateDirectory", "Move", "Copy", "ExtractToDirectory"];
        string[] owners = ["Write-SmokeFile", "Remove-SmokeFile", "Remove-SmokeTemp", "Expand-SmokeZip"];
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

    /// <summary>
    /// Scenario 8 (0.10.0): the binary is probed first (before 0.10.0 it is N/A), then --plan-timeout 1 must give TimedOut 124, --on must put
    /// every job on the machine named, and a narrowing that leaves a job no machine must fail closed (NoCapacity 75, a rerun with --only). Each
    /// plan job keeps the smoke's 5-minute limit, and the script still parses.
    /// </summary>
    [PowerShellFact]
    public void SmokeScriptRunsTheZeroTenScenarios()
    {
        var text = System.IO.File.ReadAllText(Script);
        Assert.Contains("'this binary has no --plan-timeout (before 0.10.0)'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the scenario is not written yet", text, StringComparison.Ordinal);
        Assert.Contains("(Lanq @('run', '--plan', '--plan-timeout', '1', '--on', $main, '--json', '--no-wake', '--wait', '300')) 124 'TimedOut'", text, StringComparison.Ordinal);
        Assert.Contains("$j.planTimedOut -ne $true", text, StringComparison.Ordinal);
        Assert.Contains("(Lanq @('run', '--plan', '--on', $main, '--json', '--no-wake', '--wait', '300')) 0 'Completed'", text, StringComparison.Ordinal);
        Assert.Contains("(Lanq @('run', '--plan', '--size', 'light', '--json', '--no-wake')) 75 'NoCapacity'", text, StringComparison.Ordinal);
        foreach (var name in new[] { "'8a plan timeout'", "'8b --on narrowing'", "'8c narrowing fails closed'" })
        {
            var at = text.IndexOf(name, StringComparison.Ordinal);
            Assert.True(at > 0, name);
            var body = text[at..text.IndexOf("Remove-SmokeFile 'repo/lanq.yml'", at, StringComparison.Ordinal)];
            Assert.Contains("timeout: 5", body, StringComparison.Ordinal);
        }

        const string Parse = """
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$errors)
            'ERRORS ' + @($errors).Count
            """;
        var (code, output) = Shells.Exec(Shells.PowerShell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", File("parse.ps1", Parse), Script]);
        Assert.True(code == 0, output);
        Assert.Contains("ERRORS 0", output, StringComparison.Ordinal);
    }

    /// <summary>LANQ_HOME is a fresh folder under the smoke folder before lanq is first run, and every variable it sets is restored at the end.</summary>
    [Fact]
    public void SmokeScriptSetsLanqHome()
    {
        var text = System.IO.File.ReadAllText(Script);
        var set = text.IndexOf("$env:LANQ_HOME = Join-Path $script:SmokeTemp 'home'", StringComparison.Ordinal);
        Assert.True(set > 0);
        var firstLanq = text.IndexOf("Invoke-Smoke $lanq", StringComparison.Ordinal);
        Assert.True(firstLanq > set, "lanq runs before LANQ_HOME is set");
        Assert.True(text.IndexOf("foreach ($n in @('LANQ_HOME', 'LANQ_CONFIG') + $gitNames) { $saved[$n] =", StringComparison.Ordinal) < set, "saved before it is set");
        Assert.Contains("foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Test-NotRealConfig $Config $realConfigs", StringComparison.Ordinal) < set, "the real-config refusal comes first");
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
