using System.Globalization;
using System.Text;
using LanQueue.Config;
using LanQueue.Policy;

namespace LanQueue.Jobs;

/// <summary>Builds the remote command strings (sh on Linux, PowerShell on Windows). Pure functions, unit tested.</summary>
public static class RemoteScripts
{
    /// <summary>Conservative cap on the whole remote command line for Windows hosts (cmd.exe allows 8191 characters).</summary>
    internal const int MaxWindowsCommandLength = 8000;

    public static string Wrap(Machine m, string script)
    {
        if (m.Os != MachineOs.Windows)
        {
            return "sh -c " + ShellQuote.Posix(script);
        }

        var command = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'\n" + script));
        return command.Length > MaxWindowsCommandLength
            ? throw new InvalidOperationException(
                $"The command is too long for Windows machine '{m.Name}' ({command.Length} > {MaxWindowsCommandLength} characters once encoded). Shorten the arguments.")
            : command;
    }

    public static string Probe(Machine m) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
            $inv = [cultureinfo]::InvariantCulture
            $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
            if ($null -eq $cpu) { $cpu = 0 }
            $drive = New-Object System.IO.DriveInfo ($root.Substring(0,1))
            $slots = @(Get-ChildItem -Path "$root/locks" -Filter 'slot-*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^slot-[0-9]+\z' }).Count
            'load=' + ([double]$cpu / 100).ToString('0.###', $inv)
            'disk_free_gb=' + ($drive.AvailableFreeSpace / 1GB).ToString('0.#', $inv)
            'slots_used=' + $slots
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
            if ($os -and $os.FreePhysicalMemory) { 'mem_free_gb=' + ([double]$os.FreePhysicalMemory / 1MB).ToString('0.##', $inv) }
            'paused=' + $(if (Test-Path -LiteralPath "$root/PAUSE") { '1' } else { '0' })
            'local_time=' + (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss', $inv)
            $e = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(); $s = $e - ($e % 900)
            'now_epoch=' + $e
            'tz_start=' + $s
            'tz_offsets=' + ((0..144 | ForEach-Object { [int][TimeZoneInfo]::Local.GetUtcOffset([DateTimeOffset]::FromUnixTimeSeconds($s + 900 * $_)).TotalMinutes }) -join ',')
            {{(m.Transfer == TransferMode.Archive ? WindowsTarProbe : GitProbeWindows)}}
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            cpus=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)
            l=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo 0)
            d="$R"; [ -d "$d" ] || d=/
            echo "load=$(awk -v l="$l" -v c="$cpus" 'BEGIN{printf "%.3f", l/c}')"
            echo "disk_free_gb=$(df -Pk "$d" | awk 'NR==2{printf "%.1f", $4/1048576}')"
            echo "slots_used=$(ls "$R/locks" 2>/dev/null | grep -c '^slot-[0-9][0-9]*$')"
            awk '/^MemAvailable:/{print "mem_free_gb=" sprintf("%.2f", $2/1048576)} /^SwapFree:/{print "swap_free_gb=" sprintf("%.2f", $2/1048576)}' /proc/meminfo 2>/dev/null
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
    /// replaced the old one in between), a lock is put back in its place, so the live reclaimer keeps the slot to itself. <c>stop_job</c> stops a stale slot's job if it is still
    /// running: it reads locks/job-ID.pid and kills that process group only when /proc shows the process working inside that job's folder
    /// (so a pid that was reused by an unrelated process is never killed); without pid information nothing is killed.
    /// </summary>
    internal const string LinuxSlotFunctions = """
        deadline_of() { d=$(printf '%s\n' "$1" | sed -n 2p); case "$d" in ''|*[!0-9]*) return 1 ;; esac; [ ${#d} -le 18 ] || return 1; d=$(printf '%s' "$d" | sed 's/^0*//'); echo "${d:-0}"; }
        take() {
          k="$1.reclaim"; o="$k.old.$$"
          if [ -n "$(find "$k" -mmin +10 2>/dev/null)" ] && mv "$k" "$o" 2>/dev/null; then
            if [ -n "$(find "$o" -mmin +10 2>/dev/null)" ]; then rm -f "$o"; else ( set -C; : > "$k" ) 2>/dev/null; rm -f "$o"; fi
          fi
          ( set -C; : > "$k" ) 2>/dev/null
        }
        stop_job() {
          case "$1" in ''|*[!A-Za-z0-9_-]*) return 0 ;; esac
          kp="$R/locks/job-$1.pid"; [ -f "$kp" ] || return 0
          g=$(sed -n 1p "$kp" 2>/dev/null)
          case "$g" in ''|*[!0-9]*) rm -f "$kp"; return 0 ;; esac
          if [ -d "/proc/$g" ]; then
            jd=$(cd -P "$R/jobs/$1" 2>/dev/null && pwd -P) || jd="$R/jobs/$1"
            case "$(readlink "/proc/$g/cwd" 2>/dev/null)" in
              "$jd"|"$jd"/*|"$jd (deleted)") kill -s TERM -- "-$g" 2>/dev/null; kill -s TERM "$g" 2>/dev/null; sleep 2; kill -s KILL -- "-$g" 2>/dev/null; kill -s KILL "$g" 2>/dev/null ;;
            esac
          fi
          rm -f "$kp"
        }
        """;

    /// <summary>
    /// The PowerShell helpers (they need $root): <c>Take</c> and <c>Drop</c> the slot's lock file (CreateNew; one older than ten minutes is
    /// cleared first), and <c>StopJob</c>, which kills a stale slot's still-running job (wrapper and child, with their trees) through
    /// <see cref="WindowsKillRecorded"/>: only processes whose pid AND start time match what the job recorded.
    /// </summary>
    internal const string WindowsSlotFunctions = """
        function Take([string]$f) {
          $k = "$f.reclaim"
          $o = "$k.old.$PID"; $old = [DateTime]::UtcNow.AddMinutes(-10)
          try {
            if ((Test-Path -LiteralPath $k) -and [IO.File]::GetLastWriteTimeUtc($k) -lt $old) {
              [IO.File]::Move($k, $o)
              if ([IO.File]::GetLastWriteTimeUtc($o) -ge $old) { try { [IO.File]::Open($k, 'CreateNew', 'Write', 'None').Close() } catch { } }
              [IO.File]::Delete($o)
            }
          } catch { }
          try { [IO.File]::Open($k, 'CreateNew', 'Write', 'None').Close(); $true } catch { $false }
        }
        function Drop([string]$f) { try { [IO.File]::Delete("$f.reclaim") } catch { } }
        function StopJob([string]$j) {
          if ($j -cnotmatch '^[A-Za-z0-9_-]{1,64}\z') { return }
          $kp = "$root/locks/job-$j.pid"
          if (Test-Path -LiteralPath $kp) { KillRecorded $kp $false }
        }
        """ + "\n" + WindowsKillRecorded;

    /// <summary>
    /// <c>KillRecorded $pf $legacy</c>: the Windows job wrapper records pairs of "pid" and "process start time (UTC ticks)" in
    /// locks/job-ID.pid, first its own PowerShell, then the child that runs the job. Each recorded process (child first) is killed with
    /// its tree (taskkill /T /F) only when a process with that pid exists AND started at that very instant, so a pid that Windows has
    /// since given to another process (for example the owner's own PowerShell) is never touched, and an orphaned child whose wrapper
    /// died is still reached. A pid file without start times (an older lanq) is honoured only by cleanup right after its job
    /// (<paramref name="legacy"/>), with the old "is a PowerShell process" check; a stale-slot reclaim never kills on it.
    /// </summary>
    internal const string WindowsKillRecorded = """
        function KillRecorded([string]$pf, [bool]$legacy) {
          $l = @(Get-Content -LiteralPath $pf -ErrorAction SilentlyContinue | ForEach-Object { "$_".Trim() })
          $timed = $false
          for ($i = $l.Count - ($l.Count % 2) - 2; $i -ge 0; $i -= 2) {
            if ($l[$i] -notmatch '^[0-9]{1,9}\z' -or $l[$i + 1] -notmatch '^[0-9]{1,19}\z') { continue }
            $timed = $true
            $p = Get-Process -Id ([int]$l[$i]) -ErrorAction SilentlyContinue
            $t = $null; if ($p) { try { $t = "$($p.StartTime.ToUniversalTime().Ticks)" } catch { } }
            if ($t -and $t -eq $l[$i + 1]) { & taskkill /PID $l[$i] /T /F 2>$null | Out-Null }
          }
          if (-not $timed -and $legacy -and $l.Count -ge 1 -and $l[0] -match '^[0-9]{1,9}\z') {
            $p = Get-Process -Id ([int]$l[0]) -ErrorAction SilentlyContinue
            if ($p -and ($p.ProcessName -like 'powershell*' -or $p.ProcessName -like 'pwsh*')) { & taskkill /PID $l[0] /T /F 2>$null | Out-Null }
          }
          Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $pf
        }
        """;

    /// <summary>How long past its own deadline a slot file must be before another job may reclaim it.</summary>
    internal const int StaleSlotGraceSeconds = 1800;

    /// <summary>Added to the job timeout for a slot's deadline: ensure, upload and prepare (twice with a full-bundle fallback) and cleanup.</summary>
    internal static readonly TimeSpan SlotTransferAllowance = TimeSpan.FromMinutes(40);

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
                New-Item -ItemType Directory -Force -Path "$root/locks" | Out-Null
                $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                $deadline = $now + {{lease}}
                {{WindowsSlotFunctions}}
                for ($i = 1; $i -le {{max}}; $i++) {
                  $f = "$root/locks/slot-$i"
                  try {
                    $c = [IO.File]::ReadAllText($f); $l = $c -split "`n"
                    if ($l.Count -ge 2 -and $l[1].Trim() -match '^[0-9]{1,18}\z' -and $now -gt ([long]$l[1].Trim() + {{grace}}) -and (Take $f)) {
                      try { if ([IO.File]::ReadAllText($f) -ceq $c) { StopJob $l[0].Trim(); $t = "$f.stale.$PID"; [IO.File]::Move($f, $t); [IO.File]::Delete($t) } } catch { } finally { Drop $f }
                    }
                  } catch { }
                  try {
                    $fs = [System.IO.File]::Open($f, 'CreateNew', 'Write', 'None')
                    $b = [Text.Encoding]::UTF8.GetBytes({{ShellQuote.PowerShell(jobId)}} + "`n" + $deadline + "`n"); $fs.Write($b, 0, $b.Length); $fs.Close()
                    'SLOT=' + $i; exit 0
                  } catch [System.IO.IOException] { }
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
                      rm -f "$f.reclaim"
                    fi
                  fi
                  if ( set -C; printf '%s\n%s\n' {{ShellQuote.Posix(jobId)}} "$D" > "$f" ) 2>/dev/null; then echo "SLOT=$i"; exit 0; fi
                  i=$((i+1))
                done
                echo "SLOT=FULL"
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
    /// out; a check stopped that way prints <c>cap.NAME=timeout|</c> and the next check runs.
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
                function Cap([string]$n, [string]$code) {
                  $si = New-Object System.Diagnostics.ProcessStartInfo ((Get-Process -Id $PID).Path)
                  $si.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('$global:LASTEXITCODE = 0; try { ' + $code + ' } catch { Write-Output "$_"; exit 1 }; exit $LASTEXITCODE'))
                  $si.UseShellExecute = $false; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true; $si.RedirectStandardInput = $true
                  try { $p = [Diagnostics.Process]::Start($si) } catch { "cap.$n=1|$_"; return }
                  $p.StandardInput.Close(); $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
                  if (-not $p.WaitForExit({{seconds}}000)) { & taskkill /PID $p.Id /T /F 2>$null | Out-Null; "cap.$n=timeout|"; return }
                  $p.WaitForExit()
                  "cap.$n=$($p.ExitCode)|" + "$(($o.Result + "`n" + $e.Result) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)"
                }
                {{checks}}
                """;
        }

        var env = JobEnvironmentPosix(m);
        var lines = caps.Select(c => c.ToLowerInvariant()).Select(c =>
            $"o=$($T {env}/bin/sh -c {ShellQuote.Posix(ToolChecks[c].Linux)} </dev/null 2>&1); rc=$?; "
            + $"if [ -n \"$T\" ] && [ \"$rc\" = 124 ]; then echo \"cap.{c}=timeout|\"; else echo \"cap.{c}=$rc|$(printf '%s\\n' \"$o\" | sed -n '/./{{p;q;}}')\"; fi");
        return $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            if [ -d "$R" ]; then
              t="$R/.lanq-doctor.$$"
              if ( : > "$t" ) 2>/dev/null; then rm -f "$t"; echo root=ok; else echo root=readonly; fi
            else echo root=missing; fi
            {{(m.Transfer == TransferMode.Archive ? LinuxTarProbe : GitProbeLinux + "\n" + GitVersionLinux)}}
            T=; if timeout -k 5 1 true >/dev/null 2>&1; then T="timeout -k 5 {{seconds}}"; fi
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
            $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            {{WindowsSlotFunctions}}
            foreach ($f in @(Get-ChildItem -LiteralPath "$root/locks" -Filter 'slot-*' -File -ErrorAction SilentlyContinue)) {
              $n = $f.Name; $p = $f.FullName
              if ($n -notmatch '^slot-[0-9]+\z') { continue }
              try { $c = [IO.File]::ReadAllText($p) } catch { continue }
              $l = $c -split "`n"
              if ($l.Count -lt 2 -or $l[1].Trim() -notmatch '^[0-9]{1,18}\z') { "KEPT=$n nodeadline"; continue }
              if ($now -le [long]$l[1].Trim()) { "KEPT=$n live"; continue }
              if (-not (Take $p)) { "KEPT=$n changed"; continue }
              try {
                if ([IO.File]::ReadAllText($p) -ceq $c) { StopJob $l[0].Trim(); $t = "$p.stale.$PID"; [IO.File]::Move($p, $t); [IO.File]::Delete($t); 'REMOVED=' + $n + ' ' + $l[0].Trim() }
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
              rm -f "$f.reclaim"
            done
            """,
    };

    /// <summary>
    /// Git mode: verifies the uploaded bundle against the bare per-repository cache <c>repos/&lt;repoId&gt;.git</c> (printing
    /// <see cref="BundleNotUsable"/> and failing if its prerequisites are missing), fetches it, moves <c>refs/lanq/base</c> to the snapshot
    /// (keeping it alive as the next incremental base) and adds a detached per-job worktree. Archive mode ignores <paramref name="repoId"/>.
    /// </summary>
    public static string Prepare(Machine m, string jobId, string? repoId = null) =>
        m.Transfer == TransferMode.Archive ? PrepareArchive(m, jobId) : PrepareGit(m, jobId, SafeRepoId(repoId));

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

    private static string PrepareGit(Machine m, string jobId, string repoId) => m.Os == MachineOs.Windows
        ? $$"""
            $ErrorActionPreference = 'Stop'
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
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
            G -c protocol.file.allow=always -C $c fetch -q "$root/incoming/$id.bundle" "refs/lanq/${id}:refs/lanq/${id}"
            G -C $c update-ref refs/lanq/base "refs/lanq/$id"
            G -C $c worktree add -q --detach "$root/jobs/$id" "refs/lanq/$id"
            """
        : $$"""
            set -e
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}; C="$R/repos/{{repoId}}.git"
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
            G -c protocol.file.allow=always -C "$C" fetch -q "$R/incoming/$ID.bundle" "refs/lanq/$ID:refs/lanq/$ID"
            G -C "$C" update-ref refs/lanq/base "refs/lanq/$ID"
            G -C "$C" worktree add -q --detach "$R/jobs/$ID" "refs/lanq/$ID"
            """;

    /// <summary>
    /// Printed by the git prepare step when the cache cannot take the uploaded bundle (<c>git bundle verify</c> failed: for an incremental
    /// bundle, the worker no longer has the base). The runner then sends a full bundle, once.
    /// </summary>
    internal const string BundleNotUsable = "lanq: bundle not usable by the worker cache (missing prerequisites)";

    public static string Incoming(Machine m, string jobId) => $"{m.WorkRoot}/incoming/{jobId}{(m.Transfer == TransferMode.Archive ? ".tar.gz" : ".bundle")}";

    public static string EnsureIncoming(Machine m) => m.Os == MachineOs.Windows
        ? $"New-Item -ItemType Directory -Force -Path ({ShellQuote.PowerShell(m.WorkRoot + "/incoming")}) | Out-Null"
        : $"mkdir -p {ShellQuote.Posix(m.WorkRoot + "/incoming")}";

    /// <summary>
    /// Runs the allowlisted argv inside the job worktree. Each argument is individually quoted. The directory is the
    /// normalized one (never the raw input) and its real path must stay inside the worktree. The job's process group
    /// (Linux, via setsid) or PowerShell PID (Windows) is recorded in locks/job-ID.pid so cleanup can kill it. With a
    /// <paramref name="limit"/> the worker also stops the job itself: on Linux under <c>timeout -k 30</c> when that works (exit 124), on
    /// Windows by running it in a child PowerShell and killing that tree (taskkill /T /F) when the wait runs out (exit 124).
    /// </summary>
    public static string Execute(Machine m, string jobId, string directory, IReadOnlyList<string> argv, TimeSpan? limit = null)
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
                $pf = {{ShellQuote.PowerShell(pidFile)}}
                $wr = (Resolve-Path -LiteralPath {{ShellQuote.PowerShell(worktree)}}).ProviderPath
                Set-Location -LiteralPath {{ShellQuote.PowerShell(dir)}}
                $here = (Get-Location).ProviderPath
                $sep = [IO.Path]::DirectorySeparatorChar
                if (-not (($here + $sep).StartsWith($wr + $sep, [StringComparison]::OrdinalIgnoreCase))) { exit 126 }
                $p = Get-Item -LiteralPath $here
                while ($p.FullName.Length -gt $wr.Length) {
                  if ($p.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 126 }
                  $p = $p.Parent
                }
                {{JobEnvironmentPowerShell(m)}}
                {{(m.Priority == JobPriority.Low ? "try { (Get-Process -Id $PID).PriorityClass = 'BelowNormal' } catch { }" : "")}}
                Set-Content -LiteralPath $pf -Value @($PID, (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks)
                $job = {
                & {{parts}}
                }
                $inner = '$ProgressPreference = ''SilentlyContinue''; $global:LASTEXITCODE = 0' + "`n" + 'try {' + "`n" + $job.ToString() + "`n" + 'exit $LASTEXITCODE' + "`n" + '} catch { Write-Output "$_"; exit 1 }'
                $si = New-Object System.Diagnostics.ProcessStartInfo ((Get-Process -Id $PID).Path)
                $si.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
                $si.UseShellExecute = $false
                $si.WorkingDirectory = $here
                $code = 1
                try {
                  $cp = [Diagnostics.Process]::Start($si)
                  Add-Content -LiteralPath $pf -Value @($cp.Id, $cp.StartTime.ToUniversalTime().Ticks)
                  if ($cp.WaitForExit({{WindowsWaitMilliseconds(limit)}})) { $cp.WaitForExit(); $code = $cp.ExitCode }
                  else { & taskkill /PID $cp.Id /T /F 2>$null | Out-Null; $code = 124 }
                } finally {
                  Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $pf
                }
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
            T={{(limit is { } l ? $"; if timeout -k 1 5 true >/dev/null 2>&1; then T=\"timeout -k 30 {WholeSeconds(l)}\"; fi" : "")}}
            {{(m.Priority == JobPriority.Low ? LinuxLowPriority : "")}}
            exec 3<&0
            $S $T {{(m.Priority == JobPriority.Low ? "$N $I " : "")}}{{JobEnvironmentPosix(m)}}/bin/sh -c 'exec "$@"' sh {{string.Join(' ', argv.Select(ShellQuote.Posix))}} <&3 3<&- &
            j=$!
            echo "$j" > "$P"
            wait "$j"
            rc=$?
            rm -f "$P"
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
    internal static string JobEnvironmentPosix(Machine m)
    {
        var words = m.Env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => ShellQuote.Posix(e.Key + "=" + e.Value)).ToList();
        if (m.PathPrepend.Count > 0)
        {
            words.Add(ShellQuote.Posix("PATH=" + string.Join(':', m.PathPrepend)) + "\"${PATH:+:$PATH}\"");
        }

        return words.Count == 0 ? "" : "env " + string.Join(' ', words) + " ";
    }

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

    /// <summary>
    /// Best-effort teardown: worktree, ref, bundle, slot. Never fails the job. In git mode without a repository id (nothing was fetched
    /// into a cache) only the cache steps are left out: the job's process, folder, bundle and slot are still removed.
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
            Remove-Item -Force -ErrorAction SilentlyContinue "$root/locks/slot-{{SafeSlot(slot)}}"
            exit $rc
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            P="$R/locks/job-$ID.pid"
            if [ -f "$P" ]; then
              g=$(cat "$P" 2>/dev/null)
              case "$g" in
                ''|*[!0-9]*) ;;
                *) kill -s TERM -- "-$g" 2>/dev/null; kill -s TERM "$g" 2>/dev/null; sleep 2; kill -s KILL -- "-$g" 2>/dev/null; kill -s KILL "$g" 2>/dev/null ;;
              esac
              rm -f "$P"
            fi
            rm -rf "$R/jobs/$ID"
            rm -f "$R/incoming/$ID.tar.gz" "$R/incoming/$ID.list" "$R/locks/slot-{{SafeSlot(slot)}}"
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
            Remove-Item -Force -ErrorAction SilentlyContinue "$root/locks/slot-{{SafeSlot(slot)}}"
            exit 0
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            P="$R/locks/job-$ID.pid"
            if [ -f "$P" ]; then
              g=$(cat "$P" 2>/dev/null)
              case "$g" in
                ''|*[!0-9]*) ;;
                *) kill -s TERM -- "-$g" 2>/dev/null; kill -s TERM "$g" 2>/dev/null; sleep 2; kill -s KILL -- "-$g" 2>/dev/null; kill -s KILL "$g" 2>/dev/null ;;
              esac
              rm -f "$P"
            fi
            {{(repoId is null ? "" : LinuxCacheCleanupBefore(repoId))}}
            rm -rf "$R/jobs/$ID"
            {{(repoId is null ? "" : LinuxCacheCleanupAfter)}}
            rm -f "$R/incoming/$ID.bundle" "$R/locks/slot-{{SafeSlot(slot)}}"
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
}
