#!/usr/bin/env python3
"""Harness-owned browser helper for a Projects desktop.

Agents never invoke this directly: ContainerToolAdapter calls it for computer_browser_inspect,
computer_navigate and computer_upload_file, so the one visible Chromium session is inspected and
driven from a single place. Read-only inspection modes (tabs/dom/accessibility/network/locate)
keep their original argv contract; the action modes (navigate/closetabs/upload/dialog) take one
base64url-encoded JSON payload so no caller has to quote anything through a shell.
"""
import base64
import json
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
ACTION_MODES = ("navigate", "closetabs", "upload", "dialog")


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

    def call(self, method, params=None, timeout=15):
        self.ws.settimeout(timeout)
        request_id = self.next_id
        self.next_id += 1
        self.ws.send(json.dumps({"id": request_id, "method": method, "params": params or {}}))
        deadline = time.time() + timeout
        while time.time() < deadline:
            message = json.loads(self.ws.recv())
            if message.get("id") != request_id:
                continue  # an unsolicited domain event
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


# ── entry point ──────────────────────────────────────────────────────────────────────────────
mode = (sys.argv[1] if len(sys.argv) > 1 else "dom").lower()
if mode in ACTION_MODES:
    action_payload = decode_payload(sys.argv[2]) if len(sys.argv) > 2 else {}
    action = {"navigate": do_navigate, "closetabs": do_closetabs, "upload": do_upload, "dialog": do_dialog}[mode]
    print(json.dumps(action(action_payload), indent=2, ensure_ascii=False))
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
if mode == "accessibility":
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
    challenge_result = cdp(ws_url, "Runtime.evaluate", {
        "expression": challenge_expression, "returnByValue": True, "awaitPromise": True
    })
    output["humanChallenge"] = ((challenge_result.get("result") or {}).get("value")) or {"detected": False, "signals": []}
    # A modal GTK chooser blocks the page but is invisible to the DOM. Reporting it here is what
    # stops an agent looping on a control that cannot receive input until the dialog is dealt with.
    output["nativeDialog"] = detect_native_dialog()
    output["tabIndex"] = tab_index
    output["tabCount"] = len(tabs)
print(json.dumps(output, indent=2, ensure_ascii=False))
