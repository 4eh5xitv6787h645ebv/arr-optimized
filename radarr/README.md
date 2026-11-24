# radarr-next

A small, rebase-friendly patch to [Radarr](https://github.com/Radarr/Radarr) that makes interactive
searches and RSS decisions faster **without changing any decision**: the same releases, rejections,
custom formats and scores as stock. It is shipped as a drop-in `Radarr.Core.dll` on top of the
stock linuxserver image, pinned by digest.

| | |
|---|---|
| Upstream | `v7.0.3.10918` @ `af772b051ac27a0615c7f8b7f78622b6dc183be0` |
| Base image | `lscr.io/linuxserver/radarr@sha256:044788f5508b7f67a3e0368cdd142c77c4d4cd5aec2efaba53e1a68b6b6231e7` (`7.0.3.10918-ls328`, the `latest` tag on 2026-11-24) |
| SDK | .NET SDK 10.0.404 (upstream `global.json`) |
| Image | `radarr-optimized:7.0.3.10918-next` (local build; published as `ghcr.io/4eh5xitv6787h645ebv/radarr-optimized`) |

## What the patch does

Movie mapping and movie history are read exactly as stock reads them, and a search decides all
releases in one call after the last indexer answers, as stock does. An earlier exact-ID mapping
shortcut, a history memo and per-indexer ("pipelined") decisions were removed after review because
they could serve stale or wrong data.

1. **Custom-format match cache.** Remembers which custom formats *match* a given release input
   (title, file name, group, edition, quality resolution/source/modifier, size, indexer flags,
   languages, and, only when a rule needs them, the movie's original language and year).
   At most 50,000 entries, least-recently-used eviction, 6-hour sliding expiry refreshed on every
   hit, 4,096-character input limit. Scores and all accept/reject decisions are still computed
   live. Definition edits take effect immediately. Unknown specification types bypass the cache.
   The matcher evaluates cheap conditions before regexes and stops once a format's outcome is known.
2. **Per-batch indexer memo.** Within one decision batch, an indexer definition is loaded once
   instead of three times per release. Any indexer add, edit or delete (provider events) bumps a
   generation counter and the batch drops what it holds, so a mid-batch edit is seen like stock.

## Layout

```
patches/radarr-core.patch   the whole change, against the pinned tag (7 files)
upstream.env                pinned tag, commit and base-image digest
build.sh                    clone tag -> verify commit -> apply patches -> build DLL -> build image
Dockerfile                  FROM <base digest>; COPY artifacts/Radarr.Core.dll
tests/CacheTests            truth-table, invalidation, expiry/LRU/cap, batch-memo, memory and rebase-guard tests
tests/UpstreamFixtures      NUnit regression tests for the review findings (copy into NzbDrone.Core.Test)
bench/                      offline lab: fail-closed indexer stub, offline search-cache builder,
                            sanitized clone setup, benchmark, decision-equivalence and invalidation checks
```

## Build

```
DOTNET=/path/to/dotnet-10.0.404/dotnet ./build.sh          # DLL + image radarr-optimized:<version>-next
./build.sh --no-image
```

`build.sh` refuses to build if the tag no longer points at the pinned commit or a patch does not
apply cleanly.

## Test

```
cd tests/CacheTests
dotnet run -c Release -p:CoreBin=$PWD/../../artifacts -p:StockBin=/path/to/stock/bin -- /path/to/stock/bin [--memory]
```

`/path/to/stock/bin` is `/app/radarr/bin` copied out of the base image (`docker create` + `docker cp`).
Upstream's own NUnit fixtures for the touched areas can be run on the patched tree:

```
dotnet test src/NzbDrone.Core.Test/Radarr.Core.Test.csproj -c Release -p:EnableAnalyzers=false \
  --filter "FullyQualifiedName~NzbDrone.Core.Test.DecisionEngineTests|FullyQualifiedName~NzbDrone.Core.Test.CustomFormats|FullyQualifiedName~NzbDrone.Core.Test.ParserTests|FullyQualifiedName~IndexerSearchTests|FullyQualifiedName~Download.Aggregation|FullyQualifiedName~ReviewFixes|FullyQualifiedName~ThingiProviderTests|FullyQualifiedName~NzbDrone.Core.Test.IndexerTests|FullyQualifiedName~NzbDrone.Core.Test.MovieTests|FullyQualifiedName~NzbDrone.Core.Test.HistoryTests"
```

## Updating to a new upstream release

1. Update `upstream.env` (tag, commit, base digest of the matching linuxserver tag).
2. `./build.sh --no-image`. If a hunk fails, see "Rebase notes" below.
3. Run `tests/CacheTests`. The two *rebase guard* tests fail if upstream changed
   `CustomFormatInput` or the custom-format specification types/settings; then review
   `CustomFormatMatchCache.Known/Same/InputKey` before shipping.
4. Run the decision-equivalence check in `bench/` (see bench/README.md) against stock.

## Rebase notes

The patch is 7 files, +472/−10 lines, most of it two new files. Risk per hunk:

| File | Change | Rebase risk |
|---|---|---|
| `CustomFormats/CustomFormatMatchCache.cs` | new | none textually, but **semantic risk**: if upstream adds an input to `CustomFormatInput`, a setting to a specification, or a new specification type, `Known/Same/InputKey` must be reviewed. New types bypass the cache automatically; new inputs or settings would not. The two *rebase guard* tests pin the exact shapes and fail loudly when they change. |
| `CustomFormats/CustomFormatCalculationService.cs` | 1 hunk, delegate to cache + short-circuit evaluator | low; this function has been stable since v5 |
| `DecisionEngine/DecisionBatchCache.cs` | new (indexer memo + `DecisionBatchCacheInvalidator` event handler) | none textually; **semantic**: if upstream adds an indexer write path that bypasses `ProviderFactory` events, the invalidator must be extended |
| `DownloadDecisionMaker.cs`, 2 specifications, `AggregateLanguages.cs` | one-line call-site changes | low; but if upstream adds *another* per-release indexer lookup it simply stays uncached (safe) |

`build.sh` refuses to build if the tag no longer matches the pinned commit or if any hunk does not apply.
Building only `Radarr.Core` with upstream's StyleCop analyzers enabled fails on upstream's own
files, so the build disables analyzers.

License: GPL-3.0, as upstream Radarr (see ../LICENSE).
