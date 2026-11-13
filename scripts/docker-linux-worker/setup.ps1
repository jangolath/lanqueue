<#
.SYNOPSIS
  Sets up a Linux lanq worker in a Docker container on this PC. Windows PowerShell 5.1 and PowerShell 7, Docker Desktop.

.DESCRIPTION
  setup.ps1 [-Name linux-01] [-Port 2222] [-KeyEnv LANQ_KEY_LINUX01] [-Memory 8g] [-Cpus 4] [-CpuSet 4-7]
            [-StateDir <folder>] [-SshDir <folder>] [-NoUserEnv] [-Force]
  setup.ps1 ... -Register [-MachinesFile <file>, ...] [-OwnerApproved]

  Without -Register it never touches your lanq config. It:
  1. Checks that docker answers, and refuses to replace an existing container, ssh config entry or a different
     -KeyEnv value unless -Force. An existing key or host key is reused, never overwritten.
  2. User key: <SshDir>\lanq-<Name>_ed25519 (ed25519, no passphrase, comment lanq-<Name>), made with ssh-keygen -q
     -N "" so it prints nothing, then an ACL that gives only you access. Sets the user environment variable -KeyEnv to
     the key's PATH (not the key), unless -NoUserEnv. The key is never printed, nor its fingerprint.
  3. Host key: <StateDir>\ssh_host_ed25519_key, made here so it is known before the first login (no trust on first
     use) and survives rebuilds. <StateDir>\authorized_keys is a copy of the .pub; the container adds "restrict".
  4. docker build (this folder) and docker run: --restart unless-stopped, --memory/--memory-swap, --cpus with a
     --cpuset-cpus of the same size (so nproc inside matches), --pids-limit, no-new-privileges, published only on
     127.0.0.1:<Port>, volumes lanq-<Name>-work (/srv/lq) and lanq-<Name>-nuget (the NuGet cache).
  5. ssh config: <SshDir>\config.d\lanq-<Name> (HostName 127.0.0.1, Port, User lanq, HostKeyAlias lanq-<Name>,
     IdentityFile, a private UserKnownHostsFile <SshDir>\known_hosts.d\lanq-<Name> holding the host key), both with an
     owner-only ACL, and an Include line at the TOP of <SshDir>\config (after a timestamped backup). Checks the result
     with ssh -G and one key login, printing only ok or a failure.

  -Register (separate, run by the owner): adds the machine to the lanq config. For each machines file (default: your
  real config, LANQ_CONFIG or %APPDATA%\lanq\machines.yml, plus machines.smoke.yml next to it when it exists), it backs
  the file up, refuses a file it cannot edit safely (flow style, machines: not the last top-level key, a tab, the name
  or ssh target already there), appends the entry below, runs lanq doctor --config <file> --remote --json, and restores
  the backup when the config does not load or this machine has a failing check. It prints only "added" or "refused"
  per file, never the file's content. AI agents must not edit machines.yml (AGENTS.md, Hard rules): when it finds an
  agent's environment, -Register refuses unless -OwnerApproved, which an agent may pass only when the owner has said so
  in that session.

    - name: <Name>
      os: linux
      ssh: lanq@lanq-<Name>
      key_env: <KeyEnv>
      work_root: /srv/lq
      capabilities: [dotnet, linux]
      max_jobs: 1
      transfer: git
      size: medium
      env:
        DOTNET_CLI_TELEMETRY_OPTOUT: "1"
        DOTNET_NOLOGO: "1"

  Settings for jobs go under the machine's env:, never docker -e or ENV: sshd does not pass a container's
  environment on to a login.

  Exit codes: 0 done, 1 a check failed (rolled back where it applies), 2 refused (arguments or existing state).
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,30}$')]
    [string]$Name = 'linux-01',
    [ValidateRange(1024, 65535)]
    [int]$Port = 2222,
    [ValidatePattern('^[A-Z][A-Z0-9_]{0,63}$')]
    [string]$KeyEnv = 'LANQ_KEY_LINUX01',
    [ValidatePattern('^[1-9][0-9]{0,5}[mg]$')]
    [string]$Memory = '8g',
    [ValidateRange(1, 256)]
    [int]$Cpus = 4,
    [ValidatePattern('^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$')]
    [string]$CpuSet,
    [string]$StateDir,
    [string]$SshDir,
    [switch]$NoUserEnv,
    [switch]$Force,
    [switch]$Register,
    [string[]]$MachinesFile,
    [switch]$OwnerApproved
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$alias = "lanq-$Name"
$container = "lanq-$Name"
$image = 'lanq-docker-worker:local'
$customSshDir = [bool]$SshDir
if (-not $SshDir) { $SshDir = Join-Path $HOME '.ssh' }
if (-not $StateDir) { $StateDir = Join-Path $env:LOCALAPPDATA "lanq-docker-worker\$Name" }
$SshDir = [IO.Path]::GetFullPath($SshDir)
$StateDir = [IO.Path]::GetFullPath($StateDir)
$keyPath = Join-Path $SshDir "$alias`_ed25519"
$hostKeyPath = Join-Path $StateDir 'ssh_host_ed25519_key'
$authKeysPath = Join-Path $StateDir 'authorized_keys'
$configDir = Join-Path $SshDir 'config.d'
$entryPath = Join-Path $configDir $alias
$knownHostsDir = Join-Path $SshDir 'known_hosts.d'
$knownHostsPath = Join-Path $knownHostsDir $alias
$sshConfig = Join-Path $SshDir 'config'

function Stop-Refused([string]$why) {
    Write-Host "refused: $why"
    exit 2
}

function Stop-Failed([string]$why) {
    Write-Host "failed: $why"
    exit 1
}

# One native call with an explicit argument string, so an empty argument (-N "") reaches the program the same way in
# Windows PowerShell 5.1 and PowerShell 7. Output is captured and returned, never echoed.
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $quoted = foreach ($a in $arguments) {
        if ($a -eq '') { '""' }
        elseif ($a -match '[\s"]') { '"' + ($a -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
        else { $a }
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command $exe -CommandType Application | Select-Object -First 1).Source
    $psi.Arguments = $quoted -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Out = $outTask.Result; Err = $errTask.Result }
}

# Only the current user (by SID) gets access; inheritance off. Windows OpenSSH checks this for keys and included files.
function Set-OwnerOnlyAcl([string]$path) {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $r = Invoke-Native 'icacls' @($path, '/inheritance:r', '/grant:r', "*$($sid):F")
    if ($r.ExitCode -ne 0) { Stop-Failed "could not restrict access to $(Split-Path -Leaf $path)" }
}

function Write-TextFile([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Test-AgentEnvironment {
    foreach ($n in 'CLAUDECODE', 'CLAUDE_CODE_ENTRYPOINT', 'CODEX_SANDBOX', 'CODEX_SANDBOX_NETWORK_DISABLED', 'GEMINI_CLI', 'CURSOR_AGENT', 'AIDER_CHAT') {
        if ([Environment]::GetEnvironmentVariable($n)) { return $true }
    }
    return $false
}

function Get-SshBaseArgs {
    $a = @()
    if ($customSshDir) { $a += @('-F', $sshConfig) }
    $a
}

# ---- -Register: add the machine to the lanq config files ------------------------------------------------------------
function Get-RegisterEntry([string]$indent, [string]$nl) {
    $i = $indent
    $lines = @(
        "$i- name: $Name",
        "$i  os: linux",
        "$i  ssh: lanq@$alias",
        "$i  key_env: $KeyEnv",
        "$i  work_root: /srv/lq",
        "$i  capabilities: [dotnet, linux]",
        "$i  max_jobs: 1",
        "$i  transfer: git",
        "$i  size: medium               # declared: a container shares this PC, so --min-size heavy runs stay off it",
        "$i  env:                       # job settings go here; sshd does not pass docker -e or ENV on to a login",
        "$i    DOTNET_CLI_TELEMETRY_OPTOUT: `"1`"",
        "$i    DOTNET_NOLOGO: `"1`""
    )
    ($lines -join $nl) + $nl
}

# Returns $null when the file can be edited safely, otherwise why not. Sets $script:entryIndent.
function Test-MachinesFile([string[]]$lines) {
    $script:entryIndent = '  '
    $machinesAt = -1
    for ($n = 0; $n -lt $lines.Count; $n++) {
        $l = $lines[$n]
        if ($l -match "`t") { return 'it contains a tab' }
        if ($l -match '^machines:') {
            if ($l -notmatch '^machines:\s*(#.*)?$') { return 'machines: is in flow style' }
            $machinesAt = $n
            continue
        }
        if ($machinesAt -ge 0 -and $l -match '^[^\s#-]') { return 'machines: is not the last top-level key' }
    }
    if ($machinesAt -lt 0) { return 'no machines: list' }
    $first = $true
    for ($n = $machinesAt + 1; $n -lt $lines.Count; $n++) {
        $l = $lines[$n]
        if ($l -match '^(\s*)-\s+name:\s*["'']?([^"''#\s]+)') {
            if ($first) { $script:entryIndent = $Matches[1]; $first = $false }
            if ($Matches[2] -eq $Name) { return "a machine named $Name is already there" }
        }
        if ($l -match '^\s*-?\s*ssh:\s*["'']?([^"''#\s]+)' -and $Matches[1] -eq "lanq@$alias") { return "ssh: lanq@$alias is already there" }
        if ($l -match '^\s*\{' -or $l -match '^\s*-\s*\{') { return 'a machine is in flow style' }
    }
    return $null
}

function Invoke-Register {
    if ((Test-AgentEnvironment) -and -not $OwnerApproved) {
        Stop-Refused '-Register edits your lanq config, which an AI agent must not do (AGENTS.md, Hard rules). The owner runs it, or OKs it in this session and the agent passes -OwnerApproved.'
    }
    if (-not (Get-Command lanq -ErrorAction SilentlyContinue)) { Stop-Refused 'lanq is not on PATH' }
    $files = @($MachinesFile | Where-Object { $_ })
    if ($files.Count -eq 0) {
        $real = if ($env:LANQ_CONFIG) { $env:LANQ_CONFIG } else { Join-Path $env:APPDATA 'lanq\machines.yml' }
        $files = @($real)
        $smoke = Join-Path (Split-Path -Parent $real) 'machines.smoke.yml'
        if (Test-Path -LiteralPath $smoke) { $files += $smoke }
    }
    $failed = $false
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($f in $files) {
        $leaf = Split-Path -Leaf $f
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Write-Host "$($leaf): refused (not found)"; $failed = $true; continue }
        $bytes = [IO.File]::ReadAllBytes($f)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = (New-Object System.Text.UTF8Encoding($false)).GetString($bytes)
        if ($bom) { $text = $text.Substring(1) }
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $why = Test-MachinesFile ($text -split '\r?\n')
        if ($why) { Write-Host "$($leaf): refused ($why)"; $failed = $true; continue }
        $backup = "$f.bak-$stamp"
        Copy-Item -LiteralPath $f -Destination $backup
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { $text += $nl }
        $text += Get-RegisterEntry $script:entryIndent $nl
        [IO.File]::WriteAllText($f, $text, (New-Object System.Text.UTF8Encoding($bom)))
        $ok = $false
        try {
            $r = Invoke-Native 'lanq' @('doctor', '--config', $f, '--remote', '--json')
            $doc = $r.Out | ConvertFrom-Json
            $bad = @($doc.checks | Where-Object { $_.level -eq 'fail' -and ($_.machine -eq $Name -or ($_.scope -eq 'local' -and $_.name -eq 'config')) })
            $ours = @($doc.checks | Where-Object { $_.machine -eq $Name })
            $ok = $bad.Count -eq 0 -and $ours.Count -gt 0
        }
        catch { $ok = $false }
        if ($ok) {
            Write-Host "$($leaf): added (backup $(Split-Path -Leaf $backup))"
        }
        else {
            Copy-Item -LiteralPath $backup -Destination $f -Force
            Write-Host "$($leaf): refused (lanq doctor did not pass for $Name; the file was restored; run lanq doctor --config <file> --remote to see why)"
            $failed = $true
        }
    }
    if ($failed) { exit 1 }
    Write-Host "next: lanq makeready $Name ; lanq health"
    exit 0
}

if ($Register) { Invoke-Register }

# ---- 1. Checks ------------------------------------------------------------------------------------------------------
foreach ($tool in 'docker', 'ssh', 'ssh-keygen', 'icacls') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Refused "$tool is not on PATH" }
}
$info = Invoke-Native 'docker' @('info', '--format', '{{.NCPU}} {{.OSType}}')
if ($info.ExitCode -ne 0) { Stop-Refused 'docker does not answer (is Docker Desktop running?)' }
$parts = $info.Out.Trim() -split ' '
if ($parts[1] -ne 'linux') { Stop-Refused 'docker is not in Linux containers mode' }
$vmCpus = [int]$parts[0]
if ($Cpus -gt $vmCpus) { Stop-Refused "-Cpus $Cpus is more than the $vmCpus CPUs Docker has" }
if (-not $CpuSet) { $CpuSet = if ($Cpus -eq $vmCpus) { "0-$($vmCpus - 1)" } else { "$($vmCpus - $Cpus)-$($vmCpus - 1)" } }
$setSize = 0
foreach ($range in $CpuSet -split ',') {
    $ends = @($range -split '-' | ForEach-Object { [int]$_ })
    $hi = $ends[$ends.Count - 1]
    if ($hi -lt $ends[0] -or $hi -ge $vmCpus) { Stop-Refused "-CpuSet $CpuSet does not fit the $vmCpus CPUs Docker has" }
    $setSize += $hi - $ends[0] + 1
}
if ($setSize -ne $Cpus) { Stop-Refused "-CpuSet $CpuSet has $setSize CPUs, -Cpus is $Cpus (they must match so nproc does)" }

$existing = Invoke-Native 'docker' @('container', 'inspect', '--format', '{{.Name}}', $container)
if ($existing.ExitCode -eq 0 -and -not $Force) { Stop-Refused "container $container exists (-Force replaces it; its volumes are kept)" }
if ((Test-Path -LiteralPath $entryPath) -and -not $Force) { Stop-Refused "ssh config entry config.d\$alias exists (-Force rewrites it)" }
if (-not $NoUserEnv) {
    $current = [Environment]::GetEnvironmentVariable($KeyEnv, 'User')
    if ($current -and $current -ne $keyPath -and -not $Force) { Stop-Refused "user variable $KeyEnv already points at another file (-Force changes it)" }
}
if ((Test-Path -LiteralPath $keyPath) -ne (Test-Path -LiteralPath "$keyPath.pub")) { Stop-Refused "only one of $alias`_ed25519 and its .pub exists; fix that by hand" }

# ---- 2. User key ----------------------------------------------------------------------------------------------------
foreach ($d in $SshDir, $configDir, $knownHostsDir, $StateDir) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}
Set-OwnerOnlyAcl $StateDir
Set-OwnerOnlyAcl $knownHostsDir
if (Test-Path -LiteralPath $keyPath) {
    Write-Host "key: reused $alias`_ed25519"
}
else {
    $r = Invoke-Native 'ssh-keygen' @('-q', '-t', 'ed25519', '-N', '', '-C', $alias, '-f', $keyPath)
    if ($r.ExitCode -ne 0) { Stop-Failed 'ssh-keygen could not make the key' }
    Write-Host "key: made $alias`_ed25519 (no passphrase; reachable only from this PC)"
}
Set-OwnerOnlyAcl $keyPath
if (-not $NoUserEnv) {
    [Environment]::SetEnvironmentVariable($KeyEnv, $keyPath, 'User')
    Set-Item -Path "env:$KeyEnv" -Value $keyPath
    Write-Host "env: $KeyEnv set (user) to the key's path; open a new terminal for other programs to see it"
}

# ---- 3. Host key ----------------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $hostKeyPath)) {
    $r = Invoke-Native 'ssh-keygen' @('-q', '-t', 'ed25519', '-N', '', '-C', $alias, '-f', $hostKeyPath)
    if ($r.ExitCode -ne 0) { Stop-Failed 'ssh-keygen could not make the host key' }
    Write-Host 'host key: made'
}
else { Write-Host 'host key: reused' }
Set-OwnerOnlyAcl $hostKeyPath
$userPub = ([IO.File]::ReadAllText("$keyPath.pub")).Trim()
$hostPub = ([IO.File]::ReadAllText("$hostKeyPath.pub")).Trim() -split '\s+'
Write-TextFile $authKeysPath ($userPub + "`n")
Write-TextFile $knownHostsPath ("$alias $($hostPub[0]) $($hostPub[1])`n")
Set-OwnerOnlyAcl $knownHostsPath

# ---- 4. Build and run -----------------------------------------------------------------------------------------------
$r = Invoke-Native 'docker' @('build', '--quiet', '--tag', $image, $PSScriptRoot)
if ($r.ExitCode -ne 0) { Stop-Failed "docker build failed: $($r.Err.Trim())" }
Write-Host "image: $image built"
if ($existing.ExitCode -eq 0) {
    $r = Invoke-Native 'docker' @('rm', '--force', $container)
    if ($r.ExitCode -ne 0) { Stop-Failed "could not remove the old container $container" }
}
$run = @(
    'run', '--detach', '--name', $container, '--hostname', $Name,
    '--restart', 'unless-stopped',
    '--memory', $Memory, '--memory-swap', $Memory,
    '--cpus', "$Cpus", '--cpuset-cpus', $CpuSet,
    '--pids-limit', '4096',
    '--security-opt', 'no-new-privileges:true',
    '--publish', "127.0.0.1:$($Port):22",
    '--mount', "type=volume,source=lanq-$Name-work,target=/srv/lq",
    '--mount', "type=volume,source=lanq-$Name-nuget,target=/home/lanq/.nuget",
    '--mount', "type=bind,source=$hostKeyPath,target=/run/lanq/hostkey,readonly",
    '--mount', "type=bind,source=$authKeysPath,target=/run/lanq/authorized_keys,readonly",
    $image
)
$r = Invoke-Native 'docker' $run
if ($r.ExitCode -ne 0) { Stop-Failed "docker run failed: $($r.Err.Trim())" }
Write-Host "container: $container on 127.0.0.1:$Port ($Memory, $Cpus CPUs on $CpuSet)"

# ---- 5. ssh config --------------------------------------------------------------------------------------------------
$fwd = { param($p) $p -replace '\\', '/' }
$entry = @(
    "# lanq Docker worker $Name (written by scripts/docker-linux-worker/setup.ps1)",
    "Host $alias",
    '    HostName 127.0.0.1',
    "    Port $Port",
    '    User lanq',
    "    HostKeyAlias $alias",
    "    UserKnownHostsFile `"$(& $fwd $knownHostsPath)`"",
    '    StrictHostKeyChecking yes',
    "    IdentityFile `"$(& $fwd $keyPath)`"",
    '    IdentitiesOnly yes',
    ''
) -join "`n"
Write-TextFile $entryPath $entry
Set-OwnerOnlyAcl $entryPath

$include = "Include `"$(& $fwd $entryPath)`""
$old = if (Test-Path -LiteralPath $sshConfig) { [IO.File]::ReadAllText($sshConfig) } else { '' }
$hasInclude = @($old -split '\r?\n' | Where-Object { $_.Trim() -eq $include }).Count -gt 0
if (-not $hasInclude) {
    $note = 'a new file'
    if (Test-Path -LiteralPath $sshConfig) {
        Copy-Item -LiteralPath $sshConfig -Destination "$sshConfig.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        $note = 'the old file was backed up'
    }
    # At the top: an Include after a Host or Match line would apply only inside that block.
    Write-TextFile $sshConfig ($include + "`n" + $old)
    Set-OwnerOnlyAcl $sshConfig
    Write-Host "ssh config: Include added at the top ($note)"
}

$g = Invoke-Native 'ssh' ((Get-SshBaseArgs) + @('-G', $alias))
$want = @("hostname 127.0.0.1", "port $Port", "user lanq", "hostkeyalias $alias")
$have = @($g.Out -split '\r?\n' | ForEach-Object { $_.Trim().ToLowerInvariant() })
$missing = @($want | Where-Object { $have -notcontains $_.ToLowerInvariant() })
if ($g.ExitCode -ne 0 -or $missing.Count -gt 0) { Stop-Failed "ssh -G $alias does not resolve to this container (check config.d\$alias and the Include)" }
Write-Host 'ssh config: ok'

$login = $null
for ($try = 0; $try -lt 15; $try++) {
    $login = Invoke-Native 'ssh' ((Get-SshBaseArgs) + @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=5', '-i', $keyPath, $alias, 'true'))
    if ($login.ExitCode -eq 0) { break }
    Start-Sleep -Seconds 2
}
if ($login.ExitCode -ne 0) { Stop-Failed "key login to $alias did not work (docker logs $container shows sshd's side)" }
Write-Host "login: ok. Next (the owner): setup.ps1 -Register, then lanq makeready $Name"
exit 0
