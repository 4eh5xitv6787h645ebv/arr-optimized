# Offline benchmark and equivalence lab

Everything here runs against **private** inputs that are not part of this repository: a
`radarr.db` snapshot and a Radarr search-cache database (`radarr-search-cache.sqlite3`). Outputs
(clone configs, release JSON, traces) go to a private directory (`RADARR_LAB_PRIVATE`, default
`../../radarr-lab-private`, outside the repository).

## Isolation

- One Docker network created with `--internal`: no LAN or internet egress. `lab.py infra` proves
  it by failing a TCP connect to a public address and to the LAN media host before anything starts.
- Indexers in every clone point at `radarr-opt-cache`, the unmodified search-cache service
  (a Newznab search-cache service; `--cache-service` points at its source) serving an offline copy of the cache DB.
  Its upstream is `stub_upstream.py`, which answers only Newznab `t=caps` (and the offline RSS
  feed fixture) and returns 502 for anything else, so an uncached query fails instead of
  reaching anything real. `lab.py stats` prints the refused count, which was 0 for every run.
- `prepare_clone.py` deletes download clients, notifications, import lists, pending releases and
  queued commands, disables automatic search, scheduled RSS sync and all scheduled tasks, and
  `lab.py safety` re-checks this through the API before any search. Searches cannot grab.
- Containers are named `radarr-opt-*` and publish no ports; Radarr clones are capped at 4 CPUs.

## Building the offline cache

```
build_fixtures.py --radarr-db radarr.db --search-cache radarr-search-cache.sqlite3 \
   --release-json 2=toy-story-release-dump.json --release-json 25=inception-release-dump.json --out FIXTURES
offline_cache.py --snapshot radarr-search-cache.sqlite3 --fixtures FIXTURES --radarr-db radarr.db \
   --cache-service /path/to/search-cache --out PRIVATE/offline-cache
```

The snapshot's own stored responses are reused where Radarr 7's exact query matches them (it
does for all 6). Other movies' responses are synthesized from release metadata the
search cache recorded (`nzbs` table), plus two earlier Radarr API dumps that keep original casing.
Keys are computed with the cache service's own `search_key`. Radarr's text-search fallback tier
was learned once with `lab.py infra --discovery` (the stub returns empty feeds in that mode, and
nothing is forwarded), then pinned with `offline_cache.py --fill-discovered`. All later runs use
the fail-closed stub.

## Running

```
python3 lab.py infra
python3 equivalence.py <stock image> <patched image> 2,25,81,...   # decision diff, cold + warm
python3 lab.py clone inval <patched image> && python3 invalidation.py inval 2
python3 bench.py <label> <image> --cycles 6                         # single interactive searches
python3 summarize.py PRIVATE/out <label> [<label>...]
python3 lab.py clone prof <image> --diag   # profiling only: re-enables the .NET diagnostics socket
dotnet-trace collect --diagnostic-port PRIVATE/clones/prof/tmp/dotnet-diagnostic-*-socket,connect \
   --profile dotnet-common,dotnet-sampled-thread-time --format Speedscope --duration 00:00:00:20
python3 speedscope_top.py trace.speedscope.json "DownloadDecisionMaker+<GetDecisions>" --match Radarr
python3 lab.py down
```

There is deliberately no whole-collection or bulk-run scenario.
