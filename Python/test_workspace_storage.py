"""Focused ownership/persistence checks; synthetic media and fake providers only."""
import hashlib
import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.parse import urlencode

from camera_frame_server import FrameHTTPServer, FrameStore, RequestError
import live_backend as lb
from recognition_history import WorkspaceRecognitionHistory
from test_camera_frame_server import sample_image, sample_metadata
from test_live_backend import FakeUpstream, context, wav_bytes
from workspace_storage import Owner, StorageError, owner_from, session_directory


class WorkspaceStorageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.workspace_root = self.root / "workspaces"
        self.judy = Owner("judy", "a" * 32)
        # Even identical session/request IDs in different modes stay separate.
        self.demo = Owner("demo", "a" * 32)

    def backend(self, upstream=None):
        return lb.Backend(upstream or FakeUpstream(), self.root / "audio",
                          {"question": "question", "story": "story"}, workspace_directory=self.workspace_root)

    def envelope(self, owner, request="one"):
        return json.dumps(dict(owner.fields(), sessionId="conversation", requestId=request,
                               context=context())).encode()

    def test_ownership_never_infers_a_mode_or_uses_paths_from_ids(self):
        for value in ({}, {"workspaceId": None, "experienceSessionId": ""},
                      {"workspaceId": "", "experienceSessionId": ""}):
            self.assertIsNone(owner_from(value))
        for value in ({"workspaceId": "judy"}, {"experienceSessionId": "a" * 32},
                      {"workspaceId": "unknown", "experienceSessionId": "a" * 32},
                      {"workspaceId": "demo", "experienceSessionId": ".."},
                      {"workspaceId": [], "experienceSessionId": "a" * 32}):
            with self.assertRaises(StorageError):
                owner_from(value)
        self.assertEqual(session_directory(self.workspace_root, self.demo),
                         self.workspace_root / "create-world" / "sessions" / ("a" * 32))
        self.assertFalse(self.workspace_root.exists())

    def test_capture_queries_and_late_old_mode_writes_stay_separate(self):
        archive = self.root / "photos"
        store = FrameStore(self.root / "latest", archive_dir=archive, workspace_directory=self.workspace_root)
        server = FrameHTTPServer(("127.0.0.1", 0), store)
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": .01}, daemon=True)
        thread.start()
        def stop():
            server.shutdown(); server.server_close(); thread.join(timeout=2)
        self.addCleanup(stop)
        def read(path):
            connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=2)
            try:
                connection.request("GET", path)
                result = connection.getresponse()
                return result.status, json.loads(result.read())
            finally:
                connection.close()
        for owner, frame in ((self.judy, "same"), (self.demo, "same"), (self.judy, "late")):
            metadata = dict(sample_metadata(frame), **owner.fields())
            result = store.accept(metadata, sample_image())
            self.assertEqual(result["workspaceId"], owner.workspace_id)
        status, demo = read("/latest.json?" + urlencode(self.demo.fields()))
        self.assertEqual(status, 200)
        self.assertEqual(demo["latest"]["metadata"]["frameId"], "same")
        self.assertEqual(demo["receivedCount"], 1)
        self.assertEqual(read("/latest.json")[1]["latest"], None)
        self.assertEqual(read("/latest.json?workspaceId=judy")[0], 400)
        status, index = read("/workspaces.json")
        self.assertEqual(status, 200)
        self.assertEqual({entry["workspaceId"] for entry in index["sessions"]}, {"judy", "demo"})
        self.assertTrue(all("experienceSessionId=" in entry["monitorUrl"] for entry in index["sessions"]))
        self.assertFalse(any("words" in entry or "metadata" in entry for entry in index["sessions"]))
        self.assertEqual(len(list(archive.glob("*.png"))), 0)
        for owner in (self.judy, self.demo):
            self.assertTrue(list((session_directory(archive, owner) / "captures").glob("*.png")))
        restored = FrameStore(self.root / "latest", archive_dir=archive, workspace_directory=self.workspace_root)
        self.assertEqual(restored.for_owner(self.demo).snapshot()["latest"]["metadata"]["frameId"], "same")

    def test_failed_owned_recognition_keeps_original_and_crop(self):
        archive = self.root / "photos"
        destination = session_directory(archive, self.judy) / "captures"
        def broken(frame, metadata):
            self.assertEqual(len(list(destination.glob("*.png"))), 1)
            self.assertEqual(len(list((destination / "crops").glob("*.png"))), 1)
            raise RuntimeError("synthetic recognition failure")
        store = FrameStore(self.root / "latest", broken, archive, self.workspace_root)
        metadata = dict(sample_metadata(), **self.judy.fields())
        metadata["recognitionRegion"] = {"x": 2, "y": 3, "width": 7, "height": 5,
                                         "source": "recent_hand_approach", "pixelOrigin": "top-left"}
        with self.assertRaises(RequestError) as failed:
            store.accept(metadata, sample_image())
        self.assertEqual(failed.exception.code, "recognizer_failed")
        sidecar = json.loads(next(destination.glob("*.json")).read_text())
        self.assertEqual(sidecar["metadata"]["workspaceId"], "judy")
        self.assertEqual(sidecar["targetCrop"]["status"], "saved")

    def test_history_loser_completion_routes_to_original_owner(self):
        history = WorkspaceRecognitionHistory(self.root / "legacy.jsonl", self.workspace_root)
        base = {"sessionId": "capture", "frameId": "same", "startedAtUtc": "2026-11-15T00:00:00Z"}
        for owner in (self.judy, self.demo):
            history(dict(base, **owner.fields(), selectedWord=owner.workspace_id))
        history(dict(self.judy.fields(), sessionId="capture", frameId="same", complete=True,
                     braille={"status": "recognized", "word": "FOX"}))
        self.assertTrue(history.for_owner(self.judy).snapshot()["records"][0]["complete"])
        self.assertFalse(history.for_owner(self.demo).snapshot()["records"][0]["complete"])
        self.assertEqual(history.snapshot()["records"], [])

    def test_owned_audio_and_results_survive_restart_and_legacy_eviction(self):
        backend = self.backend()
        status, judy = backend.dispatch("/question", self.envelope(self.judy), "application/json")
        self.assertEqual(status, 200)
        _, demo = backend.dispatch("/question", self.envelope(self.demo), "application/json")
        self.assertNotEqual(judy["audioPath"], demo["audioPath"])
        backend.audio.maximum_files = 1
        backend.audio.write(wav_bytes()); backend.audio.write(wav_bytes())
        replacement = FakeUpstream()
        restarted = self.backend(replacement)
        status, replay = restarted.dispatch("/question", self.envelope(self.judy), "application/json")
        self.assertEqual((status, replay), (200, judy))
        self.assertEqual(replacement.calls, [])
        self.assertEqual(restarted.audio.read(judy["audioPath"]), wav_bytes())
        self.assertEqual(restarted.audio.read(demo["audioPath"]), wav_bytes())
        for owner in (self.judy, self.demo):
            records = list((session_directory(self.workspace_root, owner) / "requests").glob("*.json"))
            self.assertEqual(len(records), 1)
            self.assertEqual(json.loads(records[0].read_text())["status"], "completed")

    def test_interrupted_generation_is_not_reissued_and_transcription_retains_no_wav(self):
        backend = self.backend()
        body = self.envelope(self.demo)
        digest = hashlib.sha256(b"/question\0" + body).hexdigest()
        backend.archive.begin(self.demo, "conversation", "one", digest, "question", json.loads(body))
        status, result = self.backend().dispatch("/question", body, "application/json")
        self.assertEqual((status, result["code"]), (409, "request_interrupted"))
        query = dict(self.judy.fields(), sessionId="conversation", requestId="speech")
        status, result = backend.dispatch("/transcribe?" + urlencode(query), wav_bytes(), "audio/wav")
        self.assertEqual(status, 200)
        directory = session_directory(self.workspace_root, self.judy)
        self.assertEqual(list(directory.rglob("*.wav")), [])
        record = json.loads(next((directory / "requests").glob("*.json")).read_text())
        self.assertEqual(set(record["input"]), {"audioSha256", "audioBytes"})

    def test_session_snapshots_are_monotonic_idempotent_and_mode_separate(self):
        backend = self.backend()
        def save(owner, revision, state):
            return backend.dispatch("/session-state", json.dumps(dict(owner.fields(), revision=revision, state=state)).encode(), "application/json")
        state = {"words": ["FOX"], "story": "x" * 70000}
        self.assertEqual(save(self.judy, 2, state)[0], 200)
        self.assertTrue(save(self.judy, 2, state)[1]["duplicate"])
        status, conflict = save(self.judy, 2, {"words": []})
        self.assertEqual((status, conflict["code"]), (409, "revision_conflict"))
        status, stale = save(self.judy, 1, {"words": []})
        self.assertEqual((status, stale["code"], stale["revision"], stale["storedRevision"]), (409, "stale_revision", 1, 2))
        self.assertEqual(stale["workspaceId"], "judy")
        self.assertEqual(save(self.demo, 0, {"words": ["TREE"]})[0], 200)
        self.assertEqual(self.backend().dispatch("/session-state", json.dumps(dict(self.judy.fields(), revision=2, state=state)).encode(), "application/json")[0], 200)
        saved = json.loads((session_directory(self.workspace_root, self.judy) / "session-state.json").read_text())
        self.assertEqual(saved["state"], state)


if __name__ == "__main__":
    unittest.main()
