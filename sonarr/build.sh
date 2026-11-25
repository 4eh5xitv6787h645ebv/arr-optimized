#!/usr/bin/env bash
# Clone the pinned upstream tag, verify its commit, apply patches/NNNN-*.patch in numeric order,
# build Sonarr.Core.dll, Sonarr.Api.V3.dll and the web UI into artifacts/ and (with --image) build
# the Docker image.
#
#   ./build.sh            build artifacts/Sonarr.Core.dll, artifacts/Sonarr.Api.V3.dll, artifacts/UI/
#   ./build.sh --image    also: docker build -t sonarr-optimized:<version>-next .
#   IMAGE_TAG=name:tag ./build.sh --image   another image tag
#
# Needs git, a .NET SDK matching DOTNET_SDK (on PATH as `dotnet`), Node.js 24 with yarn 1 (YARN=...
# to use another command, e.g. "corepack yarn") and Docker for --image.
set -euo pipefail
root="$(cd -- "$(dirname -- "$0")" && pwd)"
# shellcheck source=upstream.env
source "$root/upstream.env"
read -r -a yarn <<< "${YARN:-yarn}"

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
# Sonarr.Api.V3 references Sonarr.Core, so this builds both. Only these two DLLs are shipped; every
# other assembly in the image stays the stock one.
( cd "$src" && dotnet build src/Sonarr.Api.V3/Sonarr.Api.V3.csproj -c Release -nologo -v:q \
    -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false \
    -p:RuntimeIdentifiers=linux-musl-x64 \
    -p:AssemblyVersion="$SONARR_VERSION" -p:FileVersion="$SONARR_VERSION" -p:Version="$SONARR_VERSION" )

# The web UI, built with upstream's own scripts (yarn build = vite build into _output/UI).
( cd "$src" && "${yarn[@]}" install --frozen-lockfile --network-timeout 120000 && "${yarn[@]}" run build )

rm -rf "$root/artifacts"
mkdir -p "$root/artifacts"
cp "$src/_output/net10.0/Sonarr.Core.dll" "$src/_output/net10.0/Sonarr.Api.V3.dll" "$root/artifacts/"
cp -r "$src/_output/UI" "$root/artifacts/UI"
chmod -R a+rX "$root/artifacts"
(
  cd "$root/artifacts"
  sha256sum Sonarr.Core.dll | tee Sonarr.Core.dll.sha256
  sha256sum Sonarr.Api.V3.dll | tee Sonarr.Api.V3.dll.sha256
  # One hash for the UI folder: sha256 of the sorted per-file sha256 list.
  echo "$( (cd UI && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) | sha256sum | cut -d" " -f1)  UI/" | tee UI.sha256
)
echo "Build tree: $src"

if [ "${1:-}" = "--image" ]; then
  docker build -t "${IMAGE_TAG:-sonarr-optimized:${SONARR_VERSION}-next}" "$root"
fi
