import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import cv2
import numpy as np

from table_roi_adapter import recognize, validate_region
from camera_frame_server import FrameStore
from test_camera_frame_server import sample_metadata


class TableRegionTests(unittest.TestCase):
    def fixture(self):
        frame = np.zeros((120, 160, 3), dtype=np.uint8)
        frame[25:75, 30:90] = (12, 130, 240)
        metadata = {"sessionId": "test", "frameId": "frame-1", "captureTimestampUtc": "2026-11-14T00:00:00Z",
                    "pixelOrigin": "top-left", "recognitionRegion": {"x": 30, "y": 25, "width": 60, "height": 50,
                    "pixelOrigin": "top-left", "source": "recent_hand_approach"},
                    "handApproach": {"hand": "left", "positionWorld": {"x": 0, "y": .75, "z": .4},
                    "headPositionWorld": {"x": 0, "y": 1.2, "z": 0}, "sampleMonotonicSeconds": 10,
                    "liftStartedMonotonicSeconds": 10.1, "confirmedMonotonicSeconds": 10.25,
                    "signedGapMeters": .02, "surfaceHeightMeters": .73, "headToPointDistanceMeters": .6}}
        return frame, metadata

    def test_crop_preserves_region_and_metadata_without_recognition(self):
        frame, metadata = self.fixture()
        original = copy.deepcopy(metadata)
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"TABLE_DEPTH_OUTPUT": directory}):
            result = recognize(frame, metadata)
            crop = cv2.imread(str(Path(directory) / "latest-roi.jpg"))
            self.assertEqual(crop.shape, (50, 60, 3))
            self.assertTrue(np.all(np.abs(crop.astype(int) - np.array([12, 130, 240])) < 4))
            saved = json.loads((Path(directory) / "latest-roi.json").read_text())
            self.assertEqual(saved["handApproach"], metadata["handApproach"])
            self.assertFalse(saved["opticalFocusChanged"])
            self.assertFalse(result["recognitionPerformed"])
        self.assertEqual(metadata, original)

    def test_invalid_rectangles_rejected(self):
        for field, value in [("x", -1), ("y", 120), ("width", 0), ("height", 200), ("x", True), ("width", 1.2)]:
            with self.subTest(field=field, value=value):
                frame, metadata = self.fixture()
                metadata["recognitionRegion"][field] = value
                with self.assertRaises(ValueError): validate_region(frame, metadata)

    def test_incompatible_origin_rejected(self):
        frame, metadata = self.fixture()
        metadata["pixelOrigin"] = "bottom-left"
        with self.assertRaises(ValueError): validate_region(frame, metadata)

    def test_bad_times_and_positions_rejected(self):
        for field, value in [("sampleMonotonicSeconds", float("nan")), ("confirmedMonotonicSeconds", 1), ("signedGapMeters", float("inf"))]:
            frame, metadata = self.fixture()
            metadata["handApproach"][field] = value
            with self.assertRaises(ValueError): validate_region(frame, metadata)
        frame, metadata = self.fixture()
        metadata["handApproach"]["positionWorld"]["x"] = float("nan")
        with self.assertRaises(ValueError): validate_region(frame, metadata)

    def test_receiver_acknowledges_exact_frame_and_preserves_region(self):
        frame, additions = self.fixture()
        metadata = sample_metadata("hand-region-1")
        metadata.update(additions)
        metadata.update(imageWidth=160, imageHeight=120)
        metadata["captureTimestampUtc"] = "2026-11-14T20:16:28.1234567Z"
        ok, jpeg = cv2.imencode(".jpg", frame)
        self.assertTrue(ok)
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"TABLE_DEPTH_OUTPUT": directory}):
            store = FrameStore(Path(directory), recognize)
            response = store.accept(metadata, jpeg.tobytes())
            self.assertTrue(response["accepted"])
            self.assertEqual(response["frameId"], metadata["frameId"])
            self.assertEqual(response["sessionId"], metadata["sessionId"])
            self.assertEqual(response["status"], "region_received")
            saved = json.loads((Path(directory) / "latest.json").read_text())
            self.assertEqual(saved["metadata"]["recognitionRegion"], metadata["recognitionRegion"])
            self.assertEqual(saved["metadata"]["captureTimestampUtc"], metadata["captureTimestampUtc"])
            self.assertTrue(store.accept(metadata, jpeg.tobytes())["duplicate"])


if __name__ == "__main__": unittest.main()
