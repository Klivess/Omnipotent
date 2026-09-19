"""Durable operations: fail closed on corruption and never replay uncertain input."""
import hashlib
import json
import re
import sqlite3
import time
from contextlib import contextmanager
from pathlib import Path


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,100}", value):
        raise ValueError("Invalid identifier")
    return value


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


class State:
    def __init__(self, directory):
        self.root = Path(directory)
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.path = self.root / "state.sqlite3"
        with self.connect() as db:
            db.executescript("""
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS operations (
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL, request TEXT NOT NULL,
                    fingerprint TEXT NOT NULL, state TEXT NOT NULL,
                    result TEXT NOT NULL DEFAULT '{}', created REAL NOT NULL, updated REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS computers (
                    id TEXT PRIMARY KEY, project TEXT NOT NULL, agent TEXT NOT NULL,
                    record TEXT NOT NULL, UNIQUE(project, agent));
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """)

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.path, timeout=30)
        try:
            db.row_factory = sqlite3.Row
            db.execute("PRAGMA synchronous=FULL")
            db.execute("PRAGMA busy_timeout=30000")
            with db:
                yield db
        finally:
            db.close()

    @staticmethod
    def decode(row):
        if row is None:
            return None
        value = dict(row)
        for key in ("request", "result"):
            value[key] = json.loads(value[key])
        return value

    def submit(self, op_id, kind, request):
        identifier(op_id)
        digest = fingerprint({"kind": kind, "request": request})
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT * FROM operations WHERE id=?", (op_id,)).fetchone()
            if row:
                if row["fingerprint"] != digest:
                    raise ValueError("Operation ID already belongs to a different request")
                return self.decode(row), False
            now = time.time()
            db.execute("INSERT INTO operations(id,kind,request,fingerprint,state,created,updated) VALUES(?,?,?,?,?,?,?)",
                       (op_id, kind, json.dumps(request), digest, "queued", now, now))
        return self.get(op_id), True

    def get(self, op_id):
        with self.connect() as db:
            return self.decode(db.execute("SELECT * FROM operations WHERE id=?", (identifier(op_id),)).fetchone())

    def list(self, states=None, kind=None):
        with self.connect() as db:
            clauses, values = [], []
            if states:
                clauses.append("state IN (" + ",".join("?" * len(states)) + ")")
                values.extend(states)
            if kind:
                clauses.append("kind=?")
                values.append(kind)
            query = "SELECT * FROM operations" + (" WHERE " + " AND ".join(clauses) if clauses else "")
            rows = db.execute(query + (" ORDER BY created" if states else " ORDER BY created DESC LIMIT 200"), values)
            return [self.decode(row) for row in rows]

    def update(self, op_id, status, result=None):
        if status not in ("queued", "running", "completed", "failed", "interrupted", "outcome_unknown"):
            raise ValueError("Unknown operation state")
        with self.connect() as db:
            db.execute("UPDATE operations SET state=?, result=?, updated=? WHERE id=?",
                       (status, json.dumps(result or {}), time.time(), identifier(op_id)))

    def claim(self, op_id):
        with self.connect() as db:
            changed = db.execute("UPDATE operations SET state='running',updated=? WHERE id=? AND state='queued'",
                                 (time.time(), op_id)).rowcount
            return changed == 1

    def recover_actions(self):
        with self.connect() as db:
            db.execute("UPDATE operations SET state='outcome_unknown', result=?,updated=? WHERE state='running' AND kind IN ('action','terminal_input')",
                       (json.dumps({"reason": "Session service restarted during execution. Inspect before acting; do not replay."}), time.time()))

    def computer(self, computer_id):
        with self.connect() as db:
            row = db.execute("SELECT record FROM computers WHERE id=?", (identifier(computer_id),)).fetchone()
            return json.loads(row[0]) if row else None

    def computers(self, project=None):
        with self.connect() as db:
            rows = db.execute("SELECT record FROM computers" + (" WHERE project=?" if project else ""),
                              (project,) if project else ())
            return [json.loads(row[0]) for row in rows]

    def save_computer(self, record):
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            previous = db.execute("SELECT record FROM computers WHERE id=?", (identifier(record["computerID"]),)).fetchone()
            if previous:
                current = json.loads(previous[0])
                if any(current[key] != record[key] for key in ("projectID", "agentID")):
                    raise ValueError("Computer ownership is immutable")
                # Optional migration metadata may be written by another request while
                # the lifecycle scheduler holds an older snapshot. Missing keys are
                # not deletions; callers clear fields explicitly with None.
                current.update(record)
                record = current
            db.execute("INSERT INTO computers VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET record=excluded.record",
                       (identifier(record["computerID"]), identifier(record["projectID"]),
                        identifier(record["agentID"]), json.dumps(record)))

    def setting(self, key, value=None):
        with self.connect() as db:
            if value is not None:
                db.execute("INSERT INTO settings VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, json.dumps(value)))
                return value
            row = db.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
            return json.loads(row[0]) if row else None


def public_operation(op):
    """Scripts, keystrokes and credentials are never included in inventory responses."""
    if op is None:
        raise KeyError("Operation not found")
    return {key: value for key, value in op.items() if key not in ("request", "fingerprint")}
