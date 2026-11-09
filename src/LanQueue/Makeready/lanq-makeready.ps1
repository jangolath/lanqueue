<#
.SYNOPSIS
  lanq-makeready: READ-ONLY check of whether this PC can be a LanQueue worker.
.DESCRIPTION
  Changes nothing, installs nothing, makes no network calls, needs no admin rights.
  Every fix is printed as advice for you to run yourself (or not).
  Exit code: 0 = worker-ready, 1 = not ready, 2 = bad arguments.
.PARAMETER Json
  Also write a JSON report (schema lanq-makeready/v1) to this path. Use '-' to write ONLY the JSON to
  stdout (the human report is suppressed); this is the mode for running over SSH.
.PARAMETER Out
  Also save the human report to this NEW file. An existing file is never overwritten (exit 2).
.PARAMETER Redact
  Replace hostname, user name and user-profile paths with <redacted>.
.PARAMETER Hours
  "HH:mm-HH:mm" window used for the suggested allowed_hours (start must differ from end).
.PARAMETER Quiet
  Print only the verdict lines.
#>
[CmdletBinding(PositionalBinding = $false)]
param([string]$Json, [string]$Out, [switch]$Redact, [string]$Hours, [switch]$Quiet, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Extra)

$ToolVersion = '0.2.0'

#region ADVICE
# The ONE table of advice text. Nothing here is ever executed by this script.
$Advice = @{
  'ssh.install'  = 'Install OpenSSH Server yourself (elevated PowerShell): Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0   then: Start-Service sshd ; Set-Service -Name sshd -StartupType Automatic'
  'ssh.start'    = 'OpenSSH Server is installed but not running. In an elevated PowerShell: Start-Service sshd ; Set-Service -Name sshd -StartupType Automatic'
  'ssh.auto'     = 'sshd will not come back after a reboot. In an elevated PowerShell: Set-Service -Name sshd -StartupType Automatic'
  'ssh.fw'       = 'Port 22 is not open in the firewall. First check the network is Private: Get-NetConnectionProfile (if it says Public, in an elevated PowerShell: Set-NetConnectionProfile -InterfaceAlias <name> -NetworkCategory Private). Then, elevated: Enable-NetFirewallRule -Name OpenSSH-Server-In-TCP ; Set-NetFirewallRule -Name OpenSSH-Server-In-TCP -Profile Private -RemoteAddress LocalSubnet   (if that rule does not exist: New-NetFirewallRule -Name OpenSSH-Server-In-TCP -DisplayName "OpenSSH Server (sshd)" -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Private -RemoteAddress LocalSubnet)'
  'ssh.shell'    = 'LanQueue starts powershell from the default SSH shell, which must be cmd.exe (the default) or PowerShell. In an elevated PowerShell, return to the default: Remove-ItemProperty -Path HKLM:\SOFTWARE\OpenSSH -Name DefaultShell'
  'ssh.nokeys'   = 'Put the orchestrator public key in the build user authorized_keys (for an Administrators member: C:\ProgramData\ssh\administrators_authorized_keys). A dedicated non-admin build user is simpler and safer. To make one (elevated): New-LocalUser build ; Add-LocalGroupMember -SID S-1-5-32-545 -Member build ; then log on once as build (so C:\Users\build exists) BEFORE creating C:\Users\build\.ssh\authorized_keys, or sshd reads keys from the wrong profile folder.'
  'ssh.aclkeys'  = 'administrators_authorized_keys must be writable only by Administrators and SYSTEM, or sshd ignores it: icacls.exe C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant "*S-1-5-32-544:F" /grant "*S-1-5-18:F"   (SIDs, so it works on any Windows language)'
  'ssh.admin'    = 'This user is an administrator. A dedicated non-admin build user is simpler and safer for a worker (see the ssh.nokeys advice for how to create one: add it to Users by SID S-1-5-32-545 and log on once before creating its .ssh folder).'
  'git.install'  = 'git is only needed for transfer: git. Without it use transfer: archive, or install Git for Windows yourself.'
  'git.longpaths'= 'Optional: git config --system core.longpaths true (elevated), and keep work_root short, for example C:/lq.'
  'tar.missing'  = 'transfer: archive needs the Windows built-in bsdtar (Windows 10 1803+ / Server 2019+) or GNU tar / bsdtar on Linux. busybox tar is refused by LanQueue.'
  'longpaths.on' = 'Optional: enable long paths (Settings registry LongPathsEnabled=1, reboot) and keep work_root short, for example C:/lq.'
  'win10.eol'    = 'Windows 10 reached end of support on 2025-10-14. It still works as a worker while it gets security updates (Extended Security Updates, ESU; consumer ESU runs to 2027-10-12; enroll in Settings > Windows Update > Enroll now). 22H2 (build 19045) is the last version: update to it in Settings > Update & Security > Windows Update. Long term: Windows 11 or Linux.'
  'os.old'       = 'This Windows version is too old for .NET 10 and Docker. Use a newer Windows or Linux.'
  'arch32'       = '32-bit systems cannot run Docker (and 32-bit x86 Linux is not supported by .NET 10). Install a 64-bit OS (the CPU may support it).'
  'arch32.win'   = '.NET 10 supports 32-bit (x86) Windows, but a 64-bit OS is strongly recommended: more RAM, Docker, and current toolchains need it.'
  'docker.server'= 'Docker Desktop is not available on Windows Server. Docker Engine or Linux may be an option.'
  'ram.low'      = 'Less than 4 GB RAM: builds will swap and be slow. In machines.yml keep max_jobs: 1 and min_free_mem_gb (LanQueue holds the PC while less memory is free), and cap build tools with the env: lines in the snippet. Below 2 GB .NET SDK builds are unreliable.'
  'swap.none'    = 'No (or a tiny) page file: a build that runs out of memory fails. Run SystemPropertiesAdvanced.exe > Performance Settings > Advanced > Virtual memory > Change > tick Automatically manage paging file size for all drives, then restart.'
  'disk.hdd'     = 'A spinning disk makes builds slow. An SSD (even a small cheap one) is the single best upgrade for an old PC.'
  'disk.low'     = 'Free disk space is low. Each job needs room for a worktree plus build output; keep at least 20 GB free, below 10 GB the worker is refused. Settings > System > Storage (Cleanup recommendations, Storage Sense) helps.'
  'dotnet.os'    = 'This OS is not on the .NET 10 supported list. It may still run; Windows 11, Windows Server, or a listed Linux distribution is the safe choice.'
  'dotnet.glibc' = 'glibc 2.27 or newer is required (musl 1.2.3 on Alpine). Use a newer distribution release.'
  'dotnet.mac'   = '.NET 10 supports macOS 15 and newer. Note LanQueue workers are windows|linux only.'
  'docker.virt'  = 'Hardware virtualization is off. Turn on Intel VT-x / AMD-V (SVM) in the BIOS/UEFI setup yourself.'
  'docker.ram'   = 'Docker Desktop wants 8 GB RAM. With less, use Linux with Docker Engine, or Docker Engine inside WSL 2 with a memory cap in .wslconfig (memory=2GB). Below 4 GB, containers are not recommended.'
  'docker.wsl'   = 'WSL 2 was not found. Docker Desktop (WSL 2 backend) needs it; see learn.microsoft.com/windows/wsl/install.'
  'docker.build' = 'Docker Desktop needs Windows 10 22H2 (build 19045) or Windows 11 23H2 (build 22631) and newer. Update Windows or use Linux.'
  'docker.daemon'= 'The Docker client is installed but the engine is not answering. Start Docker Desktop (or the docker service) yourself.'
  'power.sleep'  = 'The PC will sleep or hibernate mid-job. Review it yourself in Power Options, or in an elevated prompt: powercfg /change standby-timeout-ac 0 and powercfg /change hibernate-timeout-ac 0'
  'reboot.pending'= 'A restart is pending; the PC may reboot during a job. Restart at a time you choose: Start > Power > Update and restart (or Restart).'
  'wol.wired'    = 'Wake-on-LAN needs a wired Ethernet connection (Wi-Fi rarely supports it). Use a cable, or leave wake: out and keep the PC awake.'
  'wol.win'      = 'Device Manager (devmgmt.msc) > Network adapters > the wired adapter > Properties. Power Management tab: tick "Allow this device to wake the computer" and "Only allow a magic packet to wake the computer". Advanced tab: set "Wake on Magic Packet" to Enabled. Turn Fast Startup off if it is on (see the wol.fast advice) and enable Wake on LAN in the BIOS/UEFI. Test: lanq wake <machine> --wait'
  'wol.fast'     = 'Fast Startup is on: a PC shut down this way is really hibernated and often ignores the magic packet. Control Panel > Power Options > Choose what the power buttons do > Change settings that are currently unavailable > untick "Turn on fast startup". Waking from sleep usually works either way.'
  'updates.hours'= 'Windows Update may restart the PC inside your job window. Widen Active hours (Settings > Windows Update > Advanced options) to cover it.'
  'startup.many' = 'Many startup items slow boot and use RAM. Review them yourself in Task Manager > Startup apps. This tool never disables anything.'
  'temp.big'     = 'Large temp/cache folders. Disk Cleanup or Storage Sense can reclaim space; review before deleting. This tool never deletes anything.'
  'defender.rtp' = 'Real-time scanning slows builds. Options: a Dev Drive (Windows 11, performance mode) or a scoped exclusion for ONLY the work folder. Both lower protection a little; your decision.'
  'drivers.bad'  = 'Devices report driver problems. Check Windows Update > Optional updates, or the PC maker site. Never use a generic driver updater on a headless worker.'
}
# Advice that needs an elevated (administrator) prompt to act on.
$AdviceAdmin = @('ssh.install', 'ssh.start', 'ssh.auto', 'ssh.fw', 'ssh.aclkeys', 'ssh.shell', 'git.longpaths', 'longpaths.on', 'power.sleep', 'wol.win', 'wol.fast',
  'swap.none', 'win10.eol', 'os.old', 'docker.virt', 'docker.wsl', 'docker.build', 'docker.server', 'defender.rtp', 'drivers.bad', 'dotnet.os', 'tar.missing', 'arch32', 'arch32.win')
#endregion ADVICE

# ---------- pure helpers (no I/O): used by tests with fixtures ----------

function New-Check {
  param([string]$Id, [string]$Title, [string]$Status, [string]$Detail, [string]$AdviceKey = '', [bool]$Admin = $false)
  $adv = ''
  if ($AdviceKey -and $Advice.ContainsKey($AdviceKey)) { $adv = $Advice[$AdviceKey] }
  [pscustomobject]@{ id = $Id; title = $Title; status = $Status; detail = $Detail; advice = $adv; needsAdmin = $Admin; adminToFix = ($AdviceAdmin -contains $AdviceKey) }
}

function Test-Hours([string]$H) {
  if ($H -notmatch '^([01]\d|2[0-3]):([0-5]\d)-([01]\d|2[0-3]):([0-5]\d)$') { return $false }
  $parts = $H.Split('-')
  return ($parts[0] -ne $parts[1])
}

function Test-HourInWindow([int]$Hour, [int]$StartH, [int]$EndH) {
  if ($StartH -le $EndH) { return ($Hour -ge $StartH -and $Hour -lt $EndH) }
  return ($Hour -ge $StartH -or $Hour -lt $EndH)
}

# True when some hour of the job window is NOT covered by Windows Update active hours.
function Test-UpdateOverlap([string]$H, [int]$ActStart, [int]$ActEnd) {
  $w = $H.Split('-'); $ws = [int]$w[0].Substring(0, 2); $we = [int]$w[1].Substring(0, 2)
  if ($w[1].Substring(3, 2) -ne '00') { $we = ($we + 1) % 24 }
  for ($hr = 0; $hr -lt 24; $hr++) {
    if ((Test-HourInWindow $hr $ws $we) -and -not (Test-HourInWindow $hr $ActStart $ActEnd)) { return $true }
  }
  return $false
}

# Seconds from `powercfg /q` text (AC value); $null when not found. 0 means "never".
# Works on localized Windows: the AC value is the second-to-last 0x........ value in the output.
function Get-PowercfgAcSeconds([string]$Text) {
  if ($Text -match 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)') { return [Convert]::ToInt64($Matches[1], 16) }
  $hex = @([regex]::Matches($Text, '0x([0-9a-fA-F]{8})\s*(?:\r?\n|$)'))
  if ($hex.Count -lt 2) { return $null }
  return [Convert]::ToInt64($hex[$hex.Count - 2].Groups[1].Value, 16)
}

function Get-Worst($Statuses) {
  $rank = @{ SKIP = 0; OK = 1; WARN = 2; FAIL = 3 }; $w = 'SKIP'
  foreach ($s in $Statuses) { if ($s -and $rank.ContainsKey($s) -and $rank[$s] -gt $rank[$w]) { $w = $s } }
  return $w
}

# A non-elevated administrator still has S-1-5-32-544 as a deny-only group, which .Groups omits.
function Test-AdminSid($GroupSids, $DenyOnlySids) {
  $a = 'S-1-5-32-544'
  return ((@($GroupSids) -contains $a) -or (@($DenyOnlySids) -contains $a))
}

function Get-SafeName([string]$S) {
  $n = ("$S".ToLowerInvariant() -replace '[^a-z0-9-]', '-').Trim('-')
  if (-not $n) { return 'old-pc' }
  if ($n.Length -gt 63) { $n = $n.Substring(0, 63).Trim('-') }
  return $n
}

function Test-SshUser([string]$U) { return [bool]($U -match '^[A-Za-z0-9][A-Za-z0-9._-]*$') }

# True when the docker client would talk to a remote engine (do not probe it).
function Test-DockerRemote([string]$DockerHost, [string]$Context) {
  if ($DockerHost -and $DockerHost -notmatch '^(unix|npipe)://') { return $true }
  if ($Context -and @('default', 'desktop-linux', 'desktop-windows', 'orbstack', 'colima', 'rancher-desktop') -notcontains $Context) { return $true }
  return $false
}

# Work drive: >= 20 GB free, SSD preferred, then most free space; else >= 10 GB; else the system drive.
function Select-WorkDrive($Drives, [string]$SysDrive) {
  $list = @($Drives)
  foreach ($min in @(20, 10)) {
    $ok = @($list | Where-Object { $_.FreeGB -ge $min })
    if ($ok.Count -eq 0) { continue }
    $ssd = @($ok | Where-Object { $_.Media -eq 'SSD' })
    if ($ssd.Count -gt 0) { $ok = $ssd }
    return ($ok | Sort-Object { $_.FreeGB } -Descending | Select-Object -First 1)
  }
  $s = @($list | Where-Object { $_.Letter -eq $SysDrive })
  if ($s.Count -gt 0) { return $s[0] }
  return @{ Letter = $SysDrive; FreeGB = 0; Media = 'Unknown' }
}

# Never run wsl.exe unless WSL is installed (the inbox stub can offer to install it).
# $Installed is $true, $false, or $null (could not be determined): the answer is then $null as well.
function Get-WslState($Installed, [scriptblock]$Run) {
  if ($null -eq $Installed) { return $null }
  if (-not $Installed) { return $false }
  $r = & $Run
  return [bool]($r -and $r.Ok)
}

# Feature/service or registry says installed; otherwise the Store package decides ($null = unknown).
function Get-WslInstalled([bool]$ServiceOrKey, $StorePackage) {
  if ($ServiceOrKey) { return $true }
  if ($null -eq $StorePackage) { return $null }
  return [bool]$StorePackage
}

# A one-element result array holding $false or 0 is falsy in PowerShell: only $null means "unknown".
function Get-ProbeBool($R) { if ($null -eq $R) { return $null }; return [bool](@($R)[0]) }
function Get-ProbeInt($R) { if ($null -eq $R) { return $null }; return [int](@($R)[0]) }

function Test-Sdk8([object[]]$Majors) { return [bool](@($Majors | Where-Object { [int]$_ -ge 8 }).Count) }

function Get-DockerCheck($F) {
  $t = 'Docker'
  if ($F.DockerCli -and $F.DockerRemote) { return New-Check 'docker' $t 'SKIP' 'remote docker endpoint not probed' }
  if ($F.DockerServerOk) { return New-Check 'docker' $t 'OK' "engine answering (server $($F.DockerServer))" }
  if ($F.DockerCli) { return New-Check 'docker' $t 'WARN' 'client installed but engine not reachable' 'docker.daemon' }
  if (-not $F.Is64) { return New-Check 'docker' $t 'FAIL' '32-bit Windows cannot run Docker' 'arch32' }
  if ($F.Build -lt 19041) { return New-Check 'docker' $t 'FAIL' "Windows build $($F.Build) has no WSL 2 (needs Windows 10 2004 / 19041 or newer), so Docker Desktop cannot run" 'docker.build' }
  if ($F.Build -lt 19045) { return New-Check 'docker' $t 'WARN' "Windows build $($F.Build) is below Docker's listed minimum (19045 / 22H2); usually runs" 'docker.build' }
  if ($F.IsServer) { return New-Check 'docker' $t 'WARN' 'Docker Desktop is not available on Windows Server' 'docker.server' }
  if ($F.Build -ge 22000 -and $F.Build -lt 22631) { return New-Check 'docker' $t 'WARN' "Windows 11 build $($F.Build) is below Docker's listed minimum (22631 / 23H2); usually runs" 'docker.build' }
  $virt = ($F.HypervisorPresent -eq $true) -or ($F.VirtFirmware -eq $true)
  $note = ''
  if ($F.HypervisorPresent -eq $true) { $note = ' (a hypervisor is already running, so virtualization is enabled)' }
  if (-not $virt -and $F.VirtFirmware -eq $false) { return New-Check 'docker' $t 'FAIL' 'hardware virtualization is disabled in firmware' 'docker.virt' }
  if ($F.RamGB -lt 4) { return New-Check 'docker' $t 'WARN' "only $($F.RamGB) GB RAM: containers not recommended" 'docker.ram' }
  if ($F.RamGB -lt 8) { return New-Check 'docker' $t 'WARN' "Docker Desktop wants 8 GB RAM, this PC has $($F.RamGB) GB" 'docker.ram' }
  if ($F.WslOk -eq $false) { return New-Check 'docker' $t 'WARN' 'not installed, and WSL 2 was not found' 'docker.wsl' }
  if ($null -eq $F.WslOk) { return New-Check 'docker' $t 'SKIP' 'could not determine whether WSL is installed' }
  return New-Check 'docker' $t 'OK' "not installed; prerequisites look fine$note"
}

# .NET 10 supported-OS list (dotnet/core release-notes/10.0/supported-os.md), see docs/DESIGN.md.
function Get-DotnetCheck($F) {
  $t = '.NET 10 SDK'
  $have = 'no SDK found'
  if ($F.DotnetSdks -and @($F.DotnetSdks).Count -gt 0) { $have = 'SDK majors found: ' + (@($F.DotnetSdks) -join ', ') }
  if ($F.RamGB -lt 2) { return New-Check 'dotnet' $t 'FAIL' "under 2 GB RAM ($have)" 'ram.low' }
  $ed = "$($F.EditionId)"; $b = [int]$F.Build
  $st = 'OK'; $adv = ''; $why = 'supported OS'
  if ($F.IsServer) {
    if ($b -lt 9200) { return New-Check 'dotnet' $t 'FAIL' "$($F.Caption) is not supported ($have)" 'os.old' }
    if ($b -lt 14393) { $st = 'WARN'; $adv = 'dotnet.os'; $why = 'Server 2012/2012 R2 is listed only with ESU' }
  } elseif ($b -ge 26100) { $why = 'supported OS (Windows 11 24H2 or newer)'
  } elseif ($b -eq 22631) {
    if ($ed -notmatch '^(Enterprise|IoTEnterprise)') { $st = 'WARN'; $adv = 'dotnet.os'; $why = 'Windows 11 23H2 is listed for Enterprise/IoT only, not on the published list for this edition' }
  } elseif ($b -ge 22000) { $st = 'WARN'; $adv = 'dotnet.os'; $why = 'this Windows 11 version is not on the published list'
  } elseif ($b -ge 10240) {
    if (-not (@(14393, 17763, 19044) -contains $b -and $ed -match '^(Enterprise|IoTEnterprise)')) { $st = 'WARN'; $adv = 'dotnet.os'; $why = 'not on the published list, usually runs' }
  } else { return New-Check 'dotnet' $t 'FAIL' "Windows build $b is too old ($have)" 'os.old' }
  if (-not $F.Is64 -and -not $F.IsArm) { if ($st -eq 'OK') { $st = 'WARN'; $adv = 'arch32.win' }; $why += '; 32-bit (x86) Windows' }
  if ($st -eq 'OK' -and $F.RamGB -lt 4) { $st = 'WARN'; $adv = 'ram.low'; $why += ' but low RAM' }
  return New-Check 'dotnet' $t $st "$why ($have)" $adv
}

# Pool sizing, the thresholds LanQueue 0.5.1+ lanq health uses. RAM in GB, logical CPUs (24-31 GB counts as the top band).
function Get-SuggestedJobs([double]$RamGB, [int]$Cpu) {
  if ($RamGB -lt 6) { $j = 1 } elseif ($RamGB -lt 12) { $j = 2 }
  elseif ($RamGB -lt 24) { $j = 3; if ($Cpu -ge 8) { $j = 4 } }
  else { $j = 6; if ($Cpu -ge 16) { $j = 8 } }
  if ($Cpu -ge 1 -and $j -gt $Cpu) { $j = $Cpu }
  return $j
}
function Get-SuggestedMinMem([double]$RamGB) { if ($RamGB -lt 6) { return 1.5 }; return 2 }
function Get-SuitedTier([double]$RamGB, [int]$Cpu) { if ($RamGB -lt 6) { return 'light' }; if ($RamGB -ge 16 -and $Cpu -ge 4) { return 'heavy' }; return 'medium' }
function Get-SuitedText([string]$Tier, [bool]$Docker) {
  $t = @{ light = 'light: lint and unit-test shards'; medium = 'medium: test shards and builds'; heavy = 'heavy: full suites and large builds' }[$Tier]
  if ($Docker) { $t += ' + container builds' }
  return $t
}

# Get-NetAdapter lines "name|description|mac|media|status" -> the wired adapter (an Up one first) and whether Wi-Fi exists.
function Select-WiredNic($Lines) {
  $wired = @(); $wifi = $false
  foreach ($l in @($Lines)) {
    $p = "$l".Split('|'); if ($p.Count -lt 5) { continue }
    if ($p[3] -match '802\.11' -or $p[1] -match '(?i)wi-?fi|wireless|802\.11') { $wifi = $true; continue }
    if ($p[3] -ne '802.3' -or $p[1] -match '(?i)virtual|hyper-v|vpn|tap-|bluetooth') { continue }
    if ($p[2] -notmatch '^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$') { continue }
    $wired += @{ Name = $p[0]; Desc = $p[1]; Mac = $p[2].ToLowerInvariant().Replace('-', ':'); Up = ($p[4] -eq 'Up') }
  }
  $nic = @($wired | Where-Object { $_.Up }) + @($wired | Where-Object { -not $_.Up }) | Select-Object -First 1
  return @{ Nic = $nic; Wifi = $wifi }
}

# `powercfg /devicequery wake_armed` lists the devices allowed to wake the PC, one per line.
function Test-WakeArmed([string]$Text, [string]$Desc) {
  foreach ($l in ($Text -split "`r?`n")) { if ($l.Trim() -and $l.Trim() -eq $Desc) { return $true } }
  return $false
}

function Test-SshShellOk([string]$Shell) { return ((-not $Shell) -or $Shell -match '(?i)(^|\\)(cmd|powershell|pwsh)(\.exe)?$') }

# Fast Startup only matters when hibernation is on: HiberbootEnabled=1 (Session Manager\Power) AND hibernation on (Control\Power:
# HibernateEnabled if present, else HibernateEnabledDefault; many PCs have neither value, and then hibernation is on).
function Test-FastStartup($Hiberboot, $HibernateEnabled, $HibernateDefault) {
  $h = 1
  if ($null -ne $HibernateEnabled) { $h = $HibernateEnabled } elseif ($null -ne $HibernateDefault) { $h = $HibernateDefault }
  return (($Hiberboot -eq 1) -and ($h -eq 1))
}

# Port 22: the named rule (Get-NetFirewallRule works without admin) decides; only when it is missing is the port-filter scan used.
function Get-FirewallCheck($F) {
  $t = 'Firewall port 22'
  switch ("$($F.SshFwRule)") {
    'enabled' { return New-Check 'ssh.firewall' $t 'OK' 'rule OpenSSH-Server-In-TCP is enabled' }
    'disabled' { return New-Check 'ssh.firewall' $t 'WARN' 'rule OpenSSH-Server-In-TCP exists but is disabled' 'ssh.fw' }
    'missing' {
      if ($F.SshFwPort -isnot [bool]) { return New-Check 'ssh.firewall' $t 'SKIP' 'rule OpenSSH-Server-In-TCP is missing; other rules for port 22 need admin to read' '' $true }
      if ($F.SshFwPort) { return New-Check 'ssh.firewall' $t 'OK' 'another enabled inbound rule allows TCP 22' }
      return New-Check 'ssh.firewall' $t 'WARN' 'no enabled inbound rule for TCP port 22 (OpenSSH-Server-In-TCP is missing)' 'ssh.fw'
    }
  }
  return New-Check 'ssh.firewall' $t 'SKIP' 'could not read the firewall rules'
}

function Get-WolCheck($F) {
  $t = 'Wake-on-LAN'
  if (-not $F.WolMac) {
    if ($F.WolProbe -eq $false) { return New-Check 'wol' $t 'SKIP' 'could not list the network adapters' }
    if ($F.WolWifi) { return New-Check 'wol' $t 'WARN' 'Wi-Fi only, no wired Ethernet adapter: Wake-on-LAN needs a cable' 'wol.wired' }
    return New-Check 'wol' $t 'WARN' 'no wired Ethernet adapter found' 'wol.wired'
  }
  $n = "wired $($F.WolNic) ($($F.WolMac))"; $bad = @()
  if ($F.WolMagic -eq 'Disabled') { $bad += 'Wake on Magic Packet is disabled' } elseif ($F.WolMagic -eq 'Unsupported') { $bad += 'the adapter does not support magic-packet wake' }
  if ($F.WolArmed -eq $false) { $bad += 'the adapter is not allowed to wake the computer' }
  if ($bad.Count -gt 0) { if ($F.FastStartup) { $bad += 'Fast Startup is on' }; return New-Check 'wol' $t 'WARN' "${n}: $($bad -join '; ')" 'wol.win' }
  if ($F.FastStartup) { return New-Check 'wol' $t 'WARN' "${n}: Fast Startup is on (a shut-down PC often will not wake)" 'wol.fast' }
  if ($F.WolMagic -ne 'Enabled' -or $null -eq $F.WolArmed) {
    return New-Check 'wol' $t 'SKIP' "${n}: the magic-packet setting is not readable without admin; check Device Manager > Network adapters > the adapter > Power Management and Advanced (Wake on Magic Packet)" '' $true
  }
  return New-Check 'wol' $t 'OK' "${n}: magic-packet wake on, allowed to wake the PC, Fast Startup off; the BIOS/UEFI setting cannot be read, enable it there too"
}

function Get-Checks($F) {
  $c = New-Object System.Collections.ArrayList
  # --- platform
  $d = "$($F.Caption) build $($F.Build)"
  if ($F.EditionId) { $d += ", $($F.EditionId)" }
  if (-not $F.IsServer -and $F.Build -ge 10240 -and $F.Build -lt 22000) {
    $d += '; Windows 10 is past end of support (2025-10-14)'
    if ($F.Build -lt 19045) { $d += ' and not on 22H2, its last version' }
  }
  if ($F.IsServer -and $F.Build -lt 9200) { [void]$c.Add((New-Check 'os.version' 'OS' 'FAIL' $d 'os.old')) }
  elseif (-not $F.IsServer -and $F.Build -ge 10240 -and $F.Build -lt 22000) { [void]$c.Add((New-Check 'os.version' 'OS' 'WARN' $d 'win10.eol')) }
  elseif (-not $F.IsServer -and $F.Build -lt 10240) { [void]$c.Add((New-Check 'os.version' 'OS' 'FAIL' $d 'os.old')) }
  else { [void]$c.Add((New-Check 'os.version' 'OS' 'OK' $d)) }
  if ($F.Is64 -or $F.IsArm) { [void]$c.Add((New-Check 'os.arch' 'Architecture' 'OK' "$($F.Arch)")) }
  else { [void]$c.Add((New-Check 'os.arch' 'Architecture' 'WARN' "32-bit ($($F.Arch))" 'arch32.win')) }
  [void]$c.Add((New-Check 'cpu' 'CPU' 'OK' "$($F.CpuName), $($F.Cores) cores / $($F.Logical) threads, AVX2: $($F.Avx2) (informational)"))
  $rs = 'OK'; $ra = ''
  if ($F.RamGB -lt 2) { $rs = 'FAIL'; $ra = 'ram.low' } elseif ($F.RamGB -lt 4) { $rs = 'WARN'; $ra = 'ram.low' }
  [void]$c.Add((New-Check 'ram' 'Memory' $rs "$($F.RamGB) GB installed, $($F.FreeRamGB) GB free" $ra))
  $ds = 'OK'; $da = ''; $dd = "system drive $($F.SysFreeGB) GB free, work drive $($F.WorkDrive) $($F.WorkFreeGB) GB free, work drive type: $($F.DiskMedia)"
  $minFree = [math]::Min([double]$F.SysFreeGB, [double]$F.WorkFreeGB)
  if ($minFree -lt 10) { $ds = 'FAIL'; $da = 'disk.low' }
  elseif ($F.DiskMedia -eq 'HDD') { $ds = 'WARN'; $da = 'disk.hdd' }
  elseif ($minFree -lt 20) { $ds = 'WARN'; $da = 'disk.low' }
  if ($da -eq 'disk.low') { $dd += ' (recommended: at least 20 GB free; under 10 GB is refused)' }
  [void]$c.Add((New-Check 'disk' 'Disk' $ds $dd $da))
  # --- ssh worker
  $shell = 'cmd.exe (LanQueue invokes powershell explicitly)'
  if ($F.DefaultShell) { $shell = "$($F.DefaultShell)" }
  if (-not $F.SshdInstalled) { [void]$c.Add((New-Check 'ssh.server' 'OpenSSH Server' 'FAIL' 'not installed' 'ssh.install')) }
  elseif (-not $F.SshdRunning) { [void]$c.Add((New-Check 'ssh.server' 'OpenSSH Server' 'FAIL' 'installed but not running' 'ssh.start')) }
  elseif ($F.SshdStartType -and $F.SshdStartType -ne 'Automatic') { [void]$c.Add((New-Check 'ssh.server' 'OpenSSH Server' 'WARN' "running, start type $($F.SshdStartType); default shell: $shell" 'ssh.auto')) }
  else { [void]$c.Add((New-Check 'ssh.server' 'OpenSSH Server' 'OK' "running, automatic start; default shell: $shell")) }
  if ($F.SshdInstalled) {
    [void]$c.Add((Get-FirewallCheck $F))
    if (Test-SshShellOk $F.DefaultShell) { [void]$c.Add((New-Check 'ssh.shell' 'SSH default shell' 'OK' $shell)) }
    else { [void]$c.Add((New-Check 'ssh.shell' 'SSH default shell' 'WARN' "$($F.DefaultShell): not cmd.exe or PowerShell, so lanq's commands may fail" 'ssh.shell')) }
  }
  if ($F.IsAdminUser) {
    if (-not $F.AdminKeysExists) { [void]$c.Add((New-Check 'ssh.keys' 'SSH keys' 'WARN' 'user is an administrator; administrators_authorized_keys not found' 'ssh.nokeys')) }
    elseif ($F.AdminKeysAclOk -eq $false) { [void]$c.Add((New-Check 'ssh.keys' 'SSH keys' 'WARN' 'administrators_authorized_keys has too-open permissions' 'ssh.aclkeys')) }
    else { [void]$c.Add((New-Check 'ssh.keys' 'SSH keys' 'OK' 'administrators_authorized_keys present (this user is an administrator)' 'ssh.admin')) }
  } elseif ($F.AuthKeysNonEmpty) { [void]$c.Add((New-Check 'ssh.keys' 'SSH keys' 'OK' 'authorized_keys present and non-empty')) }
  else { [void]$c.Add((New-Check 'ssh.keys' 'SSH keys' 'WARN' 'no authorized_keys for this user (fine if another user is the build user)' 'ssh.nokeys')) }
  if ($F.GitVer) {
    if ($F.GitLongPaths) { [void]$c.Add((New-Check 'git' 'git' 'OK' "$($F.GitVer), core.longpaths on")) }
    else { [void]$c.Add((New-Check 'git' 'git' 'WARN' "$($F.GitVer), core.longpaths not enabled" 'git.longpaths')) }
  } else { [void]$c.Add((New-Check 'git' 'git' 'WARN' 'not found (needed for transfer: git only)' 'git.install')) }
  if ($F.TarBsd) { [void]$c.Add((New-Check 'tar' 'tar' 'OK' 'built-in bsdtar found')) }
  else { [void]$c.Add((New-Check 'tar' 'tar' 'WARN' 'bsdtar not found (needed for transfer: archive only)' 'tar.missing')) }
  if ($F.LongPaths) { [void]$c.Add((New-Check 'longpaths' 'Long paths' 'OK' 'LongPathsEnabled = 1')) }
  else { [void]$c.Add((New-Check 'longpaths' 'Long paths' 'WARN' 'LongPathsEnabled is off' 'longpaths.on')) }
  # --- toolchains
  [void]$c.Add((Get-DotnetCheck $F))
  [void]$c.Add((Get-DockerCheck $F))
  foreach ($k in @('node', 'python', 'java', 'go', 'rustc')) {
    $v = $null; if ($F.Tools) { $v = $F.Tools[$k] }
    if ($v) { [void]$c.Add((New-Check $k $k 'OK' "$v")) } else { [void]$c.Add((New-Check $k $k 'SKIP' 'not installed (optional, only if your jobs use it)')) }
  }
  # --- overnight
  if ($null -eq $F.StandbyAcSec) { [void]$c.Add((New-Check 'power.sleep' 'Sleep/hibernate (AC)' 'SKIP' 'could not read powercfg')) }
  elseif ($F.StandbyAcSec -gt 0 -or $F.HibernateAcSec -gt 0) {
    [void]$c.Add((New-Check 'power.sleep' 'Sleep/hibernate (AC)' 'WARN' "sleeps after $([math]::Round($F.StandbyAcSec / 60)) min, hibernates after $([math]::Round($F.HibernateAcSec / 60)) min (0 = never)" 'power.sleep'))
  } else { [void]$c.Add((New-Check 'power.sleep' 'Sleep/hibernate (AC)' 'OK' 'never sleeps or hibernates on AC')) }
  if ($F.RebootPending) { [void]$c.Add((New-Check 'reboot.pending' 'Pending reboot' 'WARN' 'a restart is pending' 'reboot.pending')) }
  else { [void]$c.Add((New-Check 'reboot.pending' 'Pending reboot' 'OK' 'none')) }
  if (-not $F.Hours) { [void]$c.Add((New-Check 'updates.activehours' 'Update restarts vs job window' 'SKIP' 'pass -Hours to compare')) }
  elseif ($null -eq $F.ActiveStart) { [void]$c.Add((New-Check 'updates.activehours' 'Update restarts vs job window' 'SKIP' 'active hours not set in registry')) }
  elseif (Test-UpdateOverlap $F.Hours ([int]$F.ActiveStart) ([int]$F.ActiveEnd)) {
    [void]$c.Add((New-Check 'updates.activehours' 'Update restarts vs job window' 'WARN' "job window $($F.Hours) is outside Windows Update active hours $('{0:00}' -f [int]$F.ActiveStart):00-$('{0:00}' -f [int]$F.ActiveEnd):00" 'updates.hours'))
  } else { [void]$c.Add((New-Check 'updates.activehours' 'Update restarts vs job window' 'OK' 'job window is inside active hours')) }
  [void]$c.Add((Get-WolCheck $F))
  if ($null -eq $F.SwapMB) { [void]$c.Add((New-Check 'swap' 'Page file' 'SKIP' 'could not read')) }
  elseif ($F.RamGB -lt 8 -and $F.SwapMB -lt 1024) { [void]$c.Add((New-Check 'swap' 'Page file' 'WARN' "$([math]::Round($F.SwapMB / 1024, 1)) GB page file with $($F.RamGB) GB RAM" 'swap.none')) }
  else { [void]$c.Add((New-Check 'swap' 'Page file' 'OK' "$([math]::Round($F.SwapMB / 1024, 1)) GB page file")) }
  # --- slowdowns (report only)
  if ($null -eq $F.StartupCount) { [void]$c.Add((New-Check 'startup' 'Startup items' 'SKIP' 'could not read')) }
  elseif ($F.StartupCount -gt 20) { [void]$c.Add((New-Check 'startup' 'Startup items' 'WARN' "$($F.StartupCount) items, e.g. $(@($F.StartupNames) -join ', ')" 'startup.many')) }
  else { [void]$c.Add((New-Check 'startup' 'Startup items' 'OK' "$($F.StartupCount) items")) }
  $big = @(); $sum = @()
  foreach ($t in @($F.TempSizes)) { if ($t) { $mark = ''; if ($t.Capped) { $mark = '>=' }; $sum += "$($t.Name) $mark$($t.MB) MB"; if ($t.MB -ge 5120) { $big += $t.Name } } }
  if ($sum.Count -eq 0) { [void]$c.Add((New-Check 'temp' 'Temp/cache folders' 'SKIP' 'none readable')) }
  elseif ($big.Count -gt 0) { [void]$c.Add((New-Check 'temp' 'Temp/cache folders' 'WARN' ($sum -join '; ') 'temp.big')) }
  else { [void]$c.Add((New-Check 'temp' 'Temp/cache folders' 'OK' ($sum -join '; '))) }
  if ($null -eq $F.DefenderRtp) { [void]$c.Add((New-Check 'defender' 'Defender real-time scan' 'SKIP' 'could not read')) }
  elseif ($F.DefenderRtp) { [void]$c.Add((New-Check 'defender' 'Defender real-time scan' 'OK' "on; Dev Drive available: $($F.DevDrive). Exclusions are not read." 'defender.rtp')) }
  else { [void]$c.Add((New-Check 'defender' 'Defender real-time scan' 'OK' 'off or managed by another product')) }
  if ($null -eq $F.ProblemDevices) { [void]$c.Add((New-Check 'drivers' 'Device drivers' 'SKIP' 'could not read')) }
  elseif (@($F.ProblemDevices).Count -gt 0) { [void]$c.Add((New-Check 'drivers' 'Device drivers' 'WARN' "$(@($F.ProblemDevices).Count) device(s) with problems: $(@($F.ProblemDevices | Select-Object -First 5) -join ', ')" 'drivers.bad')) }
  else { [void]$c.Add((New-Check 'drivers' 'Device drivers' 'OK' 'no devices report problems')) }
  return , $c.ToArray()
}

function Get-Verdicts($Checks, $F) {
  $s = @{}; foreach ($k in $Checks) { $s[$k.id] = $k.status }
  # 32-bit alone does not stop an SSH + git worker, so os.arch is not part of the worker verdicts
  $w = Get-Worst @($s['ssh.server'], $s['ssh.firewall'], $s['ssh.shell'], $s['ssh.keys'], $s['disk'], $s['os.version'])
  $git = 'FAIL'; if ($F.GitVer) { $git = 'OK' }
  $tar = 'FAIL'; if ($F.TarBsd) { $tar = 'OK' }
  [ordered]@{
    worker_git     = Get-Worst @($w, $git)
    worker_archive = Get-Worst @($w, $tar)
    dotnet10       = $s['dotnet']
    docker         = $s['docker']
    overnight      = Get-Worst @($s['power.sleep'], $s['reboot.pending'], $s['updates.activehours'])
  }
}

# One-line verdict plus the numbered next steps: every WARN/FAIL check, blockers first.
function Get-Summary($Checks, $V, $F) {
  $ready = ($V.worker_git -ne 'FAIL') -or ($V.worker_archive -ne 'FAIL')
  $noTransfer = (-not $F.GitVer) -and (-not $F.TarBsd)
  $workerIds = @('os.version', 'ssh.server', 'ssh.firewall', 'ssh.shell', 'ssh.keys', 'disk', 'git', 'tar')
  $b = @(); $f = @(); $w = @()
  foreach ($k in $Checks) {
    if (@('WARN', 'FAIL') -notcontains $k.status) { continue }
    $blk = (-not $ready) -and ($workerIds -contains $k.id) -and (($k.status -eq 'FAIL') -or ($noTransfer -and @('git', 'tar') -contains $k.id))
    $s = [ordered]@{ step = 0; id = $k.id; title = $k.title; status = $k.status; blocker = [bool]$blk; detail = $k.detail; action = $k.advice; adminToFix = [bool]$k.adminToFix }
    if ($blk) { $b += $s } elseif ($k.status -eq 'FAIL') { $f += $s } else { $w += $s }
  }
  $steps = @($b) + @($f) + @($w); $i = 0
  foreach ($s in $steps) { $i++; $s['step'] = $i }
  if (-not $ready) { $p = 'blockers'; if ($b.Count -eq 1) { $p = 'blocker' }; $vd = 'NOT_READY'; $tx = "NOT READY ($($b.Count) $p)" }
  elseif (($f.Count + $w.Count) -gt 0) { $vd = 'READY_WITH_WARNINGS'; $tx = "READY WITH WARNINGS ($($f.Count + $w.Count))" }
  else { $vd = 'READY'; $tx = 'READY' }
  return [ordered]@{ verdict = $vd; text = $tx; blockers = $b.Count; warnings = ($f.Count + $w.Count); nextSteps = $steps }
}

function Format-Steps($S) {
  $o = @()
  foreach ($st in @($S.nextSteps)) {
    $tag = 'should fix'; if ($st.blocker) { $tag = 'MUST FIX' }
    $who = 'no admin needed'; if ($st.adminToFix) { $who = 'needs admin: elevated PowerShell' }
    $act = $st.action; if (-not $act) { $act = 'see the detail above' }
    $o += ('{0,2}. [{1}] {2}: {3}' -f $st.step, $tag, $st.title, $st.detail)
    $o += "    do ($who): $act"
  }
  return $o
}

function Get-Machine($F, $V, [string]$H, [bool]$Redact) {
  $name = 'old-pc'
  if (-not $Redact) { $name = Get-SafeName $F.Hostname }
  $caps = @()
  if (Test-Sdk8 $F.DotnetSdks) { $caps += 'dotnet' }
  if ($F.Tools) { foreach ($p in @(@('node', 'node'), @('python', 'python'), @('java', 'java'), @('go', 'go'), @('rustc', 'rust'))) { if ($F.Tools[$p[0]]) { $caps += $p[1] } } }
  if ($F.DockerServerOk) { $caps += 'docker' }
  if ($F.GitVer) { $caps += 'git' }
  $caps += 'windows'
  $rg = [math]::Round([double]$F.RamGB); $cpu = [int]$F.Logical
  $jobs = Get-SuggestedJobs $rg $cpu
  $tr = 'none'
  if ($F.GitVer) { $tr = 'git' } elseif ($F.TarBsd) { $tr = 'archive' }
  $root = 'C:/lq'
  if ($F.WorkDrive) { $root = "$($F.WorkDrive)/lq" }
  $user = '<user>'
  if (-not $Redact -and (Test-SshUser $F.User)) { $user = $F.User }
  $ssh = "$user@$name.lan"
  if ($Redact) { $ssh = '<user>@<hostname>.lan' }
  $m = [ordered]@{ name = $name; os = 'windows'; ssh = $ssh; work_root = $root; capabilities = $caps; max_jobs = $jobs; min_free_mem_gb = (Get-SuggestedMinMem $rg); transfer = $tr }
  if ($H) { $m['allowed_hours'] = $H }
  if ($F.WolMac -and (Get-WolCheck $F).status -eq 'OK') { $m['wake'] = [ordered]@{ mac = $F.WolMac } }
  return $m
}

function Format-Snippet($M, $V, $F) {
  $o = @()
  $ready = ($V.worker_git -ne 'FAIL') -or ($V.worker_archive -ne 'FAIL')
  if (-not $ready) { $o += '# NOT READY yet: fix the MUST FIX steps first, then re-run.' }
  $o += 'machines:'
  $o += "  - name: `"$($M.name)`""
  $o += "    os: $($M.os)"
  $o += "    ssh: `"$($M.ssh)`"   # placeholder: confirm user and host name"
  $o += "    work_root: `"$($M.work_root)`"   # short path"
  $o += "    capabilities: [$(@($M.capabilities) -join ', ')]"
  $o += "    max_jobs: $($M.max_jobs)   # $($F.RamGB) GB RAM, $($F.Logical) CPUs"
  $o += "    min_free_mem_gb: $($M.min_free_mem_gb)   # LanQueue starts no new job while less memory than this is free"
  if ($M.transfer -eq 'none') { $o += '    # transfer: needs git (transfer: git) or built-in tar (transfer: archive); neither found' }
  else { $o += "    transfer: $($M.transfer)" }
  if ($M.Contains('allowed_hours')) { $o += "    allowed_hours: `"$($M.allowed_hours)`"   # the PC's own clock" }
  else { $o += '    # allowed_hours: "22:00-07:00"   # optional: run jobs only overnight (the PC''s own clock)' }
  if (-not $F.WolMac) { $o += '    # wake: not suggested: no wired Ethernet adapter found' }
  elseif ($M.Contains('wake') -and $M.wake.mac -eq '<mac>') {
    $o += '    # wake:   # Wake-on-LAN looks ready; <mac> is a placeholder (redacted): put this PC''s wired MAC here and uncomment'
    $o += '    #   mac: "<mac>"'
  } elseif ($M.Contains('wake')) {
    $o += "    wake:   # Wake-on-LAN (LanQueue 0.5+); test it: lanq wake $($M.name) --wait"
    $o += "      mac: `"$($M.wake.mac)`""
    $o += '      # broadcast: 10.20.30.255   # optional: your subnet''s directed broadcast on a PC with several networks or a VPN'
  } else {
    $o += '    # wake:   # not ready yet (see the Wake-on-LAN step); uncomment once it is fixed'
    $o += "    #   mac: `"$($F.WolMac)`""
  }
  if ([double]$F.RamGB -lt 6) {
    $o += '    # Low-RAM PC: keep max_jobs: 1. Optional caps for build tools (job environment only):'
    $o += '    # env:'
    $o += '    #   DOTNET_GCHeapHardLimitPercent: "0x32"     # .NET GC heap at most 50% of RAM (hexadecimal)'
    $o += '    #   NODE_OPTIONS: "--max-old-space-size=1024"  # Node heap at most 1 GB'
    $o += '    # For .NET jobs on a small box, add -m:1 to dotnet build / dotnet test commands (one MSBuild process).'
  }
  return $o
}

# Redaction happens on VALUES, before anything is serialized or printed. Only free-text facts are
# touched, never fields that drive logic or the JSON structure.
function Protect-Facts($F) {
  $toks = @()
  foreach ($v in @($F.Profile, $F.User, $F.Hostname)) { if ($v -and "$v".Length -ge 2) { $toks += "$v" } }
  $fix = { param($s) foreach ($t in $toks) { $s = [regex]::Replace("$s", '(?<![A-Za-z0-9_])' + [regex]::Escape($t) + '(?![A-Za-z0-9_])', '<redacted>', 'IgnoreCase') }; $s }
  $o = @{}; foreach ($k in $F.Keys) { $o[$k] = $F[$k] }
  foreach ($k in @('Caption', 'CpuName', 'GitVer', 'DockerServer', 'DefaultShell', 'WolNic')) { if ($o[$k]) { $o[$k] = & $fix $o[$k] } }
  foreach ($k in @('StartupNames', 'ProblemDevices')) { if ($o[$k]) { $o[$k] = @($o[$k] | ForEach-Object { & $fix $_ }) } }
  if ($o.Tools) { $t2 = @{}; foreach ($k in $o.Tools.Keys) { $t2[$k] = & $fix $o.Tools[$k] }; $o.Tools = $t2 }
  $o.Hostname = '<redacted>'; $o.User = '<redacted>'; $o.Profile = '<redacted>'
  if ($o.WolMac) { $o.WolMac = '<mac>' }
  return $o
}

function New-Report($F, $Checks, $V, $M, [string]$Version, $S = $null) {
  $tier = Get-SuitedTier ([math]::Round([double]$F.RamGB)) ([int]$F.Logical)
  $r = [ordered]@{
    schema = 'lanq-makeready/v1'; tool = [ordered]@{ name = 'lanq-makeready'; version = $Version }
    generatedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    host = [ordered]@{ hostname = $F.Hostname; user = $F.User; os = "$($F.Caption) build $($F.Build)"; arch = $F.Arch }
    verdicts = [ordered]@{ worker_git = $V.worker_git; worker_archive = $V.worker_archive; dotnet10 = $V.dotnet10; docker = $V.docker; overnight = $V.overnight }
    summary = $S
    suitedFor = [ordered]@{ tier = $tier; containers = [bool]$F.DockerServerOk; text = (Get-SuitedText $tier ([bool]$F.DockerServerOk)) }
    checks = @($Checks | ForEach-Object { [ordered]@{ id = $_.id; title = $_.title; status = $_.status; detail = $_.detail; advice = $_.advice; needsAdmin = $_.needsAdmin } })
    capabilities = @($M.capabilities | Where-Object { $_ -ne 'windows' })
    suggestedMachine = $M
  }
  if (-not $S) { $r.Remove('summary') }
  return $r
}

# JSON as pure ASCII (non-ASCII becomes \uXXXX) so console encodings cannot corrupt it.
function ConvertTo-AsciiJson($Obj) {
  $t = $Obj | ConvertTo-Json -Depth 6
  return [regex]::Replace($t, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
}

# ---------- probes (read-only) ----------

# Environment for every probe: no toolchain auto-download, no prompts, no hints.
function Get-ProbeEnv {
  @{ GOTOOLCHAIN = 'local'; RUSTUP_AUTO_INSTALL = '0'; GIT_TERMINAL_PROMPT = '0'; DOCKER_CLI_HINTS = 'false'
     NPM_CONFIG_UPDATE_NOTIFIER = 'false'; PIP_DISABLE_PIP_VERSION_CHECK = '1' }
}

function Stop-Tree([int]$ProcId) {
  [void](Invoke-Bounded "$env:SystemRoot\System32\taskkill.exe" "/T /F /PID $ProcId" 5 -NoKill)
}

# Runs one external program with closed stdin, a neutral working directory, and a hard deadline
# (the whole process tree is stopped on timeout). Returns $null if it cannot start.
function Invoke-Bounded {
  param([string]$Exe, [string]$ArgText = '', [int]$Sec = 8, [switch]$Utf16, [switch]$NoKill)
  try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe; $psi.Arguments = $ArgText; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $env:SystemRoot
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $pe = Get-ProbeEnv; foreach ($k in $pe.Keys) { $psi.EnvironmentVariables[$k] = $pe[$k] }
    if ($Utf16) { $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode }
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($Sec * 1000)) {
      if (-not $NoKill) { Stop-Tree $p.Id }
      try { $p.Kill() } catch { }
      return @{ Ok = $false; TimedOut = $true; Code = -1; Out = '' }
    }
    [void]$o.Wait(2000); [void]$e.Wait(2000)
    $so = ''; if ($o.IsCompleted) { $so = $o.Result }
    $se = ''; if ($e.IsCompleted) { $se = $e.Result }
    return @{ Ok = ($p.ExitCode -eq 0); TimedOut = $false; Code = $p.ExitCode; Out = ($so + "`n" + $se) }
  } catch { return $null }
}

# Runs a cmdlet block in a separate runspace with a deadline; $null on timeout or error.
function Invoke-WithTimeout([scriptblock]$Block, $Arg, [int]$Sec = 15) {
  $ps = $null
  try {
    $ps = [System.Management.Automation.PowerShell]::Create()
    [void]$ps.AddScript($Block.ToString()).AddArgument($Arg)
    $h = $ps.BeginInvoke()
    if ($h.AsyncWaitHandle.WaitOne($Sec * 1000)) { $r = $ps.EndInvoke($h); $ps.Dispose(); return , @($r) }
    [void]$ps.BeginStop($null, $null); $script:Hung = $true
    return $null
  } catch { return $null }
}

function Find-Exe([string]$Name) {
  $g = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($g -and $g.Source -notmatch '\\WindowsApps\\') { return $g.Source }
  return $null
}

function Get-FirstLine([string]$T) {
  foreach ($l in ($T -split "`r?`n")) { if ($l.Trim()) { return $l.Trim() } }
  return ''
}

function Get-DirSizeMB([string]$Path, [int]$Sec = 5) {
  if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
  $sw = [System.Diagnostics.Stopwatch]::StartNew(); $total = [long]0; $capped = $false
  $stack = New-Object 'System.Collections.Generic.Stack[string]'; $stack.Push($Path)
  while ($stack.Count -gt 0 -and -not $capped) {
    if ($sw.Elapsed.TotalSeconds -gt $Sec) { $capped = $true; break }
    $d = $stack.Pop()
    try {
      foreach ($f in [System.IO.Directory]::EnumerateFiles($d)) {
        if ($sw.Elapsed.TotalSeconds -gt $Sec) { $capped = $true; break }
        try { $total += (New-Object System.IO.FileInfo $f).Length } catch { }
      }
      foreach ($s in [System.IO.Directory]::EnumerateDirectories($d)) {
        if ($sw.Elapsed.TotalSeconds -gt $Sec) { $capped = $true; break }
        $a = (New-Object System.IO.DirectoryInfo $s).Attributes
        if (-not ($a -band [System.IO.FileAttributes]::ReparsePoint)) { $stack.Push($s) }
      }
    } catch { }
  }
  return @{ MB = [int][math]::Round($total / 1MB); Capped = $capped }
}

function Get-IsAdminUser {
  try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $g = @($id.Groups | ForEach-Object { $_.Value })
    $d = @($id.Claims | Where-Object { $_.Type -like '*denyonlysid' } | ForEach-Object { $_.Value })
    return (Test-AdminSid $g $d)
  } catch { return $false }
}

# Context name from the environment, else `docker context show` (local; prints a name, never credentials).
function Get-DockerContext([string]$DockerExe) {
  if ($env:DOCKER_CONTEXT) { return "$env:DOCKER_CONTEXT" }
  if (-not $DockerExe) { return '' }
  $r = Invoke-Bounded $DockerExe 'context show' 5
  if ($r -and $r.Ok) { return (Get-FirstLine $r.Out) }
  return ''
}

function Get-Facts([string]$H) {
  $F = @{ Hours = $H; Hostname = $env:COMPUTERNAME; User = $env:USERNAME; Profile = $env:USERPROFILE }
  try { $os = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 15
    $F.Caption = ($os.Caption -replace '^Microsoft\s+', ''); $F.Build = [int]$os.BuildNumber; $F.IsServer = ($os.ProductType -ne 1)
    $F.RamGB = [math]::Round($os.TotalVisibleMemorySize / 1MB + 0.5, 1); $F.FreeRamGB = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
  } catch { $F.Caption = 'Windows (unreadable)'; $F.Build = 0; $F.RamGB = 0; $F.FreeRamGB = 0; $F.IsServer = $false }
  # installed RAM, not the usable amount (usable is a little lower on every PC)
  try { $cap = (@(Get-CimInstance Win32_PhysicalMemory -OperationTimeoutSec 15) | Measure-Object Capacity -Sum).Sum
    if ($cap -gt 0 -and [math]::Round($cap / 1GB, 1) -gt $F.RamGB) { $F.RamGB = [math]::Round($cap / 1GB, 1) } } catch { }
  try { $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop; $F.EditionId = $cv.EditionID } catch { $F.EditionId = '' }
  $F.Is64 = [Environment]::Is64BitOperatingSystem
  $F.IsArm = ("$env:PROCESSOR_ARCHITECTURE $env:PROCESSOR_ARCHITEW6432" -match 'ARM64')
  if ($F.IsArm) { $F.Arch = 'arm64' } elseif ($F.Is64) { $F.Arch = 'x64' } else { $F.Arch = 'x86' }
  try { $cpu = @(Get-CimInstance Win32_Processor -OperationTimeoutSec 15)
    $F.CpuName = ($cpu[0].Name -replace '\s+', ' ').Trim(); $F.Cores = ($cpu | Measure-Object NumberOfCores -Sum).Sum; $F.Logical = ($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum
    $F.VirtFirmware = $cpu[0].VirtualizationFirmwareEnabled
  } catch { $F.CpuName = 'unknown'; $F.Cores = 0; $F.Logical = 0 }
  try { $F.Avx2 = [string][System.Runtime.Intrinsics.X86.Avx2]::IsSupported } catch { $F.Avx2 = 'not queried on Windows PowerShell 5.1' }
  try { $F.HypervisorPresent = [bool](Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 15).HypervisorPresent } catch { }
  # disks: media type (SSD/HDD) per drive letter, then pick the work drive
  $media = @{}
  $mm = Invoke-WithTimeout { param($x)
    $pd = @(Get-PhysicalDisk -ErrorAction Stop)
    Get-Partition -ErrorAction Stop | Where-Object { $_.DriveLetter } | ForEach-Object { $n = "$($_.DiskNumber)"; $d = $pd | Where-Object { $_.DeviceId -eq $n } | Select-Object -First 1
      "$($_.DriveLetter)=$($d.MediaType)" } } $null 20
  foreach ($line in @($mm)) { if ("$line" -match '^([A-Za-z])=(SSD|HDD)$') { $media[$Matches[1].ToUpper()] = $Matches[2] } }
  $drives = @()
  try { foreach ($d in @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -OperationTimeoutSec 15)) {
      $m = 'Unknown'; $l = $d.DeviceID.Substring(0, 1).ToUpper(); if ($media.ContainsKey($l)) { $m = $media[$l] }
      $drives += @{ Letter = $d.DeviceID; FreeGB = [math]::Round($d.FreeSpace / 1GB, 1); Media = $m } } } catch { }
  $sel = Select-WorkDrive $drives $env:SystemDrive
  $F.WorkDrive = $sel.Letter; $F.WorkFreeGB = $sel.FreeGB; $F.DiskMedia = $sel.Media
  $sys = @($drives | Where-Object { $_.Letter -eq $env:SystemDrive }); $F.SysFreeGB = 0; if ($sys.Count -gt 0) { $F.SysFreeGB = $sys[0].FreeGB }
  # ssh
  $svc = Get-Service sshd -ErrorAction SilentlyContinue
  $F.SshdInstalled = [bool]($svc -or (Test-Path "$env:WINDIR\System32\OpenSSH\sshd.exe"))
  if ($svc) { $F.SshdRunning = ($svc.Status -eq 'Running'); $F.SshdStartType = "$($svc.StartType)" }
  $fw = Invoke-WithTimeout { param($x)
    $r = @(Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)
    if ($r.Count -eq 0) { 'missing' } elseif (@($r | Where-Object { "$($_.Enabled)" -eq 'True' }).Count -gt 0) { 'enabled' } else { 'disabled' } } $null 15
  $F.SshFwRule = $null; if ($null -ne $fw -and @($fw).Count -gt 0) { $F.SshFwRule = "$(@($fw)[0])" }
  if ($F.SshFwRule -eq 'missing') {
    $fp = Invoke-WithTimeout { param($x)
      try { $r = @(Get-NetFirewallPortFilter -Protocol TCP -ErrorAction Stop | Where-Object { @($_.LocalPort) -contains '22' } | Get-NetFirewallRule -ErrorAction Stop |
          Where-Object { "$($_.Enabled)" -eq 'True' -and "$($_.Direction)" -eq 'Inbound' -and "$($_.Action)" -eq 'Allow' })
        "$($r.Count -gt 0)" } catch { 'denied' } } $null 20
    $F.SshFwPort = $null
    if ($null -ne $fp -and @($fp).Count -gt 0) { $v = "$(@($fp)[0])"; if ($v -eq 'denied') { $F.SshFwPort = 'denied' } else { $F.SshFwPort = ($v -eq 'True') } }
  }
  try { $F.DefaultShell = (Get-ItemProperty 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -ErrorAction Stop).DefaultShell } catch { }
  $F.IsAdminUser = Get-IsAdminUser
  $ak = Join-Path $env:USERPROFILE '.ssh\authorized_keys'
  $F.AuthKeysNonEmpty = [bool]((Test-Path -LiteralPath $ak) -and ((Get-Item -LiteralPath $ak).Length -gt 0))
  $adm = "$env:ProgramData\ssh\administrators_authorized_keys"
  $F.AdminKeysExists = (Test-Path -LiteralPath $adm)
  if ($F.AdminKeysExists) { try { $ok = $true
      foreach ($ace in (Get-Acl -LiteralPath $adm).Access) { $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if (@('S-1-5-32-544', 'S-1-5-18') -notcontains $sid) { $ok = $false } }
      $F.AdminKeysAclOk = $ok } catch { $F.AdminKeysAclOk = $null } }
  # git, tar, long paths
  $git = Find-Exe 'git'
  if ($git) { $r = Invoke-Bounded $git '--version' 8; if ($r -and $r.Ok) { $F.GitVer = Get-FirstLine $r.Out }
    $r = Invoke-Bounded $git 'config --get core.longpaths' 8; $F.GitLongPaths = [bool]($r -and $r.Out -match '^\s*true') }
  $tar = "$env:SystemRoot\System32\tar.exe"
  if (Test-Path -LiteralPath $tar) { $r = Invoke-Bounded $tar '--version' 8; $F.TarBsd = [bool]($r -and $r.Out -match 'bsdtar') } else { $F.TarBsd = $false }
  try { $F.LongPaths = ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop).LongPathsEnabled -eq 1) } catch { $F.LongPaths = $false }
  # dotnet SDK folders (no process is started)
  $roots = @("$env:ProgramFiles\dotnet", "${env:ProgramFiles(x86)}\dotnet", $env:DOTNET_ROOT, "$env:USERPROFILE\.dotnet")
  $majors = @()
  foreach ($rt in $roots) { if ($rt -and (Test-Path -LiteralPath "$rt\sdk")) { foreach ($dir in (Get-ChildItem -LiteralPath "$rt\sdk" -Directory -ErrorAction SilentlyContinue)) { if ($dir.Name -match '^(\d+)\.') { $majors += [int]$Matches[1] } } } }
  $F.DotnetSdks = @($majors | Sort-Object -Unique)
  # docker (a remote endpoint is never probed), wsl (never run unless installed)
  $dk = Find-Exe 'docker'; $F.DockerCli = [bool]$dk; $F.DockerServerOk = $false
  $F.DockerRemote = Test-DockerRemote $env:DOCKER_HOST (Get-DockerContext $dk)
  if ($dk -and -not $F.DockerRemote) { $r = Invoke-Bounded $dk "version --format {{.Server.Version}}" 10; if ($r -and $r.Ok -and (Get-FirstLine $r.Out)) { $F.DockerServerOk = $true; $F.DockerServer = Get-FirstLine $r.Out } }
  $wsl = "$env:SystemRoot\System32\wsl.exe"
  $lx = [bool]((Get-Service LxssManager -ErrorAction SilentlyContinue) -or (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'))
  $store = $null
  if (-not $lx) { $sp = Invoke-WithTimeout { param($x) @(Get-AppxPackage -Name MicrosoftCorporationII.WindowsSubsystemForLinux -ErrorAction Stop | Select-Object -ExpandProperty Name) } $null 20
    if ($null -ne $sp) { $store = (@($sp[0]).Count -gt 0) } }
  $F.WslOk = Get-WslState (Get-WslInstalled $lx $store) { Invoke-Bounded $wsl '--status' 8 -Utf16 }
  # other tools
  $F.Tools = @{}
  foreach ($t in @(@('node', '--version'), @('python', '--version'), @('java', '-version'), @('go', 'version'), @('rustc', '--version'))) {
    $e = Find-Exe $t[0]; if ($e) { $r = Invoke-Bounded $e $t[1] 8; if ($r -and $r.Ok) { $F.Tools[$t[0]] = Get-FirstLine $r.Out } } }
  # power
  $pc = Find-Exe 'powercfg'
  if ($pc) { $r = Invoke-Bounded $pc '/q SCHEME_CURRENT SUB_SLEEP STANDBYIDLE' 8; if ($r) { $F.StandbyAcSec = Get-PowercfgAcSeconds $r.Out }
    $r = Invoke-Bounded $pc '/q SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE' 8; if ($r) { $F.HibernateAcSec = Get-PowercfgAcSeconds $r.Out } }
  $F.RebootPending = [bool]((Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
    (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or
    ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue).PendingFileRenameOperations))
  try { $ah = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -ErrorAction Stop
    if ($null -ne $ah.ActiveHoursStart) { $F.ActiveStart = [int]$ah.ActiveHoursStart; $F.ActiveEnd = [int]$ah.ActiveHoursEnd } } catch { }
  # Wake-on-LAN: the wired adapter and its MAC, its magic-packet setting, whether it may wake the PC, Fast Startup
  $na = Invoke-WithTimeout { param($x) @(Get-NetAdapter -Physical -ErrorAction Stop | ForEach-Object { "$($_.Name)|$($_.InterfaceDescription)|$($_.MacAddress)|$($_.PhysicalMediaType)|$($_.Status)" }) } $null 15
  $F.WolProbe = ($null -ne $na); $sel = Select-WiredNic $na; $F.WolWifi = $sel.Wifi
  if ($sel.Nic) {
    $F.WolNic = $sel.Nic.Name; $F.WolMac = $sel.Nic.Mac
    $pm = Invoke-WithTimeout { param($n) @(Get-NetAdapterPowerManagement -Name $n -ErrorAction Stop | ForEach-Object { "$($_.WakeOnMagicPacket)" }) } $sel.Nic.Name 15
    if ($null -ne $pm -and @($pm).Count -gt 0) { $F.WolMagic = "$(@($pm)[0])" }
    if ($pc) { $r = Invoke-Bounded $pc '/devicequery wake_armed' 8; if ($r -and $r.Ok) { $F.WolArmed = Test-WakeArmed $r.Out $sel.Nic.Desc } }
  }
  $hb = $null; $he = $null; $hd = $null
  try { $hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -ErrorAction Stop).HiberbootEnabled } catch { }
  try { $cp = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -ErrorAction Stop; $he = $cp.HibernateEnabled; $hd = $cp.HibernateEnabledDefault } catch { }
  $F.FastStartup = Test-FastStartup $hb $he $hd
  try { $F.SwapMB = [int](@(Get-CimInstance Win32_PageFileUsage -OperationTimeoutSec 15) | Measure-Object AllocatedBaseSize -Sum).Sum } catch { $F.SwapMB = $null }
  # slowdowns
  try { $su = @(Get-CimInstance Win32_StartupCommand -OperationTimeoutSec 15); $F.StartupCount = $su.Count; $F.StartupNames = @($su | Select-Object -First 5 | ForEach-Object { $_.Name }) } catch { }
  $F.TempSizes = @()
  foreach ($p in @(@('user temp', $env:TEMP), @('Windows temp', "$env:WINDIR\Temp"), @('NuGet packages', "$env:USERPROFILE\.nuget\packages"), @('npm cache', "$env:LOCALAPPDATA\npm-cache"))) {
    $z = Get-DirSizeMB $p[1] 3; if ($z) { $F.TempSizes += @{ Name = $p[0]; MB = $z.MB; Capped = $z.Capped } } }
  $df = Invoke-WithTimeout { param($x) (Get-MpComputerStatus -ErrorAction Stop).RealTimeProtectionEnabled } $null 15
  $F.DefenderRtp = Get-ProbeBool $df
  $F.DevDrive = ($F.Build -ge 22631)
  try { $F.ProblemDevices = @(Get-CimInstance Win32_PnPEntity -OperationTimeoutSec 20 | Where-Object { $_.ConfigManagerErrorCode -ne 0 } | ForEach-Object { $_.Name }) } catch { $F.ProblemDevices = $null }
  return $F
}

# ---------- main ----------

if ($env:LANQ_MAKEREADY_LIB -eq '1') {
  if ($MyInvocation.InvocationName -eq '.') { return }
  [Console]::Error.WriteLine('lanq-makeready: LANQ_MAKEREADY_LIB=1 is only for tests that dot-source this file; unset it.')
  exit 2
}
if ($Extra -and @($Extra).Count -gt 0) {
  [Console]::Error.WriteLine("lanq-makeready: unknown argument(s): $(@($Extra) -join ' ')")
  [Console]::Error.WriteLine('usage: lanq-makeready.ps1 [-Json <path|->] [-Out <new file>] [-Redact] [-Hours HH:mm-HH:mm] [-Quiet]   (PowerShell-style flags; the sh script uses --json/--redact)')
  exit 2
}
if ($Hours -and -not (Test-Hours $Hours)) {
  [Console]::Error.WriteLine('lanq-makeready: -Hours must look like 00:00-07:00 (HH:mm-HH:mm, start different from end).')
  exit 2
}
$OutFull = ''
if ($PSBoundParameters.ContainsKey('Out')) {
  if (-not $Out) { [Console]::Error.WriteLine('lanq-makeready: -Out needs a file name.'); exit 2 }
  $OutFull = $Out; if (-not [System.IO.Path]::IsPathRooted($OutFull)) { $OutFull = Join-Path (Get-Location).ProviderPath $Out }
  if (Test-Path -LiteralPath $OutFull) { [Console]::Error.WriteLine("lanq-makeready: -Out $Out already exists; it is never overwritten. Choose a new file name."); exit 2 }
}
$StdoutJson = ($Json -eq '-')
if ($StdoutJson) { $WarningPreference = 'SilentlyContinue'; $ProgressPreference = 'SilentlyContinue'; $InformationPreference = 'SilentlyContinue'; $VerbosePreference = 'SilentlyContinue' }
$rank = @{ OK = 'OK  '; WARN = 'WARN'; FAIL = 'FAIL'; SKIP = 'SKIP' }
$color = @{ OK = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; SKIP = 'DarkGray' }
$facts = Get-Facts $Hours
if ($Redact) { $facts = Protect-Facts $facts }
$checks = Get-Checks $facts
$verdicts = Get-Verdicts $checks $facts
$machine = Get-Machine $facts $verdicts $Hours ([bool]$Redact)
$summary = Get-Summary $checks $verdicts $facts
$Lines = New-Object System.Collections.ArrayList

# Every human line goes to the screen (unless -Json -) and to the -Out file.
function Write-Out([string]$Text, [string]$Color = 'Gray') {
  [void]$Lines.Add($Text)
  if ($StdoutJson) { return }
  Write-Host $Text -ForegroundColor $Color
}

# The only write besides the -Json report: a NEW file (CreateNew fails if it exists, so nothing is ever overwritten).
function Save-OutFile([string]$Path, [string]$Text) {
  $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
  try { $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($Text); $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
}

$vcolor = @{ READY = 'Green'; READY_WITH_WARNINGS = 'Yellow'; NOT_READY = 'Red' }[$summary.verdict]
Write-Out "lanq-makeready $ToolVersion (read-only: nothing is installed, changed or sent anywhere)"
Write-Out "VERDICT: $($summary.text)" $vcolor
if (-not $Quiet) {
  Write-Out ''
  foreach ($c in $checks) {
    Write-Out ("[{0}] {1}: {2}" -f $rank[$c.status], $c.title, $c.detail) $color[$c.status]
    if ($c.status -eq 'SKIP' -and $c.needsAdmin) { Write-Out '       skipped (needs admin); re-run elevated to fill this in' 'DarkGray' }
  }
}
Write-Out ''
Write-Out 'Verdicts'
foreach ($k in $verdicts.Keys) { Write-Out ("  {0,-15} {1}" -f $k, $verdicts[$k]) $color[$verdicts[$k]] }
Write-Out "Suited for: $(Get-SuitedText (Get-SuitedTier ([math]::Round([double]$facts.RamGB)) ([int]$facts.Logical)) ([bool]$facts.DockerServerOk))"
$ready = ($verdicts.worker_git -ne 'FAIL') -or ($verdicts.worker_archive -ne 'FAIL')
if (-not $Quiet) {
  if (@($summary.nextSteps).Count -gt 0) {
    Write-Out ''
    Write-Out 'Next steps (blockers first; nothing here is run for you):'
    foreach ($l in (Format-Steps $summary)) { Write-Out $l }
  }
  Write-Out ''
  Write-Out '# Paste into machines.yml (confirm the placeholders):'
  foreach ($l in (Format-Snippet $machine $verdicts $facts)) { Write-Out $l 'Cyan' }
  Write-Out ''
  Write-Out "VERDICT: $($summary.text)" $vcolor
}

if ($Json) {
  try {
    $text = ConvertTo-AsciiJson (New-Report $facts $checks $verdicts $machine $ToolVersion $summary)
    if ($StdoutJson) { [Console]::Out.Write($text + "`n") }
    else {
      $full = $Json
      if (-not [System.IO.Path]::IsPathRooted($full)) { $full = Join-Path (Get-Location).ProviderPath $Json }
      [System.IO.File]::WriteAllText($full, $text, (New-Object System.Text.UTF8Encoding($false)))
      if (-not $Quiet) { Write-Out 'JSON report written to the path you gave.' }
    }
  } catch { [Console]::Error.WriteLine("lanq-makeready: could not write the JSON report: $($_.Exception.Message)") }
}
if ($OutFull) {
  try { Save-OutFile $OutFull (($Lines -join "`r`n") + "`r`n") }
  catch { [Console]::Error.WriteLine("lanq-makeready: could not create the -Out file (it exists now, or the folder is not writable): $($_.Exception.Message)") }
}
$code = 1; if ($ready) { $code = 0 }
if ($script:Hung) { [Console]::Out.Flush(); [Environment]::Exit($code) }
exit $code
