"""Real color classification and saved Quest-frame integration; no cloud calls."""
import copy
import hashlib
import http.client
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

import cv2
import numpy as np

from camera_frame_server import FrameHTTPServer
from lego_color_adapter import SOURCE
from physical_story_receiver import automatic_dashboard, build_store, dashboard
from test_camera_frame_server import multipart, sample_metadata


PROJECT = Path(__file__).resolve().parents[1]


class PhysicalStoryReceiverTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.environment = patch.dict(os.environ, {}, clear=False)
        self.environment.start()
        self.store = build_store(Path(self.temp.name))
        self.server = FrameHTTPServer(("127.0.0.1", 0), self.store)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": .01}, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join(timeout=2)
        self.environment.stop(); self.temp.cleanup()

    def request(self, method, path, body=None, content_type=None):
        client = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            client.request(method, path, body=body, headers={"Content-Type": content_type} if content_type else {})
            response = client.getresponse()
            return response.status, json.loads(response.read())
        finally: client.close()

    def upload(self, metadata, image):
        content_type, body = multipart(metadata, image)
        return self.request("POST", "/frames", body, content_type)

    def example(self, frame_id="physical-color-example"):
        image = cv2.imread(str(SOURCE / "samples" / "lego_color.jpg"))
        self.assertIsNotNone(image)
        h, w = image.shape[:2]
        metadata = sample_metadata(frame_id)
        metadata.update(imageWidth=w, imageHeight=h)
        metadata["captureTimestampUtc"] = "2026-11-14T20:20:25.8808080Z"
        metadata["recognitionRegion"] = {"x": 0, "y": 0, "width": w, "height": h,
            "pixelOrigin": "top-left", "source": "recent_hand_approach"}
        metadata["handApproach"] = {"hand": "left", "positionWorld": {"x": 0, "y": .75, "z": .4},
            "headPositionWorld": {"x": 0, "y": 1.2, "z": 0}, "sampleMonotonicSeconds": 10,
            "liftStartedMonotonicSeconds": 10.1, "confirmedMonotonicSeconds": 10.25,
            "signedGapMeters": .02, "surfaceHeightMeters": .73, "headToPointDistanceMeters": .6}
        # Keep the original JPEG bytes; do not introduce an extra recompression.
        return metadata, (SOURCE / "samples" / "lego_color.jpg").read_bytes()

    def selected_faces(self, metadata):
        layout = json.loads((SOURCE / "layouts" / "lego_color.json").read_text())
        dictionary = json.loads((SOURCE / "color_dictionary.json").read_text())
        indexes = {entry["letter"]: entry["source_index"] for entry in dictionary["entries"]}
        return {"sessionId": metadata["sessionId"], "frameId": metadata["frameId"], "action": "recognize",
            "dictionarySource": "repository_example",
            "cells": [{"quad": layout["cells"][indexes[letter]]["quad"]} for letter in "OFX"]}

    def test_actual_quest_capture_is_saved_and_waits_for_regions(self):
        base = PROJECT / "Documentation/DeviceTests/table-depth/frame-8c7246df95fe45b29997e93a6054587e"
        self.assertTrue(base.with_suffix(".jpg").is_file(), "Saved physical Quest evidence is required")
        metadata = json.loads(base.with_suffix(".json").read_text())["metadata"]
        status, reply = self.upload(metadata, base.with_suffix(".jpg").read_bytes())
        self.assertEqual(status, 200, reply)
        self.assertEqual(reply["status"], "needs_regions")
        self.assertFalse(reply["recognition"]["automaticBlockDetection"])
        self.assertFalse(reply["recognition"]["recognitionPerformed"])
        roi = cv2.imread(str(Path(self.temp.name) / "latest-roi.jpg"))
        region = metadata["recognitionRegion"]
        self.assertEqual(roi.shape[:2], (region["height"], region["width"]))
        ready = self.store.color_result(metadata["sessionId"], metadata["frameId"])
        self.assertFalse(ready["ready"])
        self.assertEqual(ready["status"], "waiting_for_regions")

    def test_original_algorithm_keeps_selected_ofx_order_without_autocorrection(self):
        metadata, image = self.example()
        status, reply = self.upload(metadata, image); self.assertEqual(status, 200, reply)
        selection = self.selected_faces(metadata)
        # Expected/typed labels are not algorithm inputs.
        for cell in selection["cells"]: cell["letter"] = "Z"
        status, reply = self.request("POST", "/color/analyze", json.dumps(selection), "application/json")
        self.assertEqual(status, 200, reply)
        result = reply["recognition"]
        self.assertEqual(result["word"], "OFX")
        self.assertEqual(result["status"], "recognized")
        self.assertEqual(result["dictionarySource"], "repository_example")
        self.assertEqual(result["orderSource"], "manual_selection_order")
        ready = self.store.color_result(metadata["sessionId"], metadata["frameId"])
        self.assertTrue(ready["ready"])
        self.assertEqual("".join(d["letter"] for d in ready["detections"]), "OFX")
        self.assertTrue(all(d["accepted"] for d in ready["detections"]))

    def test_new_capture_invalidates_old_manual_regions_and_calibration_is_blocked(self):
        metadata, image = self.example(); self.upload(metadata, image)
        selection = self.selected_faces(metadata)
        selection["action"] = "calibrate"
        status, reply = self.request("POST", "/color/analyze", json.dumps(selection), "application/json")
        self.assertEqual(status, 403); self.assertEqual(reply["error"], "calibration_disabled")
        self.assertFalse((Path(self.temp.name) / "color-calibration.json").exists())
        selection["action"] = "recognize"
        newer = copy.deepcopy(metadata); newer["frameId"] = "newer-frame"; self.upload(newer, image)
        status, reply = self.request("POST", "/color/analyze", json.dumps(selection), "application/json")
        self.assertEqual(status, 409); self.assertEqual(reply["error"], "stale_frame")

    def test_unknown_colors_produce_no_word_and_never_promote_nearest_candidates(self):
        metadata, encoded = self.example("unknown-colors")
        selection = self.selected_faces(metadata)
        image = cv2.imdecode(np.frombuffer(encoded, np.uint8), cv2.IMREAD_COLOR)
        for cell in selection["cells"]:
            cv2.fillConvexPoly(image, np.asarray(cell["quad"], dtype=np.int32), (255, 0, 255))
        ok, changed = cv2.imencode(".png", image); self.assertTrue(ok)
        self.assertEqual(self.upload(metadata, changed.tobytes())[0], 200)
        status, reply = self.request("POST", "/color/analyze", json.dumps(selection), "application/json")
        self.assertEqual(status, 200, reply)
        self.assertEqual(reply["recognition"]["status"], "needs_review")
        self.assertIsNone(reply["recognition"]["word"])
        response = self.store.color_result(metadata["sessionId"], metadata["frameId"])
        self.assertTrue(response["ready"], "Ready means analyzed; Unity must also require recognized status and all letters accepted")
        self.assertEqual(response["status"], "needs_review")
        self.assertTrue(all(d["letter"] == "?" and not d["accepted"] for d in response["detections"]))

    def test_existing_saved_references_are_loaded_read_only_and_not_the_default_choice(self):
        path = PROJECT / "runtime/camera-captures/color-calibration.json"
        if not path.exists(): self.skipTest("No existing approved camera references")
        before = hashlib.sha256(path.read_bytes()).hexdigest()
        store = build_store(Path(self.temp.name), path)
        self.assertEqual(store.color_session.source, "quest_color_calibration")
        metadata, image = self.example(); store.accept(metadata, image)
        selection = self.selected_faces(metadata); selection.pop("dictionarySource")
        result = store.analyze_regions(selection)["recognition"]
        self.assertEqual(result["dictionarySource"], "repository_example")
        self.assertEqual(result["word"], "OFX")
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), before)

    def test_dashboard_keeps_full_image_coordinates_and_discloses_manual_selection(self):
        page = dashboard(False).decode()
        self.assertIn("Block boundaries are selected manually", page)
        self.assertIn("suggestedROI=meta.recognitionRegion", page)
        self.assertIn("ctx.strokeRect(roi.x,roi.y,roi.width,roi.height)", page)
        self.assertIn('value="quest_color_calibration" disabled', page)
        self.assertIn("action,cells,dictionarySource", page)


class AutomaticPhysicalStoryTests(unittest.TestCase):
    """Exercise the actual upload/result contract without a manual analyze request."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.environment = patch.dict(os.environ, {}, clear=False)
        self.environment.start()
        references = PROJECT / "Python/test_fixtures/quest_color_references_20261114.json"
        self.store = build_store(Path(self.temp.name), references, automatic=True)
        self.server = FrameHTTPServer(("127.0.0.1", 0), self.store)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": .01}, daemon=True)
        self.thread.start()

    tearDown = PhysicalStoryReceiverTests.tearDown
    request = PhysicalStoryReceiverTests.request
    upload = PhysicalStoryReceiverTests.upload

    def captured(self, name):
        path = PROJECT / "Documentation/DeviceTests/physical-story/three-word-live-20261114" / name
        return json.loads((path / "latest.json").read_text())["metadata"], (path / "latest.jpg").read_bytes()

    def result(self, metadata):
        return self.request("GET", "/color/result?sessionId=" + metadata["sessionId"] + "&frameId=" + metadata["frameId"])

    def test_upload_immediately_exposes_word_and_center_then_rejects_stale_frame(self):
        first = None
        for name, expected in (("word-1", "FOX"), ("word-2", "JUDY")):
            metadata, image = self.captured(name)
            status, reply = self.upload(metadata, image)
            self.assertEqual(status, 200, reply)
            self.assertEqual(reply["status"], "recognized", reply)
            status, result = self.result(metadata)
            self.assertEqual(status, 200, result)
            self.assertTrue(result["ready"])
            self.assertTrue(result["automaticBlockDetection"])
            self.assertEqual("".join(d["letter"] for d in result["detections"]), expected)
            self.assertTrue(all(d["accepted"] for d in result["detections"]))
            self.assertEqual(result["groupCenterPixel"], reply["recognition"]["groupCenterPixel"])
            self.assertGreater(result["proposalTimingMs"], 0)
            if first is None: first = metadata
        status, result = self.result(first)
        self.assertEqual(status, 409)
        self.assertEqual(result["error"], "stale_frame")

    def test_timer_without_valid_roi_saves_photo_without_running_recognition(self):
        references = PROJECT / "Python/test_fixtures/quest_color_references_20261114.json"
        for region in (None, {"x": -10, "y": 0, "width": 100, "height": 100,
                              "pixelOrigin": "top-left", "source": "recent_hand_approach"}):
            with self.subTest(region=region), patch("auto_color_regions.recognize") as classify, \
                    patch("physical_story_receiver.save_hand_region") as crop:
                store = build_store(Path(self.temp.name), references, automatic=True)
                metadata, image = self.captured("word-1")
                metadata["trigger"]["gesture"] = "double_pinch_timer"
                metadata["recognitionRegion"] = region
                metadata["recognitionRegionIssue"] = "Saved target is outside the captured view"
                reply = store.accept(metadata, image)
                self.assertTrue(reply["accepted"])
                self.assertEqual(reply["status"], "retry_needed")
                self.assertIsNone(reply["recognition"]["word"])
                self.assertFalse(reply["recognition"]["recognitionPerformed"])
                self.assertEqual((Path(self.temp.name) / "latest.jpg").read_bytes(), image)
                self.assertEqual(store.received_count, 1)
                self.assertFalse((Path(self.temp.name) / "latest-roi.jpg").exists())
                ready = store.color_result(metadata["sessionId"], metadata["frameId"])
                self.assertTrue(ready["ready"])
                self.assertEqual(ready["status"], "retry_needed")
                self.assertEqual(ready["detections"], [])
                self.assertIsNone(ready["groupCenterPixel"])
                crop.assert_not_called()
                classify.assert_not_called()

    def test_ordinary_capture_without_roi_is_not_silently_accepted(self):
        metadata, image = self.captured("word-1")
        metadata["recognitionRegion"] = None
        status, reply = self.upload(metadata, image)
        self.assertEqual(status, 502)
        self.assertEqual(reply["error"], "recognizer_failed")
        self.assertEqual(self.store.received_count, 0)
        self.assertFalse((Path(self.temp.name) / "latest.jpg").exists())

    def test_timer_with_valid_roi_preserves_existing_color_recognition(self):
        metadata, image = self.captured("word-1")
        metadata["trigger"]["gesture"] = "double_pinch_timer"
        status, reply = self.upload(metadata, image)
        self.assertEqual(status, 200, reply)
        self.assertTrue(reply["accepted"])
        self.assertEqual(reply["status"], "recognized")
        self.assertEqual(reply["recognition"]["word"], "FOX")

    def test_blank_capture_returns_ready_retry_without_any_letter(self):
        metadata, encoded = self.captured("word-1")
        image = cv2.imdecode(np.frombuffer(encoded, np.uint8), cv2.IMREAD_COLOR)
        region = metadata["recognitionRegion"]
        image[region["y"]:region["y"]+region["height"], region["x"]:region["x"]+region["width"]] = 185
        ok, changed = cv2.imencode(".jpg", image); self.assertTrue(ok)
        status, reply = self.upload(metadata, changed.tobytes())
        self.assertEqual(status, 200, reply)
        status, result = self.result(metadata)
        self.assertEqual(status, 200, result)
        self.assertTrue(result["ready"])
        self.assertEqual(result["status"], "retry_needed")
        self.assertEqual(result["detections"], [])
        self.assertIsNone(result["groupCenterPixel"])

    def test_automatic_dashboard_has_no_manual_recognition_controls(self):
        page = automatic_dashboard().decode()
        self.assertNotIn("pointerdown", page)
        self.assertNotIn("<button", page)
        self.assertNotIn("/color/analyze", page)
        self.assertIn("groupCenterPixel", page)
        self.assertIn("No corner selection is needed", page)


if __name__ == "__main__": unittest.main()
