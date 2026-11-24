#!/usr/bin/env python3
"""Fail-closed upstream for the offline search cache.

Answers Newznab `t=caps` per route (from caps.json) and returns 502 for every
other request, so any search the offline cache does not hold fails rather than
reaching anything real. With an RSS directory argument it also serves the
offline RSS-feed fixture for criteria-less `t=movie` requests. Counts refused requests at GET /refused.
With DISCOVERY=1 (used once to learn Radarr's fallback queries) uncached
searches get an empty feed instead; benchmarks never run in that mode.
"""
import json
import os
import re
import sys
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CAPS = json.load(open(sys.argv[1]))
REFUSED = {"count": 0}
DISCOVERED = {"count": 0}
RSS_DIR = sys.argv[2] if len(sys.argv) > 2 else None
EMPTY = (b'<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" '
         b'xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel>'
         b'<newznab:response offset="0" total="0"/></channel></rss>')
CATS = "".join(f'<category id="{c}" name="Movies"/>' for c in (2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070, 2080))


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        url = urllib.parse.urlsplit(self.path)
        query = dict(urllib.parse.parse_qsl(url.query))
        m = re.fullmatch(r"/(\d+)/api/?", url.path)
        if url.path == "/refused":
            return self.send(200, json.dumps({**REFUSED, "discovered": DISCOVERED["count"]}).encode(), "application/json")
        if m and query.get("t") == "caps" and m[1] in CAPS:
            body = (f'<?xml version="1.0" encoding="UTF-8"?><caps><limits max="100" default="100"/>'
                    f'<searching><search available="yes" supportedParams="q"/>'
                    f'<movie-search available="yes" supportedParams="{",".join(CAPS[m[1]])}"/></searching>'
                    f'<categories>{CATS}</categories></caps>').encode()
            return self.send(200, body, "application/xml")
        criteria = any(query.get(k) for k in ("q", "imdbid", "tmdbid"))
        if m and query.get("t") == "movie" and not criteria and RSS_DIR:
            # Offline RSS feed fixture (first page only); never forwarded anywhere.
            path = os.path.join(RSS_DIR, f"{m[1]}.xml")
            if query.get("offset", "0") == "0" and os.path.exists(path):
                return self.send(200, open(path, "rb").read(), "application/rss+xml")
            return self.send(200, EMPTY, "application/rss+xml")
        if os.environ.get("DISCOVERY") == "1" and m and query.get("t") in ("search", "movie"):
            # Discovery mode only: record-the-query pass; returns an empty feed.
            DISCOVERED["count"] += 1
            return self.send(200, EMPTY, "application/rss+xml")
        REFUSED["count"] += 1
        self.send(502, b'{"error":"offline lab: not cached"}', "application/json")

    def send(self, status, body, ctype):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8081"))), Handler).serve_forever()
