import base64
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import shlex
import csv
import io
from collections import OrderedDict

from .http import serve
from .jobs import Jobs
from .state import State, public_operation


class Session:
    def __init__(self, config):
        self.config = config
        self.state = State(config["state"])
        self.jobs = Jobs(self.state)
        self.state.recover_actions()
        self.visual = threading.Lock()
        self.frame_lock = threading.Lock()
        self.frame = None
        self.frame_at = 0
        self.observations = OrderedDict()
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self.work, daemon=True)
        self.thread.start()
        self.job_thread = threading.Thread(target=self.reconcile_jobs, daemon=True)
        self.job_thread.start()

    def reconcile_jobs(self):
        while not self.stop.wait(5):
            try:
                self.jobs.reconcile()
            except Exception:
                self.stop.wait(5)

    def shell(self, args, input=None, timeout=60):
        return subprocess.run(args, input=input, capture_output=True, timeout=timeout, check=True,
                              env={**os.environ, "DISPLAY": ":1", "XAUTHORITY": "/home/agent/.Xauthority",
                                   "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/1000/bus"}).stdout

    def desktop(self):
        # Starting an already running unit does not restart it or close applications.
        self.shell(["sudo", "-n", "systemctl", "start", "ka-desktop.service"])

    def screenshot(self):
        with self.frame_lock:
            if self.frame is None or time.monotonic() - self.frame_at > 0.25:
                self.frame = self.shell(["import", "-display", ":1", "-window", "root", "-quality", "65", "jpeg:-"])
                self.frame_at = time.monotonic()
            return {"jpeg": base64.b64encode(self.frame).decode(), "width": 1600, "height": 900}

    def lease(self, owner, release=False):
        with self.visual:
            current = self.state.setting("inputLease") or {}
            if release:
                if current.get("owner") == owner:
                    for op in self.state.list(["queued"]):
                        if op["kind"] == "action" and op["request"].get("actorID") == owner:
                            self.state.update(op["id"], "interrupted", {"reason": "Human input session ended before execution"})
                    try:
                        self.shell(["xdotool", "mouseup", "1", "mouseup", "2", "mouseup", "3", "keyup", "Control_L", "Alt_L", "Shift_L", "Super_L"], timeout=5)
                    except Exception:
                        pass
                    self.state.setting("inputLease", {"owner": "", "expires": 0})
            elif current.get("expires", 0) > time.time() and current.get("owner") != owner:
                return {"acquired": False, "owner": current.get("owner")}
            else:
                self.state.setting("inputLease", {"owner": owner, "expires": time.time() + 45})
            return {"acquired": True, "owner": owner}

    def work(self):
        while not self.stop.wait(0.1):
            try:
                for op in self.state.list(["queued"]):
                    if op["kind"] != "action":
                        continue
                    with self.visual:
                        lease = self.state.setting("inputLease") or {}
                        actor = op["request"].get("actorID", "")
                        if actor.startswith("human-") and (lease.get("owner") != actor or lease.get("expires", 0) <= time.time()):
                            self.state.update(op["id"], "interrupted", {"reason": "Human input lease expired before execution"})
                            continue
                        if lease.get("expires", 0) > time.time() and lease.get("owner") != op["request"].get("actorID"):
                            continue
                        if not self.state.claim(op["id"]):
                            continue
                        try:
                            result = self.action(op["request"]["tool"], op["request"].get("arguments", {}))
                            jpeg = result.pop("jpeg", None)
                            if jpeg:
                                with self.frame_lock:
                                    self.observations[op["id"]] = jpeg
                                    while len(self.observations) > 4:
                                        self.observations.popitem(last=False)
                                result["observationAvailable"] = True
                            self.state.update(op["id"], "completed", result)
                        except (subprocess.TimeoutExpired, OSError):
                            self.state.update(op["id"], "outcome_unknown", {"reason": "Action acknowledgment unavailable; inspect before further input"})
                        except Exception as error:
                            self.state.update(op["id"], "failed", {"reason": str(error)[:1000]})
            except Exception:
                # Preserve the queue on storage/temporary infrastructure failure.
                self.stop.wait(2)

    def action(self, tool, a):
        self.desktop()
        x, y = int(a.get("x", 0)), int(a.get("y", 0))
        button = {"left": "1", "middle": "2", "right": "3"}.get(str(a.get("button", "left")), str(a.get("button", 1)))
        if tool == "computer_screenshot":
            return self.screenshot()
        elif tool in ("computer_read_screen", "computer_find_text", "computer_click_text"):
            png = self.shell(["import", "-display", ":1", "-window", "root", "png:-"])
            if tool == "computer_read_screen":
                return {"text": self.shell(["tesseract", "stdin", "stdout"], png).decode(errors="replace")}
            words = list(csv.DictReader(io.StringIO(self.shell(["tesseract", "stdin", "stdout", "tsv"], png).decode()), delimiter="\t"))
            query = str(a.get("text", a.get("query", ""))).casefold()
            if not query:
                raise ValueError("text is required")
            matches = [{"text": word["text"], "x": int(word["left"]) + int(word["width"]) // 2,
                        "y": int(word["top"]) + int(word["height"]) // 2} for word in words if query in word.get("text", "").casefold()]
            if tool == "computer_click_text":
                if len(matches) != 1:
                    raise ValueError("OCR target must have exactly one match; inspect the screen")
                self.shell(["xdotool", "mousemove", str(matches[0]["x"]), str(matches[0]["y"]), "click", "1"])
            return {"matches": matches}
        elif tool == "computer_move":
            self.shell(["xdotool", "mousemove", str(x), str(y)])
        elif tool in ("computer_move_relative", "computer_mouse_move_relative"):
            self.shell(["xdotool", "mousemove_relative", "--", str(a.get("dx", x)), str(a.get("dy", y))])
        elif tool in ("computer_click", "computer_mouse_down", "computer_mouse_up"):
            self.shell(["xdotool", "mousemove", str(x), str(y)])
            verb = {"computer_click": "click", "computer_mouse_down": "mousedown", "computer_mouse_up": "mouseup"}[tool]
            self.shell(["xdotool", verb, button])
            if tool == "computer_click" and int(a.get("clicks", 1)) == 2:
                self.shell(["xdotool", "click", button])
        elif tool == "computer_drag":
            self.shell(["xdotool", "mousemove", str(a.get("fromX", a.get("startX", x))), str(a.get("fromY", a.get("startY", y))), "mousedown", button,
                        "mousemove", str(a.get("toX", a.get("endX", x))), str(a.get("toY", a.get("endY", y))), "mouseup", button])
        elif tool in ("computer_type", "computer_clipboard_set"):
            self.shell(["xclip", "-selection", "clipboard"], str(a.get("text", "")).encode())
            if tool == "computer_type":
                self.shell(["xdotool", "key", "--clearmodifiers", "ctrl+v"])
        elif tool == "computer_clipboard_get":
            return {"text": self.shell(["xclip", "-selection", "clipboard", "-o"]).decode(errors="replace")}
        elif tool in ("computer_key", "computer_key_down", "computer_key_up"):
            keys = a.get("keys", a.get("key", ""))
            if isinstance(keys, list):
                aliases = {"control": "ctrl", "meta": "super", "win": "super", "arrowleft": "Left", "arrowright": "Right", "arrowup": "Up", "arrowdown": "Down", "enter": "Return", "backspace": "BackSpace", "escape": "Escape", "tab": "Tab", "left": "Left", "right": "Right", "up": "Up", "down": "Down", "delete": "Delete", "insert": "Insert", "home": "Home", "end": "End", "pageup": "Prior", "pagedown": "Next"}
                aliases.update({f"f{i}": f"F{i}" for i in range(1, 36)})
                keys = "+".join(aliases.get(key.lower(), key) for key in keys)
            if not keys or str(keys).startswith("-"):
                raise ValueError("A keyboard key is required")
            self.shell(["xdotool", {"computer_key": "key", "computer_key_down": "keydown", "computer_key_up": "keyup"}[tool], str(keys)])
        elif tool == "computer_scroll":
            amount = max(1, min(30, abs(int(a.get("dy", a.get("amount", 3))))))
            direction = a.get("direction", "up" if a.get("dy", 0) > 0 else "down")
            self.shell(["xdotool", "click", "--repeat", str(amount), {"up": "4", "down": "5", "left": "6", "right": "7"}.get(direction, "5")])
        elif tool == "computer_release_all":
            for number in ("1", "2", "3"):
                self.shell(["xdotool", "mouseup", number])
            self.shell(["xdotool", "keyup", "Control_L", "Control_R", "Alt_L", "Alt_R", "Shift_L", "Shift_R", "Super_L", "Super_R"])
        elif tool == "computer_window_state":
            return {"text": self.shell(["wmctrl", "-lpG"]).decode(errors="replace")}
        elif tool == "computer_focus_window":
            self.shell(["wmctrl", "-a", str(a.get("titleContains", a.get("title", a.get("name", ""))))])
        elif tool in ("computer_browser_inspect", "computer_browser_action", "computer_click_browser_control", "computer_upload_file"):
            helper = "/opt/ka/browser-inspect.py"
            if tool == "computer_browser_inspect":
                mode = a.get("mode", "dom")
                if mode not in ("tabs", "dom", "accessibility", "network", "controls"):
                    raise ValueError("Unsupported inspection mode")
                argv = ["python3", helper, mode, str(a.get("maxItems", 80)), str(a.get("tabIndex", -1))]
            else:
                mode = {"computer_browser_action": "action", "computer_click_browser_control": "control", "computer_upload_file": "upload"}[tool]
                argv = ["python3", helper, mode, base64.urlsafe_b64encode(json.dumps(a).encode()).decode()]
            return {"text": self.shell(argv, timeout=120).decode(errors="replace")}
        elif tool in ("computer_open_browser", "computer_navigate", "computer_launch_app"):
            app = a.get("path", a.get("shellName", a.get("app", a.get("name", "browser"))))
            args = a.get("args", [])
            if isinstance(args, str):
                args = shlex.split(args)
            if not isinstance(args, list):
                raise ValueError("Application args must be an array or quoted argument string")
            url = a.get("url", "about:blank")
            if tool != "computer_launch_app" or app == "browser":
                if not str(url).startswith(("https://", "http://", "about:")):
                    raise ValueError("Unsupported URL scheme")
                argv = ["chromium", "--password-store=basic", "--user-data-dir=/home/agent/.config/chromium", "--remote-debugging-port=9222", str(url)]
            else:
                argv = [{"terminal": "xterm", "files": "pcmanfm", "editor": "mousepad"}.get(app, app)] + [str(arg) for arg in args]
            # Applications belong to their own unit and survive session service restarts.
            self.shell(["sudo", "-n", "systemd-run", "--quiet", "--collect", "--uid=agent",
                        "--setenv=DISPLAY=:1", "--setenv=XAUTHORITY=/home/agent/.Xauthority",
                        "--setenv=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"] + argv)
        elif tool == "computer_wait":
            time.sleep(min(30, max(0, float(a.get("seconds", 1)))))
        else:
            raise ValueError("Unsupported tool: " + tool + ". Use desktop input or computer_terminal.")
        # A screenshot failure must not change a successfully acknowledged action into a retry.
        result = {"text": tool + " executed"}
        try:
            result.update(self.screenshot())
        except Exception:
            result["observationPending"] = True
        return result

    def __call__(self, method, path, query, body):
        if path == "/health" and method == "GET":
            def active(unit):
                try:
                    return self.shell(["systemctl", "is-active", unit], timeout=3).decode().strip()
                except Exception:
                    return "unknown"
            browser = subprocess.run(["pgrep", "-x", "chromium"], capture_output=True, timeout=3).returncode == 0
            return 200, {"session": "ready", "terminal": "ready", "display": active("ka-desktop"), "browser": "running" if browser else "stopped"}
        if path == "/frame" and method == "GET":
            return 200, self.screenshot()
        if path == "/lease" and method == "POST":
            return 200, self.lease(body["owner"], body.get("release", False))
        if path == "/actions" and method == "POST":
            op, _ = self.state.submit(body["operationID"], "action", {key: body[key] for key in ("tool", "arguments", "actorID")})
            return 202, public_operation(op)
        if path.startswith("/operations/") and method == "GET":
            op_id = path.split("/")[-1]
            result = public_operation(self.state.get(op_id))
            with self.frame_lock:
                if op_id in self.observations:
                    result["result"]["jpeg"] = self.observations[op_id]
            return 200, result
        if path == "/jobs" and method == "POST":
            return 202, self.jobs.submit(body)
        if path == "/jobs" and method == "GET":
            return 200, [public_operation(op) for op in self.state.list(kind="terminal")]
        if path.startswith("/jobs/"):
            job_id = path.split("/")[2]
            if method == "GET":
                return 200, self.jobs.get(job_id, query.get("cursor", 0))
            if method == "POST" and path.endswith("/input"):
                return 200, self.jobs.input(job_id, body)
        raise KeyError(path)


def main(config):
    os.umask(0o077)
    serve(config, Session(config))
