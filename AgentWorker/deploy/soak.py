#!/usr/bin/env python3
"""Concurrent real-browser acceptance workload. Uses disposable acceptance-* projects only."""
import argparse
import concurrent.futures
import json
from pathlib import Path
import ssl
import statistics
import time
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--config", required=True)
parser.add_argument("--project", default="acceptance-computers")
parser.add_argument("--computers", type=int, default=12)
parser.add_argument("--hours", type=float, default=72)
parser.add_argument("--report", type=Path, required=True)
args = parser.parse_args()
if not args.project.startswith("acceptance-") or not 1 <= args.computers <= 24 or args.hours <= 0:
    raise SystemExit("Use an acceptance-* project and 1–24 computers")
config = json.loads(Path(args.config).read_text())
context = ssl.create_default_context(cafile=config["ca"])
context.load_cert_chain(config["cert"], config["key"])


def call(method, path, payload=None):
    request = urllib.request.Request(config["endpoint"].rstrip("/") + path, method=method,
        data=json.dumps(payload).encode() if payload is not None else None, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, context=context, timeout=45) as response:
        return json.load(response)


def route(computer, suffix):
    return "/computers/" + computer + "/" + suffix + "?projectID=" + args.project


def action(computer, tool, arguments):
    op_id = uuid.uuid4().hex
    started = time.monotonic()
    try:
        call("POST", route(computer, "actions"), {"operationID": op_id, "actorID": "acceptance", "tool": tool, "arguments": arguments})
    except Exception:
        pass  # A lost response must lead to inspection, not replay.
    deadline = time.monotonic() + 1800
    while time.monotonic() < deadline:
        try:
            result = call("GET", route(computer, "operations/" + op_id))
            if result["state"] == "completed":
                return time.monotonic() - started
            if result["state"] not in ("queued", "running"):
                raise RuntimeError("Action did not complete: " + result["state"])
        except urllib.error.URLError:
            pass
        time.sleep(1)
    raise RuntimeError("No progress for 30 minutes; inspect operation " + op_id)


computers = []
for number in range(args.computers):
    record = call("POST", "/computers/ensure", {"projectID": args.project, "agentID": "agent-" + str(number)})
    computers.append(record["computerID"])
deadline = time.monotonic() + 3600
while time.monotonic() < deadline:
    inventory = call("GET", "/computers?projectID=" + args.project)
    if all(any(row["computerID"] == computer and row["state"] == "ready" for row in inventory) for computer in computers):
        break
    time.sleep(5)
else:
    raise SystemExit("Fleet did not fit/become ready. Capacity gate failed; do not increase concurrency.")

fixture = """<!doctype html><html><body style='font:24px sans-serif;padding:40px'><button style='font:24px sans-serif' onclick='document.querySelector("output").textContent=++window.count'>Increment</button><output>0</output><input type=file><script>window.count=0</script></body></html>"""
server_jobs = []
for computer in computers:
    job_id = uuid.uuid4().hex
    command = "mkdir -p /home/agent/ka-acceptance; python3 -c "
    import shlex
    command += shlex.quote("from pathlib import Path; Path('/home/agent/ka-acceptance/index.html').write_text(" + repr(fixture) + ")")
    command += "; exec python3 -m http.server 8765 --bind 127.0.0.1 --directory /home/agent/ka-acceptance"
    call("POST", route(computer, "jobs"), {"operationID": job_id, "command": command, "cwd": "/home/agent"})
    server_jobs.append(job_id)
    for tab in range(3):
        action(computer, "computer_open_browser", {"url": "http://127.0.0.1:8765/?tab=" + str(tab)})
        time.sleep(2)

started = time.time()
report = {"started": started, "projectID": args.project, "computerIDs": computers,
          "serverJobIDs": server_jobs, "cycles": 0, "samples": 0, "errors": [], "passed": False}
latencies = []


def cycle(computer):
    duration = action(computer, "computer_click_text", {"text": "Increment"})
    action(computer, "computer_browser_inspect", {"mode": "tabs"})
    return duration


try:
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.computers) as pool:
        while time.time() - started < args.hours * 3600:
            latencies.extend(pool.map(cycle, computers))
            report.update(cycles=report["cycles"] + 1, samples=len(latencies), elapsedSeconds=time.time() - started,
                          pressure=call("GET", "/health").get("pressure"))
            if len(latencies) > 10000:
                latencies = latencies[-10000:]
            report["p95InputSeconds"] = sorted(latencies)[int((len(latencies) - 1) * .95)]
            args.report.write_text(json.dumps(report, indent=2))
            time.sleep(10)
    report["passed"] = args.computers >= 12 and args.hours >= 72
except Exception as error:
    report["errors"].append(str(error))
    raise
finally:
    report["ended"] = time.time()
    args.report.write_text(json.dumps(report, indent=2))
    print("Report:", args.report, "Computers retained for inspection. No production accounts used.")
