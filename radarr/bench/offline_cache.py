#!/usr/bin/env python3
"""Build an OFFLINE copy of the Radarr search-cache database.

The search cache (a Newznab caching proxy in front of Prowlarr) answers
Newznab searches from its `responses` table and forwards anything else to its
upstream. In the lab its upstream is stub_upstream.py, which answers only
`t=caps` and fails every other request, so a query that is not in this database
fails instead of reaching an indexer.

This script copies the private snapshot database, drops data the lab never needs
(recorded NZB URLs, grab history), pins every existing response so it never
expires, and adds Newznab responses for the selected movies. Those are built
from fixtures (build_fixtures.py), which come from the snapshot's own release
metadata plus earlier Radarr API dumps. Keys are computed with the cache
service's own `search_key`, using the exact parameters Radarr 7 sends
(t, cat, extended, offset, limit, imdbid/tmdbid).

  offline_cache.py --snapshot S.sqlite3 --fixtures DIR --radarr-db radarr.db \
      --cache-service DIR --out DIR [--fill-discovered]

--fill-discovered adds an empty (but valid) RSS page for every query the cache
recorded in `search_queries` without a stored response (e.g. Radarr's text-search
fallback tier for indexers that returned nothing). Run a discovery search pass
first, then rerun with this flag.
"""
import argparse
import datetime
import json
import shutil
import sqlite3
import sys
import zlib
from pathlib import Path
from xml.sax.saxutils import escape, quoteattr

NEVER = 4102444800.0  # 2100-01-01
PAGE = 100
HOST = "http://radarr-opt-cache:8080"


def rss(route, items, offset, total):
    out = ['<?xml version="1.0" encoding="UTF-8"?>',
           '<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" '
           'xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">',
           '<channel><title>radarr-opt offline</title><description>offline fixture</description>',
           f'<newznab:response offset="{offset}" total="{total}"/>']
    for it in items:
        link = f"{HOST}/{route}/download?id={it['guid']}"
        pub = datetime.datetime.fromtimestamp(it["published"], datetime.timezone.utc)
        attrs = [("category", it["category"]), ("size", it["size"])]
        if it.get("imdb"):
            attrs.append(("imdb", it["imdb"]))
        for k in ("grabs", "files"):
            if it.get(k) is not None:
                attrs.append((k, it[k]))
        out.append("<item>")
        out.append(f"<title>{escape(it['title'])}</title>")
        out.append(f'<guid isPermaLink="true">{escape(link)}</guid>')
        out.append(f"<link>{escape(link)}</link>")
        out.append(f"<comments>{escape(link)}#comments</comments>")
        out.append(f"<pubDate>{pub.strftime('%a, %d %b %Y %H:%M:%S +0000')}</pubDate>")
        out.append(f"<category>{it['category']}</category>")
        out.append(f'<enclosure url={quoteattr(link)} length="{it["size"]}" type="application/x-nzb"/>')
        for k, v in attrs:
            out.append(f'<newznab:attr name="{k}" value={quoteattr(str(v))}/>')
        out.append("</item>")
    out.append("</channel></rss>")
    return "\n".join(out).encode()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--snapshot", required=True)
    p.add_argument("--fixtures", required=True)
    p.add_argument("--radarr-db", required=True)
    p.add_argument("--cache-service", required=True, help="directory containing cache_service.py")
    p.add_argument("--out", required=True)
    p.add_argument("--fill-discovered", action="store_true")
    a = p.parse_args()
    sys.path.insert(0, str(Path(a.cache_service).resolve()))
    from cache_service import search_key  # the service's own key function

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    out.chmod(0o700)
    target = out / "cache.sqlite3"
    if not a.fill_discovered:
        for suffix in ("", "-wal", "-shm"):
            Path(str(target) + suffix).unlink(missing_ok=True)
        src = sqlite3.connect(f"file:{a.snapshot}?mode=ro", uri=True)
        dst = sqlite3.connect(target)
        src.backup(dst)
        src.close()
        # Never needed offline; may contain indexer URLs.
        for table in ("nzbs", "grabbed_nzbs", "failed_articles", "guard_state", "search_queries",
                      "refreshed_queries", "exhausted_movies", "counters", "settings"):
            dst.execute(f"DELETE FROM {table}")
        dst.execute("UPDATE responses SET expires=?", (NEVER,))
        dst.commit()
        dst.execute("VACUUM")
    db = sqlite3.connect(target)

    if a.fill_discovered:
        # Pin pages stored during a discovery pass, then fill anything still missing.
        db.execute("UPDATE responses SET expires=?", (NEVER,))
        rows = db.execute("SELECT q.key, q.query FROM search_queries q "
                          "LEFT JOIN responses r ON r.key=q.key WHERE r.key IS NULL").fetchall()
        for key, query in rows:
            route = json.loads(query).get("_indexer")
            body = rss(route, [], 0, 0)
            db.execute("INSERT OR IGNORE INTO responses VALUES (?,?,?,?,?,?,?)",
                       (key, route, 0, NEVER, 0, json.dumps({"Content-Type": "application/rss+xml"}),
                        zlib.compress(body)))
        db.commit()
        print(f"filled {len(rows)} discovered queries with empty pages")
        return

    rdb = sqlite3.connect(f"file:{a.radarr_db}?mode=ro", uri=True)
    cats = {iid: json.loads(s)["categories"] for iid, s in rdb.execute("SELECT Id, Settings FROM Indexers")}
    manifest = json.loads((Path(a.fixtures) / "manifest.json").read_text())
    caps = {}
    real = synth = 0
    for movie in manifest["movies"]:
        for iid_s, route in manifest["routes"].items():
            iid = int(iid_s)
            items = json.loads((Path(a.fixtures) / "items" / str(movie["id"]) / f"{iid}.json").read_text())
            # Route 2 advertised tmdbid+imdbid in the recorded live queries; the others imdbid only.
            params = ["q", "imdbid", "tmdbid"] if route == 2 else ["q", "imdbid"]
            caps[route] = params
            ids = []
            if "tmdbid" in params and movie["tmdb"]:
                ids.append(("tmdbid", str(movie["tmdb"])))
            if movie["imdb"]:
                ids.append(("imdbid", movie["imdb"][2:]))
            pages = [items[i:i + PAGE] for i in range(0, len(items), PAGE)] or [[]]
            if len(pages[-1]) == PAGE:
                pages.append([])
            for n, page in enumerate(pages):
                pairs = [("t", "movie"), ("cat", ",".join(map(str, dict.fromkeys(cats[iid])))),
                         ("extended", "1"), ("apikey", "x"), ("offset", str(n * PAGE)), ("limit", str(PAGE))] + ids
                key, _ = search_key(f"/{route}/api", pairs)
                if db.execute("SELECT 1 FROM responses WHERE key=?", (key,)).fetchone():
                    real += 1
                    continue
                db.execute("INSERT INTO responses VALUES (?,?,?,?,?,?,?)",
                           (key, route, 0, NEVER, 0, json.dumps({"Content-Type": "application/rss+xml"}),
                            zlib.compress(rss(route, page, n * PAGE, len(items)))))
                synth += 1
    db.commit()
    # Offline RSS feeds (most recent recorded releases per indexer) for the RSS-sync benchmark.
    # Served by the stub; RSS requests carry no search criteria, so the cache never stores them.
    rss_dir = out / "rss"
    rss_dir.mkdir(exist_ok=True)
    for iid_s, route in manifest["routes"].items():
        f = Path(a.fixtures) / "rss" / f"{iid_s}.json"
        items = json.loads(f.read_text()) if f.exists() else []
        (rss_dir / f"{route}.xml").write_bytes(rss(route, items[:PAGE], 0, len(items[:PAGE])))
    (out / "caps.json").write_text(json.dumps({str(k): v for k, v in caps.items()}))
    settings = {"database": "/data/cache.sqlite3", "upstream_url": "http://radarr-opt-stub:8081",
                "api_key": "offline-lab-key", "admin_token": "offline-lab-admin",
                "release_guard_enabled": False, "max_cache_bytes": 1 << 34}
    (out / "settings.json").write_text(json.dumps(settings, indent=1))
    print(f"responses: {real} real snapshot pages reused, {synth} synthesized pages; "
          f"{len(manifest['movies'])} movies")


if __name__ == "__main__":
    main()
