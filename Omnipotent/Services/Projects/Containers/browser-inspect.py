#!/usr/bin/env python3
import base64
import json
import sys
import time
import urllib.request

import websocket


def http_json(path):
    # The CDP endpoint is always container-local. Ignore inherited proxy variables: routing a
    # loopback request through a corporate/system proxy makes a healthy browser look offline.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open("http://127.0.0.1:9222" + path, timeout=3) as response:
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
tab_index = max(0, min(200, int(sys.argv[3]) if len(sys.argv) > 3 else 0))
query = {}
if mode == "locate":
    if len(sys.argv) < 5:
        raise RuntimeError("locate mode requires a base64url JSON query")
    encoded = sys.argv[4]
    encoded += "=" * ((4 - len(encoded) % 4) % 4)
    query = json.loads(base64.urlsafe_b64decode(encoded.encode("ascii")).decode("utf-8"))
tabs = [item for item in http_json("/json/list") if item.get("type") == "page"]
if mode == "tabs":
    print(json.dumps([{"index": i, "id": t.get("id"), "title": t.get("title"), "url": t.get("url")} for i, t in enumerate(tabs[:limit])], indent=2))
    raise SystemExit(0)
if not tabs:
    raise RuntimeError("No inspectable browser tab is open.")
if tab_index >= len(tabs):
    raise RuntimeError("Browser tab index %d does not exist; inspect mode=tabs and choose a listed index." % tab_index)

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
print(json.dumps(output, indent=2, ensure_ascii=False))
