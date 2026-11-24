#!/usr/bin/env python3
"""Runs inside radarr-opt-client (on the --internal lab network). One timed API call.

  req.py HOST KEY PATH [OUT.json]   -> prints {"seconds":..,"status":..,"count":..}
"""
import json
import sys
import time
import urllib.request

host, key, path = sys.argv[1:4]
out = sys.argv[4] if len(sys.argv) > 4 else None
req = urllib.request.Request(f"http://{host}:7878{path}", headers={"X-Api-Key": key})
t = time.perf_counter()
try:
    with urllib.request.urlopen(req, timeout=900) as r:
        body = r.read()
        status = r.status
except urllib.error.HTTPError as e:
    body, status = e.read(), e.code
except (urllib.error.URLError, OSError):
    body, status = b"", 0
dt = time.perf_counter() - t
count = None
try:
    data = json.loads(body)
    count = len(data) if isinstance(data, list) else None
except ValueError:
    data = None
if out:
    with open(out, "wb") as f:
        f.write(body)
print(json.dumps({"seconds": round(dt, 4), "status": status, "count": count}))
