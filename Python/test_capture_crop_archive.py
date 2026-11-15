"""Exact target-crop persistence contracts; no headset, recognition or APIs."""

import copy
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import cv2
import numpy as np

from capture_crop_archive import archive_target_crop


class CaptureCropArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.archive = Path(self.temporary.name) / "archive"
        self.frame = np.arange(10 * 12 * 3, dtype=np.uint16).reshape(10, 12, 3).astype(np.uint8)
        self.metadata = {"pixelOrigin": "top-left", "imageWidth": 12, "imageHeight": 10,
                         "recognitionRegion": {"x": 3, "y": 2, "width": 5, "height": 4,
                                               "pixelOrigin": "top-left", "source": "recent_hand_approach",
                                               "clipped": False, "retainedFraction": 1.0}}

    def save(self, metadata=None, image_name="photo.jpg", frame=None):
        return archive_target_crop(self.archive, image_name, self.frame if frame is None else frame,
                                   self.metadata if metadata is None else metadata)

    def test_exact_rectangle_is_lossless_and_linked_to_original(self):
        result = self.save()
        image = self.archive / result["imageFile"]
        self.assertEqual(result["status"], "saved")
        self.assertEqual(result["imageFile"], "crops/photo_crop.png")
        self.assertEqual(result["sourceImageFile"], "photo.jpg")
        self.assertEqual((result["width"], result["height"]), (5, 4))
        self.assertEqual(result["sourceRegion"], self.metadata["recognitionRegion"])
        self.assertEqual(result["sha256"], hashlib.sha256(image.read_bytes()).hexdigest())
        np.testing.assert_array_equal(cv2.imread(str(image)), self.frame[2:6, 3:8])

    def test_bottom_right_pixel_and_entire_explicit_region_are_valid(self):
        for x, y, width, height in [(11, 9, 1, 1), (0, 0, 12, 10)]:
            with self.subTest(region=(x, y, width, height)):
                metadata = copy.deepcopy(self.metadata)
                metadata["recognitionRegion"].update(x=x, y=y, width=width, height=height)
                result = self.save(metadata, image_name=f"boundary-{x}.png")
                np.testing.assert_array_equal(cv2.imread(str(self.archive / result["imageFile"])),
                                              self.frame[y:y + height, x:x + width])

    def test_invalid_or_missing_region_never_creates_a_fake_crop(self):
        changes = [None, {}, {"width": 0}, {"height": -1}, {"x": -1}, {"x": 11},
                   {"y": 9}, {"x": 1.5}, {"x": True}, {"pixelOrigin": "bottom-left"},
                   {"source": "inferred_background"}]
        for change in changes:
            with self.subTest(change=change):
                metadata = copy.deepcopy(self.metadata)
                if change is None:
                    metadata.pop("recognitionRegion")
                elif not change:
                    metadata["recognitionRegion"] = change
                else:
                    metadata["recognitionRegion"].update(change)
                metadata["recognitionRegionIssue"] = "Target is outside camera view"
                result = self.save(metadata)
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["recognitionRegionIssue"], "Target is outside camera view")
                self.assertNotIn("imageFile", result)
                self.assertFalse(self.archive.exists())

    def test_mismatched_image_coordinates_are_unavailable(self):
        for change in [{"imageWidth": 11}, {"imageHeight": 9}, {"pixelOrigin": "bottom-left"}]:
            metadata = dict(self.metadata, **change)
            self.assertEqual(self.save(metadata)["status"], "unavailable")
        self.assertEqual(archive_target_crop(self.archive, "photo.jpg", self.frame, None)["status"], "unavailable")
        self.assertEqual(self.save(frame=self.frame[:, :, 0])["status"], "unavailable")
        self.assertFalse(self.archive.exists())

    def test_jpeg_upload_crop_keeps_exact_decoded_pixels(self):
        ok, encoded = cv2.imencode(".jpg", self.frame)
        self.assertTrue(ok)
        decoded = cv2.imdecode(encoded, cv2.IMREAD_COLOR)
        result = self.save(frame=decoded)
        np.testing.assert_array_equal(cv2.imread(str(self.archive / result["imageFile"])), decoded[2:6, 3:8])

    def test_retries_reuse_existing_image_without_overwriting(self):
        initial = self.save()
        path = self.archive / initial["imageFile"]
        before = (path.read_bytes(), path.stat().st_mtime_ns)
        with patch("capture_crop_archive._save_new", side_effect=AssertionError("Must not overwrite")):
            retry = self.save()
        self.assertEqual(initial, retry)
        self.assertEqual(before, (path.read_bytes(), path.stat().st_mtime_ns))
        self.assertEqual(len(list(path.parent.glob("*.png"))), 1)

    def test_different_captures_have_distinct_crop_files(self):
        first = self.save(image_name="first.jpg")
        second = self.save(image_name="second.jpg")
        self.assertNotEqual(first["imageFile"], second["imageFile"])
        self.assertEqual(len(list((self.archive / "crops").glob("*.png"))), 2)

    def test_disk_failure_propagates_without_success(self):
        with patch("capture_crop_archive._save_new", side_effect=OSError("Disk full")):
            with self.assertRaises(OSError):
                self.save()
        self.assertEqual(list((self.archive / "crops").iterdir()), [])

    def test_encoding_failure_and_conflicting_existing_crop_are_not_accepted(self):
        with patch("capture_crop_archive.cv2.imencode", return_value=(False, None)):
            with self.assertRaises(ValueError):
                self.save()
        first = self.save()
        (self.archive / first["imageFile"]).write_bytes(b"invalid png")
        with self.assertRaises(ValueError):
            self.save()

    def test_archive_basename_cannot_escape_crop_directory(self):
        for image_name in ["../outside.jpg", "/outside.jpg", "nested/image.jpg", "..\\outside.jpg", "photo.txt"]:
            with self.subTest(image_name=image_name):
                with self.assertRaises(ValueError):
                    self.save(image_name=image_name)
        self.assertFalse(self.archive.exists())


if __name__ == "__main__":
    unittest.main()
