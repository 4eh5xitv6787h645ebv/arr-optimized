# arr-optimized

Drop-in [Sonarr](https://github.com/Sonarr/Sonarr) and [Radarr](https://github.com/Radarr/Radarr)
images that make release decisions (interactive search, automatic search, RSS sync) faster
**without changing a single decision**. The same releases are accepted or rejected for the same
reasons, with the same custom formats and scores, as stock.

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

Both apps re-run the same work for every release in a search: custom-format matching (hundreds of
regexes per release), repeated database reads of the same rows, and (Sonarr) an extra SQLite
connection per query. The patches remove that repetition and keep every result live:

- **Custom-format match cache.** Remembers *which* custom formats match a given release input.
  The key holds every value the supported condition types read, definitions are checked on every
  call (edits apply immediately), and unknown condition types bypass the cache. Scores and all
  accept/reject decisions are still computed live. Up to 50,000 entries with a 6-hour expiry that
  resets on every use. Typical memory use is about 20–25 MB.
- **Per-search read memo.** Rows that every release re-reads (indexer, and in Sonarr also release
  profiles, history, blocklist and episode rows) are read once per decision batch and re-read
  after any write to that table, so downloads that finish mid-search are seen exactly as on stock.
- **Sonarr only:** the database type is read once instead of opening a whole SQLite connection per
  query, and blocklist title checks within a batch use an in-memory copy of SQLite's own `LIKE`
  rules (checked against real SQLite with 2.3 million random comparisons, 0 differences).

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
between requests to the same indexer. That limit is unchanged.

Equivalence: every decision (accepted/rejected, rejection reasons, custom formats, score, order)
was compared field by field with stock: Sonarr 13,702 decisions over 30 searches plus RSS, Radarr
10,318 decisions over 27 searches plus RSS. All identical. Upstream's own test suites pass (Sonarr
2,013, Radarr 1,723; the one Radarr failure also fails on stock), plus the tests in this repository.

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
