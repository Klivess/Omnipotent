import http.client
import json
import socket
from urllib.parse import quote


class UnixConnection(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__("localhost", timeout=30)
        self.path = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self.path)


class Incus:
    def __init__(self, path="/var/lib/incus/unix.socket"):
        self.path = path

    def request(self, method, path, body=None, headers=None, raw=False):
        connection = UnixConnection(self.path)
        try:
            data = body if raw else json.dumps(body).encode() if body is not None else None
            connection.request(method, path, data, headers or {"Content-Type": "application/json"})
            response = connection.getresponse()
            payload = response.read()
            if response.status == 404:
                return None
            result = json.loads(payload)
            if response.status >= 400:
                raise RuntimeError("Incus: " + result.get("error", str(response.status)))
            if result.get("type") == "async":
                # Caller persists the returned operation and reconciles it on future ticks.
                return {"operation": result["operation"]}
            return result.get("metadata")
        finally:
            connection.close()

    def instance(self, name):
        return self.request("GET", "/1.0/instances/" + quote(name, safe=""))

    def push(self, name, path, contents, mode="0600", uid="1000", gid="1000"):
        return self.request("POST", "/1.0/instances/" + quote(name, safe="") + "/files?path=" + quote(path, safe=""),
                            contents, {"X-Incus-mode": mode, "X-Incus-uid": uid, "X-Incus-gid": gid,
                                       "X-Incus-type": "file"}, raw=True)

    def mkdir(self, name, path):
        return self.request("POST", "/1.0/instances/" + quote(name, safe="") + "/files?path=" + quote(path, safe=""), b"",
                            {"X-Incus-mode": "0700", "X-Incus-uid": "1000", "X-Incus-gid": "1000", "X-Incus-type": "directory"}, raw=True)
