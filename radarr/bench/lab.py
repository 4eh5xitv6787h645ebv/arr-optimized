#!/usr/bin/env python3
"""Offline Radarr lab orchestration (host side).

Everything runs on one Docker network created with --internal (no LAN/internet
egress). Containers are named radarr-opt-*; no ports are published, the client
container on the same network makes all API calls.

  lab.py infra                     network + fail-closed stub + offline search cache + client
  lab.py clone NAME IMAGE [--dll P] [--diag] sanitized clone radarr-opt-NAME (4 CPUs); --dll bind-mounts a Core DLL
  lab.py wait NAME                 wait until the API answers
  lab.py safety NAME               assert no download clients / notifications / non-lab indexers
  lab.py search NAME MOVIE|rss [OUT] one timed interactive search, or RSS-sync decisions (prints JSON)
  lab.py stats                     search-cache counters + stub refused count
  lab.py down                      remove radarr-opt-* containers and the network (images kept)

Environment: RADARR_LAB_PRIVATE (default ../../radarr-lab-private, outside the repository) holds clone configs,
the offline cache and outputs. RADARR_SNAPSHOT_DB is the private radarr.db snapshot.
"""
import json
import os
import secrets
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
# Inputs and outputs live outside the repository (they contain your library and indexer data).
PRIVATE = Path(os.environ.get("RADARR_LAB_PRIVATE", HERE.parents[2] / "radarr-lab-private"))
SNAPSHOT_DB = os.environ.get("RADARR_SNAPSHOT_DB", str(PRIVATE / "radarr.db"))
CACHE_SRC = Path(os.environ.get("RADARR_CACHE_SERVICE", PRIVATE / "search-cache"))
# A host address the lab must NOT reach (default: the Docker bridge gateway, i.e. this host).
HOST_PROBE = os.environ.get("RADARR_LAB_HOST_PROBE", "172.17.0.1")
NET = "radarr-opt-net"
PY = "python:3.13.14-slim"
CACHE_IMAGE = "radarr-opt-search-cache:local"


def sh(*cmd, check=True, capture=False):
    r = subprocess.run(cmd, check=check, text=True, capture_output=capture)
    return r.stdout if capture else r.returncode


def exists(name):
    return sh("docker", "container", "inspect", name, check=False, capture=True).strip() not in ("", "[]")


def rm(name):
    if exists(name):
        sh("docker", "rm", "-f", name, capture=True)


def api_key(name):
    return (PRIVATE / "clones" / name / "apikey").read_text().strip()


def client(*args):
    return sh("docker", "exec", "radarr-opt-client", "python", "-I", *args, capture=True)


def infra(discovery=False):
    nets = sh("docker", "network", "ls", "--format", "{{.Name}}", capture=True).split()
    if NET not in nets:
        sh("docker", "network", "create", "--internal", NET, capture=True)
    assert json.loads(sh("docker", "network", "inspect", NET, capture=True))[0]["Internal"] is True
    sh("docker", "build", "-q", "-t", CACHE_IMAGE, str(CACHE_SRC), capture=True)
    cache_dir = PRIVATE / "offline-cache"
    for name in ("radarr-opt-stub", "radarr-opt-cache", "radarr-opt-client"):
        rm(name)
    common = ["--network", NET, "--restart", "no", "--cpus", "1", "--memory", "512m", "--user", "1000:1000"]
    sh("docker", "run", "-d", "--name", "radarr-opt-stub", *common,
       "-v", f"{HERE}:/bench:ro", "-v", f"{cache_dir}/caps.json:/caps.json:ro", "-v", f"{cache_dir}/rss:/rss:ro",
       "-e", f"DISCOVERY={1 if discovery else 0}",
       PY, "python", "-I", "/bench/stub_upstream.py", "/caps.json", "/rss", capture=True)
    sh("docker", "run", "-d", "--name", "radarr-opt-cache", *common,
       "-v", f"{cache_dir}:/data", "-e", "CACHE_CONFIG=/data/settings.json", CACHE_IMAGE, capture=True)
    sh("docker", "run", "-d", "--name", "radarr-opt-client", *common,
       "-v", f"{HERE}:/bench:ro", "-v", f"{PRIVATE / 'out'}:/out",
       PY, "sleep", "infinity", capture=True)
    # Egress check: the internal network must not reach the internet or the LAN.
    probe = client("-c", "import socket,sys\n"
                   f"for h,p in [('1.1.1.1',443),('{HOST_PROBE}',7878),('radarr-opt-cache',8080)]:\n"
                   "  s=socket.socket();s.settimeout(3)\n"
                   "  try: s.connect((h,p)); print(h,'reachable')\n"
                   "  except OSError as e: print(h,'blocked',type(e).__name__)\n")
    print(probe.strip())
    assert "1.1.1.1 reachable" not in probe and f"{HOST_PROBE} reachable" not in probe
    assert "radarr-opt-cache reachable" in probe


def clone(name, image, dll=None, extra=(), rss=True):
    cname = f"radarr-opt-{name}"
    rm(cname)
    cfg = PRIVATE / "clones" / name
    cfg.mkdir(parents=True, exist_ok=True)
    key = secrets.token_hex(16)
    sh(sys.executable, "-I", str(HERE / "prepare_clone.py"), "--radarr-db", SNAPSHOT_DB,
       "--out", str(cfg / "config"), "--api-key", key, *(["--enable-rss"] if rss else []))
    (cfg / "apikey").write_text(key)
    tmp = cfg / "tmp"
    tmp.mkdir(exist_ok=True)
    tmp.chmod(0o1777)
    args = ["docker", "run", "-d", "--name", cname, "--network", NET, "--restart", "no",
            "--cpus", "4", "--memory", "4g", "-e", "PUID=1000", "-e", "PGID=1000", "-e", "TZ=Etc/UTC",
            "-v", f"{cfg / 'config'}:/config", "-v", f"{tmp}:/tmp", *extra]
    if dll:
        args += ["-v", f"{Path(dll).resolve()}:/app/radarr/bin/Radarr.Core.dll:ro"]
    sh(*args, image, capture=True)
    wait(name)


def wait(name, timeout=240):
    key = api_key(name)
    end = time.time() + timeout
    while time.time() < end:
        r = json.loads(client("/bench/req.py", f"radarr-opt-{name}", key, "/api/v3/system/status"))
        if r["status"] == 200:
            break
        time.sleep(2)
    else:
        raise SystemExit(f"{name} not ready")
    # Wait for startup work (migrations, housekeeping) to settle.
    time.sleep(5)


def safety(name):
    key = api_key(name)
    out = {}
    for path in ("downloadclient", "notification", "importlist", "indexer"):
        client("/bench/req.py", f"radarr-opt-{name}", key, f"/api/v3/{path}", f"/out/safety-{name}-{path}.json")
        data = json.loads((PRIVATE / "out" / f"safety-{name}-{path}.json").read_text())
        out[path] = data
        (PRIVATE / "out" / f"safety-{name}-{path}.json").unlink()
    assert out["downloadclient"] == [], "download clients present"
    assert out["notification"] == [], "notifications present"
    assert out["importlist"] == [], "import lists present"
    for ix in out["indexer"]:
        base = next(f["value"] for f in ix["fields"] if f["name"] == "baseUrl")
        assert base.startswith("http://radarr-opt-cache:8080/"), "indexer not on offline cache"
        assert not ix["enableAutomaticSearch"]
    # RSS feeds may be enabled for the RSS-decision benchmark, but the scheduled sync must be off.
    client("/bench/req.py", f"radarr-opt-{name}", key, "/api/v3/config/indexer", f"/out/safety-{name}-cfg.json")
    cfg = json.loads((PRIVATE / "out" / f"safety-{name}-cfg.json").read_text())
    (PRIVATE / "out" / f"safety-{name}-cfg.json").unlink()
    assert cfg["rssSyncInterval"] == 0, "scheduled RSS sync enabled"
    print(json.dumps({"clone": name, "download_clients": 0, "notifications": 0, "import_lists": 0,
                      "indexers_offline": len(out["indexer"]), "rss_sync_interval": 0}))


def search(name, movie, out=None):
    path = "/api/v3/release" if movie == "rss" else f"/api/v3/release?movieId={movie}"
    args = ["/bench/req.py", f"radarr-opt-{name}", api_key(name), path]
    if out:
        args.append(out)
    return json.loads(client(*args))


def stats():
    s = client("-c", "import urllib.request,json\n"
               "r=urllib.request.Request('http://radarr-opt-cache:8080/stats',headers={'Authorization':'Bearer offline-lab-admin'})\n"
               "d=json.load(urllib.request.urlopen(r));print(json.dumps(d['counters']))\n"
               "print(urllib.request.urlopen('http://radarr-opt-stub:8081/refused').read().decode())")
    print(s.strip())


def down():
    names = sh("docker", "ps", "-a", "--filter", "name=^radarr-opt-", "--format", "{{.Names}}", capture=True).split()
    for n in names:
        rm(n)
    sh("docker", "network", "rm", NET, check=False, capture=True)


if __name__ == "__main__":
    cmd, *rest = sys.argv[1:]
    if cmd == "infra":
        infra("--discovery" in rest)
    elif cmd == "clone":
        dll = None
        if "--dll" in rest:
            i = rest.index("--dll")
            dll = rest[i + 1]
            rest = rest[:i] + rest[i + 2:]
        extra = ()
        if "--diag" in rest:
            # Profiling only: the image disables the diagnostics IPC socket.
            rest.remove("--diag")
            extra = ("-e", "COMPlus_EnableDiagnostics=1", "-e", "DOTNET_EnableDiagnostics=1", "-e", "TMPDIR=/tmp")
        clone(rest[0], rest[1], dll, extra)
    elif cmd == "wait":
        wait(rest[0])
    elif cmd == "safety":
        safety(rest[0])
    elif cmd == "search":
        print(json.dumps(search(rest[0], rest[1], rest[2] if len(rest) > 2 else None)))
    elif cmd == "stats":
        stats()
    elif cmd == "down":
        down()
    else:
        raise SystemExit(__doc__)
