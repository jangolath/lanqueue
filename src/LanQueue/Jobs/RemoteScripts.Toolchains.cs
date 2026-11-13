using LanQueue.Config;

namespace LanQueue.Jobs;

/// <summary>
/// Finding a toolchain that is installed but not on the ssh session's PATH (a private folder such as D:/build/dotnet, a dotnet-install
/// default, a version manager). Read-only: folders are listed and version folders read, nothing is written, and the only program started is
/// the dotnet on PATH, for <c>dotnet --list-sdks</c> and <c>--list-runtimes</c> (to tell a runtime-only install); the search
/// is bounded (fixed places, one folder level below each fixed drive root on Windows, at most 64 folders a drive) and stops after about five
/// seconds. lanq only reports what it found and suggests the path_prepend: line; it never edits a config or a worker's PATH.
/// </summary>
public static partial class RemoteScripts
{
    /// <summary>Marks the toolchain search script (tests find it by this comment).</summary>
    internal const string FindToolchainMarker = "# lanq: find toolchain";

    /// <summary>The tools <see cref="FindToolchain"/> can look for.</summary>
    internal static readonly IReadOnlySet<string> FindableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dotnet" };

    /// <summary>
    /// Prints <c>found.TOOL=DIR|VERSION</c> for each folder holding the tool, DIR with forward slashes, VERSION the highest one there (for dotnet
    /// the highest SDK; a folder with a runtime only is not a hit). dotnet: $DOTNET_ROOT (and _X64, _ARM64) of the ssh session and ~/.dotnet
    /// everywhere; on Windows also %ProgramFiles%\dotnet, %ProgramFiles(x86)%\dotnet, %LOCALAPPDATA%\Microsoft\dotnet (the dotnet-install
    /// default), %USERPROFILE%\.dotnet, the registry's InstalledVersions\ARCH\InstallLocation, and DRIVE:\dotnet and DRIVE:\DIR\dotnet on every
    /// fixed drive; on Linux and macOS /etc/dotnet/install_location (and _ARCH), /usr/share/dotnet, /usr/lib/dotnet, /usr/lib64/dotnet,
    /// /opt/dotnet, /snap/dotnet-sdk/current, /usr/local/share/dotnet and the Homebrew folders. When the dotnet on PATH has no SDK but a
    /// runtime, it also prints <c>runtime.dotnet=VERSION</c> (the highest Microsoft.NETCore.App). A folder other local users could plant code
    /// in prints <c>open.dotnet=DIR|VERSION</c> instead and is never proposed for path_prepend, as lanq-makeready 0.4.1 judges it. Trusted:
    /// Administrators, SYSTEM, TrustedInstaller (root), members of the local Administrators group and this account. The folder, its dotnet,
    /// sdk and the newest SDK folder, host, host/fxr, shared (every framework), packs, sdk-manifests and their version folders: a trusted owner and a
    /// DACL giving nobody else more than read/execute (on Linux no write for others, nor for a group other than root's or the account's own
    /// private group, by the mode bits and by POSIX ACL: <see cref="LinuxAclCheck"/>, as lanq-makeready 0.4.1). Every parent folder: a trusted
    /// owner, and nobody else may delete or rename what is in it (on Linux: not writable by others or another group, by mode or ACL, unless
    /// sticky). Only absolute folders; on Windows a junction or link folder is refused.
    /// </summary>
    public static string FindToolchain(Machine m, string tool)
    {
        if (!FindableTools.Contains(tool))
        {
            throw new ArgumentException("No search is known for this tool.", nameof(tool));
        }

        return FindToolchainMarker + " dotnet\n" + (m.Os == MachineOs.Windows ? WindowsDotnetSearch : LinuxDotnetSearch);
    }

    /// <summary>The script lanq sends on stdin (see <see cref="FindToolchainCommand"/>): it runs from memory, so no command-line limit applies.</summary>
    internal static string FindToolchainCommand(MachineOs os) => os == MachineOs.Windows
        ? "powershell -NoProfile -NonInteractive -Command \"& ([scriptblock]::Create([Console]::In.ReadToEnd()))\""
        : "sh -s";

    private const string WindowsDotnetSearch = """
                $tr = @(); try { $tr += [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } catch { }
                try { $tr += @((Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop).SID.Value) } catch { }
                $sw = [Diagnostics.Stopwatch]::StartNew(); $seen = @{}
                function Test-Sddl([string]$Sd, [int]$Kind) {
                  $trust = '^(BA|SY|CO|OW|S-1-5-32-544|S-1-5-18|S-1-3-0|S-1-3-4|S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464|' + ((@($tr) | Where-Object { $_ } | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'
                  if ($Sd -notmatch '^O:([^G]+)G:' -or $Matches[1] -notmatch $trust) { return $false }
                  if ($Sd -notmatch 'D:' -or $Sd -match 'D:[A-Z]*NO_ACCESS_CONTROL') { return $false }
                  foreach ($m in [regex]::Matches(($Sd -replace '^.*?D:', ''), '\((A|OA|XA|ZA);([^;]*);([^;]*);[^;]*;[^;]*;([^;)]+)[^)]*\)')) {
                    if ($m.Groups[4].Value -match $trust -or ($Kind -gt 0 -and $m.Groups[2].Value -match '^(..)*?IO')) { continue }
                    $r = $m.Groups[3].Value
                    if ($Kind -eq 0) { $mask = 0x5FEDFF56; $codes = '^(..)*?(?!CC|SW|WP|LO|LT|RC|GR|GX|FR|FX)..' }
                    else { $mask = 0x100C0040 + 0x10000 * [int]($Kind -eq 1); $codes = '^(..)*?(DT|WD|WO|GA|FA|KA' + ('|SD' * [int]($Kind -eq 1)) + ')' }
                    if ($r -match '^0x([0-9A-Fa-f]{1,8})$') { try { $hv = [Convert]::ToInt64($Matches[1], 16) } catch { return $false }; if (($hv -band $mask) -ne 0) { return $false } }
                    elseif ($r -match $codes) { return $false } }
                  return $true
                }
                function Test-SafeFolder([string]$Dir, [string]$Sdk) {
                  $chk = @(@($Dir, 0), @("$Dir\dotnet.exe", 0), @("$Dir\sdk", 0), @("$Dir\sdk\$Sdk", 0), @("$Dir\host", 3), @("$Dir\host\fxr", 3), @("$Dir\shared", 3), @("$Dir\packs", 3), @("$Dir\sdk-manifests", 3))
                  $fw = @(Get-ChildItem -LiteralPath "$Dir\shared" -Directory -ErrorAction SilentlyContinue)
                  foreach ($x in @(Get-ChildItem -LiteralPath "$Dir\host\fxr" -Directory -ErrorAction SilentlyContinue) + $fw + @($fw | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue })) { $chk += , @($x.FullName, 0) }
                  $parts = @($Dir -split '\\')
                  for ($i = $parts.Count - 2; $i -ge 0; $i--) { if ($i -eq 0) { $chk += , @("$($parts[0])\", 2) } else { $chk += , @(($parts[0..$i] -join '\'), 1) } }
                  foreach ($c in $chk) {
                    $k = $c[1]; if ($k -eq 3) { if (-not (Test-Path -LiteralPath $c[0])) { continue }; $k = 0 }
                    try { $it = Get-Item -LiteralPath $c[0] -Force -ErrorAction Stop; $sd = "$((Get-Acl -LiteralPath $c[0] -ErrorAction Stop).Sddl)" } catch { return $false }
                    if ($k -ne 2 -and $it.PSIsContainer -and ($it.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
                    if (-not (Test-Sddl $sd $k)) { return $false } }
                  return $true
                }
                function Sdk($d) {
                  if (-not $d -or $sw.Elapsed.TotalSeconds -gt 5) { return }
                  $d = "$d".Trim() -replace '/', '\' -replace '\\+$', ''
                  if ($d -notmatch '^[A-Za-z]:\\' -or $seen[$d]) { return }; $seen[$d] = 1
                  if (-not (Test-Path -LiteralPath "$d\dotnet.exe" -PathType Leaf)) { return }
                  $v = @(Get-ChildItem -LiteralPath "$d\sdk" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^[0-9]+\.[0-9]+\.[0-9]+' -and (Test-Path -LiteralPath "$($_.FullName)\dotnet.dll") } | ForEach-Object { $_.Name })
                  if ($v.Count) {
                    $b = $v | Sort-Object { [version](($_ -split '-')[0]) } | Select-Object -Last 1
                    $(if (Test-SafeFolder $d $b) { 'found' } else { 'open' }) + '.dotnet=' + ($d -replace '\\', '/') + '|' + $b
                  }
                }
                foreach ($d in @($env:DOTNET_ROOT, $env:DOTNET_ROOT_X64, $env:DOTNET_ROOT_ARM64, "$env:ProgramFiles\dotnet", "${env:ProgramFiles(x86)}\dotnet", "$env:LOCALAPPDATA\Microsoft\dotnet", "$env:USERPROFILE\.dotnet")) { Sdk $d }
                foreach ($a in 'x64', 'arm64', 'x86') { try { Sdk (Get-ItemProperty "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\$a" -ErrorAction Stop).InstallLocation } catch { } }
                foreach ($r in @([IO.DriveInfo]::GetDrives() | Where-Object { "$($_.DriveType)" -eq 'Fixed' -and $_.IsReady })) {
                  if ($sw.Elapsed.TotalSeconds -gt 5) { break }; Sdk "$($r.Name)dotnet"; $n = 0
                  try { foreach ($x in [IO.Directory]::EnumerateDirectories($r.Name)) { if (++$n -gt 64 -or $sw.Elapsed.TotalSeconds -gt 5) { break }; Sdk "$x\dotnet" } } catch { }
                }
                try { if (-not @(dotnet --list-sdks 2>$null)) { $v = @(dotnet --list-runtimes 2>$null) -match '^Microsoft\.NETCore\.App '; if ($v) { 'runtime.dotnet=' + ($v[-1] -split ' ')[1] } } } catch { }
                exit 0
        """;

    /// <summary>
    /// The POSIX ACL part of the Linux safe-folder rule, as lanq-makeready 0.4.1's acl_ok judges it (kept identical; tests run it with fake
    /// getfacl, ls and uname). <c>acl DIR</c> is false (open) when another user could write there by ACL. It reads <c>getfacl -cpn</c> (at
    /// most 5 s): a named <c>user:ID</c> or <c>group:ID</c> entry whose effective rights (after <c>#effective:</c>) include w, or a
    /// <c>default:user:ID</c>/<c>default:group:ID</c> entry whose own rights do (new folders inherit it), for an ID other than 0, this user
    /// (<c>u</c>) or the private group (<c>g</c>), is open; so are getfacl failing or timing out and an output with no <c>user::</c> line.
    /// Without getfacl: <c>ls -ld</c> failing, or its 11th character being + or missing, is open, and with BusyBox ls (no marker) any group
    /// write is. Not read on macOS.
    /// </summary>
    internal const string LinuxAclCheck = """
                tm() { # tm CMD ARGS: at most 5 s, stdin closed
                  if command -v timeout >/dev/null 2>&1; then timeout 5 "$@" </dev/null; return $?; fi
                  "$@" </dev/null & tp=$!; ( sleep 5; kill "$tp" ) >/dev/null 2>&1 & tk=$!; wait "$tp"; ts=$?; kill "$tk" >/dev/null 2>&1; return "$ts"
                }
                acl() { # $1 = folder: false (open) when another user could write there by POSIX ACL (as lanq-makeready 0.4.1 judges it)
                  [ "$(uname -s)" = Darwin ] && return 0
                  if command -v getfacl >/dev/null 2>&1; then
                    al=$(tm getfacl -cpn -- "$1" 2>/dev/null) || return 1
                    printf '%s\n' "$al" | awk -v u="$u" -v g="$g" '{ e = $0; eff = ""; i = index(e, "#effective:"); if (i) { eff = substr(e, i + 11); e = substr(e, 1, i - 1) } gsub(/[ \t]+$/, "", e); gsub(/[ \t]/, "", eff); n = split(e, f, ":"); if (n == 3 && f[1] == "user" && f[2] == "") seen = 1; if (n == 4 && f[1] == "default") { t = f[2]; id = f[3]; p = f[4] } else if (n == 3) { t = f[1]; id = f[2]; p = (eff != "") ? eff : f[3] } else next; if (p !~ /w/ || id == "" || id == 0) next; if ((t == "user" && id != u) || (t == "group" && id != g)) bad = 1 } END { exit (bad || !seen) }'
                  else
                    am=$(tm ls -ld -- "$1" 2>/dev/null) || return 1
                    case "$am" in ??????????[!+]*) ;; *) return 1 ;; esac
                    case "$(tm ls --help 2>&1)" in *BusyBox*) case "$am" in ?????w*) return 1 ;; esac ;; esac
                  fi
                }

        """;

    private const string LinuxDotnetSearch = LinuxAclCheck + """
                end=$(( $(date +%s) + 5 ))
                u=$(id -u); g=0; [ "$(id -gn)" = "$(id -un)" ] && g=$(id -g)
                safe() { # $1 = folder, $2 = its newest SDK: no other local user can plant code a job's dotnet runs (as lanq-makeready 0.4.1 judges it)
                  for x in "$1" "$1/dotnet" "$1/sdk" "$1/sdk/$2" "$1/host" "$1/host/fxr" "$1"/host/fxr/* "$1/shared" "$1"/shared/* "$1"/shared/*/* "$1/packs" "$1/sdk-manifests"; do
                    case "$x" in "$1/host"*|"$1/shared"*|"$1/packs"|"$1/sdk-manifests") [ -e "$x" ] || continue ;; esac
                    x=$(readlink -f -- "$x" 2>/dev/null) && [ -n "$(find "$x" -maxdepth 0 \( -user 0 -o -user "$u" \) ! -perm -0002 \( ! -perm -0020 -o -group 0 -o -group "$g" \) 2>/dev/null)" ] || return 1
                    acl "$x" || return 1
                  done
                  r=$(readlink -f -- "$1" 2>/dev/null) || return 1
                  for a in "$1" "$r"; do
                    while n=${a%/*}; [ "$n" != "$a" ] && [ -n "$n" ]; do
                      a=$n
                      [ -n "$(find "$a" -maxdepth 0 \( -user 0 -o -user "$u" \) \( -type l -o -perm -1000 -o \( ! -perm -0002 \( ! -perm -0020 -o -group 0 -o -group "$g" \) \) \) 2>/dev/null)" ] || return 1
                      [ -L "$a" ] || [ -n "$(find "$a" -maxdepth 0 -perm -1000 2>/dev/null)" ] || acl "$a" || return 1
                    done
                  done
                  return 0
                }
                sdk() {
                  d=${1%/}; case "$d" in /*) ;; *) return 0 ;; esac
                  [ "$(date +%s)" -le "$end" ] && [ -x "$d/dotnet" ] && [ -d "$d/sdk" ] || return 0
                  case " $seen " in *" $d "*) return 0 ;; esac; seen="$seen $d"
                  v=$(ls "$d/sdk" 2>/dev/null | grep -E '^[0-9]+\.[0-9]+\.[0-9]+' | while IFS= read -r s; do [ -f "$d/sdk/$s/dotnet.dll" ] && echo "$s"; done | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)
                  [ -n "$v" ] || return 0
                  if safe "$d" "$v"; then echo "found.dotnet=$d|$v"; else echo "open.dotnet=$d|$v"; fi
                  return 0
                }
                seen=
                for d in "$DOTNET_ROOT" "$DOTNET_ROOT_X64" "$DOTNET_ROOT_ARM64" "$HOME/.dotnet"; do sdk "$d"; done
                for f in /etc/dotnet/install_location /etc/dotnet/install_location_*; do [ -r "$f" ] && sdk "$(sed -n 1p "$f" | tr -d '\r')"; done
                for d in /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet /opt/dotnet /snap/dotnet-sdk/current /usr/local/share/dotnet /opt/homebrew/opt/dotnet/libexec /usr/local/opt/dotnet/libexec; do sdk "$d"; done
                if [ -z "$(dotnet --list-sdks 2>/dev/null)" ]; then v=$(dotnet --list-runtimes 2>/dev/null | sed -n 's/^Microsoft\.NETCore\.App \([^ ]*\).*/\1/p' | tail -n 1); [ -n "$v" ] && echo "runtime.dotnet=$v"; fi
                exit 0
        """;

    /// <summary>
    /// The <c>found.TOOL=DIR|VERSION</c> lines of <see cref="FindToolchain"/> (or, with <paramref name="kind"/> "open", the folders it skipped
    /// because other local users could write there), highest version first (ties in the order found).
    /// </summary>
    internal static IReadOnlyList<(string Dir, string Version)> ParseFound(string output, string tool, string kind = "found")
    {
        var prefix = kind + "." + tool.ToLowerInvariant() + "=";
        var hits = new List<(string Dir, string Version, Version Sort, int Order)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var bar = line.LastIndexOf('|');
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || bar <= prefix.Length)
            {
                continue;
            }

            var dir = line[prefix.Length..bar];
            var version = line[(bar + 1)..];
            if (System.Version.TryParse(version.Split('-')[0], out var sort) && dir.Length <= 260 && !dir.Any(char.IsControl) && hits.TrueForAll(h => h.Dir != dir))
            {
                hits.Add((dir, version, sort, hits.Count));
            }
        }

        return [.. hits.OrderByDescending(h => h.Sort).ThenBy(h => h.Order).Select(h => (h.Dir, h.Version))];
    }
}
