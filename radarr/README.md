# radarr-next

A small, rebase-friendly patch series for [Radarr](https://github.com/Radarr/Radarr) that makes
interactive searches and RSS decisions faster **without changing any decision**: the same releases,
rejections, custom formats and scores as stock. Patches `0002`–`0008` add an opt-in indexer
rate-limit setting, smaller speed-ups and fixes for upstream bugs outside the decision path; a fix
changes behaviour only in the failure case it fixes. It is shipped as a drop-in `Radarr.Core.dll` on
top of the stock linuxserver image, pinned by digest.

| | |
|---|---|
| Upstream | `v7.0.3.10918` @ `af772b051ac27a0615c7f8b7f78622b6dc183be0` |
| Base image | `lscr.io/linuxserver/radarr@sha256:044788f5508b7f67a3e0368cdd142c77c4d4cd5aec2efaba53e1a68b6b6231e7` (`7.0.3.10918-ls328`, the `latest` tag on 2026-11-24) |
| SDK | .NET SDK 10.0.404 (upstream `global.json`) |
| Image | `radarr-optimized:7.0.3.10918-next` (local build; published as `ghcr.io/4eh5xitv6787h645ebv/radarr-optimized`) |

## What the patches do

`build.sh` applies `patches/NNNN-*.patch` in numeric order; each applies on top of the ones before it.

### `0001-radarr-core.patch`: faster decisions

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

### `0002`–`0008`

- `0002` **Opt-in indexer rate limit and cache-hit refund.** Off unless set; then only the timing of
  indexer requests and grabs changes.
  - `ARR_INDEXER_RATE_LIMIT_MS` (0–86400000) replaces the 2 s wait between requests to one indexer
    for indexer types without their own limit (types with their own limit keep it; TorrentPotato,
    which hard-coded 2 s, follows the setting).
  - `ARR_INDEXER_CACHE_HIT_HEADER` (and `ARR_INDEXER_CACHE_HIT_VALUE`, default `HIT`): a response
    marked as a cache hit by a proxy in front of the indexer gives back the indexer's own wait slot,
    unless a later request has already reserved the next one.
  - With a lowered limit, grabs from one host stay at least 2 s apart, counted from when each grab is
    released, and the host's own interval is the last wait before a grab.
  - With no variable set, requests and grabs are timed exactly as on stock.

  Example: `-e ARR_INDEXER_RATE_LIMIT_MS=500 -e ARR_INDEXER_CACHE_HIT_HEADER=X-Cache`. The app logs
  the active settings once, at the first indexer request or grab; invalid values are logged and
  ignored. A lowered limit on an indexer you reach directly can get you rate-limited (HTTP 429).
- `0003` **Faster movie statistics, release profiles and indexer settings type.** Movie statistics
  join file totals through a dictionary (48.8 → 7.6 ms). Release profiles are cached in memory and
  cleared after every add, update and delete, under one lock with loading (1.4 µs instead of 37 µs per
  lookup). The provider settings type is looked up once per type name instead of scanning every
  exported type per row (`IndexerRepository.All()` 1,792 → 166 µs). Same results as stock.
- `0004` **ffprobe result cache and timeout.** Media info is reused while the file stamp is unchanged
  before and after the probe: size, last write time and, for a symlink, the final target's path and
  last write time (an import now probes each file once instead of three times). At most 1,000 entries
  and about 32 MiB, 6 h idle expiry. A probe that runs longer than 5 minutes returns no media info
  instead of blocking forever. Differences from stock (failure cases only): a failed probe of a file
  whose newest write is at least 60 s old is not retried for 10 minutes; a file rewritten in place with
  the same size and modification time (and same symlink target) gets the old media info.
- `0005` **Import fixes.** An automatic import that throws goes back to `ImportPending` and is retried
  on the next check instead of staying in `Importing` until restart (no back-off: a permanent error is
  retried about once a minute, quietly after the first warning). A finished download whose movie link
  was lost is completed with the movie id from the import results or from its latest history item,
  and `IsImported` no longer throws when the movie is null. If no movie id can be found, the download
  is not marked imported.
- `0006` **Scheduler timer per instance.** Stock uses one static timer; every in-process restart
  (Restart button, restore, restart-requiring setting) adds a handler that is never removed, and the
  concurrent handlers can leave the timer dead so scheduled tasks stop. Each Scheduler now owns its
  timer and stops it on shutdown.
- `0007` **Duplicate extra files** (port of Sonarr PR #9338, extended). A tracked `-orig` extra-file row
  is deleted when metadata overwrites the tracked file, also when the `-orig` file is gone, and a
  daily housekeeper keeps only the newest row per (movie, path).
- `0008` **Exclusive housekeeping, safer restore.**
  - Housekeeping (with `VACUUM`) starts only when no other command is running, and a queued
    housekeeping never holds back other commands. On an instance that is never idle it waits for the
    first idle moment. Commands queued while housekeeping runs start after it.
  - A restore upload must be a Radarr database (SQLite with `VersionInfo`, `Config` and `Movies`
    tables) or a valid config; `logs.db` and other files get an HTTP 4xx instead of replacing the
    live database.
  - The previous database is kept as `radarr.db.pre-restore*` until the app starts on the restored
    one, and all database files are moved back if the final move fails. A restored database that
    fails migration is not rolled back automatically (the `.pre-restore` files remain for manual
    recovery).
  - Housekeeping exclusivity gates the command queue only; API/UI writes can still overlap `VACUUM`.

## Layout

```
patches/0001-radarr-core.patch  the decision speed-ups, against the pinned tag (7 files)
patches/0002..0008-*.patch     the later patches, each on top of the previous ones
upstream.env                pinned tag, commit and base-image digest
build.sh                    clone tag -> verify commit -> apply patches -> build DLL -> build image
Dockerfile                  FROM <base digest>; COPY artifacts/Radarr.Core.dll
tests/CacheTests            truth-table, invalidation, expiry/LRU/cap, batch-memo, memory and rebase-guard tests
tests/UpstreamFixtures      NUnit regression tests (copy into the upstream tree; see tests/README.md)
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

The regression and equivalence fixtures for `0002`–`0008` are in `tests/UpstreamFixtures/<Group>/`;
`tests/README.md` says how to copy and run them. Full-suite result for this release (filter
`Category!=ManualTest&Category!=AutomationTest&Category!=IntegrationTest&Category!=WINDOWS&Category!=PerfNextBenchmark`):
patched tree with every fixture added 4,446 passed, 26 skipped, 0 failed; stock `v7.0.3.10918`
4,173 passed, 26 skipped, 0 failed.

## Updating to a new upstream release

1. Update `upstream.env` (tag, commit, base digest of the matching linuxserver tag).
2. `./build.sh --no-image`. If a hunk fails, see "Rebase notes" below.
3. Run `tests/CacheTests`. The two *rebase guard* tests fail if upstream changed
   `CustomFormatInput` or the custom-format specification types/settings; then review
   `CustomFormatMatchCache.Known/Same/InputKey` before shipping.
4. Run the decision-equivalence check in `bench/` (see bench/README.md) against stock.

## Rebase notes

`0002`–`0008`: `0002` reuses `RateLimitService`'s store and arithmetic, so re-check it if upstream
changes `RateLimitService`. `0004`'s timeout follows upstream PR #9346; drop that part if it merges.
`0006` fixes upstream issue #9312 (Sonarr; Radarr has the same code). `0008` changes
`CommandQueue.TryGet` for `HousekeepingCommand` only; never ship its `IsExclusive` without that
`CommandQueue` change, or a queued housekeeping holds up the queue again. The other patches change
method bodies plus new private members or new types only.

`0001-radarr-core.patch` is 7 files, +472/−10 lines, most of it two new files. Risk per hunk:

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
