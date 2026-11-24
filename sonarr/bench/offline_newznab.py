#!/usr/bin/env python3
"""Offline Newznab endpoint for benchmark clones. Never contacts anything.

* Exact cached search pages (same canonical key as the production search cache)
  are served verbatim (URLs already replaced by placeholders at build time).
* Other ID/title searches for library series are answered from cached release
  metadata (title, size, publish time, indexer) in the replay index.
* Feed requests (RSS sync) return the newest 100 cached releases per indexer.
* Searches for titles that are not in the replay return an empty result, and
  non-Newznab paths fail with HTTP 503. Nothing is ever fetched from elsewhere.
"""
import email.utils, hashlib, json, os, re, sqlite3, sys, threading, time, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from xml.sax.saxutils import escape

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_replay import clean  # noqa: E402

DB = os.environ.get("REPLAY_DB", "/data/replay.sqlite3")
LOG = os.environ.get("REPLAY_LOG", "/log/requests.jsonl")
PATH = re.compile(r"^/(\d+)/(api/?|download/?)$")
CRITERIA = {"q", "tvdbid", "tvmazeid", "rid", "imdbid", "tmdbid", "traktid"}
# Search parameters each indexer advertised in production (observed queries).
CAPS = {1: "q,season,ep,tvdbid,imdbid,rid,tvmazeid,tmdbid", 3: "q,season,ep,tvdbid,rid,tvmazeid"}
DEFAULT_CAPS = "q,season,ep,tvdbid,imdbid,rid,tvmazeid"
log_lock = threading.Lock()
memo, memo_lock = {}, threading.Lock()


def load():
    """Load the whole replay index once; responses are then rendered from memory."""
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    state = {"pages": dict(con.execute("SELECT key, body FROM pages")),
             "series": {c: t for t, c in con.execute("SELECT tvdbid, clean FROM series")},
             "by_tvdb": {}, "by_name": {}, "feed": {}}
    ref = con.execute("SELECT max(published) FROM items").fetchone()[0]
    for row in con.execute("SELECT id,title,size,published,tvdbid,season,episodes,absolute,daily,indexer,name "
                           "FROM items ORDER BY published DESC, id"):
        row = row[:6] + (json.loads(row[6]) if row[6] else [],) + row[7:]
        if row[4]:
            state["by_tvdb"].setdefault((row[4], row[9]), []).append(row)
        if row[10]:
            state["by_name"].setdefault((row[10], row[9]), []).append(row)
        # FEED=library: RSS returns only releases of library series, so every decision runs the full
        # specification set (the default feed is dominated by series that are not in the library).
        if row[3] <= ref and (os.environ.get("FEED") != "library" or row[4]):
            state["feed"].setdefault(row[9], []).append(row)
    return state


STATE = load()


def search_key(path, pairs):
    query = dict(pairs)
    query.pop("apikey", None)
    canonical = path.rstrip("/") + "?" + urllib.parse.urlencode(sorted(query.items()))
    return hashlib.sha256(canonical.encode()).hexdigest()


def caps(indexer):
    params = CAPS.get(indexer, DEFAULT_CAPS)
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<caps><server title="offline"/><limits max="100" default="100"/>
<searching><search available="yes" supportedParams="q"/>
<tv-search available="yes" supportedParams="{params}"/>
<movie-search available="no"/></searching>
<categories><category id="5000" name="TV"><subcat id="5020" name="Foreign"/><subcat id="5030" name="SD"/>
<subcat id="5040" name="HD"/><subcat id="5045" name="UHD"/><subcat id="5050" name="Other"/><subcat id="5070" name="Anime"/></category></categories>
</caps>""".encode()


def rss(rows):
    items = []
    for ident, title, size, published, tvdb in rows:
        date = email.utils.formatdate(published, usegmt=True)
        url = f"http://offline.invalid/item/{ident}"
        attrs = f'<newznab:attr name="category" value="5040"/><newznab:attr name="size" value="{size}"/>'
        if tvdb:
            attrs += f'<newznab:attr name="tvdbid" value="{tvdb}"/>'
        items.append(f"<item><title>{escape(title)}</title><guid isPermaLink=\"true\">{url}</guid><link>{url}</link>"
                     f"<pubDate>{date}</pubDate><size>{size}</size><enclosure url=\"{url}\" length=\"{size}\" type=\"application/x-nzb\"/>{attrs}</item>")
    return ('<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">'
            "<channel><title>offline</title>" + "".join(items) + "</channel></rss>").encode()


def episode_filter(rows, season, ep):
    out = []
    for r in rows:
        r_season, eps, absolute, daily = r[5], r[6], r[7], r[8]
        if ep and "/" in ep:
            if daily == ep and str(r_season) == season:
                out.append(r)
        elif season is not None and ep:
            if str(r_season) == season and int(ep) in eps:
                out.append(r)
        elif season is not None:
            if str(r_season) == season:
                out.append(r)
        elif ep:
            if absolute == int(ep):
                out.append(r)
        else:
            out.append(r)
    return out


def answer(indexer, q):
    t = q.get("t")
    if t == "caps":
        return 200, "caps", caps(indexer)
    if t not in ("search", "tvsearch"):
        return 503, "unsupported", b"unsupported"
    offset, limit = int(q.get("offset", 0) or 0), int(q.get("limit", 100) or 100)
    if not any(q.get(k, "").strip() for k in CRITERIA):
        rows = STATE["feed"].get(indexer, [])[offset:offset + limit]
        return 200, "feed", rss([r[:5] for r in rows])
    season, ep = q.get("season") or None, q.get("ep") or None
    if q.get("tvdbid", "").isdigit():
        rows = STATE["by_tvdb"].get((int(q["tvdbid"]), indexer), [])
        if q.get("q", "").strip().isdigit() and season is None and ep is None:
            # Anime ID search: q carries the episode number; match absolute or episode numbers.
            n = int(q["q"])
            rows = [r for r in rows if r[7] == n or n in r[6]]
        kind = "synthesized-id" if int(q["tvdbid"]) in STATE["series"].values() else "empty-unknown"
    elif q.get("q", "").strip():
        # Text search: releases whose parsed series name equals the query title.
        text = q["q"].strip()
        m = re.match(r"^(.*?)[ ._]+(\d{1,4})$", text)
        if m and season is None and ep is None:
            text, ep = m.group(1), m.group(2)
        m = re.match(r"^(.*?)[ ._]+s(\d+)(?:e(\d+))?$", text, re.I)
        if m:
            text, season, ep = m.group(1), str(int(m.group(2))), (str(int(m.group(3))) if m.group(3) else None)
        rows = STATE["by_name"].get((clean(text), indexer), [])
        kind = "synthesized-text" if rows else "empty-unknown"
    else:
        rows, kind = [], "empty-unknown"
    rows = episode_filter(rows, season, ep)[offset:offset + limit]
    return 200, kind, rss([r[:5] for r in rows])


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"  # keep-alive, like a real indexer endpoint

    def log_message(self, *a):
        pass

    def do_GET(self):
        start = time.perf_counter()
        parts = urllib.parse.urlsplit(self.path)
        m = PATH.fullmatch(parts.path)
        pairs = urllib.parse.parse_qsl(parts.query, keep_blank_values=True)
        q = {k: v for k, v in pairs if k != "apikey"}
        if parts.path in ("/", "/health"):
            status, kind, body = 200, "health", b"ok"
        elif not m or m[2].rstrip("/") != "api":
            status, kind, body = 503, "not-cached", b"not cached"
        else:
            indexer = int(m[1])
            key = search_key(parts.path, pairs)
            if key in STATE["pages"]:
                status, kind, body = 200, "verbatim", STATE["pages"][key]
            else:
                with memo_lock:
                    hit = memo.get(key)
                if hit is None:
                    hit = answer(indexer, q)
                    with memo_lock:
                        memo[key] = hit
                status, kind, body = hit
        self.send_response(status)
        self.send_header("Content-Type", "application/rss+xml" if status == 200 else "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        with log_lock, open(LOG, "a") as f:
            f.write(json.dumps({"path": parts.path, "query": q, "status": status, "kind": kind,
                                "items": body.count(b"<item>"), "ms": round((time.perf_counter() - start) * 1000, 1),
                                "done": time.time()}) + "\n")


if __name__ == "__main__":
    ThreadingHTTPServer.request_queue_size = 256
    server = ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8080"))), Handler)
    server.daemon_threads = True
    server.serve_forever()
