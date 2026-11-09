#!/bin/sh
# Updates the lanq-makeready scripts embedded in lanq (src/LanQueue/Makeready/) from a GitHub release of
# jangolath/lanq-makeready, and only after verifying them: both files must match the release's SHA256SUMS
# (sha256sum -c) AND carry a valid GitHub build-provenance attestation (gh attestation verify).
# Needs: gh (logged in), sha256sum. Usage: scripts/sync-makeready.sh v0.1.0
set -eu

tag=${1:-}
case "$tag" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "usage: scripts/sync-makeready.sh <tag, for example v0.1.0>" >&2; exit 2 ;;
esac

repo=jangolath/lanq-makeready
root=$(cd "$(dirname "$0")/.." && pwd)
dest="$root/src/LanQueue/Makeready"
work=$(mktemp -d)
# Cleanup on every exit; an interrupt also ends the script with a failure status (130 for INT, 143 for TERM).
trap 'rm -rf "$work"' EXIT
trap 'rm -rf "$work"; exit 130' INT
trap 'rm -rf "$work"; exit 143' TERM

cd "$work"
gh release download "$tag" -R "$repo" -p SHA256SUMS -p lanq-makeready.ps1 -p lanq-makeready.sh

# Check exactly the two scripts; a SHA256SUMS that does not list each of them exactly once is refused.
: > sums
for f in lanq-makeready.ps1 lanq-makeready.sh; do
  line="^[0-9a-f]{64} [ *]$(printf '%s' "$f" | sed 's/[.]/[.]/g')\$"
  n=$(grep -c -E "$line" SHA256SUMS || true)
  if [ "$n" != 1 ]; then
    echo "SHA256SUMS of $tag does not list lanq-makeready.ps1 and lanq-makeready.sh exactly once each ($f: $n)" >&2
    exit 1
  fi
  grep -E "$line" SHA256SUMS >> sums
done
sha256sum -c sums
for f in lanq-makeready.ps1 lanq-makeready.sh; do
  gh attestation verify "$f" -R "$repo"
done

cp lanq-makeready.ps1 lanq-makeready.sh "$dest/"
echo
echo "Copied the verified $tag scripts to $dest."
echo "Now pin them: in tests/LanQueue.Tests/MakereadyTests.cs set Ps1Sha256 / ShSha256 to the hashes below,"
echo "set Cli.MakereadyVersion to ${tag#v}, run dotnet test, and commit the scripts and the pins together."
cat sums
