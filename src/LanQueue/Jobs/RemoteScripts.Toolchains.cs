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
    /// runtime, it also prints <c>runtime.dotnet=VERSION</c> (the highest Microsoft.NETCore.App). A folder other local users could plant a
    /// dotnet in (the folder or its dotnet not owned by an administrator/SYSTEM/TrustedInstaller/root or this account, or writable by Users,
    /// Authenticated Users, Interactive, Everyone, others or a non-root group) prints <c>open.dotnet=DIR|VERSION</c> instead: it is never
    /// proposed for path_prepend.
    /// </summary>
    public static string FindToolchain(Machine m, string tool)
    {
        if (!FindableTools.Contains(tool))
        {
            throw new ArgumentException("No search is known for this tool.", nameof(tool));
        }

        return m.Os == MachineOs.Windows
            ? $$"""
                {{FindToolchainMarker}} dotnet
                $sw = [Diagnostics.Stopwatch]::StartNew(); $seen = @{}; try { $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } catch { $me = 'none' }
                function Safe($d) {
                  foreach ($p in @($d, "$d\dotnet.exe")) {
                    try { $sd = "$((Get-Acl -LiteralPath $p -ErrorAction Stop).Sddl)" } catch { return $false }
                    if ($sd -notmatch ('^O:(BA|SY|S-1-5-32-544|S-1-5-18|S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464|' + [regex]::Escape($me) + ')(G:|D:|S:|$)')) { return $false }
                    foreach ($m in [regex]::Matches($sd, '\(A;[^;]*;([^;]*);;;(AU|BU|WD|IU|S-1-5-11|S-1-5-32-545|S-1-1-0|S-1-5-4)\)')) { $r = $m.Groups[1].Value
                      if ($r -match '^0x([0-9A-Fa-f]{1,8})$') { if (([Convert]::ToInt64($Matches[1], 16) -band 0x500D0046) -ne 0) { return $false } } elseif ($r -match '^(..)*?(FA|FW|GA|GW|WD|WO|AD|DC|SD)') { return $false } } }
                  $true
                }
                function Sdk($d) {
                  if (-not $d -or $sw.Elapsed.TotalSeconds -gt 5) { return }
                  $d = "$d".Trim().TrimEnd('\', '/'); $k = $d.ToLowerInvariant(); if ($seen[$k]) { return }; $seen[$k] = 1
                  if (-not (Test-Path -LiteralPath "$d\dotnet.exe" -PathType Leaf)) { return }
                  $v = @(Get-ChildItem -LiteralPath "$d\sdk" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^[0-9]+\.[0-9]+\.[0-9]+' -and (Test-Path -LiteralPath "$($_.FullName)\dotnet.dll") } | ForEach-Object { $_.Name })
                  if ($v.Count) { $(if (Safe $d) { 'found' } else { 'open' }) + '.dotnet=' + $d.Replace('\', '/') + '|' + ($v | Sort-Object { [version](($_ -split '-')[0]) } | Select-Object -Last 1) }
                }
                foreach ($d in @($env:DOTNET_ROOT, $env:DOTNET_ROOT_X64, $env:DOTNET_ROOT_ARM64, "$env:ProgramFiles\dotnet", "${env:ProgramFiles(x86)}\dotnet", "$env:LOCALAPPDATA\Microsoft\dotnet", "$env:USERPROFILE\.dotnet")) { Sdk $d }
                foreach ($a in 'x64', 'arm64', 'x86') { try { Sdk (Get-ItemProperty "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\$a" -ErrorAction Stop).InstallLocation } catch { } }
                foreach ($r in @([IO.DriveInfo]::GetDrives() | Where-Object { "$($_.DriveType)" -eq 'Fixed' -and $_.IsReady })) {
                  if ($sw.Elapsed.TotalSeconds -gt 5) { break }; Sdk "$($r.Name)dotnet"; $n = 0
                  try { foreach ($x in [IO.Directory]::EnumerateDirectories($r.Name)) { if (++$n -gt 64 -or $sw.Elapsed.TotalSeconds -gt 5) { break }; Sdk "$x\dotnet" } } catch { }
                }
                try { if (-not @(dotnet --list-sdks 2>$null)) { $v = @(dotnet --list-runtimes 2>$null) -match '^Microsoft\.NETCore\.App '; if ($v) { 'runtime.dotnet=' + ($v[-1] -split ' ')[1] } } } catch { }
                exit 0
                """
            : $$"""
                {{FindToolchainMarker}} dotnet
                end=$(( $(date +%s) + 5 ))
                sdk() {
                  d=${1%/}; [ -n "$d" ] && [ "$(date +%s)" -le "$end" ] && [ -x "$d/dotnet" ] && [ -d "$d/sdk" ] || return 0
                  case " $seen " in *" $d "*) return 0 ;; esac; seen="$seen $d"
                  v=$(ls "$d/sdk" 2>/dev/null | grep -E '^[0-9]+\.[0-9]+\.[0-9]+' | while IFS= read -r s; do [ -f "$d/sdk/$s/dotnet.dll" ] && echo "$s"; done | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)
                  [ -n "$v" ] || return 0
                  for x in "$d" "$d/dotnet"; do
                    x=$(readlink -f -- "$x" 2>/dev/null) && [ -n "$(find "$x" -maxdepth 0 \( -user 0 -o -user "$(id -u)" \) ! -perm -0002 \( ! -perm -0020 -o -group 0 \) 2>/dev/null)" ] || { echo "open.dotnet=$d|$v"; return 0; }
                  done
                  echo "found.dotnet=$d|$v"
                  return 0
                }
                seen=
                for d in "$DOTNET_ROOT" "$DOTNET_ROOT_X64" "$DOTNET_ROOT_ARM64" "$HOME/.dotnet"; do sdk "$d"; done
                for f in /etc/dotnet/install_location /etc/dotnet/install_location_*; do [ -r "$f" ] && sdk "$(sed -n 1p "$f" | tr -d '\r')"; done
                for d in /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet /opt/dotnet /snap/dotnet-sdk/current /usr/local/share/dotnet /opt/homebrew/opt/dotnet/libexec /usr/local/opt/dotnet/libexec; do sdk "$d"; done
                if [ -z "$(dotnet --list-sdks 2>/dev/null)" ]; then v=$(dotnet --list-runtimes 2>/dev/null | sed -n 's/^Microsoft\.NETCore\.App \([^ ]*\).*/\1/p' | tail -n 1); [ -n "$v" ] && echo "runtime.dotnet=$v"; fi
                exit 0
                """;
    }

    /// <summary>The <c>found.TOOL=DIR|VERSION</c> lines of <see cref="FindToolchain"/>, highest version first (ties in the order found).</summary>
    internal static IReadOnlyList<(string Dir, string Version)> ParseFound(string output, string tool)
    {
        var prefix = "found." + tool.ToLowerInvariant() + "=";
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
