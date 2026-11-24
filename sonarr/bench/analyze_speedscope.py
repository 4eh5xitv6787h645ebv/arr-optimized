#!/usr/bin/env python3
"""Summarize a dotnet-trace speedscope file restricted to stacks under a root frame.

  analyze_speedscope.py trace.speedscope.json --root DownloadDecisionMaker.GetDecisions [--top 60]
Prints inclusive/exclusive wall-clock ms for frames below the root, plus the
direct-children breakdown of the root, so idle threads and indexer waits
outside the decision path do not dilute the profile.
"""
import argparse, collections, json, re

ap = argparse.ArgumentParser()
ap.add_argument("file")
ap.add_argument("--root", default="DownloadDecisionMaker.GetDecisions")
ap.add_argument("--top", type=int, default=60)
ap.add_argument("--children-of", action="append", default=[])
a = ap.parse_args()
doc = json.load(open(a.file))
names = [f["name"] for f in doc["shared"]["frames"]]
root_re = re.compile(a.root)
roots = {i for i, n in enumerate(names) if root_re.search(n)}
child_res = [re.compile(x) for x in a.children_of]
inc, exc = collections.Counter(), collections.Counter()
kids = collections.defaultdict(collections.Counter)
total = 0.0
for prof in doc["profiles"]:
    stack, last = [], prof.get("startValue", 0)
    for ev in prof["events"]:
        at = ev["at"]
        dt = at - last
        last = at
        if dt > 0 and stack:
            pos = next((k for k, f in enumerate(stack) if f in roots), None)
            if pos is not None:
                total += dt
                for f in set(stack[pos:]):
                    inc[f] += dt
                exc[stack[-1]] += dt
                for k in range(pos, len(stack) - 1):
                    if k == pos or any(r.search(names[stack[k]]) for r in child_res):
                        kids[stack[k]][stack[k + 1]] += dt
        if ev["type"] == "O":
            stack.append(ev["frame"])
        elif stack:
            stack.pop()


def short(n, width=110):
    n = re.sub(r"\(.*\)$", "", n)
    return n if len(n) <= width else "..." + n[-width:]


print(f"total ms under root: {total:.0f}")
print("\n== top inclusive ==")
for f, v in inc.most_common(a.top):
    print(f"{v:9.0f} {100 * v / total:5.1f}%  excl {exc[f]:8.0f}  {short(names[f])}")
print("\n== top exclusive ==")
for f, v in exc.most_common(25):
    print(f"{v:9.0f} {100 * v / total:5.1f}%  {short(names[f])}")
for parent, c in kids.items():
    if sum(c.values()) < total * 0.02:
        continue
    print(f"\n== children of {short(names[parent], 90)} ({sum(c.values()):.0f} ms) ==")
    for f, v in c.most_common(15):
        print(f"{v:9.0f} {100 * v / total:5.1f}%  {short(names[f])}")
