"""Contract tests using synthetic images; no camera, microphone, or API calls."""

import copy
import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest

import cv2
import numpy as np

from camera_frame_server import FrameHTTPServer, FrameStore, MAX_REQUEST_BYTES


def sample_metadata(frame_id="frame-001"):
    return {
        "schemaVersion": 1, "sessionId": "test-session", "frameId": frame_id,
        "frameRevision": 1, "cameraEye": "left", "imageWidth": 16, "imageHeight": 12,
        "pixelOrigin": "top-left", "orientation": "upright",
        "captureTimestampUtc": "2026-11-14T12:00:00.000Z",
        "cameraPositionWorld": {"x": 0, "y": 1.5, "z": 0},
        "cameraRotationWorld": {"x": 0, "y": 0, "z": 0, "w": 1},
        "intrinsics": {"focalLength": {"x": 12, "y": 12}, "principalPoint": {"x": 8, "y": 6},
                       "sensorResolution": {"x": 16, "y": 12}},
        "board": {"position": {"x": 0, "y": 0, "z": 1},
                  "rotation": {"x": 0, "y": 0, "z": 0, "w": 1},
                  "width": 0.6, "height": 0.4, "blockHeight": 0.02},
        "trigger": {"type": "hands_off", "clearSeconds": 0.8},
    }


def sample_image(extension=".png", value=80):
    image = np.zeros((12, 16, 3), dtype=np.uint8)
    image[:, :] = [value, 30, 200]
    success, encoded = cv2.imencode(extension, image)
    assert success
    return encoded.tobytes()


def multipart(metadata, image, duplicate_image=False):
    boundary = "quest-frame-test-boundary"
    fields = [("metadata", "application/json", json.dumps(metadata).encode()),
              ("image", "image/png", image)]
    if duplicate_image:
        fields.append(fields[-1])
    body = b""
    for name, content_type, data in fields:
        body += ("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n" +
                 "Content-Type: " + content_type + "\r\n\r\n").encode() + data + b"\r\n"
    body += ("--" + boundary + "--\r\n").encode()
    return "multipart/form-data; boundary=" + boundary, body


class FrameReceiverTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.store = FrameStore(Path(self.temporary.name))
        self.server = FrameHTTPServer(("127.0.0.1", 0), self.store)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.temporary.cleanup()

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def post(self, metadata=None, image=None, duplicate_image=False):
        content_type, body = multipart(metadata or sample_metadata(), image or sample_image(), duplicate_image)
        status, _, response = self.request("POST", "/frames", body, {"Content-Type": content_type})
        return status, json.loads(response)

    def test_unity_seven_digit_timestamp_is_accepted_and_preserved(self):
        for index, timestamp in enumerate(("2026-11-14T20:16:28.1234567Z",
                                           "2026-11-14T20:16:28.1234567+00:00")):
            with self.subTest(timestamp=timestamp):
                metadata = sample_metadata("unity-timestamp-" + str(index))
                metadata["captureTimestampUtc"] = timestamp
                status, response = self.post(metadata)
                self.assertEqual(status, 200, response)
                self.assertTrue(response["accepted"])
                self.assertEqual(self.store.snapshot()["latest"]["metadata"]["captureTimestampUtc"], timestamp)

    def test_iso_fraction_precision_is_portable_without_losing_stored_value(self):
        for index, fraction in enumerate(("", ".1", ".12", ".123", ".1234", ".12345", ".123456", ".123456789")):
            with self.subTest(fraction=fraction):
                metadata = sample_metadata("timestamp-precision-" + str(index))
                timestamp = "2026-11-14T12:00:00" + fraction + "-08:00"
                metadata["captureTimestampUtc"] = timestamp
                status, response = self.post(metadata)
                self.assertEqual(status, 200, response)
                self.assertEqual(self.store.snapshot()["latest"]["metadata"]["captureTimestampUtc"], timestamp)

    def test_timestamp_compatibility_does_not_accept_missing_zone_or_bad_dates(self):
        invalid = (None, 123, "2026-11-14T12:00:00.1234567", "2026-02-30T12:00:00.1234567Z",
                   "2026-11-14T25:00:00.1234567Z", "2026-11-14T12:00:00.1234567+24:00",
                   "2026-11-14T12:00:00.1234567oopsZ", "2026-11-14T12:00:00.1234567890Z",
                   "2026-11-14T12:00:00.1234567Z\n")
        for timestamp in invalid:
            with self.subTest(timestamp=timestamp):
                metadata = sample_metadata()
                metadata["captureTimestampUtc"] = timestamp
                status, response = self.post(metadata)
                self.assertEqual(status, 400, response)
                self.assertEqual(response["error"], "invalid_metadata")
                self.assertEqual(self.store.snapshot()["receivedCount"], 0)

    def test_one_upload_is_decoded_bgr_and_acknowledged_without_fake_recognition(self):
        seen = []
        from camera_frame_server import default_recognizer

        def hook(frame, metadata):
            seen.append((frame.copy(), metadata["frameId"]))
            return default_recognizer(frame, metadata)

        self.store.recognizer = hook
        status, result = self.post()
        self.assertEqual(status, 200)
        self.assertTrue(result["accepted"])
        self.assertEqual(result["status"], "capture_only")
        self.assertEqual(result["recognition"]["detections"], [])
        self.assertEqual(result["receivedCount"], 1)
        self.assertEqual(seen[0][0].shape, (12, 16, 3))
        self.assertEqual(seen[0][0].dtype, np.uint8)
        np.testing.assert_array_equal(seen[0][0][0, 0], [80, 30, 200])
        status, headers, image = self.request("GET", "/latest.jpg")
        self.assertEqual(status, 200)
        self.assertEqual(headers["Content-Type"], "image/png")
        self.assertEqual(image, sample_image())

    def test_retry_is_idempotent_even_when_json_key_order_changes(self):
        _, initial = self.post()
        reordered = dict(reversed(list(sample_metadata().items())))
        status, retry = self.post(reordered)
        self.assertEqual(status, 200)
        self.assertTrue(retry["duplicate"])
        self.assertEqual(retry["receivedCount"], initial["receivedCount"])
        self.assertEqual(self.store.received_count, 1)

    def test_manual_capture_is_accepted_and_unknown_trigger_is_rejected(self):
        metadata = sample_metadata()
        metadata["trigger"] = {"type": "manual", "clearSeconds": 0}
        self.assertEqual(self.post(metadata)[0], 200)
        metadata["trigger"]["type"] = "invented"
        self.assertEqual(self.post(metadata)[0], 400)

    def test_bound_image_url_rejects_stale_frame(self):
        self.post()
        self.post(sample_metadata("frame-002"))
        status, _, body = self.request("GET", "/latest.jpg?sessionId=test-session&frameId=frame-001")
        self.assertEqual(status, 409)
        self.assertEqual(json.loads(body)["error"], "stale_frame")
        self.assertEqual(self.request("GET", "/latest.jpg?sessionId=test-session&frameId=frame-002")[0], 200)

    def test_reused_frame_id_with_different_content_is_rejected(self):
        self.post()
        status, response = self.post(image=sample_image(value=90))
        self.assertEqual(status, 409)
        self.assertEqual(response["error"], "frame_id_conflict")
        self.assertEqual(self.store.received_count, 1)

    def test_new_frame_replaces_image_and_retains_no_image_archive(self):
        self.post(image=sample_image(".jpg"))
        self.assertTrue((self.store.output_dir / "latest.jpg").exists())
        status, result = self.post(sample_metadata("frame-002"))
        self.assertEqual(status, 200)
        self.assertEqual(result["receivedCount"], 2)
        self.assertEqual(sorted(p.name for p in self.store.output_dir.iterdir()), [".gitignore", "latest.json", "latest.png"])
        persisted = json.loads((self.store.output_dir / "latest.json").read_text())
        self.assertEqual(persisted["metadata"]["frameId"], "frame-002")

    def test_image_size_and_capture_metadata_cannot_silently_disagree(self):
        metadata = sample_metadata()
        metadata["imageWidth"] = 15
        status, response = self.post(metadata)
        self.assertEqual(status, 400)
        self.assertEqual(response["error"], "dimension_mismatch")
        self.assertEqual(self.store.received_count, 0)

    def test_nonfinite_geometry_is_rejected_before_recognition(self):
        metadata = sample_metadata()
        metadata["cameraPositionWorld"]["x"] = float("nan")
        status, result = self.post(metadata)
        self.assertEqual(status, 400)
        self.assertFalse(result["accepted"])
        self.assertEqual(self.store.received_count, 0)

    def test_oversized_request_is_rejected_without_reading_a_body(self):
        status, _, response = self.request("POST", "/frames", headers={"Content-Length": str(MAX_REQUEST_BYTES + 1)})
        self.assertEqual(status, 413)
        self.assertEqual(json.loads(response)["error"], "request_too_large")

    def test_duplicate_multipart_field_is_rejected(self):
        status, response = self.post(duplicate_image=True)
        self.assertEqual(status, 400)
        self.assertEqual(response["error"], "invalid_multipart")

    def test_malformed_image_is_rejected(self):
        status, response = self.post(image=b"\x89PNG\r\n\x1a\nnot-an-image")
        self.assertEqual(status, 400)
        self.assertEqual(response["error"], "invalid_image")

    def test_hook_failure_is_reported_without_accepting_or_saving_the_frame(self):
        def broken_hook(frame, metadata):
            raise RuntimeError("Example recognition error")

        self.store.recognizer = broken_hook
        status, response = self.post()
        self.assertEqual(status, 502)
        self.assertEqual(response["error"], "recognizer_failed")
        self.assertEqual(self.store.received_count, 0)
        self.assertFalse((self.store.output_dir / "latest.json").exists())

    def test_dashboard_reports_waiting_without_loading_old_private_data(self):
        status, _, response = self.request("GET", "/latest.json")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(response), {"receivedCount": 0, "latest": None})
        status, _, response = self.request("GET", "/health")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(response)["recognizer"], "capture_only")

    def enable_color(self):
        from lego_color_adapter import ColorSession, recognize
        self.store.recognizer = recognize
        self.store.color_session = ColorSession(self.store.output_dir / "color-calibration.json")

    def color_capture(self, identifier, colors):
        metadata = sample_metadata(identifier)
        metadata.update(imageWidth=240, imageHeight=200)
        image = np.full((200, 240, 3), 70, dtype=np.uint8)
        for index, color in enumerate(colors):
            image[20:180, 10+index*120:90+index*120] = color
        return self.post(metadata, cv2.imencode(".png", image)[1].tobytes())

    @staticmethod
    def color_regions(identifier, action="recognize"):
        return {"sessionId": "test-session", "frameId": identifier, "action": action,
                "dictionarySource": "quest_color_calibration",
                "cells": [{"quad": [[10+i*120,20],[89+i*120,20],[89+i*120,179],[10+i*120,179]], "letter": label}
                          for i, label in enumerate(("F", "X"))]}

    def get_color_result(self, identifier):
        status, _, response = self.request("GET", "/color/result?sessionId=test-session&frameId="+identifier)
        return status, json.loads(response)

    def test_overlay_result_waits_for_regions_and_rejects_old_capture(self):
        status, result = self.get_color_result("frame-001")
        self.assertEqual(status, 409)
        self.assertFalse(result["ready"])
        self.post()
        status, result = self.get_color_result("frame-001")
        self.assertEqual(status, 200)
        self.assertFalse(result["ready"])
        self.assertEqual(result["detections"], [])
        self.assertEqual(result["analysisRevision"], 0)
        self.assertEqual(result["imageWidth"], 16)
        self.post(sample_metadata("frame-002"))
        self.assertEqual(self.get_color_result("frame-001")[0], 409)
        self.assertEqual(self.request("GET", "/color/result?frameId=frame-002")[0], 409)

    def test_overlay_result_contains_real_recognized_letters_in_selection_order(self):
        self.enable_color()
        red, yellow = [20,30,220], [10,220,240]
        self.color_capture("references", [red, yellow])
        self.store.analyze_regions(self.color_regions("references", "calibrate"))
        self.color_capture("new-view", [yellow, red])
        request = self.color_regions("new-view")
        for cell in request["cells"]:
            del cell["letter"]
        status, _, body = self.request("POST", "/color/analyze", json.dumps(request), {"Content-Type":"application/json"})
        self.assertEqual(status, 200)
        analysis = json.loads(body)
        status, result = self.get_color_result("new-view")
        self.assertEqual(status, 200)
        self.assertTrue(result["ready"])
        self.assertEqual([d["letter"] for d in result["detections"]], ["X", "F"])
        self.assertTrue(all(d["accepted"] for d in result["detections"]))
        self.assertEqual(result["detections"][0]["quad"][0], {"x":10.0,"y":20.0})
        self.assertEqual(result["coordinateSystem"], "image_pixels_top_left")
        self.assertEqual(result["analysisRevision"], analysis["analysisRevision"])
        self.assertEqual(self.get_color_result("new-view")[1], result)

    def test_calibration_is_not_an_overlay_and_unknown_colors_are_question_marks(self):
        self.enable_color()
        red, yellow = [20,30,220], [10,220,240]
        self.color_capture("references", [red, yellow])
        self.store.analyze_regions(self.color_regions("references", "calibrate"))
        self.assertFalse(self.get_color_result("references")[1]["ready"])
        self.store.analyze_regions(self.color_regions("references"))
        calibration_check = self.get_color_result("references")[1]
        self.assertEqual(calibration_check["status"], "calibration_check")
        self.assertFalse(calibration_check["ready"])
        self.assertEqual(calibration_check["detections"], [])
        self.color_capture("unknown", [[230,40,20], red])
        self.store.analyze_regions(self.color_regions("unknown"))
        result = self.get_color_result("unknown")[1]
        self.assertTrue(result["ready"])
        self.assertEqual(result["status"], "needs_review")
        self.assertEqual([d["letter"] for d in result["detections"]], ["?", "F"])
        self.assertFalse(result["detections"][0]["accepted"])

    def test_http_calibration_is_disabled_and_cannot_change_references(self):
        self.enable_color()
        self.color_capture("references", [[20,30,220], [10,220,240]])
        status, _, body = self.request("POST", "/color/analyze", json.dumps(self.color_regions("references", "calibrate")),
                                       {"Content-Type":"application/json"})
        self.assertEqual(status, 403)
        self.assertEqual(json.loads(body)["error"], "calibration_disabled")
        self.assertFalse(self.store.color_session.path.exists())
        status, _, html = self.request("GET", "/")
        self.assertIn(b'<option value="repository_example">Original repository</option>', html)
        self.assertNotIn(b'id="calibrate"', html)
        self.assertNotIn(b'id="letter"', html)


if __name__ == "__main__":
    unittest.main()
