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
            $slots = @(Get-ChildItem -Path "$root/locks" -Filter 'slot-*' -ErrorAction SilentlyContinue).Count
            'load=' + ([double]$cpu / 100).ToString('0.###', $inv)
            'disk_free_gb=' + ($drive.AvailableFreeSpace / 1GB).ToString('0.#', $inv)
            'slots_used=' + $slots
            {{(m.Transfer == TransferMode.Archive ? WindowsTarProbe : GitProbeWindows)}}
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            cpus=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)
            l=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo 0)
            d="$R"; [ -d "$d" ] || d=/
            echo "load=$(awk -v l="$l" -v c="$cpus" 'BEGIN{printf "%.3f", l/c}')"
            echo "disk_free_gb=$(df -Pk "$d" | awk 'NR==2{printf "%.1f", $4/1048576}')"
            echo "slots_used=$(ls -d "$R"/locks/slot-* 2>/dev/null | wc -l | tr -d ' ')"
            {{(m.Transfer == TransferMode.Archive ? LinuxTarProbe : GitProbeLinux)}}
            """;

    /// <summary>
    /// Options for every git call on a worker (hooks in the cache or a hooks path in the worker's git config must never run
    /// anything). Hooks are pointed at the null device, the fsmonitor daemon is off, and every transport is refused; only the
    /// bundle fetch re-allows reading a local file, with <c>-c protocol.file.allow=always</c> on that one call.
    /// </summary>
    internal const string GitHardeningLinux = "-c core.hooksPath=/dev/null -c core.fsmonitor=false -c protocol.allow=never -c protocol.file.allow=never";

    /// <inheritdoc cref="GitHardeningLinux"/>
    internal const string GitHardeningWindows = "-c core.longpaths=true -c core.hooksPath=NUL -c core.fsmonitor=false -c protocol.allow=never -c protocol.file.allow=never";

    private const string GitProbeWindows = "'tool=' + $(if (Get-Command git -ErrorAction SilentlyContinue) { 'ok' } else { 'missing' })";

    private const string GitProbeLinux = "if git --version >/dev/null 2>&1; then echo tool=ok; else echo tool=missing; fi";

    /// <summary>
    /// Archive mode needs a tar whose -t listing can be trusted: GNU tar or bsdtar/libarchive (busybox tar hides hard links and strips
    /// leading '/' and '../' in its listing, so it is refused), plus gzip. Defines the shell function tar_supported.
    /// </summary>
    private const string LinuxTarSupportedFunction =
        "tar_supported() { v=$(tar --version 2>&1 | head -n 1); case \"$v\" in *\"GNU tar\"*|*bsdtar*|*libarchive*) command -v gzip >/dev/null 2>&1 ;; *) return 1 ;; esac; }";

    private const string LinuxTarProbe = LinuxTarSupportedFunction + "\nif tar_supported; then echo tool=ok; else echo tool=missing; fi";

    /// <summary>
    /// The Windows built-in bsdtar, by full path so a GNU tar earlier on PATH (for example Git for Windows) is never used. It must also accept
    /// every restricting extract option: that is probed once here with an empty archive (extracts nothing), so Prepare never needs a fallback.
    /// </summary>
    private const string WindowsTarProbe =
        "$t = \"$env:SystemRoot\\System32\\tar.exe\"\n"
        + "$ok = (Test-Path -LiteralPath $t) -and (\"$(& $t --version 2>$null | Select-Object -First 1)\" -match 'bsdtar|libarchive')\n"
        + "if ($ok) { & $t -xf NUL --no-acls --no-xattrs --no-fflags --no-same-owner --no-same-permissions -C $env:TEMP 2>$null | Out-Null; $ok = ($LASTEXITCODE -eq 0) }\n"
        + "'tool=' + $(if ($ok) { 'ok' } else { 'missing' })";

    /// <summary>How long past its own deadline a slot file must be before another job may reclaim it.</summary>
    internal const int StaleSlotGraceSeconds = 1800;

    /// <summary>Added to the job timeout for a slot's deadline: ensure, upload and prepare (twice with a full-bundle fallback) and cleanup.</summary>
    internal static readonly TimeSpan SlotTransferAllowance = TimeSpan.FromMinutes(40);

    /// <summary>
    /// Atomically claims one of max_jobs slot files. Prints SLOT=n or SLOT=FULL. The slot file holds the job id and, on its second line,
    /// its deadline in the worker's own epoch seconds (now + <paramref name="jobTimeout"/> + <see cref="SlotTransferAllowance"/>). A slot
    /// more than <see cref="StaleSlotGraceSeconds"/> past its deadline was left by a crashed run and is reclaimed: it is renamed away
    /// (atomic), and put back if what was renamed is not the stale file that was read. A slot without a deadline is never reclaimed.
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
                for ($i = 1; $i -le {{max}}; $i++) {
                  $f = "$root/locks/slot-$i"
                  try {
                    $c = [IO.File]::ReadAllText($f); $l = $c -split "`n"
                    if ($l.Count -ge 2 -and $l[1].Trim() -match '^[0-9]{1,18}$' -and $now -gt ([long]$l[1].Trim() + {{grace}})) {
                      $t = "$f.stale.$PID"
                      [IO.File]::Move($f, $t)
                      if ([IO.File]::ReadAllText($t) -ceq $c) { [IO.File]::Delete($t) } else { try { [IO.File]::Move($t, $f) } catch { [IO.File]::Delete($t) } }
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
                i=1
                while [ "$i" -le {{max}} ]; do
                  f="$R/locks/slot-$i"
                  if [ -f "$f" ]; then
                    c=$(cat "$f" 2>/dev/null); d=$(printf '%s\n' "$c" | sed -n 2p)
                    case "$d" in
                      ''|*[!0-9]*) ;;
                      *) if [ "$now" -gt $((d + {{grace}})) ] && mv "$f" "$f.stale.$$" 2>/dev/null; then
                           if [ "$(cat "$f.stale.$$")" != "$c" ]; then ln "$f.stale.$$" "$f" 2>/dev/null; fi
                           rm -f "$f.stale.$$"
                         fi ;;
                    esac
                  fi
                  if ( set -C; printf '%s\n%s\n' {{ShellQuote.Posix(jobId)}} "$D" > "$f" ) 2>/dev/null; then echo "SLOT=$i"; exit 0; fi
                  i=$((i+1))
                done
                echo "SLOT=FULL"
                """;
    }

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
            function Q { & git {{GitHardeningWindows}} @args }
            function G { Q @args; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
            New-Item -ItemType Directory -Force -Path "$root/jobs", "$root/repos" | Out-Null
            if (-not (Test-Path -LiteralPath $c)) { G init --bare -q $c }
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
            G() { git {{GitHardeningLinux}} "$@"; }
            [ -d "$C" ] || G init --bare -q "$C"
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
    /// (Linux, via setsid) or PowerShell PID (Windows) is recorded in locks/job-ID.pid so cleanup can kill it.
    /// </summary>
    public static string Execute(Machine m, string jobId, string directory, IReadOnlyList<string> argv)
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
                Set-Content -LiteralPath $pf -Value $PID
                $code = 1
                try {
                  & {{parts}}
                  $code = $LASTEXITCODE
                } finally {
                  Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $pf
                }
                exit $code
                """;
        }

        return $$"""
            cd -P {{ShellQuote.Posix(dir)}} || exit 126
            case "$(pwd -P)/" in "$(cd -P {{ShellQuote.Posix(worktree)}} && pwd -P)"/*) ;; *) exit 126 ;; esac
            P={{ShellQuote.Posix(pidFile)}}
            S=; if command -v setsid >/dev/null 2>&1; then S="setsid -w"; fi
            $S sh -c 'echo $$ > "$1"; shift; exec "$@"' sh "$P" {{string.Join(' ', argv.Select(ShellQuote.Posix))}}
            rc=$?
            rm -f "$P"
            exit $rc
            """;
    }

    /// <summary>Best-effort teardown: worktree, ref, bundle, slot. Never fails the job.</summary>
    public static string Cleanup(Machine m, string jobId, string slot, string? repoId = null) =>
        m.Transfer == TransferMode.Archive ? CleanupArchive(m, jobId, slot) : CleanupGit(m, jobId, slot, SafeRepoId(repoId));

    private static string CleanupArchive(Machine m, string jobId, string slot) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            $pf = "$root/locks/job-$id.pid"
            if (Test-Path -LiteralPath $pf) {
              $jp = (Get-Content -LiteralPath $pf -ErrorAction SilentlyContinue | Select-Object -First 1)
              if ("$jp" -match '^[0-9]+$') {
                $proc = Get-Process -Id ([int]$jp) -ErrorAction SilentlyContinue
                if ($proc -and ($proc.ProcessName -like 'powershell*' -or $proc.ProcessName -like 'pwsh*')) { & taskkill /PID $jp /T /F 2>$null | Out-Null }
              }
              Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $pf
            }
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

    private static string CleanupGit(Machine m, string jobId, string slot, string repoId) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            $pf = "$root/locks/job-$id.pid"
            if (Test-Path -LiteralPath $pf) {
              $jp = (Get-Content -LiteralPath $pf -ErrorAction SilentlyContinue | Select-Object -First 1)
              if ("$jp" -match '^[0-9]+$') {
                $proc = Get-Process -Id ([int]$jp) -ErrorAction SilentlyContinue
                if ($proc -and ($proc.ProcessName -like 'powershell*' -or $proc.ProcessName -like 'pwsh*')) { & taskkill /PID $jp /T /F 2>$null | Out-Null }
              }
              Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $pf
            }
            $c = "$root/repos/{{repoId}}.git"
            function Q { & git {{GitHardeningWindows}} @args 2>$null }
            Q -C $c worktree remove --force "$root/jobs/$id"
            if (Test-Path "$root/jobs/$id") { cmd /c rd /s /q ("$root/jobs/$id").Replace('/','\') 2>$null }
            Q -C $c worktree prune
            Q -C $c update-ref -d "refs/lanq/$id"
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
            C="$R/repos/{{repoId}}.git"
            G() { git {{GitHardeningLinux}} "$@"; }
            G -C "$C" worktree remove --force "$R/jobs/$ID" 2>/dev/null
            rm -rf "$R/jobs/$ID"
            G -C "$C" worktree prune 2>/dev/null
            G -C "$C" update-ref -d "refs/lanq/$ID" 2>/dev/null
            rm -f "$R/incoming/$ID.bundle" "$R/locks/slot-{{SafeSlot(slot)}}"
            exit 0
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
