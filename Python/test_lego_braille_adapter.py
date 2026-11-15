"""Image-based wrapper checks; injected geometry tests are not detector accuracy claims."""
import copy
import json
import unittest
from unittest.mock import patch

import cv2
import numpy as np

import lego_braille_adapter as adapter
from braille_code import LETTER_DOTS, PUNCTUATION_DOTS


def synthetic_row(word, reverse_slots=(), ordinary=False, dot_overrides=None):
    image = np.full((200, 100 * len(word) + 20, 3), 45, np.uint8)
    cells = []
    for i, letter in enumerate(word):
        tile = np.full((160, 80, 3), 180, np.uint8)
        dots = range(1, 9) if ordinary else (dot_overrides or {}).get(i, {**LETTER_DOTS, **PUNCTUATION_DOTS}[letter])
        for dot in dots:
            x, y = map(int, adapter.GRID[dot - 1])
            cv2.circle(tile, (x, y), 12, (80, 80, 80), -1)
            cv2.circle(tile, (x - 2, y - 2), 10, (220, 220, 220), -1)
        if i in reverse_slots:
            tile = cv2.rotate(tile, cv2.ROTATE_180)
        x = 20 + 100 * i
        image[20:180, x:x + 80] = tile
        q = [[x, 20], [x + 79, 20], [x + 79, 179], [x, 179]]
        cells.append({"row_id": "word", "slot_index": i, "quad": q,
                      "orientationCandidates": [q, np.roll(q, -2, axis=0).tolist()]})
    h, w = image.shape[:2]
    metadata = {"sessionId": "test", "frameId": "synthetic", "imageWidth": w, "imageHeight": h,
                "pixelOrigin": "top-left", "recognitionRegion": {"x": 0, "y": 0, "width": w, "height": h,
                    "pixelOrigin": "top-left", "source": "recent_hand_approach"}}
    located = {"status": "localized", "cells": cells, "diagnostics": {"source": "synthetic_known_geometry"}}
    return image, metadata, located


def explicit_reading_frame(metadata, quad=None, physical_size=None):
    h, w = metadata["imageHeight"], metadata["imageWidth"]
    if quad is None:
        quad = [[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]]
    width, depth = physical_size or ((w - 1) / 1000, (h - 1) / 1000)
    metadata["recognitionRegion"].update(
        readingOrientation="labels_toward_viewer",
        readingQuad=[{"x": float(x), "y": float(y)} for x, y in quad],
        physicalWidthMeters=width, physicalDepthMeters=depth)
    return metadata


class BrailleAdapterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cv2.setNumThreads(4)

    def read_known_geometry(self, image, metadata, located):
        with patch.object(adapter, "locate_faces", return_value=located):
            return adapter.recognize(image, metadata)

    def test_actual_dot_images_preserve_arbitrary_order_and_repeated_letters(self):
        for word in ("OFX", "JUDY", "ZZAZ", "AAAA"):
            image, metadata, located = synthetic_row(word)
            result = self.read_known_geometry(image, metadata, located)
            self.assertEqual(result["status"], "recognized", result)
            self.assertEqual(result["word"], word)
            self.assertEqual(result["result"]["method"], "braille_dots")
            self.assertEqual(len(result["result"]["cells"]), len(word))

    def test_every_alphabet_letter_uses_original_dot_reader(self):
        for letter in LETTER_DOTS:
            image, metadata, located = synthetic_row(letter)
            with self.subTest(letter=letter):
                result = self.read_known_geometry(image, metadata, located)
                self.assertEqual(result.get("word"), letter, result)

    def test_rotated_individual_bricks_resolve_by_label_end_without_color(self):
        image, metadata, located = synthetic_row("FOX", reverse_slots=(0, 2))
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["word"], "FOX", result)
        np.testing.assert_allclose(result["result"]["cells"][0]["face_polygon"], located["cells"][0]["orientationCandidates"][1])

    def test_ordinary_eight_stud_bricks_never_become_letters(self):
        image, metadata, located = synthetic_row("FOX", ordinary=True)
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["status"], "retry_needed")
        self.assertIsNone(result["word"])
        self.assertEqual(result["letters"], [])

    def test_uncertain_middle_brick_does_not_drop_from_word(self):
        image, metadata, located = synthetic_row("FOX")
        image[20:180, 120:200] = 180
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["status"], "retry_needed")
        self.assertIsNone(result["word"])
        self.assertEqual(result["diagnostics"]["uncertainSlot"], 1)

    def test_no_dot_or_symmetric_midrow_geometry_is_not_guessed(self):
        image, metadata, located = synthetic_row("A")
        image[20:180, 20:100] = 180
        for y in (80, 120):
            cv2.circle(image, (40, y), 12, (80, 80, 80), -1)
            cv2.circle(image, (38, y - 2), 10, (220, 220, 220), -1)
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["status"], "retry_needed")
        self.assertIsNone(result["word"])

    def test_printed_black_ring_is_not_mistaken_for_a_fourth_row_stud(self):
        image, metadata, located = synthetic_row("A")
        cv2.circle(image, (40, 160), 10, (10, 10, 10), 4)
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["word"], "A", result)

    def test_derived_photographic_faces_use_automatic_geometry_and_original_reader(self):
        from test_braille_face_locator import photographed_reference_fixture
        image = photographed_reference_fixture()
        h, w = image.shape[:2]
        metadata = {"imageWidth": w, "imageHeight": h, "pixelOrigin": "top-left",
                    "recognitionRegion": {"x": 0, "y": 0, "width": w, "height": h,
                        "pixelOrigin": "top-left", "source": "recent_hand_approach"}}
        # No geometry injection: both localization and dot reading run here.
        # This collage is derived from calibrated reference photos, not held-out Quest validation.
        result = adapter.recognize(image, metadata)
        self.assertEqual(result["word"], "FOX", result)
        gray_bgr = cv2.cvtColor(cv2.cvtColor(image, cv2.COLOR_BGR2GRAY), cv2.COLOR_GRAY2BGR)
        self.assertEqual(adapter.recognize(gray_bgr, metadata)["word"], "FOX")

    def test_missing_region_does_not_scan_unrelated_background(self):
        image, metadata, _ = synthetic_row("FOX")
        metadata["recognitionRegion"] = None
        with patch.object(adapter, "locate_faces", side_effect=AssertionError("Must not scan background")):
            self.assertEqual(adapter.recognize(image, metadata)["reason"], "missing_word_region")

    def test_clipped_or_multiple_row_locator_failure_is_propagated(self):
        image, metadata, located = synthetic_row("FOX")
        for reason in ("clipped_candidate", "multiple_rows"):
            with patch.object(adapter, "locate_faces", return_value={"status": "retry_needed", "reason": reason, "message": "Retry"}):
                result = adapter.recognize(image, metadata)
                self.assertEqual(result["reason"], reason)
                self.assertIsNone(result["word"])

    def test_wrapper_reads_each_orientation_once_without_offset_redetection(self):
        image, metadata, located = synthetic_row("FOX")
        with patch.object(adapter, "locate_faces", return_value=located), patch.object(adapter, "_read_cell", wraps=adapter._read_cell) as reader:
            result = adapter.recognize(image, metadata)
        self.assertEqual(result["word"], "FOX", result)
        self.assertEqual(reader.call_count, 3)
        for call, cell in zip(reader.call_args_list, located["cells"]):
            np.testing.assert_array_equal(call.args[1], cell["quad"])

    def test_updated_project_a16_alias_passes_through_without_number_conversion(self):
        image, metadata, located = synthetic_row("A", dot_overrides={0: (1, 6)})
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["word"], "A", result)
        cell = result["result"]["cells"][0]
        self.assertEqual(cell["mask"], 33)
        self.assertEqual(cell["mapping_source"], "project_alias")
        self.assertEqual(cell["value"], "A")

    def test_story_reader_accepts_only_period_from_upstream_punctuation(self):
        for symbol in PUNCTUATION_DOTS:
            with self.subTest(symbol=symbol):
                image, _, located = synthetic_row(symbol)
                cell, reason = adapter._read_oriented_cell(image, located["cells"][0]["quad"])
                if symbol != ".":
                    self.assertIsNone(cell)
                    self.assertEqual(reason, "unsupported_symbol_pattern")
                    continue
                self.assertIsNotNone(cell, reason)
                self.assertTrue(cell["accepted"])
                self.assertEqual(cell["kind"], "punctuation")
                self.assertEqual(cell["value"], symbol)
                self.assertEqual(cell["punctuation_candidate"], symbol)
                self.assertIsNone(cell["letter_candidate"])

    def test_other_punctuation_rejects_whole_word_in_explicit_and_legacy_paths(self):
        for word in ("TREE!", "A'", "A,", "A-", "WHY?"):
            for explicit in (False, True):
                with self.subTest(word=word, explicit=explicit):
                    image, metadata, located = synthetic_row(word)
                    if explicit:
                        explicit_reading_frame(metadata)
                    result = self.read_known_geometry(image, metadata, located)
                    self.assertEqual(result["status"], "retry_needed", result)
                    self.assertIsNone(result["word"])
                    self.assertEqual(result["letters"], [])

    def test_period_question_orientation_ambiguity_does_not_guess_word(self):
        # On a blank-ended 2x4 face, 180 degrees maps period dots256 to
        # question dots236. Neither neighboring text nor a word list resolves it.
        image, metadata, located = synthetic_row("TREE.")
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["reason"], "ambiguous_orientation_or_pattern", result)
        self.assertEqual(result["diagnostics"]["uncertainSlot"], 4)
        self.assertIsNone(result["word"])
        self.assertEqual(result["letters"], [])

    def test_punctuation_only_is_not_a_story_word(self):
        image, metadata, located = synthetic_row(".")
        explicit_reading_frame(metadata)
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["reason"], "unsupported_word", result)
        self.assertIsNone(result["word"])

    def test_upstream_indicators_do_not_become_words_or_finish_commands(self):
        for dots in ((3, 4, 5, 6), (6,), (5, 6)):
            image, _, located = synthetic_row("A", dot_overrides={0: dots})
            quad = np.float32(located["cells"][0]["quad"])
            cell = adapter._read_cell(image, quad)
            self.assertTrue(cell["accepted"])
            self.assertEqual(cell["kind"], "indicator")
            self.assertIsNone(adapter._orientation(image, quad)[0])

    def test_original_and_retry_payloads_identify_updated_detector_and_decoder(self):
        image, metadata, located = synthetic_row("A")
        recognized = self.read_known_geometry(image, metadata, located)
        retry = adapter.recognize(None, {})
        for payload in (recognized, recognized["result"], retry):
            self.assertEqual(payload["algorithm_id"], "six_dot_radial_v1")
            self.assertEqual(payload["decoder_id"], "lego_english_a16_punctuation_v2")

    def test_updated_real_photo_reader_keeps_full_face_and_all_sixteen_symbols(self):
        # This is supplied calibrated geometry, not automatic-location or Quest
        # accuracy. The source JPEG uses encoded 4000x3000 pixels (EXIF ignored).
        image = cv2.imread(str(adapter.SOURCE / "samples/lego_final.jpg"),
                           cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
        layout = json.loads((adapter.SOURCE / "layouts/lego_final_braille.json").read_text())
        cells = [adapter._read_cell(image, np.float32(c["quad"])) for c in layout["cells"]]
        self.assertTrue(all(c["accepted"] for c in cells))
        self.assertEqual("".join(c["value"] for c in cells), "JUDYFOXRAINTREE.")
        self.assertEqual(cells[8]["mask"], 33)
        self.assertEqual(cells[-1]["raised_dots"], [2, 5, 6])
        for source_cell, cell in zip(layout["cells"], cells):
            np.testing.assert_array_equal(cell["face_polygon"], source_cell["quad"])

    def test_explicit_frame_accepts_arbitrary_words_and_period(self):
        for word in ("ZZAZ", "ONEILL", "COOP", "WHY.", "TREE.", "HELLO.", "GENERAL.", "A" * 32):
            with self.subTest(word=word):
                image, metadata, located = synthetic_row(word)
                explicit_reading_frame(metadata)
                with patch.object(adapter, "_label_end_evidence", side_effect=AssertionError("No extra dot detector")), \
                     patch.object(adapter, "_read_cell", wraps=adapter._read_cell) as reader:
                    result = self.read_known_geometry(image, metadata, located)
                self.assertEqual(result["word"], word, result)
                self.assertEqual(reader.call_count, len(word))
                self.assertEqual(result["orderSource"], "capture_box_left_to_right")
                self.assertTrue(all(c["orientationEvidence"]["source"] == "capture_box" for c in result["result"]["cells"]))

    def test_explicit_frame_reorders_geometry_without_choosing_by_decoded_content(self):
        image, metadata, located = synthetic_row("XNAB.")
        explicit_reading_frame(metadata)
        located["cells"] = list(reversed(located["cells"]))
        # Locator candidates may start at either short end; they are geometry,
        # not a claim about how the physical printed labels face.
        for cell in located["cells"]:
            cell["quad"] = np.roll(np.asarray(cell["quad"]), -2, axis=0).tolist()
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["word"], "XNAB.", result)

    def test_explicit_frame_handles_rotated_perspective_geometry(self):
        original, metadata, located = synthetic_row("QZXAB.")
        h, w = original.shape[:2]
        source = np.float32([[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]])
        # A 90-degree turn plus perspective; detection is injected separately,
        # so this tests the reading-frame contract, not locator rotation limits.
        destination = np.float32([[300, 35], [280, 760], [45, 700], [60, 65]])
        transform = cv2.getPerspectiveTransform(source, destination)
        image = cv2.warpPerspective(original, transform, (350, 800), borderValue=(45, 45, 45))
        metadata.update(imageWidth=350, imageHeight=800)
        metadata["recognitionRegion"].update(width=350, height=800)
        explicit_reading_frame(metadata, destination, ((w - 1) / 1000, (h - 1) / 1000))
        for cell in located["cells"]:
            cell["quad"] = cv2.perspectiveTransform(np.float32([cell["quad"]]), transform)[0].tolist()
        located["cells"].reverse()
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["word"], "QZXAB.", result)

    def test_explicit_frame_accepts_corners_outside_saved_image(self):
        image, metadata, located = synthetic_row("FOX.")
        h, w = image.shape[:2]
        explicit_reading_frame(metadata, [[-20, -20], [w + 20, -20], [w + 20, h + 20], [-20, h + 20]],
                               ((w + 40) / 1000, (h + 40) / 1000))
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["word"], "FOX.", result)

    def test_explicit_reading_geometry_fails_closed_before_dot_reading(self):
        image, metadata, located = synthetic_row("AB")
        explicit_reading_frame(metadata)
        cases = []
        for key, value in (("readingOrientation", "guess_from_letters"), ("readingQuad", None),
                           ("physicalWidthMeters", 0), ("physicalDepthMeters", float("nan"))):
            bad = copy.deepcopy(metadata); bad["recognitionRegion"][key] = value; cases.append((bad, located))
        for permutation in ((0, 2, 1, 3), (3, 2, 1, 0)):
            bad = copy.deepcopy(metadata)
            q = bad["recognitionRegion"]["readingQuad"]
            bad["recognitionRegion"]["readingQuad"] = [q[i] for i in permutation]
            cases.append((bad, located))
        for quad in ([[20, 20], [99, 179], [99, 20], [20, 179]],
                     [[20, 20], [99, 20], [float("nan"), 179], [20, 179]]):
            bad_cells = copy.deepcopy(located); bad_cells["cells"][0]["quad"] = quad; cases.append((metadata, bad_cells))
        # Two overlapping interiors and a second row must never form a word.
        for offset in ((-60, 0), (0, 180)):
            bad_cells = copy.deepcopy(located)
            bad_cells["cells"][1]["quad"] = (np.float32(bad_cells["cells"][1]["quad"]) + offset).tolist()
            cases.append((metadata, bad_cells))
        with patch.object(adapter, "_read_cell", side_effect=AssertionError("Geometry must be checked first")):
            for bad_metadata, bad_cells in cases:
                result = self.read_known_geometry(image, bad_metadata, bad_cells)
                self.assertEqual(result["reason"], "invalid_reading_geometry", result)
                self.assertIsNone(result["word"])

    def test_explicit_frame_preserves_whole_word_rejection_and_length_limit(self):
        image, metadata, located = synthetic_row("FOX")
        explicit_reading_frame(metadata)
        image[20:180, 120:200] = 180
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["reason"], "uncertain_dot_pattern", result)
        self.assertEqual(result["letters"], [])
        self.assertEqual(result["diagnostics"]["uncertainSlot"], 1)
        image, metadata, located = synthetic_row("A" * 33)
        explicit_reading_frame(metadata)
        self.assertEqual(self.read_known_geometry(image, metadata, located)["reason"], "invalid_face_count")

    def test_explicit_frame_passes_all_calibrated_reference_rows_to_unchanged_reader(self):
        image = cv2.imread(str(adapter.SOURCE / "samples/lego_final.jpg"),
                           cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
        layout = json.loads((adapter.SOURCE / "layouts/lego_final_braille.json").read_text())
        h, w = image.shape[:2]
        for row_id, expected in zip(("row1", "row2", "row3", "row4"), ("JUDY", "FOX", "RAIN", "TREE.")):
            cells = [c for c in layout["cells"] if c["row_id"] == row_id]
            first, last = cells[0]["quad"], cells[-1]["quad"]
            metadata = {"imageWidth": w, "imageHeight": h, "pixelOrigin": "top-left",
                        "recognitionRegion": {"x": 0, "y": 0, "width": w, "height": h,
                                              "pixelOrigin": "top-left", "source": "recent_hand_approach"}}
            explicit_reading_frame(metadata, [first[0], last[1], last[2], first[3]], (.2, .12))
            # The supplied corners test adapter compatibility; they are not
            # automatic localization input in the actual Quest application.
            result = self.read_known_geometry(image, metadata, {"status": "localized", "cells": cells})
            self.assertEqual(result["word"], expected, result)

    def test_empty_serialized_orientation_fields_keep_legacy_compatibility(self):
        image, metadata, located = synthetic_row("FOX")
        metadata["recognitionRegion"].update(readingOrientation="", readingQuad=None)
        self.assertEqual(self.read_known_geometry(image, metadata, located)["word"], "FOX")

    def test_result_geometry_is_full_image_and_bounded_json(self):
        image, metadata, located = synthetic_row("FOX")
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["groupCenterPixel"], {"x": 159.5, "y": 99.5})
        self.assertLess(len(json.dumps(result, allow_nan=False)), 65536)
        self.assertEqual(result["result"]["cells"][0]["face_polygon"], located["cells"][0]["quad"])

    def test_metadata_dimensions_and_nonfinite_geometry_fail_closed(self):
        image, metadata, located = synthetic_row("A")
        metadata["imageWidth"] += 1
        self.assertEqual(adapter.recognize(image, metadata)["reason"], "invalid_metadata")
        metadata["imageWidth"] -= 1
        located["cells"][0]["orientationCandidates"][0][0][0] = float("nan")
        result = self.read_known_geometry(image, metadata, located)
        self.assertEqual(result["status"], "retry_needed")

    def test_too_small_faces_are_not_upsampled_into_confident_letters(self):
        image, metadata, located = synthetic_row("A")
        image = cv2.resize(image, (30, 50), interpolation=cv2.INTER_AREA)
        metadata.update(imageWidth=30, imageHeight=50)
        metadata["recognitionRegion"].update(width=30, height=50)
        for cell in located["cells"]:
            cell["quad"] = (np.asarray(cell["quad"]) / 4).tolist()
            cell["orientationCandidates"] = (np.asarray(cell["orientationCandidates"]) / 4).tolist()
        self.assertEqual(self.read_known_geometry(image, metadata, located)["status"], "retry_needed")


if __name__ == "__main__":
    unittest.main()
