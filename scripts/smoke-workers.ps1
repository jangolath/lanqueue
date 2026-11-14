<#
.SYNOPSIS
  Release-candidate smoke on real workers (RELEASING.md step 1b). Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
  smoke-workers.ps1 -Run <release dry-run id> -Branch <release branch> -Config <machines.smoke.yml> [-AllowSkip] [-Keep]

  1. The binary: -Run must be a successful workflow_dispatch run of the release workflow on -Branch (never the default
     branch), whose headSha is that branch's head on origin. Its native-win-x64 artifact is downloaded (gh run download)
     and unpacked, and its "lanq --version" must exit 0 and carry "+<headSha>". It never runs the installed lanq.
  2. Isolation: refuses -Config when it is your real config: LANQ_CONFIG, and the per-user machines.yml too (resolved
     once, links followed, case-insensitive on Windows), or any file with the same SHA-256 (a hard link or a copy). The
     real files' SHA-256 must be the same at the end; it only reads them. LANQ_HOME is a fresh temporary folder, and
     LANQ_HOME, LANQ_CONFIG and the GIT_AUTHOR_*/GIT_COMMITTER_* variables are restored at the end. Before anything
     runs, every smoke machine's ssh, work_root and max_jobs must equal the real config's (a minimal line parser for
     the flat machines list; anything else is refused), so the smoke's slot locks are the ones real jobs use.
  3. A throwaway repository (fixed author, committer and dates, so its repo id is stable) runs the scenarios: normal
     runs on each OS, refusals (not allowlisted, an env name not allowed, a secret in --env, a secret in a -diff file),
     env values byte-exact, exclusive, baseline new and preexisting, --shards 2, a plan (run and dry run), the 0.10
     items when this binary has them (--plan-timeout 1 gives TimedOut 124, --on narrows every plan job, and a narrowing
     that leaves a job no machine is NoCapacity 75 with a rerun --only), the 0.11 test results when this binary has them
     (--results junit counts a fixed file and names its failure; no file is noTests with ask_developer), and no leftover
     slots. Every job runs with --timeout 5; every lanq call is
     stopped after 15 minutes.
  4. Prints a PASS/FAIL/SKIP/N/A table. N/A: the smoke config has no machine of that kind (it never fails). SKIP: the
     scenario could run but did not (a machine down); it fails the smoke unless -AllowSkip. A configured machine that is
     not reachable, or health that fails, is a FAIL. Exit 0 only when every scenario that applies passed, 1 otherwise,
     2 refused before anything ran (arguments, the real config, an identity mismatch, the run), 70 setup failed.

  Everything it writes is under one temporary folder, removed at the end unless -Keep. The smoke config lives in the
  per-user lanq folder (%APPDATA%\lanq\machines.smoke.yml on Windows, ~/.config/lanq/machines.smoke.yml elsewhere);
  its policy: allowed_commands "sh smoke/check.sh" and "powershell -NoProfile -ExecutionPolicy Bypass -File
  smoke/check.ps1", allowed_directories ["."], allowed_env [SMOKE_VALUE] (see RELEASING.md). Agents run this only when
  the developer asks.
#>
param(
    [string]$Run,
    [string]$Config,
    [string]$Branch,
    [switch]$AllowSkip,
    [switch]$Keep,
    # Defines the functions and returns without running anything (tests).
    [switch]$LoadOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1

# Windows PowerShell 5.1 started from PowerShell 7 inherits 7's PSModulePath and then cannot load its own script-module
# commands (Get-FileHash fails). Use 5.1's own module path, and avoid such commands anyway (Get-Sha256 below).
if ($PSVersionTable.PSEdition -ne 'Core' -and [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $env:PSModulePath = (@(
            [IO.Path]::Combine([Environment]::GetFolderPath('MyDocuments'), 'WindowsPowerShell', 'Modules'),
            [IO.Path]::Combine($env:ProgramFiles, 'WindowsPowerShell', 'Modules'),
            [Environment]::GetEnvironmentVariable('PSModulePath', 'Machine')) | Where-Object { $_ }) -join ';'
}

# The SHA-256 of a file as upper-case hex (.NET only: no module to load).
function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

function Test-IsWindows {
    return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}

# ---- arguments -----------------------------------------------------------------------------------------------------

# The first problem with the arguments, or $null.
function Test-SmokeArgs([string]$RunId, [string]$ConfigPath, [string]$BranchName) {
    if (-not $RunId) { return 'give -Run <id of a workflow_dispatch release run> (gh run list --workflow release.yml --branch <release branch>)' }
    if ($RunId -notmatch '^[0-9]{1,20}$') { return "-Run must be a run id (digits only), not '$RunId'" }
    if (-not $BranchName) { return 'give -Branch <the release branch the dry run was dispatched on>' }
    if ($BranchName -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$' -or $BranchName.Contains('..')) { return "-Branch '$BranchName' is not a branch name" }
    if ($BranchName -in @('main', 'master')) { return "-Branch is the default branch ($BranchName); smoke the release branch, dispatched with gh workflow run release.yml --ref <branch>" }
    if (-not $ConfigPath) { return 'give -Config <your smoke config: machines.smoke.yml in the per-user lanq folder>' }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return "-Config: no file at '$ConfigPath'" }
    return $null
}

# ---- the real config -----------------------------------------------------------------------------------------------

# A path as PowerShell resolves it from the current location (relative paths, ~), made full. Used for every path compared.
function Resolve-SmokePath([string]$Path) {
    return [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

# The per-user config file lanq reads without --config or LANQ_CONFIG: %APPDATA%\lanq\machines.yml on Windows, else
# $XDG_CONFIG_HOME/lanq/machines.yml (a full path only) or ~/.config/lanq/machines.yml.
function Get-DefaultConfigPath {
    if (Test-IsWindows) {
        if ($env:APPDATA) { return (Join-Path (Join-Path $env:APPDATA 'lanq') 'machines.yml') }
        return $null
    }
    if ($env:XDG_CONFIG_HOME -and [System.IO.Path]::IsPathRooted($env:XDG_CONFIG_HOME)) { return (Join-Path (Join-Path $env:XDG_CONFIG_HOME 'lanq') 'machines.yml') }
    if ($env:HOME) { return (Join-Path (Join-Path (Join-Path $env:HOME '.config') 'lanq') 'machines.yml') }
    return $null
}

# The config lanq itself would read without --config (LANQ_CONFIG, else the per-user file), resolved.
function Get-RealConfigPath {
    if ($env:LANQ_CONFIG) { return (Resolve-SmokePath $env:LANQ_CONFIG) }
    $default = Get-DefaultConfigPath
    if ($default) { return (Resolve-SmokePath $default) }
    return $null
}

# Every real config that exists: LANQ_CONFIG's file and the per-user file (both refused as -Config, both hash-checked).
function Get-RealConfigPaths {
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($p in @($(if ($env:LANQ_CONFIG) { Resolve-SmokePath $env:LANQ_CONFIG }), $(if (Get-DefaultConfigPath) { Resolve-SmokePath (Get-DefaultConfigPath) }))) {
        if ($p -and (Test-Path -LiteralPath $p -PathType Leaf) -and -not $paths.Contains($p)) { $paths.Add($p) }
    }
    return , $paths.ToArray()
}

# The full path of $Path with every symbolic link or junction in it followed (the file itself and its folders).
function Resolve-RealPath([string]$Path) {
    $full = Resolve-SmokePath $Path
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

# The refusal when -Config is one of the real configs (the same file, or the same content: a hard link or a copy), or $null.
function Test-NotRealConfig([string]$SmokeConfig, [string[]]$RealConfigs) {
    $smokeHash = (Get-Sha256 (Resolve-SmokePath $SmokeConfig))
    foreach ($real in $RealConfigs) {
        if (-not $real -or -not (Test-Path -LiteralPath $real -PathType Leaf)) { continue }
        if (Test-SamePath $SmokeConfig $real) {
            return "-Config is your real config ($real); the smoke never runs on it. Give the separate smoke config (machines.smoke.yml)."
        }
        if ((Get-Sha256 $real) -eq $smokeHash) {
            return "-Config has the same content as your real config ($real): a hard link or a copy. The smoke config needs its own policy (machines.smoke.yml)."
        }
    }
    return $null
}

# ---- the identity check --------------------------------------------------------------------------------------------
# Privacy: nothing here ever puts config content (line text, values, hosts, users, paths) into a message. Errors name the
# file, the line number, the key and the machine name only.

# The plain value of a key the smoke reads: unquoted (an inline " #" comment dropped), "double" (no escapes) or 'single'
# ('' is a quote). A flow, block, anchor, alias or tag value, or an empty one, throws: the smoke reads only plain scalars.
function ConvertFrom-YamlScalar([string]$Text, [string]$Where) {
    $v = $Text.Trim()
    if ($v.StartsWith('"')) {
        $end = $v.IndexOf('"', 1)
        if ($end -lt 0 -or $v.Substring(1, $end - 1).Contains('\')) { throw "$Where`: a double-quoted value the smoke cannot read (no escapes, closed on the line)" }
        $after = $v.Substring($end + 1).Trim()
        if ($after -and -not $after.StartsWith('#')) { throw "$Where`: text after a quoted value" }
        return $v.Substring(1, $end - 1)
    }
    if ($v.StartsWith("'")) {
        $m = [regex]::Match($v, "^'((?:[^']|'')*)'\s*(#.*)?$")
        if (-not $m.Success) { throw "$Where`: a single-quoted value the smoke cannot read" }
        return $m.Groups[1].Value.Replace("''", "'")
    }
    $hash = [regex]::Match($v, '\s#')
    if ($hash.Success) { $v = $v.Substring(0, $hash.Index) }
    $v = $v.Trim()
    if (-not $v) { throw "$Where`: no plain value on the line" }
    if ($v[0] -in @('[', '{', '|', '>', '&', '*', '!', '%', '@', '`')) { throw "$Where`: a flow, block, anchor, alias or tag value; write a plain value" }
    return $v
}

# name -> @{ name; ssh; work_root; max_jobs; os } from the machines: block list. An entry starts only at the indent of the
# first "- " item directly under the top-level machines: key, and must start with "- name:". Of each entry, only name, os,
# ssh, work_root and max_jobs at the entry's own key indent are read; deeper lines (path_prepend: items, env: maps, any
# nested name:) and other keys (capabilities: [a, b], key_env, ...) are skipped. os defaults to windows and max_jobs to 1,
# as in lanq. Anything it is not sure about (machines: in flow style, an entry that is not "- name:", odd indentation, a
# tab in the indentation, a value it cannot read for one of its keys, a duplicate name, a machine without ssh or work_root)
# throws: the caller refuses.
function Read-MachineIdentities([string]$Path) {
    $file = [System.IO.Path]::GetFileName($Path)
    $wanted = @('name', 'os', 'ssh', 'work_root', 'max_jobs')
    $machines = [ordered]@{}
    $current = $null
    $inMachines = $false
    $itemIndent = -1
    $keyIndent = -1
    $lines = [System.IO.File]::ReadAllLines($Path)
    for ($n = 0; $n -lt $lines.Length; $n++) {
        $where = "$file line $($n + 1)"
        $line = $lines[$n].TrimEnd()
        $body = $line.TrimStart(' ')
        if (-not $body -or $body.StartsWith('#')) { continue }
        if ($body.StartsWith("`t") -or $line.Substring(0, $line.Length - $body.Length).Contains("`t")) { throw "$where`: a tab in the indentation" }
        $indent = $line.Length - $body.Length
        if ($indent -eq 0 -and -not $body.StartsWith('-')) {
            # A top-level key: machines: starts the list, anything else ends it.
            $inMachines = $body -match '^machines\s*:'
            if ($inMachines -and $body -notmatch '^machines\s*:\s*(#.*)?$') { throw "$where`: machines: in flow style; the smoke reads only a block list" }
            $current = $null
            continue
        }
        if (-not $inMachines) { continue }
        if ($itemIndent -lt 0) {
            if (-not $body.StartsWith('-')) { throw "$where`: expected a ""- name:"" entry under machines:" }
            $itemIndent = $indent
        }
        if ($indent -lt $itemIndent) { throw "$where`: indentation the smoke cannot read under machines:" }
        if ($indent -eq $itemIndent) {
            if ($body -notmatch '^-(\s+|$)') { throw "$where`: an entry under machines: that is not a list item" }
            $rest = $body.Substring(1).TrimStart(' ')
            if ($rest -notmatch '^name\s*:(\s|$)') { throw "$where`: a machine entry that does not start with name:" }
            $keyIndent = $indent + ($body.Length - $rest.Length)
            $name = ConvertFrom-YamlScalar ($rest -replace '^name\s*:', '') "$where (key name)"
            if ($machines.Contains($name)) { throw "$where`: machine '$name' is listed twice" }
            $current = @{ name = $name; ssh = $null; work_root = $null; max_jobs = '1'; os = 'windows' }
            $machines[$name] = $current
            continue
        }
        if ($indent -lt $keyIndent) { throw "$where`: indentation the smoke cannot read in machine '$($current.name)'" }
        if ($indent -gt $keyIndent -or $body.StartsWith('-')) { continue } # nested content, or an item of a list at the key indent
        $m = [regex]::Match($body, '^([A-Za-z_][A-Za-z0-9_-]*)\s*:(\s|$)')
        if (-not $m.Success) { throw "$where`: a line the smoke cannot read in machine '$($current.name)'" }
        $key = $m.Groups[1].Value
        if ($key -eq 'name') { throw "$where`: a second name: in machine '$($current.name)'" }
        if ($wanted -contains $key) { $current[$key] = ConvertFrom-YamlScalar $body.Substring($m.Length) "$where (key $key)" }
    }
    foreach ($mc in $machines.Values) {
        if (-not $mc.ssh -or -not $mc.work_root) { throw "$file`: machine '$($mc.name)' has no ssh or work_root line the smoke can read" }
        if ($mc.os -notin @('windows', 'linux')) { throw "$file`: machine '$($mc.name)' has an os the smoke does not know (windows and linux)" }
        if ($mc.max_jobs -notmatch '^[1-9][0-9]{0,2}$') { throw "$file`: machine '$($mc.name)' has a max_jobs the smoke cannot read" }
    }
    return $machines
}

# The differences that make the smoke machines unsafe to use: each must be in the real config with the same ssh, work_root
# and max_jobs (so their slot locks are shared with real jobs). An empty list means they match. Names only, never values.
function Compare-MachineIdentities($Smoke, $Real) {
    $problems = New-Object System.Collections.Generic.List[string]
    if ($Smoke.Count -eq 0) { $problems.Add('the smoke config lists no machines') }
    foreach ($name in $Smoke.Keys) {
        $s = $Smoke[$name]
        if (-not $Real.Contains($name)) { $problems.Add("smoke machine '$name' is not in the real config"); continue }
        $r = $Real[$name]
        foreach ($key in 'ssh', 'work_root', 'max_jobs') {
            if (-not [string]::Equals([string]$s[$key], [string]$r[$key], [StringComparison]::Ordinal)) {
                $problems.Add("smoke machine '$name': its $key differs from the real config's")
            }
        }
    }
    return , $problems.ToArray()
}

# $Text with every ssh target (and its host part) and work_root of the machines replaced by a placeholder naming the
# machine, so no message or table note shows them (a worker or ssh error may quote them).
function Hide-ConfigValues([string]$Text) {
    if (-not $Text -or -not $script:HiddenValues) { return $Text }
    foreach ($h in $script:HiddenValues) {
        $Text = [regex]::Replace($Text, [regex]::Escape($h.Value), $h.Placeholder, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    return $Text
}

# The values Hide-ConfigValues hides, longest first, from every machine of the given configs.
function Set-HiddenValues($Configs) {
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($cfg in $Configs) {
        foreach ($mc in $cfg.Values) {
            foreach ($pair in @(@($mc.ssh, "<ssh of $($mc.name)>"), @(($mc.ssh -replace '^[^@]*@', ''), "<host of $($mc.name)>"), @($mc.work_root, "<work_root of $($mc.name)>"),
                    @(($mc.work_root -replace '\\', '/'), "<work_root of $($mc.name)>"), @(($mc.work_root -replace '/', '\'), "<work_root of $($mc.name)>"))) {
                if ($pair[0] -and $pair[0].Length -ge 3) { $list.Add([pscustomobject]@{ Value = [string]$pair[0]; Placeholder = $pair[1] }) }
            }
        }
    }
    $script:HiddenValues = @($list | Sort-Object { $_.Value.Length } -Descending)
}

$script:HiddenValues = @()

# ---- the run -------------------------------------------------------------------------------------------------------

# Why the run cannot be smoked, or $null. $View is gh run view's JSON; $RemoteHead the branch's head on origin.
function Test-SmokeRun($View, [string]$BranchName, [string]$RemoteHead, [string]$DefaultBranch) {
    if ($View.workflowName -ne 'release') { return "run is of workflow '$($View.workflowName)', not release" }
    if ($View.event -ne 'workflow_dispatch') { return "run is a $($View.event) run; give a workflow_dispatch release dry run" }
    if ($View.conclusion -ne 'success') { return "run concluded '$($View.conclusion)', not success" }
    if ($View.headBranch -ne $BranchName) { return "run was dispatched on '$($View.headBranch)', not $BranchName" }
    if ($DefaultBranch -and $BranchName -eq $DefaultBranch) { return "-Branch is the default branch ($DefaultBranch)" }
    if ($RemoteHead -ne $View.headSha) { return "run built $($View.headSha), but $BranchName on origin is at $RemoteHead; dispatch a new dry run (gh workflow run release.yml --ref $BranchName)" }
    return $null
}

# Why "lanq --version" does not show the run's binary, or $null: it must exit 0 and carry +<headSha> (release.yml
# publishes with SourceRevisionId, dry runs included).
function Test-SmokeVersion($Result, [string]$HeadSha) {
    if ($Result.TimedOut -or $Result.Code -ne 0) { return "lanq --version exited $($Result.Code)" }
    if (-not $Result.Stdout.Contains('+' + $HeadSha)) { return "lanq --version does not carry +$HeadSha`: $($Result.Stdout.Trim())" }
    return $null
}

# ---- files (everything written goes under $script:SmokeTemp) -------------------------------------------------------

$script:SmokeTemp = $null

function Get-SmokeChild([string]$Relative) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $script:SmokeTemp $Relative))
    if (-not $path.StartsWith($script:SmokeTemp + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing a path outside the smoke folder: $path" }
    return $path
}

function Write-SmokeFile([string]$Relative, [string]$Text) {
    $path = Get-SmokeChild $Relative
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllText($path, $Text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
    return $path
}

function Remove-SmokeFile([string]$Relative) {
    $path = Get-SmokeChild $Relative
    if ([System.IO.File]::Exists($path)) { [System.IO.File]::Delete($path) }
}

function Expand-SmokeZip([string]$Zip, [string]$Relative) {
    $dest = Get-SmokeChild $Relative
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $dest)
    return $dest
}

# Best effort: a failure here never hides the table.
function Remove-SmokeTemp {
    try {
        if ($script:SmokeTemp -and (Split-Path -Leaf $script:SmokeTemp) -like 'lanq-smoke-*' -and (Test-Path -LiteralPath $script:SmokeTemp)) {
            Get-ChildItem -LiteralPath $script:SmokeTemp -Recurse -Force | ForEach-Object { $_.Attributes = 'Normal' }
            [System.IO.Directory]::Delete($script:SmokeTemp, $true)
        }
    } catch {
        Write-Host "smoke-workers: could not remove $script:SmokeTemp ($($_.Exception.Message)); delete it by hand"
    }
}

# ---- processes -----------------------------------------------------------------------------------------------------

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

# Starts $File with $Arguments in $Dir (no shell: arguments are passed as given).
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

# Waits at most $Minutes; past that the process tree is killed and the result says TimedOut. Returns @{ Code; Stdout; Stderr; TimedOut }.
function Wait-Smoke($Started, [int]$Minutes = 15) {
    $p = $Started.Process
    if (-not $p.WaitForExit($Minutes * 60000)) {
        Stop-SmokeProcess $p
        return @{ Code = -1; Stdout = ''; Stderr = ''; TimedOut = $true }
    }
    $p.WaitForExit()
    # The output pipes can stay open after the process ends (a child that kept them): wait at most a minute more for them.
    if (-not $Started.Out.Wait(60000) -or -not $Started.Err.Wait(60000)) {
        Stop-SmokeProcess $p
        return @{ Code = -1; Stdout = ''; Stderr = ''; TimedOut = $true }
    }
    return @{ Code = $p.ExitCode; Stdout = $Started.Out.Result; Stderr = $Started.Err.Result; TimedOut = $false }
}

function Stop-SmokeProcess($P) {
    try {
        if (-not $P.HasExited) {
            if (Test-IsWindows) { & taskkill /T /F /PID $P.Id 2>&1 | Out-Null } else { $P.Kill() }
            [void]$P.WaitForExit(10000)
        }
    } catch {
        # it exited in the meantime
    }
}

function Invoke-Smoke([string]$File, [string[]]$Arguments, [string]$Dir, [int]$Minutes = 15) {
    $started = Start-Smoke $File $Arguments $Dir
    try { return Wait-Smoke $started $Minutes } finally { Stop-SmokeProcess $started.Process }
}

# ---- results -------------------------------------------------------------------------------------------------------

$script:Results = New-Object System.Collections.Generic.List[object]
$script:JobIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)

function Add-Result([string]$Scenario, [string]$Outcome, [string]$Note) {
    $Note = Hide-ConfigValues $Note
    $script:Results.Add([pscustomobject]@{ Scenario = $Scenario; Result = $Outcome; Note = $Note })
    Write-Host ('{0,-4} {1}: {2}' -f $Outcome, $Scenario, $Note)
}

# The smoke's exit code from its results: 1 on any FAIL, or on any SKIP unless skips are allowed; N/A never counts.
function Get-SmokeExitCode($Results, [bool]$SkipAllowed) {
    if (@($Results | Where-Object { $_.Result -eq 'FAIL' }).Count -gt 0) { return 1 }
    if (-not $SkipAllowed -and @($Results | Where-Object { $_.Result -eq 'SKIP' }).Count -gt 0) { return 1 }
    if (@($Results | Where-Object { $_.Result -eq 'PASS' }).Count -eq 0) { return 1 }
    return 0
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
    if ($R.TimedOut) { throw 'lanq did not finish within its limit and was stopped' }
    $text = $R.Stdout.Trim()
    try { $json = $text | ConvertFrom-Json } catch { throw "stdout is not one JSON object (exit $($R.Code)): $($text.Substring(0, [Math]::Min(200, $text.Length)))" }
    if ($null -eq $json -or $json -is [System.Array]) { throw 'stdout is not exactly one JSON object' }
    Add-JobIds $json
    $last = @($R.Stderr.Replace("`r", '').Split("`n") | Where-Object { $_ })[-1]
    if ($R.Code -ne $Code) { throw "exit code $($R.Code), expected $Code ($($json.message))" }
    if ($json.status -ne $Status) { throw "status $($json.status), expected $Status ($($json.message))" }
    # A plain prefix test: with -like, "[lanq]" would be a wildcard character class.
    if (-not $last -or -not $last.StartsWith("[lanq] $Status", [StringComparison]::Ordinal)) { throw "the last stderr line is '$last', expected one starting '[lanq] $Status'" }
    return $json
}

function Get-TailValue($Json, [string]$Pattern) {
    foreach ($line in @($Json.logTail)) { if ($line -match $Pattern) { return $Matches[1] } }
    return $null
}

# The machine names "lanq health --json" says are ok; throws when health fails or is not an array.
function Read-SmokeHealth($R) {
    if ($R.TimedOut -or $R.Code -ne 0) { throw "lanq health exited $($R.Code): $($R.Stderr.Trim())" }
    $text = $R.Stdout.Trim()
    if (-not $text.StartsWith('[')) { throw "lanq health --json did not print an array: $($text.Substring(0, [Math]::Min(200, $text.Length)))" }
    return @(($text | ConvertFrom-Json) | Where-Object { $_.state -eq 'ok' } | ForEach-Object { $_.name })
}

# ---- main ----------------------------------------------------------------------------------------------------------

if ($LoadOnly) { return }

$argProblem = Test-SmokeArgs $Run $Config $Branch
if ($argProblem) { Write-Host "smoke-workers: $argProblem"; exit 2 }
$realConfig = Get-RealConfigPath
if (-not $realConfig -or -not (Test-Path -LiteralPath $realConfig -PathType Leaf)) {
    Write-Host "smoke-workers: no real config found (LANQ_CONFIG or the per-user machines.yml); the identity check needs it."; exit 2
}
$realConfigs = Get-RealConfigPaths
$refusal = Test-NotRealConfig $Config $realConfigs
if ($refusal) { Write-Host "smoke-workers: $refusal"; exit 2 }
$realHashes = @{}
foreach ($r in $realConfigs) { $realHashes[$r] = (Get-Sha256 $r) }
try {
    $smokeMachines = Read-MachineIdentities $Config
    $realMachines = Read-MachineIdentities $realConfig
} catch {
    Write-Host "smoke-workers: refused, a config it cannot read safely: $($_.Exception.Message)"; exit 2
}
Set-HiddenValues @($smokeMachines, $realMachines)
$problems = Compare-MachineIdentities $smokeMachines $realMachines
if ($problems.Count -gt 0) {
    Write-Host 'smoke-workers: refused, the smoke machines must equal the real ones (ssh, work_root, max_jobs):'
    $problems | ForEach-Object { Write-Host "  $_" }
    exit 2
}

$configFull = Resolve-SmokePath $Config
$script:SmokeTemp = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) ('lanq-smoke-' + [guid]::NewGuid().ToString('N'))))
[void][System.IO.Directory]::CreateDirectory($script:SmokeTemp)
$gitNames = 'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_AUTHOR_DATE', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL', 'GIT_COMMITTER_DATE'
$saved = @{}
foreach ($n in @('LANQ_HOME', 'LANQ_CONFIG') + $gitNames) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$env:LANQ_HOME = Join-Path $script:SmokeTemp 'home'
$exitCode = 0
try {
    # The binary: the dry run's native-win-x64 artifact, from a successful release run whose headSha is the branch's head.
    $view = & gh run view $Run --json headSha,headBranch,event,workflowName,conclusion | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $view) { $exitCode = 70; throw 'gh run view failed' }
    $remote = (& git ls-remote origin "refs/heads/$Branch") -split '\s+' | Select-Object -First 1
    $symref = @(& git ls-remote --symref origin HEAD) | Where-Object { $_ -match '^ref: refs/heads/(\S+)\s+HEAD' } | Select-Object -First 1
    $defaultBranch = $(if ($symref -match '^ref: refs/heads/(\S+)') { $Matches[1] } else { $null })
    $runProblem = Test-SmokeRun $view $Branch $remote $defaultBranch
    if ($runProblem) { $exitCode = 2; throw $runProblem }
    $download = Join-Path $script:SmokeTemp 'download'
    & gh run download $Run -n native-win-x64 -D $download
    if ($LASTEXITCODE -ne 0) { $exitCode = 70; throw 'gh run download failed' }
    $zip = @(Get-ChildItem -LiteralPath $download -Recurse -Filter 'lanq-win-x64.zip')[0]
    if (-not $zip) { $exitCode = 70; throw 'the artifact has no lanq-win-x64.zip' }
    $binDir = Expand-SmokeZip $zip.FullName 'bin'
    $lanq = Join-Path $binDir 'lanq.exe'
    $versionResult = Invoke-Smoke $lanq @('--version') $script:SmokeTemp 2
    $versionProblem = Test-SmokeVersion $versionResult $view.headSha
    if ($versionProblem) { $exitCode = 2; throw $versionProblem }
    $version = $versionResult.Stdout.Trim()
    Write-Host "smoke-workers: $version from run $Run ($($view.headSha)), config $configFull, LANQ_HOME $env:LANQ_HOME"

    # The throwaway repository: fixed identity and dates, so its root commit (and repo id) never changes.
    $repo = Join-Path $script:SmokeTemp 'repo'
    $checkSh = @'
#!/bin/sh
# lanq smoke check (scripts/smoke-workers.ps1): run SECONDS CODE|file [LABEL], list-slots, or results 0 junit|trx|none.
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
results)
  mkdir -p TestResults
  case "${3:-}" in
  junit) printf '%s
' '<?xml version="1.0" encoding="UTF-8"?><testsuites><testsuite name="smoke" tests="2"><testcase classname="Smoke" name="Passes" time="0.01"/><testcase classname="Smoke" name="Fails" time="0.02"><failure message="smoke">expected</failure></testcase></testsuite></testsuites>' > TestResults/smoke.xml ;;
  trx) printf '%s
' '<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="00000000-0000-0000-0000-000000000001" testName="Passes" outcome="Passed" duration="00:00:00.0100000"/><UnitTestResult testId="00000000-0000-0000-0000-000000000002" testName="Fails" outcome="Failed" duration="00:00:00.0200000"/></Results><TestDefinitions><UnitTest name="Passes" id="00000000-0000-0000-0000-000000000001"><TestMethod className="Smoke" name="Passes"/></UnitTest><UnitTest name="Fails" id="00000000-0000-0000-0000-000000000002"><TestMethod className="Smoke" name="Fails"/></UnitTest></TestDefinitions></TestRun>' > TestResults/smoke.trx ;;
  none) ;;
  *) echo "usage: check.sh results 0 junit|trx|none" >&2; exit 2 ;;
  esac
  echo "SMOKE_RESULTS ${3:-}" ;;
*) echo "usage: check.sh run SECONDS CODE [LABEL] | list-slots | results 0 junit|trx|none" >&2; exit 2 ;;
esac
'@
    $checkPs = @'
# lanq smoke check (scripts/smoke-workers.ps1): run SECONDS CODE|file [LABEL], list-slots, or results 0 junit|trx|none.
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
    'results' {
        [void](New-Item -ItemType Directory -Force -Path 'TestResults')
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        switch ($Code) {
            'junit' { [IO.File]::WriteAllText((Join-Path (Get-Location).Path 'TestResults/smoke.xml'), '<?xml version="1.0" encoding="UTF-8"?><testsuites><testsuite name="smoke" tests="2"><testcase classname="Smoke" name="Passes" time="0.01"/><testcase classname="Smoke" name="Fails" time="0.02"><failure message="smoke">expected</failure></testcase></testsuite></testsuites>' + "`n", $utf8) }
            'trx' { [IO.File]::WriteAllText((Join-Path (Get-Location).Path 'TestResults/smoke.trx'), '<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="00000000-0000-0000-0000-000000000001" testName="Passes" outcome="Passed" duration="00:00:00.0100000"/><UnitTestResult testId="00000000-0000-0000-0000-000000000002" testName="Fails" outcome="Failed" duration="00:00:00.0200000"/></Results><TestDefinitions><UnitTest name="Passes" id="00000000-0000-0000-0000-000000000001"><TestMethod className="Smoke" name="Passes"/></UnitTest><UnitTest name="Fails" id="00000000-0000-0000-0000-000000000002"><TestMethod className="Smoke" name="Fails"/></UnitTest></TestDefinitions></TestRun>' + "`n", $utf8) }
            'none' { }
            default { 'usage: check.ps1 results 0 junit|trx|none'; exit 2 }
        }
        "SMOKE_RESULTS $Code"
    }
    default { 'usage: check.ps1 run SECONDS CODE [LABEL] | list-slots | results 0 junit|trx|none'; exit 2 }
}
'@
    [void](Write-SmokeFile 'repo/smoke/check.sh' $checkSh)
    [void](Write-SmokeFile 'repo/smoke/check.ps1' $checkPs)
    [void](Write-SmokeFile 'repo/.gitattributes' "*.dat -diff`n")
    [void](Write-SmokeFile 'repo/smoke/exit-code' "0`n")
    $env:GIT_AUTHOR_NAME = 'lanq smoke'; $env:GIT_AUTHOR_EMAIL = 'smoke@example.invalid'; $env:GIT_AUTHOR_DATE = '2026-01-01T00:00:00Z'
    $env:GIT_COMMITTER_NAME = 'lanq smoke'; $env:GIT_COMMITTER_EMAIL = 'smoke@example.invalid'; $env:GIT_COMMITTER_DATE = '2026-01-01T00:00:00Z'
    function Git([string[]]$GitArgs) {
        $r = Invoke-Smoke 'git' (@('-c', 'commit.gpgsign=false', '-c', 'core.autocrlf=false') + $GitArgs) $repo 2
        if ($r.TimedOut -or $r.Code -ne 0) { throw "git $($GitArgs -join ' ') failed: $($r.Stderr)" }
    }
    Git @('init', '-q', '-b', 'main')
    Git @('add', '-A'); Git @('commit', '-q', '-m', 'smoke base'); Git @('tag', 'smoke-pass')
    [void](Write-SmokeFile 'repo/smoke/exit-code' "1`n"); Git @('add', '-A'); Git @('commit', '-q', '-m', 'smoke fails'); Git @('tag', 'smoke-fail')
    [void](Write-SmokeFile 'repo/README.md' "lanq smoke repository`n"); Git @('add', '-A'); Git @('commit', '-q', '-m', 'smoke head')

    function LanqArgs([string[]]$A) { if ($A.Count -gt 1) { return @($A[0], '--config', $configFull) + $A[1..($A.Count - 1)] } return @($A[0], '--config', $configFull) }
    function Lanq([string[]]$A) { return Invoke-Smoke $lanq (LanqArgs $A) $repo }
    $common = @('--json', '--timeout', '5', '--no-wake', '--log-tail', '200')
    function Check([string]$Os) { if ($Os -eq 'windows') { return @('powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'smoke/check.ps1') } return @('sh', 'smoke/check.sh') }
    function OsOf([string]$Name) { return $smokeMachines[$Name].os }

    function Scenario([string]$Name, [scriptblock]$Body) {
        try { $note = & $Body; Add-Result $Name 'PASS' ([string]$note) }
        catch { Add-Result $Name 'FAIL' $_.Exception.Message }
    }

    # Machines. What the smoke config has decides what applies (N/A otherwise); a configured machine that is not ok fails.
    $up = Read-SmokeHealth (Lanq @('health', '--json'))
    foreach ($name in $smokeMachines.Keys) {
        if ($up -notcontains $name) { Add-Result "0 machine $name" 'FAIL' 'configured in the smoke config but not ok in lanq health (asleep, down, held)' }
    }
    $pick = @{}
    foreach ($os in 'linux', 'windows') {
        $configured = @($smokeMachines.Values | Where-Object { $_.os -eq $os })
        $pick[$os] = $(if ($configured.Count -eq 0) { 'N/A' } else { @($configured | Where-Object { $up -contains $_.name } | ForEach-Object { $_.name })[0] })
    }
    $multiConfigured = @($smokeMachines.Values | Where-Object { [int]$_.max_jobs -ge 2 })
    $multi = $(if ($multiConfigured.Count -eq 0) { 'N/A' } else { @($multiConfigured | Where-Object { $up -contains $_.name })[0] })
    $main = $(if ($pick['linux'] -and $pick['linux'] -ne 'N/A') { $pick['linux'] } elseif ($pick['windows'] -and $pick['windows'] -ne 'N/A') { $pick['windows'] } else { $null })

    # Runs $Body with a machine, or records N/A (none configured) or SKIP (configured, none reachable).
    function OnMachine([string]$Name, $Machine, [string]$What, [scriptblock]$Body) {
        if ($Machine -eq 'N/A') { Add-Result $Name 'N/A' "no $What in the smoke config"; return }
        if (-not $Machine) { Add-Result $Name 'SKIP' "no reachable $What"; return }
        Scenario $Name $Body
    }

    # 1. A normal run on each OS.
    foreach ($os in 'linux', 'windows') {
        $m = $pick[$os]
        OnMachine "1 normal run ($os)" $m "$os machine" { [void](Assert-LanqResult (Lanq (@('run') + $common + @('--on', $m, '--') + (Check $os) + @('run', '1', '0'))) 0 'Completed'); 'Completed 0' }
    }

    # 2. Refusals: nothing may run.
    $mainOs = $(if ($main) { OsOf $main } else { $null })
    OnMachine '2a not allowlisted' $main 'machine' { [void](Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--', 'sh', 'smoke/other.sh'))) 2 'Rejected'); 'Rejected 2' }
    OnMachine '2b env name not allowed' $main 'machine' {
        $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--env', 'NOT_LISTED=x', '--') + (Check $mainOs) + @('run', '1', '0'))) 2 'Rejected'
        if (@($j.nextSteps | Where-Object { $_.action -eq 'ask_developer' }).Count -eq 0) { throw 'no ask_developer next step' }
        'Rejected, ask_developer'
    }
    OnMachine '2c secret in --env' $main 'machine' {
        $token = 'ghp' + '_' + (-join ((1..36) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) }))
        $r = Lanq (@('run') + $common + @('--on', $main, '--env', "SMOKE_VALUE=$token", '--') + (Check $mainOs) + @('run', '1', '0'))
        [void](Assert-LanqResult $r 2 'Rejected')
        if (($r.Stdout + $r.Stderr).Contains($token)) { throw 'the token was echoed' }
        'Rejected/Secret, value not echoed'
    }
    OnMachine '2d secret in a -diff file (A1)' $main 'machine' {
        [void](Write-SmokeFile 'repo/secret.dat' ("key = AKIA" + (-join ((1..16) | ForEach-Object { [char](Get-Random -Minimum 65 -Maximum 91) })) + "`n"))
        try {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--') + (Check $mainOs) + @('run', '1', '0'))) 2 'Rejected'
            if (-not $j.message.Contains('uncommitted file secret.dat (binary per git)')) { throw "the refusal does not name the file: $($j.message)" }
        } finally { Remove-SmokeFile 'repo/secret.dat' }
        'Rejected/Secret, file named'
    }

    # 3. env: a value with spaces, $, quotes and a backtick comes back byte-exact.
    $value = 'a b $c ''d'' "e" `f'
    foreach ($os in 'linux', 'windows') {
        $m = $pick[$os]
        OnMachine "3 env byte-exact ($os)" $m "$os machine" {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $m, '--env', "SMOKE_VALUE=$value", '--') + (Check $os) + @('run', '0', '0'))) 0 'Completed'
            $got = Get-TailValue $j 'SMOKE_VALUE=\[(.*)\]'
            if ($got -cne $value) { throw "got [$got]" }
            'byte-exact'
        }
    }

    # 4. exclusive: a plain and an --exclusive job started together on a max_jobs >= 2 machine never overlap.
    $multiName = $(if ($multi -and $multi -ne 'N/A') { $multi.name } else { $multi })
    OnMachine '4 exclusive' $multiName 'machine with max_jobs 2 or more' {
        $base = @('run') + $common + @('--on', $multi.name, '--wait', '300')
        $plain = $null; $excl = $null
        try {
            $plain = Start-Smoke $lanq (LanqArgs ($base + @('--') + (Check $multi.os) + @('run', '20', '0'))) $repo
            Start-Sleep -Seconds 2
            $excl = Start-Smoke $lanq (LanqArgs ($base + @('--exclusive', '--') + (Check $multi.os) + @('run', '20', '0'))) $repo
            $a = Assert-LanqResult (Wait-Smoke $plain) 0 'Completed'
            $b = Assert-LanqResult (Wait-Smoke $excl) 0 'Completed'
        } finally {
            foreach ($s in @($plain, $excl)) { if ($s) { Stop-SmokeProcess $s.Process } }
        }
        $as = [long](Get-TailValue $a 'SMOKE_START (\d+)'); $ae = [long](Get-TailValue $a 'SMOKE_END (\d+)')
        $bs = [long](Get-TailValue $b 'SMOKE_START (\d+)'); $be = [long](Get-TailValue $b 'SMOKE_END (\d+)')
        if (-not ($as -gt 0 -and $ae -gt $as -and $bs -gt 0 -and $be -gt $bs)) { throw "the jobs' start and end times are not all in their logs: [$as,$ae] and [$bs,$be]" }
        if (-not ($ae -le $bs -or $be -le $as)) { throw "intervals overlap: [$as,$ae] and [$bs,$be]" }
        'no overlap'
    }

    # 5. baseline: new (the base passes), then preexisting (the base fails too). HEAD's smoke/exit-code is 1.
    OnMachine '5 baseline new' $main 'machine' {
        $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--baseline-ref', 'smoke-pass', '--') + (Check $mainOs) + @('run', '1', 'file'))) 1 'Failed'
        if ($j.baseline.verdict -ne 'new') { throw "verdict $($j.baseline.verdict)" }
        'new'
    }
    OnMachine '5 baseline preexisting' $main 'machine' {
        $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--baseline-ref', 'smoke-fail', '--') + (Check $mainOs) + @('run', '1', 'file'))) 1 'Failed'
        if ($j.baseline.verdict -ne 'preexisting') { throw "verdict $($j.baseline.verdict)" }
        'preexisting'
    }

    # 6. --shards 2 with {shard}, on the main machine's OS.
    OnMachine '6 shards' $main 'machine' {
        $j = Assert-LanqResult (Lanq (@('run') + $common + @('--needs', $mainOs, '--wait', '300', '--shards', '2', '--') + (Check $mainOs) + @('run', '1', '0', '{shard}'))) 0 'Completed'
        if ($j.shardCount -ne 2) { throw "shardCount $($j.shardCount)" }
        $done = @($j.shards | Where-Object { $_.status -eq 'Completed' } | ForEach-Object { [int]$_.shard } | Sort-Object)
        if (($done -join ',') -ne '1,2') { throw "completed shards: $($done -join ',')" }
        '2 shards'
    }

    # 7. A plan with after: and a sharded job (an untracked lanq.yml, so the root commit stays fixed), run and dry run.
    OnMachine '7 plan' $main 'machine' {
        $cmd = ((Check $mainOs) | ForEach-Object { "`"$_`"" }) -join ', '
        $plan = "version: 1`ndefaults:`n  needs: [$mainOs]`n  timeout: 5`njobs:`n  first:`n    command: [$cmd, run, `"1`", `"0`", first]`n" +
            "  second:`n    command: [$cmd, run, `"1`", `"0`", second]`n    after: [first]`n" +
            "  sharded:`n    command: [$cmd, run, `"1`", `"0`", `"{shard}`"]`n    shards: 2`n    after: [first]`n"
        [void](Write-SmokeFile 'repo/lanq.yml' $plan)
        try {
            $j = Assert-LanqResult (Lanq @('run', '--plan', '--json', '--no-wake', '--wait', '300')) 0 'Completed'
            if (@($j.jobs).Count -ne 3) { throw "$(@($j.jobs).Count) jobs" }
            $names = @($j.jobs | Where-Object { $_.status -eq 'Completed' } | ForEach-Object { $_.name } | Sort-Object)
            if (($names -join ',') -ne 'first,second,sharded') { throw "completed jobs: $($names -join ',')" }
            if (@($j.order)[0] -ne 'first') { throw "first in order: $(@($j.order)[0])" }
            [void](Assert-LanqResult (Lanq @('run', '--plan', '--dry-run', '--json', '--no-wake')) 0 'WouldRun')
        } finally { Remove-SmokeFile 'repo/lanq.yml' }
        'Completed; dry run WouldRun'
    }

    # 8. 0.10.0 items, when this binary has them (probed: a dry-run binary's version is 0.0.0-dryrun.N). Each writes an untracked
    #    lanq.yml (the root commit stays fixed) and removes it again.
    $probe = Lanq @('run', '--plan', '--dry-run', '--json', '--no-wake', '--plan-timeout', '1')
    if ($probe.Stdout -match 'plan-timeout' -and $probe.Code -eq 2) {
        Add-Result '8 plan timeout, --on narrowing' 'N/A' 'this binary has no --plan-timeout (before 0.10.0)'
    } else {
        $cmd8 = $(if ($main) { ((Check $mainOs) | ForEach-Object { "`"$_`"" }) -join ', ' } else { '' })

        # 8a. --plan-timeout 1: the job still running after a minute is stopped and cleaned up; the plan is TimedOut 124.
        OnMachine '8a plan timeout' $main 'machine' {
            [void](Write-SmokeFile 'repo/lanq.yml' ("version: 1`ndefaults:`n  needs: [$mainOs]`n  timeout: 5`njobs:`n  slow:`n    command: [$cmd8, run, `"150`", `"0`", slow]`n"))
            try {
                $j = Assert-LanqResult (Lanq @('run', '--plan', '--plan-timeout', '1', '--on', $main, '--json', '--no-wake', '--wait', '300')) 124 'TimedOut'
                if ($j.planTimedOut -ne $true -or $j.planTimeoutMinutes -ne 1) { throw "planTimedOut $($j.planTimedOut), planTimeoutMinutes $($j.planTimeoutMinutes)" }
                $slow = @($j.jobs)[0]
                if ($slow.status -ne 'TimedOut' -or $slow.exitCode -ne 124) { throw "job slow ended $($slow.status) $($slow.exitCode)" }
                if (-not $slow.message.StartsWith('plan_timeout 1 min reached', [StringComparison]::Ordinal)) { throw "job slow: $($slow.message)" }
            } finally { Remove-SmokeFile 'repo/lanq.yml' }
            'TimedOut 124, the job cleaned up'
        }

        # 8b. --on narrows every job to the machine named, never widening one.
        OnMachine '8b --on narrowing' $main 'machine' {
            [void](Write-SmokeFile 'repo/lanq.yml' ("version: 1`ndefaults:`n  needs: [$mainOs]`n  timeout: 5`njobs:`n  one:`n    command: [$cmd8, run, `"1`", `"0`", one]`n" +
                "  two:`n    command: [$cmd8, run, `"1`", `"0`", two]`n"))
            try {
                $j = Assert-LanqResult (Lanq @('run', '--plan', '--on', $main, '--json', '--no-wake', '--wait', '300')) 0 'Completed'
                $elsewhere = @($j.jobs | Where-Object { $_.machine -ne $main })
                if ($elsewhere.Count -gt 0) { throw "jobs not on the narrowed machine: $(($elsewhere | ForEach-Object { $_.name }) -join ', ')" }
            } finally { Remove-SmokeFile 'repo/lanq.yml' }
            'every job on the named machine'
        }

        # 8c. A narrowing that leaves a job no machine fails closed before the snapshot, and the rerun's --only lists the others.
        OnMachine '8c narrowing fails closed' $main 'machine' {
            [void](Write-SmokeFile 'repo/lanq.yml' ("version: 1`ndefaults:`n  needs: [$mainOs]`n  timeout: 5`njobs:`n  big:`n    command: [$cmd8, run, `"1`", `"0`", big]`n    min_size: heavy`n" +
                "  small:`n    command: [$cmd8, run, `"1`", `"0`", small]`n"))
            try {
                $j = Assert-LanqResult (Lanq @('run', '--plan', '--size', 'light', '--json', '--no-wake')) 75 'NoCapacity'
                if ($j.retryable -ne $false) { throw "retryable $($j.retryable)" }
                if (@($j.jobs | Where-Object { $_.jobId }).Count -gt 0) { throw 'a job ran' }
                $rerun = @($j.nextSteps | Where-Object { $_.action -eq 'fix_command' })[0]
                if (-not $rerun -or (@($rerun.argv) -join ' ') -notmatch '--only small') { throw "no rerun with --only small: $(@($j.nextSteps | ForEach-Object { $_.action }) -join ', ')" }
            } finally { Remove-SmokeFile 'repo/lanq.yml' }
            'NoCapacity 75, rerun --only small'
        }
    }

    # 10. 0.11.0 test results, when this binary has them. Probed without any network: --plan --results is refused by every binary, and
    #     only 0.11.0 and later say "--results comes from lanq.yml"; 0.10 says "Unknown or incomplete option" (N/A). Anything else is a
    #     FAIL. The matches avoid quoted text (the apostrophe of "job's", the quotes around the option), which JSON may escape.
    #     --results junit counts the fixed file (two tests, Smoke.Fails failed) and names the failure; with no file at all the job still
    #     completes 0, and the results say noTests (no_results_files) with an ask_developer step.
    $probe10 = Lanq @('run', '--plan', '--dry-run', '--json', '--no-wake', '--results', 'junit:TestResults/*.xml')
    if ($probe10.Stdout -match 'Unknown or incomplete option') {
        Add-Result '10 test results' 'N/A' 'this binary has no --results (before 0.11.0)'
    } elseif (-not ($probe10.Stdout -match '--results comes from lanq\.yml')) {
        Add-Result '10 test results' 'FAIL' "the --results probe answered neither as 0.10 nor as 0.11 (exit $($probe10.Code))"
    } else {
        OnMachine '10a results counted' $main 'machine' {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--results', 'junit:TestResults/*.xml', '--') + (Check $mainOs) + @('results', '0', 'junit'))) 0 'Completed'
            if (-not $j.results) { throw 'no results object' }
            if ($j.results.total -ne 2 -or $j.results.passed -ne 1 -or $j.results.failed -ne 1 -or $j.results.noTests -ne $false) {
                throw "counts: total $($j.results.total), passed $($j.results.passed), failed $($j.results.failed), noTests $($j.results.noTests)"
            }
            $names = @($j.results.failedTests | ForEach-Object { $_.name })
            if (($names -join ',') -ne 'Smoke.Fails') { throw "failed tests: $($names -join ',')" }
            '2 tests, 1 failed (Smoke.Fails)'
        }
        OnMachine '10b no tests ran' $main 'machine' {
            $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $main, '--results', 'junit:TestResults/*.xml', '--') + (Check $mainOs) + @('results', '0', 'none'))) 0 'Completed'
            if (-not $j.results -or $j.results.noTests -ne $true -or $j.results.noTestsReason -ne 'no_results_files') {
                throw "noTests $($j.results.noTests), noTestsReason $($j.results.noTestsReason)"
            }
            if (@($j.nextSteps | Where-Object { $_.action -eq 'ask_developer' }).Count -eq 0) { throw 'no ask_developer next step' }
            'noTests, ask_developer, still Completed 0'
        }
    }

    # 9. Leftover slots: lanq unlock on each smoke machine (removes only slots past their deadline), then a read-only list.
    $upNow = Read-SmokeHealth (Lanq @('health', '--json'))
    foreach ($name in $smokeMachines.Keys) {
        if ($up -contains $name -and $upNow -notcontains $name) { Add-Result "9 machine $name" 'FAIL' 'was ok at the start, not ok at the end; its slots could not be checked' }
    }
    $checkable = @($smokeMachines.Keys | Where-Object { $upNow -contains $_ })
    if ($checkable.Count -eq 0) { Add-Result '9 no leftover slots' 'SKIP' 'no reachable smoke machine' } else {
        Scenario '9 no leftover slots' {
            $left = New-Object System.Collections.Generic.List[string]
            foreach ($m in $checkable) {
                $u = Lanq @('unlock', $m)
                if ($u.TimedOut -or $u.Code -ne 0) { throw "lanq unlock $m exited $($u.Code)" }
                $j = Assert-LanqResult (Lanq (@('run') + $common + @('--on', $m, '--wait', '300', '--') + (Check (OsOf $m)) + @('list-slots'))) 0 'Completed'
                if (@($j.logTail | Where-Object { $_ -match 'SMOKE_NO_LOCKS' }).Count -gt 0) { throw "$m`: the check found no locks folder" }
                $self = Get-TailValue $j 'SMOKE_SELF (\S+)'
                $slots = @($j.logTail | Where-Object { $_ -match 'SMOKE_SLOT (\S+)' } | ForEach-Object { if ($_ -match 'SMOKE_SLOT (\S+)') { $Matches[1] } })
                if (-not $self -or $slots -notcontains $self) { throw "$m`: the check did not see its own slot ($self), so its listing cannot be trusted" }
                foreach ($id in $slots) { if ($id -ne $self -and $script:JobIds.Contains($id)) { $left.Add("$m`: $id") } }
            }
            if ($left.Count -gt 0) { throw "smoke slots left: $($left -join ', ')" }
            "$($script:JobIds.Count) smoke job id(s), none left"
        }
    }
} catch {
    if ($exitCode -eq 0) { $exitCode = 70 }
    Write-Host "smoke-workers: $(Hide-ConfigValues $_.Exception.Message)"
} finally {
    foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    foreach ($r in $realHashes.Keys) {
        if ((Get-Sha256 $r) -ne $realHashes[$r]) {
            Write-Host "smoke-workers: FAIL: the real config $r changed during the smoke"
            $exitCode = 1
        }
    }
    if ($Keep) { Write-Host "smoke-workers: kept $script:SmokeTemp" } else { Remove-SmokeTemp }
}

if ($exitCode -ne 0) { exit $exitCode }
''
'| Scenario | Result | Note |'
'| --- | --- | --- |'
foreach ($r in $script:Results) { "| $($r.Scenario) | $($r.Result) | $($r.Note) |" }
exit (Get-SmokeExitCode $script:Results ([bool]$AllowSkip))
