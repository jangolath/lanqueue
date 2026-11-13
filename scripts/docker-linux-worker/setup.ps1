<#
.SYNOPSIS
  Sets up a Linux lanq worker in a Docker container on this PC. Windows PowerShell 5.1 and PowerShell 7, Docker Desktop.

.DESCRIPTION
  setup.ps1 [-Name linux-01] [-Port 2222] [-KeyEnv LANQ_KEY_LINUX01] [-Memory 8g] [-Cpus 4] [-CpuSet 4-7]
            [-StateDir <folder>] [-SshDir <folder>] [-NoUserEnv] [-Force] [-ReplaceKeyEnv] [-OwnerApproved]
  setup.ps1 ... -Register [-MachinesFile <file>, ...] [-OwnerApproved]

  Without -Register it never touches your lanq config. It:
  1. Checks that docker answers, and refuses to replace an existing container or ssh config entry unless -Force. It
     never repoints a user variable -KeyEnv that already holds another path, unless -ReplaceKeyEnv. An existing key
     or host key is reused, never overwritten. A UTF-16 ~/.ssh/config is refused (OpenSSH cannot read it either).
  2. User key: <SshDir>\lanq-<Name>_ed25519 (ed25519, no passphrase, comment lanq-<Name>), made with ssh-keygen -q
     -N "" so it prints nothing, then an ACL that gives only you access. Sets the user environment variable -KeyEnv to
     the key's PATH (not the key), unless -NoUserEnv. The key is never printed, nor its fingerprint.
  3. Host key: <StateDir>\ssh_host_ed25519_key, made here so it is known before the first login (no trust on first
     use) and survives rebuilds. <StateDir>\authorized_keys is a copy of the .pub; the container adds "restrict". The
     host key is mounted under /root (0700) in the container, so job code (user lanq) cannot read it.
  4. docker build (this folder) and docker run: --init, --restart unless-stopped, --memory/--memory-swap, --cpus with
     a --cpuset-cpus of the same size (so nproc inside matches), --pids-limit, no-new-privileges, --cap-drop ALL with
     only the capabilities sshd needs, published only on 127.0.0.1:<Port>, volumes lanq-<Name>-work (/srv/lq) and
     lanq-<Name>-nuget (the NuGet cache).
  5. ssh config: <SshDir>\config.d\lanq-<Name> (HostName 127.0.0.1, Port, User lanq, HostKeyAlias lanq-<Name>,
     IdentityFile, a private UserKnownHostsFile <SshDir>\known_hosts.d\lanq-<Name> holding the host key), both with an
     owner-only ACL, and an Include line at the TOP of <SshDir>\config (after a timestamped backup; the existing bytes
     are kept as they are, and so is the file's ACL). Checks the result with ssh -G and one key login, printing only ok
     or a failure with the steps to undo it.

  -Register (separate, run by the owner): adds the machine to the lanq config. The files (default: your real config,
  LANQ_CONFIG or %APPDATA%\lanq\machines.yml, plus machines.smoke.yml next to it when it exists) are all checked
  first; one it cannot edit safely (flow style, machines: not the last top-level key, a tab, the name or ssh target
  already there) refuses them all. Then each is backed up, gets the entry below appended and must pass
  lanq doctor --config <file> --remote --json (5 minutes at most): the config loads and this machine has no failing
  check. When any file fails, every file is restored from its backup. It prints only "added" or "refused" per file,
  never the file's content.

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

  AI agents must not edit machines.yml, ~/.ssh or key settings (AGENTS.md, Hard rules). When it finds an agent's
  environment, -Register, and a setup that would change the real ~/.ssh or a user variable, refuse unless
  -OwnerApproved, which an agent may pass only when the owner has said so in that session. This is a soft gate (it
  looks at environment variables), not a security boundary.

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
    [switch]$ReplaceKeyEnv,
    [switch]$Register,
    [string[]]$MachinesFile,
    [switch]$OwnerApproved
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# Relative paths are resolved against PowerShell's current location once, here: .NET calls would use the process's
# working directory, which Windows PowerShell 5.1 does not keep in step with Set-Location.
function Resolve-FullPath([string]$path) { $PSCmdlet.GetUnresolvedProviderPathFromPSPath($path) }

$alias = "lanq-$Name"
$container = "lanq-$Name"
$image = 'lanq-docker-worker:local'
$realSshDir = Resolve-FullPath (Join-Path $HOME '.ssh')
$SshDir = if ($SshDir) { Resolve-FullPath $SshDir } else { $realSshDir }
$StateDir = if ($StateDir) { Resolve-FullPath $StateDir } else { Resolve-FullPath (Join-Path $env:LOCALAPPDATA "lanq-docker-worker\$Name") }
$MachinesFile = @($MachinesFile | Where-Object { $_ } | ForEach-Object { Resolve-FullPath $_ })
$customSshDir = -not $SshDir.TrimEnd('\').Equals($realSshDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
$keyPath = Join-Path $SshDir "$alias`_ed25519"
$hostKeyPath = Join-Path $StateDir 'ssh_host_ed25519_key'
$authKeysPath = Join-Path $StateDir 'authorized_keys'
$configDir = Join-Path $SshDir 'config.d'
$entryPath = Join-Path $configDir $alias
$knownHostsDir = Join-Path $SshDir 'known_hosts.d'
$knownHostsPath = Join-Path $knownHostsDir $alias
$sshConfig = Join-Path $SshDir 'config'
$script:configBackup = $null

# The only capabilities kept (each tested by dropping it): CHOWN, FOWNER and DAC_OVERRIDE for the entrypoint's
# install/chown into lanq's folders; SETUID and SETGID for sshd to become lanq; SYS_CHROOT for its privilege
# separation. Without any of them the container stops or no login works. AUDIT_WRITE and KILL are not needed: a job
# and lanq's clean-up run as lanq and signal only lanq's own processes.
$caps = @('CHOWN', 'DAC_OVERRIDE', 'FOWNER', 'SETUID', 'SETGID', 'SYS_CHROOT')

function Stop-Refused([string]$why) {
    Write-Host "refused: $why"
    exit 2
}

function Stop-Failed([string]$why) {
    Write-Host "failed: $why"
    exit 1
}

# One native call with an explicit argument string, so an empty argument (-N "") reaches the program the same way in
# Windows PowerShell 5.1 and PowerShell 7. Output is captured and returned, never echoed. A call that runs longer
# than $timeoutMs is killed and returns exit code -1.
function Invoke-Native([string]$exe, [string[]]$arguments, [int]$timeoutMs = 120000) {
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
    if (-not $p.WaitForExit($timeoutMs)) {
        try { $p.Kill() } catch { Write-Verbose 'the process ended while being stopped' }
        $p.WaitForExit()
        return [pscustomobject]@{ ExitCode = -1; Out = ''; Err = 'timed out' }
    }
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Out = $outTask.Result; Err = $errTask.Result }
}

# Only the current user (by SID) gets access; inheritance off. Windows OpenSSH checks this for keys and included files.
function Set-OwnerOnlyAcl([string]$path, [switch]$Directory) {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $grant = if ($Directory) { "*$($sid):(OI)(CI)F" } else { "*$($sid):F" }
    $r = Invoke-Native 'icacls' @($path, '/inheritance:r', '/grant:r', $grant)
    if ($r.ExitCode -ne 0) { Stop-Failed "could not restrict access to $(Split-Path -Leaf $path)" }
}

function New-OwnerOnlyDirectory([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
        Set-OwnerOnlyAcl $path -Directory
    }
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

function Write-Cleanup {
    Write-Host 'to undo this setup:'
    Write-Host "  docker rm -f $container"
    Write-Host "  docker volume rm lanq-$Name-work lanq-$Name-nuget"
    if ($script:configBackup) { Write-Host "  restore $sshConfig from $(Split-Path -Leaf $script:configBackup) (or delete its Include line for config.d/$alias)" }
    else { Write-Host "  delete the Include line for config.d/$alias at the top of $sshConfig" }
    Write-Host "  delete $entryPath, $knownHostsPath and $StateDir"
    Write-Host "  delete $keyPath and its .pub only if nothing else uses them"
    if (-not $NoUserEnv) { Write-Host "  remove the user variable $KeyEnv if this script set it" }
}

function Stop-FailedWithCleanup([string]$why) {
    Write-Host "failed: $why"
    Write-Cleanup
    exit 1
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

# A shell started before setup.ps1 set the user variable -KeyEnv does not have it yet, and lanq doctor would then fail
# its key_env check: take it from the User scope into this process (only the path; the key is never read).
function Import-KeyEnv {
    if (-not [Environment]::GetEnvironmentVariable($KeyEnv, 'Process')) {
        $user = [Environment]::GetEnvironmentVariable($KeyEnv, 'User')
        if ($user) {
            Set-Item -Path "env:$KeyEnv" -Value $user
            Write-Host "env: $KeyEnv loaded from your user variables"
        }
    }
}

# True when lanq doctor says the config loads and this machine has checks and none of them fails. On a refusal it
# prints each failing check's name and level only: its detail can hold paths and fingerprints.
function Test-DoctorPasses([string]$file) {
    Import-KeyEnv
    try {
        $r = Invoke-Native 'lanq' @('doctor', '--config', $file, '--remote', '--json') 300000
        if ($r.ExitCode -eq -1) { Write-Host '  doctor: timed out after 5 minutes'; return $false }
        $doc = $r.Out | ConvertFrom-Json
        $bad = @($doc.checks | Where-Object { $_.level -eq 'fail' -and ($_.machine -eq $Name -or ($_.scope -eq 'local' -and $_.name -eq 'config')) })
        $ours = @($doc.checks | Where-Object { $_.machine -eq $Name })
        foreach ($c in $bad) {
            $checkName = if ("$($c.name)" -match '^[a-z0-9_]{1,64}$') { $c.name } else { '(unnamed)' }
            Write-Host "  doctor: $($c.scope) check $checkName is $($c.level)"
        }
        if ($ours.Count -eq 0) { Write-Host "  doctor: no check for $Name (the config did not load it)" }
        return ($bad.Count -eq 0 -and $ours.Count -gt 0)
    }
    catch {
        Write-Host '  doctor: its output was not the expected JSON'
        return $false
    }
}

function Invoke-Register {
    if ((Test-AgentEnvironment) -and -not $OwnerApproved) {
        Stop-Refused '-Register edits your lanq config, which an AI agent must not do (AGENTS.md, Hard rules). The owner runs it, or OKs it in this session and the agent passes -OwnerApproved.'
    }
    if (-not (Get-Command lanq -ErrorAction SilentlyContinue)) { Stop-Refused 'lanq is not on PATH' }
    Import-KeyEnv
    $files = $MachinesFile
    if ($files.Count -eq 0) {
        $real = if ($env:LANQ_CONFIG) { Resolve-FullPath $env:LANQ_CONFIG } else { Join-Path $env:APPDATA 'lanq\machines.yml' }
        $files = @($real)
        $smoke = Join-Path (Split-Path -Parent $real) 'machines.smoke.yml'
        if (Test-Path -LiteralPath $smoke) { $files += $smoke }
    }

    # Check every file before changing any.
    $plans = @()
    $refused = $false
    foreach ($f in $files) {
        $leaf = Split-Path -Leaf $f
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Write-Host "$($leaf): refused (not found)"; $refused = $true; continue }
        $bytes = [IO.File]::ReadAllBytes($f)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        if ($bytes -contains 0) { Write-Host "$($leaf): refused (not UTF-8)"; $refused = $true; continue }
        $text = (New-Object System.Text.UTF8Encoding($false)).GetString($bytes)
        if ($bom) { $text = $text.Substring(1) }
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $why = Test-MachinesFile ($text -split '\r?\n')
        if ($why) { Write-Host "$($leaf): refused ($why)"; $refused = $true; continue }
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { $text += $nl }
        $plans += [pscustomobject]@{ File = $f; Leaf = $leaf; Bom = $bom; Text = $text + (Get-RegisterEntry $script:entryIndent $nl); Backup = $null }
    }
    if ($refused) { Write-Host 'nothing was changed'; exit 2 }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $allOk = $false
    try {
        foreach ($p in $plans) {
            $p.Backup = "$($p.File).bak-$stamp"
            Copy-Item -LiteralPath $p.File -Destination $p.Backup
            [IO.File]::WriteAllText($p.File, $p.Text, (New-Object System.Text.UTF8Encoding($p.Bom)))
            if (-not (Test-DoctorPasses $p.File)) {
                Write-Host "$($p.Leaf): refused (lanq doctor --config <file> --remote did not pass for $Name; run it to see why)"
                return
            }
        }
        $allOk = $true
    }
    finally {
        if (-not $allOk) {
            foreach ($p in $plans) {
                if ($p.Backup -and (Test-Path -LiteralPath $p.Backup)) { Copy-Item -LiteralPath $p.Backup -Destination $p.File -Force }
            }
            Write-Host 'every file was restored from its backup'
        }
    }
    if (-not $allOk) { exit 1 }
    foreach ($p in $plans) { Write-Host "$($p.Leaf): added (backup $(Split-Path -Leaf $p.Backup))" }
    Write-Host "next: lanq makeready $Name ; lanq health"
    exit 0
}

if ($Register) {
    Invoke-Register
    exit 1
}

# ---- 1. Checks ------------------------------------------------------------------------------------------------------
if ((Test-AgentEnvironment) -and -not $OwnerApproved -and (-not $customSshDir -or -not $NoUserEnv)) {
    Stop-Refused 'this setup changes your ~/.ssh and a user variable, which an AI agent must not do (AGENTS.md, Hard rules). The owner runs it, or OKs it in this session and the agent passes -OwnerApproved.'
}
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
    if ($current -and $current -ne $keyPath -and -not $ReplaceKeyEnv) { Stop-Refused "user variable $KeyEnv already points at another file (-ReplaceKeyEnv changes it; -Force does not)" }
}
if ((Test-Path -LiteralPath $keyPath) -ne (Test-Path -LiteralPath "$keyPath.pub")) { Stop-Refused "only one of $alias`_ed25519 and its .pub exists; fix that by hand" }
$configBytes = if (Test-Path -LiteralPath $sshConfig) { [IO.File]::ReadAllBytes($sshConfig) } else { $null }
if ($configBytes -and (($configBytes.Length -ge 2 -and (($configBytes[0] -eq 0xFF -and $configBytes[1] -eq 0xFE) -or ($configBytes[0] -eq 0xFE -and $configBytes[1] -eq 0xFF))) -or $configBytes -contains 0)) {
    Stop-Refused "$sshConfig looks like UTF-16; OpenSSH reads only UTF-8/ASCII. Save it as UTF-8 first"
}

# ---- 2. User key ----------------------------------------------------------------------------------------------------
New-OwnerOnlyDirectory $SshDir
New-OwnerOnlyDirectory $configDir
New-OwnerOnlyDirectory $knownHostsDir
New-OwnerOnlyDirectory $StateDir
Set-OwnerOnlyAcl $StateDir -Directory
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
$r = Invoke-Native 'docker' @('build', '--quiet', '--tag', $image, $PSScriptRoot) 1800000
if ($r.ExitCode -ne 0) { Stop-Failed "docker build failed: $($r.Err.Trim())" }
Write-Host "image: $image built"
if ($existing.ExitCode -eq 0) {
    $r = Invoke-Native 'docker' @('rm', '--force', $container)
    if ($r.ExitCode -ne 0) { Stop-Failed "could not remove the old container $container" }
}
$run = @(
    'run', '--detach', '--name', $container, '--hostname', $Name, '--init',
    '--restart', 'unless-stopped',
    '--memory', $Memory, '--memory-swap', $Memory,
    '--cpus', "$Cpus", '--cpuset-cpus', $CpuSet,
    '--pids-limit', '4096',
    '--security-opt', 'no-new-privileges:true',
    '--cap-drop', 'ALL'
)
foreach ($c in $caps) { $run += @('--cap-add', $c) }
$run += @(
    '--publish', "127.0.0.1:$($Port):22",
    '--mount', "type=volume,source=lanq-$Name-work,target=/srv/lq",
    '--mount', "type=volume,source=lanq-$Name-nuget,target=/home/lanq/.nuget",
    '--mount', "type=bind,source=$hostKeyPath,target=/root/lanq/hostkey,readonly",
    '--mount', "type=bind,source=$authKeysPath,target=/run/lanq/authorized_keys,readonly",
    $image
)
$r = Invoke-Native 'docker' $run
if ($r.ExitCode -ne 0) { Stop-FailedWithCleanup "docker run failed: $($r.Err.Trim())" }
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
$oldText = if ($configBytes) { (New-Object System.Text.UTF8Encoding($false)).GetString($configBytes) } else { '' }
$hasInclude = @($oldText -split '\r?\n' | Where-Object { $_.Trim().TrimStart([char]0xFEFF) -eq $include }).Count -gt 0
if (-not $hasInclude) {
    # At the top: an Include after a Host or Match line would apply only inside that block. The new line is put in
    # front of the original bytes (after a UTF-8 BOM, if any), so nothing already there is re-encoded.
    $nl = if ($oldText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $line = [Text.Encoding]::UTF8.GetBytes($include + $nl)
    if ($configBytes) {
        $script:configBackup = "$sshConfig.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item -LiteralPath $sshConfig -Destination $script:configBackup
        $skip = if ($configBytes.Length -ge 3 -and $configBytes[0] -eq 0xEF -and $configBytes[1] -eq 0xBB -and $configBytes[2] -eq 0xBF) { 3 } else { 0 }
        $out = New-Object byte[] ($configBytes.Length + $line.Length)
        [Array]::Copy($configBytes, 0, $out, 0, $skip)
        [Array]::Copy($line, 0, $out, $skip, $line.Length)
        [Array]::Copy($configBytes, $skip, $out, $skip + $line.Length, $configBytes.Length - $skip)
        [IO.File]::WriteAllBytes($sshConfig, $out)
        Write-Host 'ssh config: Include added at the top (the old file was backed up; its ACL is unchanged)'
    }
    else {
        [IO.File]::WriteAllBytes($sshConfig, $line)
        Set-OwnerOnlyAcl $sshConfig
        Write-Host 'ssh config: Include added at the top (a new file)'
    }
}

$g = Invoke-Native 'ssh' ((Get-SshBaseArgs) + @('-G', $alias))
$want = @("hostname 127.0.0.1", "port $Port", "user lanq", "hostkeyalias $alias")
$have = @($g.Out -split '\r?\n' | ForEach-Object { $_.Trim().ToLowerInvariant() })
$missing = @($want | Where-Object { $have -notcontains $_.ToLowerInvariant() })
if ($g.ExitCode -ne 0 -or $missing.Count -gt 0) { Stop-FailedWithCleanup "ssh -G $alias does not resolve to this container (check config.d\$alias and the Include)" }
Write-Host 'ssh config: ok'

$login = $null
for ($try = 0; $try -lt 15; $try++) {
    $login = Invoke-Native 'ssh' ((Get-SshBaseArgs) + @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=5', '-i', $keyPath, $alias, 'true')) 30000
    if ($login.ExitCode -eq 0) { break }
    Start-Sleep -Seconds 2
}
if ($login.ExitCode -ne 0) { Stop-FailedWithCleanup "key login to $alias did not work (docker logs $container shows sshd's side)" }
Write-Host "login: ok. Next (the owner): setup.ps1 -Register, then lanq makeready $Name"
exit 0
