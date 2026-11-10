#!/bin/sh
# The release notes for <version>, taken from CHANGELOG.md, for the NuGet package (PackageReleaseNotes) and the GitHub release body.
# Prints the body of the section headed "## <version>" or "## <version> (<anything>)", up to the next "## " heading, without leading
# or trailing blank lines, then a blank line and a link to the full changelog. By version:
#   X.Y.Z             (a stable tag) the exact section is required; it fails when the section is missing or empty, or when its
#                     heading says "unreleased", so a tag cannot ship notes that were never written or dated.
#   X.Y.Z-<pre>       (a prerelease tag) the exact section if it has text, else the X.Y.Z section (which may still be "unreleased");
#                     fails when neither has text.
#   0.0.0-dryrun.<n>  (a manual run) the first section that is not "## Unreleased", or a one-line placeholder; never fails for a
#                     missing section, so a rehearsal still exercises the extraction.
# Usage: scripts/release-notes.sh <version> <CHANGELOG.md>
set -eu

version=${1:-}
changelog=${2:-}

usage() {
  echo "usage: scripts/release-notes.sh <version> <CHANGELOG.md>" >&2
  exit 2
}

[ $# -eq 2 ] || usage
[ -f "$changelog" ] || usage
# The version names a heading and lands in messages, so only strict semver (no build metadata) on one line gets this far.
if [ "$(printf '%s\n' "$version" | wc -l | tr -d ' ')" != 1 ] ||
  ! printf '%s\n' "$version" | grep -q -E '^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z-]+([.][0-9A-Za-z-]+)*)?$'; then
  usage
fi

footer='Full changelog: https://github.com/jangolath/lanqueue/blob/main/CHANGELOG.md'

# section <mode> <version>: the trimmed body of the matching section on stdout, its heading on line 1 of stdout prefixed by "#HEAD ".
# Exit 1 when no section matches. mode "exact" matches "## <version>" or "## <version> (...)"; mode "first" takes the first "## "
# section whose heading is not "## Unreleased". Lines are read without a trailing CR, so a CRLF checkout gives the same text.
section() {
  awk -v mode="$1" -v v="$2" '
    function matches(line,    rest) {
      if (mode == "first") {
        return tolower(line) != "## unreleased"
      }
      if (line == "## " v) {
        return 1
      }
      if (index(line, "## " v " (") != 1) {
        return 0
      }
      rest = substr(line, length("## " v " (") + 1)
      return rest ~ /\)[ \t]*$/
    }
    { sub(/\r$/, "") }
    /^## / {
      if (found) {
        exit
      }
      if (matches($0)) {
        found = 1
        print "#HEAD " $0
      }
      next
    }
    found {
      if ($0 ~ /^[ \t]*$/) {
        blank++
        next
      }
      if (printed) {
        for (i = 0; i < blank; i++) {
          print ""
        }
      }
      blank = 0
      printed = 1
      print
    }
    END { exit found ? 0 : 1 }
  ' "$changelog"
}

# notes <mode> <version>: the section's body when it has text (heading in $heading); exit 1 otherwise.
notes() {
  out=$(section "$1" "$2") || return 1
  heading=$(printf '%s\n' "$out" | sed -n '1s/^#HEAD //p')
  body=$(printf '%s\n' "$out" | sed '1d')
  [ -n "$body" ]
}

fail() {
  echo "release notes for $version: $1 in $changelog; add a dated '## X.Y.Z (YYYY-MM-DD)' section (see RELEASING.md)" >&2
  exit 1
}

case "$version" in
  0.0.0-dryrun.*)
    if ! notes first ""; then
      body="Dry run $version: no release section found in the changelog."
    fi
    ;;
  *-*)
    base=${version%%-*}
    if ! notes exact "$version" && ! notes exact "$base"; then
      fail "no section '## $version' or '## $base' with any text"
    fi
    ;;
  *)
    if ! section exact "$version" > /dev/null; then
      fail "no section '## $version'"
    fi
    notes exact "$version" || fail "the section '## $version' is empty"
    case "$(printf '%s\n' "$heading" | tr '[:upper:]' '[:lower:]')" in
      *unreleased*) fail "the section is still headed '$heading'" ;;
    esac
    ;;
esac

printf '%s\n\n%s\n' "$body" "$footer"
