#!/usr/bin/env python3
"""First-install gate on an isolated computer; no production accounts or actions."""
import json
import ssl
import time
import urllib.error
import urllib.request
from pathlib import Path


def main():
    context = ssl.create_default_context(cafile='/etc/ka/ca.pem')
    context.load_cert_chain('/etc/ka/ka-api.pem', '/etc/ka/ka-api-key.pem')

    def request(method, path, payload=None):
        req = urllib.request.Request('https://10.78.0.2:7443' + path, method=method,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={'Content-Type': 'application/json'})
        with urllib.request.urlopen(req, context=context, timeout=40) as response:
            return json.load(response)

    deadline = time.monotonic() + 1800
    computer = None
    while time.monotonic() < deadline:
        try:
            computer = request('POST', '/computers/ensure', {'projectID': 'acceptance-bootstrap', 'agentID': 'probe'})
            if computer['state'] == 'ready':
                break
        except (urllib.error.URLError, TimeoutError):
            pass
        time.sleep(5)
    else:
        raise RuntimeError('Canary admission is still pending; no production computer was touched')
    route = '/computers/' + computer['computerID']

    def path(suffix):
        return route + '/' + suffix + '?projectID=acceptance-bootstrap'

    def operation(target, payload):
        # Stable IDs survive installer timeouts. Never replay uncertain input.
        try:
            request('POST', path(target), payload)
        except (urllib.error.URLError, TimeoutError):
            pass
        until = time.monotonic() + 600
        while time.monotonic() < until:
            try:
                result = request('GET', path(('jobs/' if target == 'jobs' else 'operations/') + payload['operationID']))
                if result['state'] == 'completed':
                    return result
                if result['state'] not in ('queued', 'running'):
                    raise RuntimeError('Canary operation requires inspection: ' + result['state'])
            except (urllib.error.URLError, TimeoutError):
                pass
            time.sleep(2)
        raise RuntimeError('Canary operation still pending; inspect its original ID')

    job = operation('jobs', {'operationID': 'bootstrap-terminal-v1', 'command':
        "set -eu; test $(id -u) -ne 0; sudo -n true; mkdir -p ~/Desktop; "
        "printf '[Desktop Entry]\\nType=Application\\nName=Smoke editor\\nExec=mousepad /project/work/smoke.txt\\n' > ~/Desktop/smoke.desktop; "
        "chmod +x ~/Desktop/smoke.desktop; printf persistent > /project/work/smoke.txt; "
        "sleep 3; test $(cat /project/work/smoke.txt) = persistent; printf KA_SMOKE_OK", 'interactive': False})
    if job.get('result', {}).get('exitCode') != 0:
        raise RuntimeError('Canary terminal did not exit successfully')
    operation('actions', {'operationID': 'bootstrap-editor-v1', 'actorID': 'acceptance',
        'tool': 'computer_launch_app', 'arguments': {'app': 'mousepad', 'args': '/project/work/smoke.txt'}})
    operation('actions', {'operationID': 'bootstrap-screen-v1', 'actorID': 'acceptance',
        'tool': 'computer_screenshot', 'arguments': {}})
    window = operation('jobs', {'operationID': 'bootstrap-window-v1', 'command':
        "for i in $(seq 1 30); do if wmctrl -l | grep -qi smoke.txt; then exit 0; fi; sleep 1; done; exit 1"})
    if window.get('result', {}).get('exitCode') != 0:
        raise RuntimeError('Canary graphical editor did not create a window')
    frame = request('GET', path('frame'))
    if not frame.get('jpeg'):
        raise RuntimeError('Canary desktop did not return a frame')
    Path('/var/lib/ka-bootstrap/smoke-passed.json').write_text(json.dumps({
        'computerID': computer['computerID'], 'passed': True, 'checkedAt': time.time(),
        'scope': 'sudo, persistent file, desktop application, screenshot, durable terminal job; not the production soak'}))


if __name__ == '__main__':
    main()
