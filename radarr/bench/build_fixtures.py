#!/usr/bin/env python3
"""Build offline Newznab fixtures from a private Radarr snapshot.

Inputs (all private, never committed):
  --radarr-db       radarr.db snapshot (movies, indexers)
  --search-cache    radarr-search-cache.sqlite3 (cached responses + recorded NZB metadata)
  --release-json    optional Radarr /api/v3/release dumps, as MOVIE_ID=path (repeatable).
                    These keep the original title casing, sizes, dates and IMDb ids.
  --out             output directory (keep it private)

Output: <out>/manifest.json and <out>/items/<movie>/<indexer>.json, plus <out>/rss/<indexer>.json.
Items carry only title, size, publish date, IMDb id, category, grabs and files. Every URL in
the output points at the offline server (http://radarr-opt-cache:8080/...), never at an indexer.
"""
import argparse
import collections
import datetime
import email.utils
import hashlib
import json
import re
import sqlite3
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

NEWZNAB = "{http://www.newznab.com/DTD/2010/feeds/attributes/}"

# Default selection: >= 20 movies with mixed years, editions, remux/4K profiles and foreign titles.
DEFAULT_MOVIES = [2, 25, 81, 467, 86, 470, 471, 472, 449, 33, 32, 96, 464, 82, 469, 19, 193, 414,
                  15, 422, 134, 139, 20, 1, 59, 442, 122]


def clean(title):
    title = title.lower().replace("&", "and")
    title = re.sub(r"['’]", "", title)
    title = re.sub(r"[^a-z0-9]+", " ", title)
    return "".join(w for w in title.split() if w not in ("the", "a", "an", "and", "of"))


def category(title):
    t = title.lower()
    if re.search(r"2160p|\buhd\b|\b4k\b", t):
        return 2045
    if re.search(r"1080p|720p|1080i|bluray|blu-ray|remux", t):
        return 2040
    if re.search(r"dvd|xvid|480p|576p|\bsd\b", t):
        return 2030
    return 2000


def fixture_item(title, size, published, imdb, grabs=None, files=None):
    return {"title": title, "size": int(size or 0), "published": float(published),
            "imdb": imdb, "category": category(title), "grabs": grabs, "files": files}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--radarr-db", required=True)
    p.add_argument("--search-cache", required=True)
    p.add_argument("--release-json", action="append", default=[])
    p.add_argument("--movies", default=",".join(map(str, DEFAULT_MOVIES)))
    p.add_argument("--rss-per-indexer", type=int, default=100)
    p.add_argument("--out", required=True)
    a = p.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    out.chmod(0o700)

    db = sqlite3.connect(f"file:{a.radarr_db}?mode=ro", uri=True)
    cache = sqlite3.connect(f"file:{a.search_cache}?mode=ro", uri=True)

    # Radarr indexer id -> search-cache path number (the cache's /<n>/api route).
    route = {}
    for iid, settings in db.execute("SELECT Id, Settings FROM Indexers"):
        m = re.search(r"/(\d+)/?$", json.loads(settings).get("baseUrl", ""))
        if m:
            route[iid] = int(m[1])
    by_route = {v: k for k, v in route.items()}

    wanted = [int(x) for x in a.movies.split(",") if x]
    movies = {}
    keys = collections.defaultdict(list)
    for mid, mmid, title, year, imdb, tmdb in db.execute(
            "SELECT m.Id, mm.Id, mm.Title, mm.Year, mm.ImdbId, mm.TmdbId FROM Movies m "
            "JOIN MovieMetadata mm ON m.MovieMetadataId = mm.Id"):
        if mid not in wanted:
            continue
        movies[mid] = {"id": mid, "title": title, "year": year, "imdb": imdb, "tmdb": tmdb}
        names = {clean(title)}
        names.update(clean(t) for (t,) in db.execute(
            "SELECT Title FROM AlternativeTitles WHERE MovieMetadataId=?", (mmid,)) if t)
        names.update(clean(t) for (t,) in db.execute(
            "SELECT Title FROM MovieTranslations WHERE MovieMetadataId=?", (mmid,)) if t)
        for n in names:
            keys[(n, year)].append(mid)

    items = collections.defaultdict(lambda: collections.defaultdict(list))  # movie -> indexer -> items
    source = collections.defaultdict(dict)

    # 1. Radarr API release dumps (original casing), keyed by Radarr indexer id.
    for spec in a.release_json:
        mid, path = spec.split("=", 1)
        mid = int(mid)
        for r in json.loads(Path(path).read_text()):
            published = datetime.datetime.fromisoformat(r["publishDate"].replace("Z", "+00:00")).timestamp()
            imdb = f"{int(r['imdbId']):07d}" if r.get("imdbId") else None
            items[mid][r["indexerId"]].append(fixture_item(r["title"], r["size"], published, imdb))
        for iid in route:
            source[mid][iid] = "release-json"

    # 2. Real cached Newznab response bodies.
    imdb_to_movie = {m["imdb"][2:].lstrip("0"): mid for mid, m in movies.items() if m["imdb"]}
    for indexer, body in cache.execute("SELECT indexer, body FROM responses"):
        root = ET.fromstring(zlib.decompress(body))
        iid = by_route.get(indexer)
        groups = collections.defaultdict(list)
        for it in root.iter("item"):
            attrs = {x.get("name"): x.get("value") for x in it.iter(NEWZNAB + "attr")}
            imdb = attrs.get("imdb")
            mid = imdb_to_movie.get((imdb or "").lstrip("0"))
            if mid is None or iid is None:
                continue
            published = email.utils.parsedate_to_datetime(it.findtext("pubDate")).timestamp()
            groups[mid].append(fixture_item(it.findtext("title"), it.findtext("size"), published, imdb,
                                            attrs.get("grabs"), attrs.get("files")))
        for mid, rows in groups.items():
            if source[mid].get(iid) is None:
                items[mid][iid] = rows
                source[mid][iid] = "cached-response"

    # 3. Release metadata recorded by the search cache (lower-cased titles).
    rx = re.compile(r"^(.+?)[ ._\-\(\[]+((?:19|20)\d{2})(?=[ ._\-\)\]]|$)")
    rss = collections.defaultdict(list)
    for indexer, title, size, published in cache.execute(
            "SELECT indexer, title, size, published FROM nzbs ORDER BY published DESC"):
        iid = by_route.get(indexer)
        if iid is None or not title:
            continue
        if len(rss[iid]) < a.rss_per_indexer:
            rss[iid].append(fixture_item(title, size, published or 0, None))
        m = rx.match(title)
        if not m:
            continue
        for mid in keys.get((clean(m[1]), int(m[2])), []):
            if source[mid].get(iid) in (None, "nzbs"):
                source[mid][iid] = "nzbs"
                imdb = movies[mid]["imdb"][2:] if movies[mid]["imdb"] else None
                items[mid][iid].append(fixture_item(title, size, published or 0, imdb))

    manifest = {"routes": route, "movies": []}
    for mid in wanted:
        if mid not in movies:
            continue
        folder = out / "items" / str(mid)
        folder.mkdir(parents=True, exist_ok=True)
        counts = {}
        for iid in route:
            rows = sorted(items[mid].get(iid, []), key=lambda r: -r["published"])
            for r in rows:
                r["guid"] = hashlib.sha1(f"{iid}|{r['title']}|{r['size']}".encode()).hexdigest()
            (folder / f"{iid}.json").write_text(json.dumps(rows))
            counts[iid] = len(rows)
        manifest["movies"].append({**movies[mid], "counts": counts,
                                   "sources": sorted(set(source[mid].values())),
                                   "total": sum(counts.values())})
    rdir = out / "rss"
    rdir.mkdir(exist_ok=True)
    for iid in route:
        rows = rss.get(iid, [])
        for r in rows:
            r["guid"] = hashlib.sha1(f"rss|{iid}|{r['title']}|{r['size']}".encode()).hexdigest()
        (rdir / f"{iid}.json").write_text(json.dumps(rows))
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1))
    for m in manifest["movies"]:
        print(f"{m['id']:>4} {m['title'][:40]:40} {m['year']} total={m['total']:>4} {m['sources']}")


if __name__ == "__main__":
    main()
