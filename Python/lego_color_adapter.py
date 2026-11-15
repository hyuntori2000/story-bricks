"""Frame-bound manual regions around the unmodified LEGO_OCR ColorReader.

This is a color-code test, not automatic block detection or printed-letter OCR.
Each new capture needs new manually selected regions. Only learned color
prototypes persist between captures; image-space regions never track a brick.
"""

import copy
import json
import os
from pathlib import Path
import re
import sys
import tempfile

import cv2
import numpy as np


SOURCE = Path(os.environ.get("LEGO_OCR_PATH", Path(__file__).resolve().parents[2] / "LEGO_OCR"))
if not (SOURCE / "color_reader.py").is_file():
    raise RuntimeError("LEGO_OCR/color_reader.py was not found; set LEGO_OCR_PATH to the original checkout")
sys.path.insert(0, str(SOURCE))
from color_reader import ColorReader, DESTINATION, extract_color  # noqa: E402
from braille_reader import validate_layout  # noqa: E402


def recognize(frame_bgr, metadata):
    """Capture hook: do not apply calibration-image regions to a new camera view."""
    return {
        "status": "needs_regions", "detections": [],
        "message": "Frame received. On the Mac, select each brick's four corners, then Recognize.",
        "method": "LEGO_OCR.ColorReader", "inputShape": list(frame_bgr.shape),
        "coordinateSystem": "image_pixels_top_left",
        "automaticBlockDetection": False,
    }


recognize.supports_color_regions = True


class ColorSession:
    def __init__(self, calibration_path):
        self.path = Path(calibration_path)
        self.repository_dictionary = json.loads((SOURCE / "color_dictionary.json").read_text())
        self.dictionary = copy.deepcopy(self.repository_dictionary)
        self.source = "repository_example"
        self.calibration_frame = None
        self.calibration_frames = []
        if self.path.exists():
            saved = json.loads(self.path.read_text())
            self.dictionary = saved["dictionary"]
            self.calibration_frame = saved["calibrationFrame"]
            # Older files recorded only the most recent calibration image.
            self.calibration_frames = saved.get("calibrationFrames", [self.calibration_frame])
            self.source = "quest_color_calibration"

    def process(self, frame_bgr, metadata, request):
        cells = request.get("cells")
        if not isinstance(cells, list) or not 1 <= len(cells) <= 26:
            raise ValueError("Select between 1 and 26 brick faces")
        h, w = frame_bgr.shape[:2]
        layout = {"version": 1, "image_size": [w, h], "cells": []}
        for index, cell in enumerate(cells):
            if not isinstance(cell, dict):
                raise ValueError("Each cell must be an object")
            layout["cells"].append({"row_id": "manual", "slot_index": index, "quad": cell.get("quad")})
        validate_layout(layout, (w, h))
        frame_identity = {"sessionId": metadata["sessionId"], "frameId": metadata["frameId"]}
        action = request.get("action", "recognize")
        if action == "calibrate":
            labels = [cell.get("letter") for cell in cells]
            if any(not isinstance(label, str) or not re.fullmatch("[A-Z]", label) for label in labels):
                raise ValueError("Calibration needs one A-Z letter per selected color")
            if len(labels) < 2 or len(set(labels)) != len(labels):
                raise ValueError("Calibration needs at least two different letters, each with one distinct color")
            dictionary = {"version": 1, "scope": "User-assigned Quest color code, not Braille or OCR",
                          "minimum_margin_delta_e76": 2.0, "entries": []}
            # Keep only references measured by this camera, not the repository's
            # differently lit example-photo prototypes. Work on copies so a
            # conflicting addition cannot mutate the active calibration.
            entries = {entry["letter"]: copy.deepcopy(entry) for entry in self.dictionary["entries"]}
            if self.source != "quest_color_calibration":
                entries = {}
            for label, cell in zip(labels, layout["cells"]):
                transform = cv2.getPerspectiveTransform(np.float32(cell["quad"]), DESTINATION)
                crop = cv2.warpPerspective(frame_bgr, transform, (80, 160), flags=cv2.INTER_CUBIC)
                feature = extract_color(crop)
                entries[label] = {"letter": label, "color_name": "Quest color " + label,
                                  "lab": feature.tolist()}
            dictionary["entries"] = list(entries.values())
            # The same acceptance-radius rule used by upstream color_example.calibrate.
            for entry in dictionary["entries"]:
                closest = min(float(np.linalg.norm(np.asarray(entry["lab"]) - other["lab"]))
                              for other in dictionary["entries"] if other is not entry)
                if closest < 4:
                    raise ValueError("Two color references are too similar, including saved references. Use visibly different colors for this test")
                entry["nearest_other_distance_delta_e76"] = closest
                entry["acceptance_radius_delta_e76"] = min(15, closest * .45)
            calibration_frames = copy.deepcopy(self.calibration_frames)
            if frame_identity not in calibration_frames:
                calibration_frames.append(frame_identity)
            saved = {"version": 1, "dictionary": dictionary, "calibrationFrame": frame_identity,
                     "calibrationFrames": calibration_frames}
            self.path.parent.mkdir(parents=True, exist_ok=True)
            fd, temporary = tempfile.mkstemp(prefix=".color-", dir=str(self.path.parent))
            try:
                with os.fdopen(fd, "w") as stream:
                    json.dump(saved, stream, allow_nan=False, indent=2)
                os.replace(temporary, self.path)
            finally:
                if os.path.exists(temporary):
                    os.unlink(temporary)
            self.dictionary, self.calibration_frame = dictionary, frame_identity
            self.calibration_frames = calibration_frames
            self.source = "quest_color_calibration"
            return {"status": "calibrated", "letters": labels, "word": None,
                    "message": "Color references saved. Capture a NEW frame, select its faces, then Recognize. This was calibration, not an accuracy test.",
                    "calibrationFrame": frame_identity, "dictionarySource": self.source,
                    "calibratedLetters": list(entries)}
        if action != "recognize":
            raise ValueError("action must be recognize or calibrate")
        source = request.get("dictionarySource", "repository_example")
        if source == "repository_example":
            dictionary = self.repository_dictionary
            calibration_frame, calibration_frames = None, []
        elif source == "quest_color_calibration" and self.source == "quest_color_calibration":
            dictionary = self.dictionary
            calibration_frame, calibration_frames = self.calibration_frame, self.calibration_frames
        else:
            raise ValueError("Choose repository_example or an existing quest_color_calibration dictionary")
        result, _ = ColorReader(layout, dictionary).read(frame_bgr)
        letters = [cell["letter_candidate"] for cell in result["cells"]]
        accepted = all(letter is not None for letter in letters)
        word = "".join(letters) if accepted else None
        same_frame = frame_identity in calibration_frames
        return {
            "status": "calibration_check" if same_frame else "recognized" if accepted else "needs_review",
            "word": word, "letters": letters, "display": "".join(letter or "?" for letter in letters),
            "message": ("Same calibration frame: this is not independent validation." if same_frame else
                        "Recognized selected regions: " + word if accepted else
                        "Some colors were rejected. Check face corners, lighting, and the color references."),
            "dictionarySource": source, "calibrationFrame": copy.deepcopy(calibration_frame),
            "calibrationFrames": copy.deepcopy(calibration_frames),
            "coordinateSystem": "image_pixels_top_left", "orderSource": "manual_selection_order",
            "automaticBlockDetection": False, "result": result,
        }
