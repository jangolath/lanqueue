using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LanQueue.Config;
using LanQueue.Policy;

namespace LanQueue.Jobs;

/// <summary>
/// Windows helpers that work where WMI/CIM is denied to the ssh account (a non-admin build user on a machine tuned as a server): there
/// <c>Get-CimInstance</c> and <c>taskkill</c> (also without /T) answer "Access denied". The helpers call kernel32 directly through
/// P/Invoke methods defined at run time with Reflection.Emit (no compiler, a few milliseconds; Windows PowerShell's FullLanguage mode, as
/// Add-Type needs too). Where even that is refused (ConstrainedLanguage), <c>$LqW</c> stays null and every helper falls back to what lanq
/// did before (taskkill).
/// <list type="bullet">
/// <item><c>LqJob PIDFILE HANDLE</c>: a job object (named after the job's pid file, so a later ssh call can open it) with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, with the process assigned; returns its handle or nothing. Every process the job starts inherits it,
/// so the whole tree ends with TerminateJobObject, or by itself when the wrapper holding the handle dies.</item>
/// <item><c>LqEndJob PIDFILE</c>: opens that named job object (it exists only while its wrapper is alive) and terminates it.</item>
/// <item><c>LqKill PID</c>: that process and its descendants, found with a CreateToolhelp32Snapshot walk (a child counts only when it
/// started no earlier than its parent, so a reused parent pid adds nothing), stopped with Stop-Process; taskkill /T without the helper.</item>
/// </list>
/// The helper is too long to inline in scripts that are near the Windows command-line cap, so <see cref="EnsureIncoming"/> writes it to
/// <see cref="WindowsHelperPath"/> before every job (again whenever the file there is not exactly lanq's text) and the job, stop, cleanup,
/// slot-reclaim and doctor scripts run it only when its SHA-256 is the one lanq computed from its own text (<see cref="WindowsHelperLoad"/>):
/// a job runs as the same account and could otherwise rewrite the file to run code in another job's scripts. The probe, which runs before any job, carries its own small load fallback (<see cref="WindowsProbeLoad"/>).
/// </summary>
public static partial class RemoteScripts
{
    /// <summary>A kernel32 function the helper uses: return type and parameter types as PowerShell type literals.</summary>
    private static string NativeSignature(string function) => function switch
    {
        "CreateJobObjectW" => "[IntPtr],[IntPtr],[string]",
        "OpenJobObjectW" => "[IntPtr],[int],[int],[string]",
        "SetInformationJobObject" => "[int],[IntPtr],[int],[byte[]],[int]",
        "AssignProcessToJobObject" => "[int],[IntPtr],[IntPtr]",
        "TerminateJobObject" => "[int],[IntPtr],[int]",
        "CloseHandle" => "[int],[IntPtr]",
        "CreateToolhelp32Snapshot" => "[IntPtr],[int],[int]",
        "Process32FirstW" or "Process32NextW" => "[int],[IntPtr],[byte[]]",
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };

    /// <summary>
    /// Defines <c>$LqW</c>, a type with the named kernel32 functions as static methods (PreserveSig, so a BOOL comes back as the int it is),
    /// or leaves it null when that is not allowed.
    /// </summary>
    private static string WindowsNative(params string[] functions) =>
        "$LqW=$null;try{$LqW=[AppDomain]::CurrentDomain.DefineDynamicAssembly('LqW','Run').DefineDynamicModule('LqW').DefineType('LqW')\n"
        + "foreach($LqD in " + string.Join(',', functions.Select(f => $"('{f}',{NativeSignature(f)})")) + "){$LqW.DefinePInvokeMethod($LqD[0],'kernel32.dll','Public,Static,PinvokeImpl','Standard',$LqD[1],[Type[]]@($LqD[2..9]),'Winapi','Unicode').SetImplementationFlags('PreserveSig')}\n"
        + "$LqW=$LqW.CreateType()}catch{$LqW=$null}";

    /// <summary>
    /// The job wrapper's helper (see the class summary). The job object is named "lanq-" plus the pid file path with anything but a-z 0-9 . _ -
    /// as '_' (unique per work_root and job). JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on 64-bit (112 on 32-bit) with LimitFlags at
    /// offset 16; 0x2000 is JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, class 9 the extended limit information. PROCESSENTRY32W is 568 bytes on 64-bit
    /// (556 on 32-bit), the pid at offset 8 and the parent pid at 32 (24).
    /// </summary>
    internal static string WindowsJobHelper =>
        WindowsNative("CreateJobObjectW", "SetInformationJobObject", "AssignProcessToJobObject", "OpenJobObjectW", "TerminateJobObject", "CloseHandle", "CreateToolhelp32Snapshot", "Process32FirstW", "Process32NextW")
        + "\n" + """
        function LqName($f){'lanq-'+("$f".ToLowerInvariant() -replace '[^a-z0-9._-]','_')}
        function LqJob($f,$h){if(!$LqW){return};$j=$LqW::CreateJobObjectW([IntPtr]::Zero,(LqName $f));if($j -eq [IntPtr]::Zero){return};$e=[byte[]]::new($(if([IntPtr]::Size -eq 8){144}else{112}));$e[17]=32;if($LqW::SetInformationJobObject($j,9,$e,$e.Length) -and $LqW::AssignProcessToJobObject($j,$h)){return $j};[void]$LqW::CloseHandle($j)}
        function LqEndJob($f){if($LqW){$j=$LqW::OpenJobObjectW(8,0,(LqName $f));if($j -ne [IntPtr]::Zero){[void]$LqW::TerminateJobObject($j,1);[void]$LqW::CloseHandle($j)}}}
        function LqKill($x){if(!$LqW){taskkill /PID $x /T /F 2>$null|Out-Null;Stop-Process -Id $x -Force -EA 0;return}
        $q=[IntPtr]::Size -eq 8;$e=[byte[]]::new($(if($q){568}else{556}));[BitConverter]::GetBytes($e.Length).CopyTo($e,0);$m=@{}
        $s=$LqW::CreateToolhelp32Snapshot(2,0);if($LqW::Process32FirstW($s,$e)){do{$m[[BitConverter]::ToInt32($e,8)]=[BitConverter]::ToInt32($e,$(if($q){32}else{24}))}while($LqW::Process32NextW($s,$e))};[void]$LqW::CloseHandle($s)
        $o=[Collections.ArrayList]@([int]$x);for($i=0;$i -lt $o.Count;$i++){$a=(Get-Process -Id $o[$i] -EA 0).StartTime;if($a){foreach($k in @($m.Keys)){if($m[$k] -eq $o[$i] -and $k -ne $o[$i] -and !$o.Contains($k) -and (Get-Process -Id $k -EA 0).StartTime -ge $a){[void]$o.Add($k)}}}}
        foreach($k in $o){Stop-Process -Id $k -Force -EA 0}}
        """;

    /// <summary>
    /// The probe's load where CIM is denied: CPU use from two GetSystemTimes samples a second apart (idle against kernel plus user time, all
    /// processors), as a percentage in <c>$cpu</c>, and <c>$ls</c> set to native. Leaves both alone when it cannot measure.
    /// </summary>
    internal const string WindowsProbeLoad =
        "$w=[AppDomain]::CurrentDomain.DefineDynamicAssembly('LqL','Run').DefineDynamicModule('LqL').DefineType('LqL');$r=[long].MakeByRefType()\n"
        + "$w.DefinePInvokeMethod('GetSystemTimes','kernel32.dll','Public,Static,PinvokeImpl','Standard',[int],@($r,$r,$r),'Winapi','Unicode').SetImplementationFlags('PreserveSig');$w=$w.CreateType()\n"
        + "$i1=0L;$k1=0L;$u1=0L;$i2=0L;$k2=0L;$u2=0L\n"
        + "if($w::GetSystemTimes([ref]$i1,[ref]$k1,[ref]$u1)){Start-Sleep -Milliseconds 1000;if($w::GetSystemTimes([ref]$i2,[ref]$k2,[ref]$u2) -and ($t=$k2-$k1+$u2-$u1) -gt 0){$cpu=100*[Math]::Min(1.0,[Math]::Max(0.0,1.0-($i2-$i1)/$t));$ls='native'}}";

    /// <summary>
    /// lanq doctor --remote on a Windows machine: <c>wmi=ok</c>, or <c>wmi=fail|ERROR</c> when the ssh user may not use CIM (lanq then uses its
    /// fallbacks, see <see cref="WindowsJobHelper"/>). Its own small call, so the doctor script keeps its room under the command-line cap.
    /// </summary>
    internal const string WmiCheck = "try { $null = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop; 'wmi=ok' } catch { 'wmi=fail|' + \"$_\".Trim() }";

    /// <summary>Where <see cref="EnsureIncoming"/> keeps <see cref="WindowsJobHelper"/> on a Windows worker; the name carries a hash of its text, so a changed helper is a new file.</summary>
    internal static string WindowsHelperPath(Machine m) => $"{m.WorkRoot}/bin/lanq-{WindowsJobHelperHash[..10]}.ps1";

    /// <summary>SHA-256 of the UTF-8 bytes of <see cref="WindowsJobHelper"/>, lowercase hex (its first ten characters name the file).</summary>
    internal static string WindowsJobHelperHash => Convert.ToHexStringLower(WindowsJobHelperDigest);

    /// <summary>The same SHA-256 in base64 (shorter than hex under the command-line cap): the only content a worker script will run.</summary>
    internal static string WindowsJobHelperBase64 => Convert.ToBase64String(WindowsJobHelperDigest);

    private static byte[] WindowsJobHelperDigest => SHA256.HashData(Encoding.UTF8.GetBytes(WindowsJobHelper));

    /// <summary>
    /// PowerShell: true when the file at <paramref name="variable"/> holds exactly <see cref="WindowsJobHelper"/>; its bytes are left in
    /// <c>$LqB</c>, so what was checked is what runs (no second read). Throws when the file cannot be read.
    /// </summary>
    private static string WindowsHelperVerified(string variable) =>
        "([Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash(($LqB=[IO.File]::ReadAllBytes(" + variable + "))))-ceq'" + WindowsJobHelperBase64 + "')";

    /// <summary>
    /// Loads <see cref="WindowsJobHelper"/> from <paramref name="pathExpression"/> (a PowerShell expression) when the file there is lanq's own
    /// text, byte for byte (<see cref="WindowsHelperVerified"/>); anything else (missing, unreadable, or changed by a job, which runs as the same
    /// account) is never run. First defines what the script uses when it is not loaded (an older or a fresh worker): taskkill /T plus
    /// Stop-Process for a tree, and (except for the job wrapper, <paramref name="job"/>, which calls <c>LqJob</c> only when <c>$LqW</c> is set)
    /// no job object to end. The load sits in try/catch, so the script
    /// (cleanup, slot removal) always goes on.
    /// </summary>
    internal static string WindowsHelperLoad(string pathExpression, bool job = false) =>
        (job ? "" : "function LqEndJob($f){};")
        + "function LqKill($x){taskkill /PID $x /T /F 2>$null|Out-Null;kill $x -Force -EA 0}\n"
        + "$LqH=" + pathExpression + ";try{if" + WindowsHelperVerified("$LqH") + "{. ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($LqB)))}}catch{}";

    /// <summary>
    /// Writes <see cref="WindowsJobHelper"/> to <see cref="WindowsHelperPath"/> when it is not there or is not exactly lanq's text, through a
    /// temporary file (moved into place, or replacing the changed file) so a reader never sees half of it. The text travels deflated (it is
    /// lanq's own constant; the script must stay under the command-line cap) and is written as the exact bytes the hash was taken of.
    /// </summary>
    private static string WindowsInstallHelper(Machine m) => $$"""
        New-Item -ItemType Directory -Force -Path ({{ShellQuote.PowerShell(m.WorkRoot + "/bin")}}) | Out-Null
        $h = {{ShellQuote.PowerShell(WindowsHelperPath(m))}}; $ok = $false
        try { $ok = {{WindowsHelperVerified("$h")}} } catch { }
        if (-not $ok) { $t = "$h.$PID"; try { $z = New-Object IO.Compression.DeflateStream ([IO.MemoryStream][Convert]::FromBase64String('{{Deflated(WindowsJobHelper)}}')), ([IO.Compression.CompressionMode]::Decompress); $ms = New-Object IO.MemoryStream; $z.CopyTo($ms); [IO.File]::WriteAllBytes($t, $ms.ToArray()); if ([IO.File]::Exists($h)) { try { [IO.File]::Replace($t, $h, [NullString]::Value) } catch { [IO.File]::Delete($h); [IO.File]::Move($t, $h) } } else { [IO.File]::Move($t, $h) } } catch { }; if (Test-Path -LiteralPath $t) { Remove-Item -Force -EA 0 -LiteralPath $t } }
        """;

    /// <summary>Raw deflate (no zlib header, as .NET Framework's DeflateStream reads it) of the UTF-8 text, in base64.</summary>
    private static string Deflated(string text)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize))
        {
            deflate.Write(Encoding.UTF8.GetBytes(text));
        }

        return Convert.ToBase64String(buffer.ToArray());
    }
}
