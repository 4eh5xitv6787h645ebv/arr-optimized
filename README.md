# arr-optimized

Drop-in [Sonarr](https://github.com/Sonarr/Sonarr) and [Radarr](https://github.com/Radarr/Radarr)
images that make release decisions (interactive search, automatic search, RSS sync) faster
**without changing a single decision**. The same releases are accepted or rejected for the same
reasons, with the same custom formats and scores, as stock.

They also carry a few fixes for known upstream bugs outside the decision path (stuck imports, hung
ffprobe runs, duplicate extra-file rows, a scheduler that can stop after an in-app restart, and on
Radarr, database restore and housekeeping safety). A fix changes behaviour only in the failure case
it fixes; everything else works as on stock. One optional setting lowers the wait between indexer
requests. It is off unless you set it. See [What changes](#what-changes).

Each image is the stock [linuxserver.io](https://www.linuxserver.io/) image for one upstream
release, pinned by digest, with exactly one file replaced: the app's `*.Core.dll`, built from
upstream source plus the patches in this repository. Everything else (s6 init, `PUID`/`PGID`,
`/config`, ports, mods) is the linuxserver image you already know.

| Image | Upstream | Base |
|---|---|---|
| `ghcr.io/4eh5xitv6787h645ebv/sonarr-optimized:5.0.0.3041` | Sonarr v5 (develop) `5.0.0.3041` | `lscr.io/linuxserver/sonarr:5.0.0.3041-ls212` |
| `ghcr.io/4eh5xitv6787h645ebv/radarr-optimized:7.0.3.10918` | Radarr `7.0.3.10918` | `lscr.io/linuxserver/radarr:7.0.3.10918-ls328` |

`latest` points at the newest build of each. Both images are `linux/amd64` and `linux/arm64`.

## Using it

Swap the image in your existing container. Keep every volume, port, environment variable and
network the same.

```yaml
services:
  sonarr:
    image: ghcr.io/4eh5xitv6787h645ebv/sonarr-optimized:5.0.0.3041   # was lscr.io/linuxserver/sonarr
  radarr:
    image: ghcr.io/4eh5xitv6787h645ebv/radarr-optimized:7.0.3.10918  # was lscr.io/linuxserver/radarr
```

**Back up `/config` first** (stop the app, copy the folder). Moving to a newer Sonarr or Radarr
version migrates its database, and it cannot be downgraded afterwards without that backup. In
particular, Radarr 6 → 7 is a one-way database upgrade. The Sonarr image is the v5 (develop) line,
so it suits setups that already run Sonarr v5 or want to.

To go back to stock, switch to the linuxserver image of the **same version**. The database format
is identical, since the patches don't touch it.

## What changes

### Faster release decisions (identical decisions)

Both apps re-run the same work for every release in a search: custom-format matching (hundreds of
regexes per release) and repeated database reads of the same rows. The patches remove that
repetition and keep every result live:

- **Custom-format match cache.** Remembers *which* custom formats match a given release input.
  The key holds every value the supported condition types read, definitions are checked on every
  call (edits apply immediately), and unknown condition types bypass the cache. Scores and all
  accept/reject decisions are still computed live. Up to 50,000 entries with a 6-hour expiry that
  resets on every use. Typical memory use is about 20–25 MB.
- **Per-search read memo.** Rows that every release re-reads (indexer, and in Sonarr also release
  profiles, history, blocklist and episode rows) are read once per decision batch and re-read
  after any write to that table, so downloads that finish mid-search are seen exactly as on stock.
- **Sonarr only:** blocklist title checks within a batch use an in-memory copy of SQLite's own
  `LIKE` rules (checked against real SQLite with 2.3 million random comparisons, 0 differences).

### Other speed-ups (same results as stock)

- **Statistics.** Series and movie statistics match each season or movie to its file totals with a
  lookup instead of a scan of the whole list. Same numbers, about 3–6× faster on large libraries.
- **Sonarr history page total.** On SQLite, the total of an unfiltered history page is counted
  without joining the series and episode tables (22 ms → 2.6 ms at 50,000 rows). It is exactly the
  stock total, also while deleted episodes still have history rows. Filtered pages and PostgreSQL
  use the stock query.
- **Radarr release profiles and indexer settings.** Release profiles are kept in memory and reloaded
  after every add, edit or delete. The settings type of an indexer is looked up once instead of
  scanning every type in Radarr.Core for every row.
- **ffprobe results.** Media info for a file that has not changed (same size, modification time
  and, for a symlink, the same final target and its modification time) is reused instead of
  running ffprobe again: an import now probes each file once instead of two or three times. At most
  1,000 files and about 32 MB.

### Bug fixes (behaviour changes only in the failure case)

- **Hung ffprobe.** A probe that runs longer than 5 minutes is stopped and the file gets no media
  info, instead of the import (and the command thread) waiting forever. A failed probe of a file
  that has not changed for a minute is not retried for 10 minutes.
- **Imports stuck in "Importing".** If an automatic import throws an error, the download goes back to
  "import pending" and is retried on the next check (about once a minute) instead of staying stuck
  until the app restarts. There is no back-off: a permanent error is retried every minute, quietly
  after the first warning.
- **Sonarr: downloads with no matched episodes.** A download whose episodes were all removed or
  renumbered is no longer marked as imported (and so is no longer removed from the download client
  when nothing was imported).
- **Radarr: lost movie link.** A finished download whose movie link was lost is completed with the
  movie from its import results or its history, instead of failing with an error.
- **Scheduler after an in-app restart.** Restarting from the UI or API, a restore, or a setting that
  needs a restart restarts the app inside the same process. Stock then keeps the old scheduler's
  timer handler too, and after a few restarts scheduled tasks (RSS sync, backups, housekeeping) can
  stop running. Each scheduler now has its own timer that is stopped on shutdown.
- **Duplicate extra files.** Replacing an `-orig` metadata file no longer leaves a duplicate row in
  the database (which later made some extra-file lookups fail). A daily housekeeping task removes
  existing duplicates and keeps the newest row per file.
- **Radarr: restore and housekeeping.** A restore upload must be a Radarr database or a valid
  `config.xml`; anything else (for example `logs.db`) is rejected with an error instead of
  replacing the live database. The previous database is kept as `radarr.db.pre-restore*` until the
  app has started on the restored one. Housekeeping (with its database `VACUUM`) starts only when no
  other command is running, and never holds up other commands while it waits.

### Optional: shorter wait between indexer requests (off by default)

Both apps wait 2 seconds between requests to the same indexer. If your indexers allow more (for
example a local Prowlarr, or a caching proxy in front of your indexers), you can lower that wait:

```yaml
services:
  sonarr:
    image: ghcr.io/4eh5xitv6787h645ebv/sonarr-optimized:5.0.0.3041
    environment:
      - ARR_INDEXER_RATE_LIMIT_MS=500             # wait between requests to one indexer (stock: 2000)
      - ARR_INDEXER_CACHE_HIT_HEADER=X-Cache      # optional: header your caching proxy sets
      # - ARR_INDEXER_CACHE_HIT_VALUE=HIT         # value that means "served from cache" (default HIT)
```

- `ARR_INDEXER_RATE_LIMIT_MS` (0 to 86400000) applies to indexer types that don't set their own
  limit. Types with their own limit (for example BroadcasTheNet or PassThePopcorn) keep it.
- `ARR_INDEXER_CACHE_HIT_HEADER`: a response with this header set to `HIT` (or the value you set)
  was answered by your cache, so the next request to that indexer does not wait for it. Grabs
  still wait as on stock.
- Grabs (downloading the `.torrent`/`.nzb`) from one host stay at least 2 seconds apart, as on stock.
- An invalid value is logged as a warning and ignored. When a setting is active, the app logs it
  once, at the first indexer request or grab.
- Lowering the wait for an indexer that you reach directly can get you rate-limited (HTTP 429) by
  that indexer, and so fewer results. Only lower it for indexers that allow it.

With neither variable set, requests and grabs are timed exactly as on stock.

### What was removed

Sonarr patch `0002` (cache `Database.DatabaseType`) is gone. It had no effect in the running app,
because the database object the app uses already reads the type once at startup.

Details, rebase notes and the per-patch risk are in [sonarr/README.md](sonarr/README.md) and
[radarr/README.md](radarr/README.md).

## Results

Measured in an offline lab against a copy of a real library, with indexer responses replayed
from a cache so stock and patched builds see identical input:

| | Stock | Patched |
|---|---|---|
| Sonarr, decision CPU over 30 searches | 54.9 s | 12.4 s (−77%) |
| Sonarr, full-season search | 11.4 s | 1.6 s |
| Radarr, interactive movie search | 5.5–9.7 s | 4.1–8.7 s (−11 to −25%) |
| Radarr, RSS sync | 0.8 s | 0.6 s |

Small searches gain less wall time than CPU time, because both apps deliberately wait about 2 s
between requests to the same indexer. That limit is unchanged unless you set
`ARR_INDEXER_RATE_LIMIT_MS`.

Equivalence: every decision (accepted/rejected, rejection reasons, custom formats, score, order)
was compared field by field with stock: Sonarr 13,702 decisions over 30 searches plus RSS, Radarr
10,318 decisions over 27 searches plus RSS. All identical. The later patches don't change what the
decision code computes (Radarr's in-memory release profiles are the same rows, reloaded after every
change; upstream's decision-engine and custom-format tests pass unchanged).

Tests: upstream's full core test suites pass on the patched source with every fixture from this
repository added (Sonarr 5,955 passed, 0 failed; Radarr 4,443 passed, 0 failed; stock: 5,725 and
4,173 passed, 0 failed), as do the runners in `*/tests`. Each bug fix has a regression test that
fails on stock and passes with the patch.

## Building it yourself

```sh
cd sonarr && ./build.sh --image      # or: cd radarr && ./build.sh
```

`build.sh` clones the pinned upstream tag, refuses to build unless it resolves to the pinned
commit, applies the patches, builds the core DLL with the .NET SDK named in `upstream.env`, and
builds the image. The GitHub Actions workflow does the same for every push to `main`, runs the
tests, and publishes the images.

## Updating to a new upstream release

Only ever run a DLL with the base image of the version it was built from. To move to a new
release, update `upstream.env` (tag, commit, base image digest) and the `FROM` line, rebuild, and
run the tests. The rebase notes in each app's README list what to check.

## License

GPL-3.0, the same as Sonarr and Radarr. This is an unofficial build, not affiliated with or
supported by the Sonarr, Radarr or linuxserver.io teams. Please don't report problems from these
images upstream. Reproduce on the stock image first.
