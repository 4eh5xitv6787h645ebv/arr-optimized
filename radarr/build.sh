#!/usr/bin/env bash
# Clone the pinned upstream tag, verify its commit, apply patches/NNNN-*.patch in numeric order
# (0001-radarr-core.patch first), build Radarr.Core.dll
# and (unless --no-image) the local image radarr-optimized:<version>-next.
#
#   ./build.sh [--no-image]
#   DOTNET=/path/to/dotnet ./build.sh     # SDK must satisfy upstream's global.json
set -euo pipefail
here="$(cd -- "$(dirname -- "$0")" && pwd)"
# shellcheck source=upstream.env
source "$here/upstream.env"
DOTNET="${DOTNET:-dotnet}"
version="${UPSTREAM_TAG#v}"
work="${WORK:-$here/.build}"

rm -rf "$work"
mkdir -p "$work"
git clone --quiet --depth 1 --branch "$UPSTREAM_TAG" "$UPSTREAM_REPO" "$work/src"
actual="$(git -C "$work/src" rev-parse HEAD)"
if [[ "$actual" != "$UPSTREAM_COMMIT" ]]; then
  echo "upstream tag $UPSTREAM_TAG is $actual, expected $UPSTREAM_COMMIT; refusing to build" >&2
  exit 1
fi

for p in "$here"/patches/[0-9][0-9][0-9][0-9]-*.patch; do
  git -C "$work/src" apply --check "$p"
  git -C "$work/src" apply "$p"
  echo "applied $(basename "$p")"
done

cd "$work/src"
echo "SDK: $("$DOTNET" --version) (upstream global.json: $(tr -d ' \n' < global.json))"
# Upstream's StyleCop setup fails on its own files when a single project is built, so analyzers
# are off.
"$DOTNET" build src/NzbDrone.Core/Radarr.Core.csproj -c Release -nologo -v q \
  -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false -p:RuntimeIdentifiers=linux-musl-x64 \
  -p:AssemblyVersion="$version" -p:FileVersion="$version" -p:Version="$version"

mkdir -p "$here/artifacts"
cp "$work/src/_output/net10.0/Radarr.Core.dll" "$here/artifacts/Radarr.Core.dll"
(cd "$here/artifacts" && sha256sum Radarr.Core.dll | tee Radarr.Core.dll.sha256)

if [[ "${1:-}" != "--no-image" ]]; then
  docker build --build-arg BASE_IMAGE="$BASE_IMAGE" \
    --label org.opencontainers.image.version="$version-next" \
    --label radarr-next.upstream-commit="$UPSTREAM_COMMIT" \
    -t "radarr-optimized:$version-next" "$here"
fi
