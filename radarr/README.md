# radarr-next

A small, rebase-friendly patch series for [Radarr](https://github.com/Radarr/Radarr) that makes
interactive searches and RSS decisions faster **without changing the decision logic**: given the
same inputs, the same releases, rejections, custom formats and scores as stock. Patches
`0002`–`0008` add an opt-in indexer rate-limit setting, smaller speed-ups and fixes for upstream
bugs outside the decision path; `0009` adds the Settings > Optimize page; `0010` bounds the
housekeeping wait, makes the ffprobe cache forget files the app replaces and hands out copies of
cached release profiles. Where a fix changes normal operation or the media info that decisions later
use, the entry says so. It is shipped as a drop-in `Radarr.Core.dll`, `Radarr.Api.V3.dll` and web UI
(`bin/UI`) on top of the stock linuxserver image, pinned by digest; every other file is stock.

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
  the active settings once, at the first indexer request or grab (an indexer Test from the settings
  page counts as a request); invalid values are logged and ignored. A lowered limit on an indexer
  you reach directly can get you rate-limited (HTTP 429). `0009` puts the same values (and the grab
  spacing) on the Optimize page; an environment variable still wins.
- `0003` **Faster movie statistics, release profiles and indexer settings type.** Movie statistics
  join file totals through a dictionary (48.8 → 7.6 ms). Release profiles are cached in memory and
  cleared after every add, update and delete, under one lock with loading (1.4 µs instead of 37 µs
  per lookup; callers get copies since `0010`). The provider settings type is looked up once per
  type name instead of scanning every exported type per row (`IndexerRepository.All()` 1,792 → 166
  µs). Same results as stock.
- `0004` **ffprobe result cache and timeout.** Media info is reused while the file stamp is unchanged
  before and after the probe: size, last write time and, for a symlink, the final target's path and
  last write time (an import now probes each file once instead of three times). At most 1,000 entries
  and about 32 MiB, 6 h idle expiry. All ffprobe runs for one file (the main probe, the longer probe
  when the audio channel layout is missing, and the frame probe for HDR) share one time limit; when
  it runs out the running ffprobe is stopped, an error is logged and the file gets no media info,
  instead of blocking forever. Without media info most files are not imported automatically
  ("Unable to determine if file is a sample") and the download is retried. The limit was a fixed 5
  minutes in this patch; `0009` makes it a setting with a 15-minute default, where 0 calls ffprobe
  exactly as stock. Differences from stock: a failed probe of a file whose newest write is at least
  60 s old is not retried for 10 minutes; a file rewritten in place with the same size and
  modification time (and same symlink target) gets the old media info, unless the app itself
  imported, deleted or renamed a file at that path (`0010` drops the entry then).
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
    housekeeping never holds back other commands. It waits for running commands for at most
    `HousekeepingCommand.MaxDeferral` (30 minutes, `0010`); after that it starts alongside them and
    is not exclusive for that run, as on stock. A housekeeping started by hand from System > Tasks
    can therefore show as queued for up to 30 minutes while other commands run. The wait is logged
    once at Debug, and a housekeeping that starts alongside other commands after the wait is logged
    at Info. Commands queued while housekeeping runs start after it.
  - A restore upload must be a Radarr database (SQLite with `VersionInfo`, `Config` and `Movies`
    tables) or a valid config; `logs.db` and other files get an HTTP 4xx instead of replacing the
    live database.
  - The previous database is kept as `radarr.db.pre-restore*` until the app starts on the restored
    one, and all database files are moved back if the final move fails. A restored database that
    fails migration is not rolled back automatically (the `.pre-restore` files remain for manual
    recovery).
  - Housekeeping exclusivity gates the command queue only; API/UI writes can still overlap `VACUUM`.

- `0009` **Settings > Optimize page** (Core, Api.V3 and the frontend). Adds `/settings/optimize`
  ("Optimize" in the Settings menu after UI, and a card on the Settings page) and
  `GET`/`PUT /api/v3/config/optimize`, `GET /api/v3/config/optimize/settings` and
  `GET /api/v3/config/optimize/status`. Every adjustable value of `0001`–`0004` becomes a setting
  (table below). The defaults are the constants of those patches, so with nothing changed the app
  behaves as with `0001`–`0008`, except that the ffprobe time limit is 15 minutes instead of 5.
  Values apply on Save without a restart: one immutable settings snapshot is swapped on start-up
  and on every config save, and the hot paths read it with one volatile read. Cache limits are
  applied at once under each cache's own lock. One deliberate change in the ffprobe cache: a failed
  probe of a recently written file now also removes an older result for that path, so the next read
  probes again (as stock always does) instead of reusing the older result.
- `0010` **Bounded housekeeping wait, forget replaced files' ffprobe results, release-profile copies.**
  - A queued housekeeping waits for running commands for at most `HousekeepingCommand.MaxDeferral`
    (30 minutes); after that it starts alongside them and is not exclusive for that run, as on
    stock. The wait is logged once at Debug, and a housekeeping that starts alongside other commands
    after the wait is logged at Info. While a housekeeping waits, the queue wakes up by itself at its
    deadline, so a stuck command cannot hold it back forever.
  - Import, delete and rename events (`MovieFileImportedEvent`, `MovieFileDeletedEvent`,
    `MovieFileRenamedEvent`) drop the ffprobe cache entries for the paths involved, and a probe that
    overlapped such an event is not stored. With "Change File Date" an upgrade imported to the same
    path gets the old file's modification time, so a new file of the same size could otherwise get
    the old file's media info.
  - The release-profile cache of `0003` hands out copies: same rows, same order, same filters.

## Settings > Optimize (patch 0009)

Each value: a valid environment variable wins (the field is read-only on the page), then the value
saved on the page, then the default. An invalid environment variable is ignored with one warning in
the log and shown as an error under its field. Saved values are `Config` rows named
`optimize<Name>`; stock Radarr ignores them. The environment variables have the same names as in the
Sonarr image.

| Setting | Environment variable | Default | Allowed | Notes |
|---|---|---|---|---|
| Indexer request spacing | `ARR_INDEXER_RATE_LIMIT_MS` | 2000 ms (stock) | page 0–10000, variable 0–86400000 | indexer types with their own limit (PassThePopcorn: 4000 ms) keep it; a saved value outside the page range falls back to the default |
| Cache hit header | `ARR_INDEXER_CACHE_HIT_HEADER` | empty = off | header name (RFC 9110 token), up to 256 chars | see `0002` |
| Cache hit value | `ARR_INDEXER_CACHE_HIT_VALUE` | `HIT` | no `,`, `;` or control characters, up to 256 chars | also applies with a header saved on the page |
| ffprobe time limit | `ARR_FFPROBE_TIMEOUT_MINUTES` | 15 min | 0–1440, 0 = no limit | 0 calls `FFProbe.Analyse`/`GetFrames` exactly as stock |
| Grab spacing | `ARR_GRAB_SPACING_MS` | 2000 ms | 0–60000 | 2000 makes exactly the stock `WaitAndPulseAsync(host, 2 s)` call |
| Custom-format cache entries | `ARR_CF_CACHE_CAPACITY` | 50000 | 0–1000000, 0 = off | off = the uncached evaluation |
| Custom-format cache lifetime | `ARR_CF_CACHE_LIFETIME_MINUTES` | 360 | 1–10080 | sliding, checked at every lookup |
| Custom-format cache input limit | `ARR_CF_CACHE_MAX_KEY_CHARS` | 4096 | 256–65536 | longer inputs are matched without the cache |
| Per-search indexer memo | `ARR_DECISION_MEMO_ENABLED` | on | `true`/`false` (also yes/no, on/off, 1/0) | off = load on every use |
| Release-profile cache | `ARR_RELEASE_PROFILE_CACHE` | on | as above | off = read from the database every time |
| ffprobe cache entries | `ARR_FFPROBE_CACHE_CAPACITY` | 1000 | 0–100000, 0 = off | off = probe every read, as stock |
| ffprobe cache memory | `ARR_FFPROBE_CACHE_MAX_MB` | 32 | 0–1024, 0 = off | |
| ffprobe largest result | `ARR_FFPROBE_CACHE_MAX_ENTRY_KB` | 4096 | 1–131072 | capped by the cache memory; the page shows the value in effect |
| ffprobe failure lifetime | `ARR_FFPROBE_CACHE_FAILURE_MINUTES` | 10 | 0–1440, 0 = not remembered | warning above 60 |
| ffprobe idle lifetime | `ARR_FFPROBE_CACHE_IDLE_MINUTES` | 360 | 1–10080 | |
| ffprobe settle time | `ARR_FFPROBE_CACHE_SETTLE_SECONDS` | 60 | 5–3600 | warning below 30 |
| ffprobe recently-written lifetime | `ARR_FFPROBE_CACHE_UNSETTLED_SECONDS` | 60 | 0–3600, 0 = not reused | warning above 300 |
| ffprobe cache sweep | `ARR_FFPROBE_CACHE_SWEEP_SECONDS` | 60 | 1–3600 | |

Unlike the Sonarr image, the ffprobe settle time, recently-written lifetime and failure lifetime
accept values looser than `0004`'s defaults (shown with a warning). The cache matches a file on its
stamp alone, so a settle time below 60 s or a longer recently-written lifetime can reuse media info
of a file that changed under an unchanged stamp, and a long failure lifetime keeps a file without
media info (and so not imported) long after a brief failure. Keep the defaults unless you know your
storage. Read-only rows on the page, each with the reason it is fixed: the ffprobe eviction batch,
PassThePopcorn's own limit, the import retry interval, the indexer settings-type lookup and the
housekeeping maximum wait (30 minutes, `0010`).

API: `GET /api/v3/config/optimize` returns the values (Radarr's JSON leaves out values that are not
saved) and the full list as `tunables`. `PUT` changes only the values it carries: a missing or
`null` value is left unchanged, like the stock config endpoints, and sending a value's **default**
resets it (the page sends cleared or reset fields as their default). Fields locked by an environment
variable are ignored on `PUT`. Invalid values, also numbers too large for 32 bits, give HTTP 400
with one error per field. The request-wait counters on the Status section only see requests paced
by the cache-hit limiter (cache-hit header set); with the header off, requests wait inside the
stock `HttpClient` in `Radarr.Common`, which is not rebuilt. The page's texts are English only:
Radarr's translation files ship next to the DLLs and are not rebuilt.

## Layout

```
patches/0001-radarr-core.patch  the decision speed-ups, against the pinned tag (7 files)
patches/0002..0010-*.patch     the later patches, each on top of the previous ones
upstream.env                pinned tag, commit and base-image digest
build.sh                    clone tag -> verify commit -> apply patches -> build DLLs and UI -> build image
Dockerfile                  FROM <base digest>; replace Radarr.Core.dll, Radarr.Api.V3.dll and bin/UI
tests/CacheTests            truth-table, invalidation, expiry/LRU/cap, batch-memo, memory and rebase-guard tests
tests/UpstreamFixtures      NUnit regression tests (copy into the upstream tree; see tests/README.md)
bench/                      offline lab: fail-closed indexer stub, offline search-cache builder,
                            sanitized clone setup, benchmark, decision-equivalence and invalidation checks
```

## Build

```
DOTNET=/path/to/dotnet-10.0.404/dotnet ./build.sh          # DLLs, UI + image radarr-optimized:<version>-next
./build.sh --no-image
```

`build.sh` refuses to build if the tag no longer points at the pinned commit or a patch does not
apply cleanly. It builds `Radarr.Api.V3` (which builds `Radarr.Core` too) and the web UI with
upstream's own scripts (`yarn install --frozen-lockfile`, `yarn run build --env production`; Node.js
24 and yarn 1, or `YARN="corepack yarn"`), and puts `Radarr.Core.dll`, `Radarr.Api.V3.dll` and `UI/`
into `artifacts/`. The `Dockerfile` removes the stock `bin/UI` folder (its file names carry content
hashes) and copies in the two DLLs and the new UI; every other file of the base image is stock.

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

The regression and equivalence fixtures for `0002`–`0010` are in `tests/UpstreamFixtures/<Group>/`;
`tests/README.md` says how to copy and run them. Full-suite result for this release (filter
`Category!=ManualTest&Category!=AutomationTest&Category!=IntegrationTest&Category!=WINDOWS&Category!=PerfNextBenchmark`):
patched tree with every fixture added 4,677 passed, 26 skipped (one 2-second timing check of
`0002`, run while image builds loaded the machine, was 0.03 s short and passed on three reruns of its
fixture); stock `v7.0.3.10918` 4,173 passed, 26 skipped, 0 failed. `Radarr.Api.Test` with the
Optimize controller fixture: 39 passed. `Radarr.Host.Test`: 23 passed, 5 skipped (including both
container wiring fixtures). The test projects also build with upstream's `TreatWarningsAsErrors=true`.

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
`CommandQueue` change, or a queued housekeeping holds up the queue again; `0010` adds the 30-minute
bound in the same method and a timed wait in `GetConsumingEnumerable`. The other patches change
method bodies plus new private members or new types only.

`0009`: mostly new files (`NzbDrone.Core/Optimize`, `Radarr.Api.V3/Config/Optimize*`,
`frontend/src/Settings/Optimize`) plus small edits in the `0001`–`0004` files, which read their
limits from `OptimizeSettings.Current` instead of constants (the constants stay as the defaults).
The frontend edits outside `Settings/Optimize` are the route, the settings menu entry, the Settings
card, the settings store section and one guard in `selectSettings.getValidationFailures` (a 400
whose body is not a list of field errors is shown as an error instead of blanking the page).
`IConfigService` only gains members. `OptimizeSettingsFixture` fails if a constant of those patches
is not listed on the page. The housekeeping maximum wait of `0010` is found by reflection
(`HousekeepingCommand.MaxDeferral`); rename both together. `0010`'s `VideoFileInfoReader` gains three
`IHandle<>` interfaces (additive); keep them on the class that owns `_probeCache`. Since the UI is
rebuilt, it must always come from the same tag as the DLLs.

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
