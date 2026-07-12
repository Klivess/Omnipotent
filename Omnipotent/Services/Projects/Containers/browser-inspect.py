#!/usr/bin/env python3
import json
import sys
import time
import urllib.request

import websocket


def http_json(path):
    with urllib.request.urlopen("http://127.0.0.1:9222" + path, timeout=3) as response:
        return json.load(response)


def cdp(ws_url, method, params=None):
    ws = websocket.create_connection(ws_url, timeout=5, suppress_origin=True)
    try:
        request_id = int(time.time() * 1000) % 1000000000
        ws.send(json.dumps({"id": request_id, "method": method, "params": params or {}}))
        while True:
            message = json.loads(ws.recv())
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError(message["error"].get("message", "CDP error"))
                return message.get("result", {})
    finally:
        ws.close()


mode = (sys.argv[1] if len(sys.argv) > 1 else "dom").lower()
limit = max(1, min(200, int(sys.argv[2]) if len(sys.argv) > 2 else 80))
tabs = [item for item in http_json("/json/list") if item.get("type") == "page"]
if mode == "tabs":
    print(json.dumps([{"id": t.get("id"), "title": t.get("title"), "url": t.get("url")} for t in tabs[:limit]], indent=2))
    raise SystemExit(0)
if not tabs:
    raise RuntimeError("No inspectable browser tab is open.")

tab = tabs[0]
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
        controls: [...document.querySelectorAll('button,input,select,textarea,[role=button]')].slice(0,maxItems).map(x => ({tag:x.tagName, type:x.type, text:(x.innerText||x.value||x.getAttribute('aria-label')||'').trim().slice(0,200), disabled:!!x.disabled}))};
    })()
    """ % (limit, "true" if mode == "network" else "false")
    result = cdp(ws_url, "Runtime.evaluate", {"expression": expression, "returnByValue": True, "awaitPromise": True})
    output = ((result.get("result") or {}).get("value"))
print(json.dumps(output, indent=2, ensure_ascii=False))
