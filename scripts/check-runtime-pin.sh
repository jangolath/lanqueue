#!/bin/sh
# Fails when the .NET 10 runtime patch pinned in the project (LanqRuntimePackVersion: the runtime compiled into the native binaries and
# the trim/AOT analyzers) is older than
#   installed: the newest Microsoft.NETCore.App 10.0.x in a `dotnet --list-runtimes` listing (the SDK a build machine has), or
#   latest:    "latest-runtime" in Microsoft's 10.0 releases.json (what .NET has shipped).
# Either means a .NET patch, possibly a security fix, that the native binaries do not have yet. Unreadable input fails too.
# Usage: scripts/check-runtime-pin.sh installed|latest <listing or releases.json file> <csproj>
set -eu

mode=${1:-}
input=${2:-}
project=${3:-}
if [ ! -f "$input" ] || [ ! -f "$project" ]; then
  echo "usage: scripts/check-runtime-pin.sh installed|latest <input file> <csproj>" >&2
  exit 2
fi

pin=$(sed -n 's:.*<LanqRuntimePackVersion>\(.*\)</LanqRuntimePackVersion>.*:\1:p' "$project" | head -n 1)
if ! printf '%s\n' "$pin" | grep -q -E '^10[.]0[.](0|[1-9][0-9]*)$'; then
  echo "cannot read a 10.0.N LanqRuntimePackVersion from $project (got '$pin')" >&2
  exit 1
fi

case "$mode" in
  installed)
    what="the newest installed Microsoft.NETCore.App"
    newest=$(sed -n 's/^Microsoft[.]NETCore[.]App \(10[.]0[.][0-9][0-9]*\) .*/\1/p' "$input" | sort -t . -k 3,3n | tail -n 1)
    ;;
  latest)
    what="the latest .NET 10 runtime in releases.json"
    newest=$(sed -n 's/.*"latest-runtime"[[:space:]]*:[[:space:]]*"\(10[.]0[.][0-9][0-9]*\)".*/\1/p' "$input" | head -n 1)
    ;;
  *)
    echo "usage: scripts/check-runtime-pin.sh installed|latest <input file> <csproj>" >&2
    exit 2
    ;;
esac

if [ -z "$newest" ]; then
  echo "found no 10.0.N version for $what in $input" >&2
  exit 1
fi

if [ "${newest##*.}" -gt "${pin##*.}" ]; then
  echo "::error::$what is $newest but the native binaries are pinned to $pin: bump LanqRuntimePackVersion from $pin to $newest in src/LanQueue/LanQueue.csproj, refresh both lock files and the build-image digests (see RELEASING.md)."
  exit 1
fi
echo "runtime pin $pin is current ($what: $newest)"
