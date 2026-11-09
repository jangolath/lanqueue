#!/bin/sh
# Prints the Scoop manifest (lanq.json) for one release: exact versioned URLs of the two Windows zips and their SHA-256 hashes, taken
# from that release's SHA256SUMS (the same file users verify downloads against). The release workflow attaches the output as lanq.json,
# so `scoop install https://github.com/jangolath/lanqueue/releases/latest/download/lanq.json` works.
# Needs only POSIX sh, grep and sed. Usage: scripts/scoop-manifest.sh 0.5.0 path/to/SHA256SUMS > lanq.json
set -eu

version=${1:-}
sums=${2:-}
# Plain semver (an optional pre-release), so the version is safe inside both the JSON and the URLs.
if ! printf '%s\n' "$version" | grep -q -E '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$' || [ "$(printf '%s\n' "$version" | wc -l)" -ne 1 ]; then
  echo "usage: scripts/scoop-manifest.sh <version, for example 0.5.0> <SHA256SUMS file>" >&2
  exit 2
fi
if [ ! -f "$sums" ]; then
  echo "SHA256SUMS file not found: $sums" >&2
  exit 2
fi

base="https://github.com/jangolath/lanqueue/releases/download/v$version"

# The hash of one asset; SHA256SUMS must list it exactly once (text or binary mode) with a 64-digit lowercase hash.
hash_of() {
  line="^[0-9a-f]{64} [ *]$(printf '%s' "$1" | sed 's/[.]/[.]/g')\$"
  n=$(grep -c -E "$line" "$sums" || true)
  if [ "$n" != 1 ]; then
    echo "SHA256SUMS must list $1 exactly once (found $n)" >&2
    exit 1
  fi
  grep -E "$line" "$sums" | cut -c1-64
}

x64=$(hash_of lanq-win-x64.zip)
arm64=$(hash_of lanq-win-arm64.zip)

cat <<JSON
{
  "version": "$version",
  "description": "Send build and test jobs to spare machines on your LAN over key-only SSH.",
  "homepage": "https://github.com/jangolath/lanqueue",
  "license": "MIT",
  "architecture": {
    "64bit": {
      "url": "$base/lanq-win-x64.zip",
      "hash": "$x64"
    },
    "arm64": {
      "url": "$base/lanq-win-arm64.zip",
      "hash": "$arm64"
    }
  },
  "bin": "lanq.exe"
}
JSON
