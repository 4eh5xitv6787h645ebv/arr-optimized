#!/usr/bin/env python3
"""Prepare an isolated Sonarr config directory from a private sonarr.db snapshot.

* Newznab indexers point at the offline replay container; their API keys are
  replaced with a dummy value. Any other indexer implementation is disabled.
* Download clients, notifications and import lists (and their status rows) are
  deleted from the copy; it still refuses to continue if any remain. The
  snapshot itself is never written.
* All scheduled tasks are pushed far into the future so nothing runs during
  timing except the requests the benchmark makes.
"""
import argparse, json, secrets, shutil, sqlite3, sys
from pathlib import Path

ap = argparse.ArgumentParser()
ap.add_argument("--db", required=True)
ap.add_argument("--out", required=True, help="config directory to create")
ap.add_argument("--replay-host", default="sonarr-opt-cache:8080")
ap.add_argument("--api-key", default=None)
a = ap.parse_args()
out = Path(a.out)
out.mkdir(parents=True, exist_ok=True)
shutil.copyfile(a.db, out / "sonarr.db")
con = sqlite3.connect(out / "sonarr.db")
stripped = {}
for table in ("DownloadClients", "DownloadClientStatus", "Notifications", "NotificationStatus", "ImportLists", "ImportListStatus"):
    if con.execute("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone()[0]:
        # The clone must never reach a real download client, notification target or list.
        stripped[table] = con.execute(f"DELETE FROM {table}").rowcount
for table in ("DownloadClients", "Notifications", "ImportLists"):
    n = con.execute(f"SELECT count(*) FROM {table}").fetchone()[0]
    if n:
        sys.exit(f"refusing: {table} has {n} rows")
for ident, impl, settings in con.execute("SELECT Id, Implementation, Settings FROM Indexers").fetchall():
    s = json.loads(settings)
    if impl == "Newznab":
        path = "/" + s["baseUrl"].split("://", 1)[1].split("/", 1)[1]
        s["baseUrl"] = f"http://{a.replay_host}{path}"
        s["apiKey"] = "offline"
        con.execute("UPDATE Indexers SET Settings=? WHERE Id=?", (json.dumps(s), ident))
    else:
        con.execute("UPDATE Indexers SET EnableRss=0, EnableAutomaticSearch=0, EnableInteractiveSearch=0 WHERE Id=?", (ident,))
con.execute("DELETE FROM IndexerStatus")
con.execute("UPDATE ScheduledTasks SET LastExecution='2099-01-01 00:00:00Z'")
con.execute("DELETE FROM Commands")
con.commit()
con.execute("VACUUM")
con.close()
key = a.api_key or secrets.token_hex(16)
(out / "config.xml").write_text(f"""<Config>
  <BindAddress>*</BindAddress><Port>8989</Port><SslPort>9898</SslPort><EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser><ApiKey>{key}</ApiKey>
  <AuthenticationMethod>External</AuthenticationMethod><AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <Branch>develop</Branch><LogLevel>info</LogLevel><UrlBase></UrlBase><InstanceName>Sonarr</InstanceName>
  <UpdateMechanism>Docker</UpdateMechanism><AnalyticsEnabled>False</AnalyticsEnabled>
</Config>
""")
print(json.dumps({"config": str(out), "api_key_file": "config.xml", "stripped_rows": stripped}))
