# Upstream-test fixtures

NUnit fixtures that run inside upstream's `NzbDrone.Core.Test` project (real SQLite test database,
real repositories). They are the regression tests for the review findings on patch 0003
(`DecisionScopeReviewFixture.cs`) and the re-review finding on patch 0004
(`BlocklistLikeReviewFixture.cs`: U+FFFE/U+FFFF/U+FFFD in blocklist titles).

```sh
cp -r tests/CoreTest/SonarrNext <tree>/src/NzbDrone.Core.Test/   # <tree>: build.sh's build tree
cd <tree>
dotnet build src/NzbDrone.Core.Test/Sonarr.Core.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
dotnet test _tests/net10.0/Sonarr.Core.Test.dll --filter "FullyQualifiedName~SonarrNext"
```

To check that the behavioural tests encode stock behaviour, copy them into an unpatched tree
together with `stock-stub/StockDecisionScopeStub.cs` (a `DecisionScope` that calls every loader
directly). On stock, only the three memo-mechanics tests fail (stock does not memoize):
`existing_indexer_is_read_once_per_batch_by_find_and_get`, `memo_never_stores_null` and
`unchanged_rows_are_read_once_per_batch`.

## Fixtures for patches 0005–0012

Each folder below mirrors upstream's `src/` folder, so it is copied over the build tree as is.

| Folder | Patch | Fixtures |
|---|---|---|
| `RateLimit` | 0005 | `IndexerTests/RateLimitTests/*` (options parsing, the cache-hit refund under threads and a fixed clock, `HttpIndexerBase` wiring, real-HTTP timing of searches, grabs (also several grabs from one host started at once) and redirects) and `NzbDrone.Host.Test/IndexerRateLimitContainerFixture.cs` (container wiring) |
| `Perf` | 0006 | `PerfNext/*`: the history total equals stock's for every filter and with orphaned rows; series statistics equal stock's; `HistoryPagedCountPostgresFixture` (PostgreSQL only) checks that a 300,000-row history page still loads on PostgreSQL |
| `FFprobe` | 0007 | `MediaFiles/MediaInfo/*` and the four small sample files in `Files/Media` (`make-media.sh` regenerates them). Uses the ffprobe upstream's test project already ships; the timeout tests also need `/bin/sh`, `mkfifo` and `/proc`. The symlink and timeout tests are skipped on Windows |
| `Imports` | 0008 | `ImportFixes/*`: a throwing import is retried, zero mapped episodes is not "imported" |
| `Scheduler` | 0009 | `JobTests/SchedulerFixture.cs`; `SchedulerRestartMeasurement.cs` is `[Explicit]` (measurement only) |
| `Extras` | 0010 | `Extras/Others/*`, `Housekeeping/*` |
| `Optimize` | 0011 | `OptimizationTests/*` (130 cases): defaults equal the constants of 0001–0007 and stock; every bound; environment > saved > default for every value; saved values applied through the start-up event and on save without a restart; custom-format matches equal the stock evaluation at the smallest, default and largest cache values and while they change; an ffprobe staleness oracle at many settings; ffprobe time limit 0 gives the stock calls and the same media info (real ffprobe); live request and grab timing after saving (real HTTP). `NzbDrone.Api.Test/v3/Config/OptimizeConfigControllerFixture.cs`: GET, PUT round trip, partial PUT, field errors, locked fields |
| `Polish` | 0012 and earlier patches | `MediaFiles/MediaInfo/VideoFileInfoReaderReplacedFileFixture.cs`, `MediaInfoProbeCacheInvalidationFixture.cs` and `VideoFileInfoReaderProbeOverlapFixture.cs` (0012; the last one probes a FIFO so the file is replaced while ffprobe runs, which needs `mkfifo` and is ignored on Windows; uses `FFprobe`'s sample media, so copy `FFprobe` first), `NzbDrone.Host.Test/MediaInfoCacheContainerFixture.cs` (0012 container wiring), `JobTests/SchedulerTimerLifecycleFixture.cs` (0009), `IndexerTests/RateLimitTests/GrabGateHostsFixture.cs` (0005, grabs to two hosts), `PerfNext/HistoryPagedCountQueryFixture.cs` (0006, which count query runs on SQLite and PostgreSQL, without a PostgreSQL server) |

```sh
for g in RateLimit Perf FFprobe Imports Scheduler Extras Optimize Polish; do cp -r tests/CoreTest/$g/NzbDrone.* <tree>/src/; done
cd <tree>
dotnet build src/NzbDrone.Core.Test/Sonarr.Core.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
dotnet test _tests/net10.0/Sonarr.Core.Test.dll --filter "FullyQualifiedName~RateLimitTests|FullyQualifiedName~PerfNext|FullyQualifiedName~MediaFiles.MediaInfo|FullyQualifiedName~ImportFixes|FullyQualifiedName~JobTests|FullyQualifiedName~OtherExtraFileRenamer|FullyQualifiedName~DuplicateExtraFiles|FullyQualifiedName~OptimizationTests"
dotnet build src/NzbDrone.Api.Test/Sonarr.Api.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
dotnet test _tests/net10.0/Sonarr.Api.Test.dll
```

`[Category("PerfNextBenchmark")]` tests are `[Explicit]` timings, not checks. To run the database
fixtures against PostgreSQL, set `Sonarr__Postgres__Host`, `__Port`, `__User` and `__Password`.
On PostgreSQL the language-filtered history cases are skipped, because stock's language filter SQL
does not run there (the same on stock).

The container fixtures (`IndexerRateLimitContainerFixture`, `MediaInfoCacheContainerFixture`) need the
Host test project and `Sonarr.Mono.dll` next to it:

```sh
dotnet build src/NzbDrone.Mono/Sonarr.Mono.csproj -c Release -p:EnableAnalyzers=false
dotnet build src/NzbDrone.Host.Test/Sonarr.Host.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
cp _output/net10.0/Sonarr.Mono.dll _tests/net10.0/
dotnet test _tests/net10.0/Sonarr.Host.Test.dll
```

Some fixtures only compile on a patched tree, because they use the new types
(`IndexerRateLimitOptions`, `IndexerCacheHitRateLimiter`, `MediaInfoProbeCache`,
`NzbDrone.Core.Optimization`). The others
compile on stock too, which is how the regression tests were shown to fail there.
