using System.Globalization;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Policy;

namespace LanQueue.Jobs;

/// <summary>Which part of lanq prune a script does: the job leftovers, or (holding every slot) the repository caches.</summary>
public enum PrunePhase
{
    /// <summary>jobs/&lt;id&gt;, incoming/&lt;id&gt;.*, dead pid files and lanq's own temporary files; needs no slot.</summary>
    Files,

    /// <summary>In each repos/&lt;16 hex&gt;.git: worktree prune, the pruned ids' refs and, with --caches, an old unused cache.</summary>
    Caches,
}

public static partial class RemoteScripts
{
    /// <summary>lanq's job id shape (JobRunner.NewId): the only names lanq prune ever removes under jobs/ and incoming/ (review M11).</summary>
    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}-[0-9a-f]{6}\z", RegexOptions.CultureInvariant, 1000)]
    internal static partial Regex PruneIdRegex();

    /// <summary>
    /// lanq prune (0.13.0 D2), one script per OS sent on stdin (it is too long for a Windows command line). Output lines: <c>REFUSED why</c>,
    /// <c>SLOTFILES=n</c>, <c>WOULD kind name bytes</c> (without <paramref name="remove"/>), <c>REMOVED kind name bytes</c>, <c>FAIL kind name</c>,
    /// <c>SKIP name live</c>.
    /// <para>
    /// Path safety: work_root is resolved first (a linked work_root is fine) and refused when it is a filesystem or drive root or the home
    /// folder, when locks/ is missing (lanq never used it) or when jobs, incoming, locks or repos is a link or not a folder. Only direct
    /// children are listed, each checked against the id rule before anything else; an entry that is itself a link is removed as a link only
    /// (rm -f; [IO.Directory]::Delete(p, $false)); a real folder with rm -rf (which never follows a link inside) or the \\?\ form of rd /s /q.
    /// </para>
    /// <para>
    /// Never removed: an id named on the first line of any slot file (live or stale: lanq unlock decides that), an id whose pid file names a
    /// process with its recorded start time, anything younger than <paramref name="ageSeconds"/>.
    /// </para>
    /// </summary>
    /// <param name="prunedIds">Caches phase: the ids the files phase removed, whose refs/lanq/&lt;id&gt; go too (checked against the id rule).</param>
    public static string Prune(Machine m, PrunePhase phase, long ageSeconds, bool remove, bool caches = false, long cacheAgeSeconds = 0, IReadOnlyCollection<string>? prunedIds = null)
    {
        var ids = (prunedIds ?? []).Where(i => PruneIdRegex().IsMatch(i)).ToList();
        var age = ageSeconds.ToString(CultureInfo.InvariantCulture);
        var cage = cacheAgeSeconds.ToString(CultureInfo.InvariantCulture);
        var doIt = remove ? "1" : "0";
        if (m.Os == MachineOs.Windows)
        {
            var common = $$"""
                $ErrorActionPreference = 'Continue'; $ProgressPreference = 'SilentlyContinue'
                $now = [DateTime]::UtcNow; $age = {{age}}; $do = {{(remove ? "$true" : "$false")}}
                $RP = [IO.FileAttributes]::ReparsePoint
                try { $wi = Get-Item -LiteralPath {{ShellQuote.PowerShell(m.WorkRoot)}} -Force -ErrorAction Stop } catch { 'REFUSED work_root does not exist'; exit 0 }
                $r = $wi.FullName
                if (($wi.Attributes -band $RP) -and $wi.Target) { $r = @($wi.Target)[0] }
                $full = [IO.Path]::GetFullPath($r).TrimEnd('\', '/')
                if ($full -match '^[A-Za-z]:\z' -or ($env:USERPROFILE -and $full -ieq $env:USERPROFILE.TrimEnd('\'))) { 'REFUSED work_root is a drive root or the home folder'; exit 0 }
                foreach ($d in 'jobs', 'incoming', 'locks', 'repos') {
                  $p = Join-Path $full $d
                  if (Test-Path -LiteralPath $p) { $i = Get-Item -LiteralPath $p -Force; if ($i.Attributes -band $RP) { "REFUSED $d is a link"; exit 0 }; if (-not $i.PSIsContainer) { "REFUSED $d is not a folder"; exit 0 } }
                }
                if (-not (Test-Path -LiteralPath "$full\locks")) { 'REFUSED no locks folder: lanq never used this work_root'; exit 0 }
                function Old($i) { ($now - $i.LastWriteTimeUtc).TotalSeconds -ge $age }
                function Size($i) {
                  if ($i.Attributes -band $RP) { return 0 }
                  if (-not $i.PSIsContainer) { return $i.Length }
                  $s = 0; $st = New-Object Collections.Stack; $st.Push($i.FullName)
                  while ($st.Count) { $d = $st.Pop(); try { foreach ($e in ([IO.DirectoryInfo]::new('\\?\' + $d)).EnumerateFileSystemInfos()) { if ($e.Attributes -band $RP) { continue }; if ($e -is [IO.DirectoryInfo]) { $st.Push($e.FullName.Substring(4)) } else { $s += $e.Length } } } catch { } }
                  $s
                }
                function Act($kind, $name, $i) {
                  $b = Size $i; $nm = $name -replace '[^A-Za-z0-9._-]', '_'
                  if (-not $do) { "WOULD $kind $nm $b"; return }
                  $p = $i.FullName
                  if ($i.Attributes -band $RP) { try { if ($i.PSIsContainer) { [IO.Directory]::Delete($p, $false) } else { [IO.File]::Delete($p) } } catch { } }
                  elseif ($i.PSIsContainer) { cmd /c rd /s /q "\\?\$p" 2>$null | Out-Null }
                  else { try { [IO.File]::Delete($p) } catch { } }
                  if (Test-Path -LiteralPath $p) { "FAIL $kind $nm" } else { "REMOVED $kind $nm $b" }
                }
                """;
            return phase == PrunePhase.Files
                ? common + "\n" + $$"""
                    $live = @{}; $ns = 0
                    foreach ($f in @(Get-ChildItem -LiteralPath "$full\locks" -File -Force -EA 0)) {
                      if ($f.Name -cmatch '^slot-[0-9]+\z') { $ns++; try { $live[([IO.File]::ReadAllText($f.FullName) -split "`n")[0].Trim()] = 1 } catch { } }
                      elseif ($f.Name -cmatch '^(?:job|post)-(.+)\.pid\z') {
                        $id = $Matches[1]; $l = @(Get-Content -LiteralPath $f.FullName -EA 0)
                        for ($k = 0; $k + 1 -lt $l.Count; $k += 2) { try { $pr = Get-Process -Id ([int]$l[$k]) -EA Stop; if ($pr.StartTime.ToUniversalTime().Ticks -eq [long]$l[$k + 1]) { $live[$id] = 1 } } catch { } }
                      }
                    }
                    "SLOTFILES=$ns"
                    function IdOk($n) { $n -cmatch '^[0-9]{8}-[0-9]{6}-[0-9a-f]{6}\z' }
                    if (Test-Path -LiteralPath "$full\jobs") {
                      foreach ($i in @(Get-ChildItem -LiteralPath "$full\jobs" -Force -EA 0)) {
                        if (-not (IdOk $i.Name)) { continue }; if ($live[$i.Name]) { "SKIP $($i.Name) live"; continue }; if (Old $i) { Act job $i.Name $i }
                      }
                    }
                    if (Test-Path -LiteralPath "$full\incoming") {
                      foreach ($i in @(Get-ChildItem -LiteralPath "$full\incoming" -Force -EA 0)) {
                        if ($i.Name -cnotmatch '^(.+?)\.(?:bundle|tar\.gz|list)\z') { continue }; $id = $Matches[1]
                        if (-not (IdOk $id)) { continue }; if ($live[$id]) { "SKIP $($i.Name) live"; continue }; if (Old $i) { Act incoming $i.Name $i }
                      }
                    }
                    foreach ($i in @(Get-ChildItem -LiteralPath "$full\locks" -File -Force -EA 0)) {
                      if ($i.Name -cmatch '^(?:job|post)-(.+)\.pid\z') { $id = $Matches[1]; if ((IdOk $id) -and -not $live[$id] -and (Old $i)) { Act pidfile $i.Name $i } }
                      elseif ($i.Name -clike 'slot-*.reclaim.old.*' -or $i.Name -clike '*.drop.*') { if (Old $i) { Act leftover $i.Name $i } }
                    }
                    foreach ($i in @(Get-ChildItem -LiteralPath $full -File -Force -Filter '.lanq-doctor.*' -EA 0)) { if (Old $i) { Act leftover $i.Name $i } }
                    exit 0
                    """
                : common + "\n" + $$"""
                    $cage = {{cage}}; $caches = {{(caches ? "$true" : "$false")}}
                    function Q { $env:GIT_CONFIG_NOSYSTEM = '1'; $env:GIT_CONFIG_GLOBAL = 'NUL'; & git {{GitHardeningWindows}} @args 2>$null }
                    if (-not (Test-Path -LiteralPath "$full\repos")) { exit 0 }
                    foreach ($c in @(Get-ChildItem -LiteralPath "$full\repos" -Directory -Force -EA 0)) {
                      if ($c.Name -cnotmatch '^[0-9a-f]{16}\.git\z' -or ($c.Attributes -band $RP)) { continue }
                      if ($do) { Q -C $c.FullName worktree prune; foreach ($id in @({{string.Join(", ", ids.Select(ShellQuote.PowerShell))}})) { Q -C $c.FullName update-ref -d "refs/lanq/$id" } }
                      if (-not $caches) { continue }
                      if (@(Get-ChildItem -LiteralPath (Join-Path $c.FullName 'worktrees') -Force -EA 0).Count -gt 0) { continue }
                      $newest = [DateTime]::MinValue
                      foreach ($n in 'FETCH_HEAD', 'refs\lanq\base', 'packed-refs') { $f = Join-Path $c.FullName $n; if (Test-Path -LiteralPath $f) { $t = (Get-Item -LiteralPath $f -Force).LastWriteTimeUtc; if ($t -gt $newest) { $newest = $t } } }
                      if (($now - $newest).TotalSeconds -ge $cage) { Act cache $c.Name $c }
                    }
                    exit 0
                    """;
        }

        var preamble = $$"""
            NOW=$(date +%s); AGE={{age}}; DO={{doIt}}
            cd -P {{ShellQuote.Posix(m.WorkRoot)}} 2>/dev/null || { echo "REFUSED work_root does not exist"; exit 0; }
            RR=$(pwd -P); HH=$(cd -P "$HOME" 2>/dev/null && pwd -P)
            if [ "$RR" = "/" ] || { [ -n "$HH" ] && [ "$RR" = "$HH" ]; }; then echo "REFUSED work_root is the filesystem root or the home folder"; exit 0; fi
            for d in jobs incoming locks repos; do
              if [ -L "$RR/$d" ]; then echo "REFUSED $d is a link"; exit 0; fi
              if [ -e "$RR/$d" ] && [ ! -d "$RR/$d" ]; then echo "REFUSED $d is not a folder"; exit 0; fi
            done
            [ -d "$RR/locks" ] || { echo "REFUSED no locks folder: lanq never used this work_root"; exit 0; }
            old() { mt=$(stat -c %Y "$1" 2>/dev/null) || return 1; [ $((NOW - mt)) -ge "$2" ]; }
            act() {
              nm=$(printf '%s' "$2" | tr -c 'A-Za-z0-9._-' '_')
              if [ -L "$3" ]; then k=0; else k=$(du -sk "$3" 2>/dev/null | cut -f1); fi
              case "$k" in ''|*[!0-9]*) k=0 ;; esac
              if [ "$DO" != 1 ]; then echo "WOULD $1 $nm $((k * 1024))"; return 0; fi
              if [ -L "$3" ]; then rm -f "$3"; else rm -rf "$3"; fi
              if [ -e "$3" ] || [ -L "$3" ]; then echo "FAIL $1 $nm"; else echo "REMOVED $1 $nm $((k * 1024))"; fi
            }
            """;
        return phase == PrunePhase.Files
            ? preamble + "\n" + """
                LIVE=" "; NS=0
                for f in "$RR"/locks/slot-*; do
                  [ -f "$f" ] || continue
                  case "${f##*/}" in slot-|slot-*[!0-9]*) continue ;; esac
                  NS=$((NS + 1)); LIVE="$LIVE$(sed -n 1p "$f" 2>/dev/null | tr -cd 'A-Za-z0-9_-') "
                done
                echo "SLOTFILES=$NS"
                alive() {
                  kg=$(sed -n 1p "$1" 2>/dev/null); st=$(sed -n 2p "$1" 2>/dev/null)
                  case "$kg" in ''|*[!0-9]*) return 1 ;; esac
                  [ -r "/proc/$kg/stat" ] || kill -s 0 "$kg" 2>/dev/null || return 1
                  [ -z "$st" ] || [ ! -r "/proc/$kg/stat" ] || [ "$(sed -n 's/.*) //p' "/proc/$kg/stat" 2>/dev/null | cut -d' ' -f20)" = "$st" ]
                }
                for f in "$RR"/locks/job-*.pid "$RR"/locks/post-*.pid; do
                  [ -f "$f" ] || continue
                  n=${f##*/}; n=${n#job-}; n=${n#post-}; n=${n%.pid}
                  if alive "$f"; then LIVE="$LIVE$n "; fi
                done
                idok() { case "$1" in [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) return 0 ;; esac; return 1; }
                live() { case "$LIVE" in *" $1 "*) return 0 ;; esac; return 1; }
                for p in "$RR"/jobs/*; do
                  [ -e "$p" ] || [ -L "$p" ] || continue
                  n=${p##*/}; idok "$n" || continue
                  if live "$n"; then echo "SKIP $n live"; continue; fi
                  old "$p" "$AGE" && act job "$n" "$p"
                done
                for p in "$RR"/incoming/*; do
                  [ -e "$p" ] || [ -L "$p" ] || continue
                  n=${p##*/}
                  case "$n" in *.tar.gz) id=${n%.tar.gz} ;; *.bundle) id=${n%.bundle} ;; *.list) id=${n%.list} ;; *) continue ;; esac
                  idok "$id" || continue
                  if live "$id"; then echo "SKIP $n live"; continue; fi
                  old "$p" "$AGE" && act incoming "$n" "$p"
                done
                for p in "$RR"/locks/job-*.pid "$RR"/locks/post-*.pid; do
                  [ -f "$p" ] && [ ! -L "$p" ] || continue
                  n=${p##*/}; id=${n#job-}; id=${id#post-}; id=${id%.pid}
                  idok "$id" || continue
                  live "$id" && continue
                  old "$p" "$AGE" && act pidfile "$n" "$p"
                done
                for p in "$RR"/.lanq-doctor.* "$RR"/locks/slot-*.reclaim.old.* "$RR"/locks/*.drop.*; do
                  [ -e "$p" ] || [ -L "$p" ] || continue
                  old "$p" "$AGE" && act leftover "${p##*/}" "$p"
                done
                exit 0
                """
            : preamble + "\n" + $$"""
                CAGE={{cage}}; CACHES={{(caches ? "1" : "0")}}
                {{LinuxGitFunction}}
                [ -d "$RR/repos" ] || exit 0
                for c in "$RR"/repos/*.git; do
                  [ -d "$c" ] && [ ! -L "$c" ] || continue
                  n=${c##*/}
                  printf '%s\n' "${n%.git}" | grep -qxE '[0-9a-f]{16}' || continue
                  if [ "$DO" = 1 ]; then
                    G -C "$c" worktree prune 2>/dev/null
                    for id in {{string.Join(' ', ids)}}; do G -C "$c" update-ref -d "refs/lanq/$id" 2>/dev/null; done
                  fi
                  [ "$CACHES" = 1 ] || continue
                  [ -n "$(ls -A "$c/worktrees" 2>/dev/null)" ] && continue
                  newest=0
                  for f in "$c/FETCH_HEAD" "$c/refs/lanq/base" "$c/packed-refs"; do
                    mt=$(stat -c %Y "$f" 2>/dev/null) || continue
                    [ "$mt" -gt "$newest" ] && newest=$mt
                  done
                  [ $((NOW - newest)) -ge "$CAGE" ] && act cache "$n" "$c"
                done
                exit 0
                """;
    }
}
