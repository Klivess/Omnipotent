#!/usr/bin/env python3
"""Copy an offline legacy workspace without requiring the Docker daemon.

Creates and verifies a separate backup first. Never deletes source files, changes
the active provider, or publishes account content. Resume by rerunning the same
command: identical destination hashes are accepted; conflicting files stop it.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import ssl
import urllib.request
from urllib.parse import quote
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--config", required=True, help="Protected host worker-client JSON")
parser.add_argument("--project", required=True)
parser.add_argument("--source", required=True, type=Path)
parser.add_argument("--backup", required=True, type=Path)
parser.add_argument("--offline-source", required=True, action="store_true", help="Project paused and old computers stopped/offline")
args = parser.parse_args()
if not args.project.replace("-", "").replace("_", "").isalnum():
    raise SystemExit("Invalid project ID")
source = args.source.resolve(strict=True)
backup = args.backup.resolve()
if backup.is_relative_to(source):
    raise SystemExit("Backup must be outside the source workspace")
config = json.loads(Path(args.config).read_text())
context = ssl.create_default_context(cafile=config["ca"])
context.load_cert_chain(config["cert"], config["key"])


def request(method, path, payload=None):
    url = config["endpoint"].rstrip("/") + path
    if not url.startswith("https://"):
        raise ValueError("HTTPS required")
    req = urllib.request.Request(url, data=json.dumps(payload).encode() if payload is not None else None,
                                 method=method, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, context=context, timeout=120) as response:
        return json.load(response)


files = []
for path in sorted(source.rglob("*")):
    if path.is_symlink() or path.is_junction():
        raise SystemExit("Resolve filesystem links explicitly before migration: " + str(path))
    if path.is_file():
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        files.append({"path": path.relative_to(source).as_posix(), "sha256": digest, "bytes": path.stat().st_size})
if not files:
    raise SystemExit("Source contains no files; verify source path")
backup.parent.mkdir(parents=True, exist_ok=True)
if not backup.exists():
    with zipfile.ZipFile(backup, "x", compression=zipfile.ZIP_STORED) as archive:
        for item in files:
            archive.write(source / item["path"], item["path"])
with zipfile.ZipFile(backup) as archive:
    for item in files:
        with archive.open(item["path"]) as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != item["sha256"]:
                raise SystemExit("Backup mismatch; source may have changed")
with backup.open("rb") as stream:
    backup_hash = hashlib.file_digest(stream, "sha256").hexdigest()
# Provision a computer first so the authoritative project subvolume exists.
record = request("POST", "/computers/ensure", {"projectID": args.project, "agentID": "commander"})
if record["state"] != "ready":
    raise SystemExit("Computer is queued/provisioning. Backup is verified; rerun after it becomes ready.")
for item in files:
    parent = str(Path(item["path"]).parent).replace("\\", "/")
    if parent != ".":
        request("POST", "/workspaces/" + args.project + "/mutate?path=" + quote(parent, safe=""),
                {"operationID": "mkdir-" + hashlib.sha256((args.project + parent).encode()).hexdigest()[:32], "operation": "mkdir"})
    route = "/workspaces/" + args.project + "/file?path=" + quote(item["path"], safe="")
    offset = 0
    with (source / item["path"]).open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != item["sha256"]:
            raise SystemExit("Source changed during migration; project must remain offline")
        stream.seek(0)
        while True:
            data = stream.read(1024 * 1024)
            result = request("PUT", route, {"transferID": hashlib.sha256((args.project + item["path"] + item["sha256"]).encode()).hexdigest(),
                "offset": offset, "data": base64.b64encode(data).decode(), "expectedVersion": None,
                "sha256": item["sha256"], "commit": offset + len(data) == item["bytes"]})
            offset += len(data)
            if offset == item["bytes"]:
                break
receipt = request("POST", "/workspaces/" + args.project + "/verify-migration", {"files": files, "backupSha256": backup_hash})
receipt_path = backup.with_suffix(backup.suffix + ".receipt.json")
receipt_path.write_text(json.dumps(receipt, indent=2))
print("Copied and verified", len(files), "files. Receipt:", receipt_path)
print("Provider is unchanged. Import and verify browser profiles, run canary checks, then activate the paused project.")
