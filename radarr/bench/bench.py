#!/usr/bin/env python3
"""Interactive-search benchmark (single searches only; no bulk/collection replay).

  bench.py LABEL IMAGE [--cycles N] [--dll PATH]

Each cycle starts a fresh sanitized clone (fresh process, empty caches), then:
  1. warm-up search of a third movie (recorded as first_after_start; JIT/DB caches warm)
  2. cold searches: first search of Toy Story and of Inception in this process
  3. warm searches: the same two movies again
  4. RSS decisions (GET /api/v3/release) twice
Searches are spaced GAP seconds apart so no search waits on the previous search's
per-indexer rate-limit window. Results append to <private>/out/bench-LABEL.jsonl.
Run with nothing else of yours running; clones are capped at 4 CPUs.
"""
import argparse
import json
import time

import lab

GAP = 3.0
WARMUP, MOVIES = "467", ["2", "25"]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("label")
    p.add_argument("image")
    p.add_argument("--cycles", type=int, default=6)
    p.add_argument("--dll")
    a = p.parse_args()
    out = lab.PRIVATE / "out" / f"bench-{a.label}.jsonl"
    name = "bench" + "".join(ch for ch in a.label if ch.isalnum())
    for cycle in range(a.cycles):
        lab.clone(name, a.image, a.dll)
        lab.safety(name)
        time.sleep(10)  # let startup housekeeping finish

        def timed(item, phase):
            time.sleep(GAP)
            r = lab.search(name, item)
            assert r["status"] == 200, r
            row = {"label": a.label, "cycle": cycle, "item": item, "phase": phase,
                   "seconds": r["seconds"], "releases": r["count"]}
            with out.open("a") as f:
                f.write(json.dumps(row) + "\n")
            print(json.dumps(row), flush=True)

        timed(WARMUP, "first_after_start")
        for m in MOVIES:
            timed(m, "cold")
        for m in MOVIES:
            timed(m, "warm")
        timed("rss", "cold")
        timed("rss", "warm")
        lab.rm(f"radarr-opt-{name}")


if __name__ == "__main__":
    main()
