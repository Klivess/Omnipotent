#!/usr/bin/env python3
"""Loopback HTTP front door for browser-inspect.py.

Why this exists
---------------
Every structured browser action used to reach the helper through `docker exec`. That is a hijacked
stream over the daemon's named pipe, and on 2026-09-16 a busy host left those attaches hanging: one
`computer_browser_action` sat for 26 minutes against a ~35-second bound, on three projects at once,
because the daemon — not the desktop, not the page — had stopped answering. Every agent's prompt
prefix expired while it waited, and the fleet halted on the resulting cache miss.

Serving the helper over a published loopback port takes the daemon off the hot path entirely. The
container keeps working while Docker is busy, an ordinary HTTP client timeout actually bounds the
call, and the round trip loses a whole exec's worth of latency. The helper itself is untouched and
still spawned per request: it is 2800 lines of stateful CDP work whose one-shot process model is
what keeps a wedged page from poisoning the next action.

Trust boundary
--------------
Identical to VNC's, deliberately. ContainerOrchestrator publishes this to 127.0.0.1 on the Docker
host only, so reaching it already means local access to a box that can inject arbitrary input over
the container's passwordless RFB port. Nothing here widens that. What it must never become is a
general command channel, so the mode is checked against the helper's own allowlist and the payload
is passed as a single opaque base64url argument — never through a shell.
"""
import json
import os
import re
import subprocess
import sys
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HELPER = "/usr/local/bin/browser-inspect.py"
CDP_ROOT = "http://127.0.0.1:9222"
LISTEN_PORT = int(os.environ.get("KLIVE_BROWSER_SERVICE_PORT", "5902"))

# The helper's full surface. Read-only modes take positional argv; action modes take one payload.
ALLOWED_MODES = frozenset((
    "tabs", "dom", "controls", "accessibility", "network", "locate",
    "navigate", "closetabs", "upload", "dialog", "control", "action",
))
# base64url, unpadded — exactly what ContainerToolAdapter.EncodePayload produces.
PAYLOAD_RE = re.compile(r"^[A-Za-z0-9_-]{0,262144}$")
ARG_RE = re.compile(r"^[A-Za-z0-9_.:-]{0,64}$")

# Ceiling on one helper run. Above the helper's own internal waits (a browser op may ask for 120s)
# so this never pre-empts a legitimate action — it only catches a helper that has itself wedged.
HELPER_TIMEOUT_SECONDS = 180


def browser_is_up():
    """Whether Chromium's debugger is answering, which is what 'the browser is running' means here.

    Replaces a `docker exec` that the harness previously ran before EVERY browser action just to
    make sure Chromium was alive. In steady state the answer is always yes, so paying a daemon
    round trip for it was the single most wasteful call on the path.
    """
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(CDP_ROOT + "/json/version", timeout=3) as response:
            return response.status == 200
    except Exception:
        return False


def run_helper(mode, payload, args):
    command = [sys.executable, HELPER, mode]
    if payload:
        command.append(payload)
    else:
        command.extend(args)
    try:
        finished = subprocess.run(
            command, capture_output=True, text=True, timeout=HELPER_TIMEOUT_SECONDS,
            # The helper needs the session's display; systemd-less containers inherit it from the
            # entrypoint, but be explicit so a restart of this service alone cannot lose it.
            env={**os.environ, "DISPLAY": os.environ.get("DISPLAY", ":1")},
        )
        return {"exitCode": finished.returncode, "stdout": finished.stdout, "stderr": finished.stderr}
    except subprocess.TimeoutExpired as expired:
        return {
            "exitCode": 124,
            "stdout": (expired.stdout or b"").decode("utf-8", "replace") if isinstance(expired.stdout, bytes) else (expired.stdout or ""),
            "stderr": "browser-inspect.py exceeded %ds and was killed." % HELPER_TIMEOUT_SECONDS,
        }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass  # one line per browser action would drown the container log

    def _reply(self, status, body):
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if self.path != "/health":
            self._reply(404, {"error": "not found"})
            return
        self._reply(200, {"ok": True, "browserUp": browser_is_up()})

    def do_POST(self):
        if self.path != "/run":
            self._reply(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            self._reply(400, {"error": "bad Content-Length"})
            return
        if length <= 0 or length > 4 * 1024 * 1024:
            self._reply(400, {"error": "request body must be 1..4MiB"})
            return
        try:
            request = json.loads(self.rfile.read(length).decode("utf-8", "replace"))
        except ValueError:
            self._reply(400, {"error": "body must be JSON"})
            return

        mode = request.get("mode") or ""
        payload = request.get("payload") or ""
        args = request.get("args") or []
        if mode not in ALLOWED_MODES:
            self._reply(400, {"error": "unsupported mode"})
            return
        if not PAYLOAD_RE.match(payload):
            self._reply(400, {"error": "payload must be unpadded base64url"})
            return
        if not isinstance(args, list) or len(args) > 4 or not all(
                isinstance(x, str) and ARG_RE.match(x) for x in args):
            self._reply(400, {"error": "args must be up to 4 short scalar strings"})
            return

        self._reply(200, run_helper(mode, payload, [str(x) for x in args]))


def main():
    # Binds the container's own interfaces, NOT its loopback: a published port arrives over the
    # bridge, so a 127.0.0.1 bind in here is unreachable from the host no matter how it is mapped.
    # Confinement is the HOST-side binding, which ContainerOrchestrator pins to 127.0.0.1 — exactly
    # how the passwordless VNC port is already handled.
    #
    # Threaded: an agent's action and the harness's readiness probe must not queue behind each
    # other, or the probe reports a wedged desktop whenever one is simply busy.
    server = ThreadingHTTPServer(("0.0.0.0", LISTEN_PORT), Handler)
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    main()
