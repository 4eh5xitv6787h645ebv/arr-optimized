#!/usr/bin/env python3
"""Summarize bench-*.jsonl files: median, min-max and IQR per (label, item, phase).

  summarize.py OUT_DIR LABEL [LABEL...]   -> markdown table on stdout
"""
import json
import statistics
import sys
from pathlib import Path

NAMES = {"2": "Toy Story (1995)", "25": "Inception (2010)", "467": "Dark Phoenix (2019)", "rss": "RSS decisions"}


def q(xs, f):
    xs = sorted(xs)
    k = (len(xs) - 1) * f
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


def main():
    out, labels = Path(sys.argv[1]), sys.argv[2:]
    rows = {}
    for label in labels:
        for line in (out / f"bench-{label}.jsonl").read_text().splitlines():
            r = json.loads(line)
            rows.setdefault((r["item"], r["phase"]), {}).setdefault(label, []).append(r)
    print("| search | phase | " + " | ".join(f"{l} median (min–max, n)" for l in labels) + " |")
    print("|---|---|" + "---|" * len(labels))
    order = [("2", "cold"), ("2", "warm"), ("25", "cold"), ("25", "warm"), ("467", "first_after_start"),
             ("rss", "cold"), ("rss", "warm")]
    for key in order:
        if key not in rows:
            continue
        cells = []
        for label in labels:
            rs = rows[key].get(label, [])
            xs = [r["seconds"] for r in rs]
            if not xs:
                cells.append("–")
                continue
            cells.append(f"{statistics.median(xs):.2f} s ({min(xs):.2f}–{max(xs):.2f}, IQR {q(xs, .75) - q(xs, .25):.2f}, n={len(xs)}; {rs[0]['releases']} rel.)")
        print(f"| {NAMES.get(key[0], key[0])} | {key[1]} | " + " | ".join(cells) + " |")


if __name__ == "__main__":
    main()
