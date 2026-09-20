import base64
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from ka_worker.state import State, public_operation
from ka_worker.pressure import Admission, Pressure, MIB, host_headroom
from ka_worker.broker import Broker
from ka_worker.session import Session
from ka_worker.workspace import Workspace


class DurableOperations(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.state = State(self.temp.name)

    def test_reopen_preserves_job_and_single_claim(self):
        self.state.submit("job-1", "terminal", {"command": "sleep 300"})
        self.assertTrue(self.state.claim("job-1"))
        reopened = State(self.temp.name)
        self.assertFalse(reopened.claim("job-1"))
        self.assertEqual("running", reopened.get("job-1")["state"])

    def test_lifecycle_update_preserves_migration_metadata_and_ownership(self):
        record = {"computerID": "ka-one", "projectID": "project", "agentID": "agent", "state": "starting"}
        self.state.save_computer(record)
        self.state.save_computer({**record, "profileImported": True})
        self.state.save_computer({**record, "state": "ready", "incusOperation": None})
        self.assertTrue(self.state.computer("ka-one")["profileImported"])
        with self.assertRaises(ValueError):
            self.state.save_computer({**record, "projectID": "another"})

    def test_same_id_different_command_is_rejected(self):
        self.state.submit("one", "terminal", {"command": "echo original"})
        with self.assertRaises(ValueError):
            self.state.submit("one", "terminal", {"command": "echo duplicate"})

    def test_interrupted_input_is_never_replayed(self):
        self.state.submit("click", "action", {"tool": "computer_click"})
        self.state.claim("click")
        reopened = State(self.temp.name)
        reopened.recover_actions()
        self.assertEqual("outcome_unknown", reopened.get("click")["state"])
        self.assertFalse(reopened.claim("click"))

    def test_action_recovery_does_not_interrupt_terminal_jobs(self):
        self.state.submit("job", "terminal", {"command": "work"})
        self.state.claim("job")
        self.state.recover_actions()
        self.assertEqual("running", self.state.get("job")["state"])

    def test_private_scripts_are_absent_from_inventory(self):
        op, _ = self.state.submit("one", "terminal", {"command": "secret credential"})
        self.assertNotIn("secret", json.dumps(public_operation(op)))

    def test_corruption_is_not_replaced_with_empty_state(self):
        corrupted = Path(self.temp.name) / "bad"
        corrupted.mkdir()
        path = corrupted / "state.sqlite3"
        path.write_bytes(b"preserve this corrupt original")
        with self.assertRaises(sqlite3.DatabaseError):
            State(corrupted)
        self.assertEqual(b"preserve this corrupt original", path.read_bytes())

    def test_parallel_claims_execute_once(self):
        self.state.submit("one", "action", {})
        results = []
        threads = [threading.Thread(target=lambda: results.append(self.state.claim("one"))) for _ in range(16)]
        for thread in threads: thread.start()
        for thread in threads: thread.join()
        self.assertEqual(1, sum(results))


class ResourceAdmission(unittest.TestCase):
    def test_thin_disk_admission_checks_actual_host_capacity_and_freshness(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'host.json'
            self.assertIsNotNone(host_headroom(path))
            path.write_text(json.dumps({'sampled': time.time(), 'freeBytes': 2 * 1024 ** 3}))
            self.assertIsNotNone(host_headroom(path))
            path.write_text(json.dumps({'sampled': time.time() - 600, 'freeBytes': 80 * 1024 ** 3}))
            self.assertIsNotNone(host_headroom(path))
            path.write_text(json.dumps({'sampled': time.time(), 'freeBytes': 80 * 1024 ** 3}))
            self.assertIsNone(host_headroom(path))

    def pressure(self, available=8192, full=0, swap_free=8192, age=0):
        return Pressure(available * MIB, 16384 * MIB, swap_free * MIB, 8192 * MIB, full, time.time() - age)

    def test_reservations_prevent_simultaneous_oversubscription(self):
        admission = Admission()
        pressure = self.pressure(available=3000)
        self.assertIsNone(admission.reason(pressure))
        self.assertIsNotNone(admission.reason(pressure, admission.start))

    def test_memory_swap_and_stale_telemetry_pause_admissions(self):
        for pressure in (self.pressure(available=100), self.pressure(available=2000, full=8), self.pressure(swap_free=100), self.pressure(age=60)):
            with self.subTest(pressure=pressure): self.assertIsNotNone(Admission().reason(pressure))

    def test_stale_hyper_v_psi_does_not_block_when_memory_is_available(self):
        self.assertIsNone(Admission().reason(self.pressure(available=3200, full=12)))

    def test_capacity_queue_survives_restart_and_drains(self):
        with tempfile.TemporaryDirectory() as directory:
            broker = Broker.__new__(Broker)
            broker.config = {}
            broker.state = State(directory)
            broker.admission = Admission()
            broker.state.save_computer({"computerID": "ka-one", "projectID": "project", "agentID": "agent", "state": "queued"})
            broker.state.submit("start-ka-one", "start", {"computerID": "ka-one"})
            starts = []
            broker.provision = lambda record: starts.append(record["computerID"]) or True
            with patch("ka_worker.broker.Pressure.read", return_value=self.pressure(available=100)):
                broker.tick()
            self.assertEqual([], starts)
            self.assertEqual("queued", State(directory).get("start-ka-one")["state"])
            with patch("ka_worker.broker.Pressure.read", return_value=self.pressure()): broker.tick()
            self.assertEqual(["ka-one"], starts)
            self.assertEqual("completed", broker.state.get("start-ka-one")["state"])

    def test_existing_computer_is_not_stopped_under_pressure(self):
        with tempfile.TemporaryDirectory() as directory:
            broker = Broker.__new__(Broker)
            broker.config = {}
            broker.state = State(directory)
            broker.admission = Admission()
            broker.state.save_computer({"computerID": "ka-live", "projectID": "project", "agentID": "agent", "state": "ready"})
            with patch("ka_worker.broker.Pressure.read", return_value=self.pressure(available=0)):
                broker.tick()
            self.assertEqual("ready", broker.state.computer("ka-live")["state"])


class SessionControl(unittest.TestCase):
    def test_duplicate_action_does_not_execute_twice(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(Session, "action", return_value={"text": "executed"}) as action:
                session = Session({"state": directory})
                try:
                    payload = {"operationID": "click", "tool": "computer_click", "arguments": {"x": 5}, "actorID": "agent"}
                    session("POST", "/actions", {}, payload)
                    session("POST", "/actions", {}, payload)
                    deadline = time.time() + 3
                    while session.state.get("click")["state"] != "completed" and time.time() < deadline: time.sleep(.02)
                    self.assertEqual(1, action.call_count)
                finally:
                    session.stop.set(); session.thread.join(3)

    def test_human_lease_blocks_agent_actions_but_not_job_queries(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(Session, "action", return_value={}) as action:
                session = Session({"state": directory})
                try:
                    self.assertTrue(session.lease("human")["acquired"])
                    session("POST", "/actions", {}, {"operationID": "click", "tool": "computer_click", "arguments": {}, "actorID": "agent"})
                    time.sleep(.25)
                    self.assertEqual(0, action.call_count)
                    self.assertEqual((200, []), session("GET", "/jobs", {}, {}))
                    session.lease("human", release=True)
                    deadline = time.time() + 3
                    while not action.called and time.time() < deadline: time.sleep(.02)
                    self.assertEqual(1, action.call_count)
                finally:
                    session.stop.set(); session.thread.join(3)


@unittest.skipUnless(sys.platform == "linux", "Linux dirfd and PTY integration")
class LinuxWorkspace(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "projects/p").mkdir(parents=True)
        self.workspace = Workspace(self.root / "projects")

    def test_symlink_cannot_escape_project(self):
        (self.root / "outside").write_text("secret")
        (self.root / "projects/p/link").symlink_to(self.root / "outside")
        with self.assertRaises(OSError): self.workspace.read("p", "link")
        with self.assertRaises(ValueError): self.workspace.read("p", "../outside")

    def test_transfer_retry_and_version_conflict(self):
        import hashlib
        data = b"hello"
        body = {"transferID": "transfer", "data": base64.b64encode(data).decode(), "offset": 0,
                "commit": False, "expectedVersion": None, "sha256": hashlib.sha256(data).hexdigest()}
        self.workspace.write("p", "file.txt", body)
        self.workspace.write("p", "file.txt", body)
        body["commit"] = True
        with patch("ka_worker.workspace.os.fchown"):
            self.workspace.write("p", "file.txt", body)
            self.workspace.write("p", "file.txt", body)
        self.assertEqual(data, (self.root / "projects/p/file.txt").read_bytes())
        (self.root / "projects/p/file.txt").write_text("external edit")
        with self.assertRaises(ValueError): self.workspace.write("p", "file.txt", body)
        self.assertEqual("external edit", (self.root / "projects/p/file.txt").read_text())

    def test_runner_survives_client_disappearing_and_reports_output(self):
        state = State(self.root / "jobs")
        state.submit("job", "terminal", {"command": "printf before; sleep 1; printf after", "cwd": str(self.root), "interactive": False})
        process = subprocess.Popen([sys.executable, "-m", "ka_worker.jobs", str(state.root), "job"])
        self.assertEqual(0, process.wait(timeout=10))
        self.assertEqual("completed", State(state.root).get("job")["state"])
        self.assertEqual(b"beforeafter", (state.root / "job.output").read_bytes())


if __name__ == "__main__":
    unittest.main()
