#!/usr/bin/env python3
"""Build an offline replay index from a Sonarr search-cache snapshot.

Inputs (private, never copied into this repository):
  --cache   search-cache sqlite3 (tables: responses, nzbs)
  --sonarr  sonarr.db (Series, SceneMappings) used only to map titles to TVDB ids
Output:
  --out     replay sqlite3 containing release titles, sizes, publish times and
            indexer ids, plus the verbatim cached search pages with every URL
            replaced by an opaque local placeholder. No URLs, GUIDs or API keys
            are written to the output.
"""
import argparse, hashlib, json, re, sqlite3, unicodedata, zlib

NORMALIZE = re.compile(r"((?:\b|_)(?<!^)([aà](?!$)|an|the|and|or|of)(?!$)(?:\b|_))|\W|_", re.I)
URL = re.compile(rb"https?://[^\"<\s]*")


def clean(title):
    """Python approximation of Sonarr's CleanSeriesTitle."""
    if not title:
        return ""
    if title.isdigit():
        return title
    title = re.sub(r"(?<=\b\d)%", "percent", title)
    title = NORMALIZE.sub("", title).lower()
    return "".join(c for c in unicodedata.normalize("NFD", title) if unicodedata.category(c) != "Mn")


SEP = r"[ ._\-]"
STANDARD = re.compile(
    rf"^(?P<name>.+?){SEP}+(?:\(?(?P<year>(?:19|20)\d\d)\)?{SEP}+)?"
    rf"(?:s(?P<s>\d{{1,4}})(?P<eps>(?:{SEP}?e\d{{1,4}}|-e?\d{{1,4}})*)|(?P<xs>\d{{1,2}})x(?P<xe>\d{{2,3}})|season{SEP}?(?P<ss>\d{{1,2}}))(?![a-z])")
ANIME = re.compile(rf"^\[[^\]]+\]{SEP}*(?P<name>.+?){SEP}+-{SEP}+(?:s(?P<s>\d+)e)?(?P<abs>\d{{1,4}})(?:v\d)?(?![\d])")
DAILY = re.compile(rf"^(?P<name>.+?){SEP}+(?P<y>(?:19|20)\d\d){SEP}(?P<m>\d\d){SEP}(?P<d>\d\d)(?!\d)")


def parse(title):
    m = ANIME.match(title)
    if m:
        if m["s"]:
            return m["name"], int(m["s"]), [int(m["abs"])], None, None
        return m["name"], None, [], int(m["abs"]), None
    m = DAILY.match(title)
    if m:
        return m["name"], int(m["y"]), [], None, f"{m['m']}/{m['d']}"
    m = STANDARD.match(title)
    if m:
        name = m["name"]
        if m["s"] is not None:
            eps = [int(x) for x in re.findall(r"\d+", m["eps"] or "")]
            if len(eps) == 2 and "-" in (m["eps"] or ""):
                eps = list(range(eps[0], eps[1] + 1)) if 0 <= eps[1] - eps[0] < 50 else eps
            return name, int(m["s"]), eps, None, None
        if m["xs"] is not None:
            return name, int(m["xs"]), [int(m["xe"])], None, None
        return name, int(m["ss"]), [], None, None
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True)
    ap.add_argument("--sonarr", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    src = sqlite3.connect(f"file:{a.cache}?mode=ro", uri=True)
    son = sqlite3.connect(f"file:{a.sonarr}?mode=ro", uri=True)
    titles = {}
    for tvdb, t, ct, ot in son.execute("SELECT TvdbId, Title, CleanTitle, OriginalTitle FROM Series"):
        for x in (clean(t), ct, clean(ot or "")):
            if x:
                titles.setdefault(x, tvdb)
    for tvdb, term, title in son.execute("SELECT s.TvdbId, s.ParseTerm, s.Title FROM SceneMappings s JOIN Series t ON t.TvdbId = s.TvdbId"):
        for x in (term, clean(title or "")):
            if x:
                titles.setdefault(x, tvdb)
    out = sqlite3.connect(a.out)
    out.executescript("""
        DROP TABLE IF EXISTS items; DROP TABLE IF EXISTS pages; DROP TABLE IF EXISTS series;
        CREATE TABLE items (id TEXT PRIMARY KEY, indexer INT, tvdbid INT, season INT, episodes TEXT,
            absolute INT, daily TEXT, title TEXT, size INT, published REAL, name TEXT);
        CREATE INDEX items_lookup ON items(tvdbid, indexer);
        CREATE INDEX items_recent ON items(indexer, published);
        CREATE TABLE pages (key TEXT PRIMARY KEY, indexer INT, query TEXT, headers TEXT, body BLOB);
        CREATE TABLE series (tvdbid INT PRIMARY KEY, clean TEXT, type INT);
    """)
    for tvdb, ct, typ in son.execute("SELECT TvdbId, CleanTitle, SeriesType FROM Series"):
        out.execute("INSERT INTO series VALUES (?,?,?)", (tvdb, ct, typ))
    mapped = total = 0
    rows = []
    for guid, indexer, title, size, published in src.execute("SELECT guid, indexer, title, size, published FROM nzbs"):
        total += 1
        p = parse(title)
        tvdb = name = None
        if p:
            name = re.sub(rf"{SEP}+\(?(?:19|20)\d\d\)?$", "", p[0])
            tvdb = titles.get(clean(name)) or titles.get(clean(p[0]))
        ident = hashlib.sha256(guid.encode()).hexdigest()[:32]
        if tvdb:
            mapped += 1
            rows.append((ident, indexer, tvdb, p[1], json.dumps(p[2]), p[3], p[4], title, size, published, clean(name)))
        else:
            rows.append((ident, indexer, None, None, None, None, None, title, size, published, clean(name) if p else None))
    out.executemany("INSERT OR IGNORE INTO items VALUES (?,?,?,?,?,?,?,?,?,?,?)", rows)
    # Verbatim cached pages. Every URL becomes an opaque local placeholder.
    qmap = dict(src.execute("SELECT key, query FROM search_queries"))
    for key, indexer, headers, body in src.execute("SELECT key, indexer, headers, body FROM responses"):
        raw = zlib.decompress(body)
        raw = URL.sub(lambda m: b"http://offline.invalid/item/" + hashlib.sha256(m.group(0)).hexdigest()[:32].encode(), raw)
        q = json.loads(qmap.get(key, "{}"))
        q = {k: v for k, v in q.items() if k != "apikey"}
        out.execute("INSERT INTO pages VALUES (?,?,?,?,?)", (key, indexer, json.dumps(q), headers, raw))
    out.commit()
    print(json.dumps({"nzb_rows": total, "mapped_to_library_series": mapped,
                      "verbatim_pages": out.execute("SELECT count(*) FROM pages").fetchone()[0]}))


if __name__ == "__main__":
    main()
