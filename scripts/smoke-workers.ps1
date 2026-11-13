<#
.SYNOPSIS
  Release-candidate smoke on real workers (RELEASING.md step 1b). Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
  smoke-workers.ps1 -Run <release dry-run id> -Config <machines.smoke.yml> [-Branch <release branch>] [-Keep]

  1. Downloads the native-win-x64 artifact of that workflow_dispatch release run (gh run download) and checks the run's
     headSha against the release branch's head on origin (default: the run's own branch). It runs that binary only,
     never the installed lanq.
  2. Isolation: refuses -Config when it resolves (links followed; case-insensitive on Windows) to your real config
     (LANQ_CONFIG, else the per-user machines.yml), and checks the real file's SHA-256 is the same at the end; it only
     reads it. LANQ_HOME is a fresh temporary folder. Before anything runs, every smoke machine's ssh, work_root and
     max_jobs must equal the real config's (a minimal line parser for the flat machines list; anything else is refused),
     so the smoke's slot locks are the ones real jobs use.
  3. A throwaway repository (fixed author, committer and dates, so its repo id is stable) runs the scenarios: normal
     runs on each OS, refusals (not allowlisted, an env name not allowed, a secret in --env, a secret in a -diff file),
     env values byte-exact, exclusive, baseline new and preexisting, --shards 2, a plan (run and dry run), the 0.10
     items (skipped before 0.10.0), and no leftover slots. Every job runs with --timeout 5.
  4. Prints a PASS/FAIL/SKIP table. Exit 0 when nothing failed, 1 when a scenario failed, 2 refused before anything ran
     (arguments, the real config, an identity mismatch), 70 setup failed (gh, the download, git).

  Everything it writes is under one temporary folder, removed at the end unless -Keep. The smoke config's policy:
  allowed_commands "sh smoke/check.sh" and "powershell -NoProfile -ExecutionPolicy Bypass -File smoke/check.ps1",
  allowed_directories ["."], allowed_env [SMOKE_VALUE] (see RELEASING.md). Agents run this only when the developer asks.
#>
param(
    [string]$Run,
    [string]$Config,
    [string]$Branch,
    [switch]$Keep,
    # Defines the functions and returns without running anything (tests).
    [switch]$LoadOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1

function Test-IsWindows {
    return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}

# ---- arguments -----------------------------------------------------------------------------------------------------

# The first problem with the arguments, or $null.
function Test-SmokeArgs([string]$RunId, [string]$ConfigPath) {
    if (-not $RunId) { return 'give -Run <id of a workflow_dispatch release run> (gh run list --workflow release.yml)' }
    if ($RunId -notmatch '^[0-9]{1,20}$') { return "-Run must be a run id (digits only), not '$RunId'" }
    if (-not $ConfigPath) { return 'give -Config <your smoke config, machines.smoke.yml next to machines.yml>' }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return "-Config: no file at '$ConfigPath'" }
    return $null
}

# ---- the real config -----------------------------------------------------------------------------------------------

# The config lanq itself would read without --config: LANQ_CONFIG, else %APPDATA%\lanq\machines.yml on Windows, else
# $XDG_CONFIG_HOME/lanq/machines.yml (a full path only) or ~/.config/lanq/machines.yml.
function Get-RealConfigPath {
    if ($env:LANQ_CONFIG) { return $env:LANQ_CONFIG }
    if (Test-IsWindows) {
        if ($env:APPDATA) { return (Join-Path (Join-Path $env:APPDATA 'lanq') 'machines.yml') }
        return $null
    }
    if ($env:XDG_CONFIG_HOME -and [System.IO.Path]::IsPathRooted($env:XDG_CONFIG_HOME)) { return (Join-Path (Join-Path $env:XDG_CONFIG_HOME 'lanq') 'machines.yml') }
    if ($env:HOME) { return (Join-Path (Join-Path (Join-Path $env:HOME '.config') 'lanq') 'machines.yml') }
    return $null
}

# The full path of $Path with every symbolic link or junction in it followed (the file itself and its folders).
function Resolve-RealPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    for ($hop = 0; $hop -lt 40; $hop++) {
        $changed = $false
        $parts = New-Object System.Collections.Generic.List[string]
        $cur = $full
        while ($cur) {
            $item = Get-Item -LiteralPath $cur -Force -ErrorAction SilentlyContinue
            if ($item -and $item.LinkType -in @('SymbolicLink', 'Junction')) {
                $target = @($item.Target)[0]
                if (-not [System.IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path -Parent $cur) $target }
                $rest = $parts.ToArray(); [array]::Reverse($rest)
                $full = [System.IO.Path]::GetFullPath($(if ($rest.Count) { Join-Path $target ($rest -join [System.IO.Path]::DirectorySeparatorChar) } else { $target }))
                $changed = $true
                break
            }
            $parent = Split-Path -Parent $cur
            if (-not $parent -or $parent -eq $cur) { break }
            $parts.Add((Split-Path -Leaf $cur))
            $cur = $parent
        }
        if (-not $changed) { return $full }
    }
    throw "too many links while resolving $Path"
}

# True when the two paths name the same file after links are followed (case-insensitive on Windows).
function Test-SamePath([string]$A, [string]$B) {
    $ra = Resolve-RealPath $A
    $rb = Resolve-RealPath $B
    if (Test-IsWindows) { return [string]::Equals($ra, $rb, [StringComparison]::OrdinalIgnoreCase) }
    return [string]::Equals($ra, $rb, [StringComparison]::Ordinal)
}

# The refusal when -Config is the real config, or $null.
function Test-NotRealConfig([string]$SmokeConfig, [string]$RealConfig) {
    if ($RealConfig -and (Test-Path -LiteralPath $RealConfig) -and (Test-SamePath $SmokeConfig $RealConfig)) {
        return "-Config is your real config ($RealConfig); the smoke never runs on it. Give the separate smoke config (machines.smoke.yml)."
    }
    return $null
}

# ---- the identity check --------------------------------------------------------------------------------------------

function ConvertFrom-YamlScalar([string]$Text) {
    $v = $Text.Trim()
    if ($v.StartsWith('"')) {
        $end = $v.IndexOf('"', 1)
        if ($end -lt 0 -or $v.Substring(1, $end - 1).Contains('\')) { throw "a quoted value lanq's smoke cannot read: $v" }
        return $v.Substring(1, $end - 1)
    }
    if ($v.StartsWith("'")) {
        $end = $v.IndexOf("'", 1)
        if ($end -lt 0) { throw "a quoted value lanq's smoke cannot read: $v" }
        return $v.Substring(1, $end - 1)
    }
    $hash = $v.IndexOf(' #')
    if ($hash -ge 0) { $v = $v.Substring(0, $hash) }
    return $v.Trim()
}

# name -> @{ ssh; work_root; max_jobs; os } from the flat machines: list ("  - name: x" then "    key: value" lines). Anything
# it is not sure about (flow style, a duplicate name, a machine without ssh or work_root) throws: the caller refuses.
function Read-MachineIdentities([string]$Path) {
    $machines = [ordered]@{}
    $current = $null
    $inMachines = $false
    foreach ($raw in [System.IO.File]::ReadAllLines($Path)) {
        $line = $raw.TrimEnd()
        if ($line -match '^\s*(#|$)') { continue }
        if ($line -match '^[A-Za-z_]') {
            $inMachines = $line -match '^machines:\s*$'
            if ($line -match '^machines:\s*\S') { throw 'machines: in flow style; the smoke reads only a block list' }
            $current = $null
            continue
        }
        if (-not $inMachines) { continue }
        if ($line -match '^\s*-\s*\{' -or $line -match '^\s*-\s*$') { throw 'a machine in flow style or an empty list item; the smoke reads only "- name: x" entries' }
        if ($line -match '^\s*-\s+name:\s*(.+)$') {
            $name = ConvertFrom-YamlScalar $Matches[1]
            if ($machines.Contains($name)) { throw "machine '$name' is listed twice" }
            $current = @{ name = $name; ssh = $null; work_root = $null; max_jobs = '1'; os = $null }
            $machines[$name] = $current
            continue
        }
        if ($line -match '^\s*-\s') { throw "a machine entry that does not start with name: ($line)" }
        if ($null -ne $current -and $line -match '^\s+(ssh|work_root|max_jobs|os):\s*(.*)$') {
            $current[$Matches[1]] = ConvertFrom-YamlScalar $Matches[2]
        }
    }
    foreach ($m in $machines.Values) {
        if (-not $m.ssh -or -not $m.work_root) { throw "machine '$($m.name)' has no ssh or work_root line the smoke can read" }
    }
    return $machines
}

# The differences that make the smoke machines unsafe to use: each must be in the real config with the same ssh, work_root
# and max_jobs (so their slot locks are shared with real jobs). An empty list means they match.
function Compare-MachineIdentities($Smoke, $Real) {
    $problems = New-Object System.Collections.Generic.List[string]
    if ($Smoke.Count -eq 0) { $problems.Add('the smoke config lists no machines') }
    foreach ($name in $Smoke.Keys) {
        $s = $Smoke[$name]
        if (-not $Real.Contains($name)) { $problems.Add("smoke machine '$name' is not in the real config"); continue }
        $r = $Real[$name]
        foreach ($key in 'ssh', 'work_root', 'max_jobs') {
            if (-not [string]::Equals([string]$s[$key], [string]$r[$key], [StringComparison]::Ordinal)) {
                $problems.Add("smoke machine '$name': $key is '$($s[$key])', the real config has '$($r[$key])'")
            }
        }
    }
    return , $problems.ToArray()
}

# ---- files (everything written goes under $script:SmokeTemp) -------------------------------------------------------

$script:SmokeTemp = $null

function Write-SmokeFile([string]$Relative, [string]$Text) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $script:SmokeTemp $Relative))
    if (-not $path.StartsWith($script:SmokeTemp + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to write outside the smoke folder: $path" }
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllText($path, $Text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
    return $path
}

function Remove-SmokeFile([string]$Relative) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $script:SmokeTemp $Relative))
    if (-not $path.StartsWith($script:SmokeTemp + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to delete outside the smoke folder: $path" }
    if ([System.IO.File]::Exists($path)) { [System.IO.File]::Delete($path) }
}

function Remove-SmokeTemp {
    if ($script:SmokeTemp -and (Split-Path -Leaf $script:SmokeTemp) -like 'lanq-smoke-*' -and (Test-Path -LiteralPath $script:SmokeTemp)) {
        Get-ChildItem -LiteralPath $script:SmokeTemp -Recurse -Force | ForEach-Object { $_.Attributes = 'Normal' }
        [System.IO.Directory]::Delete($script:SmokeTemp, $true)
    }
}

# ---- processes -----------------------------------------------------------------------------------------------------

# Runs $File with $Arguments in $Dir; returns @{ Code; Stdout; Stderr }. No shell: arguments are passed as given.
function Start-Smoke([string]$File, [string[]]$Arguments, [string]$Dir) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.WorkingDirectory = $Dir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.Arguments = ($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    return @{ Process = $p; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync() }
}

function Wait-Smoke($Started) {
    $Started.Process.WaitForExit()
    return @{ Code = $Started.Process.ExitCode; Stdout = $Started.Out.Result; Stderr = $Started.Err.Result }
}

function Invoke-Smoke([string]$File, [string[]]$Arguments, [string]$Dir) { return Wait-Smoke (Start-Smoke $File $Arguments $Dir) }

# One argument quoted for CommandLineToArgvW (the rule .NET uses to split a Windows command line).
function ConvertTo-WindowsArgument([string]$Arg) {
    if ($Arg.Length -gt 0 -and $Arg -notmatch '[\s"]') { return $Arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($c in $Arg.ToCharArray()) {
        if ($c -eq '\') { $slashes++; continue }
        if ($c -eq '"') { [void]$sb.Append('\', 2 * $slashes + 1); [void]$sb.Append('"'); $slashes = 0; continue }
        [void]$sb.Append('\', $slashes); [void]$sb.Append($c); $slashes = 0
    }
    [void]$sb.Append('\', 2 * $slashes)
    [void]$sb.Append('"')
    return $sb.ToString()
}

# ---- results -------------------------------------------------------------------------------------------------------

$script:Results = New-Object System.Collections.Generic.List[object]
$script:JobIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)

function Add-Result([string]$Scenario, [string]$Outcome, [string]$Note) {
    $script:Results.Add([pscustomobject]@{ Scenario = $Scenario; Result = $Outcome; Note = $Note })
    Write-Host ('{0,-4} {1}: {2}' -f $Outcome, $Scenario, $Note)
}

# Every jobId anywhere in a lanq JSON object (run, shards, plan jobs and their shards, baselines).
function Add-JobIds($Json) {
    if ($null -eq $Json) { return }
    if ($Json -is [System.Array]) { foreach ($x in $Json) { Add-JobIds $x }; return }
    if ($Json -isnot [System.Management.Automation.PSCustomObject]) { return }
    foreach ($prop in $Json.PSObject.Properties) {
        if ($prop.Name -eq 'jobId' -and $prop.Value -is [string]) { [void]$script:JobIds.Add($prop.Value) }
        else { Add-JobIds $prop.Value }
    }
}

# The contract of every --json call: the exit code, exactly one JSON object on stdout, "[lanq] <Status>" last on stderr.
# Returns the parsed object, or throws with what was wrong.
function Assert-LanqResult($R, [int]$Code, [string]$Status) {
    $text = $R.Stdout.Trim()
    try { $json = $text | ConvertFrom-Json } catch { throw "stdout is not one JSON object (exit $($R.Code)): $($text.Substring(0, [Math]::Min(200, $text.Length)))" }
    if ($null -eq $json -or $json -is [System.Array]) { throw 'stdout is not exactly one JSON object' }
    Add-JobIds $json
    $last = @($R.Stderr.Replace("`r", '').Split("`n") | Where-Object { $_ })[-1]
    if ($R.Code -ne $Code) { throw "exit code $($R.Code), expected $Code ($($json.message))" }
    if ($json.status -ne $Status) { throw "status $($json.status), expected $Status ($($json.message))" }
    if ($last -notlike "[lanq] $Status*") { throw "the last stderr line is '$last', expected '[lanq] $Status...'" }
    return $json
}

function Get-TailValue($Json, [string]$Pattern) {
    foreach ($line in @($Json.logTail)) { if ($line -match $Pattern) { return $Matches[1] } }
    return $null
}

# ---- main ----------------------------------------------------------------------------------------------------------

if ($LoadOnly) { return }

$argProblem = Test-SmokeArgs $Run $Config
if ($argProblem) { Write-Host "smoke-workers: $argProblem"; exit 2 }
$realConfig = Get-RealConfigPath
if (-not $realConfig -or -not (Test-Path -LiteralPath $realConfig -PathType Leaf)) {
    Write-Host "smoke-workers: no real config found (LANQ_CONFIG or the per-user machines.yml); the identity check needs it."; exit 2
}
$refusal = Test-NotRealConfig $Config $realConfig
if ($refusal) { Write-Host "smoke-workers: $refusal"; exit 2 }
$realHash = (Get-FileHash -LiteralPath $realConfig -Algorithm SHA256).Hash
try {
    $smokeMachines = Read-MachineIdentities $Config
    $realMachines = Read-MachineIdentities $realConfig
} catch {
    Write-Host "smoke-workers: refused, a config it cannot read safely: $($_.Exception.Message)"; exit 2
}
$problems = Compare-MachineIdentities $smokeMachines $realMachines
if ($problems.Count -gt 0) {
    Write-Host 'smoke-workers: refused, the smoke machines must equal the real ones (ssh, work_root, max_jobs):'
    $problems | ForEach-Object { Write-Host "  $_" }
    exit 2
}

$script:SmokeTemp = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) ('lanq-smoke-' + [guid]::NewGuid().ToString('N'))))
[void][System.IO.Directory]::CreateDirectory($script:SmokeTemp)
$savedHome = $env:LANQ_HOME
$savedConfig = $env:LANQ_CONFIG
$env:LANQ_HOME = Join-Path $script:SmokeTemp 'home'
$configFull = [System.IO.Path]::GetFullPath($Config)
$exitCode = 0
try {
    # The binary: the dry run's native-win-x64 artifact, from a run whose headSha is the release branch's head.
    $view = & gh run view $Run --json headSha,headBranch,event,workflowName,conclusion | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $view) { $exitCode = 70; throw 'gh run view failed' }
    if ($view.event -ne 'workflow_dispatch') { $exitCode = 2; throw "run $Run is a $($view.event) run; give a workflow_dispatch release dry run" }
    $branchName = $(if ($Branch) { $Branch } else { $view.headBranch })
    $remote = (& git ls-remote origin "refs/heads/$branchName") -split '\s+' | Select-Object -First 1
    if ($remote -ne $view.headSha) { $exitCode = 2; throw "run $Run built $($view.headSha), but $branchName on origin is at $remote; dispatch a new dry run (gh workflow run release.yml --ref $branchName)" }
    $binDir = Join-Path $script:SmokeTemp 'bin'
    & gh run download $Run -n native-win-x64 -D $binDir
    if ($LASTEXITCODE -ne 0) { $exitCode = 70; throw 'gh run download failed' }
    $lanq = @(Get-ChildItem -LiteralPath $binDir -Recurse -Filter 'lanq.exe')[0].FullName
    $version = (Invoke-Smoke $lanq @('--version') $script:SmokeTemp).Stdout.Trim()
    Write-Host "smoke-workers: $version from run $Run ($($view.headSha)), config $configFull, LANQ_HOME $env:LANQ_HOME"

    # The throwaway repository: fixed identity and dates, so its root commit (and repo id) never changes.
    $repo = Join-Path $script:SmokeTemp 'repo'
    $checkSh = @'
#!/bin/sh
# lanq smoke check (scripts/smoke-workers.ps1): run SECONDS CODE|file [LABEL], or list-slots.
set -eu
case "${1:-}" in
run)
  code=$3
  if [ "$code" = file ]; then code=$(cat smoke/exit-code); fi
  echo "SMOKE_START $(date +%s)"
  echo "SMOKE_VALUE=[${SMOKE_VALUE:-}]"
  echo "SMOKE_LABEL=${4:-}"
  sleep "$2"
  echo "SMOKE_END $(date +%s)"
  exit "$code" ;;
list-slots)
  d=$(pwd)
  while [ "$d" != / ] && ! { [ -d "$d/locks" ] && [ -d "$d/jobs" ]; }; do d=$(dirname "$d"); done
  [ -d "$d/locks" ] || { echo "SMOKE_NO_LOCKS"; exit 0; }
  for f in "$d"/locks/slot-*; do [ -f "$f" ] || continue; echo "SMOKE_SLOT $(head -n 1 "$f")"; done
  echo "SMOKE_SELF ${LANQ_JOB:-}" ;;
*) echo "usage: check.sh run SECONDS CODE [LABEL] | list-slots" >&2; exit 2 ;;
esac
'@
    $checkPs = @'
# lanq smoke check (scripts/smoke-workers.ps1): run SECONDS CODE|file [LABEL], or list-slots.
param([string]$Mode, [int]$Seconds = 0, [string]$Code = '0', [string]$Label = '')
switch ($Mode) {
    'run' {
        if ($Code -eq 'file') { $Code = ([IO.File]::ReadAllText('smoke/exit-code')).Trim() }
        "SMOKE_START $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
        "SMOKE_VALUE=[$env:SMOKE_VALUE]"
        "SMOKE_LABEL=$Label"
        Start-Sleep -Seconds $Seconds
        "SMOKE_END $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
        exit ([int]$Code)
    }
    'list-slots' {
        $d = (Get-Location).Path
        while ($d -and -not ((Test-Path "$d/locks") -and (Test-Path "$d/jobs"))) { $d = Split-Path -Parent $d }
        if (-not $d) { 'SMOKE_NO_LOCKS'; exit 0 }
        foreach ($f in Get-ChildItem "$d/locks" -Filter 'slot-*' -ErrorAction SilentlyContinue) { "SMOKE_SLOT $(([IO.File]::ReadAllText($f.FullName) -split "`n")[0].Trim())" }
        "SMOKE_SELF $env:LANQ_JOB"
    }
    default { 'usage: check.ps1 run SECONDS CODE [LABEL] | list-slots'; exit 2 }
}
'@
    $plan = @'
version: 1
defaults:
  needs: [linux]
  timeout: 5
jobs:
  first:
    command: sh smoke/check.sh run 1 0 first
  second:
    command: sh smoke/check.sh run 1 0 second
    after: [first]
  sharded:
    command: sh smoke/check.sh run 1 0 "{shard}"
    shards: 2
    after: [first]
'@
    [void](Write-SmokeFile 'repo/smoke/check.sh' $checkSh)
    [void](Write-SmokeFile 'repo/smoke/check.ps1' $checkPs)
    [void](Write-SmokeFile 'repo/lanq.yml' $plan)
    [void](Write-SmokeFile 'repo/.gitattributes' "*.dat -diff`n")
    [void](Write-SmokeFile 'repo/smoke/exit-code' "0`n")
    $env:GIT_AUTHOR_NAME = 'lanq smoke'; $env:GIT_AUTHOR_EMAIL = 'smoke@example.invalid'; $env:GIT_AUTHOR_DATE = '2026-01-01T00:00:00Z'
    $env:GIT_COMMITTER_NAME = 'lanq smoke'; $env:GIT_COMMITTER_EMAIL = 'smoke@example.invalid'; $env:GIT_COMMITTER_DATE = '2026-01-01T00:00:00Z'
    function Git([string[]]$GitArgs) {
        $r = Invoke-Smoke 'git' (@('-c', 'commit.gpgsign=false', '-c', 'core.autocrlf=false') + $GitArgs) $repo
        if ($r.Code -ne 0) { throw "git $($GitArgs -join ' ') failed: $($r.Stderr)" }
    }
    Git @('init', '-q', '-b', 'main')
    Git @('add', '-A'); Git @('commit', '-q', '-m', 'smoke base'); Git @('tag', 'smoke-pass')
    [void](Write-SmokeFile 'repo/smoke/exit-code' "1`n"); Git @('add', '-A'); Git @('commit', '-q', '-m', 'smoke fails'); Git @('tag', 'smoke-fail')
    [void](Write-SmokeFile 'repo/README.md' "lanq smoke repository`n"); Git @('add', '-A'); Git @('commit', '-q', '-m', 'smoke head')

    function LanqArgs([string[]]$A) { if ($A.Count -gt 1) { return @($A[0], '--config', $configFull) + $A[1..($A.Count - 1)] } return @($A[0], '--config', $configFull) }
    function Lanq([string[]]$A) { return Invoke-Smoke $lanq (LanqArgs $A) $repo }
    $common = @('--json', '--timeout', '5', '--no-wake', '--log-tail', '200')

    # Machines: one Linux, one Windows, one with max_jobs >= 2, among the reachable smoke machines (never named in this script).
    $health = (Lanq @('health', '--json')).Stdout | ConvertFrom-Json
    $up = @($health | Where-Object { $_.state -eq 'ok' } | ForEach-Object { $_.name })
    $linux = @($smokeMachines.Values | Where-Object { $_.os -eq 'linux' -and $up -contains $_.name } | ForEach-Object { $_.name }) | Select-Object -First 1
    $windows = @($smokeMachines.Values | Where-Object { $_.os -eq 'windows' -and $up -contains $_.name } | ForEach-Object { $_.name }) | Select-Object -First 1
    $multi = @($smokeMachines.Values | Where-Object { [int]$_.max_jobs -ge 2 -and $up -contains $_.name }) | Select-Object -First 1
    function Check([string]$Os) { if ($Os -eq 'windows') { return @('powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'smoke/check.ps1') } return @('sh', 'smoke/check.sh') }
    function OsOf([string]$Name) { return $smokeMachines[$Name].os }

    function Scenario([string]$Name, [scriptblock]$Body) {
        try { $note = & $Body; Add-Result $Name 'PASS' ([string]$note) }
        catch { Add-Result $Name 'FAIL' $_.Exception.Message }
    }

    # 1. A normal run on each OS.
    foreach ($m in @($linux, $windows)) {
        if (-not $m) { Add-Result '1 normal run' 'SKIP' 'no reachable machine of one OS in the smoke config'; continue }
        Scenario "1 normal run ($(OsOf $m))" { [void](Assert-LanqResult (Lanq (@('run') + $common + @('--on', $m, '--') + (Check (OsOf $m)) + @('run', '1', '0'))) 0 'Completed'); 'Completed 0' }
    }

    # 2. Refusals: nothing may run.
    $any = $(if ($linux) { $linux } else { $windows })
    if ($any) {
        $os = OsOf $any
        Scenario '2a not allowlisted' { [void](Assert-LanqResult (Lanq (@('run') + $common + @('--on', $any, '--', 'sh', 'smoke/other.sh'))) 2 'Rejected'); 'Rejected 2' }
        Scenario '2b env name not allowed' {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $any, '--env', 'NOT_LISTED=x', '--') + (Check $os) + @('run', '1', '0'))) 2 'Rejected'
            if (@($j.nextSteps | Where-Object { $_.action -eq 'ask_developer' }).Count -eq 0) { throw 'no ask_developer next step' }
            'Rejected, ask_developer'
        }
        Scenario '2c secret in --env' {
            $token = 'ghp' + '_' + (-join ((1..36) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) }))
            $r = Lanq (@('run') + $common + @('--on', $any, '--env', "SMOKE_VALUE=$token", '--') + (Check $os) + @('run', '1', '0'))
            [void](Assert-LanqResult $r 2 'Rejected')
            if (($r.Stdout + $r.Stderr).Contains($token)) { throw 'the token was echoed' }
            'Rejected/Secret, value not echoed'
        }
        Scenario '2d secret in a -diff file (A1)' {
            [void](Write-SmokeFile 'repo/secret.dat' ("key = AKIA" + (-join ((1..16) | ForEach-Object { [char](Get-Random -Minimum 65 -Maximum 91) })) + "`n"))
            try {
                $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $any, '--') + (Check $os) + @('run', '1', '0'))) 2 'Rejected'
                if ($j.message -notlike '*secret.dat (binary per git)*') { throw "the refusal does not name the file: $($j.message)" }
            } finally { Remove-SmokeFile 'repo/secret.dat' }
            'Rejected/Secret, file named'
        }
    } else { Add-Result '2 refusals' 'SKIP' 'no reachable smoke machine' }

    # 3. env: a value with spaces, $, quotes and a backtick comes back byte-exact.
    $value = 'a b $c ''d'' "e" `f'
    foreach ($m in @($linux, $windows)) {
        if (-not $m) { Add-Result '3 env byte-exact' 'SKIP' 'no reachable machine of one OS'; continue }
        Scenario "3 env byte-exact ($(OsOf $m))" {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $m, '--env', "SMOKE_VALUE=$value", '--') + (Check (OsOf $m)) + @('run', '0', '0'))) 0 'Completed'
            $got = Get-TailValue $j 'SMOKE_VALUE=\[(.*)\]'
            if ($got -cne $value) { throw "got [$got]" }
            'byte-exact'
        }
    }

    # 4. exclusive: a plain and an --exclusive job started together on a max_jobs >= 2 machine never overlap.
    if ($multi) {
        Scenario '4 exclusive' {
            $base = @('run') + $common + @('--on', $multi.name, '--wait', '300')
            $plain = Start-Smoke $lanq (LanqArgs ($base + @('--') + (Check $multi.os) + @('run', '20', '0'))) $repo
            Start-Sleep -Seconds 2
            $excl = Start-Smoke $lanq (LanqArgs ($base + @('--exclusive', '--') + (Check $multi.os) + @('run', '20', '0'))) $repo
            $a = Assert-LanqResult (Wait-Smoke $plain) 0 'Completed'
            $b = Assert-LanqResult (Wait-Smoke $excl) 0 'Completed'
            $as = [long](Get-TailValue $a 'SMOKE_START (\d+)'); $ae = [long](Get-TailValue $a 'SMOKE_END (\d+)')
            $bs = [long](Get-TailValue $b 'SMOKE_START (\d+)'); $be = [long](Get-TailValue $b 'SMOKE_END (\d+)')
            if (-not ($ae -le $bs -or $be -le $as)) { throw "intervals overlap: [$as,$ae] and [$bs,$be]" }
            'no overlap'
        }
    } else { Add-Result '4 exclusive' 'SKIP' 'no reachable smoke machine with max_jobs >= 2' }

    # 5. baseline: new (the base passes), then preexisting (the base fails too). HEAD's smoke/exit-code is 1.
    if ($linux) {
        Scenario '5 baseline new' {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $linux, '--baseline-ref', 'smoke-pass', '--', 'sh', 'smoke/check.sh', 'run', '1', 'file'))) 1 'Failed'
            if ($j.baseline.verdict -ne 'new') { throw "verdict $($j.baseline.verdict)" }
            'new'
        }
        Scenario '5 baseline preexisting' {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $linux, '--baseline-ref', 'smoke-fail', '--', 'sh', 'smoke/check.sh', 'run', '1', 'file'))) 1 'Failed'
            if ($j.baseline.verdict -ne 'preexisting') { throw "verdict $($j.baseline.verdict)" }
            'preexisting'
        }

        # 6. --shards 2 with {shard}.
        Scenario '6 shards' {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--needs', 'linux', '--wait', '300', '--shards', '2', '--', 'sh', 'smoke/check.sh', 'run', '1', '0', '{shard}'))) 0 'Completed'
            if ($j.shardCount -ne 2) { throw "shardCount $($j.shardCount)" }
            '2 shards'
        }

        # 7. A plan with after: and a sharded job, run and dry run.
        Scenario '7 plan' {
            $j = Assert-LanqResult (Lanq @('run', '--plan', '--json', '--no-wake', '--wait', '300')) 0 'Completed'
            if (@($j.jobs).Count -ne 3) { throw "$(@($j.jobs).Count) jobs" }
            [void](Assert-LanqResult (Lanq @('run', '--plan', '--dry-run', '--json', '--no-wake')) 0 'WouldRun')
            'Completed; dry run WouldRun'
        }
    } else { Add-Result '5-7 baseline, shards, plan' 'SKIP' 'no reachable Linux smoke machine' }

    # 8. 0.10.0 items.
    if ($version -match 'lanq (\d+)\.(\d+)' -and ([int]$Matches[1] -gt 0 -or [int]$Matches[2] -ge 10)) {
        Add-Result '8 plan timeout, --on narrowing' 'FAIL' 'not written yet for 0.10.0'
    } else { Add-Result '8 plan timeout, --on narrowing' 'SKIP' 'from 0.10.0' }

    # 9. Leftover slots: lanq unlock on each smoke machine (removes only slots past their deadline), then a read-only list.
    Scenario '9 no leftover slots' {
        $left = New-Object System.Collections.Generic.List[string]
        foreach ($m in @($smokeMachines.Keys | Where-Object { $up -contains $_ })) {
            $u = Lanq @('unlock', $m)
            if ($u.Code -ne 0) { throw "lanq unlock $m exited $($u.Code)" }
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $m, '--wait', '300', '--') + (Check (OsOf $m)) + @('list-slots'))) 0 'Completed'
            $self = Get-TailValue $j 'SMOKE_SELF (\S+)'
            foreach ($line in @($j.logTail)) {
                if ($line -match 'SMOKE_SLOT (\S+)' -and $Matches[1] -ne $self -and $script:JobIds.Contains($Matches[1])) { $left.Add("$m`: $($Matches[1])") }
            }
        }
        if ($left.Count -gt 0) { throw "smoke slots left: $($left -join ', ')" }
        "$($script:JobIds.Count) smoke job id(s), none left"
    }
} catch {
    if ($exitCode -eq 0) { $exitCode = 70 }
    Write-Host "smoke-workers: $($_.Exception.Message)"
} finally {
    $env:LANQ_HOME = $savedHome
    $env:LANQ_CONFIG = $savedConfig
    foreach ($n in 'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_AUTHOR_DATE', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL', 'GIT_COMMITTER_DATE') { [Environment]::SetEnvironmentVariable($n, $null) }
    if ((Get-FileHash -LiteralPath $realConfig -Algorithm SHA256).Hash -ne $realHash) {
        Write-Host "smoke-workers: FAIL: the real config $realConfig changed during the smoke"
        $exitCode = 1
    }
    if ($Keep) { Write-Host "smoke-workers: kept $script:SmokeTemp" } else { Remove-SmokeTemp }
}

if ($exitCode -ne 0) { exit $exitCode }
''
'| Scenario | Result | Note |'
'| --- | --- | --- |'
foreach ($r in $script:Results) { "| $($r.Scenario) | $($r.Result) | $($r.Note) |" }
if (@($script:Results | Where-Object { $_.Result -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
