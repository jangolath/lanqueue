# lanq run --shards (or a lanq.yml job with shards:): run this shard's test classes.
# A class goes to shard (cksum(class) mod N) + 1: the POSIX cksum of its full name (UTF-8, no newline), the same on
# every machine and culture, and identical to dotnet-test-shard.sh.
#   dotnet-test-shard.ps1 -Project PROJECT          build, then test this shard's classes (LANQ_SHARD of LANQ_SHARDS)
#   dotnet-test-shard.ps1 -Project PROJECT -List    no build: list this shard's tests
#   dotnet-test-shard.ps1 -AssignFile FILE -Shards N   "cksum shard class" for each class name in FILE (no dotnet)
param([string]$Project, [switch]$List, [string]$AssignFile, [int]$Shards = 0)
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

function Get-Shard([string]$Class, [int]$N) { return [int]((Get-PosixCksum $Class) % $N) + 1 }

if ($AssignFile) {
    foreach ($c in [System.IO.File]::ReadAllLines($AssignFile, (New-Object System.Text.UTF8Encoding $false))) {
        [string]::Format([cultureinfo]::InvariantCulture, '{0} {1} {2}', (Get-PosixCksum $c), (Get-Shard $c $Shards), $c)
    }
    exit 0
}

if (-not $Project) { throw 'usage: dotnet-test-shard.ps1 -Project path/to/Tests.csproj [-List]' }
if (-not $env:LANQ_SHARD -or -not $env:LANQ_SHARDS) { throw 'run this through lanq run --shards' }
$i = [int]::Parse($env:LANQ_SHARD, [cultureinfo]::InvariantCulture); $n = [int]::Parse($env:LANQ_SHARDS, [cultureinfo]::InvariantCulture)
if (-not $List) {
    dotnet build $Project -c Release -v q -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$names = dotnet test $Project -c Release --no-build --list-tests -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Fully qualified test names (indented four spaces) -> class names, each once (an ordinal set: the order does not matter).
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$mine = @(foreach ($line in $names) {
    if ($line -clike '    *') {
        $class = ($line.Trim() -creplace '\(.*$', '') -creplace '\.[^.]*$', ''
        if ($class -and $seen.Add($class) -and (Get-Shard $class $n) -eq $i) { $class }
    }
})
if ($mine.Count -eq 0) { "shard $i/$n`: no test classes"; exit 0 }

# The trailing dot: FullyQualifiedName~Ns.Class. never matches a longer class name.
$filter = ($mine | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
if ($List) {
    dotnet test $Project -c Release --no-build --list-tests -nologo --filter $filter
    exit $LASTEXITCODE
}
"shard $i/$n`: $($mine.Count) test classes"
dotnet test $Project -c Release --no-build --filter $filter -nologo --logger "trx;LogFileName=shard-$i.trx"
exit $LASTEXITCODE
