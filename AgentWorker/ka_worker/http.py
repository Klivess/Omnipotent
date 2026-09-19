import json
import ssl
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit, parse_qs


class BoundedServer(ThreadingHTTPServer):
    """Slow clients cannot consume unlimited threads or block TLS acceptance."""
    daemon_threads = True

    def __init__(self, address, handler, context, max_connections=32):
        self.context = context
        self.slots = threading.BoundedSemaphore(max_connections)
        super().__init__(address, handler)

    def get_request(self):
        connection, address = self.socket.accept()
        connection.settimeout(45)
        return self.context.wrap_socket(connection, server_side=True, do_handshake_on_connect=False), address

    def process_request(self, request, address):
        if not self.slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, address):
        try:
            request.do_handshake()
            super().process_request_thread(request, address)
        except (OSError, ssl.SSLError):
            self.shutdown_request(request)
        finally:
            self.slots.release()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass  # Never log credentials, terminal scripts or query strings.

    def handle_request(self):
        try:
            cert = self.connection.getpeercert()
            names = [value for group in cert.get("subject", ()) for key, value in group if key == "commonName"]
            if self.server.peer_name not in names:
                self.reply(403, {"error": "Client identity is not authorized"})
                return
            length = int(self.headers.get("Content-Length", "0"))
            if length < 0 or length > 16 * 1024 * 1024:
                raise ValueError("Request exceeds 16 MiB; transfer files in chunks")
            payload = json.loads(self.rfile.read(length)) if length else {}
            url = urlsplit(self.path)
            query = {key: values[-1] for key, values in parse_qs(url.query).items()}
            code, body = self.server.app(self.command, url.path, query, payload)
            self.reply(code, body)
        except (ValueError, TypeError) as error:
            self.reply(400, {"error": str(error)})
        except (KeyError, FileNotFoundError):
            self.reply(404, {"error": "Not found"})
        except PermissionError as error:
            self.reply(403, {"error": str(error)})
        except Exception as error:
            # Internal detail belongs in local diagnostics, not responses containing request data.
            self.reply(503, {"error": type(error).__name__, "reason": "Request unavailable. Inspect the existing operation before retrying mutations."})

    def reply(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    do_GET = do_POST = do_PUT = handle_request


def serve(config, app):
    context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH, cafile=config["ca"])
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.verify_mode = ssl.CERT_REQUIRED
    context.load_cert_chain(config["cert"], config["key"])
    server = BoundedServer((config["listen"], config["port"]), Handler, context)
    server.app = app
    server.peer_name = config["peer_name"]
    server.serve_forever()
