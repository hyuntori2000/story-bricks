"""Offline race, late-result and restart contracts; no external service calls."""
from copy import deepcopy
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import numpy as np

from parallel_recognition import ParallelRecognizer, GeometryError, convert, read_ocr
from recognition_history import RecognitionHistory
from camera_frame_server import FrameHTTPServer, FrameStore
from test_camera_frame_server import sample_metadata


LAYOUT = {"version": 1, "image_size": [100, 100], "cells": [
    {"row_id": "word", "slot_index": 0, "quad": [[10, 10], [40, 10], [40, 80], [10, 80]]}]}
INFO = {"originInFullImage": {"x": 120, "y": 50}, "fullImageSize": [400, 300]}


def payload(word="A", accepted=True):
    return {"cells": [{"row_id": "word", "slot_index": 0, "value": word,
                       "accepted": accepted, "reason": "ok" if accepted else "uncertain_dots",
                       "face_polygon": LAYOUT["cells"][0]["quad"]}]}


def preparer(frame, metadata, crop):
    return crop, deepcopy(LAYOUT), deepcopy(INFO)


class ParallelTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "history.jsonl"
        self.history = RecognitionHistory(self.path)
        self.image = np.zeros((100, 100, 3), np.uint8)

    def tearDown(self):
        self.temp.cleanup()

    def capture(self, reader, frame="one"):
        return reader(self.image, {"sessionId": "test", "frameId": frame}, unity_crop_bgr=self.image)

    def race(self, fast):
        release, late_started, completed = threading.Event(), threading.Event(), threading.Event()
        seen = []
        slow = "braille" if fast == "ocr" else "ocr"
        def slow_read(crop, layout):
            self.assertIs(crop, self.image)
            late_started.set()
            release.wait(3)
            return payload("B")
        def fast_read(crop, layout):
            self.assertIs(crop, self.image)
            late_started.wait(2)
            return payload("A")
        reader = ParallelRecognizer(self.history, preparer=preparer, readers={fast: fast_read, slow: slow_read})
        def capture():
            seen.append(self.capture(reader)); completed.set()
        thread = threading.Thread(target=capture)
        try:
            thread.start()
            self.assertTrue(completed.wait(2), "Winner must return while loser is still blocked")
            self.assertEqual(seen[0]["selectedSource"], fast)
            self.assertEqual(seen[0]["word"], "A")
            self.assertFalse(self.history.snapshot()["records"][0]["complete"])
            # Snapshot returned to Unity must not mutate when late work finishes.
            release.set(); thread.join(2); reader.close()
            final = self.history.snapshot()["records"][0]
            self.assertTrue(final["complete"])
            self.assertTrue(final["disagreement"])
            self.assertEqual(final[slow]["word"], "B")
            self.assertGreater(final[slow]["elapsedMs"], final[fast]["elapsedMs"])
            self.assertFalse(seen[0]["parallel"]["complete"])
            self.assertEqual(RecognitionHistory(self.path).snapshot()["records"], self.history.snapshot()["records"])
            self.assertEqual(seen[0]["cells"][0]["quad"][0], [130.0, 60.0])
        finally:
            release.set(); thread.join(3); reader.close()

    def test_ocr_first_returns_before_braille_and_records_later_disagreement(self):
        self.race("ocr")

    def test_braille_first_is_not_overridden_by_later_ocr(self):
        self.race("braille")

    def test_fast_failure_then_success_and_separate_frames(self):
        failed = threading.Event()
        def ocr(crop, layout):
            failed.set(); return payload("?", False)
        def braille(crop, layout):
            failed.wait(2); return payload("A")
        reader = ParallelRecognizer(self.history, preparer=preparer, readers={"ocr": ocr, "braille": braille})
        try:
            for frame in ("one", "two"):
                result = self.capture(reader, frame)
                self.assertEqual(result["selectedSource"], "braille")
                self.assertEqual(result["parallel"]["frameId"], frame)
        finally:
            reader.close()
        self.assertEqual({r["frameId"] for r in self.history.snapshot()["records"]}, {"one", "two"})

    def test_both_failure_and_engine_exception(self):
        def fail(crop, layout):
            raise RuntimeError("test reader error")
        reader = ParallelRecognizer(self.history, preparer=preparer,
                                    readers={"ocr": fail, "braille": lambda c, l: payload("?", False)})
        try:
            result = self.capture(reader)
            self.assertEqual(result["status"], "retry_needed")
            self.assertIsNone(result["word"])
            self.assertTrue(result["parallel"]["complete"])
            self.assertEqual(result["parallel"]["ocr"]["status"], "error")
        finally:
            reader.close()

    def test_exception_does_not_prevent_other_reader_success(self):
        def fail(crop, layout):
            raise RuntimeError("test reader error")
        reader = ParallelRecognizer(self.history, preparer=preparer,
                                    readers={"ocr": fail, "braille": lambda c, l: payload("A")})
        try:
            self.assertEqual(self.capture(reader)["word"], "A")
        finally:
            reader.close()

    def test_geometry_failure_is_logged_without_running_readers(self):
        def no_geometry(*args):
            raise GeometryError("uncertain_faces")
        def must_not_run(*args):
            raise AssertionError("No geometry")
        reader = ParallelRecognizer(self.history, preparer=no_geometry,
                                    readers={"ocr": must_not_run, "braille": must_not_run})
        try:
            result = self.capture(reader)
            self.assertEqual(result["status"], "retry_needed")
            self.assertFalse(result["recognitionPerformed"])
            self.assertEqual(result["parallel"]["ocr"]["status"], "not_run")
        finally:
            reader.close()

    def test_ocr_can_read_labels_when_full_face_geometry_fails(self):
        recognized = {"status": "recognized", "result": payload("A")}
        region = {"readingOrientation": "labels_toward_viewer", "readingQuad": [
            {"x": 0, "y": 0}, {"x": 99, "y": 0}, {"x": 99, "y": 99}, {"x": 0, "y": 99}]}
        with patch('parallel_recognition.locate_layout', side_effect=GeometryError('unresolved_face_boundaries')), \
             patch('parallel_recognition._read_crop', return_value=recognized):
            raw = read_ocr(self.image, {"region": region})
            result = convert(raw, {"region": region}, INFO, "ocr")
        self.assertEqual(result["word"], "A")
        self.assertEqual(result["readerPath"], "automatic_labels")
        self.assertEqual(result["geometryIssue"], "unresolved_face_boundaries")

    def test_both_winners_have_the_existing_unity_detection_contract(self):
        store = FrameStore(Path(self.temp.name) / 'frames')
        metadata = sample_metadata()
        for source in ('ocr', 'braille'):
            result = convert(payload('A'), LAYOUT, INFO, source)
            store.latest = {'metadata': metadata, 'colorAnalysis': {'recognition': result, 'analysisRevision': 1}}
            response = store.color_result(metadata['sessionId'], metadata['frameId'])
            self.assertTrue(response['ready'])
            self.assertEqual(response['detections'][0]['letter'], 'A')
            self.assertTrue(response['detections'][0]['accepted'])

    def test_period_is_a_positive_punctuation_detection_not_an_unknown_word(self):
        for source in ('ocr', 'braille'):
            result = convert(payload('.'), LAYOUT, INFO, source)
            self.assertEqual(result['status'], 'recognized')
            self.assertEqual(result['word'], '.')
            self.assertEqual(result['result']['cells'][0]['kind'], 'punctuation')
            self.assertEqual(result['result']['cells'][0]['punctuation_candidate'], '.')
            self.assertEqual(convert(payload('?', False), LAYOUT, INFO, source)['status'], 'retry_needed')

    def test_history_endpoint_remains_available_during_capture_lock(self):
        import json
        import urllib.request
        store = FrameStore(Path(self.temp.name) / 'frames')
        store.recognition_history = self.history
        server = FrameHTTPServer(('127.0.0.1', 0), store)
        thread = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': .01}, daemon=True)
        thread.start()
        try:
            with store.lock:
                with urllib.request.urlopen(f'http://127.0.0.1:{server.server_port}/recognition-history.json', timeout=1) as response:
                    self.assertEqual(json.load(response)['records'], [])
        finally:
            server.shutdown(); server.server_close(); thread.join(2)


class HistoryTests(unittest.TestCase):
    def test_partial_late_updates_restore_without_reordering_or_losing_fields(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "history.jsonl"
            history = RecognitionHistory(path, max_records=2)
            for i in range(3):
                history({"sessionId": "s", "frameId": str(i), "startedAtUtc": f"2026-11-14T12:00:0{i}+00:00",
                         "ocr": {"status": "recognized", "word": "A", "elapsedMs": 10}})
            history({"sessionId": "s", "frameId": "0", "braille": {"status": "recognized", "word": "B"}, "complete": True})
            history({"sessionId": "s", "frameId": "2", "braille": {"status": "retry_needed"}, "complete": True})
            restored = RecognitionHistory(path, max_records=2).snapshot()["records"]
            self.assertEqual([r["frameId"] for r in restored], ["2", "1"])
            self.assertEqual(restored[0]["ocr"]["word"], "A")
            self.assertEqual(restored[0]["braille"]["status"], "retry_needed")
            self.assertEqual(len(path.read_text().splitlines()), 5)


if __name__ == "__main__":
    unittest.main()
