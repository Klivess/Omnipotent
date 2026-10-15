#!/usr/bin/env python3
"""klive-computerd — the agent computer's in-container control plane.

Served as /usr/local/bin/browser-service.py on purpose. Desktops are never recreated to pick up a
new image (that would erase what their agents installed), but the harness refreshes these helper
files in place, and every entrypoint ever shipped already supervises a process of this name and
restarts it within seconds. Keeping the filename is what lets a computer created months ago gain
this whole control plane on the next tool call, with no rebuild and no lost state.

Why one daemon
--------------
The harness used to reach the desktop through four separate channels: the Docker daemon for
lifecycle, `docker exec` for every shell command, readiness probe, browser launch, window list
and clipboard read, a VNC socket for pixels and input, and this service for structured browser
work. `docker exec` is a hijacked stream over the daemon's named pipe; on a busy Windows host it
stalls for minutes and ignores its cancellation token (2026-09-16: one browser action sat for 26
minutes on three projects at once). Every one of those calls is now an HTTP request to a process
already running inside the container, which answers whatever the daemon is doing and whose client
timeout is real. `docker exec` remains only as the fallback for a container whose daemon is down.

Surface
-------
  GET  /health            public. Superset of the original {ok, browserUp}: every desktop
                          component, browser state, cgroup memory + OOM kills, recent events.
  POST /run               public. The browser-inspect.py front door, unchanged contract.
  POST /exec              token. Bounded command execution as the desktop user.
  POST /input             token. Pointer/keyboard events through XTEST (xdotool).
  GET  /screenshot        token. The X display as jpeg/png/raw BGRA, captured locally.
  GET  /clipboard         token. The X clipboard text (POST sets it).
  GET  /windows           token. Mapped windows + the active one.
  GET  /browser/state     token. Open tabs, last-known tabs, browser watchdog state.
  GET  /diag              token. Log tails, top memory consumers, the event journal.

Trust boundary
--------------
ContainerOrchestrator publishes this port to 127.0.0.1 on the Docker host only — the same
confinement as the container's passwordless VNC port, which can already type anything into a
terminal. The /run surface keeps its strict allowlist. Everything that executes or injects input
additionally needs the per-container token the harness writes to /etc/klive-computerd.token (or the
KLIVE_COMPUTERD_TOKEN environment variable); with no token configured those routes refuse, and the
harness falls back to the paths it used before this daemon existed.

Stdlib only, and importable on Windows: the harness's own test-suite runs this file directly.
"""
import base64
import collections
import hmac
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SERVICE_NAME = "klive-computerd"
# Bumped when the HTTP surface changes. The harness reads it from /health to decide which routes it
# may use, so an older daemon that a refresh has not reached yet is never asked for a route it lacks.
PROTOCOL = 2

HELPER = os.environ.get("KLIVE_BROWSER_HELPER", "/usr/local/bin/browser-inspect.py")
CDP_ROOT = os.environ.get("KLIVE_CDP_ROOT", "http://127.0.0.1:9222")
LISTEN_PORT = int(os.environ.get("KLIVE_BROWSER_SERVICE_PORT", "5902"))
DISPLAY = os.environ.get("DISPLAY") or ":1"
ACTIVE_TAB_FILE = os.environ.get("KLIVE_ACTIVE_TAB_FILE", "/tmp/klive-active-tab")
MAX_BODY_BYTES = 4 * 1024 * 1024
LINUX = sys.platform.startswith("linux")
STARTED = time.time()

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
ENV_NAME_RE = re.compile(r"^[A-Z_][A-Z0-9_]{0,63}$")

# Ceiling on one helper run. Above the helper's own internal waits (a browser op may ask for 120s)
# so this never pre-empts a legitimate action — it only catches a helper that has itself wedged.
HELPER_TIMEOUT_SECONDS = 180

# How execs are bounded. "timeout" wraps the command in GNU timeout exactly like the docker-exec
# path did, so a command's own children die with it. "none" exists for the harness's Windows tests.
EXEC_WRAPPER = os.environ.get("KLIVE_COMPUTERD_EXEC_WRAPPER", "timeout")
SESSION_PREFIX = "if [ -r /tmp/desktop-session.env ]; then . /tmp/desktop-session.env; fi; "

# A browser whose debugger has not answered for this long, while its process is alive, is wedged
# rather than busy: the debugger answers from the browser process's IO thread, which keeps
# responding through heavy page work. Ninety seconds is far beyond any legitimate stall.
WATCHDOG_INTERVAL_SECONDS = float(os.environ.get("KLIVE_COMPUTERD_WATCHDOG_SECONDS", "15"))
WATCHDOG_HUNG_CHECKS = int(os.environ.get("KLIVE_COMPUTERD_HUNG_CHECKS", "6"))
# How long past its own timeout an exec may run before the daemon kills its process group itself.
EXEC_GRACE_SECONDS = float(os.environ.get("KLIVE_COMPUTERD_EXEC_GRACE", "15"))


# ── event journal ─────────────────────────────────────────────────────────────────────────────
# Notable things that happened to this computer while nobody was looking: the browser crashed or
# was killed as wedged, the kernel OOM-killed a process, a desktop component went missing. The
# harness surfaces these to the agent ("your browser was restarted at 12:01 after an OOM kill")
# instead of letting it discover a vanished tab by failing at it.

_events = collections.deque(maxlen=60)
_events_lock = threading.Lock()
_event_seq = [0]


def record_event(kind, message, **details):
    with _events_lock:
        _event_seq[0] += 1
        _events.append({
            "seq": _event_seq[0], "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "kind": kind, "message": message, "details": details,
        })


def recent_events(since=0, limit=20):
    with _events_lock:
        items = [item for item in _events if item["seq"] > since]
    return items[-limit:]


# ── authentication ────────────────────────────────────────────────────────────────────────────

def _token_paths():
    paths = []
    explicit = os.environ.get("KLIVE_COMPUTERD_TOKEN_FILE")
    if explicit:
        paths.append(explicit)
    # Where the harness delivers it (ComputerDaemonToken.ContainerTokenPath), through the Docker
    # archive API as root:0644 — readable by the daemon whichever user the entrypoint runs it as.
    paths.append("/etc/klive-computerd.token")
    paths.append(os.path.join(os.path.expanduser("~"), ".klive", "computerd.token"))
    paths.append("/home/agent/.klive/computerd.token")
    seen = set()
    unique = []
    for path in paths:
        if path not in seen:
            seen.add(path)
            unique.append(path)
    return unique


_token_cache = {}
_token_lock = threading.Lock()


def configured_tokens():
    """Every token that currently authorises a privileged call.

    Read on demand (with an mtime cache) rather than once at start, because the harness writes the
    file into a running container: a token rotated by a refresh takes effect on the next request.
    """
    tokens = []
    from_env = (os.environ.get("KLIVE_COMPUTERD_TOKEN") or "").strip()
    if from_env:
        tokens.append(from_env)
    for path in _token_paths():
        try:
            stat = os.stat(path)
        except OSError:
            continue
        key = (stat.st_mtime_ns, stat.st_size)
        with _token_lock:
            cached = _token_cache.get(path)
            if cached and cached[0] == key:
                value = cached[1]
            else:
                try:
                    with open(path, "r", encoding="utf-8") as handle:
                        value = handle.read().strip()
                except OSError:
                    value = ""
                _token_cache[path] = (key, value)
        if value and value not in tokens:
            tokens.append(value)
    return tokens


def check_token(headers):
    tokens = configured_tokens()
    if not tokens:
        return "no-token-configured"
    presented = (headers.get("X-Klive-Token") or "").strip()
    authorization = (headers.get("Authorization") or "").strip()
    if not presented and authorization.lower().startswith("bearer "):
        presented = authorization[7:].strip()
    if not presented:
        return "token-required"
    for token in tokens:
        if hmac.compare_digest(presented.encode("utf-8"), token.encode("utf-8")):
            return None
    return "token-rejected"


# ── probes (no subprocess on the health path) ─────────────────────────────────────────────────

def process_table():
    """{comm: [pid, …]} from /proc. Health must stay cheap enough to poll every few seconds, so it
    reads the kernel's own table instead of spawning pgrep once per component."""
    table = {}
    if not LINUX:
        return table
    try:
        entries = os.listdir("/proc")
    except OSError:
        return table
    for entry in entries:
        if not entry.isdigit():
            continue
        try:
            with open("/proc/%s/comm" % entry, "r") as handle:
                comm = handle.read().strip()
        except OSError:
            continue
        table.setdefault(comm, []).append(int(entry))
    return table


def display_socket_path():
    number = DISPLAY.split(":")[-1].split(".")[0] or "1"
    return "/tmp/.X11-unix/X%s" % number


def display_answers():
    if not LINUX:
        return False
    path = display_socket_path()
    if not os.path.exists(path):
        return False
    probe = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    probe.settimeout(1.0)
    try:
        probe.connect(path)
        return True
    except OSError:
        return False
    finally:
        probe.close()


def _read(path):
    try:
        with open(path, "r") as handle:
            return handle.read().strip()
    except OSError:
        return None


def _read_int(path):
    value = _read(path)
    if value is None or value == "max":
        return None
    try:
        return int(value.split()[0])
    except (ValueError, IndexError):
        return None


def _key_values(path):
    found = {}
    text = _read(path) or ""
    for line in text.splitlines():
        parts = line.split()
        if len(parts) >= 2:
            try:
                found[parts[0].rstrip(":")] = int(parts[1])
            except ValueError:
                pass
    return found


def memory_status():
    """Container memory against its own ceiling, and how many processes the kernel has killed.

    cgroup v2 first, then v1 (Docker Desktop releases from 2021 still mount v1). The OOM-kill
    count is the single most useful number here: a browser that "crashed" with it rising was
    killed for memory, and the remedy is fewer tabs, not a retry.
    """
    status = {"limitMB": None, "usedMB": None, "availableMB": None, "oomKills": None, "cgroup": None}
    limit = _read_int("/sys/fs/cgroup/memory.max")
    current = _read_int("/sys/fs/cgroup/memory.current")
    if current is not None:
        status["cgroup"] = "v2"
        events = _key_values("/sys/fs/cgroup/memory.events")
        status["oomKills"] = events.get("oom_kill")
    else:
        limit = _read_int("/sys/fs/cgroup/memory/memory.limit_in_bytes")
        current = _read_int("/sys/fs/cgroup/memory/memory.usage_in_bytes")
        if current is not None:
            status["cgroup"] = "v1"
            control = _key_values("/sys/fs/cgroup/memory/memory.oom_control")
            status["oomKills"] = control.get("oom_kill")
            # v1 reports an unlimited ceiling as a huge sentinel, not "max".
            if limit is not None and limit >= (1 << 60):
                limit = None
    meminfo = _key_values("/proc/meminfo")
    total_kb = meminfo.get("MemTotal")
    available_kb = meminfo.get("MemAvailable")
    if limit is not None:
        status["limitMB"] = round(limit / 1048576.0, 1)
    elif total_kb:
        status["limitMB"] = round(total_kb / 1024.0, 1)
    if current is not None:
        status["usedMB"] = round(current / 1048576.0, 1)
    if limit is not None and current is not None:
        status["availableMB"] = round(max(0, limit - current) / 1048576.0, 1)
    elif available_kb:
        status["availableMB"] = round(available_kb / 1024.0, 1)
    return status


def load_status():
    load = _read("/proc/loadavg")
    values = {"load1": None, "load5": None, "cpus": os.cpu_count()}
    if load:
        parts = load.split()
        try:
            values["load1"] = float(parts[0])
            values["load5"] = float(parts[1])
        except (ValueError, IndexError):
            pass
    return values


def disk_status():
    found = {}
    for label, path in (("root", "/"), ("project", "/project")):
        try:
            usage = shutil.disk_usage(path)
            found[label + "FreeMB"] = round(usage.free / 1048576.0)
        except OSError:
            pass
    return found


def screen_size():
    try:
        return int(os.environ.get("DISPLAY_WIDTH") or 1920), int(os.environ.get("DISPLAY_HEIGHT") or 1080)
    except ValueError:
        return 1920, 1080


def cdp_json(path, timeout=3):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(CDP_ROOT + path, timeout=timeout) as response:
        body = response.read().decode("utf-8", "replace")
    return json.loads(body) if body.strip() else {}


def browser_is_up(timeout=3):
    """Whether Chromium's debugger is answering, which is what 'the browser is running' means here.

    Replaces a `docker exec` that the harness previously ran before EVERY browser action just to
    make sure Chromium was alive. In steady state the answer is always yes, so paying a daemon
    round trip for it was the single most wasteful call on the path.
    """
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(CDP_ROOT + "/json/version", timeout=timeout) as response:
            return response.status == 200
    except Exception:
        return False


def page_targets(timeout=3):
    pages = []
    for item in cdp_json("/json/list", timeout) or []:
        if isinstance(item, dict) and item.get("type") == "page":
            pages.append({"id": item.get("id"), "url": (item.get("url") or "")[:1200],
                          "title": (item.get("title") or "")[:300]})
    return pages


def which(tool):
    return shutil.which(tool) is not None


_image = {"value": None}


def image_stamp():
    """The image's baked capability stamp (/etc/klive-desktop.json), read once."""
    if _image["value"] is None:
        stamp = {"version": "", "capabilities": []}
        try:
            with open("/etc/klive-desktop.json", "r", encoding="utf-8") as handle:
                parsed = json.load(handle)
            stamp["version"] = str(parsed.get("imageVersion") or "")
            stamp["capabilities"] = [str(x) for x in (parsed.get("capabilities") or [])][:64]
        except (OSError, ValueError, AttributeError):
            pass
        _image["value"] = stamp
    return _image["value"]


_pillow = {"checked": False, "available": False}


def pillow_available():
    if not _pillow["checked"]:
        _pillow["checked"] = True
        try:
            from PIL import ImageGrab  # noqa: F401
            _pillow["available"] = True
        except Exception:
            _pillow["available"] = False
    return _pillow["available"]


def tools_status():
    return {
        "xdotool": which("xdotool"), "ffmpeg": which("ffmpeg"), "xclip": which("xclip"),
        "wmctrl": which("wmctrl"), "timeout": which("timeout"), "pillow": pillow_available(),
    }


# ── browser watchdog ──────────────────────────────────────────────────────────────────────────

class BrowserWatch(object):
    """Notices what the agent would otherwise discover by failing: a browser that crashed, was
    OOM-killed, or has wedged with its debugger silent. A wedged browser is killed so the next
    harness action relaunches it cleanly; the tabs it had open are kept for the harness to report."""

    def __init__(self):
        self.lock = threading.Lock()
        self.cdp_failures = 0
        self.last_up = None
        self.last_tabs = []
        self.last_tabs_at = None
        self.was_running = False
        self.restarts = 0
        self.last_oom = None
        self.killed_at = None

    def snapshot(self):
        with self.lock:
            return {
                "consecutiveCdpFailures": self.cdp_failures,
                "lastResponsiveAt": self.last_up,
                "lastKnownTabs": list(self.last_tabs),
                "lastKnownTabsAt": self.last_tabs_at,
                "hungKills": self.restarts,
                "lastHungKillAt": self.killed_at,
            }

    def tick(self):
        table = process_table()
        chromium = table.get("chromium", [])
        memory = memory_status()
        oom = memory.get("oomKills")
        with self.lock:
            if oom is not None:
                if self.last_oom is not None and oom > self.last_oom:
                    record_event("oom-kill", "The kernel killed %d process(es) for memory in this desktop."
                                 % (oom - self.last_oom), usedMB=memory.get("usedMB"), limitMB=memory.get("limitMB"))
                self.last_oom = oom
            if not chromium:
                if self.was_running:
                    record_event("browser-exited", "Chromium is no longer running (crashed, killed or closed).",
                                 lastKnownTabs=list(self.last_tabs))
                self.was_running = False
                self.cdp_failures = 0
                return
            self.was_running = True
        up = browser_is_up(timeout=4)
        tabs = None
        if up:
            try:
                tabs = page_targets(timeout=4)
            except Exception:
                tabs = None
        with self.lock:
            if up:
                self.cdp_failures = 0
                self.last_up = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                if tabs is not None:
                    self.last_tabs = tabs[:12]
                    self.last_tabs_at = self.last_up
                return
            self.cdp_failures += 1
            failures = self.cdp_failures
            known = list(self.last_tabs)
        if failures < WATCHDOG_HUNG_CHECKS:
            return
        for pid in chromium:
            try:
                os.kill(pid, signal.SIGKILL)
            except (OSError, AttributeError):
                pass
        with self.lock:
            self.cdp_failures = 0
            self.restarts += 1
            self.killed_at = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            self.was_running = False
        record_event("browser-hung-killed",
                     "Chromium's debugger was silent for about %ds while its process stayed alive, so it was "
                     "killed; the next browser action relaunches it." % int(failures * WATCHDOG_INTERVAL_SECONDS),
                     lastKnownTabs=known)


WATCH = BrowserWatch()


def watchdog_loop():
    while True:
        time.sleep(WATCHDOG_INTERVAL_SECONDS)
        try:
            WATCH.tick()
        except Exception as error:  # the watchdog must outlive anything it observes
            record_event("watchdog-error", "Browser watchdog tick failed: %s" % str(error)[:300])


# ── health ────────────────────────────────────────────────────────────────────────────────────

_health_cache = {"at": 0.0, "value": None}
_health_lock = threading.Lock()


def build_health():
    table = process_table()

    def running(name):
        return len(table.get(name, [])) > 0

    browser_process = running("chromium")
    cdp = browser_is_up(timeout=2) if (browser_process or not LINUX) else False
    tabs = None
    if cdp:
        try:
            tabs = len(page_targets(timeout=2))
        except Exception:
            tabs = None
    width, height = screen_size()
    components = {
        "display": display_answers() if LINUX else False,
        "xvfb": running("Xvfb"),
        "x11vnc": running("x11vnc"),
        "windowManager": running("xfwm4"),
        "panel": running("xfce4-panel"),
        "session": running("xfce4-session"),
        "desktop": running("xfdesktop"),
        "browserProcess": browser_process,
        "browserCdp": cdp,
    }
    resources = {"memory": memory_status()}
    resources.update(load_status())
    resources.update(disk_status())
    return {
        "ok": True,
        "browserUp": cdp,
        "service": SERVICE_NAME,
        "protocol": PROTOCOL,
        "uptimeSeconds": int(time.time() - STARTED),
        "display": DISPLAY,
        "screen": {"width": width, "height": height},
        "components": components,
        "browser": {"process": browser_process, "cdp": cdp, "tabs": tabs,
                    "processCount": len(table.get("chromium", [])), "watch": WATCH.snapshot()},
        "resources": resources,
        "tools": tools_status(),
        "image": image_stamp(),
        "capabilities": ["run", "exec", "input", "screenshot", "clipboard", "windows", "browser-state", "diag"],
        "authConfigured": bool(configured_tokens()),
        "events": recent_events(limit=12),
    }


def health():
    now = time.time()
    with _health_lock:
        if _health_cache["value"] is not None and now - _health_cache["at"] < 1.0:
            return _health_cache["value"]
    value = build_health()
    with _health_lock:
        _health_cache["value"] = value
        _health_cache["at"] = time.time()
    return value


# ── browser helper front door (unchanged contract) ────────────────────────────────────────────

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
            env={**os.environ, "DISPLAY": DISPLAY},
        )
        return {"exitCode": finished.returncode, "stdout": finished.stdout, "stderr": finished.stderr}
    except subprocess.TimeoutExpired as expired:
        return {
            "exitCode": 124,
            "stdout": (expired.stdout or b"").decode("utf-8", "replace") if isinstance(expired.stdout, bytes) else (expired.stdout or ""),
            "stderr": "browser-inspect.py exceeded %ds and was killed." % HELPER_TIMEOUT_SECONDS,
        }


# ── exec ──────────────────────────────────────────────────────────────────────────────────────

class BoundedReader(threading.Thread):
    """Drains one pipe to EOF, keeping only the first `limit` bytes. Draining matters: a command that
    fills an unread pipe blocks forever, which would look exactly like the hangs this replaces."""

    def __init__(self, pipe, limit):
        threading.Thread.__init__(self)
        self.daemon = True
        self.pipe = pipe
        self.limit = limit
        self.chunks = []
        self.kept = 0
        self.truncated = False

    def run(self):
        try:
            while True:
                chunk = self.pipe.read(65536)
                if not chunk:
                    break
                if self.kept < self.limit:
                    keep = chunk[:self.limit - self.kept]
                    self.chunks.append(keep)
                    self.kept += len(keep)
                    if len(keep) < len(chunk):
                        self.truncated = True
                else:
                    self.truncated = True
        except Exception:
            pass
        finally:
            try:
                self.pipe.close()
            except Exception:
                pass

    def text(self):
        return b"".join(self.chunks).decode("utf-8", "replace")


def _clamp(value, low, high, default):
    try:
        number = int(value)
    except (TypeError, ValueError):
        return default
    return max(low, min(high, number))


def _kill_tree(process):
    try:
        if LINUX:
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
    except Exception:
        try:
            process.kill()
        except Exception:
            pass


def run_exec(request):
    """Runs one command as the desktop user and returns its bounded output.

    `command` is a Bash string run exactly as the docker-exec path ran it (session environment
    sourced, login shell, GNU timeout owning the process group). `argv` runs a program directly with
    no shell, which is how fixed scripts such as the browser launcher are passed without quoting.
    """
    timeout_seconds = _clamp(request.get("timeoutSeconds"), 1, 900, 120)
    max_output = _clamp(request.get("maxOutputBytes"), 1024, 4 * 1024 * 1024, 64 * 1024)
    argv = request.get("argv")
    command = request.get("command")
    if argv is not None:
        if (not isinstance(argv, list) or not argv or len(argv) > 256
                or not all(isinstance(x, str) and len(x) <= 131072 and "\x00" not in x for x in argv)):
            return 400, {"error": "argv must be 1..256 strings without NUL bytes"}
        inner = list(argv)
    elif isinstance(command, str) and command.strip():
        if len(command) > 65536 or "\x00" in command:
            return 400, {"error": "command must be at most 65,536 characters without NUL bytes"}
        inner = ["bash", "-lc", SESSION_PREFIX + 'exec bash -lc "$1"', "desktop-terminal", command]
    else:
        return 400, {"error": "provide command (string) or argv (list)"}

    wrapped = EXEC_WRAPPER == "timeout" and which("timeout")
    full = (["timeout", "--signal=TERM", "--kill-after=5s", "%ds" % timeout_seconds] + inner) if wrapped else inner

    cwd = request.get("cwd") or ("/project" if os.path.isdir("/project") else os.path.expanduser("~"))
    if not isinstance(cwd, str) or "\x00" in cwd:
        return 400, {"error": "cwd must be a path"}
    if not os.path.isdir(cwd):
        cwd = os.path.expanduser("~")

    environment = dict(os.environ)
    environment["DISPLAY"] = DISPLAY
    extra = request.get("env") or {}
    if isinstance(extra, dict):
        for key, value in list(extra.items())[:32]:
            if isinstance(key, str) and ENV_NAME_RE.match(key) and isinstance(value, str) and len(value) <= 8192:
                environment[key] = value

    stdin_text = request.get("stdin")
    started = time.time()
    try:
        process = subprocess.Popen(
            full, cwd=cwd, env=environment,
            stdin=subprocess.PIPE if isinstance(stdin_text, str) else subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            start_new_session=LINUX,
        )
    except FileNotFoundError as missing:
        return 200, {"exitCode": 127, "stdout": "", "stderr": "Executable not found: %s" % missing,
                     "timedOut": False, "truncated": False, "durationMs": 0}
    except OSError as error:
        return 200, {"exitCode": 126, "stdout": "", "stderr": "Could not start the command: %s" % error,
                     "timedOut": False, "truncated": False, "durationMs": 0}

    out = BoundedReader(process.stdout, max_output)
    err = BoundedReader(process.stderr, max_output)
    out.start()
    err.start()
    if isinstance(stdin_text, str):
        try:
            process.stdin.write(stdin_text.encode("utf-8")[:MAX_BODY_BYTES])
            process.stdin.close()
        except Exception:
            pass
    hard_timeout = False
    try:
        # GNU timeout ends the command first; this is the backstop for a wrapper that did not.
        process.wait(timeout=timeout_seconds + EXEC_GRACE_SECONDS)
    except subprocess.TimeoutExpired:
        hard_timeout = True
        _kill_tree(process)
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
    out.join(5)
    err.join(5)
    code = process.returncode if process.returncode is not None else 137
    timed_out = hard_timeout or (wrapped and code in (124, 137))
    return 200, {
        "exitCode": code, "stdout": out.text(), "stderr": err.text(),
        "timedOut": bool(timed_out), "truncated": out.truncated or err.truncated,
        "durationMs": int((time.time() - started) * 1000),
    }


def run_simple(args, timeout=10, stdin=None):
    environment = dict(os.environ)
    environment["DISPLAY"] = DISPLAY
    try:
        finished = subprocess.run(args, input=stdin, capture_output=True, timeout=timeout, env=environment)
        return finished.returncode, finished.stdout, finished.stderr
    except subprocess.TimeoutExpired:
        return 124, b"", b"timed out"
    except OSError as error:
        return 127, b"", str(error).encode("utf-8", "replace")


# ── input ─────────────────────────────────────────────────────────────────────────────────────

KEY_NAMES = {
    "enter": "Return", "return": "Return", "tab": "Tab", "escape": "Escape", "esc": "Escape",
    "backspace": "BackSpace", "delete": "Delete", "del": "Delete", "insert": "Insert",
    "home": "Home", "end": "End", "pageup": "Prior", "page_up": "Prior", "pagedown": "Next",
    "page_down": "Next", "left": "Left", "right": "Right", "up": "Up", "down": "Down",
    "shift": "shift", "ctrl": "ctrl", "control": "ctrl", "alt": "alt", "super": "super",
    "win": "super", "meta": "super", "cmd": "super", "space": "space",
}
CHAR_KEYSYMS = {
    " ": "space", "/": "slash", ".": "period", ",": "comma", "-": "minus", "=": "equal",
    ";": "semicolon", "'": "apostrophe", "[": "bracketleft", "]": "bracketright",
    "\\": "backslash", "`": "grave", "+": "plus",
}


def xdo_key(name):
    key = (name or "").strip()
    lowered = key.lower()
    if lowered in KEY_NAMES:
        return KEY_NAMES[lowered]
    if re.match(r"^f([1-9]|1[0-2])$", lowered):
        return lowered.upper()
    if len(key) == 1:
        return CHAR_KEYSYMS.get(key, key)
    raise ValueError("unknown key '%s'" % key[:40])


def xdo_chord(chord):
    parts = [part for part in re.split(r"\+(?!$)", (chord or "").strip()) if part]
    if not parts:
        raise ValueError("empty key chord")
    return "+".join(xdo_key(part) for part in parts)


def build_input_commands(events):
    """Turns the harness's event list into xdotool invocations.

    Pointer events chain inside one invocation, so a humanised path keeps its timing; `key` and
    `type` consume the rest of xdotool's argv, so each of those is its own invocation.
    """
    if not isinstance(events, list) or not events or len(events) > 4000:
        raise ValueError("events must be a list of 1..4000 items")
    commands = []
    chain = []
    slept = 0.0

    def flush():
        if chain:
            commands.append(["xdotool"] + chain[:])
            del chain[:]

    for event in events:
        if not isinstance(event, dict):
            raise ValueError("each event must be an object")
        kind = event.get("t")
        if kind == "move":
            chain += ["mousemove", str(max(0, int(event.get("x", 0)))), str(max(0, int(event.get("y", 0))))]
        elif kind in ("down", "up"):
            button = _clamp(event.get("b"), 1, 9, 1)
            chain += ["mousedown" if kind == "down" else "mouseup", str(button)]
        elif kind == "click":
            button = _clamp(event.get("b"), 1, 9, 1)
            repeat = _clamp(event.get("n"), 1, 3, 1)
            chain += ["click", "--repeat", str(repeat), "--delay", str(_clamp(event.get("delay"), 1, 1000, 90)), str(button)]
        elif kind == "scroll":
            dy = int(event.get("dy") or 0)
            dx = int(event.get("dx") or 0)
            if dy:
                chain += ["click", "--repeat", str(min(100, abs(dy))), "--delay", "30", "4" if dy > 0 else "5"]
            if dx:
                chain += ["click", "--repeat", str(min(100, abs(dx))), "--delay", "30", "7" if dx > 0 else "6"]
        elif kind == "sleep":
            ms = _clamp(event.get("ms"), 0, 10000, 0)
            slept += ms / 1000.0
            if ms:
                chain += ["sleep", "%.3f" % (ms / 1000.0)]
        elif kind in ("key", "keydown", "keyup"):
            flush()
            verb = {"key": "key", "keydown": "keydown", "keyup": "keyup"}[kind]
            target = xdo_chord(event.get("k")) if kind == "key" else xdo_key(event.get("k"))
            invocation = ["xdotool", verb]
            if kind == "key":
                invocation += ["--delay", str(_clamp(event.get("delay"), 1, 1000, 55))]
                repeat = _clamp(event.get("repeat"), 1, 50, 1)
                if repeat > 1:
                    invocation += ["--repeat", str(repeat)]
            commands.append(invocation + [target])
        elif kind == "type":
            flush()
            text = event.get("text")
            if not isinstance(text, str) or len(text) > 20000:
                raise ValueError("type needs text of at most 20,000 characters")
            if text:
                commands.append(["xdotool", "type", "--delay", str(_clamp(event.get("delay"), 0, 500, 12)), "--", text])
        else:
            raise ValueError("unknown event type %r" % (kind,))
    flush()
    if slept > 120:
        raise ValueError("events sleep for more than 120 seconds in total")
    return commands


def run_input(request):
    try:
        commands = build_input_commands(request.get("events"))
    except (ValueError, TypeError) as error:
        return 400, {"error": str(error)}
    if not which("xdotool"):
        return 503, {"error": "xdotool is not installed in this desktop"}
    started = time.time()
    for command in commands:
        code, _, stderr = run_simple(command, timeout=150)
        if code != 0:
            return 200, {"ok": False, "error": stderr.decode("utf-8", "replace")[:600] or ("exit %d" % code),
                         "durationMs": int((time.time() - started) * 1000)}
    pointer = None
    code, stdout, _ = run_simple(["xdotool", "getmouselocation", "--shell"], timeout=5)
    if code == 0:
        values = dict(line.split("=", 1) for line in stdout.decode("utf-8", "replace").splitlines() if "=" in line)
        try:
            pointer = {"x": int(values.get("X", "0")), "y": int(values.get("Y", "0"))}
        except ValueError:
            pointer = None
    return 200, {"ok": True, "durationMs": int((time.time() - started) * 1000), "pointer": pointer}


# ── screenshot ────────────────────────────────────────────────────────────────────────────────

def _scaled_size(width, height, max_width):
    if max_width and 0 < max_width < width:
        return max_width, max(1, int(round(height * (max_width / float(width)))))
    return width, height


def capture_with_pillow(fmt, quality, max_width):
    from PIL import ImageGrab, Image
    image = ImageGrab.grab(xdisplay=DISPLAY)
    width, height = image.size
    out_w, out_h = _scaled_size(width, height, max_width)
    if (out_w, out_h) != (width, height):
        image = image.resize((out_w, out_h), getattr(Image, "Resampling", Image).LANCZOS)
    image = image.convert("RGB")
    if fmt == "raw":
        r, g, b = image.split()
        alpha = Image.new("L", image.size, 255)
        data = Image.merge("RGBA", (b, g, r, alpha)).tobytes()
        return data, width, height, out_w, out_h, "application/octet-stream"
    import io
    buffer = io.BytesIO()
    if fmt == "png":
        image.save(buffer, format="PNG", compress_level=1)
        return buffer.getvalue(), width, height, out_w, out_h, "image/png"
    image.save(buffer, format="JPEG", quality=quality)
    return buffer.getvalue(), width, height, out_w, out_h, "image/jpeg"


def capture_with_ffmpeg(fmt, quality, max_width):
    width, height = screen_size()
    out_w, out_h = _scaled_size(width, height, max_width)
    command = ["ffmpeg", "-loglevel", "error", "-nostdin", "-f", "x11grab", "-draw_mouse", "0",
               "-video_size", "%dx%d" % (width, height), "-i", DISPLAY, "-frames:v", "1"]
    if (out_w, out_h) != (width, height):
        command += ["-vf", "scale=%d:%d:flags=lanczos" % (out_w, out_h)]
    if fmt == "raw":
        command += ["-f", "rawvideo", "-pix_fmt", "bgra", "-"]
        content_type = "application/octet-stream"
    elif fmt == "png":
        command += ["-f", "image2pipe", "-vcodec", "png", "-"]
        content_type = "image/png"
    else:
        scale = max(2, min(31, int(round(31 - (quality / 100.0) * 29))))
        command += ["-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", str(scale), "-"]
        content_type = "image/jpeg"
    code, stdout, stderr = run_simple(command, timeout=20)
    if code != 0 or not stdout:
        raise RuntimeError("ffmpeg x11grab failed: %s" % stderr.decode("utf-8", "replace")[:400])
    if fmt == "raw" and len(stdout) < out_w * out_h * 4:
        raise RuntimeError("ffmpeg returned a short frame (%d bytes)" % len(stdout))
    return stdout, width, height, out_w, out_h, content_type


def capture(fmt, quality, max_width):
    errors = []
    if pillow_available():
        try:
            return capture_with_pillow(fmt, quality, max_width)
        except Exception as error:
            errors.append("pillow: %s" % str(error)[:200])
    if which("ffmpeg"):
        try:
            return capture_with_ffmpeg(fmt, quality, max_width)
        except Exception as error:
            errors.append(str(error)[:300])
    raise RuntimeError("; ".join(errors) or "no capture backend (pillow or ffmpeg) is available")


# ── clipboard, windows, browser state, diagnostics ────────────────────────────────────────────

def clipboard_get():
    if not which("xclip"):
        return 503, {"error": "xclip is not installed in this desktop"}
    code, stdout, stderr = run_simple(["xclip", "-selection", "clipboard", "-o"], timeout=5)
    if code != 0:
        # An empty clipboard is "no owner", which xclip reports as an error.
        return 200, {"ok": True, "text": "", "empty": True}
    return 200, {"ok": True, "text": stdout.decode("utf-8", "replace")[:1048576], "empty": len(stdout) == 0}


def clipboard_set(request):
    text = request.get("text")
    if not isinstance(text, str) or len(text) > 1048576:
        return 400, {"error": "text must be a string of at most 1 MiB"}
    if not which("xclip"):
        return 503, {"error": "xclip is not installed in this desktop"}
    environment = dict(os.environ)
    environment["DISPLAY"] = DISPLAY
    try:
        # xclip forks a child that keeps serving the selection; it must not inherit our pipes or
        # the call would wait for a process that is meant to outlive it.
        process = subprocess.Popen(["xclip", "-selection", "clipboard", "-i"], stdin=subprocess.PIPE,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=environment)
        process.communicate(text.encode("utf-8"), timeout=5)
        return 200, {"ok": process.returncode == 0}
    except Exception as error:
        return 200, {"ok": False, "error": str(error)[:300]}


def list_windows():
    if not which("wmctrl"):
        return 503, {"error": "wmctrl is not installed in this desktop"}
    active = None
    if which("xdotool"):
        code, stdout, _ = run_simple(["xdotool", "getactivewindow"], timeout=4)
        if code == 0:
            try:
                active = "0x%08x" % int(stdout.strip())
            except ValueError:
                active = None
    code, stdout, stderr = run_simple(["wmctrl", "-lxGp"], timeout=5)
    if code != 0:
        return 200, {"ok": False, "error": stderr.decode("utf-8", "replace")[:300], "windows": []}
    windows = []
    for line in stdout.decode("utf-8", "replace").splitlines():
        parts = line.split(None, 8)
        if len(parts) < 8:
            continue
        try:
            window = {
                "id": parts[0], "desktop": int(parts[1]), "pid": int(parts[2]),
                "x": int(parts[3]), "y": int(parts[4]), "width": int(parts[5]), "height": int(parts[6]),
                "class": parts[7], "title": (parts[8] if len(parts) > 8 else "")[:300],
            }
        except ValueError:
            continue
        try:
            window["active"] = active is not None and int(window["id"], 16) == int(active, 16)
        except ValueError:
            window["active"] = False
        windows.append(window)
    return 200, {"ok": True, "active": active, "windows": windows[:80]}


def browser_state():
    state = {"ok": True, "up": browser_is_up(timeout=3), "tabs": [], "activeTabId": None,
             "watch": WATCH.snapshot()}
    if state["up"]:
        try:
            state["tabs"] = page_targets(timeout=3)[:24]
        except Exception as error:
            state["error"] = str(error)[:300]
    active = _read(ACTIVE_TAB_FILE)
    if active:
        state["activeTabId"] = active
    return 200, state


def tail(path, lines=40, max_bytes=16384):
    try:
        with open(path, "rb") as handle:
            handle.seek(0, os.SEEK_END)
            size = handle.tell()
            handle.seek(max(0, size - max_bytes))
            data = handle.read().decode("utf-8", "replace")
        return "\n".join(data.splitlines()[-lines:])
    except OSError:
        return None


def top_memory(limit=8):
    rows = []
    if not LINUX:
        return rows
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        status = _key_values("/proc/%s/status" % entry)
        rss = status.get("VmRSS")
        comm = _read("/proc/%s/comm" % entry)
        if rss and comm:
            rows.append({"pid": int(entry), "comm": comm, "rssMB": round(rss / 1024.0, 1)})
    rows.sort(key=lambda row: row["rssMB"], reverse=True)
    return rows[:limit]


def diagnostics():
    logs = {}
    for name in ("chromium.log", "x11vnc.log", "xfce4-session.log", "browser-service.log",
                 "xfwm4.log", "xfdesktop.log", "xfce4-panel.log"):
        value = tail(os.path.join("/tmp", name))
        if value:
            logs[name] = value
    return 200, {
        "ok": True, "health": build_health(), "topMemory": top_memory(),
        "events": recent_events(limit=60), "logs": logs,
    }


# ── HTTP ──────────────────────────────────────────────────────────────────────────────────────

PRIVILEGED_GET = frozenset(("/screenshot", "/clipboard", "/windows", "/browser/state", "/diag"))
PRIVILEGED_POST = frozenset(("/exec", "/input", "/clipboard"))


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = SERVICE_NAME

    def log_message(self, fmt, *args):
        pass  # one line per browser action would drown the container log

    def _reply(self, status, body):
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _reply_bytes(self, status, data, content_type, headers=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        for key, value in (headers or {}).items():
            self.send_header(key, str(value))
        self.end_headers()
        self.wfile.write(data)

    def _split_path(self):
        path, _, query = self.path.partition("?")
        params = {}
        for pair in query.split("&"):
            if "=" in pair:
                key, value = pair.split("=", 1)
                params[key] = urllib.parse.unquote(value)
        return path, params

    def _authorize(self):
        problem = check_token(self.headers)
        if problem is None:
            return True
        self._reply(401 if problem != "no-token-configured" else 403, {"error": problem})
        return False

    def do_GET(self):
        path, params = self._split_path()
        if path == "/health":
            self._reply(200, health())
            return
        if path not in PRIVILEGED_GET:
            self._reply(404, {"error": "not found"})
            return
        if not self._authorize():
            return
        if path == "/screenshot":
            fmt = (params.get("format") or "jpeg").lower()
            if fmt not in ("jpeg", "png", "raw"):
                self._reply(400, {"error": "format must be jpeg, png or raw"})
                return
            quality = _clamp(params.get("quality"), 10, 95, 70)
            max_width = _clamp(params.get("maxWidth"), 0, 7680, 0)
            try:
                data, width, height, out_w, out_h, content_type = capture(fmt, quality, max_width)
            except Exception as error:
                self._reply(503, {"error": str(error)[:600]})
                return
            self._reply_bytes(200, data, content_type, {
                "X-Frame-Width": width, "X-Frame-Height": height,
                "X-Image-Width": out_w, "X-Image-Height": out_h,
            })
            return
        if path == "/clipboard":
            status, body = clipboard_get()
        elif path == "/windows":
            status, body = list_windows()
        elif path == "/browser/state":
            status, body = browser_state()
        else:
            status, body = diagnostics()
        self._reply(status, body)

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

    def _json_body(self):
        raw = self._read_body()
        if raw is None:
            self._reply(400, {"error": "request body must be 1..4MiB of JSON (Content-Length or chunked)"})
            return None
        try:
            request = json.loads(raw.decode("utf-8", "replace"))
        except ValueError:
            self._reply(400, {"error": "body must be JSON"})
            return None
        if not isinstance(request, dict):
            self._reply(400, {"error": "body must be a JSON object"})
            return None
        return request

    def do_POST(self):
        path, _ = self._split_path()
        if path == "/run":
            request = self._json_body()
            if request is None:
                return
            mode = request.get("mode") or ""
            payload = request.get("payload") or ""
            args = request.get("args") or []
            if mode not in ALLOWED_MODES:
                self._reply(400, {"error": "unsupported mode"})
                return
            if not isinstance(payload, str) or not PAYLOAD_RE.match(payload):
                self._reply(400, {"error": "payload must be unpadded base64url"})
                return
            if not isinstance(args, list) or len(args) > 4 or not all(
                    isinstance(x, str) and ARG_RE.match(x) for x in args):
                self._reply(400, {"error": "args must be up to 4 short scalar strings"})
                return
            self._reply(200, run_helper(mode, payload, [str(x) for x in args]))
            return
        if path not in PRIVILEGED_POST:
            self._reply(404, {"error": "not found"})
            return
        # Authorise before reading the body: an unauthorised caller should not get to make this
        # process buffer four megabytes.
        if not self._authorize():
            return
        request = self._json_body()
        if request is None:
            return
        if path == "/exec":
            status, body = run_exec(request)
        elif path == "/input":
            status, body = run_input(request)
        else:
            status, body = clipboard_set(request)
        self._reply(status, body)


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
    if LINUX and os.environ.get("KLIVE_COMPUTERD_WATCHDOG", "1") != "0":
        threading.Thread(target=watchdog_loop, name="browser-watchdog", daemon=True).start()
    record_event("started", "%s protocol %d listening on port %d." % (SERVICE_NAME, PROTOCOL, LISTEN_PORT))
    server.serve_forever()


if __name__ == "__main__":
    main()
