# The guard for dotnet-test-shard.ps1 (the Windows twin of check-test-split.sh): every test of PROJECT lands in
# exactly one of N shards.
#   check-test-split.ps1 -Project PROJECT -Shards N
# Builds once, lists every test, then lists each shard's tests through the shard script's own filter
# (dotnet test --list-tests --filter), and fails unless the shards together list exactly the full list: none
# missing, none twice. It runs in the machine's own culture and console encoding.
param([string]$Project, [string]$Shards)
$ErrorActionPreference = 'Stop'
if (-not $Project -or $Shards -cnotmatch '^[1-9][0-9]{0,2}\z') {
    [Console]::Error.WriteLine('usage: check-test-split.ps1 -Project path/to/Tests.csproj -Shards N (1 to 999)')
    exit 2
}
$n = [int]::Parse($Shards, [cultureinfo]::InvariantCulture)
$shardScript = Join-Path $PSScriptRoot 'dotnet-test-shard.ps1'

function Get-Listed($Lines) { @($Lines | Where-Object { $_ -clike '    *' } | ForEach-Object { $_.Substring(4) }) }

dotnet build $Project -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$all = Get-Listed (dotnet test $Project -c Release --no-build --list-tests -nologo)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($all.Count -eq 0) { [Console]::Error.WriteLine('check-test-split: dotnet test --list-tests listed no tests'); exit 1 }

$union = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le $n; $i++) {
    $env:LANQ_SHARD = "$i"; $env:LANQ_SHARDS = "$n"
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $shardScript -Project $Project -List
    if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine("check-test-split: shard $i/$n failed (exit $LASTEXITCODE)"); exit 1 }
    $listed = Get-Listed $out
    "shard $i/$n`: $($listed.Count) test(s)"
    $union.AddRange([string[]]$listed)
}

# Multisets compared in ordinal order (the full list itself may hold the same display name twice).
$a = [string[]]$all; [Array]::Sort($a, [StringComparer]::Ordinal)
$b = $union.ToArray(); [Array]::Sort($b, [StringComparer]::Ordinal)
if ($a.Count -eq $b.Count -and [System.Linq.Enumerable]::SequenceEqual([string[]]$a, [string[]]$b)) {
    "check-test-split: OK, $($a.Count) test(s) in exactly one of $n shard(s)"
    exit 0
}
[Console]::Error.WriteLine("check-test-split: FAILED, the $n shard(s) list $($b.Count) test(s), the full list has $($a.Count)")
$missing = @($a | Where-Object { [Array]::BinarySearch($b, $_, [StringComparer]::Ordinal) -lt 0 } | Select-Object -First 20)
foreach ($m in $missing) { [Console]::Error.WriteLine("missing: $m") }
exit 1
