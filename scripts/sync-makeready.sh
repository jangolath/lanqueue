#!/bin/sh
# Updates the lanq-makeready scripts embedded in lanq (src/LanQueue/Makeready/) from a GitHub release of
# jangolath/lanq-makeready, and only after verifying them: every file must match the release's SHA256SUMS
# (sha256sum -c) AND carry a valid GitHub build-provenance attestation (gh attestation verify).
# The four files are the read-only scanner and the read-only tune check (0.5.0+), each for Windows and Linux.
# The fix script (lanq-makeready-fix) is never embedded.
# Needs: gh (logged in), sha256sum. Usage: scripts/sync-makeready.sh v0.5.0
set -eu

tag=${1:-}
case "$tag" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "usage: scripts/sync-makeready.sh <tag, for example v0.5.0>" >&2; exit 2 ;;
esac

repo=jangolath/lanq-makeready
files="lanq-makeready.ps1 lanq-makeready.sh lanq-makeready-tune.ps1 lanq-makeready-tune.sh"
root=$(cd "$(dirname "$0")/.." && pwd)
dest="$root/src/LanQueue/Makeready"
work=$(mktemp -d)
# Cleanup on every exit; an interrupt also ends the script with a failure status (130 for INT, 143 for TERM).
trap 'rm -rf "$work"' EXIT
trap 'rm -rf "$work"; exit 130' INT
trap 'rm -rf "$work"; exit 143' TERM

cd "$work"
set -- -p SHA256SUMS
for f in $files; do
  set -- "$@" -p "$f"
done
gh release download "$tag" -R "$repo" "$@"

# Check exactly these files; a SHA256SUMS that does not list each of them exactly once is refused.
: > sums
for f in $files; do
  line="^[0-9a-f]{64} [ *]$(printf '%s' "$f" | sed 's/[.]/[.]/g')\$"
  n=$(grep -c -E "$line" SHA256SUMS || true)
  if [ "$n" != 1 ]; then
    echo "SHA256SUMS of $tag does not list $files exactly once each ($f: $n)" >&2
    exit 1
  fi
  grep -E "$line" SHA256SUMS >> sums
done
sha256sum -c sums
for f in $files; do
  gh attestation verify "$f" -R "$repo"
done

# shellcheck disable=SC2086 # $files is a fixed list of plain names
cp $files "$dest/"
echo
echo "Copied the verified $tag scripts to $dest."
echo "Now pin them: in tests/LanQueue.Tests/MakereadyTests.cs set Ps1Sha256 / ShSha256 / TunePs1Sha256 / TuneShSha256 to the"
echo "hashes below, set Cli.MakereadyVersion to ${tag#v}, run dotnet test, and commit the scripts and the pins together."
cat sums
