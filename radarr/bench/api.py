#!/usr/bin/env python3
"""Runs inside radarr-opt-client: one API call with an optional JSON body on stdin.

  api.py HOST KEY METHOD PATH [< body.json]   -> prints the response body
"""
import sys
import urllib.request

host, key, method, path = sys.argv[1:5]
data = sys.stdin.buffer.read() if method in ("POST", "PUT") else None
req = urllib.request.Request(f"http://{host}:7878{path}", data=data, method=method,
                             headers={"X-Api-Key": key, "Content-Type": "application/json"})
with urllib.request.urlopen(req, timeout=900) as r:
    sys.stdout.buffer.write(r.read())
