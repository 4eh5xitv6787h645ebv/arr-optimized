# sonarr-next

A small set of patches for [Sonarr](https://github.com/Sonarr/Sonarr) v5, built as a drop-in
`Sonarr.Core.dll`, `Sonarr.Api.V3.dll` and web UI (`bin/UI`) on top of the stock linuxserver image;
every other file is stock. The release decision logic is unchanged: given the same inputs, the same
releases are accepted or rejected for the same reasons, with the same custom formats and scores.
Patches 0001–0004 make decisions faster; 0005–0010 add an opt-in indexer rate-limit setting,
smaller speed-ups and fixes for upstream bugs outside the decision path; 0011 adds the
Settings > Optimize page; 0012 makes the ffprobe cache forget files the app replaces. Where a fix
changes normal operation or the media info that decisions later use, the entry says so.

| | |
|---|---|
| Upstream | `v5.0.0.3041` (`03f822c78293981e6e1459fbce59d11476b2107f`, develop) |
| Base image | `lscr.io/linuxserver/sonarr@sha256:c8c4a3dc…` (`5.0.0.3041-ls212`, the `develop` tag) |
| Image tag | `sonarr-optimized:5.0.0.3041-next` (local build) |
| SDK | .NET SDK 10.0.404 (upstream `global.json`) |

## Patches

Applied in numeric order by `build.sh` (`git apply`). Each applies on top of the ones before it.
There is no `0002` any more (see below); the numbers of the others are kept so links stay valid.

1. `0001` **Custom-format match cache.** Caches *which* custom formats match a release or file
   input. The key holds every value the supported condition types read, and the definition
   snapshot is compared on every call. Up to 50,000 entries, 6 h sliding expiry refreshed on every
   hit, least-recently-used batch eviction at the cap, lock-free hits. Scores, profiles and all
   decisions stay live. Unknown condition types bypass the cache.
2. `0003` **Per-batch read memo (`DecisionScope`).** Within one `GetSearchDecision`/`GetRssDecision`
   call, rows that every release re-reads (indexer definition, release profiles, episode history,
   episodes in a file) are read once. Nothing outside a batch is cached. A memoized lookup is
   reloaded after any write to the table it read (every `BasicRepository` write and the raw-SQL
   housekeepers report the table), so rows written during a batch, e.g. by Completed Download
   Handling, are seen exactly as on stock. `null` results and exceptions are never memoized. Also
   caches the provider-settings type lookup that scanned every exported type of `Sonarr.Core` per row.
3. `0004` **Blocklist title match in memory.** Within a batch, a series' blocklist rows are read
   once and SQLite's `LIKE '%' || title || '%'` is evaluated in memory with SQLite's exact rules
   (ASCII-only case folding, U+FFFE/U+FFFF equal to U+FFFD, `%`/`_` wildcards, no escape).
   PostgreSQL, NUL, surrogates, over-long titles and anything outside a batch use the original
   query.
4. `0005` **Opt-in indexer rate limit and cache-hit refund.** Off unless an environment variable is
   set; then only the timing of indexer requests and grabs changes, never what is decided.
   - `ARR_INDEXER_RATE_LIMIT_MS` (0–86400000) replaces the 2 s wait between requests to one indexer
     for indexer types without their own limit (types with their own limit keep it).
   - `ARR_INDEXER_CACHE_HIT_HEADER` (and `ARR_INDEXER_CACHE_HIT_VALUE`, default `HIT`): a response
     marked as a cache hit by a proxy in front of the indexer gives back the indexer's own wait slot.
     The slot is given back only if no later request has reserved one since.
   - With a lowered limit, grabs from one host stay at least 2 s apart, counted from when each grab is
     released, and the host's own interval is the last wait before a grab.
   - With no variable set, `RateLimit` is exactly 2 s and grabs make the stock
     `WaitAndPulseAsync(host, 2 s)` call (pinned by tests).

   Example: `-e ARR_INDEXER_RATE_LIMIT_MS=500 -e ARR_INDEXER_CACHE_HIT_HEADER=X-Cache`. The app logs
   the active settings once, at the first indexer request or grab (an indexer Test from the settings
   page counts as a request). Invalid values are logged and ignored. `0011` puts the same values (and
   the grab spacing) on the Optimize page; an environment variable still wins.
5. `0006` **Faster history total and series statistics.** On SQLite, an unfiltered history page counts
   all rows minus rows whose series or episode is gone, instead of joining both tables (50,000 rows:
   22.0 → 2.6 ms). The total is exactly stock's, including while orphaned rows wait for housekeeping.
   Filtered pages and PostgreSQL keep the stock count (PostgreSQL can plan the subtraction as a
   rescan of Episodes per History row; stock's join is fast there). Series statistics join file
   totals through a dictionary instead of a scan per season (54 → 18 ms).
6. `0007` **ffprobe result cache and timeout.** Media info is reused while the file stamp is unchanged
   before and after the probe: size, last write time and, for a symlink, the final target's path and
   last write time. At most 1,000 entries and about 32 MiB (estimated), 6 h idle expiry. All
   ffprobe runs for one file (the main probe, the longer probe when the audio channel layout is
   missing, and the frame probe for HDR) share one time limit; when it runs out the running ffprobe
   is stopped, an error is logged and the file gets no media info, instead of blocking forever.
   Without media info most files are not imported automatically ("Unable to determine if file is a
   sample") and the download is retried. The limit was a fixed 5 minutes in this patch; `0011` makes
   it a setting with a 15-minute default, where 0 calls ffprobe exactly as stock. Differences from
   stock: a failed probe of a file whose newest write is at least 60 s old is not retried for 10
   minutes; a file rewritten in place with the same size and the same modification time (and same
   symlink target) gets the old media info, unless the app itself imported, deleted or renamed a
   file at that path (`0012` drops the entry then); results for files written less than 60 s before
   the probe are reused for 60 s only.
7. `0008` **Import fixes.** An automatic import that throws goes back to `ImportPending` and is retried
   on the next check instead of staying in `Importing` until restart. No back-off: a permanent error
   is retried about once a minute (each retry runs the whole import; with 0007 an unchanged file is not
   probed again, and a failed probe of a settled file is repeated at most every 10 minutes), with one
   warning and then debug logging. A download that is mapped to no episodes (all of its episodes were
   removed or renumbered) is no longer treated as already imported (stock marked it Imported and could
   remove it from the client).
8. `0009` **Scheduler timer per instance.** Stock uses one static timer; every in-process restart
   (Restart button, restore, restart-requiring setting) adds a handler that is never removed, and the
   concurrent handlers can leave the timer dead so scheduled tasks stop. Each Scheduler now owns its
   timer and stops it on shutdown. With a single start, ticks are the same as stock (30 s).
9. `0010` **Duplicate extra files** (upstream PR #9338, extended). A tracked `-orig` extra-file row is
   deleted when metadata overwrites the tracked file, also when the `-orig` file is already gone (the
   file goes to the recycle bin first if present), and a daily housekeeper keeps only the newest row
   per (series, path). Known edge: if duplicate rows already exist at the `-orig` path and that file
   is missing, the lookup throws until the housekeeper or the next rescan cleans up.

10. `0011` **Settings > Optimize page** (Core, Api.V3 and the frontend). Adds `/settings/optimize`
    (sidebar entry after UI and a card on the Settings page) and `GET`/`PUT /api/v3/config/optimize`,
    `GET /api/v3/config/optimize/settings` and `GET /api/v3/config/optimize/status`. Every
    adjustable value of `0001`–`0007` becomes a setting (table below). The defaults are the
    constants of those patches, so with nothing changed the app behaves as with `0001`–`0010`,
    except that the ffprobe time limit is 15 minutes instead of 5. Values apply on Save without a
    restart: one immutable settings snapshot is swapped on start-up and on every config save, and
    the hot paths read it with one volatile read (no database query, no new locks). Cache limits
    are applied at once under each cache's existing lock (smaller = least recently used entries
    dropped, off = cleared).
11. `0012` **Forget replaced files' ffprobe results.** Import, delete and rename events
    (`EpisodeImportedEvent`, `EpisodeFileDeletedEvent`, `EpisodeFileRenamedEvent`) drop the cache
    entries for the paths involved, and a probe that overlapped such an event is not stored. With
    "Change File Date" an upgrade imported to the same path gets the old file's modification time,
    so a new file of the same size could otherwise get the old file's media info.

Patch `0002` (cache `Database.DatabaseType`) was removed. It had no effect in the running app:
repositories receive `MainDatabase`, which already reads the type once at startup. Its runner test
(`Test_DatabaseTypeOpensOnlyOnce`) was removed with it, since stock `Database` intentionally opens a
connection to read the type.

## Settings > Optimize (patch 0011)

Each value: a valid environment variable wins (the field is read-only on the page), then the value
saved on the page, then the default. An invalid environment variable is ignored with one warning in
the log and shown as an error under its field. Saved values are `Config` rows named
`optimize<Name>`; stock Sonarr ignores them.

| Setting | Environment variable | Default | Allowed | Notes |
|---|---|---|---|---|
| Indexer request spacing | `ARR_INDEXER_RATE_LIMIT_MS` | 2000 ms (stock) | page 0–10000, variable 0–86400000 | indexer types with their own limit (BroadcasTheNet: 5000 ms) keep it |
| Cache hit header | `ARR_INDEXER_CACHE_HIT_HEADER` | empty = off | header name (RFC 9110 token), up to 256 chars | see `0005` |
| Cache hit value | `ARR_INDEXER_CACHE_HIT_VALUE` | `HIT` | no `,`, `;` or control characters, up to 256 chars | compared without regard to case |
| ffprobe time limit | `ARR_FFPROBE_TIMEOUT_MINUTES` | 15 min | 0–1440, 0 = no limit | 0 calls `FFProbe.Analyse`/`GetFrames` exactly as stock |
| Grab spacing | `ARR_GRAB_SPACING_MS` | 2000 ms | 0–60000 | 2000 makes exactly the stock `WaitAndPulseAsync(host, 2 s)` call |
| Custom-format cache entries | `ARR_CF_CACHE_CAPACITY` | 50000 | 0–1000000, 0 = off | off = the stock evaluation, nothing cached |
| Custom-format cache lifetime | `ARR_CF_CACHE_LIFETIME_MINUTES` | 360 | 1–10080 | sliding |
| Custom-format cache input limit | `ARR_CF_CACHE_MAX_KEY_CHARS` | 4096 | 256–65536 | longer inputs are matched without the cache |
| Custom-format cache sweep | `ARR_CF_CACHE_SWEEP_SECONDS` | 60 | 1–3600 | how often idle entries are dropped |
| Per-search read memo | `ARR_DECISION_MEMO_ENABLED` | on | `true`/`false` (also yes/no, 1/0, on/off) | off = every lookup reads the database, blocklist uses the stock SQL |
| ffprobe cache entries | `ARR_FFPROBE_CACHE_CAPACITY` | 1000 | 0–100000, 0 = off | off = no file stamps read, probe as stock |
| ffprobe cache memory | `ARR_FFPROBE_CACHE_MAX_MB` | 32 | 0–1024, 0 = off | |
| ffprobe largest result | `ARR_FFPROBE_CACHE_MAX_ENTRY_KB` | 4096 | 1–131072 | capped by the cache memory |
| ffprobe idle lifetime | `ARR_FFPROBE_CACHE_IDLE_MINUTES` | 360 | 1–10080 | |
| ffprobe failure lifetime | `ARR_FFPROBE_CACHE_FAILURE_MINUTES` | 10 | 0–10, 0 = not remembered | can only be shorter than `0007`'s |
| ffprobe settle time | `ARR_FFPROBE_CACHE_SETTLE_SECONDS` | 60 | 60–3600 | can only be longer than `0007`'s |
| ffprobe recently-written lifetime | `ARR_FFPROBE_CACHE_UNSETTLED_SECONDS` | 60 | 0–60, 0 = not reused | can only be shorter than `0007`'s |
| ffprobe cache sweep | `ARR_FFPROBE_CACHE_SWEEP_SECONDS` | 60 | 1–3600 | |

The ffprobe cache matches a file on its stamp alone, and a file written less than a minute ago can
change under an unchanged stamp (coarse timestamps, attribute caches of network shares,
preallocation). So the settle time, recently-written lifetime and failure lifetime only allow values
at least as strict as `0007`'s. Read-only rows on the page, each with the reason it is fixed: the
cache eviction batches, BroadcasTheNet's own limit, the scheduler interval (stock 30 s), the
failed-import retry (stock 1-minute task), the duplicate extra-file cleanup (stock daily
housekeeping) and the blocklist `LIKE` pattern limit (50,000 bytes, SQLite's own limit; any other
value would make the in-memory comparison of `0004` differ from the stock SQL).

API: `GET /api/v3/config/optimize` returns the values (`null` = not saved, the default applies) and
the full list as `settings`. `PUT` changes only the values present in the body: a value left out
keeps its saved value, an explicit `null` resets it to the default. Fields locked by an environment
variable are ignored on `PUT`. Invalid values give HTTP 400 with one error per field. Risk warnings
are help texts on the page, not validation warnings (the stock API turns any validation warning into
a 400). The request-wait counters on the Status section only see requests paced by the cache-hit
limiter (cache-hit header set); with the header off, requests wait inside the stock
`RateLimitService` in `Sonarr.Common`, which is not rebuilt. The page's texts are English only: the
translation files are outside the rebuilt assemblies.

## Build

```sh
./build.sh            # clone tag, verify commit, apply patches, build artifacts/Sonarr.Core.dll,
                      # artifacts/Sonarr.Api.V3.dll and artifacts/UI/
./build.sh --image    # ...and docker build -t sonarr-optimized:5.0.0.3041-next .
```

The UI is built with upstream's own scripts (`yarn install --frozen-lockfile`, `yarn build`), so
Node.js 24 and yarn 1 are needed (`YARN="corepack yarn" ./build.sh` uses corepack). The
`Dockerfile` removes the stock `bin/UI` folder (its file names carry content hashes) and copies in
the two DLLs and the new UI.

Rebasing to a new upstream release: update `upstream.env` (tag, commit, version, base image digest,
SDK) and the `FROM` line in `Dockerfile`, run `./build.sh`, fix any patch that no longer applies,
then run the tests and the equivalence check below. Never put this DLL into a different Sonarr
version than the one it was built from.

## Tests

`tests/SonarrNextTests` runs against the built DLL plus the unchanged managed dependencies of the
base image:

```sh
id=$(docker create "$(. ./upstream.env; echo $BASE_IMAGE)") && docker cp "$id":/app/sonarr/bin ./sonarr-bin && docker rm "$id"
dotnet build tests/SonarrNextTests -c Release -p:CoreBin=$PWD/artifacts -p:StockBin=$PWD/sonarr-bin
dotnet tests/SonarrNextTests/bin/Release/net10.0/SonarrNextTests.dll $PWD/sonarr-bin
```

The LIKE test compares against Microsoft.Data.Sqlite and against the image's own
`System.Data.SQLite.dll` (the provider Sonarr uses). On the host both run on the bundled e_sqlite3
(3.41.2); `SONARR_NEXT_SQLITE_NATIVE=/path/to/libsqlite3.so` points System.Data.SQLite at another
native build, and `SONARR_NEXT_EXPECT_SQLITE=<version>` fails the test unless that version is in use.
To run against Sonarr's own native SQLite (3.53.4, musl), publish the runner self-contained and run it
inside the image:

```sh
dotnet publish tests/SonarrNextTests -c Release -r linux-musl-x64 --self-contained \
  -p:CoreBin=$PWD/artifacts -p:StockBin=$PWD/sonarr-bin -o ./runner-musl
rm runner-musl/System.Data.SQLite.dll && cp sonarr-bin/libe_sqlite3.so runner-musl/   # use the image's copies
docker run --rm --network none -e SONARR_NEXT_EXPECT_SQLITE=3.53.4 -v $PWD/runner-musl:/t:ro \
  --entrypoint /t/SonarrNextTests sonarr-optimized:5.0.0.3041-next /app/sonarr/bin
```

They cover the native custom-format truth table under input and definition mutation, stale-input
protection (original language, languages, quality, release type, flags, size, format and profile
edits), capacity, LRU eviction order, refresh-on-hit and idle expiry, memory per entry at 50,000,
the batch memo (null results, invalidation on table writes, a deleted indexer behaving and logging
as on stock), and 2.3 million comparisons per provider of the LIKE
emulation against real SQLite (U+FFFD/U+FFFE/U+FFFF, non-ASCII case variants, Turkish and Greek
forms, fullwidth letters, combining marks, `%`/`_`/backslash inside titles, titles up to 20,000
characters and the 50,000-byte pattern limit).

`tests/CoreTest` holds NUnit fixtures for upstream's own test project (real repositories and test
database): a deleted indexer during a batch, history, blocklist and episode-file rows written
during a batch, and in-batch blocklist title matching against the stock SQL query. It also holds one
folder per later patch (`RateLimit`, `Perf`, `FFprobe`, `Imports`, `Scheduler`, `Extras`,
`Optimize`, `Polish`), laid out like upstream's `src/` so it can be copied in as is: the regression
tests for every bug fix (each fails on stock and passes with the patch), the equivalence tests for
the speed-ups, and the Optimize page's tests (including `NzbDrone.Api.Test` for the controller). See
`tests/CoreTest/README.md`.

Full-suite result for this release (upstream filter `Category!=ManualTest&Category!=AutomationTest&
Category!=IntegrationTest&Category!=WINDOWS`, plus `Category!=PerfNextBenchmark`): patched tree with
every fixture added 6,117 passed, 28 skipped, 0 failed; stock `v5.0.0.3041` 5,725 passed, 27 skipped,
0 failed. The extra skip is the PostgreSQL-only history test, which passes against PostgreSQL 16.
`Sonarr.Api.Test` with the Optimize controller fixture: 47 passed. `Sonarr.Host.Test`: 24 passed,
4 skipped (including both container wiring fixtures). The test projects also build with upstream's
`TreatWarningsAsErrors=true`.

## Benchmark and equivalence harness

`bench/` holds the offline harness (no private data). Inputs are a `sonarr.db` snapshot and a
search-cache database, which stay outside this repository.

- `build_replay.py`: builds an offline replay index from the search cache. It keeps release titles,
  sizes, publish times and indexer ids, plus the verbatim cached pages with every URL replaced.
- `offline_newznab.py`: Newznab endpoint serving that index. It never fetches anything.
- `make_clone.py`: prepares an isolated config. Indexers point at the replay with dummy keys, other
  indexers are disabled, scheduled tasks are parked, and download clients, notifications and import
  lists are deleted from the copy (the snapshot is never written).
- `lab.py`: `up`/`down` (an `--internal` Docker network, no egress), `search` (full decision
  output per query), `bench` (cold and warm search timing), `rss`, `rss-decisions` (reads the
  rotated debug logs oldest first), `profile` (dotnet-trace).
- `compare.py`: field-by-field comparison of two `search` outputs.
- `analyze_speedscope.py`: profile summary restricted to the decision code path.

## Rebase notes and risks

Port effort this time: none textual. `CustomFormats/` was unchanged between 2569 and 3041 and the
old patch applied cleanly. For the next rebase:

- **`0001` (highest semantic risk, low textual risk).** It hooks one method. The risk is semantic:
  if upstream changes what a condition type reads, adds fields to `CustomFormatInput`, or changes
  `SpecificationMatchesGroup`, then `Known`, `Same` and `InputKey` must be re-audited. New condition
  *types* are safe (they bypass the cache) but are not accelerated until added. Check
  `git diff OLD..NEW -- src/NzbDrone.Core/CustomFormats` on every rebase.
- **`0003`.** Seven one-line call-site edits in specifications and services, the write hooks in
  `BasicRepository` and three housekeepers. Conflicts are likely only if upstream rewrites those
  lines. Semantic checks: memoized results must stay read-only for their consumers, each memo kind
  must be the table its loader reads, and every write to a memoized table must call
  `DecisionScope.TableChanged` after it commits. On a rebase, grep for new raw-SQL writes to
  `History`, `Blocklist`, `Episodes` (other than `Monitored`), `Indexers` and `ReleaseProfiles`.
- **`0004`.** Depends on `BlocklistRepository.BlocklistedByTitle` still being
  `SourceTitle.Contains(title)` and on `WhereBuilderSqlite` translating `Contains` as
  `LIKE '%' || x || '%'` with no `ESCAPE`. If either changes, the emulation must change too or the
  patch should be dropped. The tests check the LIKE semantics against SQLite but not the SQL text.
  On an SQLite upgrade in the base image, rerun the LIKE test inside the image (README): it checks
  the engine version, the 50,000-byte pattern limit and `sqlite3Utf8Read`'s character mapping.
- **`0005`.** Two new files plus three one-line edits (`HttpIndexerBase.RateLimit`,
  `FetchIndexerResponse`, `DownloadService` grab wait). The cache-hit refund reuses
  `RateLimitService`'s store and arithmetic, so re-check it if upstream changes `RateLimitService`.
  Known limits: the limiter is reached through a static instance (one per process in production);
  the shared `<host>` key still couples indexers on one host, as on stock; the cache-hit header is
  trusted as sent.
- **`0006`.** Body-only changes in `HistoryRepository.GetPaged` and `SeriesStatisticsRepository`. If
  upstream changes the joins of the paged history query, the unfiltered count must change with it
  (the equivalence tests compare it with the records query, including orphaned rows).
- **`0007`.** New `MediaInfoProbeCache` plus `VideoFileInfoReader` changes. The timeout follows
  upstream PR #9346; drop that part if it merges.
- **`0008`.** Body-only changes in `CompletedDownloadService` and `TrackedDownloadAlreadyImported`.
- **`0009`.** `Scheduler.cs` only; upstream issue #9312 is still open.
- **`0010`.** The housekeeper file is upstream PR #9338's; the renamer change extends it to the
  missing-file case. When #9338 merges, keep only that extension.
- **`0011`.** Mostly new files (`NzbDrone.Core/Optimization`, `Sonarr.Api.V3/Config/Optimize*`,
  `frontend/src/Settings/Optimize`) plus small edits in the `0001`–`0007` files, which read their
  limits from `OptimizeSettings.Current` instead of constants (the constants stay as the defaults),
  and three frontend lines (route, sidebar entry, Settings card). `IConfigService` only gains
  `GetOptimizeValue`/`SaveOptimizeValues`. `OptimizeSettingsServiceFixture` fails if a constant of
  those patches is not listed on the page, and `OptimizeSettingBoundsFixture` pins every default and
  bound. Since the UI is rebuilt, a rebase must rebuild `bin/UI` from the same tag, never mix it
  with another version's UI.
- **`0012`.** `VideoFileInfoReader` gains three `IHandle<>` interfaces (additive); keep them on the
  class that owns `_probeCache`.
- Version-pinned: the DLLs and the UI must only ever be used with the base image of the same Sonarr
  version.
