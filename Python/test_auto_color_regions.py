"""Offline held-out-photo and adversarial checks for automatic color regions."""
import copy
import itertools
import json
from pathlib import Path
import unittest

import cv2
import numpy as np

from auto_color_regions import recognize
from lego_color_adapter import ColorSession


PROJECT = Path(__file__).resolve().parents[1]
REFERENCES = Path(__file__).resolve().parent / "test_fixtures/quest_color_references_20261114.json"
CAPTURES = PROJECT / "Documentation/DeviceTests/physical-story"


class AutomaticColorRegionTests(unittest.TestCase):
    def setUp(self):
        self.session = ColorSession(REFERENCES)
        self.colors = {
            entry["letter"]: tuple(int(value) for value in np.rint(
                cv2.cvtColor(np.array([[entry["lab"]]], np.float32), cv2.COLOR_LAB2BGR)[0, 0] * 255))
            for entry in self.session.dictionary["entries"]
        }

    def saved(self, folder):
        path = CAPTURES / folder
        metadata = json.loads((path / "latest.json").read_text())["metadata"]
        return cv2.imread(str(path / "latest.jpg")), metadata

    def synthetic(self, word, gap=6):
        frame = np.full((480, 640, 3), 185, np.uint8)
        x = (640 - (len(word) * (30 + gap) - gap)) // 2
        for index, letter in enumerate(word):
            left = x + index * (30 + gap)
            cv2.rectangle(frame, (left, 220), (left + 29, 274),
                          self.colors.get(letter, (240, 30, 240)), -1)
        return frame, {
            "sessionId": "synthetic", "frameId": "fixture-" + word,
            "imageWidth": 640, "imageHeight": 480,
            "recognitionRegion": {"x": 40, "y": 80, "width": 560, "height": 320,
                                  "pixelOrigin": "top-left", "source": "recent_hand_approach"},
        }

    def assert_retry(self, frame, metadata):
        result = recognize(frame, metadata, self.session)
        self.assertEqual(result["status"], "retry_needed", result)
        self.assertIsNone(result["word"])
        self.assertEqual(result["letters"], [])
        return result

    def test_real_fox_and_judy_without_manual_corners(self):
        for name, expected in (("word-1", "FOX"), ("word-2", "JUDY")):
            with self.subTest(name=name):
                frame, metadata = self.saved("three-word-live-20261114/" + name)
                before = frame.copy()
                result = recognize(frame, metadata, self.session)
                self.assertEqual(result["status"], "recognized", result)
                self.assertEqual(result["word"], expected)
                self.assertTrue(result["automaticBlockDetection"])
                self.assertEqual(result["result"]["method"], "example_color")
                self.assertEqual(result["orderSource"], "image_left_to_right")
                self.assertTrue(np.array_equal(before, frame))
                region = metadata["recognitionRegion"]
                for cell in result["cells"]:
                    q = np.asarray(cell["quad"])
                    self.assertGreaterEqual(float(q[:, 0].min()), region["x"])
                    self.assertLessEqual(float(q[:, 0].max()), region["x"] + region["width"])
                    self.assertGreaterEqual(float(q[:, 1].min()), region["y"])
                    self.assertLessEqual(float(q[:, 1].max()), region["y"] + region["height"])
                # Previously drawn face regions are independent reference annotations,
                # never supplied to the detector. This is pixel agreement, not 3D truth.
                manual = json.loads((CAPTURES / "three-word-live-20261114" / name / "manual-regions.json").read_text())
                for found, annotated in zip(result["candidateCenters"], manual["cells"]):
                    reference = np.asarray(annotated["quad"], dtype=float).mean(axis=0)
                    self.assertLess(np.linalg.norm(reference - [found["x"], found["y"]]), 12)

    def test_real_oxf_keeps_order_and_accepts_interior_word_in_partially_clipped_search(self):
        frame, metadata = self.saved("hand-word-test-20261114")
        self.assertTrue(metadata["recognitionRegion"]["clipped"])
        result = recognize(frame, metadata, self.session)
        self.assertEqual(result["word"], "OXF", result)
        self.assertNotEqual(result["word"], "FOX")

    def test_all_fox_permutations_use_geometry_not_a_word_whitelist(self):
        for word in map("".join, itertools.permutations("FOX")):
            with self.subTest(word=word):
                result = recognize(*self.synthetic(word), self.session)
                self.assertEqual(result["word"], word, result)

    def test_arbitrary_count_and_repeated_separated_letters(self):
        for word in ("F", "X", "FF", "YJ", "DYUF", "FOXX", "JUDYFOX"):
            with self.subTest(word=word):
                result = recognize(*self.synthetic(word), self.session)
                self.assertEqual(result["word"], word, result)
                self.assertEqual(len(result["cells"]), len(word))

    def test_unknown_magenta_first_middle_and_last_never_yields_partial_word(self):
        for word in ("?FX", "F?X", "FX?", "JU?DY", "?", "F?OX"):
            with self.subTest(word=word):
                result = self.assert_retry(*self.synthetic(word))
                self.assertIn("unknown", result["message"].lower())

    def test_unknown_cyan_is_rejected_by_original_color_classifier(self):
        frame, metadata = self.synthetic("F?X")
        frame[220:275, 305:335] = (240, 240, 0)
        self.assert_retry(frame, metadata)

    def test_unknown_neutral_between_known_faces_triggers_row_gap_retry(self):
        frame, metadata = self.synthetic("F?X")
        frame[220:275, 305:335] = (215, 215, 215)
        self.assert_retry(frame, metadata)

    def test_cropped_first_or_last_letter_does_not_become_a_partial_word(self):
        # Full FOX remains visible in the JPEG, but its hand search window cuts
        # through an end letter. A thin strip must not be silently discarded.
        for x, width in ((40, 310), (40, 327), (290, 310), (273, 327)):
            with self.subTest(x=x, width=width):
                frame, metadata = self.synthetic("FOX")
                metadata["recognitionRegion"].update(x=x, width=width)
                result = self.assert_retry(frame, metadata)
                self.assertIn("cut off", result["message"])

    def test_boundary_fragment_away_from_word_row_does_not_block_it(self):
        frame, metadata = self.synthetic("FOX")
        cv2.rectangle(frame, (30, 110), (55, 164), self.colors["F"], -1)
        self.assertEqual(recognize(frame, metadata, self.session)["word"], "FOX")

    def test_two_rows_do_not_silently_select_a_preferred_word(self):
        frame, metadata = self.synthetic("FOX")
        cv2.rectangle(frame, (300, 135), (329, 189), self.colors["F"], -1)
        self.assert_retry(frame, metadata)

    def test_large_gaps_reject_a_disconnected_collection(self):
        self.assert_retry(*self.synthetic("FOX", gap=40))

    def test_color_distractors_outside_hand_roi_do_not_change_word(self):
        frame, metadata = self.synthetic("FOX")
        cv2.rectangle(frame, (90, 10), (119, 64), self.colors["Y"], -1)
        cv2.rectangle(frame, (200, 10), (229, 64), (240, 30, 240), -1)
        self.assertEqual(recognize(frame, metadata, self.session)["word"], "FOX")

    def test_blank_real_table_region_does_not_become_a_word(self):
        frame, metadata = self.saved("three-word-live-20261114/word-1")
        metadata["recognitionRegion"].update(x=700, y=780, width=180, height=160)
        self.assert_retry(frame, metadata)

    def test_calibration_frames_do_not_count_as_new_recognition(self):
        frame, metadata = self.synthetic("FOX")
        metadata.update(self.session.calibration_frames[0])
        self.assert_retry(frame, metadata)

    def test_invalid_roi_and_frame_identity_fail_closed(self):
        for mutation in (
            lambda m: m.update(imageWidth=100),
            lambda m: m.update(frameId=""),
            lambda m: m["recognitionRegion"].update(x=-1),
            lambda m: m["recognitionRegion"].update(width=9999),
            lambda m: m["recognitionRegion"].update(x=40.0),
            lambda m: m["recognitionRegion"].update(source="other"),
            lambda m: m["recognitionRegion"].update(pixelOrigin="bottom-left"),
            lambda m: m["recognitionRegion"].update(clipped=True, retainedFraction=.4),
        ):
            frame, metadata = self.synthetic("FOX")
            mutation(metadata)
            self.assert_retry(frame, metadata)

    def test_approved_references_are_required_and_not_modified(self):
        frame, metadata = self.synthetic("FOX")
        before = copy.deepcopy(self.session.dictionary)
        reference_bytes = REFERENCES.read_bytes()
        recognize(frame, metadata, self.session)
        self.assertEqual(self.session.dictionary, before)
        self.assertEqual(REFERENCES.read_bytes(), reference_bytes)
        self.assertEqual(recognize(frame, metadata, None)["status"], "retry_needed")
        self.session.source = "repository_example"
        self.assert_retry(frame, metadata)


if __name__ == "__main__":
    unittest.main()
