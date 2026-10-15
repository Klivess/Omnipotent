"""Tests for klive-computerd (Containers/browser-service.py) that run on any OS.

The X11-facing routes (screenshot, input execution, clipboard, windows) need a real display and are
exercised in a desktop container; everything here — the HTTP surface, token handling, bounded exec,
the unchanged /run contract and the input-plan compiler — runs on the Windows dev box too.

    python -I Omnipotent.Tests/Projects/computerd_test.py
"""
import importlib.util
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
SERVICE = os.path.normpath(os.path.join(HERE, "..", "..", "Omnipotent", "Services", "Projects", "Containers",
                                        "browser-service.py"))


def load_module():
    spec = importlib.util.spec_from_file_location("computerd", SERVICE)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def free_port():
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    return port


class DaemonFixture(object):
    def __init__(self):
        self.home = tempfile.mkdtemp(prefix="computerd-home-")
        self.token_file = os.path.join(self.home, "token")
        self.port = free_port()
        environment = dict(os.environ)
        environment.update({
            "KLIVE_BROWSER_SERVICE_PORT": str(self.port),
            "KLIVE_COMPUTERD_TOKEN_FILE": self.token_file,
            "KLIVE_COMPUTERD_EXEC_WRAPPER": "none",
            "KLIVE_COMPUTERD_EXEC_GRACE": "1",
            "KLIVE_CDP_ROOT": "http://127.0.0.1:1",
            "HOME": self.home,
            "USERPROFILE": self.home,
        })
        environment.pop("KLIVE_COMPUTERD_TOKEN", None)
        self.process = subprocess.Popen([sys.executable, "-I", SERVICE], env=environment,
                                        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        deadline = time.time() + 15
        while time.time() < deadline:
            try:
                self.get("/health")
                return
            except Exception:
                time.sleep(0.1)
        self.close()
        raise RuntimeError("computerd did not start")

    def url(self, path):
        return "http://127.0.0.1:%d%s" % (self.port, path)

    def get(self, path, token=None):
        request = urllib.request.Request(self.url(path))
        if token:
            request.add_header("X-Klive-Token", token)
        return self._open(request)

    def post(self, path, body, token=None, bearer=False):
        raw = json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.url(path), data=raw, method="POST")
        request.add_header("Content-Type", "application/json")
        if token:
            if bearer:
                request.add_header("Authorization", "Bearer " + token)
            else:
                request.add_header("X-Klive-Token", token)
        return self._open(request)

    @staticmethod
    def _open(request):
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        try:
            with opener.open(request, timeout=30) as response:
                return response.status, json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read().decode("utf-8") or "{}")

    def write_token(self, value):
        with open(self.token_file, "w", encoding="utf-8") as handle:
            handle.write(value + "\n")

    def close(self):
        try:
            self.process.kill()
            self.process.wait(5)
        except Exception:
            pass


class HttpSurfaceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.daemon = DaemonFixture()

    @classmethod
    def tearDownClass(cls):
        cls.daemon.close()

    def setUp(self):
        try:
            os.remove(self.daemon.token_file)
        except OSError:
            pass

    def test_health_is_a_superset_of_the_original_contract(self):
        status, body = self.daemon.get("/health")
        self.assertEqual(200, status)
        self.assertTrue(body["ok"])
        self.assertFalse(body["browserUp"])           # no browser on the test box
        self.assertEqual("klive-computerd", body["service"])
        self.assertEqual(2, body["protocol"])
        self.assertIn("exec", body["capabilities"])
        self.assertIn("components", body)
        self.assertIn("memory", body["resources"])

    def test_privileged_routes_refuse_when_no_token_is_configured(self):
        status, body = self.daemon.post("/exec", {"command": "true"})
        self.assertEqual(403, status)
        self.assertEqual("no-token-configured", body["error"])

    def test_privileged_routes_need_the_right_token(self):
        self.daemon.write_token("s3cret-token")
        status, body = self.daemon.post("/exec", {"argv": [sys.executable, "-c", "print('hi')"]})
        self.assertEqual(401, status)
        self.assertEqual("token-required", body["error"])
        status, body = self.daemon.post("/exec", {"argv": [sys.executable, "-c", "print('hi')"]}, token="wrong")
        self.assertEqual(401, status)
        self.assertEqual("token-rejected", body["error"])

    def test_exec_runs_argv_and_reports_output_and_exit_code(self):
        self.daemon.write_token("tok")
        status, body = self.daemon.post("/exec", {
            "argv": [sys.executable, "-c", "import sys; print('out'); sys.stderr.write('err'); sys.exit(3)"],
        }, token="tok")
        self.assertEqual(200, status)
        self.assertEqual(3, body["exitCode"])
        self.assertEqual("out", body["stdout"].strip())
        self.assertEqual("err", body["stderr"].strip())
        self.assertFalse(body["timedOut"])
        self.assertFalse(body["truncated"])

    def test_exec_accepts_a_bearer_token_too(self):
        self.daemon.write_token("tok2")
        status, body = self.daemon.post("/exec", {"argv": [sys.executable, "-c", "print(1)"]}, token="tok2", bearer=True)
        self.assertEqual(200, status, body)
        self.assertEqual(0, body["exitCode"])

    def test_exec_bounds_output_without_blocking_the_command(self):
        self.daemon.write_token("tok")
        status, body = self.daemon.post("/exec", {
            "argv": [sys.executable, "-c", "import sys; sys.stdout.write('x' * 500000)"],
            "maxOutputBytes": 2048,
        }, token="tok")
        self.assertEqual(200, status)
        self.assertEqual(0, body["exitCode"])
        self.assertTrue(body["truncated"])
        self.assertEqual(2048, len(body["stdout"]))

    def test_exec_kills_a_command_that_outlives_its_budget(self):
        self.daemon.write_token("tok")
        started = time.time()
        status, body = self.daemon.post("/exec", {
            "argv": [sys.executable, "-c", "import time; time.sleep(30)"], "timeoutSeconds": 1,
        }, token="tok")
        self.assertEqual(200, status)
        self.assertTrue(body["timedOut"])
        self.assertLess(time.time() - started, 12)

    def test_exec_passes_stdin(self):
        self.daemon.write_token("tok")
        status, body = self.daemon.post("/exec", {
            "argv": [sys.executable, "-c", "import sys; print(sys.stdin.read().upper())"], "stdin": "hello",
        }, token="tok")
        self.assertEqual(200, status)
        self.assertEqual("HELLO", body["stdout"].strip())

    def test_exec_rejects_malformed_requests(self):
        self.daemon.write_token("tok")
        self.assertEqual(400, self.daemon.post("/exec", {}, token="tok")[0])
        self.assertEqual(400, self.daemon.post("/exec", {"argv": "not-a-list"}, token="tok")[0])
        self.assertEqual(400, self.daemon.post("/exec", {"argv": ["a\x00b"]}, token="tok")[0])

    def test_a_missing_executable_is_a_result_not_a_crash(self):
        self.daemon.write_token("tok")
        status, body = self.daemon.post("/exec", {"argv": ["definitely-not-a-real-binary-xyz"]}, token="tok")
        self.assertEqual(200, status)
        self.assertEqual(127, body["exitCode"])

    def test_run_keeps_its_allowlist(self):
        self.assertEqual(400, self.daemon.post("/run", {"mode": "exec", "payload": ""})[0])
        self.assertEqual(400, self.daemon.post("/run", {"mode": "dom", "payload": "$(whoami)"})[0])
        self.assertEqual(400, self.daemon.post("/run", {"mode": "dom", "args": ["a b"]})[0])

    def test_unknown_routes_are_404(self):
        self.assertEqual(404, self.daemon.get("/nope")[0])
        self.assertEqual(404, self.daemon.post("/nope", {})[0])

    def test_a_rotated_token_file_takes_effect_without_a_restart(self):
        self.daemon.write_token("first")
        self.assertEqual(200, self.daemon.post("/exec", {"argv": [sys.executable, "-c", "0"]}, token="first")[0])
        time.sleep(0.05)
        self.daemon.write_token("second-and-longer")
        self.assertEqual(401, self.daemon.post("/exec", {"argv": [sys.executable, "-c", "0"]}, token="first")[0])
        self.assertEqual(200, self.daemon.post("/exec", {"argv": [sys.executable, "-c", "0"]}, token="second-and-longer")[0])


class InputPlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.m = load_module()

    def test_key_names_map_to_x_keysyms(self):
        self.assertEqual("ctrl+l", self.m.xdo_chord("ctrl+l"))
        self.assertEqual("Return", self.m.xdo_chord("enter"))
        self.assertEqual("ctrl+shift+t", self.m.xdo_chord("Ctrl+Shift+t"))
        self.assertEqual("F5", self.m.xdo_chord("f5"))
        self.assertEqual("Prior", self.m.xdo_chord("pageup"))
        self.assertEqual("ctrl+plus", self.m.xdo_chord("ctrl++"))
        self.assertEqual("slash", self.m.xdo_chord("/"))
        with self.assertRaises(ValueError):
            self.m.xdo_chord("ctrl+notakey")

    def test_pointer_events_chain_into_one_invocation(self):
        commands = self.m.build_input_commands([
            {"t": "move", "x": 10, "y": 20}, {"t": "sleep", "ms": 12},
            {"t": "down", "b": 1}, {"t": "up", "b": 1},
        ])
        self.assertEqual([["xdotool", "mousemove", "10", "20", "sleep", "0.012", "mousedown", "1", "mouseup", "1"]],
                         commands)

    def test_key_and_type_each_get_their_own_invocation(self):
        commands = self.m.build_input_commands([
            {"t": "move", "x": 1, "y": 2}, {"t": "key", "k": "ctrl+a"},
            {"t": "type", "text": "-not an option"}, {"t": "move", "x": 3, "y": 4},
        ])
        self.assertEqual(4, len(commands))
        self.assertEqual(["xdotool", "mousemove", "1", "2"], commands[0])
        self.assertEqual("key", commands[1][1])
        self.assertEqual("ctrl+a", commands[1][-1])
        self.assertEqual(["xdotool", "type", "--delay", "12", "--", "-not an option"], commands[2])
        self.assertEqual(["xdotool", "mousemove", "3", "4"], commands[3])

    def test_scroll_maps_to_wheel_buttons(self):
        commands = self.m.build_input_commands([{"t": "scroll", "dy": -3}, {"t": "scroll", "dy": 2, "dx": 1}])
        flat = commands[0]
        self.assertIn("5", flat)   # down
        self.assertIn("4", flat)   # up
        self.assertIn("7", flat)   # right

    def test_bad_plans_are_rejected(self):
        for bad in ([], [{"t": "teleport"}], [{"t": "sleep", "ms": 10000}] * 13, "not-a-list"):
            with self.assertRaises(ValueError):
                self.m.build_input_commands(bad)


class TokenLocationTests(unittest.TestCase):
    """The daemon must look where the harness delivers the token, or every privileged call is refused
    and the harness silently falls back to docker exec for the life of the container."""

    def test_the_daemon_reads_the_path_the_harness_writes(self):
        import re
        client = os.path.normpath(os.path.join(os.path.dirname(SERVICE), "ComputerDaemonClient.cs"))
        with open(client, encoding="utf-8") as handle:
            source = handle.read()
        directory = re.search(r'ContainerTokenDirectory\s*=\s*"([^"]+)"', source).group(1)
        name = re.search(r'ContainerTokenFileName\s*=\s*"([^"]+)"', source).group(1)
        self.assertIn(directory.rstrip("/") + "/" + name, load_module()._token_paths())


if __name__ == "__main__":
    unittest.main(verbosity=2)
