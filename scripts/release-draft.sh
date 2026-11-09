#!/bin/sh
# The two GitHub steps of the release's publish job (needs gh with GH_TOKEN and GH_REPO):
#   draft <tag> <prerelease> <assets dir>  create a DRAFT release with every asset. On a re-run it reuses its own draft only when that
#                                          draft is empty or holds the same SHA256SUMS as these assets; a published release, or a draft
#                                          from another build, is refused.
#   publish <tag> <prerelease>             make the draft public. A prerelease is never marked latest, so releases/latest (and the Scoop
#                                          URL) never serve a release candidate.
# <prerelease> is true or false and must agree with the tag (a '-' in the version).
set -eu

mode=${1:-}
tag=${2:-}
prerelease=${3:-}
assets=${4:-}

usage() {
  echo "usage: scripts/release-draft.sh draft <tag> <prerelease> <assets dir> | publish <tag> <prerelease>" >&2
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

if [ "$prerelease" = true ]; then
  latest=--latest=false
else
  latest=--latest
fi

case "$mode" in
  draft)
    [ -f "$assets/SHA256SUMS" ] || usage
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
    elif [ "$prerelease" = true ]; then
      gh release create "$tag" "$assets"/* --draft --prerelease --latest=false --title "$tag" --generate-notes --verify-tag
    else
      gh release create "$tag" "$assets"/* --draft --title "$tag" --generate-notes --verify-tag
    fi
    ;;
  publish)
    gh release edit "$tag" --draft=false "$latest"
    ;;
  *)
    usage
    ;;
esac
