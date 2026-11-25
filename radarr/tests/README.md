# Tests

`CacheTests` loads the **patched** `Radarr.Core.dll` and compares every cached custom-format
result with a native truth table: the stock grouping rule evaluated directly on the live
specification objects.

```
dotnet run -c Release -p:CoreBin=<dir with patched Radarr.Core.dll> -p:StockBin=<stock /app/radarr/bin> \
  -- <stock /app/radarr/bin> [--memory]
```

| Group | Tests |
|---|---|
| Rebase guards | `CustomFormatInput` shape; specification types and their settings |
| Equivalence | truth table over every input dimension incl. in-place edits; definition edits (values, required/negate, order, membership, names, replaced spec objects); every scalar setting; unknown types bypass; result isolation; 10,000 concurrent evaluations |
| Movie metadata | year fallback when the parsed year is 0 (fixed key bug); original-language edit; different movie objects with the same release text |
| Limits | oversized (>4,096 chars) inputs not retained; never more than 50,000 entries |
| Expiry/eviction | LRU eviction order at the cap; refresh-on-hit; idle expiry at exactly 6 h; idle tail purge on insert; old plan cannot populate a new generation |
| Batch memo | scope, null results not memoized, nesting, thread isolation; no movie-history memo; indexer writes mid-batch (update, a write racing a load, delete via Find and Get) and the event handler's generation bump |
| `--memory` | managed bytes retained per entry at 50,000 entries, typical and worst case |

The suite also runs against older builds (tolerant lookups) so it can demonstrate regressions:
against a port of the original 6.0.4 patch it fails the truth table and the year-fallback test.

## Upstream NUnit fixtures (`UpstreamFixtures/`)

Regression tests for the review findings on `0001-radarr-core.patch`, written against upstream's own
test framework. Copy the `*.cs` files at the top of `UpstreamFixtures/` into the patched tree's
`src/NzbDrone.Core.Test/ReviewFixes/` and run:

```
dotnet test src/NzbDrone.Core.Test/Radarr.Core.Test.csproj -c Debug -f net10.0 \
  --filter "FullyQualifiedName~ReviewFixes" -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
```

| Fixture | Tests | Stock | Pre-fix patch | Fixed (`6389a9e1`) |
|---|---|---|---|---|
| `ReviewImdbShortcutFixture` | duplicate IMDb id maps to the row stock picks; TMDb and IMDb exact-ID matches see the row as it is at decision time (profile, monitored) | pass | 3 fail | pass |
| `ReviewBatchMemoFixture` | history written mid-batch (`AlreadyImportedSameName`); indexer tags edited mid-batch; indexer deleted mid-batch; Find-null never makes Get-based specs throw | pass | 3 fail (the Find/Get guard passes) | pass |
| `R2OverlapHistoryFixture` (re-review) | an import written while a slow indexer is still fetching is seen by the fast indexer's release (`AlreadyImportedSameName`) | pass | fail on `0263cf9d` (approved) | pass |
| `R2OverlapFixture` (re-review) | every release is decided after the last indexer answers | pass | fail on `0263cf9d` (fast batch decided before the write) | pass |

They compile against stock and older builds too: the batch-memo pieces are resolved by name.

### Fixtures for patches 0002–0008

Each folder below mirrors upstream's `src/` folder, so it is copied over the build tree as is.

| Folder | Patch | Fixtures |
|---|---|---|
| `RateLimit` | 0002 | `IndexerTests/RateLimitTests/*` (options parsing, the cache-hit refund under threads and a fixed clock, `HttpIndexerBase` and TorrentPotato wiring, real-HTTP timing of searches, grabs and redirects) and `NzbDrone.Host.Test/IndexerRateLimitContainerFixture.cs` (container wiring) |
| `Perf` | 0003 | `PerfNext/*`: statistics equal stock's; the release-profile cache is cleared on every write and a load that overlaps a write never keeps old rows; the settings-type cache is used |
| `FFprobe` | 0004 | `MediaFiles/MediaInfo/*` and the four small sample files in `Files/Media` (`make-media.sh` regenerates them). Uses the ffprobe upstream's test project already ships; the timeout tests also need `/bin/sh`, `mkfifo` and `/proc`. The symlink and timeout tests are skipped on Windows |
| `Imports` | 0005 | `ImportFixes/*`: a throwing import is retried; a lost movie link is completed from import results or history |
| `Scheduler` | 0006 | `JobTests/SchedulerFixture.cs`; `SchedulerRestartMeasurement.cs` is `[Explicit]` (measurement only) |
| `Extras` | 0007 | `Extras/Others/*`, `Housekeeping/*` |
| `RadarrDb` | 0008 | `Messaging/Commands/HousekeepingCommandQueueFixture.cs`, `BackupTests/*`, `Datastore/DatabaseRestorationServiceFixture.cs` |

```sh
for g in RateLimit Perf FFprobe Imports Scheduler Extras RadarrDb; do cp -r tests/UpstreamFixtures/$g/. <tree>/src/; done
cd <tree>
dotnet build src/NzbDrone.Core.Test/Radarr.Core.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
dotnet test _tests/net10.0/Radarr.Core.Test.dll --filter "FullyQualifiedName~RateLimitTests|FullyQualifiedName~PerfNext|FullyQualifiedName~MediaFiles.MediaInfo|FullyQualifiedName~ImportFixes|FullyQualifiedName~JobTests.SchedulerFixture|FullyQualifiedName~OtherExtraFileRenamer|FullyQualifiedName~DuplicateExtraFiles|FullyQualifiedName~BackupTests|FullyQualifiedName~DatabaseRestorationServiceFixture|FullyQualifiedName~HousekeepingCommandQueueFixture"
```

`[Category("PerfNextBenchmark")]` tests are `[Explicit]` timings, not checks. To run the database
fixtures against PostgreSQL, set `Radarr__Postgres__Host`, `__Port`, `__User` and `__Password`.

The container fixture needs the Host test project and `Radarr.Mono.dll` next to it:

```sh
dotnet build src/NzbDrone.Mono/Radarr.Mono.csproj -c Release -p:EnableAnalyzers=false
dotnet build src/NzbDrone.Host.Test/Radarr.Host.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
cp _output/net10.0/Radarr.Mono.dll _tests/net10.0/
dotnet test _tests/net10.0/Radarr.Host.Test.dll
```

Some fixtures only compile on a patched tree, because they use the new types
(`IndexerRateLimitOptions`, `IndexerCacheHitRateLimiter`, `MediaInfoProbeCache`). The others
compile on stock too, which is how the regression tests were shown to fail there.
