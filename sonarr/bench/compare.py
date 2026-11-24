#!/usr/bin/env python3
"""Compare full release-decision output of two runs (directories of <query>.json).

Every field returned by /api/v3/release is compared, in order, except the ones
derived from the wall clock at request time (age, ageHours, ageMinutes).
Exit status 1 on any difference.
"""
import json, sys
from pathlib import Path

CLOCK = {"age", "ageHours", "ageMinutes"}


def norm(r):
    return {k: v for k, v in r.items() if k not in CLOCK}


a, b = Path(sys.argv[1]), Path(sys.argv[2])
bad = 0
summary = []
for fa in sorted(a.glob("*.json")):
    fb = b / fa.name
    ra, rb = json.loads(fa.read_text()), json.loads(fb.read_text())
    na, nb = [norm(r) for r in ra], [norm(r) for r in rb]
    same = na == nb
    approved = sum(r["approved"] for r in ra)
    rejections = sum(len(r["rejections"]) for r in ra)
    cfs = sum(len(r["customFormats"]) for r in ra)
    summary.append({"query": fa.stem, "releases": len(ra), "approved": approved, "rejection_reasons": rejections,
                    "custom_format_matches": cfs, "identical": same})
    if not same:
        bad += 1
        for i, (x, y) in enumerate(zip(na, nb)):
            if x != y:
                diff = {k: (x.get(k), y.get(k)) for k in set(x) | set(y) if x.get(k) != y.get(k)}
                print(f"DIFF {fa.stem} #{i} {x.get('title')}: {json.dumps(diff)[:600]}")
                break
        if len(na) != len(nb):
            print(f"DIFF {fa.stem}: {len(na)} vs {len(nb)} releases")
print(json.dumps({"queries": len(summary), "identical": len(summary) - bad, "different": bad,
                  "releases": sum(s["releases"] for s in summary)}))
for s in summary:
    print(json.dumps(s))
sys.exit(1 if bad else 0)
