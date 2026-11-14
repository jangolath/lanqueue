<#
.SYNOPSIS
  lanq-makeready-tune: READ-ONLY tune scan. What slows this PC down as a build worker, and what keeps it busy at night.
.DESCRIPTION
  Changes nothing, installs nothing, makes no network calls, needs no admin rights (a few checks say more when run
  elevated). Statuses are OK, INFO, WARN and SKIP; nothing here changes whether the PC is worker-ready (that is the
  scanner, lanq-makeready.ps1), and the exit code is always 0 (2 for bad arguments). Items with a fix id can be changed
  at the PC with: lanq-makeready-fix.ps1 -Tune -WhatIf (then without -WhatIf, confirming each one).
  The scan stops probing after about 90 seconds; a probe over its time budget reports SKIP "time budget".
.PARAMETER Json
  Also write a JSON report (schema lanq-makeready-tune/v1) to this path. '-' writes ONLY the JSON to stdout.
.PARAMETER Redact
  Replace hostname, user name and user-profile paths with <redacted>.
.PARAMETER Hours
  "HH:mm-HH:mm" job window: scheduled scans and maintenance inside it are reported.
.PARAMETER WorkRoot
  LanQueue's work_root for this PC, like C:/lq (drive path, forward slashes, no spaces, no '..').
#>
[CmdletBinding(PositionalBinding = $false)]
param([string]$Json, [switch]$Redact, [string]$Hours, [string]$WorkRoot, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Extra)

$ToolVersion = '0.5.0'
$TotalSec = 90
$Clock = [System.Diagnostics.Stopwatch]::StartNew()
$OverBudget = @{}
$script:Hung = $false

# Curated OEM updater / assistant / telemetry names (services, scheduled tasks, Run entries). Generic across makers,
# changed only by reviewed pull requests. The same list is in lanq-makeready-fix.ps1 (a test keeps them identical).
# Every entry is a case-insensitive regex with a literal name in it: no bare wildcards.
$VendorPatterns = @(
  '^Dell ?SupportAssist', '^SupportAssist', '^Dell ?(Update|Command ?Update|Digital ?Delivery|Data ?Vault|TechHub|Optimizer|Core ?Services)', '^DDV(DataCollector|RulesProcessor|CollectorSvcApi)$',
  '^HP ?(Support ?Assistant|Support ?Solutions|Analytics|App ?Helper|Diagnostics|Insights|Touchpoint|System ?Info|Network ?Cap|Comm ?Recovery|JumpStart)', '^HP(AppHelperCap|DiagsCap|NetworkCap|SysInfoCap)$', '^HpTouchpointAnalyticsService$',
  '^Lenovo ?(Vantage|Service ?Bridge|Smart ?Standby|Utility|Welcome|System ?Update)', '^ImControllerService$', '^LITSSVC$',
  '^ASUS ?(Optimization|Software ?Manager|System ?Analysis|System ?Diagnosis|Link|Update|App ?Service)', '^ArmouryCrate', '^AsusUpdateCheck$',
  '^Acer ?(Quick ?Access|Care ?Center|Jumpstart|Collection|Product ?Registration)', '^MSI ?(Center|Dragon ?Center)', '^Samsung ?(Update|Settings)',
  '^Intel\(R\) ?(Driver ?(&|and) ?Support|Computing ?Improvement)', '^DSA(Update)?Service$', '^NvTelemetryContainer$', '^NVIDIA ?(GeForce ?Experience|Telemetry)'
)

# ---------- pure helpers (no I/O): tests call these with fixture facts ----------

function Test-Hours([string]$H) {
  if ($H -notmatch '^([01]\d|2[0-3]):([0-5]\d)-([01]\d|2[0-3]):([0-5]\d)$') { return $false }
  $p = $H.Split('-'); return ($p[0] -ne $p[1])
}
# LanQueue's Windows work_root rule (ConfigLoader): drive path, forward slashes, [A-Za-z0-9_./-], no '..'.
# A valid work_root (LanQueue's rule: a drive path with forward slashes, no '.' or '..' part, not a whole drive).
function Test-WorkRoot([string]$W) { return ($W -cmatch '^[A-Za-z]:/[A-Za-z0-9_.-][A-Za-z0-9_./-]*$' -and $W -notmatch '\.\.' -and $W -notmatch '(^|/)\.(/|$)') }
# A name from the PC (Run entry, service, task, driver, product) made safe to print: control and bidi characters
# removed (any user process can write a Run entry), at most 80 characters.
function Get-CleanName([string]$S) {
  $c = [regex]::Replace("$S", '[\p{Cc}\p{Cf}\p{Cs}\u2028\u2029]', '')
  if ($c.Length -gt 80) { $c = $c.Substring(0, 77) + '...' }
  return $c
}
function Get-HoursMin([string]$T) { $p = $T.Split(':'); return ([int]$p[0] * 60 + [int]$p[1]) }
# True when minute-of-day $M falls inside the window "HH:mm-HH:mm" (the end is exclusive; windows may wrap midnight).
function Test-InWindow([int]$M, [string]$H) {
  $w = $H.Split('-'); $s = Get-HoursMin $w[0]; $e = Get-HoursMin $w[1]
  if ($s -lt $e) { return ($M -ge $s -and $M -lt $e) }
  return ($M -ge $s -or $M -lt $e)
}
function Format-Min([int]$M) { return ('{0:00}:{1:00}' -f [math]::Floor($M / 60), ($M % 60)) }
function Test-Vendor([string]$Name) { foreach ($p in $VendorPatterns) { if ($Name -match $p) { return $true } }; return $false }
# Seconds or a percentage from `powercfg /q` (the AC index); $null when not found. Localized output too: the AC value
# is the second-to-last 0x........ value.
function Get-PowercfgAc([string]$Text) {
  if ($Text -match 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)') { return [Convert]::ToInt64($Matches[1], 16) }
  $hex = @([regex]::Matches($Text, '0x([0-9a-fA-F]{8})\s*(?:\r?\n|$)'))
  if ($hex.Count -lt 2) { return $null }
  return [Convert]::ToInt64($hex[$hex.Count - 2].Groups[1].Value, 16)
}
# Windows Search crawl rules (URL, Include): the longest rule that matches a path decides whether it is indexed.
function Test-Indexed([string]$Path, $Rules) {
  $best = -1; $inc = $false; $p = $Path.Replace('/', '\')
  foreach ($r in @($Rules)) {
    $u = "$($r.Url)"; if ($u -notmatch '^file:///') { continue }
    $u = $u.Substring(8); $re = '^' + [regex]::Escape($u).Replace('\*', '.*')
    if ($p -match $re -or ($p + '\') -match $re) { if ($u.Length -gt $best) { $best = $u.Length; $inc = [bool]$r.Include } }
  }
  return $inc
}
# The first $N names, joined, with a count of the rest.
function Join-Names($Names, [int]$N = 6) {
  $a = @($Names | ForEach-Object { Get-CleanName $_ }); if ($a.Count -le $N) { return ($a -join ', ') }
  return ((@($a[0..($N - 1)]) -join ', ') + " and $($a.Count - $N) more")
}

$HostIds = @('power.plan', 'power.usbsuspend', 'startup.vendor', 'vendor.services', 'vendor.tasks', 'sysmain', 'search.index', 'do.mode', 'wu.reboot', 'visual.fx',
  'defender.paths', 'defender.schedule', 'av.other', 'pagefile', 'disk.health', 'drivers.age', 'autologon.plain', 'bios.power', 'maint.schedule')

function New-TuneItem([string]$Id, [string]$Title, [string]$Status, [string]$Detail, [string]$Advice = '', [bool]$Admin = $false, [string]$FixId = '', [bool]$Security = $false) {
  $fx = $null; if ($FixId -and @('INFO', 'WARN') -contains $Status) { $fx = $FixId }
  if (@('OK', 'SKIP') -contains $Status) { $Advice = '' }
  $Detail = [regex]::Replace($Detail, '[\p{Cc}\p{Cf}\p{Cs}\u2028\u2029]', ''); $Advice = [regex]::Replace($Advice, '[\p{Cc}\p{Cf}\p{Cs}\u2028\u2029]', '')
  if ($Detail.Length -gt 1024) { $Detail = $Detail.Substring(0, 1021) + '...' }; if ($Advice.Length -gt 1024) { $Advice = $Advice.Substring(0, 1021) + '...' }
  return [ordered]@{ id = $Id; title = $Title; status = $Status; detail = $Detail; advice = $Advice; needsAdmin = $Admin; fixId = $fx; security = $Security }
}

# Every check, from the facts alone. $F.X is $null when a probe could not read X; $F.Budget[probe] when it ran out of time.
function Get-TuneItems($F) {
  $it = New-Object System.Collections.ArrayList
  $add = { param($i) if ($F.Container -and $HostIds -contains $i.id) { $i.status = 'SKIP'; $i.detail = 'inside a container (a host setting)'; $i.advice = ''; $i.fixId = $null }; [void]$it.Add($i) }
  $why = { param($probe, $what) if ($F.Budget -and $F.Budget[$probe]) { 'time budget' } else { "could not read $what" } }

  $t = 'Processor power (AC)'
  if ($null -eq $F.CpuMax -or $null -eq $F.CpuMin) { & $add (New-TuneItem 'power.plan' $t 'SKIP' (& $why 'powercfg' 'the processor power settings (powercfg /q)')) }
  elseif ($F.CpuMax -lt 100) { & $add (New-TuneItem 'power.plan' $t 'WARN' "maximum processor state is $($F.CpuMax)% on AC: builds run slower" 'Power Options > Change plan settings > Change advanced power settings > Processor power management: Maximum 100%, Minimum 5%.' $true 'power.cpu') }
  elseif ($F.CpuMin -ge 100) { & $add (New-TuneItem 'power.plan' $t 'INFO' 'minimum processor state is 100% on AC: the CPU never clocks down (more heat and power, rarely faster builds)' 'Power Options > Processor power management: Minimum 5% (Maximum stays 100%).' $true 'power.cpu') }
  else { & $add (New-TuneItem 'power.plan' $t 'OK' "maximum $($F.CpuMax)%, minimum $($F.CpuMin)% on AC") }

  $t = 'USB selective suspend'
  if ($null -eq $F.UsbSuspend -or $null -eq $F.Nics) { & $add (New-TuneItem 'power.usbsuspend' $t 'SKIP' (& $why 'nics' 'the USB power setting or the network adapters')) }
  else {
    $usbNic = @($F.Nics | Where-Object { $_.Up -and $_.Usb })
    if ($F.UsbSuspend -eq 1 -and $usbNic.Count -gt 0) { & $add (New-TuneItem 'power.usbsuspend' $t 'WARN' "on, and the network adapter in use is on USB ($(Join-Names @($usbNic | ForEach-Object { $_.Name })))): it may be suspended and drop the SSH connection" 'Power Options > USB settings > USB selective suspend setting: Disabled (on AC).' $true 'power.usbsuspend') }
    elseif ($F.UsbSuspend -eq 1) { & $add (New-TuneItem 'power.usbsuspend' $t 'OK' 'on; no network adapter in use is on USB') }
    else { & $add (New-TuneItem 'power.usbsuspend' $t 'OK' 'off on AC') }
  }

  $t = 'Maker tools at startup'
  if ($null -eq $F.Startup) { & $add (New-TuneItem 'startup.vendor' $t 'SKIP' 'could not read the Run keys') }
  else {
    $v = @($F.Startup | Where-Object { $_.Enabled -and ((Test-Vendor $_.Name) -or (Test-Vendor $_.Exe)) } | ForEach-Object { $_.Name })
    if ($v.Count) { & $add (New-TuneItem 'startup.vendor' $t 'INFO' "$($v.Count) PC-maker updater or assistant entries start at logon: $(Join-Names $v)" 'Task Manager > Startup apps > Disable (it is not uninstalled; you can enable it again there).' $false 'startup.vendor') }
    else { & $add (New-TuneItem 'startup.vendor' $t 'OK' 'no known PC-maker updater or assistant starts at logon') }
  }

  $t = 'Maker services'
  if ($null -eq $F.Services) { & $add (New-TuneItem 'vendor.services' $t 'SKIP' (& $why 'services' 'the services')) }
  else {
    $v = @($F.Services | Where-Object { $_.Start -ne 'Disabled' -and ((Test-Vendor $_.Name) -or (Test-Vendor $_.Display)) } | ForEach-Object { $_.Name })
    if ($v.Count) { & $add (New-TuneItem 'vendor.services' $t 'INFO' "$($v.Count) PC-maker updater, assistant or telemetry services are not disabled: $(Join-Names $v)" 'They use CPU, disk and network in the background and may install drivers or restart the PC. To stop one: services.msc > the service > Startup type: Disabled. They also stop driver and BIOS update offers: check the maker site now and then.' $true 'vendor.services') }
    else { & $add (New-TuneItem 'vendor.services' $t 'OK' 'no known PC-maker service is enabled') }
  }

  $t = 'Maker scheduled tasks'
  if ($F.Tasks -eq 'denied') { & $add (New-TuneItem 'vendor.tasks' $t 'SKIP' 'the scheduled tasks could not be read (access denied)') }
  elseif ($F.Tasks -eq 'nomodule') { & $add (New-TuneItem 'vendor.tasks' $t 'SKIP' 'the ScheduledTasks module is not available on this PC') }
  elseif ($F.Tasks -eq 'error') { & $add (New-TuneItem 'vendor.tasks' $t 'SKIP' 'the scheduled tasks could not be read') }
  elseif ($null -eq $F.Tasks) { & $add (New-TuneItem 'vendor.tasks' $t 'SKIP' (& $why 'tasks' 'the scheduled tasks')) }
  else {
    $v = @($F.Tasks | Where-Object { $_.State -ne 'Disabled' -and $_.Path -notlike '\Microsoft\*' -and ((Test-Vendor $_.Name) -or (Test-Vendor ($_.Path.Trim('\')))) } | ForEach-Object { "$($_.Path)$($_.Name)" })
    if ($v.Count) { & $add (New-TuneItem 'vendor.tasks' $t 'INFO' "$($v.Count) PC-maker updater or assistant tasks are enabled: $(Join-Names $v)" 'Task Scheduler (taskschd.msc) > the task > Disable. Updaters often enable their tasks again when they update themselves; run this scan again later.' $true 'vendor.tasks') }
    else { & $add (New-TuneItem 'vendor.tasks' $t 'OK' 'no known PC-maker task is enabled (tasks under \Microsoft\ are never listed)') }
  }

  $t = 'SysMain (Superfetch)'
  if ($null -eq $F.Disks -or $null -eq $F.Services) { & $add (New-TuneItem 'sysmain' $t 'SKIP' (& $why 'disks' 'the disks or services')) }
  else {
    $sm = @($F.Services | Where-Object { $_.Name -eq 'SysMain' })
    $fixed = @($F.Disks | Where-Object { @('USB', 'File Backed Virtual', 'Virtual') -notcontains $_.Bus })
    $allSsd = ($fixed.Count -gt 0 -and @($fixed | Where-Object { $_.Media -ne 'SSD' }).Count -eq 0)
    if ($sm.Count -eq 0 -or $sm[0].Start -eq 'Disabled') { & $add (New-TuneItem 'sysmain' $t 'OK' 'not running') }
    elseif (-not $allSsd) { & $add (New-TuneItem 'sysmain' $t 'OK' 'on; not every disk is a known SSD (a spinning or unknown disk benefits from it)') }
    else { & $add (New-TuneItem 'sysmain' $t 'INFO' 'on while every disk is an SSD: it preloads apps a build worker does not use, and uses RAM and disk' 'services.msc > SysMain > Startup type: Disabled (optional; a small gain on an SSD-only PC).' $true 'sysmain') }
  }

  $t = 'Windows Search indexing'
  $paths = @(); if ($F.WorkRoot) { $paths += @{ Name = 'work_root'; Path = $F.WorkRoot } }; if ($F.NugetDir) { $paths += @{ Name = 'NuGet packages'; Path = $F.NugetDir } }
  if ($null -eq $F.SearchOn) { & $add (New-TuneItem 'search.index' $t 'SKIP' 'could not read the WSearch service') }
  elseif (-not $F.SearchOn) { & $add (New-TuneItem 'search.index' $t 'OK' 'the indexer (WSearch) is not running') }
  elseif ($null -eq $F.IndexRules) { & $add (New-TuneItem 'search.index' $t 'SKIP' 'could not read the indexed locations') }
  else {
    $ix = @($paths | Where-Object { Test-Indexed $_.Path $F.IndexRules } | ForEach-Object { "$($_.Name) ($($_.Path))" })
    if ($ix.Count) { & $add (New-TuneItem 'search.index' $t 'INFO' "indexed: $($ix -join ', '): every build writes files the indexer then reads" 'Indexing Options (control srchadmin.dll) > Modify: untick those folders. Report only.') }
    else { & $add (New-TuneItem 'search.index' $t 'OK' 'work_root and the NuGet packages are not indexed (or not given)') }
  }

  $t = 'Delivery Optimization (update sharing)'
  $mode = $F.DoPolicy; $src = 'policy'; if ($null -eq $mode) { $mode = $F.DoSetting; $src = 'setting' }
  if ($null -eq $mode -and $null -ne $F.Edition) { $src = 'edition default'; if ("$($F.Edition)" -like 'Core*') { $mode = 3 } else { $mode = 1 } }
  if ($null -eq $mode) { & $add (New-TuneItem 'do.mode' $t 'SKIP' 'could not read the download mode') }
  elseif (@(1, 2, 3) -contains [int]$mode) {
    $what = @{ 1 = 'PCs on this LAN'; 2 = 'PCs in a group'; 3 = 'PCs on the LAN and the internet' }[[int]$mode]
    $fx = 'do.mode'; $note = ''; if ($null -ne $F.DoPolicy) { $fx = ''; $note = ' It is set by a policy, so the fix does not offer to change it.' }
    & $add (New-TuneItem 'do.mode' $t 'INFO' "mode $mode ($src): this PC downloads from and uploads update files to $what.$note" 'Settings > Windows Update > Advanced options > Delivery Optimization: off. The fix sets the policy DODownloadMode=0 (HTTP only); Settings then shows "managed by your organization".' $true $fx)
  } else { & $add (New-TuneItem 'do.mode' $t 'OK' "mode $mode ($src): no peer uploads") }

  $t = 'Restart with a user logged on'
  if ($F.WuNoReboot -eq 1) { & $add (New-TuneItem 'wu.reboot' $t 'OK' 'NoAutoRebootWithLoggedOnUsers is 1') }
  else { & $add (New-TuneItem 'wu.reboot' $t 'INFO' 'the NoAutoRebootWithLoggedOnUsers policy is not set: Windows Update may restart the PC after installing updates' 'Report only. The scanner checks active hours (updates.activehours). The policy (HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU, NoAutoRebootWithLoggedOnUsers=1) works only with AUOptions=4, counts interactive logons only (not SSH sessions), some editions ignore it, and Settings then shows "managed by your organization".' $true) }

  $t = 'Visual effects over Remote Desktop'
  if ($F.RdpOn) { & $add (New-TuneItem 'visual.fx' $t 'INFO' 'Remote Desktop is on: animations and visual effects cost CPU on a slow PC' 'SystemPropertiesPerformance.exe > Adjust for best performance (per user). Report only.') }
  else { & $add (New-TuneItem 'visual.fx' $t 'OK' 'Remote Desktop is off') }

  $t = '.NET machine environment'
  if ($null -eq $F.DotnetEnv) { & $add (New-TuneItem 'dotnet.env' $t 'SKIP' 'could not read the machine environment') }
  else {
    $miss = @(@('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_GENERATE_ASPNET_CERTIFICATE') | Where-Object { -not $F.DotnetEnv[$_] })
    if ($miss.Count) { & $add (New-TuneItem 'dotnet.env' $t 'INFO' "not set for the machine: $($miss -join ', ') (telemetry, the first-run banner and the HTTPS dev certificate on every fresh build user)" 'Set them per machine in LanQueue machines.yml (env:) without touching the PC, or for the whole PC with the fix. After a machine-level change restart sshd (Restart-Service sshd) so new SSH sessions see it.' $true 'dotnet.env') }
    else { & $add (New-TuneItem 'dotnet.env' $t 'OK' 'telemetry opt-out, nologo and no dev certificate are set for the machine') }
  }

  $t = 'WSL memory reclaim'
  if (-not $F.WslInstalled) { & $add (New-TuneItem 'wsl.reclaim' $t 'OK' 'WSL is not installed') }
  elseif ($F.WslReclaim) { & $add (New-TuneItem 'wsl.reclaim' $t 'OK' '.wslconfig sets autoMemoryReclaim') }
  else { & $add (New-TuneItem 'wsl.reclaim' $t 'INFO' 'WSL is installed and .wslconfig does not set autoMemoryReclaim: the WSL VM keeps memory it no longer uses' 'Report only (a per-user file): add [experimental] autoMemoryReclaim=gradual to %USERPROFILE%\.wslconfig, then wsl --shutdown.') }

  $t = 'Defender scanning of build folders'; $d = $F.Defender
  if ($null -eq $d) { & $add (New-TuneItem 'defender.paths' $t 'SKIP' (& $why 'defender' 'Defender') '' $true '' $true) }
  elseif ($d.Mode -and $d.Mode -ne 'Normal') { & $add (New-TuneItem 'defender.paths' $t 'SKIP' "Defender is not the active antivirus (AMRunningMode: $($d.Mode))" '' $true '' $true) }
  elseif (-not $d.Rtp) { & $add (New-TuneItem 'defender.paths' $t 'OK' 'real-time protection is off' '' $true '' $true) }
  elseif ($null -eq $d.Scanned) { & $add (New-TuneItem 'defender.paths' $t 'SKIP' 'needs admin (exclusions are readable only elevated; the list is never printed)' '' $true '' $true) }
  else {
    $rows = @(); $scan = 0
    foreach ($k in @('work_root', 'NuGet packages', 'temp')) { if ($d.Scanned.Contains($k)) { $s = 'scanned'; if (-not $d.Scanned[$k]) { $s = 'excluded' } elseif ($k -ne 'temp') { $scan++ }; $rows += "$k $s" } }
    $adv = 'Not automated (report only). Prefer a Dev Drive (performance mode) for build folders. To exclude by hand: only a top-level build folder (like C:\lq) and your own %USERPROFILE%\.nuget\packages, never temp, a profile, system or program folder, or a folder on PATH. First let only Administrators, SYSTEM and you change it (icacls /inheritance:r /grant:r, /setowner), then in an elevated PowerShell: Add-MpPreference -ExclusionPath <folder> (undo: Remove-MpPreference). Steps: docs/TUNE.md.'
    if ($d.Scanned.Contains('temp') -and -not $d.Scanned['temp']) { & $add (New-TuneItem 'defender.paths' $t 'WARN' "real-time scanning: $($rows -join ', '): temp should never be excluded (downloads and archives are unpacked there)" 'Remove that exclusion in an elevated PowerShell: Remove-MpPreference -ExclusionPath <the temp folder>. Use a Dev Drive or a locked-down top-level build folder instead (docs/TUNE.md).' $true '' $true) }
    elseif ($scan -gt 0) { & $add (New-TuneItem 'defender.paths' $t 'INFO' "real-time scanning: $($rows -join ', ')" $adv $true '' $true) }
    else { & $add (New-TuneItem 'defender.paths' $t 'OK' "real-time scanning: $($rows -join ', ')" '' $true '' $true) }
  }

  $t = 'Defender scheduled scan'
  if ($null -eq $d) { & $add (New-TuneItem 'defender.schedule' $t 'SKIP' (& $why 'defender' 'Defender')) }
  elseif ($d.Mode -and $d.Mode -ne 'Normal') { & $add (New-TuneItem 'defender.schedule' $t 'SKIP' "Defender is not the active antivirus (AMRunningMode: $($d.Mode))") }
  elseif (-not $F.Hours) { & $add (New-TuneItem 'defender.schedule' $t 'SKIP' 'no -Hours given (the job window)') }
  elseif ($d.Day -eq 8 -or $null -eq $d.Time) { & $add (New-TuneItem 'defender.schedule' $t 'OK' 'no scheduled full scan') }
  else {
    $hit = @(); if (Test-InWindow $d.Time $F.Hours) { $hit += "scan at $(Format-Min $d.Time)" }; if ($d.Quick -gt 0 -and (Test-InWindow $d.Quick $F.Hours)) { $hit += "quick scan at $(Format-Min $d.Quick)" }
    $idle = ''; if ($d.Idle) { $idle = ' (only when the PC is idle, which a quiet build box often is)' }
    $w = $F.Hours.Split('-'); $tgt = ((Get-HoursMin $w[1]) + 60) % 1440; $fx = 'defender.scantime'; $adv = 'Move it outside the window: the fix (defender.scantime) sets the scan times to an hour after the window ends; or Set-MpPreference -ScanScheduleTime yourself.'
    if (Test-InWindow $tgt $F.Hours) { $fx = ''; $adv = 'The job window leaves no hour after it outside the window, so the fix is not offered; choose a scan time yourself (Set-MpPreference -ScanScheduleTime).' }
    if ($hit.Count) { & $add (New-TuneItem 'defender.schedule' $t 'INFO' "the scheduled $($hit -join ' and ') falls inside the job window $($F.Hours)$idle" $adv $true $fx) }
    else { & $add (New-TuneItem 'defender.schedule' $t 'OK' "scheduled scan at $(Format-Min $d.Time), outside $($F.Hours)") }
  }

  $t = 'Other antivirus'; $am = ''; if ($d -and $d.Mode -and $d.Mode -ne 'Normal') { $am = " (Defender AMRunningMode: $($d.Mode))" }
  $avAdv = 'Its real-time scanning slows builds too. Use its own setting for a build folder exclusion or a Dev Drive; this tool never changes it.'
  if ($null -eq $F.AvOther -and $am) { & $add (New-TuneItem 'av.other' $t 'INFO' "Defender is not the active antivirus$am; Security Center could not be read" $avAdv $false '' $true) }
  elseif ($null -eq $F.AvOther) { & $add (New-TuneItem 'av.other' $t 'SKIP' 'could not read Security Center (not on Windows Server)' '' $false '' $true) }
  elseif (@($F.AvOther).Count) { & $add (New-TuneItem 'av.other' $t 'INFO' "another antivirus is active: $(Join-Names $F.AvOther)$am" $avAdv $false '' $true) }
  else { & $add (New-TuneItem 'av.other' $t 'OK' 'no other active antivirus is registered' '' $false '' $true) }

  $t = 'Page file placement'
  if ($null -eq $F.Pagefile -or $null -eq $F.Disks) { & $add (New-TuneItem 'pagefile' $t 'SKIP' (& $why 'pagefile' 'the page file settings')) }
  else {
    $media = @{}; foreach ($p in @($F.Parts)) { if ("$p" -match '^([A-Z])=(\d+)$') { $n = $Matches[2]; $dk = @($F.Disks | Where-Object { "$($_.Id)" -eq $n }); if ($dk.Count) { $media[$Matches[1]] = $dk[0].Media } } }
    $hasSsd = @($media.Values | Where-Object { $_ -eq 'SSD' }).Count -gt 0
    $onHdd = @($F.Pagefile.Files | Where-Object { $media["$($_.Name)".Substring(0, 1).ToUpper()] -eq 'HDD' } | ForEach-Object { $_.Name })
    $small = @($F.Pagefile.Files | Where-Object { -not $F.Pagefile.Auto -and $_.Max -gt 0 -and $_.Max -lt $F.RamMB } | ForEach-Object { "$($_.Name) (max $($_.Max) MB)" })
    $msg = @(); if ($onHdd.Count -and $hasSsd) { $msg += "the page file is on a spinning disk ($(Join-Names $onHdd)) while an SSD exists" }; if ($small.Count) { $msg += "a fixed size below the RAM ($($F.RamMB) MB): $(Join-Names $small)" }
    if ($msg.Count) { & $add (New-TuneItem 'pagefile' $t 'INFO' ($msg -join '; ') 'SystemPropertiesAdvanced.exe > Performance Settings > Advanced > Virtual memory: system managed size on the SSD. Report only.') }
    else { & $add (New-TuneItem 'pagefile' $t 'OK' 'on an SSD or system managed') }
  }

  $t = 'Disk health'
  if ($null -eq $F.Disks) { & $add (New-TuneItem 'disk.health' $t 'SKIP' (& $why 'disks' 'the physical disks') '' $true) }
  else {
    $w = @(); foreach ($k in @($F.Disks)) {
      if ($k.Health -and $k.Health -ne 'Healthy') { $w += "disk $($k.Id) ($($k.Media)) reports $($k.Health)" }
      if ($k.Wear -ge 90) { $w += "disk $($k.Id) wear $($k.Wear)%" }; if ($k.ReadErr -gt 0) { $w += "disk $($k.Id) has $($k.ReadErr) uncorrected read errors" } }
    $note = ''; if (-not $F.Elevated) { $note = ' (reliability counters and TRIM need admin)' }
    if ($w.Count) { & $add (New-TuneItem 'disk.health' $t 'WARN' (($w -join '; ') + $note) 'Back up and plan to replace that disk; a failing disk corrupts builds. Report only.' $true) }
    elseif ($F.TrimOff -and @($F.Disks | Where-Object { $_.Media -eq 'SSD' }).Count) { & $add (New-TuneItem 'disk.health' $t 'INFO' 'TRIM is off for NTFS (DisableDeleteNotify=1) while an SSD is present' 'In an elevated prompt: fsutil behavior set DisableDeleteNotify 0. Report only.' $true) }
    else { & $add (New-TuneItem 'disk.health' $t 'OK' "$(@($F.Disks).Count) disk(s) healthy$note" '' $true) }
  }

  $t = 'Disk fullness'
  if ($null -eq $F.Volumes) { & $add (New-TuneItem 'disk.full' $t 'SKIP' 'could not read the drives') }
  else {
    $full = @($F.Volumes | Where-Object { $_.SizeGB -gt 0 -and (($_.SizeGB - $_.FreeGB) / $_.SizeGB) -gt 0.9 } | ForEach-Object { "$($_.Letter) $([math]::Round(100 * ($_.SizeGB - $_.FreeGB) / $_.SizeGB))% used" })
    if ($full.Count) { & $add (New-TuneItem 'disk.full' $t 'WARN' "over 90% full: $($full -join ', ') (NTFS slows down and builds fail when it fills)" 'Settings > System > Storage > Cleanup recommendations; move work_root to a drive with room. Report only.') }
    else { & $add (New-TuneItem 'disk.full' $t 'OK' 'every fixed drive is under 90% full') }
  }

  $t = 'Old network and storage drivers'
  if ($null -eq $F.Drivers) { & $add (New-TuneItem 'drivers.age' $t 'SKIP' (& $why 'drivers' 'the driver dates')) }
  else {
    $old = @($F.Drivers | Where-Object { $_.Date -and $_.Date -lt $F.Now.AddYears(-7) } | ForEach-Object { "$($_.Name) ($($_.Date.ToString('yyyy-MM-dd')))" })
    if ($old.Count) { & $add (New-TuneItem 'drivers.age' $t 'INFO' "older than 7 years: $(Join-Names $old 4)" 'Check Windows Update > Optional updates or the maker site for that model. Never use a generic driver updater. Report only: drivers are never touched.') }
    else { & $add (New-TuneItem 'drivers.age' $t 'OK' 'no maker network or storage driver older than 7 years (inbox Microsoft drivers are not counted)') }
  }

  $t = 'Download caches'
  $c = @(); foreach ($z in @(@('NuGet HTTP cache (v3-cache)', $F.NugetV3, 1024), @('Windows Update downloads', $F.SoftDist, 2048))) { if ($z[1] -and $z[1].MB -ge $z[2]) { $at = ''; if ($z[1].Capped) { $at = 'at least ' }; $c += "$($z[0]) $at$([math]::Round($z[1].MB / 1024, 1)) GB" } }
  if ($c.Count) { & $add (New-TuneItem 'cache.sizes' $t 'INFO' ($c -join ', ') 'Report only, nothing is deleted. NuGet: dotnet nuget locals http-cache --clear. Windows Update: Settings > System > Storage > Temporary files > Windows Update Cleanup. The scanner reports temp folders (temp).') }
  else { & $add (New-TuneItem 'cache.sizes' $t 'OK' 'the NuGet HTTP cache and Windows Update downloads are small') }

  $t = 'Autologon password'
  if ($null -eq $F.AutoLogon) { & $add (New-TuneItem 'autologon.plain' $t 'SKIP' 'could not read the Winlogon key' '' $false '' $true) }
  elseif ($F.AutoLogon -eq '1' -and $F.AutoLogonPlain) { & $add (New-TuneItem 'autologon.plain' $t 'WARN' 'autologon is on and its password is stored in plain text in the registry (the DefaultPassword value exists; it was not read)' 'Any local administrator, and backups of the registry, can read it. Use Sysinternals Autologon (stores it as an LSA secret) or turn autologon off (netplwiz). Report only.' $false '' $true) }
  elseif ($F.AutoLogon -eq '1') { & $add (New-TuneItem 'autologon.plain' $t 'OK' 'autologon is on without a plain-text password value' '' $false '' $true) }
  else { & $add (New-TuneItem 'autologon.plain' $t 'OK' 'autologon is off' '' $false '' $true) }

  $t = 'work_root and OneDrive'
  if (-not $F.WorkRoot) { & $add (New-TuneItem 'onedrive.workroot' $t 'SKIP' 'no -WorkRoot given') }
  else {
    $wr = $F.WorkRoot.Replace('/', '\').TrimEnd('\') + '\'; $od = @($F.OneDrive | Where-Object { $_ -and $wr.StartsWith("$_".TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) })
    if ($od.Count -or $wr -match '\\OneDrive( - [^\\]+)?\\') { & $add (New-TuneItem 'onedrive.workroot' $t 'WARN' 'work_root is inside a OneDrive folder: every build output would be synced' 'Use a short folder outside OneDrive, like C:/lq. Report only.') }
    else { & $add (New-TuneItem 'onedrive.workroot' $t 'OK' 'work_root is not inside OneDrive') }
  }

  & $add (New-TuneItem 'bios.power' 'BIOS power settings (reminder)' 'INFO' 'Windows cannot read these' 'In the BIOS/UEFI setup, on a PC that should answer at night: keep Wake on LAN on, turn off ErP/EuP or deep sleep, and set what happens after a power loss. Deep C-states are fine for most builds; turn them off only if the network drops. Report only.')

  $t = 'Automatic maintenance time'
  if (-not $F.Hours) { & $add (New-TuneItem 'maint.schedule' $t 'SKIP' 'no -Hours given (the job window)') }
  elseif ($F.MaintOff) { & $add (New-TuneItem 'maint.schedule' $t 'OK' 'automatic maintenance is turned off') }
  elseif ($null -eq $F.MaintMin) { & $add (New-TuneItem 'maint.schedule' $t 'SKIP' 'could not read the maintenance time') }
  elseif (Test-InWindow $F.MaintMin $F.Hours) {
    $dg = ''; if ($F.DefragOn) { $dg = '; the drive optimization (defrag/TRIM) task runs in it' }
    & $add (New-TuneItem 'maint.schedule' $t 'INFO' "automatic maintenance starts at $(Format-Min $F.MaintMin), inside the job window $($F.Hours)$dg" 'Control Panel > Security and Maintenance > Maintenance > Change maintenance settings: pick a time outside the window. Report only.')
  } else { & $add (New-TuneItem 'maint.schedule' $t 'OK' "automatic maintenance at $(Format-Min $F.MaintMin), outside $($F.Hours)") }
  return $it
}

function Get-TuneSummary($Items, [int]$Sec) {
  $s = [ordered]@{ ok = 0; info = 0; warn = 0; skip = 0; seconds = $Sec }
  foreach ($i in @($Items)) { $k = "$($i.status)".ToLowerInvariant(); $s[$k] = $s[$k] + 1 }
  return $s
}

# Redaction on values: host name, user name and profile folder in every free-text field (longest first).
function Protect-Text([string]$S, $Toks) {
  if (@($Toks).Count) { $S = [regex]::Replace($S, 'S-1-5-21-[0-9-]+', 'S-1-5-21-<redacted>') }   # account SIDs (task names often carry one)
  foreach ($t in $Toks) { $S = [regex]::Replace($S, '(?<![A-Za-z0-9_])' + [regex]::Escape($t) + '(?![A-Za-z0-9_])', '<redacted>', 'IgnoreCase') }
  return $S
}
function Get-RedactTokens($F) {
  $toks = @(); foreach ($v in @($F.Profile, ("$($F.Profile)" -replace '\\', '/'), ("$($F.Profile)" -replace '^.*[\\/]', ''), $F.User, $F.Hostname)) { if ($v -and "$v".Length -ge 2) { $toks += "$v" } }
  return @($toks | Sort-Object { "$_".Length } -Descending)
}

function New-TuneReport($F, $Items, [bool]$Red) {
  $toks = @(); if ($Red) { $toks = Get-RedactTokens $F }
  $hn = $F.Hostname; $us = $F.User; if ($Red) { $hn = '<redacted>'; $us = '<redacted>' }
  $ctx = [ordered]@{ container = [bool]$F.Container; elevated = [bool]$F.Elevated }
  if ($F.WorkRoot) { $ctx.workRoot = Protect-Text $F.WorkRoot $toks }
  if ($F.Hours) { $ctx.hours = $F.Hours }
  $out = @(foreach ($i in @($Items)) {
      [ordered]@{ id = $i.id; title = $i.title; status = $i.status; detail = (Protect-Text $i.detail $toks); advice = (Protect-Text $i.advice $toks); needsAdmin = [bool]$i.needsAdmin; fixId = $i.fixId; security = [bool]$i.security } })
  return [ordered]@{
    schema = 'lanq-makeready-tune/v1'; tool = [ordered]@{ name = 'lanq-makeready-tune'; version = $ToolVersion }
    generatedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    host = [ordered]@{ hostname = "$hn"; user = "$us"; os = 'windows'; arch = $F.Arch }
    context = $ctx; items = $out; summary = (Get-TuneSummary $out $F.Seconds)
  }
}

function ConvertTo-AsciiJson($Obj) {
  $t = $Obj | ConvertTo-Json -Depth 6
  return [regex]::Replace($t, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
}

# ---------- probes (read-only; each with a time budget inside the 90 s deadline) ----------

function Get-Left { return ($TotalSec - [int]$Clock.Elapsed.TotalSeconds) }

# Runs a self-contained read-only block in a separate runspace with a deadline; $null on error, timeout or when the
# scan's own deadline is near (then $OverBudget[$Name] is set and the check says "time budget").
function Invoke-Probe([string]$Name, [int]$Sec, [scriptblock]$Block, $Arg = $null) {
  $left = Get-Left; if ($left -lt 2) { $OverBudget[$Name] = $true; return $null }
  $Sec = [math]::Min($Sec, $left - 1); $ps = $null
  try {
    $ps = [System.Management.Automation.PowerShell]::Create()
    [void]$ps.AddScript($Block.ToString()).AddArgument($Arg)
    $h = $ps.BeginInvoke()
    if ($h.AsyncWaitHandle.WaitOne($Sec * 1000)) { $r = $ps.EndInvoke($h); $ps.Dispose(); return , @($r) }
    [void]$ps.BeginStop($null, $null); $script:Hung = $true; $OverBudget[$Name] = $true
    return $null
  } catch { return $null }
}

# Runs a program from System32 with fixed arguments, closed stdin and a deadline; $null when it cannot start.
function Invoke-Exe([string]$Exe, [string]$ArgText, [int]$Sec = 8) {
  $left = Get-Left; if ($left -lt 2) { return $null }
  $Sec = [math]::Min($Sec, $left - 1)
  try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe; $psi.Arguments = $ArgText; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WorkingDirectory = $env:SystemRoot
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()   # both drained, so a full pipe cannot block it
    if (-not $p.WaitForExit($Sec * 1000)) { try { $p.Kill() } catch { }; return $null }
    [void]$o.Wait(2000); [void]$e.Wait(2000)
    return @{ Code = $p.ExitCode; Out = "$($o.Result)" }
  } catch { return $null }
}

# A registry value through Get-Item and the key object (never Get-ItemProperty); $null when absent or unreadable.
function Get-RegValue([string]$Path, [string]$Name) {
  try { $k = Get-Item -LiteralPath $Path -ErrorAction Stop; if (@($k.GetValueNames()) -contains $Name) { return $k.GetValue($Name) } } catch { }
  return $null
}

function Get-DirSizeMB([string]$Path, [int]$Sec = 5) {
  if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
  $sw = [System.Diagnostics.Stopwatch]::StartNew(); $total = [long]0; $capped = $false
  $stack = New-Object 'System.Collections.Generic.Stack[string]'; $stack.Push($Path)
  while ($stack.Count -gt 0 -and -not $capped) {
    if ((Get-Left) -lt 2) { $capped = $true; break }   # the scan's own deadline
    $d = $stack.Pop()
    try {
      foreach ($f in [System.IO.Directory]::EnumerateFiles($d)) { if ($sw.Elapsed.TotalSeconds -gt $Sec) { $capped = $true; break }; try { $total += (New-Object System.IO.FileInfo $f).Length } catch { } }
      foreach ($s in [System.IO.Directory]::EnumerateDirectories($d)) { if ((New-Object System.IO.DirectoryInfo $s).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }; $stack.Push($s) }
    } catch { }
  }
  return @{ MB = [int][math]::Round($total / 1MB); Capped = $capped }
}

function Test-Elevated {
  try { return (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
  catch { return $false }
}

function Get-TuneFacts([string]$H, [string]$W) {
  $F = @{ Hours = $H; WorkRoot = $W; Hostname = $env:COMPUTERNAME; User = $env:USERNAME; Profile = $env:USERPROFILE; Now = [DateTime]::Now; Budget = $OverBudget }
  $F.Elevated = Test-Elevated
  $F.Arch = 'x86'; if ("$env:PROCESSOR_ARCHITECTURE $env:PROCESSOR_ARCHITEW6432" -match 'ARM64') { $F.Arch = 'arm64' } elseif ([Environment]::Is64BitOperatingSystem) { $F.Arch = 'x64' }
  $ctl = $null; try { $ctl = Get-Item -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control' -ErrorAction Stop } catch { }
  $F.Container = [bool]($ctl -and @($ctl.GetValueNames()) -contains 'ContainerType')
  $F.Edition = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'EditionID'
  $F.NugetDir = "$env:USERPROFILE\.nuget\packages"; if ($env:NUGET_PACKAGES) { $F.NugetDir = "$env:NUGET_PACKAGES" }
  $F.OneDrive = @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial) | Where-Object { $_ }
  # power (powercfg /q, AC values of the active plan)
  $pc = Join-Path ([Environment]::GetFolderPath('System')) 'powercfg.exe'
  $r = Invoke-Exe $pc '/q SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX'; if ($r -and $r.Code -eq 0) { $F.CpuMax = Get-PowercfgAc $r.Out } else { $OverBudget['powercfg'] = ($null -eq $r) }
  $r = Invoke-Exe $pc '/q SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN'; if ($r -and $r.Code -eq 0) { $F.CpuMin = Get-PowercfgAc $r.Out }
  $r = Invoke-Exe $pc '/q SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226'; if ($r -and $r.Code -eq 0) { $F.UsbSuspend = Get-PowercfgAc $r.Out }
  # network adapters (USB or not, driver dates) and storage drivers (Class registry): one 15 s budget
  $dr = Invoke-Probe 'drivers' 15 { param($x)
    $n = @(Get-NetAdapter -Physical -ErrorAction Stop | ForEach-Object { @{ Kind = 'nic'; Name = "$($_.InterfaceDescription)"; Up = ("$($_.Status)" -eq 'Up'); Usb = ("$($_.PnPDeviceID)" -like 'USB\*'); Date = "$($_.DriverDate)"; Prov = "$($_.DriverProvider)" } })
    foreach ($g in '{4d36e97b-e325-11ce-bfc1-08002be10318}', '{4d36e96a-e325-11ce-bfc1-08002be10318}') {
      foreach ($k in @(Get-ChildItem -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$g" -ErrorAction SilentlyContinue)) {
        if ($k.PSChildName -notmatch '^\d{4}$') { continue }
        $n += @{ Kind = 'storage'; Name = "$($k.GetValue('DriverDesc'))"; Date = "$($k.GetValue('DriverDate'))"; Prov = "$($k.GetValue('ProviderName'))" } } }
    $n }
  if ($null -ne $dr) {
    $F.Nics = @($dr | Where-Object { $_.Kind -eq 'nic' })
    $F.Drivers = @(foreach ($d in @($dr)) { if ($d.Prov -match '^Microsoft') { continue }; $dt = $null
        foreach ($fmt in 'yyyy-MM-dd', 'M-d-yyyy', 'M/d/yyyy') { $p = [datetime]::MinValue; if ([datetime]::TryParseExact($d.Date.Split(' ')[0], $fmt, [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$p)) { $dt = $p; break } }
        if ($d.Name) { @{ Name = $d.Name; Date = $dt } } })
  }
  # Run entries and whether Task Manager has them disabled (StartupApproved: absent = enabled; first byte odd = disabled)
  $run = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; $ap = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved'
  $F.Startup = @(foreach ($src in @(@('HKCU:', $run, 'Run'), @('HKLM:', $run, 'Run'), @('HKLM:', 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run', 'Run32'))) {
      try { $k = Get-Item -LiteralPath "$($src[0])\$($src[1])" -ErrorAction Stop } catch { continue }
      foreach ($n in @($k.GetValueNames())) { if (-not $n) { continue }
        $flag = Get-RegValue "$($src[0])\$ap\$($src[2])" $n; $on = $true; if ($flag -is [byte[]] -and $flag.Length -ge 1) { $on = (($flag[0] -band 1) -eq 0) }
        $exe = ("$($k.GetValue($n))" -replace '^\s*"?([^"]*?\.exe).*$', '$1') -replace '^.*\\', ''
        @{ Name = $n; Exe = $exe; Enabled = $on } } })
  $sv = Invoke-Probe 'services' 15 { param($x) @(Get-Service -ErrorAction SilentlyContinue | ForEach-Object { @{ Name = "$($_.Name)"; Display = "$($_.DisplayName)"; Start = "$($_.StartType)"; Status = "$($_.Status)" } }) }
  if ($null -ne $sv) { $F.Services = @($sv) }
  $ws = @($F.Services | Where-Object { $_.Name -eq 'WSearch' }); if ($null -ne $F.Services) { $F.SearchOn = ($ws.Count -gt 0 -and $ws[0].Status -eq 'Running') }
  $tk = Invoke-Probe 'tasks' 20 { param($x)
    if (@(Get-Command -Name Get-ScheduledTask -ErrorAction SilentlyContinue).Count -eq 0) { return 'nomodule' }
    try { $all = @(Get-ScheduledTask -ErrorAction Stop) } catch { if ("$($_.Exception.Message)" -match '(?i)denied|0x80070005') { return 'denied' }; return 'error' }
    $df = @($all | Where-Object { $_.TaskPath -eq '\Microsoft\Windows\Defrag\' -and $_.TaskName -eq 'ScheduledDefrag' -and "$($_.State)" -ne 'Disabled' }).Count -gt 0
    @{ Defrag = $df; List = @($all | Where-Object { $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object { @{ Path = "$($_.TaskPath)"; Name = "$($_.TaskName)"; State = "$($_.State)" } }) } }
  if ($null -ne $tk) { if (@('denied', 'nomodule', 'error') -contains @($tk)[0]) { $F.Tasks = @($tk)[0] } else { $F.Tasks = @(@($tk)[0].List); $F.DefragOn = @($tk)[0].Defrag } }
  $dk = Invoke-Probe 'disks' 20 { param($admin)
    $out = @(); foreach ($d in @(Get-PhysicalDisk -ErrorAction Stop)) {
      $h = @{ Id = "$($d.DeviceId)"; Media = "$($d.MediaType)"; Bus = "$($d.BusType)"; Health = "$($d.HealthStatus)"; Wear = $null; ReadErr = $null }
      if ($admin) { try { $rc = $d | Get-StorageReliabilityCounter -ErrorAction Stop; $h.Wear = $rc.Wear; $h.ReadErr = $rc.ReadErrorsUncorrected } catch { } }
      $out += $h }
    @{ Disks = $out; Parts = @(Get-Partition -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { "$($_.DriveLetter)=$($_.DiskNumber)".ToUpper() }) } } $F.Elevated
  if ($null -ne $dk) { $F.Disks = @(@($dk)[0].Disks); $F.Parts = @(@($dk)[0].Parts) }
  try { $F.Volumes = @([System.IO.DriveInfo]::GetDrives() | Where-Object { "$($_.DriveType)" -eq 'Fixed' -and $_.IsReady } | ForEach-Object { @{ Letter = "$($_.Name)".Substring(0, 2); SizeGB = [math]::Round($_.TotalSize / 1GB, 1); FreeGB = [math]::Round($_.AvailableFreeSpace / 1GB, 1) } }) } catch { }
  if ($F.Elevated) { $r = Invoke-Exe (Join-Path ([Environment]::GetFolderPath('System')) 'fsutil.exe') 'behavior query DisableDeleteNotify'; if ($r -and "$($r.Out)" -match 'NTFS DisableDeleteNotify\s*=\s*1') { $F.TrimOff = $true } }
  # Windows Search crawl rules
  try { $F.IndexRules = @(foreach ($sub in 'WorkingSetRules', 'DefaultRules') { foreach ($k in @(Get-ChildItem -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows Search\CrawlScopeManager\Windows\SystemIndex\$sub" -ErrorAction Stop)) { @{ Url = "$($k.GetValue('URL'))"; Include = ($k.GetValue('Include') -eq 1) } } }) } catch { }
  # Delivery Optimization, Windows Update restart policy, Remote Desktop
  $F.DoPolicy = Get-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode'
  $F.DoSetting = Get-RegValue 'Registry::HKEY_USERS\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings' 'DownloadMode'
  $F.WuNoReboot = Get-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoRebootWithLoggedOnUsers'
  $F.RdpOn = ((Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' 'fDenyTSConnections') -eq 0)
  # .NET machine environment, WSL
  $envKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  try { $null = Get-Item -LiteralPath $envKey -ErrorAction Stop; $F.DotnetEnv = @{}; foreach ($n in 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_GENERATE_ASPNET_CERTIFICATE') { $F.DotnetEnv[$n] = Get-RegValue $envKey $n } } catch { }
  $F.WslInstalled = (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\LxssManager') -or (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\WslService')
  $wc = Join-Path "$env:USERPROFILE" '.wslconfig'; $F.WslReclaim = $false
  try { if (Test-Path -LiteralPath $wc -PathType Leaf) { $F.WslReclaim = [bool](@(Get-Content -LiteralPath $wc -TotalCount 200 -ErrorAction Stop) -match '^\s*autoMemoryReclaim\s*=') } } catch { }
  # Defender: running mode, real-time scanning, schedule; whether each build folder is excluded (the list never leaves the probe)
  $paths = [ordered]@{}; if ($W) { $paths['work_root'] = $W.Replace('/', '\') }; $paths['NuGet packages'] = $F.NugetDir; $paths['temp'] = "$env:TEMP"
  $df = Invoke-Probe 'defender' 15 { param($paths)
    $s = Get-MpComputerStatus -ErrorAction Stop; $p = Get-MpPreference -ErrorAction Stop
    $min = { param($v) if ($v -is [TimeSpan]) { [int]$v.TotalMinutes } elseif ($v -is [DateTime]) { [int]$v.TimeOfDay.TotalMinutes } else { $null } }
    $h = @{ Mode = "$($s.AMRunningMode)"; Rtp = [bool]$s.RealTimeProtectionEnabled; Day = [int]$p.ScanScheduleDay; Time = (& $min $p.ScanScheduleTime); Quick = (& $min $p.ScanScheduleQuickScanTime); Idle = [bool]$p.ScanOnlyIfIdleEnabled; Scanned = $null }
    $ex = @($p.ExclusionPath | Where-Object { $_ })
    if (-not ($ex.Count -eq 1 -and "$($ex[0])" -match '^N/A')) {
      $h.Scanned = [ordered]@{}
      foreach ($k in $paths.Keys) { $t = "$($paths[$k])".TrimEnd('\') + '\'; $h.Scanned[$k] = -not (@($ex | Where-Object { $t.StartsWith(([Environment]::ExpandEnvironmentVariables("$_")).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) } }
    $h } $paths
  if ($null -ne $df) { $F.Defender = @($df)[0] }
  $av = Invoke-Probe 'av' 10 { param($x) @(Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -OperationTimeoutSec 8 -ErrorAction Stop |
      Where-Object { "$($_.displayName)" -notmatch '(Windows|Microsoft) Defender' -and ((([int]$_.productState) -shr 12) -band 0xF) -eq 1 } | ForEach-Object { "$($_.displayName)" }) }
  if ($null -ne $av) { $F.AvOther = @($av | Where-Object { $_ }) }
  $pf = Invoke-Probe 'pagefile' 15 { param($x)
    $cs = Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 10 -ErrorAction Stop
    $set = @(Get-CimInstance Win32_PageFileSetting -OperationTimeoutSec 10 -ErrorAction Stop | ForEach-Object { @{ Name = "$($_.Name)"; Max = [int]$_.MaximumSize } })
    $use = @(Get-CimInstance Win32_PageFileUsage -OperationTimeoutSec 10 -ErrorAction Stop | ForEach-Object { @{ Name = "$($_.Name)"; Max = 0 } })
    $files = $set; if ($cs.AutomaticManagedPagefile -or $files.Count -eq 0) { $files = $use }
    @{ Auto = [bool]$cs.AutomaticManagedPagefile; Files = $files; RamMB = [int]($cs.TotalPhysicalMemory / 1MB) } }
  if ($null -ne $pf) { $F.Pagefile = @($pf)[0]; $F.RamMB = $F.Pagefile.RamMB }
  # caches (time-capped walks)
  $F.NugetV3 = Get-DirSizeMB "$env:LOCALAPPDATA\NuGet\v3-cache" 4
  $F.SoftDist = Get-DirSizeMB "$env:SystemRoot\SoftwareDistribution\Download" 4
  # autologon: the DefaultPassword value is only tested for existence, never read
  try { $wl = Get-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction Stop
    $F.AutoLogon = "$($wl.GetValue('AutoAdminLogon'))".Trim(); $F.AutoLogonPlain = (@($wl.GetValueNames()) -contains 'DefaultPassword') } catch { }
  # automatic maintenance
  $mk = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'
  $F.MaintOff = ((Get-RegValue $mk 'MaintenanceDisabled') -eq 1); $ab = Get-RegValue $mk 'Activation Boundary'; $F.MaintMin = 120
  if ($ab) { $F.MaintMin = $null; if ("$ab" -match 'T(\d{2}):(\d{2})') { $F.MaintMin = [int]$Matches[1] * 60 + [int]$Matches[2] } }
  $F.Seconds = [int]$Clock.Elapsed.TotalSeconds
  return $F
}

# ---------- main ----------

if ($env:LANQ_MAKEREADY_TUNE_LIB -eq '1') {
  if ($MyInvocation.InvocationName -eq '.') { return }
  [Console]::Error.WriteLine('lanq-makeready-tune: LANQ_MAKEREADY_TUNE_LIB=1 is only for tests that dot-source this file; unset it.'); exit 2
}
$Usage = 'usage: lanq-makeready-tune.ps1 [-Json <path|->] [-Redact] [-Hours HH:mm-HH:mm] [-WorkRoot C:/lq]   (read-only; the sh script uses --json/--redact/--hours/--work-root)'
if ($Extra -and @($Extra | Where-Object { $null -ne $_ -and "$_" -ne '' }).Count -gt 0) { [Console]::Error.WriteLine("lanq-makeready-tune: unknown argument(s): $(@($Extra) -join ' ')"); [Console]::Error.WriteLine($Usage); exit 2 }
if ($Hours -and -not (Test-Hours $Hours)) { [Console]::Error.WriteLine('lanq-makeready-tune: -Hours must look like 00:00-07:00 (HH:mm-HH:mm, start different from end).'); exit 2 }
if ($WorkRoot) { $WorkRoot = $WorkRoot.Replace('\', '/') }
if ($WorkRoot -and -not (Test-WorkRoot $WorkRoot)) { [Console]::Error.WriteLine('lanq-makeready-tune: -WorkRoot must be a drive path like C:/lq (letters, digits, _ . / -; no spaces, no ..), as in LanQueue machines.yml.'); exit 2 }
$StdoutJson = ($Json -eq '-')
if ($StdoutJson) { $WarningPreference = 'SilentlyContinue'; $ProgressPreference = 'SilentlyContinue'; $InformationPreference = 'SilentlyContinue'; $VerbosePreference = 'SilentlyContinue' }
$facts = Get-TuneFacts $Hours $WorkRoot
$items = Get-TuneItems $facts
$report = New-TuneReport $facts $items ([bool]$Redact)

function Write-Out([string]$Text, [string]$Color = 'Gray') { if (-not $StdoutJson) { Write-Host $Text -ForegroundColor $Color } }
$color = @{ OK = 'Green'; INFO = 'Cyan'; WARN = 'Yellow'; SKIP = 'DarkGray' }
Write-Out "lanq-makeready-tune $ToolVersion (read-only: nothing is changed; this never affects whether the PC is worker-ready)"
Write-Out ''
foreach ($i in $report.items) {
  Write-Out ('[{0,-4}] {1}: {2}' -f $i.status, $i.title, $i.detail) $color[$i.status]
  if ($i.advice) { Write-Out "       $($i.advice)" }
  if ($i.fixId) { Write-Out "       fix id: $($i.fixId)" 'DarkCyan' }
}
$s = $report.summary
Write-Out ''
Write-Out ("Summary: {0} OK, {1} INFO, {2} WARN, {3} SKIP in {4} s" -f $s.ok, $s.info, $s.warn, $s.skip, $s.seconds)
if (@($report.items | Where-Object { $_.fixId }).Count -gt 0) { Write-Out 'Possible fixes (run at the PC, elevated): lanq-makeready-fix.ps1 -Tune -WhatIf   (then without -WhatIf; each change is confirmed and can be undone)' }
if ($Json) {
  try {
    $text = ConvertTo-AsciiJson $report
    if ($StdoutJson) { [Console]::Out.Write($text + "`n") }
    else { $full = $Json; if (-not [System.IO.Path]::IsPathRooted($full)) { $full = Join-Path (Get-Location).ProviderPath $Json }; [System.IO.File]::WriteAllText($full, $text, (New-Object System.Text.UTF8Encoding($false))); Write-Out 'JSON report written to the path you gave.' }
  } catch { [Console]::Error.WriteLine("lanq-makeready-tune: could not write the JSON report: $($_.Exception.Message)") }
}
if ($script:Hung) { [Console]::Out.Flush(); [Environment]::Exit(0) }
exit 0
