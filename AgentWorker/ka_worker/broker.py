import hashlib
import json
import os
from pathlib import Path
import ssl
import subprocess
import threading
import time
import urllib.error
import urllib.request
import re
import shutil
from urllib.parse import urlencode

from .http import serve
from .incus import Incus
from .pressure import Admission, Pressure, host_headroom
from .state import State, identifier, public_operation
from .workspace import Workspace


class Broker:
    def __init__(self, config):
        self.config = config
        if len(config.get("image_fingerprint", "")) != 64 or any(c not in "0123456789abcdef" for c in config["image_fingerprint"]):
            raise ValueError("A tested immutable image fingerprint must be pinned before startup")
        self.state = State(config["state"])
        self.incus = Incus(config.get("incus_socket", "/var/lib/incus/unix.socket"))
        self.workspace = Workspace(config["projects_root"])
        # Incus maps container uid/gid 0 to the first root subid (1000000 on
        # the managed worker), so the agent's uid/gid 1000 maps to 1001000.
        self.project_host_uid = int(config.get("project_host_uid", 1001000))
        self.admission = Admission(config.get("reserve_bytes", 1024 ** 3), config.get("start_bytes", 1536 * 1024 ** 2))
        self.context = ssl.create_default_context(cafile=config["ca"])
        self.context.load_cert_chain(config["session_client_cert"], config["session_client_key"])
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def session(self, computer, method, path, body=None, query=None):
        if not computer:
            raise KeyError("computer")
        # IP and certificate SAN are persisted together at creation, never supplied by callers.
        url = "https://" + computer["address"] + ":7444" + path
        if query:
            url += "?" + urlencode(query)
        request = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None,
                                         method=method, headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, context=self.context, timeout=35) as response:
            result = json.load(response)
        observed = self.state.setting("contact:" + computer["computerID"]) or {}
        observed["lastContact"] = time.time()
        if method == "GET" and path == "/health":
            observed.update(health=result, healthObservedAt=time.time())
        self.state.setting("contact:" + computer["computerID"], observed)
        return result

    def ensure(self, payload):
        project = identifier(payload["projectID"])
        agent = identifier(payload["agentID"])
        computer_id = "ka-" + hashlib.sha256((project + "/" + agent).encode()).hexdigest()[:24]
        with self.lock:
            record = self.state.computer(computer_id)
            if not record:
                # Persist allocation before creating anything. A missing daemon cannot lose identity.
                used = {item["address"] for item in self.state.computers()}
                address = next((self.config.get("subnet_prefix", "10.77.0.") + str(i) for i in range(10, 250)
                                if self.config.get("subnet_prefix", "10.77.0.") + str(i) not in used), None)
                if not address:
                    raise ValueError("Computer address pool exhausted")
                record = {"computerID": computer_id, "containerID": computer_id, "projectID": project,
                          "agentID": agent, "provider": "incus", "state": "queued", "address": address,
                          "width": 1600, "height": 900, "created": time.time(), "reason": "Waiting for admission"}
                self.state.save_computer(record)
            op_id = "start-" + computer_id
            self.state.submit(op_id, "start", {"computerID": computer_id})
            return record

    def ensure_workspace(self, project):
        project = identifier(project)
        with self.lock:
            root = Path(self.config["projects_root"]) / project
            if root.is_symlink():
                raise ValueError("Workspace root cannot be a symlink")
            if not root.exists():
                subprocess.run(["btrfs", "subvolume", "create", str(root)], check=True, capture_output=True, timeout=30)
                os.chown(root, self.project_host_uid, self.project_host_uid)
            for folder in ("inputs", "shared", "work", "outputs"):
                path = root / folder
                if not path.exists():
                    path.mkdir()
                    os.chown(path, self.project_host_uid, self.project_host_uid)
        return {"projectID": project, "ready": True}

    def tick(self):
        pressure = Pressure.read()
        self.state.setting("pressure", pressure.json())
        # Reservations outlive the broker. Hold through startup and 120s of observation afterwards.
        pending = sum(self.admission.start for item in self.state.list(["running", "completed"])
                      if item["kind"] in ("start", "heavy_job") and
                      (item["state"] == "running" or time.time() - item["updated"] < 120))
        for op in self.state.list(["queued", "running"]):
            if op["kind"] not in ("start", "heavy_job"):
                continue
            record = self.state.computer(op["request"]["computerID"])
            if op["state"] == "queued":
                reason = self.admission.reason(pressure, pending) or host_headroom(self.config.get("host_pressure_file"))
                if self.config.get("projects_root") and shutil.disk_usage(self.config["projects_root"]).free < 5 * 1024 ** 3:
                    reason = "Waiting for disk headroom; no existing computer will be removed"
                if reason:
                    self.state.update(op["id"], "queued", {"reason": reason})
                    if op["kind"] == "start":
                        record.update(state="queued", reason=reason)
                        self.state.save_computer(record)
                    continue
                if not self.state.claim(op["id"]):
                    continue
                pending += self.admission.start
                record["reservationUntil"] = time.time() + 120
                self.state.save_computer(record)
            try:
                if op["kind"] == "heavy_job":
                    result = self.session(record, "POST", "/jobs", op["request"]["payload"])
                    self.state.update(op["id"], "completed", {"jobID": result["id"], "computerID": record["computerID"]})
                elif self.provision(record):
                    self.state.update(op["id"], "completed", {"computerID": record["computerID"]})
            except Exception as error:
                # Timeout is not a failed computer. Reconcile deterministic lifecycle operations.
                self.state.update(op["id"], "running", {"reason": "Provisioning pending: " + type(error).__name__})

    def provision(self, record):
        name = record["computerID"]
        # Keep reservation while lifecycle operations are unresolved, including across restarts.
        record["reservationUntil"] = time.time() + 120
        self.state.save_computer(record)
        operation = record.get("incusOperation")
        if operation:
            status = self.incus.request("GET", operation)
            if status and status.get("status_code", 0) < 200:
                return False
            if status and status.get("status_code", 0) >= 400:
                record.update(state="blocked", reason=status.get("err", "Incus operation failed"))
                self.state.save_computer(record)
                self.state.update("start-" + name, "failed", {"reason": record["reason"]})
                return False
            record["incusOperation"] = None
            self.state.save_computer(record)
        instance = self.incus.instance(name)
        if instance is None:
            self.ensure_workspace(record["projectID"])
            root = Path(self.config["projects_root"]) / record["projectID"]
            created = self.incus.request("POST", "/1.0/instances", {
                "name": name, "type": "container", "profiles": [],
                "source": {"type": "image", "fingerprint": self.config["image_fingerprint"]},
                "config": {"security.privileged": "false",
                           "boot.autostart": "true", "limits.cpu.priority": "5", "limits.disk.priority": "5",
                           "user.ka.project": record["projectID"], "user.ka.agent": record["agentID"],
                           "raw.lxc": "lxc.cgroup2.memory.high=3221225472\nlxc.cgroup2.memory.oom.group=1"},
                "devices": {"root": {"type": "disk", "pool": self.config["pool"], "path": "/"},
                            "eth0": {"type": "nic", "network": "ka-computers", "name": "eth0", "ipv4.address": record["address"],
                                     "security.ipv4_filtering": "true", "security.mac_filtering": "true", "security.port_isolation": "true"},
                            "project": {"type": "disk", "source": str(root), "path": "/project"}}})
            record.update(state="provisioning", incusOperation=created["operation"])
            self.state.save_computer(record)
            return False
        if not record.get("configured"):
            cert_root = self.state.root / "certificates" / name
            cert_root.mkdir(parents=True, exist_ok=True, mode=0o700)
            if not (cert_root / "cert.pem").exists():
                subprocess.run(["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", str(cert_root / "key.pem"),
                                "-out", str(cert_root / "request.pem"), "-subj", "/CN=" + name], check=True, capture_output=True, timeout=30)
                (cert_root / "extensions").write_text("subjectAltName=IP:" + record["address"] + "\nextendedKeyUsage=serverAuth\n")
                subprocess.run(["openssl", "x509", "-req", "-in", str(cert_root / "request.pem"), "-CA", self.config["ca"],
                                "-CAkey", self.config["ca_key"], "-set_serial", "0x" + hashlib.sha256(name.encode()).hexdigest()[:30],
                                "-days", "365", "-out", str(cert_root / "cert.pem"), "-extfile", str(cert_root / "extensions")],
                               check=True, capture_output=True, timeout=30)
            session_config = {"listen": "0.0.0.0", "port": 7444, "state": "/home/agent/.local/state/ka",
                              "ca": "/etc/ka/ca.pem", "cert": "/etc/ka/cert.pem", "key": "/etc/ka/key.pem", "peer_name": "ka-broker"}
            for target, data in (("ca.pem", Path(self.config["ca"]).read_bytes()), ("cert.pem", (cert_root / "cert.pem").read_bytes()),
                                 ("key.pem", (cert_root / "key.pem").read_bytes()), ("session.json", json.dumps(session_config).encode())):
                self.incus.push(name, "/etc/ka/" + target, data)
            record["configured"] = True
            self.state.save_computer(record)
        if instance.get("status") != "Running":
            started = self.incus.request("PUT", "/1.0/instances/" + name + "/state", {"action": "start", "timeout": -1})
            record.update(state="starting", incusOperation=started["operation"])
            self.state.save_computer(record)
            return False
        health = self.session(record, "GET", "/health")
        record.update(state="ready", reason="", health=health, lastHeartbeat=time.time())
        self.state.save_computer(record)
        return True

    def run(self):
        while not self.stop.wait(2):
            try:
                self.tick()
                self.state.setting("schedulerError", "")
            except Exception as error:
                try:
                    self.state.setting("schedulerError", type(error).__name__ + ": admissions paused; existing computers preserved")
                except Exception:
                    pass
                self.stop.wait(5)

    def import_profile(self, record):
        if record.get("profileImported"):
            return {"imported": True, "computerID": record["computerID"]}
        if self.session(record, "GET", "/health").get("browser") != "stopped":
            raise ValueError("Close Chromium explicitly before importing its profile")
        source = Path(self.config["projects_root"]) / record["projectID"] / ".klive" / "browser-profiles" / record["agentID"]
        if not source.is_dir() or source.is_symlink():
            raise ValueError("No recoverable profile for this agent in the staged workspace")
        target_version = subprocess.run(["incus", "exec", record["computerID"], "--", "chromium", "--version"],
                                        check=True, capture_output=True, timeout=30).stdout.decode()
        last_version = source / "Last Version"
        if last_version.exists():
            previous = re.search(r"\d+", last_version.read_text())
            current = re.search(r"\d+", target_version)
            if previous and current and int(previous.group()) > int(current.group()):
                raise ValueError("Target Chromium is older than the saved profile; upgrade target browser before import")
        destination = "/home/agent/.config/chromium"
        self.incus.mkdir(record["computerID"], "/home/agent/.config")
        self.incus.mkdir(record["computerID"], destination)
        for file in sorted(source.rglob("*")):
            if file.is_symlink():
                if file.name.startswith("Singleton"):
                    continue
                raise ValueError("Profile contains a symlink; manual recovery required")
            relative = file.relative_to(source).as_posix()
            if file.is_dir():
                self.incus.mkdir(record["computerID"], destination + "/" + relative)
            elif file.is_file() and not file.name.startswith("Singleton"):
                self.incus.push(record["computerID"], destination + "/" + relative, file.read_bytes())
        record["profileImported"] = True
        self.state.save_computer(record)
        return {"imported": True, "computerID": record["computerID"], "loginVerificationRequired": True}

    def __call__(self, method, path, query, body):
        if path == "/health" and method == "GET":
            try:
                pressure = Pressure.read()
                reason = self.admission.reason(pressure) or host_headroom(self.config.get("host_pressure_file"))
                return 200, {"available": True, "provider": "incus", "pressure": pressure.json(), "admissionReason": reason,
                             "queued": len(self.state.list(["queued"])), "computers": len(self.state.computers()),
                             "schedulerError": self.state.setting("schedulerError")}
            except OSError:
                return 200, {"available": True, "admissionReason": "Worker telemetry unavailable; admissions paused"}
        if path == "/computers" and method == "GET":
            records = self.state.computers(query.get("projectID"))
            for record in records:
                contact = self.state.setting("contact:" + record["computerID"]) or {}
                record.update(contact)
                observed = contact.get("healthObservedAt", record.get("lastHeartbeat", 0))
                record["healthStale"] = time.time() - observed > 60
                # Stale telemetry is explicitly unknown, never evidence for replacement.
                if record["healthStale"]:
                    record["health"] = {"session": "unknown", "desktop": "unknown", "browser": "unknown", "terminal": "unknown"}
            return 200, records
        if path == "/computers/ensure" and method == "POST":
            return 202, self.ensure(body)
        if path.startswith("/operations/") and method == "GET":
            return 200, public_operation(self.state.get(path.split("/")[-1]))
        if path.startswith("/migrations/") and method == "GET":
            receipt = self.state.setting("migration:" + identifier(path.split("/")[-1]))
            if not receipt:
                raise KeyError("No verified migration")
            return 200, receipt
        parts = path.strip("/").split("/")
        if len(parts) >= 3 and parts[0] == "computers":
            computer = self.state.computer(parts[1])
            if not computer or computer["projectID"] != query.get("projectID"):
                raise KeyError("Computer does not belong to this project")
            target = "/" + "/".join(parts[2:])
            if target == "/import-browser-profile" and method == "POST":
                with self.lock:
                    return 200, self.import_profile(computer)
            if method == "GET" and target.startswith("/jobs/"):
                queued = self.state.get(parts[3])
                if queued and queued["kind"] == "heavy_job" and queued["request"]["computerID"] == computer["computerID"] and queued["state"] != "completed":
                    return 200, public_operation(queued)
            if target == "/jobs" and method == "POST" and body.get("heavy"):
                op, _ = self.state.submit(body["operationID"], "heavy_job", {"computerID": computer["computerID"], "payload": body})
                return 202, public_operation(op)
            if target not in ("/health", "/frame", "/lease", "/actions", "/jobs") and not target.startswith(("/jobs/", "/operations/")):
                raise KeyError(target)
            return 200, self.session(computer, method, target, body if method != "GET" else None, query)
        if len(parts) == 3 and parts[0] == "workspaces":
            project = identifier(parts[1])
            if parts[2] == "ensure" and method == "POST":
                return 200, self.ensure_workspace(project)
            if parts[2] == "verify-migration" and method == "POST":
                files = body["files"]
                if not isinstance(files, list) or not files:
                    raise ValueError("Nonempty source manifest required")
                for item in files:
                    actual = self.workspace.read(project, item["path"], count=1)
                    if actual["version"] != item["sha256"] or actual["bytes"] != item["bytes"]:
                        raise ValueError("Migrated file does not match source manifest")
                receipt = {"projectID": project, "verified": True, "verifiedAt": time.time(), "fileCount": len(files),
                           "sourceManifestHash": hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest(),
                           "backupSha256": body["backupSha256"], "activated": False}
                self.state.setting("migration:" + project, receipt)
                return 200, receipt
            if parts[2] == "list" and method == "GET":
                return 200, self.workspace.list(project, query.get("path", ""))
            if parts[2] == "file" and method == "GET":
                return 200, self.workspace.read(project, query["path"], query.get("offset", 0), query.get("count", 1024 * 1024))
            if parts[2] == "file" and method == "PUT":
                return 200, self.workspace.write(project, query["path"], body)
            if parts[2] == "mutate" and method == "POST":
                op, new = self.state.submit(body["operationID"], "workspace", {"projectID": project, "path": query["path"], **body})
                if new and self.state.claim(op["id"]):
                    try:
                        self.workspace.mutate(project, body["operation"], query["path"], body.get("destination"), body.get("recursive", False))
                        self.state.update(op["id"], "completed")
                    except Exception:
                        self.state.update(op["id"], "outcome_unknown", {"reason": "Inspect workspace before retrying"})
                        raise
                return 200, public_operation(self.state.get(op["id"]))
        raise KeyError(path)


def main(config):
    os.umask(0o077)
    serve(config, Broker(config))
