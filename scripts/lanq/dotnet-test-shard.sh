#!/bin/sh
# lanq run --shards (or a lanq.yml job with shards:): run this shard's test classes.
# A class goes to shard (cksum(class) mod N) + 1: the POSIX cksum of its full name (UTF-8, no newline), the same on
# every machine and locale, and a class keeps its shard when others are added.
#   sh dotnet-test-shard.sh PROJECT            build, then test this shard's classes (LANQ_SHARD of LANQ_SHARDS)
#   sh dotnet-test-shard.sh PROJECT --list     no build: list this shard's tests (check-test-split.sh uses it)
#   sh dotnet-test-shard.sh --assign N FILE    "cksum shard class" for each class name in FILE (no dotnet)
set -eu

shard_of() { # CLASS N
  sum=$(printf '%s' "$1" | cksum | cut -d' ' -f1)
  echo $((sum % $2 + 1))
}

if [ "${1:-}" = "--assign" ]; then
  n=${2:?usage: dotnet-test-shard.sh --assign N FILE}
  while IFS= read -r c || [ -n "$c" ]; do
    printf '%s %s %s\n' "$(printf '%s' "$c" | cksum | cut -d' ' -f1)" "$(shard_of "$c" "$n")" "$c"
  done < "${3:?usage: dotnet-test-shard.sh --assign N FILE}"
  exit 0
fi

: "${LANQ_SHARD:?run this through lanq run --shards}"
: "${LANQ_SHARDS:?run this through lanq run --shards}"
PROJECT=${1:?usage: dotnet-test-shard.sh path/to/Tests.csproj [--list]}
LIST=${2:-}
[ -n "$LIST" ] || dotnet build "$PROJECT" -c Release -v q -nologo
names=$(dotnet test "$PROJECT" -c Release --no-build --list-tests -nologo)

# Fully qualified test names (indented four spaces) -> class names, each once (a set: the order does not matter).
classes=$(printf '%s\n' "$names" | sed -n 's/^    //p' | sed 's/(.*//' | sed 's/\.[^.]*$//' | awk 'NF && !seen[$0]++')
mine=$(printf '%s\n' "$classes" | while IFS= read -r c; do
  if [ -n "$c" ] && [ "$(shard_of "$c" "$LANQ_SHARDS")" -eq "$LANQ_SHARD" ]; then printf '%s\n' "$c"; fi
done)
[ -n "$mine" ] || { echo "shard $LANQ_SHARD/$LANQ_SHARDS: no test classes"; exit 0; }

# The trailing dot: FullyQualifiedName~Ns.Class. never matches a longer class name.
filter=$(printf '%s\n' "$mine" | sed 's/.*/FullyQualifiedName~&./' | paste -sd'|' -)
if [ -n "$LIST" ]; then
  exec dotnet test "$PROJECT" -c Release --no-build --list-tests -nologo --filter "$filter"
fi
echo "shard $LANQ_SHARD/$LANQ_SHARDS: $(printf '%s\n' "$mine" | wc -l | tr -d ' ') test classes"
exec dotnet test "$PROJECT" -c Release --no-build --filter "$filter" -nologo --logger "trx;LogFileName=shard-$LANQ_SHARD.trx"
