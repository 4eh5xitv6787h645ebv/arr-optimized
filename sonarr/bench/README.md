# Benchmark and equivalence harness

Everything runs on an `--internal` Docker network (`sonarr-opt-net`): no LAN or internet egress.
Containers are named `sonarr-opt-*` and reached by container IP, so no host ports are published.
Sonarr clones are capped at 4 CPUs.

```sh
LAB=/private/lab            # outside the repository; mode 0700
python3 -I build_replay.py --cache /private/sonarr-search-cache.sqlite3 --sonarr /private/sonarr.db --out $LAB/replay.sqlite3
python3 -I make_clone.py --db /private/sonarr.db --out $LAB/template --api-key 0123456789abcdef0123456789abcdef
python3 -I lab.py up --lab $LAB                      # network + offline Newznab replay
python3 -I lab.py search --lab $LAB --image IMAGE --name sonarr-opt-a --out $LAB/eq/a --query e123 --query s45:2
python3 -I compare.py $LAB/eq/stock $LAB/eq/next     # field-by-field decision diff
python3 -I lab.py bench --lab $LAB --image IMAGE --label next --target e123 --warmup e456 --reps 5 --warm 5 --out $LAB/bench
python3 -I lab.py up --lab $LAB --feed library       # RSS feed of library releases only
python3 -I lab.py rss-decisions --lab $LAB --image IMAGE --name sonarr-opt-r --label next --out $LAB/eq/rss
python3 -I lab.py rss --lab $LAB --image IMAGE --label next --reps 5 --warm 3 --warmup e456 --out $LAB/bench
python3 -I summarize.py $LAB/bench/*.jsonl
python3 -I lab.py down
```

Queries are `e<episodeId>` (interactive episode search) or `s<seriesId>:<season>` (season search).

## What the replay serves

- Search pages that are in the cache verbatim (same canonical key as the production search cache)
  are served as stored, with every URL replaced by an `offline.invalid` placeholder.
- Other ID or title searches for library series are rebuilt from cached release metadata (title,
  size, publish time, indexer). Releases are mapped to series and episodes by a title parser
  (`build_replay.py`). Cached titles are case-folded, which the real indexer responses are not.
- Unknown queries return an empty result, so indexers never enter back-off and runs stay
  deterministic. Non-Newznab paths return 503. Nothing is ever fetched from elsewhere.
- RSS returns the newest 100 cached releases per indexer (`--feed library`: library series only).

## Metrics

- `wall_s`: API latency of `/api/v3/release`. Sonarr rate-limits each indexer to one request per
  2 s, so wall time is mostly indexer waits when a search sends several requests to one indexer.
- `cpu_s`: container CPU seconds used during the search (cgroup `cpu.stat`). This is Sonarr's own
  work: parsing, decisions and database access.
- `tail_s`: time from the last indexer response to the API response, the processing on the
  critical path.
