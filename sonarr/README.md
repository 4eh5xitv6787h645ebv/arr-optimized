# sonarr-next

A small set of performance patches for [Sonarr](https://github.com/Sonarr/Sonarr) v5, built as a
drop-in `Sonarr.Core.dll` on top of the stock linuxserver image. Release decisions are unchanged:
the same releases are accepted or rejected for the same reasons, with the same custom formats and
scores.

| | |
|---|---|
| Upstream | `v5.0.0.3041` (`03f822c78293981e6e1459fbce59d11476b2107f`, develop) |
| Base image | `lscr.io/linuxserver/sonarr@sha256:c8c4a3dc…` (`5.0.0.3041-ls212`, the `develop` tag) |
| Image tag | `sonarr-optimized:5.0.0.3041-next` |
| SDK | .NET SDK 10.0.404 (upstream `global.json`) |

## Patches

Applied in order by `build.sh` (`git apply`). Each is independent of later ones.

1. `0001` **Custom-format match cache.** Caches *which* custom formats match a release or file
   input. The key holds every value the supported condition types read, and the definition
   snapshot is compared on every call. Up to 50,000 entries, 6 h sliding expiry refreshed on every
   hit, least-recently-used batch eviction at the cap, lock-free hits. Scores, profiles and all
   decisions stay live. Unknown condition types bypass the cache.
2. `0002` **Cache `Database.DatabaseType`.** Every repository query asked for the database type,
   which opened (and closed) a whole extra SQLite connection per query. The type never changes, so
   it is now read once.
3. `0003` **Per-batch read memo (`DecisionScope`).** Within one `GetSearchDecision`/`GetRssDecision`
   call, rows that every release re-reads (indexer definition, release profiles, episode history,
   episodes in a file) are read once. Nothing outside a batch is cached. A memoized lookup is
   reloaded after any write to the table it read (every `BasicRepository` write and the raw-SQL
   housekeepers report the table), so rows written during a batch, e.g. by Completed Download
   Handling, are seen exactly as on stock. `null` results and exceptions are never memoized. Also
   caches the provider-settings type lookup that scanned every exported type of `Sonarr.Core` per row.
4. `0004` **Blocklist title match in memory.** Within a batch, a series' blocklist rows are read
   once and SQLite's `LIKE '%' || title || '%'` is evaluated in memory with SQLite's exact rules
   (ASCII-only case folding, U+FFFE/U+FFFF equal to U+FFFD, `%`/`_` wildcards, no escape).
   PostgreSQL, NUL, surrogates, over-long titles and anything outside a batch use the original
   query.

## Build

```sh
./build.sh            # clone tag, verify commit, apply patches, build artifacts/Sonarr.Core.dll
./build.sh --image    # ...and docker build -t sonarr-optimized:5.0.0.3041-next .
```

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
as on stock), the database-type cache, and 2.3 million comparisons per provider of the LIKE
emulation against real SQLite (U+FFFD/U+FFFE/U+FFFF, non-ASCII case variants, Turkish and Greek
forms, fullwidth letters, combining marks, `%`/`_`/backslash inside titles, titles up to 20,000
characters and the 50,000-byte pattern limit).

`tests/CoreTest` holds NUnit fixtures for upstream's own test project (real repositories and test
database): a deleted indexer during a batch, history, blocklist and episode-file rows written
during a batch, and in-batch blocklist title matching against the stock SQL query. See `tests/CoreTest/README.md`.

## Benchmark and equivalence harness

`bench/` holds the offline harness (no private data). Inputs are a `sonarr.db` snapshot and a
search-cache database, which stay outside this repository.

- `build_replay.py`: builds an offline replay index from the search cache. It keeps release titles,
  sizes, publish times and indexer ids, plus the verbatim cached pages with every URL replaced.
- `offline_newznab.py`: Newznab endpoint serving that index. It never fetches anything.
- `make_clone.py`: prepares an isolated config. Indexers point at the replay with dummy keys, other
  indexers are disabled, scheduled tasks are parked, and it refuses to run if download clients,
  notifications or import lists exist.
- `lab.py`: `up`/`down` (an `--internal` Docker network, no egress), `search` (full decision
  output per query), `bench` (cold and warm search timing), `rss`, `rss-decisions`, `profile`
  (dotnet-trace).
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
- **`0002`.** One getter. Low risk.
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
- Version-pinned: the DLL must only ever be used with the base image of the same Sonarr version.
