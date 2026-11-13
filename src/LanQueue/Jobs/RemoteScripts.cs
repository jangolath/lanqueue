using System.Globalization;
using System.Text;
using LanQueue.Config;
using LanQueue.Policy;

namespace LanQueue.Jobs;

/// <summary>Builds the remote command strings (sh on Linux, PowerShell on Windows). Pure functions, unit tested.</summary>
public static partial class RemoteScripts
{
    /// <summary>Conservative cap on the whole remote command line for Windows hosts (cmd.exe allows 8191 characters).</summary>
    internal const int MaxWindowsCommandLength = 8000;

    public static string Wrap(Machine m, string script)
    {
        if (m.Os != MachineOs.Windows)
        {
            return "sh -c " + ShellQuote.Posix(script);
        }

        var command = WindowsCommand(script);
        return command.Length > MaxWindowsCommandLength
            ? throw new InvalidOperationException(
                $"The command for Windows machine '{m.Name}' is {command.Length} characters once encoded; the limit is {MaxWindowsCommandLength}. "
                + "Shorten the arguments (the machine's env: and path_prepend: count too).")
            : command;
    }

    /// <summary>The powershell command line for a script, whatever its length (<see cref="Wrap"/> refuses one over the cap).</summary>
    internal static string WindowsCommand(string script)
    {
        // Indentation and blank lines cost characters under the encoded-command cap and mean nothing to PowerShell here: none of these
        // scripts has a here-string, and no value spliced into them can contain a line break.
        var compact = string.Join('\n', script.Split('\n').Select(l => l.TrimStart(' ', '\t').TrimEnd('\r')).Where(l => l.Trim().Length > 0));
        return "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'\n" + compact));
    }

    /// <summary>
    /// The health probe: one <c>key=value</c> per line. On Windows <c>load=</c> and memory come from CIM when the ssh user may use it, else
    /// load from GetSystemTimes (<see cref="WindowsProbeLoad"/>) and memory from .NET's ComputerInfo (GlobalMemoryStatusEx underneath);
    /// <c>load_source=cim|native|none</c> and <c>mem_source=cim|vb|none</c> say which (with none, load= stays 0 for an older lanq, and this one
    /// shows the load as unknown). Keys are only ever added, so an older lanq reads the output of a newer probe.
    /// </summary>
    public static string Probe(Machine m) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
            $inv = [cultureinfo]::InvariantCulture
            $cpu = $null; $ls = 'cim'
            try { $cpu = (Get-CimInstance Win32_Processor -ErrorAction Stop | Measure-Object -Property LoadPercentage -Average).Average } catch { }
            if ($null -eq $cpu) { $ls = 'none'; try {
            {{WindowsProbeLoad}}
            } catch { } }
            if ($null -eq $cpu) { $cpu = 0 }
            $drive = New-Object System.IO.DriveInfo ($root.Substring(0,1))
            $sf = @(Get-ChildItem -Path "$root/locks" -Filter 'slot-*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^slot-[0-9]+\z' }); $slots = $sf.Count
            'load=' + ([double]$cpu / 100).ToString('0.###', $inv)
            'load_source=' + $ls
            'disk_free_gb=' + ($drive.AvailableFreeSpace / 1GB).ToString('0.#', $inv)
            'slots_used=' + $slots
            $mt = 0; $ms = 'cim'
            try { $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop; $mt = 1KB * $os.TotalVisibleMemorySize; $mf = 1KB * $os.FreePhysicalMemory } catch { }
            if (-not $mt) { $ms = 'vb'; try { Add-Type -AssemblyName Microsoft.VisualBasic; $vb = New-Object Microsoft.VisualBasic.Devices.ComputerInfo; $mt = $vb.TotalPhysicalMemory; $mf = $vb.AvailablePhysicalMemory } catch { } }
            if ($mt) { 'mem_free_gb=' + ($mf / 1GB).ToString('0.##', $inv); 'mem_total_gb=' + ($mt / 1GB).ToString('0.##', $inv) } else { $ms = 'none' }
            'mem_source=' + $ms
            'cores=' + [Environment]::ProcessorCount
            'paused=' + $(if (Test-Path -LiteralPath "$root/PAUSE") { '1' } else { '0' })
            'local_time=' + (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss', $inv)
            $e = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(); $s = $e - ($e % 900)
            {{WindowsStaleSlotCount}}
            'now_epoch=' + $e
            'tz_start=' + $s
            'tz_offsets=' + ((0..144 | ForEach-Object { [int][TimeZoneInfo]::Local.GetUtcOffset([DateTimeOffset]::FromUnixTimeSeconds($s + 900 * $_)).TotalMinutes }) -join ',')
            {{(m.Transfer == TransferMode.Archive ? WindowsTarProbe : GitProbeWindows)}}
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            pos() { case "$1" in ''|*[!0-9]*|0*) return 1 ;; esac; }
            n=$(getconf _NPROCESSORS_ONLN 2>/dev/null); pos "$n" || n=$(nproc 2>/dev/null); pos "$n" || n=$(grep -c '^processor' /proc/cpuinfo 2>/dev/null); pos "$n" || n=
            cpus=${n:-1}
            l=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo 0)
            d="$R"; [ -d "$d" ] || d=/
            echo "load=$(awk -v l="$l" -v c="$cpus" 'BEGIN{printf "%.3f", l/c}')"
            echo "disk_free_gb=$(df -Pk "$d" | awk 'NR==2{printf "%.1f", $4/1048576}')"
            echo "slots_used=$(ls "$R/locks" 2>/dev/null | grep -c '^slot-[0-9][0-9]*$')"
            {{LinuxStaleSlotCount}}
            awk '/^MemAvailable:/{print "mem_free_gb=" sprintf("%.2f", $2/1048576)} /^MemTotal:/{print "mem_total_gb=" sprintf("%.2f", $2/1048576)} /^SwapFree:/{print "swap_free_gb=" sprintf("%.2f", $2/1048576)}' /proc/meminfo 2>/dev/null
            if [ -n "$n" ]; then echo "cores=$n"; fi
            if [ -r /proc/pressure/memory ]; then awk '$1=="some"{for(i=2;i<=NF;i++) if(index($i,"avg10=")==1) print "mem_pressure=" substr($i,7)}' /proc/pressure/memory 2>/dev/null; fi
            if [ -e "$R/PAUSE" ] || [ -e "$R/pause" ]; then echo paused=1; else echo paused=0; fi
            echo "local_time=$(date '+%Y-%m-%dT%H:%M:%S')"
            {{LinuxOffsetTable}}
            {{(m.Transfer == TransferMode.Archive ? LinuxTarProbe : GitProbeLinux)}}
            """;

    /// <summary>
    /// now_epoch=, tz_start= and tz_offsets=: the worker's UTC offset in minutes on every UTC quarter hour from the one at or before now
    /// (tz_start) for 36 hours, 145 values. GNU date converts them all in one call (<c>date -f -</c>); otherwise one <c>date -d @EPOCH +%z</c>
    /// each (busybox accepts that). When date can do neither, tz_offsets is left out and lanq uses wall-clock time for the window end.
    /// </summary>
    internal const string LinuxOffsetTable = """
        e=$(date +%s); s=$((e - e % 900)); echo "now_epoch=$e"; echo "tz_start=$s"
        l=$(k=0; while [ "$k" -le 144 ]; do echo "@$((s + k * 900))"; k=$((k + 1)); done)
        z=$(printf '%s\n' "$l" | date -f - +%z 2>/dev/null)
        if [ "$(printf '%s\n' "$z" | grep -c .)" != 145 ]; then z=; for t in $l; do o=$(date -d "$t" +%z 2>/dev/null) || { z=; break; }; z="$z $o"; done; fi
        if [ -n "$z" ]; then echo $z | awk '{ s = "tz_offsets="; for (i = 1; i <= NF; i++) { v = $i; if (v !~ /^[+-][0-9][0-9][0-9][0-9]$/) exit; m = substr(v, 2, 2) * 60 + substr(v, 4, 2); s = s (i > 1 ? "," : "") (substr(v, 1, 1) == "-" ? 0 - m : m) } if (NF == 145) print s }'; fi
        """;

    /// <summary>
    /// Options for every git call on a worker (hooks in the cache or a hooks path in the worker's git config must never run
    /// anything). Hooks are pointed at the null device, the fsmonitor daemon is off, and every transport is refused; only the
    /// bundle fetch re-allows reading a local file, with <c>-c protocol.file.allow=always</c> on that one call.
    /// </summary>
    internal const string GitHardeningLinux = "-c core.hooksPath=/dev/null -c core.attributesFile=/dev/null -c core.fsmonitor=false -c protocol.allow=never -c protocol.file.allow=never";

    /// <summary>
    /// The sh git wrapper: besides <see cref="GitHardeningLinux"/>, the system config (GIT_CONFIG_NOSYSTEM) and the worker user's global
    /// config (GIT_CONFIG_GLOBAL at the null device, git 2.32 and later) are never read, so a filter, include or attributes file set
    /// there (git-lfs, for example) never runs for a lanq job.
    /// </summary>
    internal const string LinuxGitFunction = "G() { GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null git " + GitHardeningLinux + " \"$@\"; }";

    /// <summary>
    /// The only keys a worker cache's own config may hold: what <c>git init --bare</c> writes. Anything else (filter.*, include.*,
    /// includeIf.*, alias.*, remote.*, credential.*, url.*, any other core.* key...) means someone changed the cache, so prepare deletes and
    /// recreates it (one full bundle).
    /// </summary>
    internal const string CacheConfigKeys = @"core\.(repositoryformatversion|filemode|bare|symlinks|ignorecase|precomposeunicode|logallrefupdates)|extensions\.(objectformat|refstorage)";

    /// <summary>
    /// The only top-level entries a worker cache may hold: what git init, fetch, worktree add and auto-gc create. Anything else, above all a
    /// <c>commondir</c> or <c>gitdir</c> file (git follows them even in a main bare repository and then reads another directory's config,
    /// info/attributes and objects), makes prepare recreate the cache. The worktrees/*/commondir files git writes per job are below this level.
    /// </summary>
    internal const string CacheTopLevel =
        @"HEAD|config|description|hooks|info|objects|refs|reftable|branches|packed-refs|logs|worktrees|FETCH_HEAD|ORIG_HEAD|shallow|gc\.pid|gc\.log|(HEAD|config|packed-refs|shallow)\.lock";

    /// <summary>Printed by prepare when it recreated a worker cache whose config held anything but <see cref="CacheConfigKeys"/>.</summary>
    internal const string CacheRecreated = "the worker cache had unexpected config keys or files; deleted and recreated it (this job sends a full bundle)";

    /// <inheritdoc cref="GitHardeningLinux"/>
    internal const string GitHardeningWindows = "-c core.longpaths=true -c core.hooksPath=NUL -c core.attributesFile=NUL -c core.fsmonitor=false -c protocol.allow=never -c protocol.file.allow=never";

    internal const string GitProbeWindows = "'tool=' + $(if (Get-Command git -ErrorAction SilentlyContinue) { 'ok' } else { 'missing' })";

    /// <summary>lanq doctor --remote: the worker git's version line (GIT_CONFIG_GLOBAL, which isolates the global config, needs 2.32).</summary>
    internal const string GitVersionWindows = """'git_version=' + "$(& git --version 2>$null | Select-Object -First 1)" """;

    /// <inheritdoc cref="GitVersionWindows"/>
    internal const string GitVersionLinux = """echo "git_version=$(git --version 2>/dev/null | sed -n 1p)" """;

    internal const string GitProbeLinux = "if git --version >/dev/null 2>&1; then echo tool=ok; else echo tool=missing; fi";

    /// <summary>
    /// Archive mode needs a tar whose -t listing can be trusted: GNU tar or bsdtar/libarchive (busybox tar hides hard links and strips
    /// leading '/' and '../' in its listing, so it is refused), plus gzip. Defines the shell function tar_supported.
    /// </summary>
    private const string LinuxTarSupportedFunction =
        "tar_supported() { v=$(tar --version 2>&1 | head -n 1); case \"$v\" in *\"GNU tar\"*|*bsdtar*|*libarchive*) command -v gzip >/dev/null 2>&1 ;; *) return 1 ;; esac; }";

    internal const string LinuxTarProbe = LinuxTarSupportedFunction + "\nif tar_supported; then echo tool=ok; else echo tool=missing; fi";

    /// <summary>
    /// The Windows built-in bsdtar, by full path so a GNU tar earlier on PATH (for example Git for Windows) is never used. It must also accept
    /// every restricting extract option: that is probed once here with an empty archive (extracts nothing), so Prepare never needs a fallback.
    /// </summary>
    internal const string WindowsTarProbe =
        "$t = \"$env:SystemRoot\\System32\\tar.exe\"\n"
        + "$ok = (Test-Path -LiteralPath $t) -and (\"$(& $t --version 2>$null | Select-Object -First 1)\" -match 'bsdtar|libarchive')\n"
        + "if ($ok) { & $t -xf NUL --no-acls --no-xattrs --no-fflags --no-same-owner --no-same-permissions -C $env:TEMP 2>$null | Out-Null; $ok = ($LASTEXITCODE -eq 0) }\n"
        + "'tool=' + $(if ($ok) { 'ok' } else { 'missing' })";

    /// <summary>
    /// sh helpers for reclaim and unlock (they need $R). <c>deadline_of</c> prints a slot file's deadline as plain base-10 (leading zeros
    /// stripped, so "08" is 8 and never octal) and fails unless it is 1 to 18 digits. <c>take</c> creates the slot's lock file exclusively
    /// (set -C), first clearing one older than ten minutes (a reclaimer that died): the old lock is renamed to a unique name
    /// (atomic, so one clearer wins) and checked again there; if what was renamed turns out to be fresh (a live reclaimer's lock that
    /// replaced the old one in between), it is put back with its owner's token. Every lock holds its creator's unique token, and <c>drop</c>
    /// removes a lock only after renaming it away and finding its own token in it (otherwise it puts it back), so a reclaimer never removes
    /// another one's lock. sh has no atomic compare-and-delete, so a narrow window remains (see SECURITY.md). <c>stop_job</c> stops a stale slot's job if it is still
    /// running, through the shared <see cref="LinuxKillRecordedFunction"/> (the recorded group only while it is still the job's, matched by the
    /// leader's start time, so a pid that was reused by an unrelated process is never killed); without pid information nothing is killed.
    /// </summary>
    internal const string LinuxSlotFunctions = """
        deadline_of() { d=$(printf '%s\n' "$1" | sed -n 2p); case "$d" in ''|*[!0-9]*) return 1 ;; esac; [ ${#d} -le 18 ] || return 1; d=$(printf '%s' "$d" | sed 's/^0*//'); echo "${d:-0}"; }
        tok="$$.$(date +%s).$(od -An -N6 -tx1 /dev/urandom 2>/dev/null | tr -d ' \n')"
        take() {
          k="$1.reclaim"; o="$k.old.$$"
          if [ -n "$(find "$k" -mmin +10 2>/dev/null)" ] && mv "$k" "$o" 2>/dev/null; then
            if [ -n "$(find "$o" -mmin +10 2>/dev/null)" ]; then rm -f "$o"; else ( set -C; cat "$o" > "$k" ) 2>/dev/null; rm -f "$o"; fi
          fi
          ( set -C; printf '%s\n' "$tok" > "$k" ) 2>/dev/null
        }
        drop() {
          k="$1.reclaim"; o="$k.drop.$$"
          [ "$(cat "$k" 2>/dev/null)" = "$tok" ] && mv "$k" "$o" 2>/dev/null || return 0
          if [ "$(cat "$o" 2>/dev/null)" != "$tok" ]; then ( set -C; cat "$o" > "$k" ) 2>/dev/null; fi
          rm -f "$o"
        }
        stop_job() {
          case "$1" in ''|*[!A-Za-z0-9_-]*) return 0 ;; esac
          kill_recorded "$R/locks/job-$1.pid" "$R/jobs/$1" "$1"
        }
        """ + "\n" + LinuxKillRecordedFunction;

    /// <summary>
    /// The PowerShell helpers (they need $root): <c>Take</c> and <c>Drop</c> the slot's lock file the same way as the sh ones (CreateNew with a
    /// unique token; an abandoned lock renamed away and put back if fresh; Drop removes only a lock that still holds its own token), and <c>StopJob</c>, which kills a stale slot's still-running job (wrapper and child, with their trees) through
    /// <see cref="WindowsKillRecorded"/>: only processes whose pid AND start time match what the job recorded.
    /// </summary>
    internal static string WindowsSlotFunctions => """
        $tok="$PID."+[guid]::NewGuid().ToString('N');$IOFile=[IO.File]
        function Take($f){$k="$f.reclaim";$o="$k.old.$PID";$old=[DateTime]::UtcNow.AddMinutes(-10)
        try{if($IOFile::Exists($k) -and $IOFile::GetLastWriteTimeUtc($k) -lt $old){$IOFile::Move($k,$o);if($IOFile::GetLastWriteTimeUtc($o) -ge $old){try{$IOFile::Move($o,$k)}catch{}};$IOFile::Delete($o)}}catch{}
        try{$s=$IOFile::Open($k,'CreateNew','Write','None');$b=[Text.Encoding]::ASCII.GetBytes($tok);$s.Write($b,0,$b.Length);$s.Close();$true}catch{$false}}
        function Drop($f){$k="$f.reclaim";$o="$k.drop.$PID"
        try{if($IOFile::ReadAllText($k) -cne $tok){return};$IOFile::Move($k,$o);if($IOFile::ReadAllText($o) -cne $tok){try{$IOFile::Move($o,$k)}catch{}};$IOFile::Delete($o)}catch{}}
        function StopJob($j){$kp="$root/locks/job-$j.pid";if($j -cmatch '^[A-Za-z0-9_-]{1,64}\z' -and $IOFile::Exists($kp)){KillRecorded $kp $false}}
        """ + "\n" + WindowsKillRecorded;

    /// <summary>
    /// <c>KillRecorded $pf $legacy</c> (needs $root): the Windows job wrapper records pairs of "pid" and "process start time (UTC ticks)" in
    /// locks/job-ID.pid, first its own PowerShell, then the child that runs the job. The job object the wrapper put the child in (named after
    /// the pid file, see <see cref="WindowsJobHelper"/>) is terminated first when it still exists. Then each recorded process (child first) is
    /// stopped with its tree (<c>LqKill</c>: a toolhelp walk, no WMI; taskkill /T where the helper is not available) only when a process with
    /// that pid exists AND started at that very instant, so a pid that Windows has since given to another process (for example the owner's
    /// own PowerShell) is never touched, and an orphaned child whose wrapper died is still reached. A pid file without start times (an older
    /// lanq) is honoured only by cleanup right after its job (<paramref name="legacy"/>), with the old "is a PowerShell process" check; a
    /// stale-slot reclaim never kills on it.
    /// </summary>
    internal static string WindowsKillRecorded => WindowsHelperLoad("\"$root/bin/lanq-" + WindowsJobHelperHash[..10] + ".ps1\"") + "\n" + """
        function KillRecorded($pf,$lg){LqEndJob $pf
        $l=@(Get-Content -LiteralPath $pf -EA 0|%{"$_".Trim()});$tm=$false
        for($i=$l.Count-$l.Count%2-2;$i -ge 0;$i-=2){if($l[$i] -notmatch '^[0-9]{1,9}\z' -or $l[$i+1] -notmatch '^[0-9]{1,19}\z'){continue}
        $tm=$true;$p=Get-Process -Id $l[$i] -EA 0;$t=$null;if($p){try{$t="$($p.StartTime.ToUniversalTime().Ticks)"}catch{}}
        if($t -and $t -eq $l[$i+1]){LqKill $l[$i]}}
        if(!$tm -and $lg -and $l[0] -match '^[0-9]{1,9}\z'){$p=Get-Process -Id $l[0] -EA 0;if($p.ProcessName -match '^(powershell|pwsh)'){LqKill $l[0]}}
        Remove-Item -Force -EA 0 -LiteralPath $pf}
        """;

    /// <summary>How long past its own deadline a slot file must be before another job may reclaim it.</summary>
    internal const int StaleSlotGraceSeconds = 1800;

    /// <summary>
    /// The probe's <c>slots_stale=</c> on Windows (needs $sf, the probe's slot-N files, and $e, the worker's epoch now): how many slot-N files a claim would reclaim now, by the reclaim's own rules in
    /// <see cref="AcquireSlot"/> (a second line of 1 to 18 digits, and the worker's epoch now more than <see cref="StaleSlotGraceSeconds"/>
    /// past it). A slot without a deadline, with a malformed one, or still inside the grace is not stale.
    /// </summary>
    internal static string WindowsStaleSlotCount =>
        "'slots_stale='+@($sf|?{try{$x=[IO.File]::ReadAllText($_.FullName) -split \"`n\";$x.Count -ge 2 -and $x[1].Trim() -match '^[0-9]{1,18}\\z' -and $e -gt [long]$x[1].Trim()+"
        + StaleSlotGraceSeconds.ToString(CultureInfo.InvariantCulture) + "}catch{}}).Count";

    /// <summary>The probe's <c>slots_stale=</c> on Linux (needs $R): as <see cref="WindowsStaleSlotCount"/>, with deadline_of's rules (leading zeros stripped).</summary>
    internal static string LinuxStaleSlotCount =>
        "sn=0; ne=$(date +%s); for sf in \"$R\"/locks/slot-*; do case \"${sf##*/}\" in slot-|slot-*[!0-9]*) continue ;; esac; [ -f \"$sf\" ] || continue; "
        + "dl=$(sed -n 2p \"$sf\" 2>/dev/null); case \"$dl\" in ''|*[!0-9]*) continue ;; esac; [ ${#dl} -le 18 ] || continue; dl=$(printf '%s' \"$dl\" | sed 's/^0*//'); "
        + "if [ \"$ne\" -gt $((${dl:-0} + " + StaleSlotGraceSeconds.ToString(CultureInfo.InvariantCulture) + ")) ]; then sn=$((sn+1)); fi; done; echo \"slots_stale=$sn\"";

    /// <summary>
    /// Added to the job timeout for a slot's deadline: ensure, upload and prepare (twice with a full-bundle fallback), the --fetch step
    /// (<see cref="JobRunner.FetchTimeout"/>) and cleanup.
    /// </summary>
    internal static readonly TimeSpan SlotTransferAllowance = TimeSpan.FromMinutes(50);

    /// <summary>
    /// Atomically claims one of max_jobs slot files. Prints SLOT=n or SLOT=FULL. The slot file holds the job id and, on its second line,
    /// its deadline in the worker's own epoch seconds (now + <paramref name="jobTimeout"/> + <see cref="SlotTransferAllowance"/>). A slot
    /// more than <see cref="StaleSlotGraceSeconds"/> past its deadline was left by a crashed run and is reclaimed under that slot's lock
    /// file (<c>slot-N.reclaim</c>, created exclusively): the slot is read again there and only if it is still the stale file, its job is
    /// stopped if it is somehow still running (see <see cref="LinuxSlotFunctions"/>), then it is renamed away to a unique name and deleted,
    /// and the slot is created afresh like any free one (another claimer may win it; then the next slot is tried). There is no step that
    /// puts a file back. A slot without a deadline, or with a deadline that is not 1 to 18 decimal digits, is never reclaimed.
    /// </summary>
    public static string AcquireSlot(Machine m, string jobId, TimeSpan jobTimeout)
    {
        var max = m.MaxJobs.ToString(CultureInfo.InvariantCulture);
        var lease = ((long)(jobTimeout + SlotTransferAllowance).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var grace = StaleSlotGraceSeconds.ToString(CultureInfo.InvariantCulture);
        return m.Os == MachineOs.Windows
            ? $$"""
                $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
                [void][IO.Directory]::CreateDirectory("$root/locks")
                $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();$deadline=$now+{{lease}}
                {{WindowsSlotFunctions}}
                for($i=1;$i -le {{max}};$i++){
                  $f="$root/locks/slot-$i"
                  try {
                    $c=$IOFile::ReadAllText($f);$l=$c -split "`n"
                    if ($l.Count -ge 2 -and $l[1].Trim() -match '^[0-9]{1,18}\z' -and $now -gt ([long]$l[1].Trim()+{{grace}}) -and (Take $f)) {
                      try{if($IOFile::ReadAllText($f) -ceq $c){StopJob $l[0].Trim();$t="$f.stale.$PID";$IOFile::Move($f,$t);$IOFile::Delete($t)} }catch{}finally{Drop $f}
                    }
                  } catch { }
                  try {
                    $fs=$IOFile::Open($f,'CreateNew','Write','None')
                    $b=[Text.Encoding]::UTF8.GetBytes({{ShellQuote.PowerShell(jobId)}}+"`n"+$deadline+"`n");$fs.Write($b,0,$b.Length);$fs.Close()
                    "SLOT=$i";exit 0
                  }catch [IO.IOException]{}
                }
                'SLOT=FULL'
                """
            : $$"""
                R={{ShellQuote.Posix(m.WorkRoot)}}
                mkdir -p "$R/locks"
                now=$(date +%s); D=$((now + {{lease}}))
                {{LinuxSlotFunctions}}
                i=1
                while [ "$i" -le {{max}} ]; do
                  f="$R/locks/slot-$i"
                  if [ -f "$f" ]; then
                    c=$(cat "$f" 2>/dev/null)
                    if d=$(deadline_of "$c") && [ "$now" -gt $((d + {{grace}})) ] && take "$f"; then
                      if [ "$(cat "$f" 2>/dev/null)" = "$c" ]; then
                        stop_job "$(printf '%s\n' "$c" | sed -n 1p)"
                        mv "$f" "$f.stale.$$" 2>/dev/null && rm -f "$f.stale.$$"
                      fi
                      drop "$f"
                    fi
                  fi
                  if ( set -C; printf '%s\n%s\n' {{ShellQuote.Posix(jobId)}} "$D" > "$f" ) 2>/dev/null; then echo "SLOT=$i"; exit 0; fi
                  i=$((i+1))
                done
                echo "SLOT=FULL"
                """;
    }

    /// <summary>
    /// <see cref="WindowsSlotFunctions"/> (lanq's own constant text) deflated and dot-sourced, so <see cref="AcquireAll"/> with its extra
    /// back-out and higher-slot checks stays under the Windows command-line cap; it defines the same functions and variables in the same scope.
    /// </summary>
    private static readonly string WindowsSlotFunctionsPacked =
        ". ([scriptblock]::Create((New-Object IO.StreamReader (New-Object IO.Compression.DeflateStream ([IO.MemoryStream][Convert]::FromBase64String('"
        + Deflated(WindowsSlotFunctions) + "')), ([IO.Compression.CompressionMode]::Decompress))).ReadToEnd()))";

    /// <summary>
    /// lanq run --exclusive: claims EVERY slot file, slot-1 to slot-max_jobs in ascending order, each exactly as <see cref="AcquireSlot"/>
    /// claims one (the same atomic create, the same content: job id and deadline, and the same stale reclaim under the slot's lock file).
    /// Prints <c>SLOTS=1,2,...,n</c> when it holds them all. At the first slot it cannot create it removes the slots this attempt created
    /// (each only if its first line is still this job id) and prints <c>SLOT=FULL</c>: all or nothing. The ascending order means two
    /// exclusive claims never deadlock; at worst both back off and one wins on a later look. Each slot file is an ordinary slot, so the
    /// probe, <see cref="Unlock"/> and a later stale reclaim (slot by slot) treat them as they treat any other.
    /// Once it holds them all it also looks above max_jobs: a live slot-N with N above max_jobs (another lanq with a higher max_jobs for the
    /// same work_root runs a job there; one past its deadline plus the grace does not count) makes it back out and print <c>SLOT=FULL</c> too.
    /// </summary>
    public static string AcquireAll(Machine m, string jobId, TimeSpan jobTimeout)
    {
        var max = m.MaxJobs.ToString(CultureInfo.InvariantCulture);
        var lease = ((long)(jobTimeout + SlotTransferAllowance).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var grace = StaleSlotGraceSeconds.ToString(CultureInfo.InvariantCulture);
        // The sh loop keeps its own names (sf, n, nn, got): stop_job's kill_recorded used f and g as globals once.
        return m.Os == MachineOs.Windows
            ? $$"""
                $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
                [void][IO.Directory]::CreateDirectory("$root/locks")
                $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();$deadline=$now+{{lease}}
                {{WindowsSlotFunctionsPacked}}
                $jid={{ShellQuote.PowerShell(jobId)}};$got=@()
                function BO{foreach($n in $got){$g="$root/locks/slot-$n";try{if(($IOFile::ReadAllText($g) -split "`n")[0].Trim() -ceq $jid){$IOFile::Delete($g)} }catch{} };'SLOT=FULL';exit 0}
                function HL{foreach($h in (gci "$root/locks" -Filter slot-* -EA 0)){if($h.Name -notmatch '^slot-0*([0-9]+)\z' -or ($Matches[1].Length -lt 10 -and [long]$Matches[1] -le {{max}})){continue};try{$x=$IOFile::ReadAllText($h.FullName) -split "`n"}catch [IO.FileNotFoundException]{continue}catch{return 1};if(!($x.Count -ge 2 -and $x[1].Trim() -match '^[0-9]{1,18}\z' -and $now -gt ([long]$x[1].Trim()+{{grace}}))){return 1} };0}
                for($i=1;$i -le {{max}};$i++){
                  $f="$root/locks/slot-$i"
                  try {
                    $c=$IOFile::ReadAllText($f);$l=$c -split "`n"
                    if ($l.Count -ge 2 -and $l[1].Trim() -match '^[0-9]{1,18}\z' -and $now -gt ([long]$l[1].Trim()+{{grace}}) -and (Take $f)) {
                      try{if($IOFile::ReadAllText($f) -ceq $c){StopJob $l[0].Trim();$t="$f.stale.$PID";$IOFile::Move($f,$t);$IOFile::Delete($t)} }catch{}finally{Drop $f}
                    }
                  } catch { }
                  $ok=$false
                  try {
                    $fs=$IOFile::Open($f,'CreateNew','Write','None')
                    $b=[Text.Encoding]::UTF8.GetBytes($jid+"`n"+$deadline+"`n");$fs.Write($b,0,$b.Length);$fs.Close();$ok=$true
                  }catch [IO.IOException]{}
                  if($ok){$got+=$i;continue}
                  BO
                }
                if(HL){BO}
                'SLOTS='+($got -join ',')
                """
            : $$"""
                R={{ShellQuote.Posix(m.WorkRoot)}}
                mkdir -p "$R/locks"
                now=$(date +%s); D=$((now + {{lease}}))
                {{LinuxSlotFunctions}}
                J={{ShellQuote.Posix(jobId)}}; got=
                back_out() {
                  n=1
                  while [ "$n" -lt "$i" ]; do
                    sf="$R/locks/slot-$n"
                    if [ "$(sed -n 1p "$sf" 2>/dev/null)" = "$J" ]; then rm -f "$sf"; fi
                    n=$((n+1))
                  done
                  echo "SLOT=FULL"; exit 0
                }
                higher_live() {
                  for sf in "$R"/locks/slot-*; do
                    case "${sf##*/}" in slot-|slot-*[!0-9]*) continue ;; esac
                    [ -f "$sf" ] || continue
                    nn=$(printf '%s' "${sf##*/slot-}" | sed 's/^0*//')
                    if [ ${#nn} -le 9 ] && [ "${nn:-0}" -le {{max}} ]; then continue; fi
                    if d=$(deadline_of "$(cat "$sf" 2>/dev/null)") && [ "$now" -gt $((d + {{grace}})) ]; then continue; fi
                    return 0
                  done
                  return 1
                }
                i=1
                while [ "$i" -le {{max}} ]; do
                  sf="$R/locks/slot-$i"
                  if [ -f "$sf" ]; then
                    c=$(cat "$sf" 2>/dev/null)
                    if d=$(deadline_of "$c") && [ "$now" -gt $((d + {{grace}})) ] && take "$sf"; then
                      if [ "$(cat "$sf" 2>/dev/null)" = "$c" ]; then
                        stop_job "$(printf '%s\n' "$c" | sed -n 1p)"
                        mv "$sf" "$sf.stale.$$" 2>/dev/null && rm -f "$sf.stale.$$"
                      fi
                      drop "$sf"
                    fi
                  fi
                  if ( set -C; printf '%s\n%s\n' "$J" "$D" > "$sf" ) 2>/dev/null; then got="$got${got:+,}$i"; i=$((i+1)); continue; fi
                  back_out
                done
                if higher_live; then back_out; fi
                echo "SLOTS=$got"
                """;
    }

    /// <summary>
    /// The tool command lanq doctor --remote runs for each capability it knows (Linux sh, Windows PowerShell). Version probes only: Go is
    /// pinned to its local toolchain so the probe can never download one.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Linux, string Windows)> ToolChecks =
        new Dictionary<string, (string Linux, string Windows)>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = ("dotnet --version", "dotnet --version"),
            ["node"] = ("node --version", "node --version"),
            ["npm"] = ("npm --version", "npm --version"),
            ["python"] = ("python3 --version 2>&1 || python --version", "if (Get-Command python3 -ErrorAction SilentlyContinue) { python3 --version } else { python --version }"),
            ["go"] = ("GOTOOLCHAIN=local go version", "$env:GOTOOLCHAIN = 'local'; go version"),
            ["rust"] = ("RUSTUP_AUTO_INSTALL=0 cargo --version", "$env:RUSTUP_AUTO_INSTALL = '0'; cargo --version"),
            ["java"] = ("java -version", "java -version"),
            ["docker"] = ("docker version --format '{{.Server.Version}}'", "docker version --format '{{.Server.Version}}'"),
        };

    /// <summary>
    /// lanq doctor --remote, one ssh call per machine. Prints <c>root=ok|readonly|missing</c> (work_root writable, checked with a temporary
    /// file that is removed again; a missing work_root is not created), <c>tool=ok|missing</c> (git or tar, as the health probe checks), and
    /// <c>cap.NAME=EXITCODE|FIRST LINE</c> for each capability in <see cref="ToolChecks"/>, run in the job environment: the machine's
    /// env: and path_prepend: applied exactly as for a job, in the same non-login shell. Each check may take <paramref name="checkSeconds"/>:
    /// on Linux it runs under <c>timeout</c> (when that works), on Windows in a child PowerShell killed with its tree when the wait runs
    /// out (with the worker's helper, <see cref="WindowsJobHelper"/>, by a process snapshot walk that needs no WMI); a check stopped that way
    /// prints <c>cap.NAME=timeout|</c> and the next check runs.
    /// </summary>
    public static string Doctor(Machine m, int checkSeconds = 20)
    {
        var caps = m.Capabilities.Where(ToolChecks.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seconds = Math.Max(checkSeconds, 1).ToString(CultureInfo.InvariantCulture);
        if (m.Os == MachineOs.Windows)
        {
            var checks = string.Join('\n', caps.Select(c => $"Cap {ShellQuote.PowerShell(c.ToLowerInvariant())} {ShellQuote.PowerShell(ToolChecks[c].Windows)}"));
            return $$"""
                $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
                if (Test-Path -LiteralPath $root -PathType Container) {
                  try { $t = "$root/.lanq-doctor.$PID"; [IO.File]::WriteAllText($t, ''); [IO.File]::Delete($t); 'root=ok' } catch { 'root=readonly' }
                } else { 'root=missing' }
                {{(m.Transfer == TransferMode.Archive ? WindowsTarProbe : GitProbeWindows + "\n" + GitVersionWindows)}}
                {{JobEnvironmentPowerShell(m)}}
                $ErrorActionPreference = 'Continue'
                {{WindowsHelperLoad(ShellQuote.PowerShell(WindowsHelperPath(m)))}}
                function Cap([string]$n, [string]$code) {
                  $si = New-Object System.Diagnostics.ProcessStartInfo ((Get-Process -Id $PID).Path)
                  $si.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('$global:LASTEXITCODE = 0; try { ' + $code + ' } catch { Write-Output "$_"; exit 1 }; exit $LASTEXITCODE'))
                  $si.UseShellExecute = $false; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true; $si.RedirectStandardInput = $true
                  try { $p = [Diagnostics.Process]::Start($si) } catch { "cap.$n=1|$_"; return }
                  $p.StandardInput.Close(); $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
                  if (-not $p.WaitForExit({{seconds}}000)) { LqKill $p.Id; "cap.$n=timeout|"; return }
                  $p.WaitForExit()
                  "cap.$n=$($p.ExitCode)|" + "$(($o.Result + "`n" + $e.Result) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)"
                }
                {{checks}}
                """;
        }

        var env = JobEnvironmentPosix(m);
        var lines = caps.Select(c => c.ToLowerInvariant()).Select(c =>
            // timeout exits 124 when TERM stopped the check, and 137 (128 + KILL) when the tool ignored TERM and -k had to kill it; 137 counts
            // as a timeout only when the check really ran for its whole limit (an OOM-killed tool exits 137 too, but early).
            $"s=$(date +%s); o=$($T {env}/bin/sh -c {ShellQuote.Posix(ToolChecks[c].Linux)} </dev/null 2>&1); rc=$?; "
            + $"if [ -n \"$T\" ] && {{ [ \"$rc\" = 124 ] || {{ [ \"$rc\" = 137 ] && [ $(( $(date +%s) - s )) -ge {seconds} ]; }}; }}; then echo \"cap.{c}=timeout|\"; "
            + $"else echo \"cap.{c}=$rc|$(printf '%s\\n' \"$o\" | sed -n '/./{{p;q;}}')\"; fi");
        return $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            if [ -d "$R" ]; then
              t="$R/.lanq-doctor.$$"
              if ( : > "$t" ) 2>/dev/null; then rm -f "$t"; echo root=ok; else echo root=readonly; fi
            else echo root=missing; fi
            {{(m.Transfer == TransferMode.Archive ? LinuxTarProbe : GitProbeLinux + "\n" + GitVersionLinux)}}
            T=; if timeout -k 5 30 true >/dev/null 2>&1; then T="timeout -k 5 {{seconds}}"; fi
            {{string.Join('\n', lines)}}
            """;
    }

    /// <summary>
    /// lanq unlock. Prints <c>REMOVED=&lt;file&gt; &lt;job id&gt;</c> or <c>KEPT=&lt;file&gt; live|nodeadline|changed</c> per slot file.
    /// Without <paramref name="all"/> only a <c>slot-N</c> past its recorded deadline is removed, the same way a stale slot is reclaimed
    /// (under its lock, re-checked, its job stopped if somehow still running, renamed away and deleted); no grace period, since a person asked. With <paramref name="all"/>
    /// every <c>slot-*</c> file is removed, including one without a deadline and a reclaim's transient file.
    /// </summary>
    public static string Unlock(Machine m, bool all) => (m.Os, all) switch
    {
        (MachineOs.Windows, true) => $$"""
            # unlock --all
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
            foreach ($f in @(Get-ChildItem -LiteralPath "$root/locks" -Filter 'slot-*' -File -ErrorAction SilentlyContinue)) {
              $j = ''; try { $j = ([IO.File]::ReadAllText($f.FullName) -split "`n")[0].Trim() } catch { }
              try { [IO.File]::Delete($f.FullName); 'REMOVED=' + $f.Name + ' ' + $j } catch { 'KEPT=' + $f.Name + ' changed' }
            }
            """,
        (MachineOs.Windows, false) => $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
            $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            {{WindowsSlotFunctions}}
            foreach($f in @(Get-ChildItem -LiteralPath "$root/locks" -Filter 'slot-*' -File -EA 0)){
              $n=$f.Name;$p=$f.FullName
              if ($n -notmatch '^slot-[0-9]+\z') { continue }
              try{$c=$IOFile::ReadAllText($p)}catch{continue}
              $l=$c -split "`n"
              if ($l.Count -lt 2 -or $l[1].Trim() -notmatch '^[0-9]{1,18}\z') { "KEPT=$n nodeadline"; continue }
              if ($now -le [long]$l[1].Trim()) { "KEPT=$n live"; continue }
              if (-not (Take $p)) { "KEPT=$n changed"; continue }
              try {
                if ($IOFile::ReadAllText($p) -ceq $c){StopJob $l[0].Trim();$t="$p.stale.$PID";$IOFile::Move($p,$t);$IOFile::Delete($t);'REMOVED='+$n+' '+$l[0].Trim()}
                else { "KEPT=$n changed" }
              } catch { "KEPT=$n changed" } finally { Drop $p }
            }
            """,
        (_, true) => $$"""
            # unlock --all
            R={{ShellQuote.Posix(m.WorkRoot)}}
            for f in "$R"/locks/slot-*; do
              [ -f "$f" ] || continue
              j=$(sed -n 1p "$f" 2>/dev/null)
              if rm -f "$f"; then echo "REMOVED=${f##*/} $j"; else echo "KEPT=${f##*/} changed"; fi
            done
            """,
        _ => $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            now=$(date +%s)
            {{LinuxSlotFunctions}}
            for f in "$R"/locks/slot-*; do
              [ -f "$f" ] || continue
              n=${f##*/}
              case "$n" in slot-|slot-*[!0-9]*) continue ;; esac
              c=$(cat "$f" 2>/dev/null)
              d=$(deadline_of "$c") || { echo "KEPT=$n nodeadline"; continue; }
              if [ "$now" -le "$d" ]; then echo "KEPT=$n live"; continue; fi
              take "$f" || { echo "KEPT=$n changed"; continue; }
              if [ "$(cat "$f" 2>/dev/null)" = "$c" ]; then
                stop_job "$(printf '%s\n' "$c" | sed -n 1p)"
                mv "$f" "$f.stale.$$" 2>/dev/null && rm -f "$f.stale.$$"
                echo "REMOVED=$n $(printf '%s\n' "$c" | sed -n 1p)"
              else
                echo "KEPT=$n changed"
              fi
              drop "$f"
            done
            """,
    };

    /// <summary>
    /// Git mode: verifies the uploaded bundle against the bare per-repository cache <c>repos/&lt;repoId&gt;.git</c> (printing
    /// <see cref="BundleNotUsable"/> and failing if its prerequisites are missing), fetches it, moves <c>refs/lanq/base</c> to the snapshot
    /// (keeping it alive as the next incremental base) unless the snapshot is an ancestor of the current base (a lanq run --baseline rerun of an
    /// older commit: the newer base stays, so the next run's bundle stays small), and adds a detached per-job worktree. Archive mode ignores <paramref name="repoId"/>.
    /// </summary>
    /// <param name="bundleRef">The id the bundle carries the snapshot under (refs/lanq/&lt;bundleRef&gt;): the payload id shared by every shard of one
    /// run; null means <paramref name="jobId"/>. The job's own ref in the cache is always refs/lanq/&lt;jobId&gt;.</param>
    public static string Prepare(Machine m, string jobId, string? repoId = null, string? bundleRef = null) =>
        m.Transfer == TransferMode.Archive ? PrepareArchive(m, jobId) : PrepareGit(m, jobId, SafeRepoId(repoId), SafeJobId(bundleRef ?? jobId));

    /// <summary>
    /// Extracts the uploaded tar.gz into a fresh per-job folder with the platform's own tar. Only GNU tar or bsdtar is accepted. The archive is
    /// listed first and refused before anything is written if any entry is absolute, contains '..' or a backslash (on Windows also names that
    /// Windows cannot store), or is anything but a plain file or directory (a verbose-listing line showing a link is refused as well).
    /// </summary>
    private static string PrepareArchive(Machine m, string jobId) => m.Os == MachineOs.Windows
        ? $$"""
            $ErrorActionPreference = 'Stop'
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            $tar = "$env:SystemRoot\System32\tar.exe"
            if (-not (Test-Path -LiteralPath $tar)) { Write-Output 'unsupported tar: the Windows built-in tar.exe was not found'; exit 1 }
            if ("$(& $tar --version 2>$null | Select-Object -First 1)" -notmatch 'bsdtar|libarchive') { Write-Output 'unsupported tar: need the Windows built-in bsdtar'; exit 1 }
            $a = "$root/incoming/$id.tar.gz"; $j = "$root/jobs/$id"
            $names = @(& $tar -tzf $a); if ($LASTEXITCODE -ne 0) { exit 1 }
            foreach ($n in $names) { if ($n -match '^[/\\]|\\|[<>:"|?*]|(^|/)\.\.(/|$)|(^|/)[^/]*[. ](/|$)|(^|/)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.[^/]*)?(/|$)') { Write-Output "unsafe archive entry: $n"; exit 1 } }
            $kinds = @(& $tar -tvzf $a); if ($LASTEXITCODE -ne 0) { exit 1 }
            foreach ($k in $kinds) { if ($k -notmatch '^[-d]' -or $k -match ' link to | -> ') { Write-Output "unsafe archive entry type: $k"; exit 1 } }
            if (Test-Path -LiteralPath $j) { exit 1 }
            New-Item -ItemType Directory -Force -Path $j | Out-Null
            $ErrorActionPreference = 'Continue'
            & $tar -xzf $a -C $j --no-same-owner --no-same-permissions --no-acls --no-xattrs --no-fflags 2>&1 | ForEach-Object { "$_" }
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            exit 0
            """
        : $$"""
            set -e
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            A="$R/incoming/$ID.tar.gz"; J="$R/jobs/$ID"; L="$R/incoming/$ID.list"
            {{LinuxTarSupportedFunction}}
            tar_supported || { echo "unsupported tar (need GNU tar or bsdtar, and gzip)"; exit 1; }
            tar -tzf "$A" > "$L"
            while IFS= read -r e; do
              case "$e" in /*|..|../*|*/..|*/../*|*\\*) echo "unsafe archive entry: $e"; exit 1 ;; esac
            done < "$L"
            tar -tvzf "$A" > "$L"
            while IFS= read -r e; do
              case "$e" in
                *" link to "*|*" -> "*) echo "unsafe archive entry link: $e"; exit 1 ;;
                -*|d*) ;;
                *) echo "unsafe archive entry type: $e"; exit 1 ;;
              esac
            done < "$L"
            rm -f "$L"
            mkdir -p "$R/jobs"
            mkdir "$J"
            tar -xzf "$A" -C "$J" --no-same-owner --no-same-permissions
            """;

    private static string PrepareGit(Machine m, string jobId, string repoId, string bundleRef) => m.Os == MachineOs.Windows
        ? $$"""
            $ErrorActionPreference = 'Stop'
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}; $b = {{ShellQuote.PowerShell(bundleRef)}}
            $c = "$root/repos/{{repoId}}.git"
            function Q { $env:GIT_CONFIG_NOSYSTEM = '1'; $env:GIT_CONFIG_GLOBAL = 'NUL'; & git {{GitHardeningWindows}} @args }
            function G { Q @args; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
            New-Item -ItemType Directory -Force -Path "$root/jobs", "$root/repos" | Out-Null
            $ErrorActionPreference = 'Continue'
            if (Test-Path -LiteralPath $c) {
              $keys = @(Q config --file "$c/config" --name-only --list 2>$null); $read = $LASTEXITCODE -eq 0
              $odd = @(Get-ChildItem -LiteralPath $c -Force -Name -ErrorAction SilentlyContinue | Where-Object { $_ -cnotmatch '^({{CacheTopLevel}})\z' })
              if (-not $read -or $odd.Count -gt 0 -or @($keys | Where-Object { $_ -and $_ -cnotmatch '^({{CacheConfigKeys}})\z' }).Count -gt 0) {
                'lanq: {{CacheRecreated}}'
                cmd /c rd /s /q ($c.Replace('/', '\')) 2>$null | Out-Null
                if (Test-Path -LiteralPath $c) { 'lanq: could not remove the worker cache'; exit 1 }
              }
            }
            $ErrorActionPreference = 'Stop'
            if (-not (Test-Path -LiteralPath $c)) { G init --bare -q $c }
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue -LiteralPath "$c/info/attributes"
            $ErrorActionPreference = 'Continue'
            Q -C $c bundle verify "$root/incoming/$id.bundle" 2>&1 | Out-Null
            $ok = $LASTEXITCODE -eq 0
            $ErrorActionPreference = 'Stop'
            if (-not $ok) { '{{BundleNotUsable}}'; exit 3 }
            G -c protocol.file.allow=always -C $c fetch -q "$root/incoming/$id.bundle" "refs/lanq/${b}:refs/lanq/${id}"
            $ErrorActionPreference = 'Continue'
            Q -C $c merge-base --is-ancestor "refs/lanq/$id" refs/lanq/base 2>&1 | Out-Null
            $older = $LASTEXITCODE -eq 0
            $ErrorActionPreference = 'Stop'
            if (-not $older) { G -C $c update-ref refs/lanq/base "refs/lanq/$id" }
            G -C $c worktree add -q --detach "$root/jobs/$id" "refs/lanq/$id"
            """
        : $$"""
            set -e
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}; B={{ShellQuote.Posix(bundleRef)}}; C="$R/repos/{{repoId}}.git"
            mkdir -p "$R/jobs" "$R/repos"
            {{LinuxGitFunction}}
            if [ -d "$C" ]; then
              k=$(G config --file "$C/config" --name-only --list 2>/dev/null) && [ -z "$(printf '%s\n' "$k" | grep -v -x -E '{{CacheConfigKeys}}' | grep -v -x '')" ] \
                && e=$(ls -A "$C") && [ -z "$(printf '%s\n' "$e" | grep -v -x -E '{{CacheTopLevel}}' | grep -v -x '')" ] \
                || { echo 'lanq: {{CacheRecreated}}'; rm -rf "$C"; }
            fi
            [ -d "$C" ] || G init --bare -q "$C"
            rm -rf "$C/info/attributes"
            G -C "$C" bundle verify "$R/incoming/$ID.bundle" >/dev/null 2>&1 || { echo '{{BundleNotUsable}}'; exit 3; }
            G -c protocol.file.allow=always -C "$C" fetch -q "$R/incoming/$ID.bundle" "refs/lanq/$B:refs/lanq/$ID"
            G -C "$C" merge-base --is-ancestor "refs/lanq/$ID" refs/lanq/base >/dev/null 2>&1 || G -C "$C" update-ref refs/lanq/base "refs/lanq/$ID"
            G -C "$C" worktree add -q --detach "$R/jobs/$ID" "refs/lanq/$ID"
            """;

    /// <summary>
    /// Printed by the git prepare step when the cache cannot take the uploaded bundle (<c>git bundle verify</c> failed: for an incremental
    /// bundle, the worker no longer has the base). The runner then sends a full bundle, once.
    /// </summary>
    internal const string BundleNotUsable = "lanq: bundle not usable by the worker cache (missing prerequisites)";

    public static string Incoming(Machine m, string jobId) => $"{m.WorkRoot}/incoming/{jobId}{(m.Transfer == TransferMode.Archive ? ".tar.gz" : ".bundle")}";

    /// <summary>Creates incoming/ before an upload; on Windows also writes the job wrapper's helper (<see cref="WindowsInstallHelper"/>).</summary>
    public static string EnsureIncoming(Machine m) => m.Os == MachineOs.Windows
        ? $"New-Item -ItemType Directory -Force -Path ({ShellQuote.PowerShell(m.WorkRoot + "/incoming")}) | Out-Null\n" + WindowsInstallHelper(m)
        : $"mkdir -p {ShellQuote.Posix(m.WorkRoot + "/incoming")}";

    /// <summary>
    /// Runs the allowlisted argv inside the job worktree. Each argument is individually quoted. The directory is the
    /// normalized one (never the raw input) and its real path must stay inside the worktree. The job's process group
    /// (Linux, via setsid) or PowerShell PID (Windows) is recorded in locks/job-ID.pid so cleanup can kill it. With a
    /// <paramref name="limit"/> the worker also stops the job itself: on Linux under <c>timeout -k 30</c> when that works (exit 124), on
    /// Windows by running it in a child PowerShell and ending that tree when the wait runs out (exit 124). On Windows the child runs in a job
    /// object (<see cref="WindowsJobHelper"/>, loaded from the file <see cref="EnsureIncoming"/> wrote): the timeout terminates it, and it ends
    /// by itself when the wrapper dies; without the helper the tree is stopped with taskkill /T (which needs WMI) and Stop-Process.
    /// </summary>
    /// <param name="shard">lanq run --shards: sets LANQ_SHARD and LANQ_SHARDS for the job (next to LANQ_JOB), or null.</param>
    /// <param name="planJob">lanq run --plan: sets LANQ_PLAN_JOB to the plan job's name, or null.</param>
    /// <param name="nonce">
    /// This run's <see cref="NewMarkerNonce"/>, put into the wrapper's own marker lines (<see cref="ExitMarker"/>, <see cref="WindowsArgsMarker"/>)
    /// and nowhere else (not in the job's environment), so a job that prints a marker-like line cannot pass it off as the wrapper's. Null
    /// leaves it out (tests that run the wrapper by hand).
    /// </param>
    /// <param name="env">
    /// lanq run --env: the job's own variables (already checked: <see cref="JobEnv"/>), set in the given order after the machine's env: and
    /// before LANQ_JOB, LANQ_SHARD(S) and LANQ_PLAN_JOB, so lanq's own variables always win. Null or empty sets none.
    /// </param>
    public static string Execute(Machine m, string jobId, string directory, IReadOnlyList<string> argv, TimeSpan? limit = null, ShardInfo? shard = null, string? planJob = null,
        string? nonce = null, IReadOnlyList<JobEnvVar>? env = null)
    {
        var rel = JobPolicy.Normalize(directory) ?? throw new ArgumentException("Directory is not a safe relative path.", nameof(directory));
        var worktree = $"{m.WorkRoot}/jobs/{jobId}";
        var dir = rel == "." ? worktree : worktree + "/" + rel;
        var pidFile = $"{m.WorkRoot}/locks/job-{jobId}.pid";
        if (m.Os == MachineOs.Windows)
        {
            var windows = JobPolicy.ValidateForWindows(argv);
            if (!windows.Ok)
            {
                throw new InvalidOperationException(windows.Reason);
            }

            var parts = string.Join(' ', argv.Select(ShellQuote.PowerShell));
            return $$"""
                $pf={{ShellQuote.PowerShell(pidFile)}}
                $wr=(Resolve-Path -LiteralPath {{ShellQuote.PowerShell(worktree)}}).ProviderPath
                Set-Location -LiteralPath {{ShellQuote.PowerShell(dir)}}
                $here=(Get-Location).ProviderPath;$sep=[IO.Path]::DirectorySeparatorChar
                if(!(($here+$sep).StartsWith($wr+$sep,'OrdinalIgnoreCase'))){exit 126}
                $p=Get-Item -LiteralPath $here
                while($p.FullName.Length -gt $wr.Length){if($p.Attributes -band [IO.FileAttributes]::ReparsePoint){exit 126};$p=$p.Parent}
                {{JobEnvironmentPowerShell(m)}}
                {{JobEnvPowerShell(env)}}
                $env:{{JobMarker}} = {{ShellQuote.PowerShell(SafeJobId(jobId))}}
                {{ShardEnvironmentPowerShell(shard)}}
                {{PlanJobEnvironmentPowerShell(planJob)}}
                {{WindowsNativeCheck(jobId, argv, nonce)}}
                {{(m.Priority == JobPriority.Low ? "try{(Get-Process -Id $PID).PriorityClass = 'BelowNormal'}catch{}" : "")}}
                {{WindowsHelperLoad(ShellQuote.PowerShell(WindowsHelperPath(m)), job: true)}}
                $me=Get-Process -Id $PID
                Set-Content -LiteralPath $pf @($PID,$me.StartTime.ToUniversalTime().Ticks)
                $job={
                & {{parts}}
                }
                $inner='$ProgressPreference=''SilentlyContinue'';$global:LASTEXITCODE=0'+"`ntry{`n"+$job.ToString()+"`nexit `$LASTEXITCODE`n"+'}catch{Write-Output "$_";exit 1}'
                $si=[Diagnostics.ProcessStartInfo]::new($me.Path)
                $si.Arguments='-NoProfile -NonInteractive -ep Bypass -ec '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
                $si.UseShellExecute=$false;$si.WorkingDirectory=$here;$code=1
                try{
                  $cp=[Diagnostics.Process]::Start($si)
                  $jo=if($LqW){LqJob $pf $cp.Handle}
                  Add-Content -LiteralPath $pf @($cp.Id,$cp.StartTime.ToUniversalTime().Ticks)
                  if($cp.WaitForExit({{WindowsWaitMilliseconds(limit)}})){$cp.WaitForExit();$code=$cp.ExitCode}
                  else{$code=124;LqKill $cp.Id;if($jo){[void]$LqW::TerminateJobObject($jo,124)} }
                }finally{Remove-Item -Force -EA 0 -LiteralPath $pf}
                [Console]::Out.WriteLine({{ShellQuote.PowerShell(ExitMarker(jobId, nonce))}} + $code)
                exit $code
                """;
        }

        // The job runs in the background so its pid (with setsid: its session and process group; with timeout alone: timeout's own group)
        // is known here and recorded for cleanup; stdin is handed through on fd 3 since a background command would otherwise get /dev/null.
        return $$"""
            cd -P {{ShellQuote.Posix(dir)}} || exit 126
            case "$(pwd -P)/" in "$(cd -P {{ShellQuote.Posix(worktree)}} && pwd -P)"/*) ;; *) exit 126 ;; esac
            P={{ShellQuote.Posix(pidFile)}}
            S=; if command -v setsid >/dev/null 2>&1; then S="setsid -w"; fi
            T={{(limit is { } l ? $"; if timeout -k 1 30 true >/dev/null 2>&1; then T=\"timeout -k 30 {WholeSeconds(l)}\"; fi" : "")}}
            {{(m.Priority == JobPriority.Low ? LinuxLowPriority : "")}}
            exec 3<&0
            $S $T {{(m.Priority == JobPriority.Low ? "$N $I " : "")}}{{JobEnvironmentPosix(m, jobId, shard, planJob, env)}}/bin/sh -c 'exec "$@"' sh {{string.Join(' ', argv.Select(ShellQuote.Posix))}} <&3 3<&- &
            j=$!
            printf '%s\n%s\n%s\n' "$j" "$(sed -n 's/.*) //p' "/proc/$j/stat" 2>/dev/null | cut -d' ' -f20)" "$(cat /proc/sys/kernel/random/boot_id 2>/dev/null)" > "$P"
            wait "$j"
            rc=$?
            rm -f "$P"
            printf '%s%s\n' {{ShellQuote.Posix(ExitMarker(jobId, nonce))}} "$rc"
            exit $rc
            """;
    }

    private static string WholeSeconds(TimeSpan t) => ((long)Math.Ceiling(Math.Max(t.TotalSeconds, 1))).ToString(CultureInfo.InvariantCulture);

    /// <summary>Process.WaitForExit milliseconds for the job's own deadline; -1 waits for ever (no limit given).</summary>
    private static string WindowsWaitMilliseconds(TimeSpan? limit) =>
        limit is { } l ? (long.Parse(WholeSeconds(l), CultureInfo.InvariantCulture) * 1000).ToString(CultureInfo.InvariantCulture) : "-1";

    /// <summary>
    /// priority: low on Linux. nice and ionice are used only when present and when they actually work for this user (idle I/O class can be
    /// refused on old kernels); otherwise the job simply runs at normal priority.
    /// </summary>
    private const string LinuxLowPriority =
        "N=; if nice -n 10 true >/dev/null 2>&1; then N=\"nice -n 10\"; fi\n"
        + "I=; if ionice -c3 true >/dev/null 2>&1; then I=\"ionice -c3\"; fi";

    /// <summary>
    /// env: and path_prepend: for the job's own process only, as one "env NAME=value ..." prefix right before the job's sh. Each assignment
    /// is a single POSIX-quoted word, and lanq's own script variables (S, P, rc, IFS...) are never touched. An empty PATH adds no
    /// trailing ':' (which would mean the current directory).
    /// </summary>
    internal static string JobEnvironmentPosix(Machine m, string? jobId = null, ShardInfo? shard = null, string? planJob = null, IReadOnlyList<JobEnvVar>? env = null)
    {
        var words = m.Env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => ShellQuote.Posix(e.Key + "=" + e.Value)).ToList();

        // The job's --env, in the order given: after the machine's (a clash is refused before this), before lanq's own.
        words.AddRange((env ?? []).Select(e => ShellQuote.Posix(SafeJobEnvName(e.Name) + "=" + e.Value)));
        if (jobId is not null)
        {
            // After the machine's own env: words, so the marker wins even if one named it (config refuses LANQ_* names anyway).
            words.Add(ShellQuote.Posix(JobMarker + "=" + SafeJobId(jobId)));
        }

        if (shard is not null)
        {
            words.AddRange(ShardVariables(shard).Select(v => ShellQuote.Posix(v.Name + "=" + v.Value)));
        }

        if (planJob is not null)
        {
            words.Add(ShellQuote.Posix(PlanJobVariable + "=" + SafePlanJob(planJob)));
        }

        if (m.PathPrepend.Count > 0)
        {
            words.Add(ShellQuote.Posix("PATH=" + string.Join(':', m.PathPrepend)) + "\"${PATH:+:$PATH}\"");
        }

        return words.Count == 0 ? "" : "env " + string.Join(' ', words) + " ";
    }

    /// <summary>
    /// The environment variable that marks every process of a job with its id (LANQ_JOB=&lt;job id&gt;). The Linux kill uses it to recognise a
    /// job's group after its leader has gone; jobs and tools may read it too.
    /// </summary>
    internal const string JobMarker = "LANQ_JOB";

    /// <summary>
    /// The start of the job wrapper's last stdout line, followed by the job's exit code: <c>LANQ_EXIT=&lt;job id&gt;:&lt;nonce&gt;:&lt;code&gt;</c>
    /// (<c>LANQ_EXIT=&lt;job id&gt;:&lt;code&gt;</c> without a <paramref name="nonce"/>). ssh passes a
    /// remote exit code through, so 255 alone cannot tell a job that exited 255 from a lost connection; this line, printed only after the job
    /// is over, can, when it is the final output line (<see cref="JobRunner.ReadExitMarker"/>). It decides only that; any other code is ssh's.
    /// </summary>
    internal static string ExitMarker(string jobId, string? nonce = null) => "LANQ_EXIT=" + SafeJobId(jobId) + ":" + NonceField(nonce);

    /// <summary>
    /// The line the Windows job wrapper prints instead of starting a job given &amp; or | whose program does not resolve to a native .exe:
    /// <c>LANQ_WINARGS=&lt;job id&gt;:&lt;nonce&gt;:&lt;what it resolved to&gt;</c> (the file's path, "Alias where" and the like, or nothing when not found),
    /// followed by the exit marker with 2. lanq reports that as Rejected (exit 2, <see cref="RejectKind.WindowsArgs"/>).
    /// </summary>
    internal static string WindowsArgsMarker(string jobId, string? nonce = null) => "LANQ_WINARGS=" + SafeJobId(jobId) + ":" + NonceField(nonce);

    /// <summary>
    /// A fresh random value for one run of the job wrapper (16 lowercase hex digits): its marker lines carry it, so only the wrapper's own lines
    /// count as markers (see <see cref="Execute"/>).
    /// </summary>
    internal static string NewMarkerNonce() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    private static string NonceField(string? nonce) =>
        nonce is null ? ""
        : nonce.Length == 16 && nonce.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? nonce + ":"
        : throw new ArgumentException("Invalid marker nonce.", nameof(nonce));

    /// <summary>
    /// For a job given &amp; or | (<see cref="JobPolicy.HasShimUnsafe"/>), the Windows wrapper's check before anything starts: argv[0] is looked
    /// up exactly as the job's call operator will look it up (same PATH with path_prepend:, same folder, aliases and functions first) and
    /// must be an application whose file is an .exe and not cmd or a package-manager shim; otherwise nothing runs and the wrapper prints
    /// <see cref="WindowsArgsMarker"/> and exits 2. A .cmd/.bat (say a dotnet.cmd earlier on PATH) would re-parse the arguments through
    /// cmd.exe. Empty for other jobs.
    /// </summary>
    private static string WindowsNativeCheck(string jobId, IReadOnlyList<string> argv, string? nonce)
    {
        if (!JobPolicy.HasShimUnsafe(argv))
        {
            return "";
        }

        var shims = string.Join(",", JobPolicy.WindowsShimPrograms.Select(ShellQuote.PowerShell));
        return $$"""
            $lc=Get-Command -Name {{ShellQuote.PowerShell(argv[0])}} -EA 0|select -First 1;$la=$null -ne $lc -and $lc.CommandType -eq 'Application'
            if(!($la -and $lc.Path -like '*.exe' -and @({{shims}}) -notcontains [IO.Path]::GetFileNameWithoutExtension($lc.Path))){
            $lr=if($la){$lc.Path}elseif($null -ne $lc){[string]$lc.CommandType+' '+$lc.Name}else{''}
            [Console]::Out.WriteLine({{ShellQuote.PowerShell(WindowsArgsMarker(jobId, nonce))}}+$lr);[Console]::Out.WriteLine({{ShellQuote.PowerShell(ExitMarker(jobId, nonce) + "2")}});exit 2}
            """;
    }

    /// <summary>A job id is 1 to 64 of A-Z, a-z, 0-9, '_' and '-' (lanq makes them as yyyyMMdd-HHmmss-xxxxxx).</summary>
    internal static string SafeJobId(string jobId) =>
        jobId.Length is > 0 and <= 64 && jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? jobId
            : throw new ArgumentException("Invalid job id.", nameof(jobId));

    /// <summary>LANQ_SHARD (1-based index) and LANQ_SHARDS (count): whole numbers lanq formats itself, checked against the shard cap again here.</summary>
    internal static IEnumerable<(string Name, string Value)> ShardVariables(ShardInfo shard)
    {
        if (shard.Count is < 1 or > ShardInfo.MaxShards || shard.Index < 1 || shard.Index > shard.Count)
        {
            throw new ArgumentException("Invalid shard.", nameof(shard));
        }

        yield return ("LANQ_SHARD", shard.Index.ToString(CultureInfo.InvariantCulture));
        yield return ("LANQ_SHARDS", shard.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>LANQ_PLAN_JOB: the name of the lanq.yml job this job runs for (lanq run --plan).</summary>
    internal const string PlanJobVariable = "LANQ_PLAN_JOB";

    /// <summary>A plan job name as lanq.yml allows it (1 to 32 of A-Z, a-z, 0-9, '_' and '-', not starting with '_' or '-'), checked again here.</summary>
    internal static string SafePlanJob(string name) =>
        name.Length is > 0 and <= 32 && char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? name
            : throw new ArgumentException("Invalid plan job name.", nameof(name));

    private static string PlanJobEnvironmentPowerShell(string? planJob) =>
        planJob is null ? "" : $"$env:{PlanJobVariable} = '{SafePlanJob(planJob)}'";

    private static string ShardEnvironmentPowerShell(ShardInfo? shard) =>
        shard is null ? "" : string.Join('\n', ShardVariables(shard).Select(v => $"$env:{v.Name} = '{v.Value}'"));

    /// <summary>A job's --env name, checked again here (a valid name, not on the deny table) before it is written into a script.</summary>
    private static string SafeJobEnvName(string name) =>
        JobEnv.IsValidName(name) && !JobEnv.IsJobDenied(name) ? name : throw new ArgumentException("Invalid job variable name.", nameof(name));

    /// <summary>A job's --env as PowerShell lines ($env:NAME = 'value'), in the order given; empty for none.</summary>
    private static string JobEnvPowerShell(IReadOnlyList<JobEnvVar>? env) =>
        string.Join('\n', (env ?? []).Select(e => $"$env:{SafeJobEnvName(e.Name)} = {ShellQuote.PowerShell(e.Value)}"));

    /// <summary>The shortest job arguments that still bring the native .exe check into the Windows job script (a one-letter program and a |).</summary>
    private static readonly string[] WorstCaseArgv = ["x", "|"];

    /// <summary>
    /// The Windows job script's encoded length at its largest for a machine, before any job arguments: the largest LANQ_SHARD / LANQ_SHARDS,
    /// the longest LANQ_PLAN_JOB, and the native .exe check a job given &amp; or | carries, plus the job's own <paramref name="env"/> (none for
    /// the config check). Over <see cref="MaxWindowsCommandLength"/> every such job there would fail.
    /// </summary>
    internal static int WorstCaseWindowsCommandLength(Machine m, IReadOnlyList<JobEnvVar>? env = null) =>
        WindowsCommand(Execute(
            m, "20260101-000000-000000", ".", WorstCaseArgv, JobRequest.DefaultTimeout, new ShardInfo(ShardInfo.MaxShards, ShardInfo.MaxShards),
            new string('x', Plan.PlanFileLoader.MaxNameLength), new string('0', 16), env)).Length;

    /// <summary>The same for PowerShell: process environment variables set just before the job starts, so its children inherit them.</summary>
    internal static string JobEnvironmentPowerShell(Machine m)
    {
        var lines = m.Env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"$env:{e.Key} = {ShellQuote.PowerShell(e.Value)}").ToList();
        if (m.PathPrepend.Count > 0)
        {
            var dirs = ShellQuote.PowerShell(string.Join(';', m.PathPrepend.Select(d => d.Replace('/', '\\'))));
            lines.Add($"$env:Path = {dirs} + $(if ($env:Path) {{ ';' + $env:Path }} else {{ '' }})");
        }

        return string.Join('\n', lines);
    }

    /// <summary>Marks the stop-before-fetch script (tests find it by this comment).</summary>
    internal const string StopJobMarker = "# lanq: stop the job before fetching";

    /// <summary>
    /// <c>kill_recorded PIDFILE JOBDIR JOBID</c>: the Linux job wrapper records its process group (line 1), the group leader's start time
    /// (line 2: field 22 of /proc/PID/stat, which stays the same across exec) and the boot id (line 3). With /proc, nothing is signalled unless
    /// the boot id is this boot's (a pid file that survived a reboot names nothing); then the group (TERM, 2 s, KILL) only when the leader exists
    /// with that start time, or the leader is gone, the group is alive and at least one live member of it carries LANQ_JOB=JOBID in its
    /// environment (the job's own processes; a group number reused by an unrelated program has none). Only for a pid file without a start time,
    /// or without /proc, the older checks decide: the leader's cwd inside JOBDIR (via /proc), else <c>ps -o pgid=</c> showing it still leads
    /// that group.
    /// The pid file is removed either way. Shared by cleanup, <see cref="StopJob"/> and stale-slot reclaim (stop_job).
    /// </summary>
    internal const string LinuxKillRecordedFunction = """
        kill_recorded() {
          kp="$1"; [ -f "$kp" ] || return 0
          kg=$(sed -n 1p "$kp" 2>/dev/null); st=$(sed -n 2p "$kp" 2>/dev/null); bt=$(sed -n 3p "$kp" 2>/dev/null)
          case "$kg" in ''|*[!0-9]*) rm -f "$kp"; return 0 ;; esac
          case "$st" in *[!0-9]*) st= ;; esac
          case "$bt" in *[!0-9a-f-]*) bt= ;; esac
          case "$3" in ''|*[!A-Za-z0-9_-]*) rm -f "$kp"; return 0 ;; esac
          mine=
          if [ -n "$st" ] && [ -d /proc/self ]; then
            if [ -z "$bt" ] || [ "$bt" != "$(cat /proc/sys/kernel/random/boot_id 2>/dev/null)" ]; then
              :
            elif [ -r "/proc/$kg/stat" ]; then
              [ "$(sed -n 's/.*) //p' "/proc/$kg/stat" 2>/dev/null | cut -d' ' -f20)" = "$st" ] && mine=1
            elif kill -s 0 -- "-$kg" 2>/dev/null; then
              for kf in /proc/[0-9]*/stat; do
                [ "$(sed -n 's/.*) //p' "$kf" 2>/dev/null | cut -d' ' -f3)" = "$kg" ] || continue
                if { tr '\000' '\n' < "${kf%/stat}/environ"; } 2>/dev/null | grep -qxF "LANQ_JOB=$3"; then mine=1; break; fi
              done
            fi
          elif [ -e /proc/self/cwd ]; then
            jd=$(cd -P "$2" 2>/dev/null && pwd -P) || jd="$2"
            case "$(readlink "/proc/$kg/cwd" 2>/dev/null)" in "$jd"|"$jd"/*|"$jd (deleted)") mine=1 ;; esac
          elif [ "$(ps -o pgid= -p "$kg" 2>/dev/null | tr -d ' ')" = "$kg" ]; then
            mine=1
          fi
          if [ -n "$mine" ]; then kill -s TERM -- "-$kg" 2>/dev/null; kill -s TERM "$kg" 2>/dev/null; sleep 2; kill -s KILL -- "-$kg" 2>/dev/null; kill -s KILL "$kg" 2>/dev/null; fi
          rm -f "$kp"
        }
        """;

    /// <summary>Cleanup's and <see cref="StopJob"/>'s use of <see cref="LinuxKillRecordedFunction"/>; needs $R and $ID.</summary>
    internal const string LinuxKillRecordedJob = LinuxKillRecordedFunction + "\n" + "kill_recorded \"$R/locks/job-$ID.pid\" \"$R/jobs/$ID\" \"$ID\"";

    /// <summary>
    /// Stops a job lanq gave up on (timed out, or its machine's hours ended) before its files are fetched. The worker enforces the same limit
    /// itself only <see cref="JobRunner.WorkerDeadlineMargin"/> later, so the job can still be running and writing. This uses the recorded
    /// processes exactly as cleanup does: the process group on Linux, and on Windows only processes whose pid and start time match what the
    /// job recorded (see <see cref="WindowsKillRecorded"/>). It then waits a moment for the processes to go and for files to be closed.
    /// </summary>
    public static string StopJob(Machine m, string jobId) => m.Os == MachineOs.Windows
        ? $$"""
            {{StopJobMarker}}
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            $pf = "$root/locks/job-$id.pid"
            {{WindowsKillRecorded}}
            if (Test-Path -LiteralPath $pf) { KillRecorded $pf $false; Start-Sleep -Seconds 1 }
            exit 0
            """
        : $$"""
            {{StopJobMarker}}
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            {{LinuxKillRecordedJob}}
            sleep 1
            exit 0
            """;

    /// <summary>
    /// Best-effort teardown: worktree, ref, bundle, slot. Never fails the job. In git mode without a repository id (nothing was fetched
    /// into a cache) only the cache steps are left out: the job's process, folder, bundle and slot are still removed.
    /// <paramref name="slot"/> is what <see cref="JobRunner.ParseSlot"/> returned: one slot ("2"), removed as always, or an exclusive job's
    /// list ("1,2,3,4", from <see cref="AcquireAll"/>), each of which is removed only while its first line is still this job's id.
    /// </summary>
    public static string Cleanup(Machine m, string jobId, string slot, string? repoId = null) =>
        m.Transfer == TransferMode.Archive ? CleanupArchive(m, jobId, slot) : CleanupGit(m, jobId, slot, repoId is null ? null : SafeRepoId(repoId));

    private static string CleanupArchive(Machine m, string jobId, string slot) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            $pf = "$root/locks/job-$id.pid"
            {{WindowsKillRecorded}}
            if (Test-Path -LiteralPath $pf) { KillRecorded $pf $true }
            $rc = 0
            $jd = "$root/jobs/$id"
            if (Test-Path -LiteralPath $jd) {
              $full = [IO.Path]::GetFullPath($jd.Replace('/','\'))
              cmd /c rd /s /q "\\?\$full" 2>$null | Out-Null
              if (Test-Path -LiteralPath $jd) { Write-Output "cleanup incomplete: $jd could not be removed"; $rc = 1 }
            }
            Remove-Item -Force -ErrorAction SilentlyContinue "$root/incoming/$id.tar.gz", "$root/incoming/$id.list"
            {{WindowsSlotRemoval(slot)}}
            exit $rc
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            {{LinuxKillRecordedJob}}
            rm -rf "$R/jobs/$ID"
            {{LinuxSlotRemoval(slot, "rm -f \"$R/incoming/$ID.tar.gz\" \"$R/incoming/$ID.list\"")}}
            exit 0
            """;

    private static string CleanupGit(Machine m, string jobId, string slot, string? repoId) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            $pf = "$root/locks/job-$id.pid"
            {{WindowsKillRecorded}}
            if (Test-Path -LiteralPath $pf) { KillRecorded $pf $true }
            {{(repoId is null ? "" : WindowsCacheCleanupBefore(repoId))}}
            if (Test-Path "$root/jobs/$id") { cmd /c rd /s /q ("$root/jobs/$id").Replace('/','\') 2>$null }
            {{(repoId is null ? "" : WindowsCacheCleanupAfter)}}
            Remove-Item -Force -ErrorAction SilentlyContinue "$root/incoming/$id.bundle"
            {{WindowsSlotRemoval(slot)}}
            exit 0
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            {{LinuxKillRecordedJob}}
            {{(repoId is null ? "" : LinuxCacheCleanupBefore(repoId))}}
            rm -rf "$R/jobs/$ID"
            {{(repoId is null ? "" : LinuxCacheCleanupAfter)}}
            {{LinuxSlotRemoval(slot, "rm -f \"$R/incoming/$ID.bundle\"")}}
            exit 0
            """;

    private static string WindowsCacheCleanupBefore(string repoId) => $$"""
        $c = "$root/repos/{{repoId}}.git"
        function Q { $env:GIT_CONFIG_NOSYSTEM = '1'; $env:GIT_CONFIG_GLOBAL = 'NUL'; & git {{GitHardeningWindows}} @args 2>$null }
        Q -C $c worktree remove --force "$root/jobs/$id"
        """;

    private const string WindowsCacheCleanupAfter = """
        Q -C $c worktree prune
        Q -C $c update-ref -d "refs/lanq/$id"
        """;

    private static string LinuxCacheCleanupBefore(string repoId) => $$"""
        C="$R/repos/{{repoId}}.git"
        {{LinuxGitFunction}}
        G -C "$C" worktree remove --force "$R/jobs/$ID" 2>/dev/null
        """;

    private const string LinuxCacheCleanupAfter = """
        G -C "$C" worktree prune 2>/dev/null
        G -C "$C" update-ref -d "refs/lanq/$ID" 2>/dev/null
        """;

    /// <summary>A repository id is exactly 16 lowercase hex digits, so it can never carry a path or shell syntax into a script.</summary>
    internal static string SafeRepoId(string? repoId) =>
        repoId is { Length: 16 } && repoId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? repoId
            : throw new ArgumentException("Invalid repository id.", nameof(repoId));

    private static string SafeSlot(string slot) =>
        int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException("Invalid slot id.", nameof(slot));

    /// <summary>The slot ids of "2" or "1,2,3,4", each a positive whole number (anything else throws, so no text reaches a script).</summary>
    private static List<string> SafeSlots(string slot) => [.. slot.Split(',').Select(SafeSlot)];

    /// <summary>
    /// Cleanup's slot step on Windows (needs $root and $id): each of the job's slots is removed only while the file's first line is still this
    /// job's id. A slot that was reclaimed and claimed by another job in the meantime (for example by an exclusive job while this job's
    /// controller was asleep past the stale grace) is left to that job.
    /// </summary>
    private static string WindowsSlotRemoval(string slot) =>
        "foreach($n in @(" + string.Join(',', SafeSlots(slot)) + ")){$g=\"$root/locks/slot-$n\";try{if(([IO.File]::ReadAllText($g) -split \"`n\")[0].Trim() -ceq $id){[IO.File]::Delete($g)}}catch{}}";

    /// <summary>
    /// Cleanup's last step on Linux (needs $R and $ID): <paramref name="rm"/>, then each of the job's slots only while its first line is still
    /// this job's id (as <see cref="WindowsSlotRemoval"/>).
    /// </summary>
    private static string LinuxSlotRemoval(string slot, string rm) => rm + "\n" + LinuxCheckedSlotLoop(SafeSlots(slot));

    private static string LinuxCheckedSlotLoop(IEnumerable<string> slots) =>
        "for n in " + string.Join(' ', slots) + "; do if [ \"$(sed -n 1p \"$R/locks/slot-$n\" 2>/dev/null)\" = \"$ID\" ]; then rm -f \"$R/locks/slot-$n\"; fi; done";

    /// <summary>The comment that starts <see cref="ReleaseClaim"/> (tests and logs recognise the call by it).</summary>
    internal const string ReleaseClaimMarker = "# lanq: release the slots of a claim that did not report back";

    /// <summary>
    /// After a slot claim that never reported back (timed out, lost its connection, or printed something unreadable): removes every slot
    /// file slot-1 to slot-max_jobs whose first line is this job's id, so slots the claim may have created do not block the machine until
    /// their deadline plus the stale grace. Another job's slot is never touched (the same check as cleanup's).
    /// </summary>
    public static string ReleaseClaim(Machine m, string jobId)
    {
        var slots = Enumerable.Range(1, Math.Max(m.MaxJobs, 1)).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        return m.Os == MachineOs.Windows
            ? ReleaseClaimMarker + "\n$root = " + ShellQuote.PowerShell(m.WorkRoot) + "; $id = " + ShellQuote.PowerShell(jobId) + "\n" + WindowsSlotRemoval(string.Join(',', slots)) + "\nexit 0"
            : ReleaseClaimMarker + "\nR=" + ShellQuote.Posix(m.WorkRoot) + "; ID=" + ShellQuote.Posix(jobId) + "\n" + LinuxCheckedSlotLoop(slots) + "\nexit 0";
    }
}
