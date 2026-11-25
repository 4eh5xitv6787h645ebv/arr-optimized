#!/usr/bin/env bash
# Clone the pinned upstream tag, verify its commit, apply patches/NNNN-*.patch in numeric order,
# build Sonarr.Core.dll into artifacts/ and (with --image) build the Docker image.
#
#   ./build.sh            build artifacts/Sonarr.Core.dll
#   ./build.sh --image    also: docker build -t sonarr-optimized:<version>-next .
#
# Needs git, a .NET SDK matching DOTNET_SDK (on PATH as `dotnet`), and Docker for --image.
set -euo pipefail
root="$(cd -- "$(dirname -- "$0")" && pwd)"
# shellcheck source=upstream.env
source "$root/upstream.env"

grep -qx "FROM $BASE_IMAGE" "$root/Dockerfile" || { echo "Dockerfile FROM does not match BASE_IMAGE in upstream.env" >&2; exit 1; }
work="${BUILD_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/sonarr-next-build-XXXXXX")}"
src="$work/Sonarr"
rm -rf "$src"
git clone --quiet --depth 1 --branch "$SONARR_TAG" https://github.com/Sonarr/Sonarr.git "$src"
actual="$(git -C "$src" rev-parse HEAD)"
if [ "$actual" != "$SONARR_COMMIT" ]; then
  echo "Tag $SONARR_TAG resolved to $actual, expected $SONARR_COMMIT" >&2
  exit 1
fi

for p in "$root"/patches/[0-9][0-9][0-9][0-9]-*.patch; do
  echo "Applying $(basename "$p")"
  git -C "$src" apply --check "$p"
  git -C "$src" apply "$p"
done

sdk="$(dotnet --version)"
[ "$sdk" = "$DOTNET_SDK" ] || echo "warning: dotnet $sdk, upstream pins $DOTNET_SDK" >&2
( cd "$src" && dotnet build src/NzbDrone.Core/Sonarr.Core.csproj -c Release -nologo -v:q \
    -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false \
    -p:RuntimeIdentifiers=linux-musl-x64 \
    -p:AssemblyVersion="$SONARR_VERSION" -p:FileVersion="$SONARR_VERSION" -p:Version="$SONARR_VERSION" )

mkdir -p "$root/artifacts"
cp "$src/_output/net10.0/Sonarr.Core.dll" "$root/artifacts/Sonarr.Core.dll"
( cd "$root/artifacts" && sha256sum Sonarr.Core.dll | tee Sonarr.Core.dll.sha256 )
echo "Build tree: $src"

if [ "${1:-}" = "--image" ]; then
  docker build -t "sonarr-optimized:${SONARR_VERSION}-next" "$root"
fi
