"""A job runner is a separate systemd unit, not a child owned by the HTTP service."""
import base64
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time

from .state import State, identifier, public_operation


def boot_id():
    return Path("/proc/sys/kernel/random/boot_id").read_text().strip()


class Jobs:
    def __init__(self, state):
        self.state = state

    def submit(self, payload):
        job_id = identifier(payload["operationID"])
        request = {"command": payload.get("command", ""), "cwd": payload.get("cwd") or "/project",
                   "interactive": bool(payload.get("interactive", False))}
        if not request["command"] and not request["interactive"]:
            raise ValueError("command or interactive:true is required")
        op, _ = self.state.submit(job_id, "terminal", request)
        if op["state"] == "queued":
            # Unit identity prevents duplicate launch even when submission/acknowledgement races.
            subprocess.run(["sudo", "-n", "systemd-run", "--quiet", "--collect",
                            "--unit=ka-job-" + job_id, "--property=User=agent",
                            "--property=KillMode=control-group", "--property=OOMPolicy=continue",
                            "--property=WorkingDirectory=/home/agent", "--setenv=DISPLAY=:1",
                            "--setenv=PYTHONPATH=/opt/ka",
                            "--setenv=XAUTHORITY=/home/agent/.Xauthority",
                            "--setenv=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus",
                            sys.executable, "-m", "ka_worker.jobs", str(self.state.root), job_id],
                           capture_output=True, timeout=15, check=False)
        return public_operation(self.state.get(job_id))

    def get(self, job_id, cursor=0):
        op = self.state.get(job_id)
        if not op or op["kind"] != "terminal":
            raise KeyError(job_id)
        if op["state"] == "running" and "bootID" in op["result"] and op["result"]["bootID"] != boot_id():
            self.state.update(job_id, "interrupted", {"reason": "Computer rebooted; command was not replayed"})
            op = self.state.get(job_id)
        result = public_operation(op)
        output = self.state.root / (identifier(job_id) + ".output")
        cursor = max(0, int(cursor))
        data = b""
        if output.exists():
            with output.open("rb") as stream:
                stream.seek(cursor)
                data = stream.read(256 * 1024)
        result.update(output=data.decode("utf-8", errors="replace"), outputBase64=base64.b64encode(data).decode(),
                      cursor=cursor + len(data))
        return result

    def reconcile(self):
        for op in self.state.list(["queued", "running"]):
            if op["kind"] != "terminal":
                continue
            if op["state"] == "queued":
                self.submit({"operationID": op["id"], **op["request"]})
            elif op["result"].get("bootID") and op["result"]["bootID"] != boot_id():
                self.state.update(op["id"], "interrupted", {"reason": "Computer rebooted; command not replayed"})
            elif time.time() - op["updated"] > 30:
                probe = subprocess.run(["systemctl", "show", "ka-job-" + op["id"], "--property=LoadState", "--value"],
                                       capture_output=True, timeout=10, check=False)
                if probe.returncode == 0 and probe.stdout.strip() == b"not-found":
                    self.state.update(op["id"], "outcome_unknown", {"reason": "Job unit disappeared without an exit record; command not replayed"})

    def input(self, job_id, payload):
        op = self.state.get(job_id)
        if not op or op["kind"] != "terminal" or op["state"] != "running":
            raise ValueError("Job is not running")
        request_id = identifier(payload["operationID"])
        request = {"jobID": job_id, "text": payload.get("text", ""), "cancel": bool(payload.get("cancel", False))}
        op, new = self.state.submit(request_id, "terminal_input", request)
        if not new:
            return public_operation(op)
        self.state.claim(request_id)
        try:
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(10)
                client.connect(str(self.state.root / (identifier(job_id) + ".sock")))
                client.sendall(json.dumps(request).encode() + b"\n")
                if client.recv(32) != b"OK":
                    raise OSError("Input acknowledgment absent")
            self.state.update(request_id, "completed")
        except Exception:
            self.state.update(request_id, "outcome_unknown", {"reason": "Input may have reached the terminal. Do not replay."})
        return public_operation(self.state.get(request_id))


def run(root, job_id):
    # Imports deliberately local: state/admission tests also run on Windows.
    import fcntl
    import pty
    import selectors
    import termios
    state = State(root)
    identifier(job_id)
    lock = (state.root / (job_id + ".lock")).open("a")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        return
    if not state.claim(job_id):
        return
    op = state.get(job_id)
    process = None
    try:
        master, slave = pty.openpty()
        command = ["/bin/bash", "-l"] if op["request"]["interactive"] else ["/bin/bash", "-lc", op["request"]["command"]]
        def controlling_terminal():
            os.setsid()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)
        process = subprocess.Popen(command, cwd=op["request"]["cwd"], stdin=slave, stdout=slave, stderr=slave,
                                   preexec_fn=controlling_terminal, env={**os.environ, "TERM": "xterm-256color", "DISPLAY": ":1"})
        os.close(slave)
        state.update(job_id, "running", {"pid": process.pid, "bootID": boot_id(), "interactive": op["request"]["interactive"]})
        endpoint = state.root / (job_id + ".sock")
        endpoint.unlink(missing_ok=True)
        cancelled = False
        with socket.socket(socket.AF_UNIX) as listener, (state.root / (job_id + ".output")).open("ab", buffering=0) as output:
            listener.bind(str(endpoint))
            os.chmod(endpoint, 0o600)
            listener.listen(8)
            selector = selectors.DefaultSelector()
            selector.register(listener, selectors.EVENT_READ)
            selector.register(master, selectors.EVENT_READ)
            while True:
                for key, _ in selector.select(0.2):
                    if key.fileobj == listener:
                        client, _ = listener.accept()
                        with client:
                            client.settimeout(5)
                            message = bytearray()
                            while not message.endswith(b"\n") and len(message) < 1048576:
                                part = client.recv(4096)
                                if not part:
                                    break
                                message.extend(part)
                            req = json.loads(message)
                            if req.get("cancel"):
                                cancelled = True
                                os.killpg(process.pid, signal.SIGTERM)
                            else:
                                os.write(master, req.get("text", "").encode())
                            client.sendall(b"OK")
                    else:
                        try:
                            data = os.read(master, 65536)
                        except OSError:
                            data = b""
                        if data:
                            output.write(data)
                        else:
                            selector.unregister(master)
                if process.poll() is not None:
                    # Read everything still buffered before persisting final status.
                    os.set_blocking(master, False)
                    while True:
                        try:
                            data = os.read(master, 65536)
                            if not data:
                                break
                            output.write(data)
                        except OSError:
                            break
                    os.fsync(output.fileno())
                    break
        state.update(job_id, "interrupted" if cancelled else "completed" if process.returncode == 0 else "failed",
                     {"exitCode": process.returncode, "reason": "Explicit cancellation" if cancelled else "Process exited"})
        os.close(master)
        endpoint.unlink(missing_ok=True)
    except Exception as error:
        state.update(job_id, "outcome_unknown" if process else "failed", {"reason": type(error).__name__})


if __name__ == "__main__":
    run(sys.argv[1], sys.argv[2])
