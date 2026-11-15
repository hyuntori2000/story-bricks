"""Real upstream color classification with frame identity and calibration checks."""

import copy
import json
from pathlib import Path
import tempfile
import unittest

import cv2
import numpy as np

from camera_frame_server import FrameStore, RequestError
from lego_color_adapter import recognize
from test_camera_frame_server import sample_metadata


def frame(colors):
    image = np.full((200, 240, 3), 70, dtype=np.uint8)
    for index, color in enumerate(colors):
        image[20:180, 10 + index * 120:90 + index * 120] = color
    return cv2.imencode(".png", image)[1].tobytes()


def regions(frame_id="calibration", labels=("F", "X"), action="calibrate"):
    return {"sessionId": "test-session", "frameId": frame_id, "action": action,
            "dictionarySource": "quest_color_calibration",
            "cells": [{"letter": label, "quad": [[10+i*120,20],[89+i*120,20],
                                                    [89+i*120,179],[10+i*120,179]]}
                      for i, label in enumerate(labels)]}


class LegoColorAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = FrameStore(Path(self.temp.name), recognize)
        self.red, self.yellow = [20, 30, 220], [10, 220, 240]

    def tearDown(self):
        self.temp.cleanup()

    def capture(self, frame_id, colors=None):
        metadata = sample_metadata(frame_id)
        metadata.update(imageWidth=240, imageHeight=200)
        metadata["trigger"] = {"type": "manual", "clearSeconds": 0}
        return self.store.accept(metadata, frame(colors or [self.red, self.yellow]))

    def test_real_reader_classifies_new_reordered_frame_and_ignores_typed_labels(self):
        ack = self.capture("calibration")
        self.assertEqual(ack["status"], "needs_regions")
        saved = self.store.analyze_regions(regions())["recognition"]
        self.assertEqual(saved["status"], "calibrated")
        self.assertIsNone(saved["word"])
        self.capture("new-view", [self.yellow, self.red])
        result = self.store.analyze_regions(regions("new-view", ("A", "B"), "recognize"))["recognition"]
        self.assertEqual(result["status"], "recognized")
        self.assertEqual(result["word"], "XF")
        self.assertEqual(result["result"]["method"], "example_color")
        self.assertEqual(result["coordinateSystem"], "image_pixels_top_left")
        self.assertFalse(result["automaticBlockDetection"])

    def test_same_frame_is_only_a_calibration_check(self):
        self.capture("calibration")
        self.store.analyze_regions(regions())
        result = self.store.analyze_regions(regions(action="recognize"))["recognition"]
        self.assertEqual(result["status"], "calibration_check")
        self.assertEqual(result["word"], "FX")

    def test_stale_selection_cannot_calibrate_or_analyze_the_new_image(self):
        self.capture("calibration")
        self.capture("new-view")
        for action in ("recognize", "calibrate"):
            with self.assertRaises(RequestError) as caught:
                self.store.analyze_regions(regions(action=action))
            self.assertEqual(caught.exception.code, "stale_frame")
        self.assertFalse(self.store.color_session.path.exists())
        with self.assertRaises(RequestError):
            self.store.image("test-session", "calibration")

    def test_invalid_corners_or_indistinguishable_colors_do_not_save_calibration(self):
        self.capture("calibration")
        bad = regions()
        bad["cells"][0]["quad"][0][0] = -1
        with self.assertRaises(RequestError):
            self.store.analyze_regions(bad)
        self.capture("same-colors", [self.red, self.red])
        with self.assertRaises(RequestError):
            self.store.analyze_regions(regions("same-colors"))
        self.assertFalse(self.store.color_session.path.exists())

    def test_color_references_persist_but_region_selections_do_not(self):
        self.capture("calibration")
        self.store.analyze_regions(regions())
        restarted = FrameStore(Path(self.temp.name), recognize)
        self.assertIsNone(restarted.latest)
        self.assertEqual(restarted.color_session.source, "quest_color_calibration")
        saved = json.loads(restarted.color_session.path.read_text())
        self.assertNotIn("cells", saved)
        self.assertNotIn("layout", saved)

    def test_added_colors_preserve_old_recognition_after_reload_without_labels(self):
        self.capture("calibration")
        self.store.analyze_regions(regions())
        blue, green = [230, 40, 20], [20, 190, 20]
        self.capture("added-colors", [blue, green])
        added = self.store.analyze_regions(regions("added-colors", ("D", "O")))["recognition"]
        self.assertEqual(set(added["calibratedLetters"]), {"F", "X", "D", "O"})
        self.store = FrameStore(Path(self.temp.name), recognize)
        for identifier, colors, expected in [
            ("independent-old", [self.yellow, self.red], "XF"),
            ("independent-new", [green, blue], "OD"),
        ]:
            self.capture(identifier, colors)
            request = regions(identifier, action="recognize")
            for cell in request["cells"]:
                del cell["letter"]
            result = self.store.analyze_regions(request)["recognition"]
            self.assertEqual(result["status"], "recognized")
            self.assertEqual(result["word"], expected)

    def test_legacy_calibration_frame_remains_a_calibration_check_after_addition(self):
        self.capture("calibration")
        self.store.analyze_regions(regions())
        path = self.store.color_session.path
        legacy = json.loads(path.read_text())
        del legacy["calibrationFrames"]
        path.write_text(json.dumps(legacy))
        self.store = FrameStore(Path(self.temp.name), recognize)
        self.capture("added-colors", [[230, 40, 20], [20, 190, 20]])
        self.store.analyze_regions(regions("added-colors", ("D", "O")))
        self.store = FrameStore(Path(self.temp.name), recognize)
        self.capture("calibration")
        result = self.store.analyze_regions(regions(action="recognize"))["recognition"]
        self.assertEqual(result["status"], "calibration_check")
        self.assertEqual(result["word"], "FX")

    def test_conflicting_addition_preserves_file_and_active_references(self):
        self.capture("calibration")
        self.store.analyze_regions(regions())
        before_file = self.store.color_session.path.read_bytes()
        before_dictionary = copy.deepcopy(self.store.color_session.dictionary)
        before_frames = copy.deepcopy(self.store.color_session.calibration_frames)
        self.capture("conflicting-colors", [self.red, [20, 190, 20]])
        with self.assertRaises(RequestError):
            self.store.analyze_regions(regions("conflicting-colors", ("D", "O")))
        self.assertEqual(self.store.color_session.path.read_bytes(), before_file)
        self.assertEqual(self.store.color_session.dictionary, before_dictionary)
        self.assertEqual(self.store.color_session.calibration_frames, before_frames)

    def test_original_dictionary_is_default_and_ignores_saved_calibration(self):
        from lego_color_adapter import SOURCE
        self.capture("calibration")
        self.store.analyze_regions(regions(labels=("A", "B")))
        session = self.store.color_session
        original = cv2.imread(str(SOURCE / "samples/lego_color.jpg"))
        layout = json.loads((SOURCE / "layouts/lego_color.json").read_text())
        request = {"action": "recognize", "cells": [{"quad": c["quad"], "letter": "Z"} for c in layout["cells"]]}
        metadata = {"sessionId": "test-session", "frameId": "calibration"}
        result = session.process(original, metadata, request)
        self.assertEqual(result["dictionarySource"], "repository_example")
        self.assertEqual(result["status"], "recognized")
        self.assertEqual(result["word"], "JUDYFOXRAIN")
        self.assertIsNone(result["calibrationFrame"])
        self.assertEqual(result["calibrationFrames"], [])

    def test_unknown_dictionary_source_is_rejected(self):
        self.capture("calibration")
        request = regions(action="recognize")
        request["dictionarySource"] = "invented"
        with self.assertRaises(RequestError):
            self.store.analyze_regions(request)


if __name__ == "__main__":
    unittest.main()
