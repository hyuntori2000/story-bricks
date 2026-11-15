"""Local index of completed story movies. Listing and playback never submit AI jobs."""
from __future__ import annotations

from datetime import datetime, timezone
import json
from pathlib import Path
import re
import threading
from workspace_storage import write_json

ID = re.compile(r"^[0-9a-f]{32}$")
ARCHIVE = re.compile(r"^[0-9a-f]{64}\.json$")
ROUTE = re.compile(r"^/story-videos/([0-9a-f]{32})\.mp4$")
MAX_VIDEO_BYTES = 128 * 1024 * 1024


class LibraryError(Exception):
    def __init__(self, code, message, status=404):
        self.code, self.message, self.status = code, message, status
        super().__init__(message)


class StoryLibrary:
    def __init__(self, workspace_directory):
        self.root = Path(workspace_directory).resolve()
        self.index = self.root / "story-library" / "index.json"
        self.lock = threading.Lock()

    def _safe(self, path):
        try:
            relative = path.relative_to(self.root)
            current = self.root
            for part in relative.parts:
                current = current / part
                if current.is_symlink():
                    return False
            return path.resolve(strict=True).is_relative_to(self.root)
        except (ValueError, OSError):
            return False

    def _json(self, path):
        if not self._safe(path) or not path.is_file() or path.stat().st_size > 256 * 1024:
            raise ValueError("Invalid local metadata")
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            raise ValueError("Invalid local metadata")
        return value

    def _movie(self, path):
        if not self._safe(path) or not path.is_file() or not 24 <= path.stat().st_size <= MAX_VIDEO_BYTES:
            return False
        with path.open("rb") as source:
            header = source.read(12)
        return header[4:8] == b"ftyp" and 16 <= int.from_bytes(header[:4], "big") <= path.stat().st_size

    def _entry(self, metadata):
        job = self._json(metadata)
        owner, job_id, session = job.get("experienceSessionId"), job.get("jobId"), job.get("sessionId")
        if (job.get("workspaceId") != "demo" or job.get("status") != "ready" or
                any(not isinstance(value, str) or not ID.fullmatch(value) for value in (owner, job_id, session)) or
                metadata.parent.name != job_id or metadata.parents[2].name != owner or
                job.get("videoPath") != "/story-videos/" + job_id + ".mp4"):
            return None
        movie = metadata.parent / "story.mp4"
        if not self._movie(movie):
            return None
        words = []
        archive = job.get("storyArchive", "")
        if isinstance(archive, str) and ARCHIVE.fullmatch(archive):
            try:
                record = self._json(metadata.parents[2] / "requests" / archive)
                if (record.get("operation") == "story" and record.get("status") == "completed" and
                        record.get("httpStatus") == 200 and all(record.get(key) == job.get(key)
                        for key in ("workspaceId", "experienceSessionId", "sessionId"))):
                    for word in record.get("input", {}).get("context", {}).get("words", []):
                        value = word.get("word", "") if isinstance(word, dict) else ""
                        if isinstance(value, str) and re.fullmatch(r"[A-Za-z.]{1,32}", value) and value.strip("."):
                            words.append(value.strip(".").upper())
            except (ValueError, TypeError, AttributeError, OSError):
                pass
        timestamp = job.get("createdAtUtc", "")
        created = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        if created.tzinfo is None:
            raise ValueError("Unzoned creation date")
        duration = sum(float(shot.get("duration", 0)) for shot in job.get("shots", []) if isinstance(shot, dict))
        if not 0 <= duration <= 600:
            duration = 0
        return {"id": job_id, "workspaceId": "demo", "experienceSessionId": owner, "sessionId": session,
                "title": " + ".join(words[:4])[:100] or "Your story", "words": words[:32],
                "createdAtUtc": created.astimezone(timezone.utc).isoformat(), "durationSeconds": duration,
                "videoPath": job["videoPath"]}

    def _scan(self):
        entries, seen = [], set()
        sessions = self.root / "create-world" / "sessions"
        if not sessions.is_dir() or not self._safe(sessions):
            return entries
        for metadata in sessions.glob("*/videos/*/job.json"):
            try:
                entry = self._entry(metadata)
                if entry and entry["id"] not in seen:
                    entries.append(entry)
                    seen.add(entry["id"])
            except (ValueError, TypeError, AttributeError, OSError, OverflowError):
                continue
        return sorted(entries, key=lambda item: (item["createdAtUtc"], item["id"]), reverse=True)

    def listing(self):
        with self.lock:
            entries = self._scan()
            result = {"schemaVersion": 1, "stories": entries[:128]}
            # The persistent memory is an index; movie bytes stay in their original owned archives.
            if self.index.parent.is_symlink() or self.index.is_symlink():
                raise LibraryError("library_storage", "The saved-story index is unavailable.", 503)
            try:
                write_json(self.index, {"schemaVersion": 1, "stories": entries})
            except OSError:
                raise LibraryError("library_storage", "The saved-story index could not be saved.", 503) from None
            return result

    def video_file(self, route):
        match = ROUTE.fullmatch(route) if isinstance(route, str) else None
        if not match:
            raise LibraryError("library_video_not_found", "The saved animation is unavailable.")
        with self.lock:
            for entry in self._scan():
                if entry["id"] == match[1]:
                    return self.root / "create-world" / "sessions" / entry["experienceSessionId"] / "videos" / entry["id"] / "story.mp4"
        raise LibraryError("library_video_not_found", "The saved animation is unavailable.")
