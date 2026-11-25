#!/usr/bin/env bash
# Clone the pinned upstream tag, verify its commit, apply patches/NNNN-*.patch in numeric order
# (0001-radarr-core.patch first), build Radarr.Core.dll, Radarr.Api.V3.dll and the web UI into
# artifacts/ and (unless --no-image) the local image radarr-optimized:<version>-next.
#
#   ./build.sh [--no-image]
#   DOTNET=/path/to/dotnet ./build.sh     # SDK must satisfy upstream's global.json
#   YARN="corepack yarn" ./build.sh       # yarn 1 command (default: yarn); needs Node.js 24
#   IMAGE_TAG=name:tag ./build.sh         # another image tag
set -euo pipefail
here="$(cd -- "$(dirname -- "$0")" && pwd)"
# shellcheck source=upstream.env
source "$here/upstream.env"
DOTNET="${DOTNET:-dotnet}"
read -r -a yarn <<< "${YARN:-yarn}"
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
# are off. Radarr.Api.V3 references Radarr.Core, so this builds both. Only these two DLLs are
# shipped; every other assembly in the image stays the stock one.
"$DOTNET" build src/Radarr.Api.V3/Radarr.Api.V3.csproj -c Release -nologo -v q \
  -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false -p:RuntimeIdentifiers=linux-musl-x64 \
  -p:AssemblyVersion="$version" -p:FileVersion="$version" -p:Version="$version"

# The web UI, built as upstream's build.sh does (webpack into _output/UI).
"${yarn[@]}" install --frozen-lockfile --network-timeout 120000
"${yarn[@]}" run build --env production

rm -rf "$here/artifacts"
mkdir -p "$here/artifacts"
cp "$work/src/_output/net10.0/Radarr.Core.dll" "$work/src/_output/net10.0/Radarr.Api.V3.dll" "$here/artifacts/"
cp -r "$work/src/_output/UI" "$here/artifacts/UI"
chmod -R a+rX "$here/artifacts"
(
  cd "$here/artifacts"
  sha256sum Radarr.Core.dll | tee Radarr.Core.dll.sha256
  sha256sum Radarr.Api.V3.dll | tee Radarr.Api.V3.dll.sha256
  # One hash for the UI folder: sha256 of the sorted per-file sha256 list.
  echo "$( (cd UI && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) | sha256sum | cut -d" " -f1)  UI/" | tee UI.sha256
)

if [[ "${1:-}" != "--no-image" ]]; then
  docker build --build-arg BASE_IMAGE="$BASE_IMAGE" \
    --label org.opencontainers.image.version="$version-next" \
    --label radarr-next.upstream-commit="$UPSTREAM_COMMIT" \
    -t "${IMAGE_TAG:-radarr-optimized:$version-next}" "$here"
fi
