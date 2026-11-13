#!/bin/sh
# The guard for dotnet-test-shard.sh: every test of PROJECT lands in exactly one of N shards.
#   sh check-test-split.sh PROJECT N
# Builds once, lists every test, then lists each shard's tests through the shard script's own filter
# (dotnet test --list-tests --filter), and fails unless the shards together list exactly the full list: none
# missing, none twice. It runs in the machine's own locale.
set -eu
PROJECT=${1:?usage: check-test-split.sh path/to/Tests.csproj N}
N=${2:?usage: check-test-split.sh path/to/Tests.csproj N}
case $N in '' | *[!0-9]* | 0) echo "check-test-split: N must be a whole number above 0, not '$N'" >&2; exit 2 ;; esac
dir=$(dirname "$0")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

dotnet build "$PROJECT" -c Release -v q -nologo
dotnet test "$PROJECT" -c Release --no-build --list-tests -nologo > "$tmp/out"
sed -n 's/^    //p' "$tmp/out" | LC_ALL=C sort > "$tmp/all"
[ -s "$tmp/all" ] || { echo "check-test-split: dotnet test --list-tests listed no tests" >&2; exit 1; }

: > "$tmp/shards"
i=1
while [ "$i" -le "$N" ]; do
  LANQ_SHARD=$i LANQ_SHARDS=$N sh "$dir/dotnet-test-shard.sh" "$PROJECT" --list > "$tmp/out.$i"
  sed -n 's/^    //p' "$tmp/out.$i" >> "$tmp/shards"
  echo "shard $i/$N: $(sed -n 's/^    //p' "$tmp/out.$i" | wc -l | tr -d ' ') test(s)"
  i=$((i + 1))
done
LC_ALL=C sort "$tmp/shards" > "$tmp/union"

total=$(wc -l < "$tmp/all" | tr -d ' ')
if cmp -s "$tmp/all" "$tmp/union"; then
  echo "check-test-split: OK, $total test(s) in exactly one of $N shard(s)"
  exit 0
fi
echo "check-test-split: FAILED, the $N shard(s) do not list each of the $total test(s) exactly once" >&2
echo "missing from every shard (first 20):" >&2
LC_ALL=C comm -23 "$tmp/all" "$tmp/union" | head -n 20 >&2
echo "in more than one shard, or unknown (first 20):" >&2
LC_ALL=C comm -13 "$tmp/all" "$tmp/union" | head -n 20 >&2
exit 1
