"""Explicit ownership and durable local session records. No provider dependencies."""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import threading


WORKSPACE_DIRECTORIES = {"judy": "judy", "demo": "create-world"}
UUID_N = re.compile(r"^[0-9a-f]{32}$")
OWNER_FIELDS = ("workspaceId", "experienceSessionId")


class StorageError(Exception):
    def __init__(self, code, message, status=400, details=None):
        super().__init__(message)
        self.code, self.message, self.status = code, message, status
        self.details = details or {}


@dataclass(frozen=True)
class Owner:
    workspace_id: str
    experience_session_id: str

    def fields(self):
        return {"workspaceId": self.workspace_id, "experienceSessionId": self.experience_session_id}


def owner_from(value, required=False):
    present = [key in value for key in OWNER_FIELDS]
    # Unity JsonUtility emits unset newly added string fields as null/empty.
    # Both empty means the old unscoped transport; never infer a real mode.
    if not required and all(value.get(key) in (None, "") for key in OWNER_FIELDS):
        return None
    if (not all(present) or not isinstance(value.get("workspaceId"), str) or
            value.get("workspaceId") not in WORKSPACE_DIRECTORIES or
            not isinstance(value.get("experienceSessionId"), str) or
            not UUID_N.fullmatch(value["experienceSessionId"])):
        raise StorageError("invalid_workspace_owner", "Provide workspaceId (judy or demo) and a lowercase 32-character experienceSessionId.")
    return Owner(value["workspaceId"], value["experienceSessionId"])


def owner_from_query(query):
    value = {}
    for key in OWNER_FIELDS:
        if key in query:
            if len(query[key]) != 1:
                raise StorageError("invalid_workspace_owner", "Workspace ownership must occur once in the query.")
            value[key] = query[key][0]
    return owner_from(value)


def session_directory(root, owner):
    # Validate even internal callers: transport ID rules permit dots and must
    # never be reused as filesystem component rules.
    owner = owner_from(owner.fields(), required=True)
    root = Path(root).resolve()
    path = (root / WORKSPACE_DIRECTORIES[owner.workspace_id] / "sessions" / owner.experience_session_id).resolve()
    if root not in path.parents:
        raise StorageError("invalid_storage_path", "Session storage must remain under its configured directory.")
    return path


def now():
    return datetime.now(timezone.utc).isoformat()


def encode(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".pending-", dir=str(path.parent))
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encode(value))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def read_json(path):
    def reject_constant(value):
        raise ValueError("Nonfinite JSON value: " + value)
    return json.loads(Path(path).read_text(encoding="utf-8"), parse_constant=reject_constant)


class SessionStateStore:
    def __init__(self, root):
        self.root = Path(root)
        self.lock = threading.Lock()

    def save(self, value):
        owner = owner_from(value, required=True)
        revision, state = value.get("revision"), value.get("state")
        if type(revision) is not int or not 0 <= revision <= 9007199254740991 or not isinstance(state, dict):
            raise StorageError("invalid_session_state", "Provide a nonnegative integer revision and a state object.")
        reply = dict(owner.fields(), revision=revision)
        # Serialization validates finite JSON before touching storage.
        digest = hashlib.sha256(encode(state)).hexdigest()
        path = session_directory(self.root, owner) / "session-state.json"
        with self.lock:
            previous = read_json(path) if path.exists() else None
            if previous is not None:
                if previous["revision"] > revision:
                    raise StorageError("stale_revision", "A newer session snapshot is already saved.", 409,
                                       dict(reply, storedRevision=previous["revision"]))
                if previous["revision"] == revision:
                    if previous["stateSha256"] != digest:
                        raise StorageError("revision_conflict", "This revision was already saved with different state.", 409,
                                           dict(reply, storedRevision=previous["revision"]))
                    return dict(reply, ok=True, duplicate=True)
            write_json(path, dict(reply, schemaVersion=1, savedAtUtc=now(), stateSha256=digest, state=state))
        return dict(reply, ok=True, duplicate=False)


class RequestArchive:
    """One backend owns this store. Interrupted generation is never silently rerun."""
    def __init__(self, root):
        self.root = Path(root)

    def path(self, owner, session, request):
        name = hashlib.sha256(encode([session, request])).hexdigest() + ".json"
        return session_directory(self.root, owner) / "requests" / name

    def begin(self, owner, session, request, digest, operation, input_record):
        path = self.path(owner, session, request)
        if path.exists():
            record = read_json(path)
            if record.get("digest") != digest:
                raise StorageError("request_conflict", "A requestId was reused with different input.", 409)
            if record.get("status") == "completed":
                return path, (record["httpStatus"], record["result"])
            raise StorageError("request_interrupted", "This request was interrupted. Its outcome is unknown; start a new explicit request to retry.", 409)
        record = dict(owner.fields(), schemaVersion=1, sessionId=session, requestId=request,
                      operation=operation, digest=digest, status="pending", startedAtUtc=now(), input=input_record)
        write_json(path, record)
        return path, None

    def complete(self, path, status, result):
        record = read_json(path)
        record.update(status="completed", completedAtUtc=now(), httpStatus=status, result=result)
        if status == 200 and record["operation"] == "story":
            owner = owner_from(record, required=True)
            write_json(session_directory(self.root, owner) / "story" / path.name, record)
        write_json(path, record)
