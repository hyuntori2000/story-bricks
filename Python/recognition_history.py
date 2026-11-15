"""Durable, bounded recent recognition history; no camera or inference dependency."""

from copy import deepcopy
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import threading
from workspace_storage import owner_from, session_directory


MAX_RECENT_RECORDS = 1000
BRANCHES = ("ocr", "braille")


def _timestamp(value):
    if not isinstance(value, str):
        raise ValueError("startedAtUtc must be an ISO timestamp with a timezone")
    stamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if stamp.tzinfo is None:
        raise ValueError("startedAtUtc must include a timezone")
    return stamp.timestamp()


def _patch(record):
    # Detach caller-owned branch dictionaries and reject non-JSON/nonfinite values.
    result = json.loads(json.dumps(record, allow_nan=False))
    if not isinstance(result, dict):
        raise ValueError("Recognition history expects a record object")
    for field in ("sessionId", "frameId"):
        if not isinstance(result.get(field), str) or not 1 <= len(result[field]) <= 128:
            raise ValueError(field + " must be a nonempty short identifier")
    if "startedAtUtc" in result:
        _timestamp(result["startedAtUtc"])
    for branch in BRANCHES:
        if branch in result and not isinstance(result[branch], dict):
            raise ValueError(branch + " must be a result object")
    return result


def _key(record):
    return record["sessionId"], record["frameId"]


def _merge(previous, update):
    result = deepcopy(previous or {
        "algorithmCommit": "", "selectedSource": None, "selectedWord": None,
        "decisionMs": None, "complete": False, "disagreement": False,
        "ocr": {"status": "pending", "word": None, "elapsedMs": None, "reason": ""},
        "braille": {"status": "pending", "word": None, "elapsedMs": None, "reason": ""},
    })
    for field, value in update.items():
        if field in BRANCHES:
            result[field].update(value)
        else:
            result[field] = value
    return result


class RecognitionHistory:
    """Append patches to JSONL; retain only the newest frame records in memory.

    Ordering uses capture start time, not branch completion time. Two streaming
    restore passes first choose recent frame IDs, then merge all their updates.
    Thus late patches for an evicted frame cannot erase its earlier disk record.
    Use one instance per log file; worker threads may share that instance.
    """

    def __init__(self, path, *, max_records=MAX_RECENT_RECORDS):
        if type(max_records) is not int or not 1 <= max_records <= MAX_RECENT_RECORDS:
            raise ValueError("max_records must be between 1 and 1000")
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.max_records = max_records
        self._lock = threading.Lock()
        self._records = {}
        self._updates = 0
        self._skipped_lines = 0
        self._restore()

    def _entries(self):
        if not self.path.exists():
            return
        with self.path.open("r", encoding="utf-8", errors="replace") as stream:
            for line in stream:
                try:
                    yield _patch(json.loads(line))
                except (ValueError, TypeError, OverflowError):
                    yield None

    @staticmethod
    def _order(record):
        return _timestamp(record["startedAtUtc"]), _key(record)

    def _trim(self):
        if len(self._records) > self.max_records:
            oldest = min(self._records, key=lambda key: self._order(self._records[key]))
            del self._records[oldest]

    def _restore(self):
        # Selection is bounded even when the log contains years of updates.
        for update in self._entries():
            if update is None:
                self._skipped_lines += 1
                continue
            self._updates += 1
            if "startedAtUtc" in update:
                self._records[_key(update)] = _merge(None, update)
                self._trim()
        selected = self._records
        restored = {}
        for update in self._entries():
            if update is not None and _key(update) in selected:
                key = _key(update)
                restored[key] = _merge(restored.get(key), update)
        self._records = restored

    def _read_frame(self, key):
        result = None
        for update in self._entries():
            if update is not None and _key(update) == key:
                result = _merge(result, update)
        return result

    def __call__(self, record):
        update = _patch(record)
        with self._lock:
            key = _key(update)
            previous = self._records.get(key)
            # A partial completion for an evicted frame is uncommon; reconstruct
            # it from disk instead of keeping an unbounded all-time frame index.
            if previous is None and "startedAtUtc" not in update:
                previous = self._read_frame(key)
            if previous is None and "startedAtUtc" not in update:
                update["startedAtUtc"] = datetime.now(timezone.utc).isoformat()
            result = _merge(previous, update)
            encoded = json.dumps(update, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
            # Preserve an interrupted trailing line, but separate the next append
            # so that subsequent valid records remain readable after restart.
            separator = ""
            if self.path.exists() and self.path.stat().st_size:
                with self.path.open("rb") as existing:
                    existing.seek(-1, os.SEEK_END)
                    if existing.read(1) != b"\n":
                        separator = "\n"
            fd = os.open(self.path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
            with os.fdopen(fd, "a", encoding="utf-8") as stream:
                stream.write(separator + encoded + "\n")
                stream.flush()
                os.fsync(stream.fileno())
            self._updates += 1
            self._records[key] = result
            self._trim()

    def snapshot(self, limit=200):
        if type(limit) is not int:
            raise ValueError("limit must be an integer")
        limit = max(1, min(limit, self.max_records))
        with self._lock:
            records = sorted(self._records.values(), key=self._order, reverse=True)
            return {"schemaVersion": 1, "records": deepcopy(records[:limit]),
                    "retainedCount": len(records), "retentionLimit": self.max_records,
                    "updateCount": self._updates, "skippedLines": self._skipped_lines}


class WorkspaceRecognitionHistory:
    """Route immutable per-frame ownership, including late loser completions."""
    def __init__(self, legacy_path, workspace_directory):
        self.legacy = RecognitionHistory(legacy_path)
        self.workspace_directory = workspace_directory
        self._histories = {}
        self._lock = threading.Lock()

    def for_owner(self, owner):
        if owner is None:
            return self.legacy
        with self._lock:
            if owner not in self._histories:
                self._histories[owner] = RecognitionHistory(
                    session_directory(self.workspace_directory, owner) / "recognition" / "history.jsonl")
            return self._histories[owner]

    def __call__(self, record):
        self.for_owner(owner_from(record))(record)

    def snapshot(self, limit=200):
        return self.legacy.snapshot(limit)
