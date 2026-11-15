"""Durable original-photo archive contracts; synthetic images, no device or APIs."""

import hashlib
import http.client
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

import camera_frame_server
from camera_frame_server import FrameHTTPServer, FrameStore, RequestError, default_recognizer
from physical_story_receiver import DEFAULT_OUTPUT, build_store, configured_archive_dir
from test_camera_frame_server import sample_image, sample_metadata


class CaptureArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.archive = self.root / "capturedimage2026"
        self.store = FrameStore(self.root / "latest", archive_dir=self.archive)

    def images(self):
        return sorted(list(self.archive.glob("*.jpg")) + list(self.archive.glob("*.png")))

    def assert_archived(self, reply, metadata, data):
        image = self.archive / reply["archive"]["imageFile"]
        sidecar = self.archive / reply["archive"]["metadataFile"]
        self.assertEqual(image.read_bytes(), data)
        record = json.loads(sidecar.read_text())
        self.assertEqual(record["metadata"], metadata)
        self.assertEqual(record["imageFile"], image.name)
        self.assertEqual(record["imageSha256"], hashlib.sha256(data).hexdigest())
        return image, sidecar

    def test_multiple_photos_keep_original_bytes_while_preview_advances(self):
        first_meta, second_meta = sample_metadata("first"), sample_metadata("second")
        first_data, second_data = sample_image(".jpg"), sample_image(".png", value=100)
        first = self.store.accept(first_meta, first_data)
        second = self.store.accept(second_meta, second_data)
        self.assert_archived(first, first_meta, first_data)
        self.assert_archived(second, second_meta, second_data)
        self.assertEqual(len(self.images()), 2)
        self.assertEqual(len(list(self.archive.glob("*.json"))), 2)
        self.assertFalse((self.store.output_dir / "latest.jpg").exists())
        self.assertEqual((self.store.output_dir / "latest.png").read_bytes(), second_data)
        self.assertEqual(self.store.snapshot()["latest"]["metadata"]["frameId"], "second")

    def test_retry_and_receiver_restart_reuse_archive_without_overwriting(self):
        metadata, data = sample_metadata(), sample_image()
        first = self.store.accept(metadata, data)
        image, sidecar = self.assert_archived(first, metadata, data)
        before = (image.read_bytes(), sidecar.read_bytes(), image.stat().st_mtime_ns, sidecar.stat().st_mtime_ns)
        reordered = dict(reversed(list(metadata.items())))
        retry = self.store.accept(reordered, data)
        self.assertTrue(retry["duplicate"])
        self.assertEqual(retry["archive"], first["archive"])
        restarted = FrameStore(self.root / "after-restart", archive_dir=self.archive)
        next_reply = restarted.accept(reordered, data)
        self.assertEqual(next_reply["archive"], first["archive"])
        after = (image.read_bytes(), sidecar.read_bytes(), image.stat().st_mtime_ns, sidecar.stat().st_mtime_ns)
        self.assertEqual(before, after)
        self.assertEqual(len(self.images()), 1)

    def test_identical_pixels_from_distinct_captures_remain_distinct_photos(self):
        data = sample_image()
        first = self.store.accept(sample_metadata("one"), data)
        second = self.store.accept(sample_metadata("two"), data)
        self.assertNotEqual(first["archive"]["imageFile"], second["archive"]["imageFile"])
        self.assertEqual(len(self.images()), 2)

    def test_recognition_exception_keeps_photo_saved_before_hook_runs(self):
        def broken_hook(frame, metadata):
            self.assertEqual(len(self.images()), 1)
            self.assertEqual(len(list(self.archive.glob("*.json"))), 1)
            raise RuntimeError("Recognition failed after upload")

        self.store.recognizer = broken_hook
        with self.assertRaises(RequestError) as error:
            self.store.accept(sample_metadata(), sample_image())
        self.assertEqual(error.exception.code, "recognizer_failed")
        self.assertEqual(error.exception.http_status, 502)
        self.assertEqual(self.images()[0].read_bytes(), sample_image())
        self.assertEqual(self.store.received_count, 0)
        self.assertFalse((self.store.output_dir / "latest.json").exists())
        self.store.recognizer = default_recognizer
        self.assertTrue(self.store.accept(sample_metadata(), sample_image())["accepted"])
        self.assertEqual(len(self.images()), 1)

    def test_target_crop_is_saved_even_when_recognition_throws(self):
        import cv2
        import numpy as np
        metadata, data = sample_metadata("unreadable-target"), sample_image(".jpg")
        metadata["recognitionRegion"] = {"x": 2, "y": 3, "width": 7, "height": 5,
                                         "source": "recent_hand_approach", "pixelOrigin": "top-left"}
        def broken_hook(frame, metadata):
            sidecar = json.loads(next(self.archive.glob("*.json")).read_text())
            crop = sidecar["targetCrop"]
            self.assertEqual(crop["status"], "saved")
            np.testing.assert_array_equal(cv2.imread(str(self.archive / crop["imageFile"])), frame[3:8, 2:9])
            raise RuntimeError("Unreadable braille")
        self.store.recognizer = broken_hook
        with self.assertRaises(RequestError) as error:
            self.store.accept(metadata, data)
        self.assertEqual(error.exception.code, "recognizer_failed")
        self.assertEqual(len(list((self.archive / "crops").glob("*.png"))), 1)

    def test_zero_target_region_still_saves_original_and_records_why_no_crop(self):
        metadata = sample_metadata("offscreen-target")
        metadata["recognitionRegion"] = {"x": 0, "y": 0, "width": 0, "height": 0,
                                         "source": "recent_hand_approach", "pixelOrigin": "top-left"}
        metadata["recognitionRegionIssue"] = "Last hand region is outside the captured camera view"
        reply = self.store.accept(metadata, sample_image())
        self.assertTrue(reply["accepted"])
        self.assertEqual(reply["archive"]["crop"]["status"], "unavailable")
        self.assertEqual(reply["archive"]["crop"]["recognitionRegionIssue"], metadata["recognitionRegionIssue"])
        self.assertEqual(len(self.images()), 1)
        self.assertEqual(list((self.archive / "crops").glob("*.png")), [])

    def test_bad_image_dimensions_and_unsafe_ids_create_no_archive(self):
        wrong_size = sample_metadata()
        wrong_size["imageWidth"] += 1
        unsafe = sample_metadata("../escape")
        cases = [(sample_metadata(), b"\x89PNG\r\n\x1a\ninvalid", "invalid_image"),
                 (wrong_size, sample_image(), "dimension_mismatch"),
                 (unsafe, sample_image(), "invalid_metadata")]
        for metadata, data, expected in cases:
            with self.subTest(expected=expected), self.assertRaises(RequestError) as error:
                self.store.accept(metadata, data)
            self.assertEqual(error.exception.code, expected)
        self.assertEqual(self.images(), [])
        self.assertEqual(list(self.archive.glob("*.json")), [])

    def test_conflicting_frame_id_is_rejected_without_archiving_conflict(self):
        self.store.accept(sample_metadata(), sample_image())
        with self.assertRaises(RequestError) as error:
            self.store.accept(sample_metadata(), sample_image(value=90))
        self.assertEqual(error.exception.code, "frame_id_conflict")
        self.assertEqual(len(self.images()), 1)

    def test_archive_failure_prevents_recognition_and_success_ack(self):
        self.store.recognizer = Mock()
        with patch("camera_frame_server.atomic_write", side_effect=OSError("disk full")):
            with self.assertRaises(RequestError) as error:
                self.store.accept(sample_metadata(), sample_image())
        self.assertEqual(error.exception.code, "archive_failed")
        self.assertEqual(error.exception.http_status, 507)
        self.store.recognizer.assert_not_called()
        self.assertEqual(self.store.received_count, 0)
        self.assertIsNone(self.store.snapshot()["latest"])

    def test_sidecar_write_failure_is_repaired_by_retry_without_extra_image(self):
        original_write = camera_frame_server.atomic_write

        def fail_sidecar(path, content):
            if path.parent == self.archive and path.suffix == ".json":
                raise OSError("metadata disk write failed")
            original_write(path, content)

        with patch("camera_frame_server.atomic_write", side_effect=fail_sidecar):
            with self.assertRaises(RequestError) as error:
                self.store.accept(sample_metadata(), sample_image())
        self.assertEqual(error.exception.code, "archive_failed")
        self.assertEqual(len(self.images()), 1)
        self.assertEqual(list(self.archive.glob("*.json")), [])
        reply = self.store.accept(sample_metadata(), sample_image())
        self.assertTrue(reply["accepted"])
        self.assert_archived(reply, sample_metadata(), sample_image())
        self.assertEqual(len(self.images()), 1)

    def test_real_braille_missing_roi_still_archives_original_and_returns_retry(self):
        metadata = sample_metadata("braille-no-region")
        metadata["trigger"] = {"type": "manual", "clearSeconds": 0,
                               "gesture": "double_pinch_timer", "delaySeconds": 1.5}
        metadata["recognitionRegion"] = None
        with patch.dict(os.environ, {}, clear=False), \
                patch("physical_story_receiver.ColorSession", side_effect=AssertionError("Color mode used")):
            store = build_store(self.root / "braille", automatic=True,
                                recognition_mode="braille", archive_dir=self.archive)
            reply = store.accept(metadata, sample_image(".jpg"))
        self.assertTrue(reply["accepted"])
        self.assertEqual(reply["status"], "retry_needed")
        self.assertIsNone(reply["recognition"]["word"])
        self.assertEqual(store.recognizer.recognition_method, "braille_dots")
        self.assertIsNone(store.color_session)
        self.assert_archived(reply, metadata, sample_image(".jpg"))

    def test_configured_destination_persists_across_fresh_stores(self):
        config = self.root / "capture-archive.json"
        self.assertEqual(configured_archive_dir(config), DEFAULT_OUTPUT / "capturedimage2026")
        config.write_text(json.dumps({"archiveDirectory": str(self.archive)}))
        before = config.read_bytes()
        for index in range(2):
            store = FrameStore(self.root / ("run-" + str(index)), archive_dir=configured_archive_dir(config))
            store.accept(sample_metadata("run-" + str(index)), sample_image())
        self.assertEqual(len(self.images()), 2)
        self.assertEqual(config.read_bytes(), before)
        for invalid in ("relative/folder", 42, None):
            config.write_text(json.dumps({"archiveDirectory": invalid}))
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                configured_archive_dir(config)

    def test_health_discloses_active_archive_destination(self):
        server = FrameHTTPServer(("127.0.0.1", 0), self.store)
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": .01}, daemon=True)
        thread.start()
        connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=3)
        try:
            connection.request("GET", "/health")
            response = connection.getresponse()
            self.assertEqual(response.status, 200)
            health = json.loads(response.read())
            self.assertTrue(health["archiveEnabled"])
            self.assertEqual(Path(health["archiveDirectory"]), self.archive.resolve())
        finally:
            connection.close()
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
