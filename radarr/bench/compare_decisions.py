#!/usr/bin/env python3
"""Diff two Radarr /api/v3/release outputs for decision equivalence.

  compare_decisions.py A.json B.json        exit 0 if identical

Compares every field of every release, in returned (prioritized) order:
accepted/rejected, rejection reasons, matched custom formats and scores,
quality, languages, mapping, weights. Only clock-dependent fields
(age, ageHours, ageMinutes) are ignored. Prints a summary line, plus the
first differences if any.
"""
import json
import sys

VOLATILE = {"age", "ageHours", "ageMinutes"}


def norm(r):
    return {k: v for k, v in r.items() if k not in VOLATILE}


def main():
    a = json.load(open(sys.argv[1]))
    b = json.load(open(sys.argv[2]))
    diffs = []
    if len(a) != len(b):
        diffs.append(f"count {len(a)} != {len(b)}")
    for i, (x, y) in enumerate(zip(a, b)):
        x, y = norm(x), norm(y)
        if x != y:
            keys = sorted(k for k in set(x) | set(y) if x.get(k) != y.get(k))
            diffs.append(f"#{i} {x.get('title')!r}: fields {keys}")
    summary = {
        "releases": len(a),
        "approved": sum(r["approved"] for r in a),
        "rejected": sum(bool(r["rejections"]) for r in a),
        "with_custom_formats": sum(bool(r.get("customFormats")) for r in a),
        "identical": not diffs,
    }
    print(json.dumps(summary))
    for d in diffs[:10]:
        print("  DIFF", d)
    sys.exit(0 if not diffs else 1)


if __name__ == "__main__":
    main()
