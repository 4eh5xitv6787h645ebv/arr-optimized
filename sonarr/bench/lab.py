#!/usr/bin/env python3
"""Isolated benchmark lab: internal Docker network, offline replay, Sonarr clones.

All containers are named sonarr-opt-*, sit on an --internal network (no LAN or
internet egress) and are reached from the host by container IP; no host ports
are published. Clones are capped at 4 CPUs.

  lab.py up       --lab DIR            network + offline replay (DIR/replay.sqlite3)
  lab.py down                          remove sonarr-opt-* containers and the network
  lab.py search   --lab DIR --image IMG --name N --query Q [--query Q ...] --out DIR
  lab.py bench    --lab DIR --image IMG --label L --target Q --warmup Q --reps 5 --warm 5
Query syntax: e<episodeId> (episode search) or s<seriesId>:<season> (season search).
"""
import argparse, json, os, shutil, signal, subprocess, sys, time, urllib.request
from pathlib import Path

NET = "sonarr-opt-net"
BENCH = Path(__file__).resolve().parent
API_KEY = "0123456789abcdef0123456789abcdef"  # lab-only key written by make_clone.py --api-key


def sh(*args, check=True, capture=True):
    r = subprocess.run(args, check=check, text=True, capture_output=capture)
    return r.stdout.strip() if capture else ""


def ip(name):
    return sh("docker", "inspect", name, "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}")


def api(name, path, timeout=600):
    req = urllib.request.Request(f"http://{ip(name)}:8989/api/v3/{path}", headers={"X-Api-Key": API_KEY})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def up(lab, feed="all"):
    if NET not in sh("docker", "network", "ls", "--format", "{{.Name}}").split():
        sh("docker", "network", "create", "--internal", NET)
    assert sh("docker", "network", "inspect", NET, "-f", "{{.Internal}}") == "true"
    sh("docker", "rm", "-f", "sonarr-opt-cache", check=False)
    (lab / "logs").mkdir(exist_ok=True)
    os.chmod(lab / "logs", 0o777)
    sh("docker", "run", "-d", "--name", "sonarr-opt-cache", "--network", NET, "--network-alias", "sonarr-opt-cache",
       "--cpus", "2", "--user", "1000:1000", "-e", "PYTHONDONTWRITEBYTECODE=1", "-e", f"FEED={feed}",
       "-v", f"{BENCH}:/app:ro", "-v", f"{lab / 'replay.sqlite3'}:/data/replay.sqlite3:ro", "-v", f"{lab / 'logs'}:/log",
       "python:3-alpine", "python", "/app/offline_newznab.py")
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://{ip('sonarr-opt-cache')}:8080/health", timeout=2)
            return
        except Exception:
            time.sleep(1)
    sys.exit("replay did not start")


def down():
    names = sh("docker", "ps", "-a", "--filter", "name=^sonarr-opt-", "--format", "{{.Names}}").split()
    if names:
        sh("docker", "rm", "-f", *names)
    sh("docker", "network", "rm", NET, check=False)


def start(lab, image, name, trace=False, debug=False):
    sh("docker", "rm", "-f", name, check=False)
    cfg = lab / "run" / name
    if cfg.exists():
        shutil.rmtree(cfg)
    cfg.parent.mkdir(exist_ok=True)
    sh("cp", "-a", str(lab / "template"), str(cfg))
    if debug:
        xml = (cfg / "config.xml").read_text().replace("<LogLevel>info</LogLevel>", "<LogLevel>debug</LogLevel>")
        (cfg / "config.xml").write_text(xml)
    args = ["docker", "run", "-d", "--name", name, "--network", NET, "--cpus", "4",
            "-e", "PUID=1000", "-e", "PGID=1000", "-e", "TZ=Etc/UTC", "-v", f"{cfg}:/config"]
    if trace:
        (cfg / "tmp").mkdir(exist_ok=True)
        # linuxserver disables the diagnostics IPC; enable it and expose TMPDIR for dotnet-trace.
        args += ["-e", "COMPlus_EnableDiagnostics=1", "-e", "DOTNET_EnableDiagnostics=1",
                 "-v", f"{cfg / 'tmp'}:/run/sonarr-temp"]
    sh(*args, image)
    deadline = time.time() + 300
    while time.time() < deadline:
        try:
            api(name, "system/status", timeout=5)
            break
        except Exception:
            time.sleep(1)
    else:
        sys.exit(f"{name} did not start")
    # Isolation checks: no download clients, notifications or import lists.
    for kind in ("downloadclient", "notification", "importlist"):
        if api(name, kind):
            sys.exit(f"refusing: {kind} configured in clone")
    idle(name)
    return cfg


def idle(name, quiet=8):
    """Wait until no command has been queued/started for `quiet` seconds."""
    calm = 0
    while calm < quiet:
        busy = [c for c in api(name, "command") if c.get("status") in ("queued", "started")]
        calm = 0 if busy else calm + 1
        time.sleep(1)


def query_path(q):
    if q.startswith("e"):
        return f"release?episodeId={int(q[1:])}"
    series, season = q[1:].split(":")
    return f"release?seriesId={int(series)}&seasonNumber={int(season)}"


def cpu_usec(name):
    cid = sh("docker", "inspect", name, "-f", "{{.Id}}")
    for line in Path(f"/sys/fs/cgroup/system.slice/docker-{cid}.scope/cpu.stat").read_text().splitlines():
        if line.startswith("usage_usec "):
            return int(line.split()[1])


def last_replay_response(lab):
    """Wall-clock time the offline indexer finished its most recent response."""
    log = Path(lab) / "logs" / "requests.jsonl"
    with open(log, "rb") as f:
        f.seek(max(0, log.stat().st_size - 4096))
        return json.loads(f.read().splitlines()[-1])["done"]


def search(name, q):
    t = time.perf_counter()
    data = api(name, query_path(q))
    return time.perf_counter() - t, data


def measured_search(lab, name, q):
    """wall: API latency. cpu: container CPU seconds for the search (Sonarr's own work).
    tail: last indexer response -> API response, i.e. processing on the critical path."""
    c0 = cpu_usec(name)
    t = time.perf_counter()
    data = api(name, query_path(q))
    wall = time.perf_counter() - t
    end = time.time()
    cpu = (cpu_usec(name) - c0) / 1e6
    return {"wall_s": round(wall, 4), "cpu_s": round(cpu, 4), "tail_s": round(end - last_replay_response(lab), 4),
            "releases": len(data)}, data


def post(name, path, body):
    req = urllib.request.Request(f"http://{ip(name)}:8989/api/v3/{path}", data=json.dumps(body).encode(),
                                 headers={"X-Api-Key": API_KEY, "Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read())


def rss_sync(name):
    """Run one RSS sync command; returns command duration, wall and container CPU seconds."""
    c0 = cpu_usec(name)
    t = time.perf_counter()
    cmd = post(name, "command", {"name": "RssSync"})
    while True:
        state = api(name, f"command/{cmd['id']}")
        if state["status"] in ("completed", "failed", "aborted", "cancelled", "orphaned"):
            break
        time.sleep(0.05)
    wall = time.perf_counter() - t
    return {"status": state["status"], "wall_s": round(wall, 4), "cpu_s": round((cpu_usec(name) - c0) / 1e6, 4),
            "message": state.get("message")}


def stop(name):
    sh("docker", "rm", "-f", name, check=False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd")
    ap.add_argument("--lab", type=Path)
    ap.add_argument("--image")
    ap.add_argument("--name", default="sonarr-opt-app")
    ap.add_argument("--label")
    ap.add_argument("--query", action="append", default=[])
    ap.add_argument("--target")
    ap.add_argument("--warmup", action="append", default=[])
    ap.add_argument("--reps", type=int, default=5)
    ap.add_argument("--warm", type=int, default=5)
    ap.add_argument("--out", type=Path)
    ap.add_argument("--feed", default="all", choices=["all", "library"])
    a = ap.parse_args()
    if a.cmd == "up":
        up(a.lab, a.feed)
    elif a.cmd == "down":
        down()
    elif a.cmd == "search":
        a.out.mkdir(parents=True, exist_ok=True)
        start(a.lab, a.image, a.name)
        for q in a.query:
            time.sleep(2.5)  # let the per-indexer 2 s rate limit lapse between searches
            m, data = measured_search(a.lab, a.name, q)
            (a.out / f"{q.replace(':', '_')}.json").write_text(json.dumps(data))
            print(json.dumps({"query": q, **m}), flush=True)
        stop(a.name)
    elif a.cmd == "profile":
        # Sampled wall-clock trace of the listed searches (after warmups) with dotnet-trace.
        a.out.mkdir(parents=True, exist_ok=True)
        cfg = start(a.lab, a.image, a.name, trace=True)
        for w in a.warmup:
            search(a.name, w)
        idle(a.name, 3)
        sock = next((cfg / "tmp").glob("dotnet-diagnostic-*-socket"))
        trace = a.out / f"{a.label}.nettrace"
        env = dict(os.environ, DOTNET_ROLL_FORWARD="Major")
        proc = subprocess.Popen([os.path.expanduser("~/.dotnet/tools/dotnet-trace"), "collect", "--diagnostic-port", f"{sock},connect",
                                 "--profile", "dotnet-common,dotnet-sampled-thread-time", "--format", "Speedscope", "-o", str(trace)],
                                env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(4)
        for rep in range(max(1, a.warm)):
            for q in a.query:
                time.sleep(2.5)  # let the per-indexer 2 s rate limit lapse between searches
                s, data = search(a.name, q)
                print(json.dumps({"query": q, "pass": rep, "seconds": round(s, 3), "releases": len(data)}), flush=True)
        time.sleep(1)
        proc.send_signal(signal.SIGINT)  # dotnet-trace stops and converts on Ctrl+C
        proc.wait()
        stop(a.name)
    elif a.cmd == "rss-decisions":
        # One RSS sync with debug logging; writes every per-release decision line, in order.
        import re
        a.out.mkdir(parents=True, exist_ok=True)
        cfg = start(a.lab, a.image, a.name, debug=True)
        result = rss_sync(a.name)
        time.sleep(2)
        lines = []
        # Oldest first: NLog's rolling archives are sonarr.debug.<n>.txt with 0 the newest, then sonarr.debug.txt.
        def age(log):
            n = log.name[len("sonarr.debug."):-len(".txt")]
            return -int(n) if n.isdigit() else 1
        for log in sorted((cfg / "logs").glob("sonarr.debug*.txt"), key=age):
            for line in log.read_text(errors="replace").splitlines():
                m = re.match(r"^\S+ \S+\|Debug\|DownloadDecisionMaker\|(Release '.*' from '.*' (?:accepted|rejected.*))$", line)
                if m:
                    lines.append(m.group(1))
        (a.out / f"{a.label}-rss-decisions.txt").write_text("\n".join(lines) + "\n")
        print(json.dumps({"label": a.label, **result, "decision_lines": len(lines)}))
        stop(a.name)
    elif a.cmd == "rss":
        # RSS sync timing: first sync after start (cold) then a.warm repeats (same feed, warm caches).
        a.out.mkdir(parents=True, exist_ok=True)
        for rep in range(a.reps):
            start(a.lab, a.image, a.name)
            for w in a.warmup:
                time.sleep(2.5)
                measured_search(a.lab, a.name, w)
            idle(a.name, 3)
            runs = []
            for i in range(a.warm + 1):
                time.sleep(2.5)
                runs.append(rss_sync(a.name))
            row = {"label": a.label, "image": a.image, "rep": rep, "cold": runs[0], "warm": runs[1:]}
            print(json.dumps(row), flush=True)
            with open(a.out / f"{a.label}-rss.jsonl", "a") as f:
                f.write(json.dumps(row) + "\n")
            stop(a.name)
    elif a.cmd == "bench":
        a.out.mkdir(parents=True, exist_ok=True)
        for rep in range(a.reps):
            start(a.lab, a.image, a.name)
            first = None
            for w in a.warmup:
                time.sleep(2.5)
                m, _ = measured_search(a.lab, a.name, w)
                first = first or m
            idle(a.name, 3)
            time.sleep(2.5)
            cold, data = measured_search(a.lab, a.name, a.target)
            warm = []
            for _ in range(a.warm):
                time.sleep(2.5)
                m, data = measured_search(a.lab, a.name, a.target)
                warm.append(m)
            row = {"label": a.label, "image": a.image, "rep": rep, "target": a.target, "process_first": first,
                   "cold": cold, "warm": warm}
            print(json.dumps(row), flush=True)
            with open(a.out / f"{a.label}.jsonl", "a") as f:
                f.write(json.dumps(row) + "\n")
            stop(a.name)


if __name__ == "__main__":
    main()
