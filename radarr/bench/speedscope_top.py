#!/usr/bin/env python3
"""Summarise a dotnet-trace speedscope file for stacks under a root frame.

  speedscope_top.py FILE.speedscope.json ROOT_SUBSTRING [--top N] [--match SUBSTR]

Walks every thread's evented profile. Wall time is attributed only while a frame
whose name contains ROOT_SUBSTRING is on the stack (so idle threads and unrelated
work are excluded). Prints total, then inclusive and exclusive time per frame.
--match limits the printed rows to frames containing SUBSTR.
"""
import collections
import json
import sys


def main():
    path, root = sys.argv[1], sys.argv[2]
    top = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 60
    match = sys.argv[sys.argv.index("--match") + 1] if "--match" in sys.argv else None
    data = json.load(open(path))
    names = [f["name"] for f in data["shared"]["frames"]]
    is_root = [root in n for n in names]
    incl = collections.Counter()
    excl = collections.Counter()
    total = 0.0
    for prof in data["profiles"]:
        if prof.get("type") != "evented":
            continue
        stack = []
        roots = 0
        last = None
        for ev in prof["events"]:
            at = ev["at"]
            if last is not None and roots and stack:
                dt = at - last
                total += dt
                seen = set()
                for f in stack:
                    if f not in seen:
                        incl[f] += dt
                        seen.add(f)
                excl[stack[-1]] += dt
            last = at
            f = ev["frame"]
            if ev["type"] == "O":
                stack.append(f)
                roots += is_root[f]
            else:
                # close the matching frame (speedscope guarantees nesting)
                while stack:
                    g = stack.pop()
                    roots -= is_root[g]
                    if g == f:
                        break
    unit = data["profiles"][0].get("unit", "milliseconds")
    print(f"total under '{root}': {total:.1f} {unit}")

    def show(title, counter):
        print(f"\n== {title}")
        n = 0
        for f, v in counter.most_common():
            if match and match not in names[f]:
                continue
            print(f"{v:10.1f} {100 * v / total:5.1f}%  {names[f][:150]}")
            n += 1
            if n >= top:
                break

    show("inclusive", incl)
    show("exclusive", excl)


if __name__ == "__main__":
    main()
