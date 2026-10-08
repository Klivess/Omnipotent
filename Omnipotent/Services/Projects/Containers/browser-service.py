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

HELPER = os.environ.get("KLIVE_BROWSER_HELPER", "/usr/local/bin/browser-inspect.py")
CDP_ROOT = os.environ.get("KLIVE_CDP_ROOT", "http://127.0.0.1:9222")
LISTEN_PORT = int(os.environ.get("KLIVE_BROWSER_SERVICE_PORT", "5902"))
MAX_BODY_BYTES = 4 * 1024 * 1024

# The helper's full surface. Read-only modes take positional argv; action modes take one payload.
# preflight/receipt verify that pointer and keyboard input actually reached the page; cdp is the
# agent-facing DevTools tool. All three are ordinary helper modes with the same payload contract.
ALLOWED_MODES = frozenset((
    "tabs", "dom", "controls", "accessibility", "network", "locate",
    "navigate", "closetabs", "upload", "dialog", "control", "action",
    "preflight", "receipt", "cdp",
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

    def _read_body(self):
        """The request body, from either framing an HTTP/1.1 client may choose.

        The harness sends Content-Length. Chunked transfer encoding is accepted as well because
        .NET's JSON helpers stream with it by default, and refusing it is exactly how every request
        from the C# client came to be rejected with 400 until October 2026. Returns None when the
        body is missing, malformed or over the size limit.
        """
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            body = bytearray()
            while True:
                size_line = self.rfile.readline(64)
                try:
                    size = int(size_line.split(b";", 1)[0].strip() or b"0", 16)
                except ValueError:
                    return None
                if size == 0:
                    # Trailer section ends with an empty line.
                    while self.rfile.readline(1024) not in (b"\r\n", b"\n", b""):
                        pass
                    break
                if len(body) + size > MAX_BODY_BYTES:
                    return None
                body += self.rfile.read(size)
                self.rfile.readline(4)  # CRLF after each chunk
            return bytes(body) if body else None
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            return None
        if length <= 0 or length > MAX_BODY_BYTES:
            return None
        return self.rfile.read(length)

    def do_POST(self):
        if self.path != "/run":
            self._reply(404, {"error": "not found"})
            return
        raw = self._read_body()
        if raw is None:
            self._reply(400, {"error": "request body must be 1..4MiB of JSON (Content-Length or chunked)"})
            return
        try:
            request = json.loads(raw.decode("utf-8", "replace"))
        except ValueError:
            self._reply(400, {"error": "body must be JSON"})
            return
        if not isinstance(request, dict):
            self._reply(400, {"error": "body must be a JSON object"})
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
