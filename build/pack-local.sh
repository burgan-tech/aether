#!/usr/bin/env bash
#
# Packs every BBT.Aether.* library in framework/src into the repo-local NuGet feed
# at .local-feed, so a consumer (vnext) can build against unreleased framework work
# without publishing to nuget.org.
#
#   ./build/pack-local.sh              # packs 1.0.40-local
#   ./build/pack-local.sh 1.0.41-local # packs an explicit version
#
# Wiring it up on the consumer side is a one-off:
#
#   dotnet nuget add source /Users/<you>/…/aether/.local-feed -n aether-local \
#     --configfile <consumer>/nuget.config
#   # then set the consumer's Aether version property to the packed version
#
# WHY THE CACHE PURGE BELOW MATTERS
# ---------------------------------
# NuGet extracts a package into ~/.nuget/packages/<id>/<version>/ the first time it
# restores it, and from then on that directory wins — the feed is not consulted again
# for a version it already has. Re-packing the SAME version therefore appears to do
# nothing: the build keeps compiling against the previous contents, and the mismatch
# shows up as "the member I just added does not exist". Purging the version from the
# global cache before each pack is what makes iterating on a local feed reliable.
# The alternative — bumping the version on every single pack — is worse: it leaves
# the consumer's version property stale and litters the cache.

set -euo pipefail

VERSION="${1:-1.0.40-local}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="$REPO_ROOT/framework/src"
FEED_DIR="$REPO_ROOT/.local-feed"
CACHE_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

if [[ ! "$VERSION" == *-local ]]; then
  echo "refusing to pack '$VERSION': local-feed versions must end in -local so they can" >&2
  echo "never be mistaken for, or restored in place of, a published release." >&2
  exit 1
fi

echo "==> packing BBT.Aether.* $VERSION into ${FEED_DIR/#$HOME/~}"
mkdir -p "$FEED_DIR"

# Drop any earlier build of THIS version, from both the feed and the global cache.
rm -f "$FEED_DIR"/*."$VERSION".nupkg "$FEED_DIR"/*."$VERSION".snupkg 2>/dev/null || true

purged=0
for package_dir in "$CACHE_DIR"/bbt.aether.*/; do
  [[ -d "$package_dir$VERSION" ]] || continue
  rm -rf "${package_dir:?}$VERSION"
  purged=$((purged + 1))
done
[[ $purged -gt 0 ]] && echo "    purged $purged stale entr$([[ $purged -eq 1 ]] && echo y || echo ies) from the global package cache"

packed=0
for project in "$SRC_DIR"/*/*.csproj; do
  name="$(basename "$project" .csproj)"
  printf '    %-32s' "$name"
  if dotnet pack "$project" \
      --configuration Release \
      --output "$FEED_DIR" \
      -p:Version="$VERSION" \
      --verbosity quiet --nologo > /dev/null 2>&1; then
    echo "ok"
    packed=$((packed + 1))
  else
    echo "FAILED"
    echo
    echo "re-running to show the error:" >&2
    dotnet pack "$project" --configuration Release --output "$FEED_DIR" \
      -p:Version="$VERSION" --nologo 2>&1 | tail -25 >&2
    exit 1
  fi
done

echo
echo "==> $packed packages at $VERSION"
ls "$FEED_DIR" | grep -F "$VERSION" | grep '\.nupkg$' | sed 's/^/    /'
