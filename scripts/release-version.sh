#!/bin/sh
# The release version and whether it is a prerelease, for $GITHUB_OUTPUT. A tag push must be v + strict semver (no build metadata, no
# leading zeros); anything else is refused, so the version is safe in every later step, URL and file name. A manual run is always
# 0.0.0-dryrun.<run number> (a prerelease), whatever its ref.
# Usage: scripts/release-version.sh <ref name> <is tag: true|false> <run number>
set -eu

ref=${1:-}
is_tag=${2:-}
run=${3:-}

semver='^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z-]+([.][0-9A-Za-z-]+)*)?$'

one_line() {
  [ "$(printf '%s\n' "$1" | wc -l | tr -d ' ')" = 1 ]
}

case "$is_tag" in
  true)
    case "$ref" in
      v*) version=${ref#v} ;;
      *) echo "release tag '$ref' must be v<semver>, for example v0.5.0 or v0.5.0-rc.1" >&2; exit 1 ;;
    esac
    ;;
  false)
    if ! printf '%s\n' "$run" | grep -q -E '^[0-9]+$' || ! one_line "$run"; then
      echo "run number '$run' is not a number" >&2
      exit 1
    fi
    version="0.0.0-dryrun.$run"
    ;;
  *)
    echo "usage: scripts/release-version.sh <ref name> <is tag: true|false> <run number>" >&2
    exit 2
    ;;
esac

if ! one_line "$version" || ! printf '%s\n' "$version" | grep -q -E "$semver"; then
  echo "release tag '$ref' is not v<semver> (for example v0.5.0 or v0.5.0-rc.1; no build metadata)" >&2
  exit 1
fi

case "$version" in
  *-*) prerelease=true ;;
  *) prerelease=false ;;
esac
printf 'version=%s\nprerelease=%s\n' "$version" "$prerelease"
