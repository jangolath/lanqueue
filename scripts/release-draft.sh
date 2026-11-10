#!/bin/sh
# The two GitHub steps of the release's publish job (needs gh with GH_TOKEN and GH_REPO):
#   draft <tag> <prerelease> <assets dir> [<notes file>]
#                                          create a DRAFT release with every asset. On a re-run it reuses its own draft only when that
#                                          draft is empty or holds the same SHA256SUMS as these assets; a published release, or a draft
#                                          from another build, is refused. With a notes file (scripts/release-notes.sh) the body is that
#                                          file's text followed by GitHub's generated notes (the compare link); a reused draft whose
#                                          body lacks the notes' first line gets the notes (gh release edit cannot regenerate the link,
#                                          so a draft that already has them keeps its body). Without one the body is the generated notes.
#   publish <tag> <prerelease>             make the draft public. It is marked latest only when it is a stable release newer than every
#                                          stable release already published: a prerelease or a backport (v0.4.1 after v0.5.0) never
#                                          moves releases/latest, so the Scoop latest URL only ever serves the newest stable release.
# <prerelease> is true or false and must agree with the tag (a '-' in the version).
set -eu

mode=${1:-}
tag=${2:-}
prerelease=${3:-}
assets=${4:-}
notes=${5:-}

usage() {
  echo "usage: scripts/release-draft.sh draft <tag> <prerelease> <assets dir> [<notes file>] | publish <tag> <prerelease>" >&2
  exit 2
}

case "$tag" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *) usage ;;
esac
case "$tag" in
  *-*) want=true ;;
  *) want=false ;;
esac
if [ "$prerelease" != "$want" ]; then
  echo "prerelease=$prerelease does not match tag $tag" >&2
  exit 2
fi

# True when stable version $1 (x.y.z) is newer than $2, comparing each part as a number.
newer() {
  awk -v a="$1" -v b="$2" 'BEGIN { split(a, x, "."); split(b, y, "."); for (i = 1; i <= 3; i++) { if (x[i] + 0 > y[i] + 0) exit 0; if (x[i] + 0 < y[i] + 0) exit 1 } exit 1 }'
}

# --latest when $tag is a stable release newer than every published stable release (one tag per line on stdin), else --latest=false.
latest_flag() {
  if [ "$prerelease" = true ]; then
    echo --latest=false
    return
  fi
  mine=${tag#v}
  while read -r other; do
    case "$other" in
      v*) other=${other#v} ;;
      *) continue ;;
    esac
    printf '%s\n' "$other" | grep -q -E '^[0-9]+[.][0-9]+[.][0-9]+$' || continue
    if ! newer "$mine" "$other"; then
      echo --latest=false
      return
    fi
  done
  echo --latest
}

case "$mode" in
  draft)
    [ -f "$assets/SHA256SUMS" ] || usage
    if [ -n "$notes" ]; then
      [ -s "$notes" ] || usage
      # gh prepends --notes-file text to the notes --generate-notes asks GitHub for.
      set -- --notes-file "$notes"
    else
      set --
    fi
    if draft=$(gh release view "$tag" --json isDraft --jq .isDraft 2>/dev/null); then
      if [ "$draft" != true ]; then
        echo "release $tag is already published; nothing to do" >&2
        exit 1
      fi
      if [ -n "$(gh release view "$tag" --json assets --jq '.assets[].name')" ]; then
        work=$(mktemp -d)
        trap 'rm -rf "$work"' EXIT
        if ! gh release download "$tag" -p SHA256SUMS -D "$work" || ! cmp -s "$work/SHA256SUMS" "$assets/SHA256SUMS"; then
          echo "the existing draft $tag holds assets whose SHA256SUMS is missing or differs from this build's; delete that draft first" >&2
          exit 1
        fi
      fi
      gh release upload "$tag" "$assets"/* --clobber
      if [ -n "$notes" ]; then
        first=$(sed -n '/[^[:space:]]/{p;q;}' "$notes")
        body=$(gh release view "$tag" --json body --jq .body)
        if ! printf '%s\n' "$body" | grep -q -F -- "$first"; then
          gh release edit "$tag" --notes-file "$notes"
        fi
      fi
    elif [ "$prerelease" = true ]; then
      gh release create "$tag" "$assets"/* --draft --prerelease --latest=false --title "$tag" --generate-notes --verify-tag "$@"
    else
      gh release create "$tag" "$assets"/* --draft --title "$tag" --generate-notes --verify-tag "$@"
    fi
    ;;
  publish)
    if [ "$prerelease" = true ]; then
      latest=--latest=false
    else
      published=$(gh release list --exclude-drafts --exclude-pre-releases --limit 1000 --json tagName --jq '.[].tagName')
      latest=$(printf '%s\n' "$published" | latest_flag)
    fi
    gh release edit "$tag" --draft=false "$latest"
    ;;
  *)
    usage
    ;;
esac
