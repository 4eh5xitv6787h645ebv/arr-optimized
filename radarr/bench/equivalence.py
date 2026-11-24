#!/usr/bin/env python3
"""Decision-equivalence check: stock image vs patched image against the same offline cache.

  equivalence.py STOCK_IMAGE PATCHED_IMAGE MOVIE_ID[,MOVIE_ID...] [--label NAME]

For each movie (and the RSS decision set) the stock clone is searched once and the patched
clone twice (cold match cache, then warm). Every output must be identical to stock in every
field except release age (see compare_decisions.py). Results go to <private>/out/eq-*.json and
a summary JSON is printed. Run `lab.py infra` first.
"""
import json
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lab  # noqa: E402


def run(name, image, items, passes):
    lab.clone(name, image)
    lab.safety(name)
    out = {}
    for p in range(passes):
        for m in items:
            time.sleep(2.5)  # stay clear of the per-indexer 2 s rate-limit window
            r = lab.search(name, m, f"/out/eq-{name}-{m}-{p}.json")
            assert r["status"] == 200, (name, m, r)
            out[(m, p)] = r
    lab.rm(f"radarr-opt-{name}")
    return out


def main():
    stock, patched, movies = sys.argv[1:4]
    items = [m for m in movies.split(",") if m] + ["rss"]
    run("eqstock", stock, items, 1)
    run("eqnext", patched, items, 2)
    results = []
    for m in items:
        for p in range(2):
            a = lab.PRIVATE / "out" / f"eq-eqstock-{m}-0.json"
            b = lab.PRIVATE / "out" / f"eq-eqnext-{m}-{p}.json"
            r = subprocess.run([sys.executable, "-I", str(HERE / "compare_decisions.py"), str(a), str(b)],
                               capture_output=True, text=True)
            summary = json.loads(r.stdout.splitlines()[0])
            summary.update(item=m, patched_pass="cold" if p == 0 else "warm")
            if r.returncode:
                summary["diffs"] = r.stdout.splitlines()[1:]
            results.append(summary)
            print(json.dumps(summary), flush=True)
    lab.stats()
    ok = all(r["identical"] for r in results)
    total = {"comparisons": len(results), "identical": sum(r["identical"] for r in results),
             "releases_compared": sum(r["releases"] for r in results), "all_identical": ok}
    print(json.dumps(total))
    (lab.PRIVATE / "out" / "equivalence-summary.json").write_text(json.dumps({"total": total, "results": results}, indent=1))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
