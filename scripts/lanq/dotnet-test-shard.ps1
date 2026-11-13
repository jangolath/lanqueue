# lanq run --shards (or a lanq.yml job with shards:): run this shard's test classes.
# A class goes to shard (cksum(class) mod N) + 1: the POSIX cksum of its full name (UTF-8, no newline), the same on
# every machine and culture, and identical to dotnet-test-shard.sh.
#   dotnet-test-shard.ps1 -Project PROJECT          build, then test this shard's classes (LANQ_SHARD of LANQ_SHARDS)
#   dotnet-test-shard.ps1 -Project PROJECT -List    no build: list this shard's tests (check-test-split.ps1 uses it)
#   dotnet-test-shard.ps1 -AssignFile FILE -Shards N   "cksum shard class" for each class name in FILE (no dotnet)
# It fails closed: a bad LANQ_SHARD/LANQ_SHARDS, no test classes at all, or a class name that is not printable ASCII
# (which a Windows console could re-encode) stop it before any test runs, and a shard that runs no tests fails.
param([string]$Project, [switch]$List, [string]$AssignFile, [string]$Shards = '')
$ErrorActionPreference = 'Stop'

# POSIX cksum: CRC-32, polynomial 0x04C11DB7, MSB first, over the bytes and then the length (low byte first, no
# trailing zero bytes), inverted. Integers are [long] masked to 32 bits (PowerShell has no unsigned shifts).
$script:Mask = [long]4294967295
$script:CrcTable = New-Object 'long[]' 256
for ($t = 0; $t -lt 256; $t++) {
    $c = [long]$t -shl 24
    for ($k = 0; $k -lt 8; $k++) {
        if ($c -band [long]2147483648) { $c = (($c -shl 1) -bxor [long]79764919) -band $script:Mask } else { $c = ($c -shl 1) -band $script:Mask }
    }
    $script:CrcTable[$t] = $c
}

function Get-PosixCksum([string]$Text) {
    $crc = [long]0
    $bytes = (New-Object System.Text.UTF8Encoding $false).GetBytes($Text)
    foreach ($b in $bytes) { $crc = (($crc -shl 8) -band $script:Mask) -bxor $script:CrcTable[(($crc -shr 24) -bxor $b) -band 255] }
    for ($n = [long]$bytes.Length; $n -ne 0; $n = $n -shr 8) {
        $crc = (($crc -shl 8) -band $script:Mask) -bxor $script:CrcTable[(($crc -shr 24) -bxor ($n -band 255)) -band 255]
    }
    return (-bnot $crc) -band $script:Mask
}

# A whole number from 1 to 999, or exit 2.
function Get-Number([string]$Name, [string]$Value) {
    if ($Value -cnotmatch '^[1-9][0-9]{0,2}\z') {
        [Console]::Error.WriteLine("dotnet-test-shard: $Name must be a whole number from 1 to 999, not '$Value'")
        exit 2
    }
    return [int]::Parse($Value, [cultureinfo]::InvariantCulture)
}

if ($AssignFile) {
    $n = Get-Number 'Shards' $Shards
    foreach ($c in [System.IO.File]::ReadAllLines($AssignFile, (New-Object System.Text.UTF8Encoding $false))) {
        $sum = Get-PosixCksum $c
        [string]::Format([cultureinfo]::InvariantCulture, '{0} {1} {2}', $sum, ($sum % $n) + 1, $c)
    }
    exit 0
}

$n = Get-Number 'LANQ_SHARDS' $env:LANQ_SHARDS
$i = Get-Number 'LANQ_SHARD' $env:LANQ_SHARD
if ($i -gt $n) { [Console]::Error.WriteLine("dotnet-test-shard: LANQ_SHARD $i is above LANQ_SHARDS $n"); exit 2 }
if (-not $Project) { [Console]::Error.WriteLine('usage: dotnet-test-shard.ps1 -Project path/to/Tests.csproj [-List]'); exit 2 }
if (-not $List) {
    dotnet build $Project -c Release -v q -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$names = dotnet test $Project -c Release --no-build --list-tests -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Fully qualified test names (indented four spaces) -> class names, each once (an ordinal set: the order does not matter).
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$classes = @(foreach ($line in $names) {
    if ($line -clike '    *') {
        $class = ($line.Trim() -creplace '\(.*$', '') -creplace '\.[^.]*$', ''
        if ($class -and $seen.Add($class)) { $class }
    }
})
if ($classes.Count -eq 0) { [Console]::Error.WriteLine('dotnet-test-shard: dotnet test --list-tests listed no test classes'); exit 1 }
# A ? too: a console code page that cannot show a character prints ? for it (CJK, for example), and such a filter matches nothing.
if (@($classes | Where-Object { $_ -cmatch '[^\x20-\x7E]|\?' }).Count -gt 0) {
    [Console]::Error.WriteLine('dotnet-test-shard: a test class name is not printable ASCII (or holds ?); rename it (both shard scripts refuse it)')
    exit 2
}
$mine = @($classes | Where-Object { ((Get-PosixCksum $_) % $n) + 1 -eq $i })
if ($mine.Count -eq 0) { "shard $i/$n`: no test classes"; exit 0 }

# The trailing dot: FullyQualifiedName~Ns.Class. never matches a longer class name.
$filter = ($mine | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
if ($List) {
    dotnet test $Project -c Release --no-build --list-tests -nologo --filter $filter
    exit $LASTEXITCODE
}
"shard $i/$n`: $($mine.Count) test classes"
dotnet test $Project -c Release --no-build --filter $filter -nologo --logger "trx;LogFileName=shard-$i.trx" '--' RunConfiguration.TreatNoTestsAsError=true
exit $LASTEXITCODE
