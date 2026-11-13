#!/bin/sh
# lanq run --shards (or a lanq.yml job with shards:): run this shard's test classes.
# A class goes to shard (cksum(class) mod N) + 1: the POSIX cksum of its full name (UTF-8, no newline), the same on
# every machine and locale, and a class keeps its shard when others are added.
#   sh dotnet-test-shard.sh PROJECT            build, then test this shard's classes (LANQ_SHARD of LANQ_SHARDS)
#   sh dotnet-test-shard.sh PROJECT --list     no build: list this shard's tests (check-test-split.sh uses it)
#   sh dotnet-test-shard.sh --assign N FILE    "cksum shard class" for each class name in FILE (no dotnet)
# It fails closed: a bad LANQ_SHARD/LANQ_SHARDS, no test classes at all, or a class name that is not printable ASCII
# (which a Windows console could re-encode) stop it before any test runs, and a shard that runs no tests fails.
set -eu

# A whole number from 1 to 999, or exit 2.
number() { # NAME VALUE
  case $2 in
    [1-9] | [1-9][0-9] | [1-9][0-9][0-9]) ;;
    *) echo "dotnet-test-shard: $1 must be a whole number from 1 to 999, not '$2'" >&2; exit 2 ;;
  esac
}

if [ "${1:-}" = "--assign" ]; then
  n=${2:-}
  number N "$n"
  while IFS= read -r c || [ -n "$c" ]; do
    sum=$(printf '%s' "$c" | cksum | cut -d' ' -f1)
    printf '%s %s %s\n' "$sum" "$((sum % n + 1))" "$c"
  done < "${3:?usage: dotnet-test-shard.sh --assign N FILE}"
  exit 0
fi

number LANQ_SHARDS "${LANQ_SHARDS:-}"
number LANQ_SHARD "${LANQ_SHARD:-}"
[ "$LANQ_SHARD" -le "$LANQ_SHARDS" ] || { echo "dotnet-test-shard: LANQ_SHARD $LANQ_SHARD is above LANQ_SHARDS $LANQ_SHARDS" >&2; exit 2; }
PROJECT=${1:?usage: dotnet-test-shard.sh path/to/Tests.csproj [--list]}
LIST=${2:-}
[ -n "$LIST" ] || dotnet build "$PROJECT" -c Release -v q -nologo
names=$(dotnet test "$PROJECT" -c Release --no-build --list-tests -nologo)

# Fully qualified test names (indented four spaces) -> class names, each once (a set: the order does not matter).
classes=$(printf '%s\n' "$names" | sed -n 's/^    //p' | sed 's/(.*//' | sed 's/\.[^.]*$//' | awk 'NF && !seen[$0]++')
[ -n "$classes" ] || { echo "dotnet-test-shard: dotnet test --list-tests listed no test classes" >&2; exit 1; }
if printf '%s\n' "$classes" | LC_ALL=C grep -q '[^ -~]' || printf '%s\n' "$classes" | grep -qF '?'; then
  echo "dotnet-test-shard: a test class name is not printable ASCII (or holds ?); rename it (both shard scripts refuse it)" >&2
  exit 2
fi
mine=$(printf '%s\n' "$classes" | while IFS= read -r c; do
  sum=$(printf '%s' "$c" | cksum | cut -d' ' -f1)
  if [ $((sum % LANQ_SHARDS + 1)) -eq "$LANQ_SHARD" ]; then printf '%s\n' "$c"; fi
done)
[ -n "$mine" ] || { echo "shard $LANQ_SHARD/$LANQ_SHARDS: no test classes"; exit 0; }

# The trailing dot: FullyQualifiedName~Ns.Class. never matches a longer class name.
filter=$(printf '%s\n' "$mine" | sed 's/.*/FullyQualifiedName~&./' | paste -sd'|' -)
if [ -n "$LIST" ]; then
  exec dotnet test "$PROJECT" -c Release --no-build --list-tests -nologo --filter "$filter"
fi
echo "shard $LANQ_SHARD/$LANQ_SHARDS: $(printf '%s\n' "$mine" | wc -l | tr -d ' ') test classes"
exec dotnet test "$PROJECT" -c Release --no-build --filter "$filter" -nologo --logger "trx;LogFileName=shard-$LANQ_SHARD.trx" \
  -- RunConfiguration.TreatNoTestsAsError=true
