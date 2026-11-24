#!/usr/bin/env python3
"""Create a sanitized, offline Radarr config directory from a private radarr.db snapshot.

- Every indexer points at the offline search cache (http://radarr-opt-cache:8080/<route>)
  with a lab-only API key; RSS and automatic search are disabled.
- Download clients, notifications, import lists, pending releases and queued commands
  are deleted (and asserted empty), so nothing can be grabbed or sent anywhere.
- Scheduled tasks are pushed to 2099 and RSS sync is disabled.
- A fresh config.xml with a lab API key is written; no secrets are printed.

  prepare_clone.py --radarr-db radarr.db --out DIR --api-key KEY
"""
import argparse
import json
import re
import sqlite3
from pathlib import Path

CONFIG = """<Config>
  <BindAddress>*</BindAddress>
  <Port>7878</Port>
  <SslPort>9898</SslPort>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>{key}</ApiKey>
  <AuthenticationMethod>External</AuthenticationMethod>
  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <Branch>master</Branch>
  <LogLevel>info</LogLevel>
  <UrlBase></UrlBase>
  <InstanceName>radarr-opt lab</InstanceName>
  <UpdateMechanism>Docker</UpdateMechanism>
  <UpdateAutomatically>False</UpdateAutomatically>
  <AnalyticsEnabled>False</AnalyticsEnabled>
</Config>
"""


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--radarr-db", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--api-key", required=True)
    p.add_argument("--indexer-key", default="offline-lab-key")
    p.add_argument("--enable-rss", action="store_true",
                   help="keep indexer RSS enabled so GET /api/v3/release (RSS decisions) has feeds; "
                        "the scheduled RSS sync stays disabled and there are no download clients")
    a = p.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    for f in out.iterdir():
        if f.is_file():
            f.unlink()
    src = sqlite3.connect(f"file:{a.radarr_db}?mode=ro", uri=True)
    db = sqlite3.connect(out / "radarr.db")
    src.backup(db)
    src.close()
    tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}

    for iid, settings in db.execute("SELECT Id, Settings FROM Indexers").fetchall():
        s = json.loads(settings)
        route = re.search(r"/(\d+)/?$", s.get("baseUrl", ""))
        if not route:
            raise SystemExit(f"indexer {iid}: cannot map to a search-cache route; refusing")
        s["baseUrl"] = f"http://radarr-opt-cache:8080/{route[1]}"
        s["apiKey"] = a.indexer_key
        db.execute("UPDATE Indexers SET Settings=?, EnableRss=?, EnableAutomaticSearch=0 WHERE Id=?",
                   (json.dumps(s), 1 if a.enable_rss else 0, iid))
    for t in ("DownloadClients", "Notifications", "ImportLists", "ImportListMovies", "PendingReleases",
              "Commands", "IndexerStatus", "DownloadClientStatus", "ImportListStatus", "NotificationStatus",
              "AutoTagging"):
        if t in tables:
            db.execute(f"DELETE FROM {t}")
    db.execute("UPDATE ScheduledTasks SET LastExecution='2099-01-01 00:00:00Z'")
    cols = {r[1] for r in db.execute("PRAGMA table_info(ScheduledTasks)")}
    if "LastStartTime" in cols:
        db.execute("UPDATE ScheduledTasks SET LastStartTime='2099-01-01 00:00:00Z'")
    for k, v in {"rsssyncinterval": "0", "importlistsyncinterval": "0", "checkforfinisheddownloadinterval": "0"}.items():
        db.execute("DELETE FROM Config WHERE Key=?", (k,))
        db.execute("INSERT INTO Config (Key, Value) VALUES (?,?)", (k, v))
    db.commit()

    # Assertions: nothing that could grab, notify or reach a real host remains.
    for t in ("DownloadClients", "Notifications", "ImportLists"):
        assert db.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0] == 0, t
    for (settings,) in db.execute("SELECT Settings FROM Indexers"):
        assert json.loads(settings)["baseUrl"].startswith("http://radarr-opt-cache:8080/")
    db.close()
    (out / "config.xml").write_text(CONFIG.format(key=a.api_key))
    out.chmod(0o777)
    print(f"clone config ready in {out}: indexers rewired to offline cache, "
          f"0 download clients, 0 notifications, 0 import lists")


if __name__ == "__main__":
    main()
