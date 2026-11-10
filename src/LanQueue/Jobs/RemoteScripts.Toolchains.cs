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
    /// in prints <c>open.dotnet=DIR|VERSION</c> instead and is never proposed for path_prepend, as lanq-makeready 0.3.0 judges it: the folder,
    /// its dotnet, its newest SDK folder, host, host/fxr and its version folders must be owned by Administrators, SYSTEM, TrustedInstaller
    /// (root) or this account with a DACL that gives nobody else more than read/execute (on Linux no write for others, nor for a group other
    /// than root's or the account's own private group); every parent folder must have such an owner and let nobody else delete or rename
    /// what is in it (on Linux: not writable by others or another group unless sticky).
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
                $sw=[Diagnostics.Stopwatch]::StartNew();$seen=@{};try{$me=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}catch{$me=0}
                function T{$sw.Elapsed.TotalSeconds -gt 5}
                function Ok($s,$k){$t='^(BA|SY|CO|OW|S-1-5-(32-544|18|80-956008885-3418522649-1831038044-1853292631-2271478464)|S-1-3-[04]|'+[regex]::Escape($me)+')$'
                if($s -notmatch '^O:([^G]+)G:' -or $Matches[1] -notmatch $t -or $s -notmatch 'D:' -or $s -match 'D:[A-Z]*NO_A'){return 0}
                foreach($m in [regex]::Matches(($s -replace '^.*?D:' -replace 'S:.*'),'\((O|X|Z)?A;([^;]*);([^;]*);[^;]*;[^;]*;([^;)]+)')){if($m.Groups[4].Value -match $t -or($k -and $m.Groups[2].Value -match 'IO')){continue};$r=$m.Groups[3].Value;$q=+($k -eq 1)
                if($k){$x=0x100C0040+0x10000*$q;$c='DT|WD|WO|GA|FA'+'|SD'*$q}else{$x=0x5FEDFF56;$c='(?!CC|SW|WP|LT|RC|GR|GX|FR|FX)..'}
                if($r -match '^0x(.{1,8})$'){if([Convert]::ToInt64($Matches[1],16) -band $x){return 0} }elseif($r -match "^(..)*?($c)"){return 0} };1}
                function Safe($d,$v){$a=@(($d,0),("$d\dotnet.exe",0),("$d\sdk\$v",0))
                foreach($h in @("$d\host","$d\host\fxr")+@(gci -Li "$d\host\fxr" -ad -EA 0|%{$_.FullName})){if(Test-Path -Li $h){$a+=,($h,0)} }
                $p=$d -split '\\';for($i=$p.Count-2;$i -ge 0;$i--){$a+=,($(if($i){$p[0..$i] -join '\'}else{"$($p[0])\"}),$(if($i){1}else{2}))}
                foreach($e in $a){try{$s="$((Get-Acl -Li $e[0] -EA Stop).Sddl)"}catch{return 0};if(!(Ok $s $e[1])){return 0} };1}
                function Sdk($d){if(!$d -or(T)){return};$d="$d".Trim().TrimEnd('\','/');if($seen[$d]){return};$seen[$d]=1
                if(!(Test-Path -Li "$d\dotnet.exe")){return}
                $v=@(gci -Li "$d\sdk" -ad -EA 0|?{$_.Name -match '^\d+\.\d+\.\d+' -and(Test-Path -Li "$($_.FullName)\dotnet.dll")}|%{$_.Name})
                if($v.Count){$b=$v|sort{[version](($_ -split '-')[0])}|select -Last 1;$(if(Safe $d $b){'found'}else{'open'})+'.dotnet='+$d.Replace('\','/')+'|'+$b} }
                foreach($d in @($env:DOTNET_ROOT,$env:DOTNET_ROOT_X64,$env:DOTNET_ROOT_ARM64,"$env:ProgramFiles\dotnet","${env:ProgramFiles(x86)}\dotnet","$env:LOCALAPPDATA\Microsoft\dotnet","$env:USERPROFILE\.dotnet")){Sdk $d}
                foreach($a in 'x64','arm64','x86'){try{Sdk (gp "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\$a" -EA Stop).InstallLocation}catch{} }
                foreach($r in @([IO.DriveInfo]::GetDrives()|?{"$($_.DriveType)" -eq 'Fixed' -and $_.IsReady})){if(T){break};Sdk "$($r.Name)dotnet";$n=0
                try{foreach($x in [IO.Directory]::EnumerateDirectories($r.Name)){if(++$n -gt 64 -or(T)){break};Sdk "$x\dotnet"} }catch{} }
                try{if(!@(dotnet --list-sdks 2>$null)){$v=@(dotnet --list-runtimes 2>$null) -match '^Microsoft.NETCore.App ';if($v){'runtime.dotnet='+($v[-1] -split ' ')[1]} } }catch{}
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
                  u=$(id -u); g=0; [ "$(id -gn)" = "$(id -un)" ] && g=$(id -g)
                  for x in "$d" "$d/dotnet" "$d/sdk/$v" "$d/host" "$d/host/fxr" "$d"/host/fxr/*; do
                    case "$x" in "$d/host"*) [ -e "$x" ] || continue ;; esac
                    x=$(readlink -f -- "$x" 2>/dev/null) && [ -n "$(find "$x" -maxdepth 0 \( -user 0 -o -user "$u" \) ! -perm -0002 \( ! -perm -0020 -o -group 0 -o -group "$g" \) 2>/dev/null)" ] || { echo "open.dotnet=$d|$v"; return 0; }
                  done
                  for a in "$d" "$(readlink -f -- "$d")"; do
                    while a=${a%/*}; [ -n "$a" ]; do
                      [ -n "$(find "$a" -maxdepth 0 \( -user 0 -o -user "$u" \) \( -type l -o -perm -1000 -o \( ! -perm -0002 \( ! -perm -0020 -o -group 0 -o -group "$g" \) \) \) 2>/dev/null)" ] || { echo "open.dotnet=$d|$v"; return 0; }
                    done
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
