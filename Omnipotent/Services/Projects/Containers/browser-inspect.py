#!/usr/bin/env python3
"""Harness-owned browser helper for a Projects desktop.

ContainerToolAdapter calls this for browser inspection, navigation, uploads and structured
actions, so the one visible Chromium session is inspected and driven from a single place.
Read-only inspection modes (tabs/dom/controls/accessibility/network and legacy locate) keep their
argv contract; action modes (navigate/closetabs/upload/dialog/control/action) take one base64url
JSON payload so no caller has to quote page text, URLs, selectors or file paths through a shell.
"""
import base64
import hashlib
import json
import math
import os
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request

import websocket

CDP_ROOT = "http://127.0.0.1:9222"
# The tab the harness last drove. Chromium exposes no "active tab" flag over HTTP, so the live
# visibilityState probe is primary and this file is the fallback when every tab reports hidden
# (window minimised/unmapped) — which is exactly when defaulting to index 0 picked a stale page.
ACTIVE_TAB_FILE = "/tmp/klive-active-tab"
BLANK_URLS = ("", "about:blank", "about:newtab", "chrome://newtab/", "chrome://new-tab-page/")
ACTION_MODES = ("navigate", "closetabs", "upload", "dialog", "control", "action")


def http_json(path, method="GET", timeout=5):
    # The CDP endpoint is always container-local. Ignore inherited proxy variables: routing a
    # loopback request through a corporate/system proxy makes a healthy browser look offline.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(urllib.request.Request(CDP_ROOT + path, method=method), timeout=timeout) as response:
        body = response.read().decode("utf-8", "replace").strip()
    if not body:
        return {}
    try:
        return json.loads(body)
    except ValueError:
        # /json/close answers with a plain "Target is closing" sentence.
        return {"raw": body}


class Session(object):
    """One CDP websocket that survives several commands. DOM node ids are per-session, so the
    upload path cannot use a connect-per-command helper."""

    def __init__(self, ws_url, timeout=15):
        self.ws = websocket.create_connection(ws_url, timeout=timeout, suppress_origin=True)
        self.next_id = 1
        self.events = []

    def call(self, method, params=None, timeout=15):
        self.ws.settimeout(timeout)
        request_id = self.next_id
        self.next_id += 1
        self.ws.send(json.dumps({"id": request_id, "method": method, "params": params or {}}))
        deadline = time.time() + timeout
        while time.time() < deadline:
            message = json.loads(self.ws.recv())
            if message.get("id") != request_id:
                # Retain a bounded event tail. Structured control uses this to report JavaScript
                # dialogs and lifecycle changes that would otherwise be silently discarded while
                # waiting for a command response. Existing one-shot callers keep the same contract.
                if message.get("method"):
                    self.events.append(message)
                    if len(self.events) > 200:
                        del self.events[:-200]
                continue
            if "error" in message:
                raise RuntimeError(message["error"].get("message", "CDP error"))
            return message.get("result", {})
        raise RuntimeError("Timed out waiting for the CDP response to " + method)

    def evaluate(self, expression, timeout=15):
        result = self.call("Runtime.evaluate",
                           {"expression": expression, "returnByValue": True, "awaitPromise": True}, timeout)
        if result.get("exceptionDetails"):
            details = result["exceptionDetails"]
            raise RuntimeError(details.get("text") or ((details.get("exception") or {}).get("description"))
                               or "Runtime.evaluate failed")
        return (result.get("result") or {}).get("value")

    def evaluate_in_context(self, expression, context_id=None, timeout=15, return_by_value=True):
        params = {
            "expression": expression,
            "returnByValue": return_by_value,
            "awaitPromise": True,
            "userGesture": False,
        }
        if context_id is not None:
            params["contextId"] = context_id
        result = self.call("Runtime.evaluate", params, timeout)
        if result.get("exceptionDetails"):
            details = result["exceptionDetails"]
            raise RuntimeError(details.get("text") or ((details.get("exception") or {}).get("description"))
                               or "Runtime.evaluate failed")
        return result.get("result") or {}

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def cdp(ws_url, method, params=None, timeout=15):
    session = Session(ws_url, timeout)
    try:
        return session.call(method, params, timeout)
    finally:
        session.close()


def decode_payload(encoded):
    encoded += "=" * ((4 - len(encoded) % 4) % 4)
    value = json.loads(base64.urlsafe_b64decode(encoded.encode("ascii")).decode("utf-8"))
    return value if isinstance(value, dict) else {}


def list_tabs():
    return [item for item in http_json("/json/list") if item.get("type") == "page"]


def list_targets():
    return list(http_json("/json/list"))


def is_blank(url):
    return (url or "").rstrip("/") in [x.rstrip("/") for x in BLANK_URLS] or (url or "").startswith("chrome://new-tab")


def remembered_active_id():
    try:
        with open(ACTIVE_TAB_FILE, "r") as handle:
            return handle.read().strip()
    except Exception:
        return ""


def remember_active(tab_id):
    try:
        with open(ACTIVE_TAB_FILE, "w") as handle:
            handle.write(tab_id or "")
    except Exception:
        pass


def is_visible(tab):
    ws_url = tab.get("webSocketDebuggerUrl")
    if not ws_url:
        return False
    try:
        # Short timeout on purpose: a tab sitting behind a blocking alert() never answers, and the
        # foreground tab of every other window still does.
        return cdp(ws_url, "Runtime.evaluate",
                   {"expression": "document.visibilityState", "returnByValue": True},
                   timeout=2).get("result", {}).get("value") == "visible"
    except Exception:
        return False


def active_index(tabs):
    """The foreground tab. Exactly one tab of a mapped window reports visibilityState 'visible'."""
    if len(tabs) <= 1:
        return 0
    for index, tab in enumerate(tabs[:8]):
        if is_visible(tab):
            return index
    remembered = remembered_active_id()
    if remembered:
        for index, tab in enumerate(tabs):
            if tab.get("id") == remembered:
                return index
    return 0


def resolve_index(tabs, requested):
    if not tabs:
        raise RuntimeError("No inspectable browser tab is open.")
    if requested is not None and requested >= 0:
        if requested >= len(tabs):
            raise RuntimeError("Browser tab index %d does not exist; inspect mode=tabs and choose a listed index."
                               % requested)
        return requested
    return active_index(tabs)


def session_for(tab):
    ws_url = tab.get("webSocketDebuggerUrl")
    if not ws_url:
        raise RuntimeError("The selected browser tab exposes no debugger endpoint; another client may be attached.")
    return Session(ws_url)


# ── native (GTK) dialog detection ────────────────────────────────────────────────────────────
# Chromium's file chooser is a real GTK3 toplevel owned by the browser process, so it is invisible
# to every DOM/CDP surface. Detecting it is what turns "the agent is silently stuck behind a modal"
# into an actionable instruction.
FILE_CHOOSER_TITLE = re.compile(
    r"(^\s*(open|save|select|choose|upload)\b)|file upload|choose files?|select files?|upload files?", re.I)
# "<page> - Chromium", and the bare "Chromium" a titleless page shows — both are the browser
# window itself, never a dialog. Getting this wrong would report a phantom modal on every click.
BROWSER_WINDOW_TITLE = re.compile(r"(-\s*chromium(-browser)?\s*$)|(^\s*chromium(-browser)?\s*$)", re.I)


def run_x11(args, timeout=5):
    environment = dict(os.environ)
    environment.setdefault("DISPLAY", ":1")
    try:
        finished = subprocess.run(args, capture_output=True, text=True, timeout=timeout, env=environment)
        return finished.stdout.strip() if finished.returncode == 0 else ""
    except Exception:
        return ""


def detect_native_dialog():
    """Any mapped Chromium-owned toplevel that is not the browser window itself is a native
    dialog. wmctrl gives class+title in one call; xdotool is the fallback."""
    windows = []
    for line in run_x11(["wmctrl", "-lx"]).splitlines():
        parts = line.split(None, 4)
        if len(parts) < 5:
            continue
        window_id, window_class, title = parts[0], parts[2], parts[4].strip()
        if "chrom" not in window_class.lower():
            continue
        if not title or BROWSER_WINDOW_TITLE.search(title):
            continue
        windows.append({"id": window_id, "title": title[:200],
                        "kind": "file-chooser" if FILE_CHOOSER_TITLE.search(title) else "dialog"})
    if not windows:
        for window_id in run_x11(["xdotool", "search", "--onlyvisible", "--class", "chromium"]).split()[:40]:
            title = run_x11(["xdotool", "getwindowname", window_id])
            if not title or BROWSER_WINDOW_TITLE.search(title):
                continue
            windows.append({"id": window_id, "title": title[:200],
                            "kind": "file-chooser" if FILE_CHOOSER_TITLE.search(title) else "dialog"})
    return {"open": bool(windows), "fileChooser": any(w["kind"] == "file-chooser" for w in windows),
            "windows": windows[:8]}


def do_dialog(payload):
    """Dialog state plus, optionally, activation and container-side validation of the files the
    caller is about to attach. One exec answers everything the upload flow needs to decide."""
    state = detect_native_dialog()
    if payload.get("activate") and state["windows"]:
        target = next((w for w in state["windows"] if w["kind"] == "file-chooser"), state["windows"][0])
        run_x11(["wmctrl", "-ia", target["id"]])
        run_x11(["xdotool", "windowactivate", "--sync", target["id"]], timeout=3)
        run_x11(["xdotool", "windowraise", target["id"]], timeout=3)
        state["activated"] = target
        time.sleep(0.2)
    files = []
    for path in (payload.get("paths") or [])[:16]:
        entry = {"path": path, "exists": os.path.isfile(path)}
        if entry["exists"]:
            entry["size"] = os.path.getsize(path)
            entry["readable"] = os.access(path, os.R_OK)
        files.append(entry)
    if files:
        state["files"] = files
    return state


# ── navigation and tab hygiene ───────────────────────────────────────────────────────────────
def wait_ready(session, budget_seconds=15):
    deadline = time.time() + budget_seconds
    state = "unknown"
    while time.time() < deadline:
        try:
            state = session.evaluate("document.readyState", timeout=5)
        except Exception:
            state = "unknown"
        if state in ("interactive", "complete"):
            return state
        time.sleep(0.25)
    return state


def prune_tabs(max_tabs, active_id, keep_urls=()):
    """Blank tabs, then duplicates, then least-recently-active tabs over the cap. /json/list is
    ordered most-recently-active first, so the tail is the coldest. The active tab and the last
    remaining tab are never closed."""
    survivors = list_tabs()
    closed = []

    def drop(tab):
        if len(survivors) <= 1 or tab.get("id") == active_id or (tab.get("url") or "") in keep_urls:
            return False
        try:
            http_json("/json/close/" + urllib.parse.quote(tab.get("id") or "", safe=""))
        except Exception:
            return False
        survivors.remove(tab)
        closed.append({"title": (tab.get("title") or "")[:120], "url": (tab.get("url") or "")[:300]})
        return True

    for tab in list(survivors):
        if is_blank(tab.get("url")):
            drop(tab)
    seen = set()
    for tab in list(survivors):
        url = tab.get("url") or ""
        if url in seen:
            drop(tab)
        else:
            seen.add(url)
    for tab in list(reversed(survivors)):
        if len(survivors) <= max_tabs:
            break
        drop(tab)
    return closed, survivors


def do_navigate(payload):
    url = (payload.get("url") or "").strip()
    if not re.match(r"^https?://", url, re.I):
        raise RuntimeError("navigate requires an absolute http(s) URL.")
    tabs = list_tabs()
    reused = False
    target_id = ""
    ready = "unknown"
    if payload.get("newTab") or not tabs:
        created = http_json("/json/new?" + urllib.parse.quote(url, safe=""), method="PUT", timeout=10)
        target_id = created.get("id") or ""
        for _ in range(24):
            tabs = list_tabs()
            if any(tab.get("id") == target_id for tab in tabs):
                break
            time.sleep(0.25)
    else:
        index = resolve_index(tabs, payload.get("tabIndex"))
        target_id = tabs[index].get("id") or ""
        session = session_for(tabs[index])
        try:
            session.call("Page.enable")
            result = session.call("Page.navigate", {"url": url}, timeout=30)
            if result.get("errorText"):
                raise RuntimeError("Navigation failed: %s" % result["errorText"])
            try:
                session.call("Page.bringToFront", timeout=5)
            except Exception:
                pass
            ready = wait_ready(session)
        finally:
            session.close()
        reused = True
    remember_active(target_id)

    max_tabs = int(payload.get("maxTabs") or 0)
    closed = []
    tabs = list_tabs()
    if max_tabs > 0:
        closed, tabs = prune_tabs(max_tabs, target_id, keep_urls=tuple(payload.get("keepUrls") or ()))
    current = next((tab for tab in tabs if tab.get("id") == target_id), None) or (tabs[0] if tabs else {})
    return {"navigated": url, "reusedTab": reused, "readyState": ready,
            "url": current.get("url"), "title": current.get("title"),
            "tabIndex": next((i for i, tab in enumerate(tabs) if tab.get("id") == target_id), 0),
            "tabCount": len(tabs), "closedTabs": closed,
            "nativeDialog": detect_native_dialog()}


def do_closetabs(payload):
    tabs = list_tabs()
    if not tabs:
        return {"tabCount": 0, "closedTabs": []}
    index = resolve_index(tabs, payload.get("keepIndex"))
    active_id = tabs[index].get("id") or ""
    remember_active(active_id)
    closed, survivors = prune_tabs(max(1, int(payload.get("maxTabs") or 6)), active_id,
                                   keep_urls=tuple(payload.get("keepUrls") or ()))
    return {"keptTab": {"title": tabs[index].get("title"), "url": tabs[index].get("url")},
            "closedTabs": closed, "tabCount": len(survivors)}


# ── file-input upload ────────────────────────────────────────────────────────────────────────
def collect_file_inputs(session):
    """Every <input type=file> in the tab, including ones inside iframes, shadow roots and the
    display:none inputs that styled upload buttons actually drive. The pierced DOM tree is walked
    in Python because DOM.querySelectorAll does not cross those boundaries."""
    document = session.call("DOM.getDocument", {"depth": -1, "pierce": True}, timeout=30)
    found = []

    def walk(node, depth=0):
        if depth > 64 or not isinstance(node, dict):
            return
        if node.get("nodeName") == "INPUT":
            attributes = node.get("attributes") or []
            pairs = dict(zip(attributes[0::2], attributes[1::2]))
            if (pairs.get("type") or "").lower() == "file":
                found.append({
                    "nodeId": node.get("nodeId"),
                    "name": pairs.get("name") or pairs.get("id") or pairs.get("aria-label") or "",
                    "accept": (pairs.get("accept") or "")[:200],
                    "multiple": "multiple" in pairs,
                })
        for child in node.get("children") or []:
            walk(child, depth + 1)
        if node.get("contentDocument"):
            walk(node["contentDocument"], depth + 1)
        for shadow_root in node.get("shadowRoots") or []:
            walk(shadow_root, depth + 1)
        if node.get("templateContent"):
            walk(node["templateContent"], depth + 1)

    walk(document.get("root") or {})
    return found


def describe_input(session, node_id):
    resolved = session.call("DOM.resolveNode", {"nodeId": node_id})
    object_id = (resolved.get("object") or {}).get("objectId")
    if not object_id:
        return {}
    result = session.call("Runtime.callFunctionOn", {
        "objectId": object_id,
        "returnByValue": True,
        "functionDeclaration": "function(){return {count:this.files?this.files.length:0,"
                               "names:this.files?Array.prototype.map.call(this.files,function(f){return f.name;}):[],"
                               "name:this.name||this.id||'',accept:this.accept||'',multiple:!!this.multiple,"
                               "disabled:!!this.disabled};}",
    })
    return ((result.get("result") or {}).get("value")) or {}


def do_upload(payload):
    paths = [str(path) for path in (payload.get("paths") or []) if str(path or "").strip()]
    verify_only = bool(payload.get("verifyOnly"))
    if not verify_only:
        if not paths:
            raise RuntimeError("upload requires at least one absolute container file path.")
        missing = [path for path in paths if not os.path.isfile(path)]
        if missing:
            raise RuntimeError("File not found inside the desktop container: %s" % ", ".join(missing))
    tabs = list_tabs()
    index = resolve_index(tabs, payload.get("tabIndex"))
    session = session_for(tabs[index])
    try:
        session.call("DOM.enable")
        session.call("Runtime.enable")
        inputs = collect_file_inputs(session)
        if not inputs:
            raise RuntimeError(
                "This page exposes no <input type=file> element. The site is using a native picker "
                "(File System Access API) or a drag-and-drop-only uploader, so the visible GTK dialog "
                "is the only route: click the page's upload control, then call computer_upload_file "
                "again while the dialog is open.")
        if verify_only:
            states = []
            for candidate in inputs[:12]:
                state = describe_input(session, candidate["nodeId"])
                if state:
                    states.append(state)
            return {"verified": True, "inputs": states, "fileInputCount": len(inputs),
                    "attached": sum(int(state.get("count") or 0) for state in states),
                    "url": tabs[index].get("url"), "title": tabs[index].get("title"), "tabIndex": index}

        wanted = (payload.get("name") or "").strip().lower()
        candidates = [item for item in inputs if wanted in (item["name"] or "").lower()] if wanted else list(inputs)
        if not candidates:
            raise RuntimeError("No file input matched name='%s'. Available inputs: %s"
                               % (wanted, json.dumps([item["name"] for item in inputs][:12])))
        occurrence = max(0, int(payload.get("occurrence") or 0))
        if occurrence >= len(candidates):
            raise RuntimeError("Occurrence %d does not exist; %d matching file input(s) on this page."
                               % (occurrence, len(candidates)))
        chosen = candidates[occurrence]
        if len(paths) > 1 and not chosen.get("multiple"):
            raise RuntimeError("The selected file input does not accept multiple files; attach one path.")
        session.call("DOM.setFileInputFiles", {"files": paths, "nodeId": chosen["nodeId"]}, timeout=90)
        state = describe_input(session, chosen["nodeId"])
        return {"attached": state.get("count", 0), "files": state.get("names", []),
                "input": {"name": state.get("name") or chosen["name"], "accept": state.get("accept"),
                          "multiple": state.get("multiple"), "disabled": state.get("disabled")},
                "fileInputCount": len(inputs), "tabIndex": index,
                "url": tabs[index].get("url"), "title": tabs[index].get("title")}
    finally:
        session.close()


# ── structured, non-visual browser control ───────────────────────────────────────────────────
# This surface intentionally operates the same visible/authenticated Chromium target as the
# existing inspector. It does not expose cookies, storage, request bodies, or input values.

SENSITIVE_URL_KEY = re.compile(
    r"(^|[_-])(authorization|auth|code|credential|key|nonce|password|secret|session|signature|token)([_-]|$)",
    re.I)
REF_PREFIX = "kref1_"
MAX_CONTROL_SCAN = 2400
MAX_FRAMES = 64


def bounded_text(value, limit):
    value = "" if value is None else str(value)
    return value if len(value) <= limit else value[:limit] + "…"


def safe_url(value, limit=1200):
    """Keep URLs useful without returning obvious bearer material in query/fragment fields."""
    value = bounded_text(value or "", limit * 2)
    try:
        parsed = urllib.parse.urlsplit(value)
        # User-info is rarely useful to an agent and can contain a literal username/password.
        hostname = parsed.hostname or ""
        if ":" in hostname and not hostname.startswith("["):
            hostname = "[" + hostname + "]"
        netloc = hostname
        if parsed.port is not None:
            netloc += ":" + str(parsed.port)
        if parsed.username is not None or parsed.password is not None:
            netloc = "<redacted>@" + netloc
        query = []
        for key, item in urllib.parse.parse_qsl(parsed.query, keep_blank_values=True):
            query.append((key, "<redacted>" if SENSITIVE_URL_KEY.search(key) else bounded_text(item, 300)))
        fragment = parsed.fragment
        if fragment and ("=" in fragment or "&" in fragment):
            fragment_items = []
            for key, item in urllib.parse.parse_qsl(fragment, keep_blank_values=True):
                fragment_items.append((key, "<redacted>" if SENSITIVE_URL_KEY.search(key)
                                       else bounded_text(item, 300)))
            fragment = urllib.parse.urlencode(fragment_items)
        rebuilt = urllib.parse.urlunsplit((
            parsed.scheme, netloc, parsed.path,
            urllib.parse.urlencode(query), fragment))
        return bounded_text(rebuilt, limit)
    except Exception:
        # Even malformed URLs must not fall back to echoing obvious credentials verbatim.
        fallback = re.sub(r"(?i)(?<=://)[^/@\s]+@", "<redacted>@", value)
        fallback = re.sub(
            r"(?i)([?&#][^=&#]*(?:authorization|auth|code|credential|key|nonce|password|"
            r"secret|session|signature|token)[^=&#]*=)[^&#]*",
            r"\1<redacted>", fallback)
        return bounded_text(fallback, limit)


def control_signature(item):
    material = json.dumps([
        item.get("tag") or "", item.get("role") or "", item.get("name") or "",
        item.get("type") or "", item.get("id") or "", item.get("htmlName") or "",
        item.get("label") or "", item.get("placeholder") or "", item.get("testId") or "",
    ], ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(material.encode("utf-8", "replace")).hexdigest()[:20]


def encode_ref(tab_id, frame_id, endpoint_id, path, signature):
    body = {
        "v": 1, "tab": tab_id or "", "frame": frame_id or "",
        "endpoint": endpoint_id or tab_id or "", "path": path, "sig": signature,
    }
    raw = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return REF_PREFIX + base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def decode_ref(value):
    if not isinstance(value, str) or not value.startswith(REF_PREFIX) or len(value) > 8192:
        raise ValueError("The control ref is malformed; inspect mode=controls and use a returned ref.")
    encoded = value[len(REF_PREFIX):]
    encoded += "=" * ((4 - len(encoded) % 4) % 4)
    try:
        body = json.loads(base64.urlsafe_b64decode(encoded.encode("ascii")).decode("utf-8"))
    except Exception as ex:
        raise ValueError("The control ref could not be decoded; inspect mode=controls again.") from ex
    path = body.get("path")
    if body.get("v") != 1 or not isinstance(path, list) or len(path) > 160 \
            or any(not isinstance(part, int) or part < -1 or part > 1000000 for part in path):
        raise ValueError("The control ref is invalid or from an unsupported helper version.")
    return body


def error_result(code, message, **details):
    result = {"ok": False, "error": {"code": code, "message": bounded_text(message, 1200)}}
    for key, value in details.items():
        if value is not None:
            result["error"][key] = value
    return result


def flatten_frame_tree(frame_tree, depth=0, output=None):
    output = output if output is not None else []
    if not isinstance(frame_tree, dict) or len(output) >= MAX_FRAMES:
        return output
    frame = frame_tree.get("frame") or {}
    if frame.get("id"):
        output.append({
            "id": frame.get("id"), "url": frame.get("url") or "",
            "name": frame.get("name") or "", "depth": depth,
        })
    for child in frame_tree.get("childFrames") or []:
        flatten_frame_tree(child, depth + 1, output)
    return output


def isolated_worlds(session, tab):
    """One isolated execution world per frame. Page.createIsolatedWorld crosses normal
    same-origin boundaries. Site-isolated OOPIF targets are used as a bounded fallback."""
    session.call("Page.enable")
    session.call("Runtime.enable")
    tree = session.call("Page.getFrameTree").get("frameTree") or {}
    frames = flatten_frame_tree(tree)
    targets = {item.get("id"): item for item in list_targets()
               if item.get("type") == "iframe" and item.get("id")}
    worlds = []
    warnings = []
    for frame in frames[:MAX_FRAMES]:
        try:
            made = session.call("Page.createIsolatedWorld", {
                "frameId": frame["id"],
                "worldName": "klive-structured-control",
                "grantUniveralAccess": False,
            })
            context_id = made.get("executionContextId")
            if not context_id:
                raise RuntimeError("no execution context id")
            worlds.append({
                **frame, "session": session, "contextId": context_id,
                "endpointId": tab.get("id"), "owned": False,
            })
            continue
        except Exception as primary_error:
            target = targets.get(frame["id"])
            if not target or not target.get("webSocketDebuggerUrl"):
                warnings.append("Frame %s could not be inspected in an isolated world: %s"
                                % (frame["id"], bounded_text(primary_error, 180)))
                continue
            child = None
            try:
                child = Session(target["webSocketDebuggerUrl"])
                child.call("Runtime.enable")
                worlds.append({
                    **frame, "session": child, "contextId": None,
                    "endpointId": target.get("id"), "owned": True,
                })
            except Exception as fallback_error:
                if child:
                    child.close()
                warnings.append("Cross-origin frame %s was unavailable: %s"
                                % (frame["id"], bounded_text(fallback_error, 180)))
    return worlds, warnings


def close_owned_worlds(worlds):
    closed = set()
    for world in worlds:
        child = world.get("session")
        if world.get("owned") and child is not None and id(child) not in closed:
            closed.add(id(child))
            child.close()


# Functions live in an isolated JavaScript world, so page scripts cannot monkey-patch the helper's
# Array/String prototypes. Paths are child indexes with -1 marking entry into an open shadow root.
CONTROL_LIBRARY = r"""
const __norm = value => String(value == null ? '' : value).replace(/\s+/g, ' ').trim().toLowerCase();
const __clip = (value, max) => {
  value = String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
  return value.length <= max ? value : value.slice(0, max) + '…';
};
const __rootFor = el => el && el.getRootNode ? el.getRootNode() : document;
const __label = el => {
  try {
    const direct = el.labels ? Array.from(el.labels).map(x => x.innerText || x.textContent || '').join(' ') : '';
    if (direct.trim()) return __clip(direct, 240);
    const parent = el.closest && el.closest('label');
    if (parent) return __clip(parent.innerText || parent.textContent || '', 240);
    const id = el.getAttribute && el.getAttribute('id');
    const root = __rootFor(el);
    if (id && root && root.querySelector) {
      const escaped = globalThis.CSS && CSS.escape ? CSS.escape(id) : id.replace(/["\\]/g, '\\$&');
      const found = root.querySelector('label[for="' + escaped + '"]');
      if (found) return __clip(found.innerText || found.textContent || '', 240);
    }
  } catch (_) {}
  return '';
};
const __role = el => {
  const explicit = (el.getAttribute('role') || '').trim().split(/\s+/)[0];
  if (explicit) return explicit.toLowerCase();
  const tag = el.tagName.toLowerCase();
  const type = (el.getAttribute('type') || '').toLowerCase();
  if (tag === 'button' || (tag === 'input' && ['button','submit','reset','image'].includes(type))) return 'button';
  if (tag === 'a' && el.hasAttribute('href')) return 'link';
  if (tag === 'select') return el.multiple ? 'listbox' : 'combobox';
  if (tag === 'textarea') return 'textbox';
  if (tag === 'input') {
    if (type === 'checkbox') return 'checkbox';
    if (type === 'radio') return 'radio';
    if (type === 'range') return 'slider';
    if (type === 'number') return 'spinbutton';
    if (type === 'search') return 'searchbox';
    if (!['button','submit','reset','image','file','hidden','color'].includes(type)) return 'textbox';
  }
  if (tag === 'summary') return 'button';
  if (/^h[1-6]$/.test(tag)) return 'heading';
  if (tag === 'img' && el.getAttribute('alt')) return 'img';
  if (tag === 'option') return 'option';
  return '';
};
const __editable = el => {
  const tag = el.tagName.toLowerCase();
  const type = (el.getAttribute('type') || '').toLowerCase();
  return tag === 'textarea' || (tag === 'input' && !['button','submit','reset','image','checkbox',
    'radio','range','file','hidden','color'].includes(type)) || el.isContentEditable;
};
const __name = el => {
  const labelled = (el.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean).map(id => {
    const root = __rootFor(el);
    const found = (root && root.getElementById ? root.getElementById(id) : null) || document.getElementById(id);
    return found ? (found.innerText || found.textContent || '') : '';
  }).join(' ');
  const label = __label(el);
  const tag = el.tagName.toLowerCase();
  const type = (el.getAttribute('type') || '').toLowerCase();
  const safeButtonLabel = tag === 'input' && ['button','submit','reset'].includes(type)
    ? el.getAttribute('value') || '' : '';
  const safeText = __editable(el) || tag === 'select' ? '' : (el.innerText || el.textContent || '');
  return __clip(el.getAttribute('aria-label') || labelled || label || el.getAttribute('alt') ||
    safeText || el.getAttribute('placeholder') || el.getAttribute('title') || safeButtonLabel ||
    el.getAttribute('name') || '', 300);
};
const __text = el => __editable(el) || el.tagName.toLowerCase() === 'select'
  ? '' : __clip(el.innerText || el.textContent || '', 400);
const __ancestorHidden = el => {
  let current = el;
  for (let depth = 0; current && depth < 80; depth++) {
    if (current.nodeType === 1) {
      const style = getComputedStyle(current);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' ||
          Number(style.opacity || 1) <= 0 || current.hidden || current.inert ||
          current.getAttribute('aria-hidden') === 'true') return true;
    }
    const root = current.getRootNode && current.getRootNode();
    current = current.parentElement || (root && root.host) || null;
  }
  return false;
};
const __visibility = el => {
  const rect = el.getBoundingClientRect();
  const rendered = rect.width > 1 && rect.height > 1 && !__ancestorHidden(el);
  const inViewport = rendered && rect.bottom > 0 && rect.right > 0 &&
    rect.top < innerHeight && rect.left < innerWidth;
  let clipped = {left: Math.max(0, rect.left), top: Math.max(0, rect.top),
    right: Math.min(innerWidth, rect.right), bottom: Math.min(innerHeight, rect.bottom)};
  let current = el.parentElement;
  for (let depth = 0; current && depth < 60; depth++, current = current.parentElement) {
    const style = getComputedStyle(current);
    if (/(auto|scroll|hidden|clip)/.test(style.overflow + style.overflowX + style.overflowY)) {
      const box = current.getBoundingClientRect();
      clipped.left = Math.max(clipped.left, box.left); clipped.top = Math.max(clipped.top, box.top);
      clipped.right = Math.min(clipped.right, box.right); clipped.bottom = Math.min(clipped.bottom, box.bottom);
    }
  }
  const hitVisible = inViewport && clipped.right - clipped.left > 1 && clipped.bottom - clipped.top > 1;
  return {rendered, inViewport: hitVisible, rect};
};
const __disabled = el => {
  try { if (el.matches(':disabled')) return true; } catch (_) {}
  if (el.disabled || el.getAttribute('aria-disabled') === 'true') return true;
  let parent = el.parentElement;
  for (let i = 0; parent && i < 30; i++, parent = parent.parentElement)
    if (parent.getAttribute('aria-disabled') === 'true' || parent.inert) return true;
  return false;
};
const __testId = el => el.getAttribute('data-testid') || el.getAttribute('data-test') ||
  el.getAttribute('data-cy') || '';
const __candidate = el => {
  const tag = el.tagName.toLowerCase();
  const type = (el.getAttribute('type') || '').toLowerCase();
  if (tag === 'input' && type === 'hidden') return false;
  return ['button','input','select','textarea','a','summary','label','option'].includes(tag) ||
    /^h[1-6]$/.test(tag) || !!el.getAttribute('role') || el.isContentEditable ||
    el.tabIndex >= 0 || el.hasAttribute('onclick') || !!__testId(el);
};
const __matchText = (actual, wanted, exact) => {
  actual = __norm(actual); wanted = __norm(wanted);
  return !wanted || (exact ? actual === wanted : actual.includes(wanted));
};
let __cssError = '';
const __match = (el, q) => {
  q = q || {};
  const exact = q.exact === true;
  if (q.css) {
    try { if (!el.matches(q.css)) return false; }
    catch (error) { __cssError = String(error && error.message || error); return false; }
  }
  const role = __role(el), tag = el.tagName.toLowerCase(), name = __name(el), text = __text(el);
  if (q.role && __norm(role) !== __norm(q.role)) return false;
  if (q.tag && __norm(tag) !== __norm(q.tag)) return false;
  if (q.name && !__matchText(name, q.name, exact)) return false;
  if (q.text && !__matchText(text || name, q.text, exact)) return false;
  if (q.label && !__matchText(__label(el), q.label, exact)) return false;
  if (q.placeholder && !__matchText(el.getAttribute('placeholder') || '', q.placeholder, exact)) return false;
  if (q.testId && !__matchText(__testId(el), q.testId, exact)) return false;
  return true;
};
const __meta = (el, path) => {
  const visibility = __visibility(el), rect = visibility.rect;
  const tag = el.tagName.toLowerCase(), type = (el.getAttribute('type') || '').toLowerCase();
  const editable = __editable(el);
  let hasValue = false;
  try {
    hasValue = tag === 'input' && type === 'file' ? !!(el.files && el.files.length) :
      editable ? !!(el.isContentEditable ? el.textContent : el.value) : false;
  } catch (_) {}
  const options = tag === 'select' ? Array.from(el.options || []).slice(0, 80).map((option, index) => ({
    index, text: __clip(option.textContent || option.label || '', 180),
    selected: !!option.selected, disabled: !!option.disabled
  })) : undefined;
  return {
    tag, role: __role(el), name: __name(el), text: __text(el), label: __label(el),
    placeholder: __clip(el.getAttribute('placeholder') || '', 200),
    testId: __clip(__testId(el), 160), type, id: __clip(el.id || '', 160),
    htmlName: __clip(el.getAttribute('name') || '', 160),
    visible: visibility.rendered, inViewport: visibility.inViewport,
    disabled: __disabled(el), readonly: !!el.readOnly || el.getAttribute('aria-readonly') === 'true',
    required: !!el.required || el.getAttribute('aria-required') === 'true',
    checked: typeof el.checked === 'boolean' ? !!el.checked :
      (el.getAttribute('aria-checked') == null ? undefined : el.getAttribute('aria-checked') === 'true'),
    expanded: el.getAttribute('aria-expanded') == null ? undefined : el.getAttribute('aria-expanded') === 'true',
    selected: el.getAttribute('aria-selected') == null ? undefined : el.getAttribute('aria-selected') === 'true',
    editable, focused: document.activeElement === el, multiple: !!el.multiple, hasValue,
    href: tag === 'a' ? __clip(el.href || '', 1400) : undefined,
    accept: tag === 'input' && type === 'file' ? __clip(el.accept || '', 240) : undefined,
    attached: tag === 'input' && type === 'file' && el.files ? el.files.length : undefined,
    options,
    bounds: {x: Math.round(rect.x), y: Math.round(rect.y),
      width: Math.round(rect.width), height: Math.round(rect.height)},
    _path: path
  };
};
const __resolvePath = path => {
  let root = document, current = null;
  if (!Array.isArray(path)) return null;
  for (const part of path) {
    if (part === -1) {
      if (!current || !current.shadowRoot) return null;
      root = current.shadowRoot; current = null; continue;
    }
    const children = root && root.children ? root.children : null;
    if (!children || part < 0 || part >= children.length) return null;
    current = children[part]; root = current;
  }
  return current;
};
const __collect = (query, limit, onlyMatches) => {
  const items = []; let total = 0; let visited = 0;
  const walk = (root, path, depth) => {
    if (!root || depth > 100 || visited > 50000) return;
    const children = Array.from(root.children || []);
    for (let index = 0; index < children.length; index++) {
      if (visited++ > 50000) return;
      const el = children[index], next = path.concat(index);
      const matches = __match(el, query || {});
      if ((!onlyMatches && __candidate(el)) || (onlyMatches && matches)) {
        if (!onlyMatches || matches) {
          total++;
          if (items.length < limit) items.push(__meta(el, next));
        }
      }
      if (el.shadowRoot) walk(el.shadowRoot, next.concat(-1), depth + 1);
      walk(el, next, depth + 1);
    }
  };
  walk(document, [], 0);
  return {items, total, truncated: total > items.length, cssError: __cssError};
};
"""


def js_expression(tail, query=None):
    return "(() => {\n" + CONTROL_LIBRARY + "\nconst __query = " + \
        json.dumps(query or {}, ensure_ascii=False) + ";\n" + tail + "\n})()"


def requested_tab(tabs, payload, decoded=None):
    tab_id = (decoded or {}).get("tab") or payload.get("tabId")
    if tab_id:
        for index, tab in enumerate(tabs):
            if tab.get("id") == tab_id:
                return index, tab
        raise RuntimeError("The referenced browser tab is no longer open; inspect mode=tabs again.")
    requested = payload.get("tabIndex")
    try:
        requested = int(requested) if requested is not None else None
    except (TypeError, ValueError):
        raise RuntimeError("tabIndex must be an integer from inspect mode=tabs.")
    index = resolve_index(tabs, requested)
    return index, tabs[index]


def tab_descriptor(tab, index, active=None):
    return {
        "id": tab.get("id"), "index": index,
        "title": bounded_text(tab.get("title") or "", 300),
        "url": safe_url(tab.get("url") or ""),
        "active": (index == active) if active is not None else None,
        "openerId": tab.get("openerId"),
    }


def javascript_dialog_from_events(session):
    for event in reversed(session.events):
        if event.get("method") == "Page.javascriptDialogOpening":
            params = event.get("params") or {}
            return {
                "open": True, "type": params.get("type"),
                "message": bounded_text(params.get("message") or "", 1000),
                "hasBrowserHandler": bool(params.get("hasBrowserHandler")),
            }
        if event.get("method") == "Page.javascriptDialogClosed":
            return {"open": False}
    return {"open": False}


def read_page_state(session, tab, index, tabs):
    state = {
        "title": bounded_text(tab.get("title") or "", 300),
        "url": safe_url(tab.get("url") or ""),
        "readyState": "unknown", "visibility": "unknown",
    }
    try:
        value = session.evaluate(
            "(() => ({title:document.title,url:location.href,readyState:document.readyState,"
            "visibility:document.visibilityState,activeElement:(()=>{const x=document.activeElement;"
            "if(!x)return null;const t=(x.tagName||'').toLowerCase();const ty=(x.getAttribute&&x.getAttribute('type')||'').toLowerCase();"
            "return {tag:t,type:ty,role:x.getAttribute&&x.getAttribute('role')||'',"
            "name:x.getAttribute&&x.getAttribute('name')||'',id:x.id||'',"
            "editable:t==='textarea'||(t==='input'&&!['button','submit','reset','checkbox','radio','file','hidden'].includes(ty))||!!x.isContentEditable};})()}))()",
            timeout=4)
        if isinstance(value, dict):
            state.update({
                "title": bounded_text(value.get("title") or state["title"], 300),
                "url": safe_url(value.get("url") or state["url"]),
                "readyState": value.get("readyState") or "unknown",
                "visibility": value.get("visibility") or "unknown",
                "activeElement": value.get("activeElement"),
            })
    except Exception as ex:
        state["inspectionError"] = bounded_text(ex, 300)
    state["javascriptDialog"] = javascript_dialog_from_events(session)
    state["nativeDialog"] = detect_native_dialog()
    state["tab"] = tab_descriptor(tab, index, active_index(tabs) if tabs else 0)
    state["tabCount"] = len(tabs)
    return state


def locator_from_payload(payload):
    locator = {}
    for key in ("name", "text", "role", "tag", "css", "label", "placeholder", "testId"):
        value = payload.get(key)
        if value is not None and str(value).strip():
            value = str(value).strip()
            maximum = 1200 if key == "css" else 500
            if len(value) > maximum:
                raise ValueError("%s selector is too long (maximum %d characters)." % (key, maximum))
            locator[key] = value
    if payload.get("exact") is True:
        locator["exact"] = True
    return locator


def sanitize_control(item, tab, world):
    clean = {key: value for key, value in item.items() if key != "_path" and value is not None}
    clean["frame"] = {
        "id": world.get("id"), "url": safe_url(world.get("url") or ""),
        "name": bounded_text(world.get("name") or "", 160), "depth": world.get("depth", 0),
    }
    if clean.get("href"):
        clean["href"] = safe_url(clean["href"])
    if isinstance(clean.get("options"), list):
        clean["options"] = clean["options"][:80]
    signature = control_signature(clean)
    clean["ref"] = encode_ref(tab.get("id"), world.get("id"), world.get("endpointId"),
                              item.get("_path") or [], signature)
    return clean


def collect_world(session, tab, world, query, limit, only_matches):
    expression = js_expression(
        "return __collect(__query, %d, %s);" %
        (max(1, min(MAX_CONTROL_SCAN, int(limit))), "true" if only_matches else "false"),
        query)
    raw = world["session"].evaluate_in_context(
        expression, world.get("contextId"), timeout=20, return_by_value=True).get("value")
    raw = raw if isinstance(raw, dict) else {}
    items = []
    for item in raw.get("items") or []:
        if isinstance(item, dict):
            items.append(sanitize_control(item, tab, world))
    return {
        "items": items, "total": int(raw.get("total") or 0),
        "truncated": bool(raw.get("truncated")), "cssError": raw.get("cssError") or "",
    }


def inspect_controls(tab, index, tabs, limit):
    session = session_for(tab)
    worlds = []
    try:
        worlds, warnings = isolated_worlds(session, tab)
        controls = []
        total = 0
        truncated = False
        frame_results = []
        for world in worlds:
            remaining = max(1, limit - len(controls))
            try:
                result = collect_world(session, tab, world, {}, remaining, False)
                total += result["total"]
                if len(controls) < limit:
                    controls.extend(result["items"][:limit - len(controls)])
                truncated = truncated or result["truncated"] or total > len(controls)
                frame_results.append({
                    "id": world.get("id"), "url": safe_url(world.get("url") or ""),
                    "name": bounded_text(world.get("name") or "", 160),
                    "depth": world.get("depth", 0), "controlCount": result["total"],
                })
            except Exception as ex:
                warnings.append("Frame %s control scan failed: %s" %
                                (world.get("id"), bounded_text(ex, 240)))
        state = read_page_state(session, tab, index, tabs)
        return {
            "title": state.get("title"), "url": state.get("url"),
            "readyState": state.get("readyState"), "visibility": state.get("visibility"),
            "controls": controls, "controlCount": total, "returned": len(controls),
            "truncated": truncated, "frames": frame_results[:MAX_FRAMES],
            "warnings": warnings[:20], "javascriptDialog": state.get("javascriptDialog"),
            "nativeDialog": state.get("nativeDialog"), "tabIndex": index, "tabCount": len(tabs),
        }
    finally:
        close_owned_worlds(worlds)
        session.close()


def raw_item_for_path(world, path):
    expression = js_expression(
        "const __selected = __resolvePath(%s);"
        "if (!__selected) return {missing:true};"
        "globalThis.__kliveSelected = __selected;"
        "return {missing:false,item:__meta(__selected,%s)};" %
        (json.dumps(path, separators=(",", ":")), json.dumps(path, separators=(",", ":"))))
    value = world["session"].evaluate_in_context(
        expression, world.get("contextId"), timeout=12, return_by_value=True).get("value")
    return value if isinstance(value, dict) else {"missing": True}


def select_control(tab, worlds, payload, decoded_ref=None):
    """Resolve a ref or locator, leave the chosen node in its isolated world's
    globalThis.__kliveSelected, and return (world, sanitized item) or an error dict."""
    if decoded_ref:
        if decoded_ref.get("tab") != tab.get("id"):
            return None, None, error_result(
                "stale-ref", "The ref belongs to a different browser tab; inspect controls again.")
        world = next((candidate for candidate in worlds
                      if candidate.get("id") == decoded_ref.get("frame")), None)
        if world is None:
            return None, None, error_result(
                "stale-ref", "The ref's frame was detached or navigated; inspect controls again.")
        try:
            raw = raw_item_for_path(world, decoded_ref.get("path") or [])
        except Exception as ex:
            return None, None, error_result(
                "stale-ref", "The referenced frame changed: " + bounded_text(ex, 300))
        if raw.get("missing") or not isinstance(raw.get("item"), dict):
            return None, None, error_result(
                "stale-ref", "The referenced element no longer exists; inspect controls again.")
        item = sanitize_control(raw["item"], tab, world)
        if decoded_ref.get("sig") != control_signature(item):
            return None, None, error_result(
                "stale-ref", "The DOM position now identifies a different control; inspect controls again.",
                current=item)
        return world, item, None

    try:
        locator = locator_from_payload(payload)
    except ValueError as ex:
        return None, None, error_result("invalid-selector", str(ex))
    if not locator:
        return None, None, error_result(
            "missing-target",
            "Provide ref or at least one of name, text, role, tag, css, label, placeholder, or testId.")
    occurrence_supplied = "occurrence" in payload
    try:
        occurrence = max(0, int(payload.get("occurrence") or 0))
    except (TypeError, ValueError):
        return None, None, error_result("invalid-selector", "occurrence must be a non-negative integer.")
    scan_limit = min(MAX_CONTROL_SCAN, max(16, occurrence + 16))
    matches = []
    total = 0
    for world in worlds:
        try:
            result = collect_world(world["session"], tab, world, locator, scan_limit, True)
        except Exception as ex:
            return None, None, error_result(
                "inspection-failed", "Could not search frame %s: %s" %
                (world.get("id"), bounded_text(ex, 300)))
        if result["cssError"]:
            return None, None, error_result(
                "invalid-selector", "The CSS selector is invalid: " + bounded_text(result["cssError"], 300))
        total += result["total"]
        matches.extend((world, item) for item in result["items"])
    if total == 0:
        return None, None, error_result(
            "not-found", "No element matched the structured locator.",
            locator=locator)
    candidates = [item for _, item in matches[:8]]
    if not occurrence_supplied and total > 1:
        return None, None, error_result(
            "ambiguous", "The locator matched %d elements. Add exact=true, another locator field, "
            "or an explicit occurrence." % total, matchCount=total, candidates=candidates)
    if occurrence >= total:
        return None, None, error_result(
            "occurrence-out-of-range", "Occurrence %d does not exist; %d element(s) matched." %
            (occurrence, total), matchCount=total, candidates=candidates)
    if occurrence >= len(matches):
        return None, None, error_result(
            "too-many-matches", "The requested occurrence is beyond the bounded scan. "
            "Use a more specific locator or a ref from inspect mode=controls.",
            matchCount=total, scanLimit=MAX_CONTROL_SCAN, candidates=candidates)
    world, item = matches[occurrence]
    try:
        decoded = decode_ref(item["ref"])
        raw = raw_item_for_path(world, decoded["path"])
    except Exception as ex:
        return None, None, error_result(
            "stale-target", "The matched element changed before it could be selected: " +
            bounded_text(ex, 300))
    if raw.get("missing"):
        return None, None, error_result(
            "stale-target", "The matched element disappeared before the action; inspect and retry.")
    refreshed = sanitize_control(raw["item"], tab, world)
    return world, refreshed, None


def selected_eval(world, body, timeout=15):
    expression = "(() => { const el = globalThis.__kliveSelected; " \
                 "if (!el || !el.isConnected) return {missing:true}; " + body + " })()"
    result = world["session"].evaluate_in_context(
        expression, world.get("contextId"), timeout=timeout, return_by_value=True).get("value")
    return result if isinstance(result, dict) else {}


def refresh_selected(tab, world):
    expression = js_expression(
        "const el=globalThis.__kliveSelected;"
        "if(!el||!el.isConnected)return {missing:true};"
        "return {missing:false,item:__meta(el,[])};")
    value = world["session"].evaluate_in_context(
        expression, world.get("contextId"), timeout=10, return_by_value=True).get("value")
    if not isinstance(value, dict) or value.get("missing") or not isinstance(value.get("item"), dict):
        return None
    # A refreshed action result deliberately has no reusable ref: its empty synthetic path would
    # resolve the document element. The pre-action ref remains the durable handle while still live.
    clean = {key: item for key, item in value["item"].items()
             if key != "_path" and item is not None}
    clean["frame"] = {
        "id": world.get("id"), "url": safe_url(world.get("url") or ""),
        "name": bounded_text(world.get("name") or "", 160), "depth": world.get("depth", 0),
    }
    if clean.get("href"):
        clean["href"] = safe_url(clean["href"])
    return clean


def selected_object_id(world):
    remote = world["session"].evaluate_in_context(
        "globalThis.__kliveSelected", world.get("contextId"),
        timeout=8, return_by_value=False)
    return remote.get("objectId")


def frame_owner_offset(top_session, frame_id):
    try:
        owner = top_session.call("DOM.getFrameOwner", {"frameId": frame_id}, timeout=8)
        params = {}
        if owner.get("backendNodeId"):
            params["backendNodeId"] = owner["backendNodeId"]
        elif owner.get("nodeId"):
            params["nodeId"] = owner["nodeId"]
        else:
            return 0.0, 0.0
        model = top_session.call("DOM.getBoxModel", params, timeout=8).get("model") or {}
        quad = model.get("content") or model.get("border") or []
        if len(quad) >= 8:
            return min(quad[0::2]), min(quad[1::2])
    except Exception:
        pass
    return 0.0, 0.0


def selected_geometry(top_session, world):
    object_id = selected_object_id(world)
    if not object_id:
        return None
    try:
        result = world["session"].call("DOM.getContentQuads", {"objectId": object_id}, timeout=10)
        quads = [quad for quad in (result.get("quads") or []) if isinstance(quad, list) and len(quad) >= 8]
    except Exception:
        quads = []
    if not quads:
        return None
    # Prefer a non-zero quad, then the largest. Coordinates are viewport CSS pixels.
    def area(quad):
        xs, ys = quad[0::2], quad[1::2]
        return max(0.0, max(xs) - min(xs)) * max(0.0, max(ys) - min(ys))
    quad = max(quads, key=area)
    offset_x, offset_y = (frame_owner_offset(top_session, world.get("id"))
                          if world.get("owned") else (0.0, 0.0))
    xs = [number + offset_x for number in quad[0::2]]
    ys = [number + offset_y for number in quad[1::2]]
    left, right, top, bottom = min(xs), max(xs), min(ys), max(ys)
    viewport_x, viewport_y = (left + right) / 2.0, (top + bottom) / 2.0
    local_hit = selected_eval(world, r"""
      const rect=el.getBoundingClientRect(), cx=rect.left+rect.width/2, cy=rect.top+rect.height/2;
      const root=el.getRootNode&&el.getRootNode();
      const hit=(root&&root.elementFromPoint?root.elementFromPoint(cx,cy):document.elementFromPoint(cx,cy));
      const composedContains=(ancestor,node)=>{
        for(let x=node,depth=0;x&&depth<80;depth++){
          if(x===ancestor)return true;
          const r=x.getRootNode&&x.getRootNode();
          x=x.parentElement||(r&&r.host)||null;
        }
        return false;
      };
      const intercepted=!!hit&&!composedContains(el,hit);
      const safeName=x=>x?(x.getAttribute&&x.getAttribute('aria-label')||x.innerText||x.textContent||'').replace(/\s+/g,' ').trim().slice(0,160):'';
      return {intercepted,interceptedBy:intercepted?{tag:(hit.tagName||'').toLowerCase(),
        role:hit.getAttribute&&hit.getAttribute('role')||'',name:safeName(hit)}:null};
    """)
    metrics = top_session.evaluate(
        "(() => ({screenX:window.screenX||0,screenY:window.screenY||0,"
        "outerWidth:window.outerWidth||window.innerWidth,outerHeight:window.outerHeight||window.innerHeight,"
        "innerWidth:window.innerWidth,innerHeight:window.innerHeight,devicePixelRatio:window.devicePixelRatio||1}))()",
        timeout=6) or {}
    border_x = max(0.0, (float(metrics.get("outerWidth") or 0) -
                         float(metrics.get("innerWidth") or 0)) / 2.0)
    browser_top = max(0.0, float(metrics.get("outerHeight") or 0) -
                      float(metrics.get("innerHeight") or 0) - border_x)
    scale = max(0.25, min(8.0, float(metrics.get("devicePixelRatio") or 1.0)))
    screen_left = (float(metrics.get("screenX") or 0) + border_x + left) * scale
    screen_top = (float(metrics.get("screenY") or 0) + browser_top + top) * scale
    return {
        "x": int(round((float(metrics.get("screenX") or 0) + border_x + viewport_x) * scale)),
        "y": int(round((float(metrics.get("screenY") or 0) + browser_top + viewport_y) * scale)),
        "bounds": {
            "x": int(round(screen_left)), "y": int(round(screen_top)),
            "width": max(0, int(round((right - left) * scale))),
            "height": max(0, int(round((bottom - top) * scale))),
        },
        "viewport": {
            "x": int(round(viewport_x)), "y": int(round(viewport_y)),
            "bounds": {"x": int(round(left)), "y": int(round(top)),
                       "width": max(0, int(round(right - left))),
                       "height": max(0, int(round(bottom - top)))},
        },
        "intercepted": bool(local_hit.get("intercepted")),
        "interceptedBy": local_hit.get("interceptedBy"),
    }


def insert_text(session, text):
    session.call("Input." + "insertText", {"text": text}, timeout=30)


KEY_DATA = {
    "enter": ("Enter", "Enter", 13), "return": ("Enter", "Enter", 13),
    "tab": ("Tab", "Tab", 9), "escape": ("Escape", "Escape", 27), "esc": ("Escape", "Escape", 27),
    "backspace": ("Backspace", "Backspace", 8), "delete": ("Delete", "Delete", 46),
    "space": (" ", "Space", 32), "spacebar": (" ", "Space", 32),
    "arrowup": ("ArrowUp", "ArrowUp", 38), "up": ("ArrowUp", "ArrowUp", 38),
    "arrowdown": ("ArrowDown", "ArrowDown", 40), "down": ("ArrowDown", "ArrowDown", 40),
    "arrowleft": ("ArrowLeft", "ArrowLeft", 37), "left": ("ArrowLeft", "ArrowLeft", 37),
    "arrowright": ("ArrowRight", "ArrowRight", 39), "right": ("ArrowRight", "ArrowRight", 39),
    "home": ("Home", "Home", 36), "end": ("End", "End", 35),
    "pageup": ("PageUp", "PageUp", 33), "pagedown": ("PageDown", "PageDown", 34),
}


def dispatch_key(session, chord, repeats=1):
    pieces = [part.strip().lower() for part in re.split(r"\+", chord or "") if part.strip()]
    if not pieces:
        raise ValueError("press requires key (for example Enter, Tab, ctrl+a, or ArrowDown).")
    modifiers = 0
    key_name = None
    for piece in pieces:
        if piece in ("alt", "option"):
            modifiers |= 1
        elif piece in ("ctrl", "control"):
            modifiers |= 2
        elif piece in ("meta", "super", "win", "command", "cmd"):
            modifiers |= 4
        elif piece == "shift":
            modifiers |= 8
        elif key_name is None:
            key_name = piece
        else:
            raise ValueError("press accepts one non-modifier key per chord.")
    if not key_name:
        raise ValueError("press requires a non-modifier key.")
    if key_name in KEY_DATA:
        key, code, virtual = KEY_DATA[key_name]
    elif len(key_name) == 1:
        key = key_name.upper() if modifiers & 8 else key_name
        if key_name.isalpha():
            code, virtual = "Key" + key_name.upper(), ord(key_name.upper())
        elif key_name.isdigit():
            code, virtual = "Digit" + key_name, ord(key_name)
        else:
            code, virtual = "", ord(key_name)
    elif re.match(r"^f([1-9]|1[0-2])$", key_name):
        number = int(key_name[1:])
        key, code, virtual = key_name.upper(), key_name.upper(), 111 + number
    else:
        key, code, virtual = key_name, key_name, 0
    method = "Input." + "dispatchKeyEvent"
    for _ in range(max(1, min(50, int(repeats)))):
        common = {
            "key": key, "code": code, "windowsVirtualKeyCode": virtual,
            "nativeVirtualKeyCode": virtual, "modifiers": modifiers,
        }
        session.call(method, dict(common, type="rawKeyDown"), timeout=10)
        if len(key) == 1 and not (modifiers & (1 | 2 | 4)):
            session.call(method, dict(common, type="char", text=key,
                                      unmodifiedText=key), timeout=10)
        session.call(method, dict(common, type="keyUp"), timeout=10)


def click_geometry(session, geometry, button="left", clicks=1):
    if not geometry:
        raise RuntimeError("The selected control has no usable on-screen geometry.")
    point = geometry["viewport"]
    x, y = point["x"], point["y"]
    method = "Input." + "dispatchMouseEvent"
    button = button if button in ("left", "middle", "right") else "left"
    clicks = max(1, min(2, int(clicks)))
    session.call(method, {"type": "mouseMoved", "x": x, "y": y}, timeout=10)
    session.call(method, {"type": "mousePressed", "x": x, "y": y, "button": button,
                          "clickCount": clicks}, timeout=10)
    session.call(method, {"type": "mouseReleased", "x": x, "y": y, "button": button,
                          "clickCount": clicks}, timeout=10)


def hover_geometry(session, geometry):
    if not geometry:
        raise RuntimeError("The selected control has no usable on-screen geometry.")
    point = geometry["viewport"]
    session.call("Input." + "dispatchMouseEvent", {
        "type": "mouseMoved", "x": point["x"], "y": point["y"],
    }, timeout=10)


def wait_page_ready(session, timeout_seconds=20):
    deadline = time.time() + max(0.1, timeout_seconds)
    last = {"readyState": "unknown", "url": "", "title": ""}
    while time.time() < deadline:
        try:
            value = session.evaluate(
                "(() => ({readyState:document.readyState,url:location.href,title:document.title}))()",
                timeout=min(5, max(1, deadline - time.time())))
            if isinstance(value, dict):
                last = value
                if value.get("readyState") in ("interactive", "complete"):
                    return last
        except Exception:
            pass
        time.sleep(0.2)
    return last


def control_state_matches(item, state):
    state = (state or "visible").strip().lower()
    if state == "attached":
        return item is not None
    if state == "detached":
        return item is None
    if state == "visible":
        return item is not None and item.get("visible") and item.get("inViewport")
    if state == "hidden":
        return item is None or not item.get("visible") or not item.get("inViewport")
    if state == "enabled":
        return item is not None and not item.get("disabled")
    if state == "disabled":
        return item is not None and bool(item.get("disabled"))
    if state == "checked":
        return item is not None and item.get("checked") is True
    if state == "unchecked":
        return item is not None and item.get("checked") is False
    if state == "focused":
        return item is not None and bool(item.get("focused"))
    if state == "editable":
        return item is not None and bool(item.get("editable")) and not item.get("readonly")
    if state == "filled":
        return item is not None and bool(item.get("hasValue"))
    if state == "empty":
        return item is not None and not bool(item.get("hasValue"))
    raise ValueError("wait state must be attached, detached, visible, hidden, enabled, disabled, "
                     "checked, unchecked, focused, editable, filled, or empty.")


def wait_for_control(payload, tab, index, session, decoded_ref, before_tabs):
    # Compatibility vocabulary used by ContainerToolAdapter's public action schema.
    condition = str(payload.get("condition") or "").strip().lower()
    wait_for = str(payload.get("waitFor") or "").strip()
    if condition:
        if condition in ("text", "selector", "url", "gone") and not wait_for:
            return error_result(
                "invalid-wait", "condition=%s requires a non-empty waitFor value." % condition)
        payload = dict(payload)
        if condition == "text":
            # Page text is not necessarily an actionable control. Probe body.innerText directly
            # instead of feeding the phrase to the semantic control locator (where ancestor text
            # commonly creates ambiguous matches).
            payload["pageText"] = wait_for
        elif condition == "selector":
            payload["css"] = wait_for
            payload["state"] = "visible"
        elif condition == "url":
            payload["urlContains"] = wait_for
        elif condition == "ready":
            payload["readyState"] = wait_for or "complete"
        elif condition == "gone":
            # Public contract: gone is the detached/hidden counterpart to condition=selector.
            payload["css"] = wait_for
            payload["state"] = "hidden"
        else:
            return error_result(
                "invalid-wait",
                "condition must be text, selector, url, ready, or gone.")
    timeout_ms = max(50, min(120000, int(payload.get("timeoutMs") or payload.get("maxMs") or 15000)))
    poll_ms = max(50, min(2000, int(payload.get("pollMs") or 200)))
    state_name = str(payload.get("state") or ("visible" if decoded_ref or locator_from_payload(payload)
                                              else "")).strip().lower()
    url_contains = str(payload.get("urlContains") or "")
    url_regex_text = str(payload.get("urlRegex") or "")
    title_contains = str(payload.get("titleContains") or "")
    wanted_ready = str(payload.get("readyState") or payload.get("loadState") or "").strip().lower()
    page_text = str(payload.get("pageText") or payload.get("waitForText") or "")
    native_wanted = payload.get("nativeDialog")
    network_idle_ms = max(0, min(30000, int(payload.get("networkIdleMs") or 0)))
    if len(url_regex_text) > 500:
        return error_result("invalid-wait", "urlRegex is too long.")
    try:
        url_regex = re.compile(url_regex_text) if url_regex_text else None
        if state_name:
            control_state_matches(None, state_name)
    except (ValueError, re.error) as ex:
        return error_result("invalid-wait", str(ex))
    if not any((state_name, url_contains, url_regex, title_contains, wanted_ready,
                page_text, native_wanted is not None, network_idle_ms)):
        wanted_ready = "complete"

    deadline = time.time() + timeout_ms / 1000.0
    last_item = None
    last_state = {}
    last_error = None
    while True:
        worlds = []
        try:
            worlds, warnings = isolated_worlds(session, tab)
            item = None
            if state_name:
                world, item, selection_error = select_control(tab, worlds, payload, decoded_ref)
                if selection_error:
                    code = selection_error.get("error", {}).get("code")
                    if state_name in ("detached", "hidden") and code in (
                            "not-found", "stale-ref", "stale-target"):
                        item = None
                    else:
                        last_error = selection_error
                        item = None
                else:
                    last_error = None
            last_item = item
            try:
                probe = session.evaluate(
                    "(() => {const entries=performance.getEntriesByType('resource');"
                    "const latest=entries.reduce((m,x)=>Math.max(m,x.responseEnd||x.startTime||0),0);"
                    "return {url:location.href,title:document.title,readyState:document.readyState,"
                    "pageText:%s,networkQuietFor:Math.max(0,performance.now()-latest)};})()" %
                    (json.dumps(page_text.lower()) +
                     " ? (document.body?.innerText||'').toLowerCase().includes(" +
                     json.dumps(page_text.lower()) + ") : true"),
                    timeout=5) or {}
            except Exception as ex:
                probe = {"url": tab.get("url") or "", "title": tab.get("title") or "",
                         "readyState": "unknown", "pageText": False, "networkQuietFor": 0,
                         "error": bounded_text(ex, 240)}
            dialog = detect_native_dialog()
            tabs_now = list_tabs()
            popup_ids = [candidate.get("id") for candidate in tabs_now
                         if candidate.get("id") not in before_tabs]
            conditions = []
            if state_name:
                conditions.append(control_state_matches(item, state_name))
            if url_contains:
                conditions.append(url_contains.lower() in str(probe.get("url") or "").lower())
            if url_regex:
                conditions.append(bool(url_regex.search(str(probe.get("url") or ""))))
            if title_contains:
                conditions.append(title_contains.lower() in str(probe.get("title") or "").lower())
            if wanted_ready:
                if wanted_ready in ("domcontentloaded", "interactive"):
                    conditions.append(probe.get("readyState") in ("interactive", "complete"))
                elif wanted_ready in ("load", "complete", "networkidle", "network_idle"):
                    conditions.append(probe.get("readyState") == "complete")
                else:
                    conditions.append(str(probe.get("readyState") or "").lower() == wanted_ready)
            if page_text:
                conditions.append(bool(probe.get("pageText")))
            if native_wanted is not None:
                conditions.append(bool(dialog.get("open")) == bool(native_wanted))
            if network_idle_ms:
                conditions.append(float(probe.get("networkQuietFor") or 0) >= network_idle_ms)
            if wanted_ready in ("networkidle", "network_idle") and not network_idle_ms:
                conditions.append(float(probe.get("networkQuietFor") or 0) >= 500)
            last_state = {
                "url": safe_url(probe.get("url") or ""), "title": bounded_text(probe.get("title") or "", 300),
                "readyState": probe.get("readyState"), "nativeDialog": dialog,
                "popupTabIds": popup_ids[:20], "warnings": warnings[:8],
            }
            if conditions and all(conditions):
                return {
                    "ok": True, "condition": {
                        "state": state_name or None, "urlContains": url_contains or None,
                        "urlRegex": url_regex_text or None, "titleContains": title_contains or None,
                        "readyState": wanted_ready or None, "pageTextFound": bool(page_text) or None,
                        "nativeDialog": native_wanted, "networkIdleMs": network_idle_ms or None,
                    },
                    "control": item, "state": last_state,
                    "elapsedMs": max(0, timeout_ms - int(max(0, deadline - time.time()) * 1000)),
                }
        except Exception as ex:
            last_error = error_result("wait-probe-failed", bounded_text(ex, 500))
        finally:
            close_owned_worlds(worlds)
        if time.time() >= deadline:
            detail = last_error.get("error") if isinstance(last_error, dict) else last_error
            return error_result(
                "timeout", "The structured wait timed out after %dms." % timeout_ms,
                lastControl=last_item, lastState=last_state, lastError=detail)
        time.sleep(min(poll_ms / 1000.0, max(0.01, deadline - time.time())))


SCRIPT_BLOCKS = [
    (re.compile(r"\bdocument\s*\.\s*cookie\b", re.I), "document.cookie"),
    (re.compile(r"\b(cookieStore|localStorage|sessionStorage|indexedDB|caches)\b", re.I), "browser storage"),
    (re.compile(r"\bnavigator\s*\.\s*credentials\b", re.I), "stored credentials"),
    (re.compile(r"\b(password|passwd)\b", re.I), "password data"),
    (re.compile(r"(?:\.|\[\s*['\"])\s*value(?:['\"]\s*\])?\b", re.I), "input values"),
    (re.compile(r"\b(FormData|XMLHttpRequest|WebSocket|EventSource|sendBeacon)\b", re.I), "network/form exfiltration"),
    (re.compile(r"\bfetch\s*\(", re.I), "network requests"),
    (re.compile(r"\b(?:location|document\s*\.\s*location)\s*=", re.I), "scripted network navigation"),
]
SENSITIVE_RESULT_KEY = re.compile(
    r"authorization|cookie|credential|password|secret|storage|token|value", re.I)


def sanitize_script_result(value, depth=0):
    if depth > 5:
        return "<max-depth>"
    if value is None or isinstance(value, (bool, int, float)):
        if isinstance(value, float) and (math.isnan(value) or math.isinf(value)):
            return str(value)
        return value
    if isinstance(value, str):
        return bounded_text(value, 2000)
    if isinstance(value, list):
        items = [sanitize_script_result(item, depth + 1) for item in value[:80]]
        if len(value) > 80:
            items.append("<truncated>")
        return items
    if isinstance(value, dict):
        result = {}
        for key, item in list(value.items())[:80]:
            key = bounded_text(key, 160)
            result[key] = "<redacted>" if SENSITIVE_RESULT_KEY.search(key) \
                else sanitize_script_result(item, depth + 1)
        if len(value) > 80:
            result["<truncated>"] = True
        return result
    return bounded_text(value, 1000)


def run_guarded_script(payload, worlds):
    code = payload.get("script")
    expression = payload.get("expression")
    if code is None and expression is None:
        return error_result("missing-script", "script requires 'script' (function body) or 'expression'.")
    source = str(expression if expression is not None else code)
    if not source.strip():
        return error_result("missing-script", "The script is empty.")
    if len(source) > 16000:
        return error_result("script-too-large", "The guarded script is limited to 16,000 characters.")
    for pattern, label in SCRIPT_BLOCKS:
        if pattern.search(source):
            return error_result(
                "script-blocked",
                "The last-resort script was blocked because it references %s. "
                "Use structured controls; cookie/storage/password/input-value/network reads are not exposed." % label)
    frame_id = str(payload.get("frameId") or "")
    world = next((item for item in worlds if item.get("id") == frame_id), None) if frame_id else \
        next((item for item in worlds if item.get("depth") == 0), worlds[0] if worlds else None)
    if not world:
        return error_result("frame-not-found", "The requested script frame is not available.")
    if expression is not None:
        wrapped = "(async()=>await (" + source + "))()"
    else:
        wrapped = "(async()=>{ \"use strict\";\n" + source + "\n})()"
    try:
        script_timeout = payload.get("timeoutSeconds")
        if script_timeout is None:
            script_timeout = max(1, math.ceil(int(payload.get("timeoutMs") or 10000) / 1000.0))
        remote = world["session"].evaluate_in_context(
            wrapped, world.get("contextId"), timeout=max(1, min(120, int(script_timeout))),
            return_by_value=True)
        result = sanitize_script_result(remote.get("value"))
        serialized = json.dumps(result, ensure_ascii=False)
        if len(serialized) > 12000:
            result = bounded_text(serialized, 12000)
        return {
            "ok": True, "result": result,
            "warning": "Last-resort isolated-world script executed. Its output was bounded and sensitive-looking keys were redacted.",
            "frameId": world.get("id"),
        }
    except Exception as ex:
        return error_result("script-failed", bounded_text(ex, 1000))


def add_control_state(result, session, tab, index, before_tab_ids):
    tabs_now = list_tabs()
    active_now = active_index(tabs_now) if tabs_now else 0
    current_index = next((i for i, candidate in enumerate(tabs_now)
                          if candidate.get("id") == tab.get("id")), index)
    current_tab = next((candidate for candidate in tabs_now
                        if candidate.get("id") == tab.get("id")), tab)
    try:
        state = read_page_state(session, current_tab, current_index, tabs_now)
    except Exception as ex:
        state = {
            "title": bounded_text(current_tab.get("title") or "", 300),
            "url": safe_url(current_tab.get("url") or ""), "readyState": "unknown",
            "nativeDialog": detect_native_dialog(), "inspectionError": bounded_text(ex, 300),
        }
    result["tab"] = state.get("tab") or tab_descriptor(current_tab, current_index)
    result["title"] = state.get("title")
    result["url"] = state.get("url")
    result["readyState"] = state.get("readyState")
    result["visibility"] = state.get("visibility")
    result["javascriptDialog"] = state.get("javascriptDialog")
    result["nativeDialog"] = state.get("nativeDialog")
    result["tabCount"] = len(tabs_now)
    result["popups"] = [
        tab_descriptor(candidate, i, active_now)
        for i, candidate in enumerate(tabs_now)
        if candidate.get("id") not in before_tab_ids
    ][:20]
    return result


def do_control(payload):
    op = str(payload.get("op") or "").strip().lower()
    aliases = {
        "scrollintoview": "scroll_into_view", "activate": "activate_tab",
        "close": "close_tab", "go_back": "back", "go_forward": "forward",
    }
    op = aliases.get(op, op)
    allowed = {
        "locate", "click", "fill", "type", "select", "check", "uncheck", "focus",
        "hover", "scroll_into_view", "scroll", "press", "wait", "back", "forward",
        "reload", "activate_tab", "close_tab", "script",
    }
    if op not in allowed:
        return error_result(
            "invalid-op", "control op must be one of: " + ", ".join(sorted(allowed)))

    decoded_ref = None
    if payload.get("ref"):
        try:
            decoded_ref = decode_ref(payload.get("ref"))
        except ValueError as ex:
            return error_result("invalid-ref", str(ex))
    tabs = list_tabs()
    if not tabs:
        return error_result("no-tab", "No inspectable browser tab is open.")
    try:
        index, tab = requested_tab(tabs, payload, decoded_ref)
    except Exception as ex:
        return error_result("tab-not-found", str(ex))
    before_tab_ids = {candidate.get("id") for candidate in tabs}

    if op == "close_tab":
        if len(tabs) <= 1:
            return error_result("last-tab", "Refusing to close the browser's last inspectable tab.")
        try:
            http_json("/json/close/" + urllib.parse.quote(tab.get("id") or "", safe=""))
            for _ in range(20):
                remaining = list_tabs()
                if not any(candidate.get("id") == tab.get("id") for candidate in remaining):
                    break
                time.sleep(0.1)
            remaining = list_tabs()
            activate = remaining[0] if remaining else None
            if activate and activate.get("webSocketDebuggerUrl"):
                replacement = session_for(activate)
                try:
                    replacement.call("Page.bringToFront", timeout=5)
                    remember_active(activate.get("id"))
                finally:
                    replacement.close()
            return {
                "ok": True, "op": op, "closedTab": tab_descriptor(tab, index),
                "tabCount": len(remaining),
                "tabs": [tab_descriptor(candidate, i, active_index(remaining) if remaining else 0)
                         for i, candidate in enumerate(remaining[:20])],
                "nativeDialog": detect_native_dialog(),
            }
        except Exception as ex:
            return error_result("close-failed", "Could not close the selected tab: " + bounded_text(ex, 500))

    session = session_for(tab)
    worlds = []
    try:
        session.call("Page.enable")
        session.call("Runtime.enable")
        session.call("DOM.enable")
        # Every target-bearing action activates its tab first. This is essential for op=locate:
        # the returned geometry is intended for a later physical VNC click and must describe what
        # is actually in front, never a background tab at the same coordinates.
        try:
            session.call("Page.bringToFront", timeout=6)
            remember_active(tab.get("id"))
            run_x11(["wmctrl", "-a", "Chromium"], timeout=3)
        except Exception:
            pass

        if op == "activate_tab":
            time.sleep(0.15)
            return add_control_state(
                {"ok": True, "op": op, "activated": True},
                session, tab, index, before_tab_ids)

        if op in ("back", "forward"):
            try:
                history = session.call("Page.getNavigationHistory", timeout=8)
                current = int(history.get("currentIndex") or 0)
                wanted = current - 1 if op == "back" else current + 1
                entries = history.get("entries") or []
                if wanted < 0 or wanted >= len(entries):
                    result = error_result(
                        "no-history", "There is no %s history entry for this tab." % op)
                    return add_control_state(result, session, tab, index, before_tab_ids)
                session.call("Page.navigateToHistoryEntry", {"entryId": entries[wanted]["id"]}, timeout=15)
                wait_page_ready(session, max(1, min(60, int(payload.get("timeoutSeconds") or 20))))
                return add_control_state(
                    {"ok": True, "op": op, "historyIndex": wanted},
                    session, tab, index, before_tab_ids)
            except Exception as ex:
                result = error_result(op + "-failed", bounded_text(ex, 800))
                return add_control_state(result, session, tab, index, before_tab_ids)

        if op == "reload":
            try:
                session.call("Page.reload", {"ignoreCache": bool(payload.get("ignoreCache"))}, timeout=15)
                wait_page_ready(session, max(1, min(60, int(payload.get("timeoutSeconds") or 20))))
                return add_control_state(
                    {"ok": True, "op": op}, session, tab, index, before_tab_ids)
            except Exception as ex:
                result = error_result("reload-failed", bounded_text(ex, 800))
                return add_control_state(result, session, tab, index, before_tab_ids)

        if op == "wait":
            try:
                waited = wait_for_control(
                    payload, tab, index, session, decoded_ref, before_tab_ids)
            except Exception as ex:
                waited = error_result("invalid-wait", bounded_text(ex, 800))
            waited["op"] = op
            return add_control_state(waited, session, tab, index, before_tab_ids)

        worlds, world_warnings = isolated_worlds(session, tab)
        if not worlds:
            result = error_result(
                "no-frame", "No isolated frame world is available for structured control.",
                warnings=world_warnings[:20])
            return add_control_state(result, session, tab, index, before_tab_ids)

        if op == "script":
            result = run_guarded_script(payload, worlds)
            result["op"] = op
            if world_warnings:
                result["warnings"] = world_warnings[:20]
            return add_control_state(result, session, tab, index, before_tab_ids)

        has_target = bool(decoded_ref)
        try:
            has_target = has_target or bool(locator_from_payload(payload))
        except ValueError as ex:
            result = error_result("invalid-selector", str(ex))
            return add_control_state(result, session, tab, index, before_tab_ids)

        world = item = selection_error = None
        if has_target:
            world, item, selection_error = select_control(tab, worlds, payload, decoded_ref)
            if selection_error:
                selection_error["op"] = op
                if world_warnings:
                    selection_error["warnings"] = world_warnings[:20]
                return add_control_state(selection_error, session, tab, index, before_tab_ids)
        elif op not in ("scroll", "press"):
            result = error_result(
                "missing-target",
                "%s requires ref or a name/text/role/tag/css/label/placeholder/testId locator." % op)
            return add_control_state(result, session, tab, index, before_tab_ids)

        geometry = selected_geometry(session, world) if world else None
        if op == "locate":
            result = {
                "ok": True, "op": op, "control": item, "geometry": geometry,
                "usableForPhysicalClick": bool(
                    geometry and item.get("visible") and item.get("inViewport")
                    and not geometry.get("intercepted") and not item.get("disabled")),
            }
            if geometry and geometry.get("intercepted"):
                result["warning"] = "The control's centre is intercepted; dismiss the reported blocker before clicking."
            elif item and not item.get("inViewport"):
                result["warning"] = "The control is outside the frame viewport; use op=scroll_into_view, then locate again."
            return add_control_state(result, session, tab, index, before_tab_ids)

        native = detect_native_dialog()
        if native.get("open") and op not in ("press",):
            result = error_result(
                "native-dialog-open",
                "A browser-owned native dialog is blocking page input. Clear it before this action.",
                nativeDialog=native)
            result["op"] = op
            return add_control_state(result, session, tab, index, before_tab_ids)

        if item and item.get("disabled") and op in (
                "click", "fill", "type", "select", "check", "uncheck", "focus"):
            result = error_result(
                "disabled", "The matched control is disabled; inspect the form for unmet requirements.",
                control=item)
            result["op"] = op
            return add_control_state(result, session, tab, index, before_tab_ids)
        if item and item.get("readonly") and op in ("fill", "type"):
            result = error_result(
                "readonly", "The matched field is read-only.", control=item)
            result["op"] = op
            return add_control_state(result, session, tab, index, before_tab_ids)

        # Bring a rendered off-screen target into the visible viewport before any operation that
        # relies on focus or mouse geometry. display:none remains a clear semantic error.
        if item and not item.get("visible"):
            result = error_result(
                "hidden", "The matched control is not rendered. Wait for it or choose another target.",
                control=item)
            result["op"] = op
            return add_control_state(result, session, tab, index, before_tab_ids)
        if item and not item.get("inViewport") and op not in ("scroll",):
            selected_eval(world, "el.scrollIntoView({block:'center',inline:'center',behavior:'auto'});"
                          "return {scrolled:true};")
            time.sleep(0.12)
            refreshed_item = refresh_selected(tab, world)
            if refreshed_item:
                refreshed_item["ref"] = item.get("ref")
                item = refreshed_item
            geometry = selected_geometry(session, world)

        action_data = {}
        if op == "fill":
            # `text` is also a semantic locator. The public adapter sends field content in `value`,
            # so value must win even when the locator text is present (or present as an empty key).
            text = payload.get("value") if "value" in payload else payload.get("text", "")
            if not isinstance(text, str):
                text = str(text)
            if len(text) > 100000:
                result = error_result("text-too-large", "fill text is limited to 100,000 characters.")
                result["op"] = op
                return add_control_state(result, session, tab, index, before_tab_ids)
            encoded = json.dumps(text, ensure_ascii=False)
            action_data = selected_eval(world, r"""
              const text=%s, tag=el.tagName.toLowerCase(), type=(el.getAttribute('type')||'').toLowerCase();
              if(tag==='input'&&type==='file')return {error:'Use computer_upload_file for file inputs.'};
              if(!(tag==='input'||tag==='textarea'||el.isContentEditable))return {error:'The target is not editable.'};
              el.focus({preventScroll:true});
              if(el.isContentEditable){el.textContent=text;}
              else {
                const proto=tag==='textarea'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;
                const setter=Object.getOwnPropertyDescriptor(proto,'value').set; setter.call(el,text);
              }
              el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:null}));
              el.dispatchEvent(new Event('change',{bubbles:true}));
              return {filled:true};
            """ % encoded)
        elif op == "type":
            text = payload.get("value") if "value" in payload else payload.get("text", "")
            if not isinstance(text, str):
                text = str(text)
            if len(text) > 100000:
                result = error_result("text-too-large", "type text is limited to 100,000 characters.")
                result["op"] = op
                return add_control_state(result, session, tab, index, before_tab_ids)
            focused = selected_eval(world,
                                    "if(!(el.matches('input,textarea')||el.isContentEditable))"
                                    "return {error:'The target is not editable.'};"
                                    "el.focus({preventScroll:true});return {focused:true};")
            if not focused.get("error"):
                insert_text(session, text)
            action_data = focused
            if not action_data.get("error"):
                action_data["typed"] = True
        elif op == "select":
            option_index = payload.get("optionIndex")
            option_text = payload.get("optionText")
            option_value = payload.get("optionValue")
            public_value = payload.get("value") if "value" in payload else None
            public_values = payload.get("values") if isinstance(payload.get("values"), list) else []
            if "option" in payload:
                if isinstance(payload["option"], int):
                    option_index = payload["option"]
                elif option_text is None:
                    option_text = payload["option"]
            request = json.dumps({
                "index": option_index, "text": option_text,
                "value": option_value if option_value is not None else public_value,
                "values": [str(value) for value in public_values[:100]],
                "exact": payload.get("exact") is True,
            }, ensure_ascii=False)
            action_data = selected_eval(world, r"""
              if(el.tagName.toLowerCase()!=='select')
                return {error:'Structured select currently supports native <select>; use click/press for a custom combobox.'};
              const wanted=%s, options=Array.from(el.options||[]);
              const norm=x=>String(x==null?'':x).replace(/\s+/g,' ').trim().toLowerCase();
              const findOne=needle=>{
                const raw=String(needle==null?'':needle), normal=norm(raw);
                const byValue=options.filter(x=>String(x.value)===raw);
                if(byValue.length===1)return byValue[0];
                const byText=options.filter(x=>{const t=norm(x.textContent||x.label||'');
                  return wanted.exact?t===normal:t.includes(normal);});
                if(byText.length>1)return {ambiguous:true,count:byText.length};
                return byText[0]||null;
              };
              let chosen=[];
              if(Array.isArray(wanted.values)&&wanted.values.length){
                if(!el.multiple)return {error:'The selected control is not a multi-select.'};
                for(const requested of wanted.values){
                  const match=findOne(requested);
                  if(match&&match.ambiguous)return {error:'An option label is ambiguous.',matchCount:match.count};
                  if(!match)return {error:'No select option matched one of the requested values/labels.'};
                  if(match.disabled)return {error:'A matched select option is disabled.'};
                  chosen.push(match);
                }
                for(const option of options)option.selected=chosen.includes(option);
              } else {
                let match=null;
                if(wanted.index!==null&&wanted.index!==undefined)match=options[Number(wanted.index)]||null;
                else if(wanted.value!==null&&wanted.value!==undefined)match=findOne(wanted.value);
                else if(wanted.text!==null&&wanted.text!==undefined)match=findOne(wanted.text);
                if(match&&match.ambiguous)return {error:'The option label is ambiguous.',matchCount:match.count};
                if(!match)return {error:'No select option matched by value or visible label.'};
                if(match.disabled)return {error:'The matched select option is disabled.'};
                chosen=[match]; el.selectedIndex=match.index;
              }
              el.focus({preventScroll:true});
              el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));
              return {selected:true,selectedIndices:chosen.map(x=>x.index),
                selectedTexts:chosen.map(x=>String(x.textContent||x.label||'').trim().slice(0,180))};
            """ % request)
        elif op in ("check", "uncheck"):
            wanted = "true" if op == "check" else "false"
            action_data = selected_eval(world, r"""
              const wanted=%s, tag=el.tagName.toLowerCase(), type=(el.getAttribute('type')||'').toLowerCase();
              if(tag==='input'&&(type==='checkbox'||type==='radio')){
                if(type==='radio'&&!wanted)return {error:'A radio button cannot be unchecked directly; check another radio in its group.'};
                if(!!el.checked!==wanted)el.click();
                return {changed:true,checked:!!el.checked};
              }
              const aria=el.getAttribute('aria-checked');
              if(aria===null)return {error:'The target is not a checkbox/radio/switch.'};
              if((aria==='true')!==wanted)el.click();
              return {changed:true,checked:el.getAttribute('aria-checked')==='true'};
            """ % wanted)
        elif op == "focus":
            action_data = selected_eval(
                world, "el.focus({preventScroll:true});return {focused:document.activeElement===el};")
        elif op == "hover":
            geometry = selected_geometry(session, world)
            if geometry and geometry.get("intercepted"):
                result = error_result(
                    "intercepted", "The hover point is covered by another element.",
                    control=item, geometry=geometry)
                result["op"] = op
                return add_control_state(result, session, tab, index, before_tab_ids)
            try:
                hover_geometry(session, geometry)
                action_data = {"hovered": True}
            except Exception:
                action_data = selected_eval(world, r"""
                  const r=el.getBoundingClientRect(),opts={bubbles:true,clientX:r.left+r.width/2,clientY:r.top+r.height/2};
                  el.dispatchEvent(new MouseEvent('mouseover',opts));el.dispatchEvent(new MouseEvent('mouseenter',opts));
                  el.dispatchEvent(new MouseEvent('mousemove',opts));return {hovered:true,syntheticFallback:true};
                """)
        elif op == "scroll_into_view":
            block = str(payload.get("block") or "center")
            inline = str(payload.get("inline") or "nearest")
            if block not in ("start", "center", "end", "nearest"):
                block = "center"
            if inline not in ("start", "center", "end", "nearest"):
                inline = "nearest"
            action_data = selected_eval(
                world, "el.scrollIntoView({block:%s,inline:%s,behavior:'auto'});return {scrolled:true};" %
                (json.dumps(block), json.dumps(inline)))
            time.sleep(0.12)
            geometry = selected_geometry(session, world)
            action_data["geometry"] = geometry
        elif op == "scroll":
            try:
                delta_x = int(payload.get("deltaX") or 0)
                delta_y = int(payload.get("deltaY") or 0)
            except (TypeError, ValueError):
                delta_x = delta_y = 0
            direction = str(payload.get("direction") or "").lower()
            amount = max(1, min(10000, abs(int(payload.get("amount") or 600))))
            if not delta_x and not delta_y:
                if direction == "up":
                    delta_y = -amount
                elif direction == "left":
                    delta_x = -amount
                elif direction == "right":
                    delta_x = amount
                else:
                    delta_y = amount
            delta_x, delta_y = max(-50000, min(50000, delta_x)), max(-50000, min(50000, delta_y))
            if world:
                action_data = selected_eval(world, r"""
                  let target=el;
                  for(let depth=0;target&&depth<40;depth++,target=target.parentElement){
                    const s=getComputedStyle(target);
                    if((target.scrollHeight>target.clientHeight||target.scrollWidth>target.clientWidth)&&
                       /(auto|scroll)/.test(s.overflow+s.overflowX+s.overflowY))break;
                  }
                  if(target){target.scrollBy({left:%d,top:%d,behavior:'auto'});}
                  else{window.scrollBy({left:%d,top:%d,behavior:'auto'});}
                  return {scrolled:true,deltaX:%d,deltaY:%d};
                """ % (delta_x, delta_y, delta_x, delta_y, delta_x, delta_y))
            else:
                top_world = next((candidate for candidate in worlds if candidate.get("depth") == 0), worlds[0])
                value = top_world["session"].evaluate_in_context(
                    "(()=>{window.scrollBy({left:%d,top:%d,behavior:'auto'});"
                    "return {scrolled:true,deltaX:%d,deltaY:%d};})()" %
                    (delta_x, delta_y, delta_x, delta_y),
                    top_world.get("contextId"), timeout=10, return_by_value=True).get("value")
                action_data = value if isinstance(value, dict) else {"scrolled": True}
        elif op == "press":
            chord = str(payload.get("key") or payload.get("chord") or "")
            repeats = max(1, min(50, int(payload.get("repeats") or 1)))
            if world:
                selected_eval(world, "el.focus({preventScroll:true});return {focused:true};")
            js_dialog = javascript_dialog_from_events(session)
            if js_dialog.get("open") and chord.strip().lower() in ("enter", "return", "escape", "esc"):
                accept = chord.strip().lower() in ("enter", "return")
                session.call("Page.handleJavaScriptDialog", {"accept": accept}, timeout=10)
                action_data = {"dialogHandled": True, "accepted": accept}
            elif native.get("open"):
                do_dialog({"activate": True})
                key = chord.strip().lower().replace("control", "ctrl").replace("command", "ctrl")
                output = run_x11(["xdotool", "key", "--clearmodifiers", key], timeout=5)
                action_data = {"pressed": True, "nativeDialog": True,
                               "xdotoolOutput": bounded_text(output, 200) if output else None}
            else:
                dispatch_key(session, chord, repeats)
                action_data = {"pressed": True}
        elif op == "click":
            geometry = selected_geometry(session, world)
            if geometry and geometry.get("intercepted"):
                result = error_result(
                    "intercepted", "The click point is covered by another element.",
                    control=item, geometry=geometry)
                result["op"] = op
                return add_control_state(result, session, tab, index, before_tab_ids)
            click_geometry(
                session, geometry, str(payload.get("button") or "left").lower(),
                max(1, min(2, int(payload.get("clicks") or 1))))
            action_data = {"clicked": True, "geometry": geometry}

        if action_data.get("error"):
            result = error_result("action-not-supported", action_data.get("error"),
                                  control=item, detail={
                                      key: value for key, value in action_data.items() if key != "error"})
            result["op"] = op
            return add_control_state(result, session, tab, index, before_tab_ids)
        time.sleep(0.15)
        # Clicks and key presses can synchronously navigate or detach their frame. The action still
        # succeeded in that case; a failed best-effort refresh must not turn it into a false error.
        try:
            refreshed = refresh_selected(tab, world) if world else None
        except Exception:
            refreshed = None
        if refreshed and item and item.get("ref"):
            refreshed["ref"] = item["ref"]
        result = {
            "ok": True, "op": op, "result": action_data,
            "control": refreshed or item,
        }
        if world_warnings:
            result["warnings"] = world_warnings[:20]
        return add_control_state(result, session, tab, index, before_tab_ids)
    except Exception as ex:
        result = error_result(op + "-failed", bounded_text(ex, 1200))
        result["op"] = op
        try:
            return add_control_state(result, session, tab, index, before_tab_ids)
        except Exception:
            result["nativeDialog"] = detect_native_dialog()
            return result
    finally:
        close_owned_worlds(worlds)
        session.close()
# ── entry point ──────────────────────────────────────────────────────────────────────────────
mode = (sys.argv[1] if len(sys.argv) > 1 else "dom").lower()
if mode in ACTION_MODES:
    action_payload = decode_payload(sys.argv[2]) if len(sys.argv) > 2 else {}
    action = {
        "navigate": do_navigate, "closetabs": do_closetabs, "upload": do_upload,
        "dialog": do_dialog, "control": do_control, "action": do_control,
    }[mode]
    action_output = action(action_payload)
    print(json.dumps(action_output, indent=2, ensure_ascii=False))
    # Existing action helpers signal operational failures by throwing/non-zero exit. Structured
    # action returns an error envelope so callers and humans get a useful reason; mirror the old
    # process contract as well so ContainerToolAdapter does not report {"ok":false} as success.
    if mode in ("control", "action") and isinstance(action_output, dict) \
            and action_output.get("ok") is False:
        raise SystemExit(2)
    raise SystemExit(0)

limit = max(1, min(200, int(sys.argv[2]) if len(sys.argv) > 2 else 80))
# -1 (or omitted) means "the tab the human would be looking at". Defaulting to index 0 made every
# inspection after a few navigations report a stale page while the agent acted on the live one.
tab_index = max(-1, min(200, int(sys.argv[3]) if len(sys.argv) > 3 else -1))
query = {}
if mode == "locate":
    if len(sys.argv) < 5:
        raise RuntimeError("locate mode requires a base64url JSON query")
    query = decode_payload(sys.argv[4])
tabs = list_tabs()
if mode == "tabs":
    active = active_index(tabs) if tabs else 0
    print(json.dumps([{"index": i, "id": t.get("id"), "title": t.get("title"), "url": t.get("url"),
                       "active": i == active, "blank": is_blank(t.get("url"))}
                      for i, t in enumerate(tabs[:limit])], indent=2))
    raise SystemExit(0)
if not tabs:
    raise RuntimeError("No inspectable browser tab is open.")

tab_index = resolve_index(tabs, tab_index)
tab = tabs[tab_index]
ws_url = tab["webSocketDebuggerUrl"]
if mode == "controls":
    output = inspect_controls(tab, tab_index, tabs, limit)
elif mode == "accessibility":
    result = cdp(ws_url, "Accessibility.getFullAXTree")
    nodes = []
    for node in result.get("nodes", [])[:limit]:
        nodes.append({
            "role": (node.get("role") or {}).get("value"),
            "name": (node.get("name") or {}).get("value"),
            "description": (node.get("description") or {}).get("value"),
            "ignored": node.get("ignored", False),
        })
    output = {"title": tab.get("title"), "url": tab.get("url"), "nodes": nodes}
elif mode == "locate":
    expression = r"""
    (() => {
      const query = %s;
      const norm = value => String(value || '').replace(/\s+/g, ' ').trim().toLowerCase();
      const roleOf = x => {
        const explicit = x.getAttribute('role');
        if (explicit) return explicit.toLowerCase();
        const tag = x.tagName.toLowerCase();
        const type = (x.getAttribute('type') || '').toLowerCase();
        if (tag === 'button' || (tag === 'input' && ['button','submit','reset','image'].includes(type))) return 'button';
        if (tag === 'a' && x.hasAttribute('href')) return 'link';
        if (tag === 'select') return 'combobox';
        if (tag === 'textarea' || (tag === 'input' && !['checkbox','radio','range','file','color','hidden'].includes(type))) return 'textbox';
        if (tag === 'input' && type === 'checkbox') return 'checkbox';
        if (tag === 'input' && type === 'radio') return 'radio';
        return '';
      };
      const nameOf = x => {
        const labelledBy = (x.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean)
          .map(id => document.getElementById(id)?.innerText || '').join(' ');
        const labels = x.labels ? [...x.labels].map(label => label.innerText || '').join(' ') : '';
        const type = (x.getAttribute('type') || '').toLowerCase();
        const safeButtonValue = ['button','submit','reset'].includes(type) ? x.getAttribute('value') : '';
        return (x.getAttribute('aria-label') || labelledBy || labels || x.innerText ||
          x.getAttribute('placeholder') || x.getAttribute('title') || safeButtonValue || x.getAttribute('name') || '').trim();
      };
      const controls = [...document.querySelectorAll('button,input,select,textarea,a[href],[role],[contenteditable="true"]')]
        .map((x, index) => {
          const rect = x.getBoundingClientRect();
          const style = getComputedStyle(x);
          const visible = rect.width > 2 && rect.height > 2 && style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity || 1) > 0;
          const cx = rect.left + rect.width / 2;
          const cy = rect.top + rect.height / 2;
          const hit = visible ? document.elementFromPoint(cx, cy) : null;
          const intercepted = !!hit && hit !== x && !x.contains(hit);
          const borderX = Math.max(0, (window.outerWidth - window.innerWidth) / 2);
          const browserTop = Math.max(0, window.outerHeight - window.innerHeight - borderX);
          return {element:x, index, name:nameOf(x), role:roleOf(x), tag:x.tagName.toLowerCase(), visible,
            disabled:!!x.disabled || x.getAttribute('aria-disabled') === 'true', intercepted,
            interceptedBy:intercepted ? {name:nameOf(hit), role:roleOf(hit), tag:hit.tagName.toLowerCase()} : null,
            x:Math.round(window.screenX + borderX + cx), y:Math.round(window.screenY + browserTop + cy),
            bounds:{x:Math.round(rect.left),y:Math.round(rect.top),width:Math.round(rect.width),height:Math.round(rect.height)}};
        }).filter(item => item.visible);
      const wantedName = norm(query.name || query.text);
      const wantedRole = norm(query.role);
      const wantedTag = norm(query.tag);
      const exact = query.exact === true;
      const matched = controls.filter(item => {
        const itemName = norm(item.name);
        return (!wantedName || (exact ? itemName === wantedName : itemName.includes(wantedName))) &&
          (!wantedRole || norm(item.role) === wantedRole) && (!wantedTag || norm(item.tag) === wantedTag);
      });
      const occurrence = Math.max(0, Number(query.occurrence || 0));
      const selected = matched[occurrence];
      const clean = item => item ? {index:item.index,name:item.name,role:item.role,tag:item.tag,
        disabled:item.disabled,intercepted:item.intercepted,interceptedBy:item.interceptedBy,
        x:item.x,y:item.y,bounds:item.bounds} : null;
      return {title:document.title,url:location.href,match:clean(selected),matchCount:matched.length,
        candidates:controls.slice(0,20).map(clean)};
    })()
    """ % json.dumps(query, ensure_ascii=False)
    result = cdp(ws_url, "Runtime.evaluate", {"expression": expression, "returnByValue": True, "awaitPromise": True})
    if result.get("exceptionDetails"):
        details = result["exceptionDetails"]
        raise RuntimeError(details.get("text") or ((details.get("exception") or {}).get("description")) or "Runtime.evaluate failed")
    output = ((result.get("result") or {}).get("value"))
else:
    expression = """
    (() => {
      const maxItems = %d;
      if (%s) {
        return {title: document.title, url: location.href,
          resources: performance.getEntriesByType('resource').slice(-maxItems).map(x => ({name:x.name, initiatorType:x.initiatorType, duration:Math.round(x.duration), transferSize:x.transferSize}))};
      }
      return {title: document.title, url: location.href,
        text: (document.body?.innerText || '').slice(0, 16000),
        links: [...document.querySelectorAll('a[href]')].slice(0,maxItems).map(x => ({text:(x.innerText||x.getAttribute('aria-label')||'').trim().slice(0,200), href:x.href})),
        forms: [...document.forms].slice(0,maxItems).map(f => ({action:f.action, method:f.method, fields:[...f.elements].slice(0,40).map(x => ({name:x.name, type:x.type, ariaLabel:x.getAttribute('aria-label'), required:x.required}))})),
        fileInputs: [...document.querySelectorAll('input[type=file]')].map(x => ({name:x.name||x.id||'', accept:x.accept||'', multiple:!!x.multiple, attached:x.files?x.files.length:0})),
        controls: [...document.querySelectorAll('button,input,select,textarea,[role],[contenteditable="true"]')].slice(0,maxItems).map(x => ({
          tag:x.tagName,
          role:x.getAttribute('role')||undefined,
          type:x.type||undefined,
          text:(x.innerText||x.getAttribute('aria-label')||x.getAttribute('placeholder')||'').trim().slice(0,200),
          name:x.getAttribute('name')||undefined,
          ariaLabel:x.getAttribute('aria-label')||undefined,
          expanded:x.getAttribute('aria-expanded')||undefined,
          selected:x.getAttribute('aria-selected')||undefined,
          checked:typeof x.checked==='boolean'?x.checked:undefined,
          required:!!x.required,
          disabled:!!x.disabled,
          bounds:(() => { const r=x.getBoundingClientRect(); return {x:Math.round(r.x),y:Math.round(r.y),width:Math.round(r.width),height:Math.round(r.height)}; })()
        }))};
    })()
    """ % (limit, "true" if mode == "network" else "false")
    result = cdp(ws_url, "Runtime.evaluate", {"expression": expression, "returnByValue": True, "awaitPromise": True})
    if result.get("exceptionDetails"):
        details = result["exceptionDetails"]
        raise RuntimeError(details.get("text") or ((details.get("exception") or {}).get("description")) or "Runtime.evaluate failed")
    output = ((result.get("result") or {}).get("value"))
    if output is None:
        raise RuntimeError("Runtime.evaluate returned no inspectable value for the selected visible tab.")

# Detect human-verification gates on every inspectable mode, before an agent burns retries trying
# controls that automation cannot complete. DOM selectors catch reCAPTCHA/hCaptcha/Turnstile;
# bounded visible-text signals cover provider-hosted interstitials.
if isinstance(output, dict):
    challenge_expression = r"""
    (() => {
      const signals = [];
      const visible = x => {
        const r = x.getBoundingClientRect(); const s = getComputedStyle(x);
        return r.width > 2 && r.height > 2 && s.display !== 'none' && s.visibility !== 'hidden';
      };
      const selectors = ['.g-recaptcha','.h-captcha','[data-sitekey]','[name="cf-turnstile-response"]',
        'iframe[src*="recaptcha"]','iframe[src*="hcaptcha"]','iframe[src*="challenges.cloudflare.com"]'];
      for (const selector of selectors) {
        if ([...document.querySelectorAll(selector)].some(visible)) signals.push('visible selector: ' + selector);
      }
      const text = (document.body?.innerText || '').slice(0, 6000).toLowerCase();
      for (const marker of ['verify you are human','complete the security check','checking your browser',
        'security verification','captcha']) {
        if (text.includes(marker)) signals.push('visible text: ' + marker);
      }
      return {detected: signals.length > 0, signals: [...new Set(signals)].slice(0,8)};
    })()
    """
    try:
        challenge_result = cdp(ws_url, "Runtime.evaluate", {
            "expression": challenge_expression, "returnByValue": True, "awaitPromise": True
        })
        output["humanChallenge"] = ((challenge_result.get("result") or {}).get("value")) or {
            "detected": False, "signals": []}
    except Exception as challenge_error:
        # A JavaScript/native modal can make Runtime.evaluate unavailable. Keep the primary
        # inspection result and report that the optional challenge probe was inconclusive.
        output["humanChallenge"] = {
            "detected": False, "signals": [],
            "inspectionError": bounded_text(challenge_error, 300),
        }
    # A modal GTK chooser blocks the page but is invisible to the DOM. Reporting it here is what
    # stops an agent looping on a control that cannot receive input until the dialog is dealt with.
    output["nativeDialog"] = detect_native_dialog()
    output["tabIndex"] = tab_index
    output["tabCount"] = len(tabs)
print(json.dumps(output, indent=2, ensure_ascii=False))
