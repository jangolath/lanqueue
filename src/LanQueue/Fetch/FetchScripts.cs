using System.Globalization;
using System.Text;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Policy;

namespace LanQueue.Fetch;

/// <summary>
/// The worker side of lanq run --fetch: one read-only script that lists the files matching the patterns below the job's working folder and
/// prints each one as a framed text stream (see <see cref="FetchReceiver"/> for the frame grammar). Every path is printed base64-encoded
/// (UTF-8), so a name with a newline or any other byte can never break a frame line. The worker is not trusted: lanq checks every frame,
/// size, count, path and hash again.
/// </summary>
public static class FetchScripts
{
    /// <summary>Bytes per data line on Windows (4096 base64 characters). Linux uses base64's own 76-column lines.</summary>
    internal const int WindowsChunkBytes = 3072;

    /// <summary>
    /// The Windows fetch script is sent on ssh stdin (it is longer than the encoded command line allows once patterns are added), as base64
    /// of its UTF-16 text so the console code page can never change a character of it.
    /// </summary>
    internal const string WindowsStdinCommand =
        "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd()))))\"";

    /// <summary>The bytes sent on stdin for a Windows worker: base64 of the script's UTF-16 text, then a newline.</summary>
    internal static byte[] WindowsStdin(string script) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'\n" + script)) + "\n");

    /// <summary>
    /// The fetch script for <paramref name="m"/>: matches <paramref name="patterns"/> (validated) below <c>jobs/ID/DIR</c>, whose real path
    /// must stay inside the job folder. Only regular files are sent; symlinks, junctions and other reparse points, hard-linked files
    /// (Linux), devices, FIFOs and sockets are reported as skipped and never followed. Files are taken in sorted order until a cap is hit.
    /// </summary>
    public static string Fetch(Machine m, string jobId, string directory, IReadOnlyList<string> patterns, FetchLimits limits)
    {
        var rel = JobPolicy.Normalize(directory) ?? throw new ArgumentException("Directory is not a safe relative path.", nameof(directory));
        var worktree = $"{m.WorkRoot}/jobs/{jobId}";
        var dir = rel == "." ? worktree : worktree + "/" + rel;
        var roots = FetchPatterns.WalkRoots(patterns);
        var maxFiles = limits.MaxFiles.ToString(CultureInfo.InvariantCulture);
        var maxFile = limits.MaxFileBytes.ToString(CultureInfo.InvariantCulture);
        var maxTotal = limits.MaxTotalBytes.ToString(CultureInfo.InvariantCulture);
        if (m.Os == MachineOs.Windows)
        {
            var winRoots = string.Join(',', roots.Select(ShellQuote.PowerShell));
            return $$"""
                $ErrorActionPreference = 'Stop'
                function O($s) { [Console]::Out.WriteLine($s) }
                function B($s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
                try { $w = (Get-Item -Force -LiteralPath {{ShellQuote.PowerShell(worktree)}}).FullName.TrimEnd('\'); $j = Get-Item -Force -LiteralPath {{ShellQuote.PowerShell(dir)}} } catch { O 'LQF1 ERROR nofolder'; O 'LQF1 DONE'; exit 0 }
                $jp = $j.FullName.TrimEnd('\')
                if (-not ($jp + '\').StartsWith($w + '\', [StringComparison]::OrdinalIgnoreCase)) { O 'LQF1 ERROR outside'; O 'LQF1 DONE'; exit 0 }
                for ($p = $j; $p -and $p.FullName.TrimEnd('\').Length -ge $w.Length; $p = $p.Parent) { if ($p.Attributes -band 1024) { O 'LQF1 ERROR outside'; O 'LQF1 DONE'; exit 0 } }
                $re = New-Object Text.RegularExpressions.Regex ({{ShellQuote.PowerShell(FetchPatterns.ToDotNet(patterns))}}, 'IgnoreCase, CultureInvariant')
                $F = New-Object Collections.Generic.List[string]; $X = New-Object Collections.Generic.List[string]
                foreach ($r in @({{winRoots}})) {
                  $d = $j; $ok = $true
                  if ($r -ne '.') { foreach ($s in $r.Split('/')) { $d = New-Object IO.DirectoryInfo ($d.FullName.TrimEnd('\') + '\' + $s); if (-not $d.Exists) { $ok = $false; break }; if ($d.Attributes -band 1024) { $X.Add('link ' + $r); $ok = $false; break } } }
                  if (-not $ok) { continue }
                  $st = New-Object Collections.Stack; $st.Push($d)
                  while ($st.Count) {
                    $c = $st.Pop(); try { $es = $c.GetFileSystemInfos() } catch { continue }
                    foreach ($e in $es) {
                      $q = $e.FullName.Substring($jp.Length + 1).Replace('\', '/'); $a = [int]$e.Attributes
                      if (($a -band 16) -and -not ($a -band 1024)) { $st.Push($e); continue }
                      if (-not $re.IsMatch($q)) { continue }
                      if ($a -band 1024) { $X.Add('link ' + $q) } elseif (-not ($a -band 16)) { $F.Add($q) }
                    }
                  }
                }
                $F.Sort([StringComparer]::Ordinal); $X.Sort([StringComparer]::Ordinal)
                foreach ($x in $X) { $i = $x.IndexOf(' '); O ('LQF1 SKIP ' + $x.Substring(0, $i) + ' ' + (B $x.Substring($i + 1))) }
                $n = 0; [long]$t = 0; $buf = New-Object byte[] {{WindowsChunkBytes}}
                foreach ($q in $F) {
                  try { $h = [IO.File]::Open($jp + '\' + $q.Replace('/', '\'), 'Open', 'Read', 'Read') } catch { O ('LQF1 SKIP unreadable ' + (B $q)); continue }
                  try {
                    $z = $h.Length
                    if ($n -ge {{maxFiles}}) { O ('LQF1 SKIP count ' + (B $q)); continue }
                    if ($z -gt {{maxFile}}) { O ('LQF1 SKIP size ' + (B $q)); continue }
                    if ($t + $z -gt {{maxTotal}}) { O ('LQF1 SKIP total ' + (B $q)); continue }
                    $n++; $t += $z; O ('LQF1 FILE ' + $z + ' ' + (B $q)); $k = [Security.Cryptography.SHA256]::Create()
                    try {
                      do {
                        $m = 0; while ($m -lt {{WindowsChunkBytes}}) { $g = $h.Read($buf, $m, {{WindowsChunkBytes}} - $m); if ($g -le 0) { break }; $m += $g }
                        if ($m) { [void]$k.TransformBlock($buf, 0, $m, $null, 0); O ([Convert]::ToBase64String($buf, 0, $m)) }
                      } while ($m -eq {{WindowsChunkBytes}})
                      [void]$k.TransformFinalBlock($buf, 0, 0); O ('LQF1 END ' + [BitConverter]::ToString($k.Hash).Replace('-', '').ToLowerInvariant())
                    } catch { O 'LQF1 ABORT' }
                  } finally { $h.Close() }
                }
                O 'LQF1 DONE'
                """;
        }

        // A walk root that is itself a link is reported on fd 3 (the script's stdout), past the awk filter. Nothing else writes until sort has
        // read all of its input, so those lines can never land inside a file's frame.
        var shRoots = string.Join(' ', roots.Select(r => ShellQuote.Posix(r == "." ? "." : "./" + r)));
        return $$"""
            exec 2>/dev/null 3>&1
            W={{ShellQuote.Posix(worktree)}}; J={{ShellQuote.Posix(dir)}}
            cd -P "$J" || { echo 'LQF1 ERROR nofolder'; echo 'LQF1 DONE'; exit 0; }
            J=$(pwd -P); case "$J/" in "$(cd -P "$W" && pwd -P)"/*) ;; *) echo 'LQF1 ERROR outside'; echo 'LQF1 DONE'; exit 0 ;; esac
            for t in base64 sha256sum find awk sort readlink wc tr cut; do command -v "$t" >/dev/null || { echo "LQF1 ERROR tool-$t"; echo 'LQF1 DONE'; exit 0; }; done
            b() { printf '%s' "$1" | base64 | tr -d '\n'; }
            for r in {{shRoots}}; do
              [ -d "$r" ] || continue
              if [ -L "$r" ]; then echo "LQF1 SKIP link $(b "${r#./}")" >&3; continue; fi
              case "$(cd -P "$r" && pwd -P)/" in "$J"/*) ;; *) echo "LQF1 SKIP outside $(b "${r#./}")" >&3; continue ;; esac
              find "$r" \( -name '*[[:cntrl:]]*' -prune -exec sh -c 'for f; do g=${f#./}; printf "C./%s\t" "$(printf "%s" "$g" | tr "\001-\037\177" "?")"; printf "%s" "$g" | base64 | tr -d "\n"; echo; done' sh {} + \) \
                -o -type l -exec printf 'L%s\n' {} + -o -type f -links 1 -exec printf 'F%s\n' {} + -o -type f -exec printf 'H%s\n' {} + -o ! -type d -exec printf 'O%s\n' {} +
            done | LQ_RE={{ShellQuote.Posix(FetchPatterns.ToEre(patterns))}} awk '{ t = substr($0, 1, 1); p = substr($0, 4); n = p; if (t == "C") { i = index(p, "\t"); n = substr(p, 1, i - 1); p = substr(p, i + 1) } if (n ~ ENVIRON["LQ_RE"]) print t p }' | LC_ALL=C sort -u | {
            n=0; s=0
            while IFS= read -r l; do
              t=${l%"${l#?}"}; p=${l#?}; f="./$p"
              case "$t" in
                C) echo "LQF1 SKIP name $p"; continue ;;
                L) echo "LQF1 SKIP link $(b "$p")"; continue ;;
                H) echo "LQF1 SKIP hardlink $(b "$p")"; continue ;;
                O) echo "LQF1 SKIP special $(b "$p")"; continue ;;
              esac
              case "$(readlink -f "$f")" in "$J"/*) ;; *) echo "LQF1 SKIP outside $(b "$p")"; continue ;; esac
              if [ -L "$f" ] || [ ! -f "$f" ]; then echo "LQF1 SKIP special $(b "$p")"; continue; fi
              z=$(wc -c < "$f" | tr -d ' ')
              case "$z" in ''|*[!0-9]*) echo "LQF1 SKIP unreadable $(b "$p")"; continue ;; esac
              if [ "$n" -ge {{maxFiles}} ]; then echo "LQF1 SKIP count $(b "$p")"
              elif [ "$z" -gt {{maxFile}} ]; then echo "LQF1 SKIP size $(b "$p")"
              elif [ $((s + z)) -gt {{maxTotal}} ]; then echo "LQF1 SKIP total $(b "$p")"
              else n=$((n + 1)); s=$((s + z)); echo "LQF1 FILE $z $(b "$p")"; base64 < "$f"; echo "LQF1 END $(sha256sum < "$f" | cut -c1-64)"
              fi
            done
            echo 'LQF1 DONE'
            }
            """;
    }
}

/// <param name="MaxFiles">Most files fetched (default 500).</param>
/// <param name="MaxFileBytes">Largest single file (default: the total cap).</param>
/// <param name="MaxTotalBytes">Most bytes fetched in all (default 200 MB, at most <see cref="CeilingBytes"/>).</param>
public sealed record FetchLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes)
{
    public const int DefaultMaxFiles = 500;

    public const int CeilingFiles = 5000;

    public const long DefaultMaxTotalBytes = 200L * 1024 * 1024;

    /// <summary>The hard ceiling for --fetch-max: 2 GB.</summary>
    public const long CeilingBytes = 2L * 1024 * 1024 * 1024;

    public static FetchLimits Default { get; } = new(DefaultMaxFiles, DefaultMaxTotalBytes, DefaultMaxTotalBytes);
}
