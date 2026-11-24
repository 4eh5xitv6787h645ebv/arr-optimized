#!/usr/bin/env python3
"""Summarize lab.py bench/rss output: median and spread per label.

  summarize.py bench/*.jsonl
Search rows: cold = first search of the target in a fresh process after a warm-up search of a
different episode; warm = repeats of the target. RSS rows: cold = first sync, warm = repeats.
"""
import json, statistics, sys
from collections import defaultdict


def stats(xs):
    xs = sorted(xs)
    if not xs:
        return "-"
    q = statistics.quantiles(xs, n=4) if len(xs) >= 4 else [xs[0], statistics.median(xs), xs[-1]]
    return f"{statistics.median(xs):.3f} (IQR {q[0]:.3f}-{q[2]:.3f}, range {xs[0]:.3f}-{xs[-1]:.3f}, n={len(xs)})"


rows = defaultdict(list)
for path in sys.argv[1:]:
    for line in open(path):
        r = json.loads(line)
        rows[(r["label"], r.get("target", "rss"))].append(r)
for (label, target), rs in rows.items():
    print(f"## {label} [{target}]")
    for metric in ("wall_s", "cpu_s", "tail_s"):
        cold = [r["cold"][metric] for r in rs if metric in r["cold"]]
        warm = [w[metric] for r in rs for w in r["warm"] if metric in w]
        if cold:
            print(f"  cold {metric:7s} {stats(cold)}")
            print(f"  warm {metric:7s} {stats(warm)}")
    first = [r["process_first"]["cpu_s"] for r in rs if r.get("process_first")]
    if first:
        print(f"  first search in process cpu_s {stats(first)}")
    print()
