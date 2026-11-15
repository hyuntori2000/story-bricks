"""Race unchanged upstream readers on one automatically located target crop.

The first complete accepted word is returned. The other reader is never
canceled: its result is retained in the per-frame experiment history.
"""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import threading
from time import perf_counter

import cv2
import numpy as np

from lego_ocr_adapter import get_engine, _read_crop
from lego_braille_adapter import _reading_order
from braille_face_locator import locate_faces, capture_selection_quad, capture_face_membership
from capture_crop_archive import target_crop_pixels
from braille_geometry import validate_layout
from braille_reader import BrailleReader
from ocr_reader import CalibratedOCRReader, MIN_OCR_SCORE

PROJECT = Path(__file__).resolve().parents[1]
PIN = json.loads((PROJECT / "Config/recognition-source.json").read_text())
METHOD = "parallel_first_accepted_word_v1"
PROVENANCE = {"algorithm_id": METHOD, "upstream_commit": PIN["commit"],
              "ocr_reader": "LEGO_OCR.CalibratedOCRReader",
              "ocr_geometry_fallback": "automatic labels with upstream OCR and printed-period validation",
              "braille_reader": "LEGO_OCR.BrailleReader",
              "ocr_min_score": MIN_OCR_SCORE,
              "selection_policy": "first_complete_word", "cancel_loser": False,
              "geometry": "automatic_gray_face_geometry"}


class GeometryError(ValueError):
    pass


class RecognitionRejected(ValueError):
    def __init__(self, reason, reader_path):
        super().__init__(reason)
        self.reader_path = reader_path


def prepare(frame, metadata, unity_crop=None):
    """No sample layouts, supplied per-photo corners, rescaling or black mask."""
    if not isinstance(frame, np.ndarray) or frame.dtype != np.uint8 or frame.ndim != 3 or frame.shape[2] != 3:
        raise GeometryError("invalid_image")
    h, w = frame.shape[:2]
    if metadata.get("imageWidth") != w or metadata.get("imageHeight") != h or metadata.get("pixelOrigin") != "top-left":
        raise GeometryError("invalid_image_coordinates")
    region = deepcopy(metadata.get("recognitionRegion"))
    if not isinstance(region, dict) or region.get("source") != "recent_hand_approach" or region.get("pixelOrigin") != "top-left":
        raise GeometryError("missing_word_region")
    bounds = [region.get(k) for k in ("x", "y", "width", "height")]
    if any(type(v) is not int for v in bounds):
        raise GeometryError("invalid_word_region")
    x, y, rw, rh = bounds
    if x < 0 or y < 0 or rw < 24 or rh < 24 or x+rw > w or y+rh > h:
        raise GeometryError("invalid_word_region")
    crop, mask = target_crop_pixels(frame, metadata, unity_crop)
    region.update(x=0, y=0)
    points = region.get("readingQuad")
    if not isinstance(points, list) or len(points) != 4:
        raise GeometryError("missing_reading_orientation")
    region["readingQuad"] = [dict(p, x=p["x"]-x, y=p["y"]-y) for p in points]
    capture_selection_quad(region)
    direct = unity_crop is not None and all(metadata["unityCrop"][k] == metadata["recognitionRegion"][k]
                                          for k in ("x", "y", "width", "height"))
    info = {"type": "target_crop", "width": rw, "height": rh,
            "originInFullImage": {"x": x, "y": y}, "fullImageSize": [w, h],
            "pixelFormat": "BGR8", "coordinateSystem": "crop_pixels_top_left",
            "resized": False, "captureMask": mask,
            "pixelSource": "unity_target_png" if direct else "unity_stage1_png" if unity_crop is not None else "original_jpeg_or_png",
            "cropStages": 1 if direct or unity_crop is None else 2}
    return crop, {"region": region}, info


def locate_layout(crop, context):
    if "cells" in context:
        return context
    region = context["region"]
    capture_quad = capture_selection_quad(region)
    found = locate_faces(crop, region)
    if found.get("status") != "localized":
        raise GeometryError(found.get("reason", "uncertain_faces"))
    faces = [c for c in found.get("cells", []) if capture_face_membership(c["quad"], capture_quad) == "inside"]
    if not 1 <= len(faces) <= 32:
        raise GeometryError("capture_box_empty_or_too_many_faces")
    try:
        quads = _reading_order(faces, region)
        h, w = crop.shape[:2]
        layout = {"version": 1, "image_size": [w, h], "cells": [
            {"row_id": "word", "slot_index": i, "quad": q.tolist()} for i, q in enumerate(quads)]}
        validate_layout(layout, (w, h))
        return layout
    except (ValueError, KeyError, TypeError, cv2.error) as error:
        raise GeometryError("invalid_face_geometry: " + str(error)) from error


def read_ocr(crop, context):
    try:
        layout = locate_layout(crop, context)
    except GeometryError as error:
        # Moving-camera faces may be unresolved although printed labels remain
        # readable. Preserve the existing automatic-text route in this case.
        selection = capture_selection_quad(context["region"])
        automatic = _read_crop(crop, selection, perf_counter())
        if automatic["status"] != "recognized":
            raise RecognitionRejected(automatic.get("reason", "unread_labels") + "; face geometry: " + str(error), "automatic_labels")
        payload = automatic["result"]
        layout = {"version": 1, "image_size": list(crop.shape[1::-1]), "cells": [
            {"row_id": c["row_id"], "slot_index": c["slot_index"], "quad": c["face_polygon"]} for c in payload["cells"]]}
        payload.update(_layout=layout, readerPath="automatic_labels", geometryIssue=str(error),
                       diagnostics=automatic.get("diagnostics", {}))
        return payload
    payload = CalibratedOCRReader(layout, engine=get_engine()).read(crop)[0]
    payload.update(_layout=layout, readerPath="calibrated_labels")
    return payload


def read_braille(crop, context):
    layout = locate_layout(crop, context)
    payload = BrailleReader(layout).read(crop)[0]
    payload.update(_layout=layout, readerPath="six_dot_faces")
    return payload


def convert(payload, layout, info, source):
    layout = payload.get("_layout", layout)
    cells = sorted(payload.get("cells", []), key=lambda c: c.get("slot_index", -1))
    expected = {(c["row_id"], c["slot_index"]) for c in layout["cells"]}
    actual = {(c.get("row_id"), c.get("slot_index")) for c in cells}
    if len(cells) != len(expected) or actual != expected:
        raise ValueError("reader_cell_count_mismatch")
    word = "".join(c.get("value") or "?" for c in cells)
    accepted = all(c.get("accepted") is True for c in cells) and bool(re.fullmatch(r"[A-Z.]{1,32}", word)) and (word == "." or bool(re.search(r"[A-Z]", word)))
    keys = ("row_id", "slot_index", "value", "accepted", "reason", "bits", "mask",
            "raw_ocr_text", "raw_ocr_score", "score", "source", "estimated_source_stud_diameter_px", "period_diagnostics")
    x, y = info["originInFullImage"]["x"], info["originInFullImage"]["y"]
    compact = []
    for c in cells:
        item = {k: c[k] for k in keys if k in c}
        item["kind"] = "punctuation" if c.get("value") == "." else "letter" if re.fullmatch(r"[A-Z]", c.get("value") or "") else "unknown"
        item["letter_candidate"] = c.get("value") if item["kind"] == "letter" else None
        item["punctuation_candidate"] = "." if item["kind"] == "punctuation" else None
        q = np.asarray(c["face_polygon"], np.float32)
        if q.shape != (4, 2) or not np.isfinite(q).all():
            raise ValueError("invalid_reader_polygon")
        item["face_polygon"] = (q + [x, y]).tolist()
        compact.append(item)
    transport_method = "braille_dots" if source == "braille" else "ocr"
    result = {"status": "recognized" if accepted else "retry_needed", "word": word if accepted else None,
              "letters": list(word) if accepted else [], "display": word,
              "reason": "recognized" if accepted else ", ".join(dict.fromkeys(c.get("reason", "unread_cell") for c in cells if not c.get("accepted"))) or "unsupported_word",
              "method": transport_method, "algorithm_id": payload.get("algorithm_id"),
              "readerPath": payload.get("readerPath", source), "geometryIssue": payload.get("geometryIssue"),
              "decoder_id": payload.get("decoder_id"), "automaticBlockDetection": True,
              "recognitionPerformed": True, "coordinateSystem": "image_pixels_top_left",
              "dictionarySource": "lego_letters_period", "orderSource": "capture_box_left_to_right",
              "cells": [{"quad": c["face_polygon"]} for c in compact],
              "result": {"method": transport_method, "cells": compact, "image_size": info["fullImageSize"]},
              "recognitionInput": info}
    if "diagnostics" in payload:
        result["diagnostics"] = payload["diagnostics"]
    if accepted:
        points = np.concatenate([np.asarray(c["face_polygon"]) for c in compact])
        center = (points.min(axis=0) + points.max(axis=0)) * .5
        result["groupCenterPixel"] = {"x": float(center[0]), "y": float(center[1])}
    return result


class ParallelRecognizer:
    supports_unity_crop = True
    recognition_method = "parallel_ocr_braille"
    recognition_input = "target_crop"
    recognition_provenance = PROVENANCE
    recognition_description = "First accepted complete word; both reader results and elapsed times are retained."

    def __init__(self, history, *, preparer=prepare, readers=None):
        self.history = history
        self.prepare = preparer
        self.readers = readers or {"ocr": read_ocr, "braille": read_braille}
        self._pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="word-reader")
        self._call_lock = threading.Lock()
        self._pending = []
        self._closed = False

    def _save(self, record):
        try:
            self.history(deepcopy(record))
        except Exception as error:
            record["historyError"] = type(error).__name__
            print("Recognition history write failed: " + type(error).__name__, flush=True)

    def __call__(self, frame, metadata, unity_crop_bgr=None):
        started = perf_counter()
        record = {"sessionId": metadata["sessionId"], "frameId": metadata["frameId"],
                  "startedAtUtc": datetime.now(timezone.utc).isoformat(), "algorithmCommit": PIN["commit"],
                  "selectedSource": None, "selectedWord": None, "decisionMs": None,
                  "complete": False, "disagreement": False,
                  "ocr": {"status": "pending", "word": None, "elapsedMs": None, "reason": ""},
                  "braille": {"status": "pending", "word": None, "elapsedMs": None, "reason": ""}}
        self._save(record)
        with self._call_lock:
            if self._closed:
                raise RuntimeError("Recognition service is closed")
            # A late result finishes before the next pair starts. This bounds
            # background work and preserves each backend's mutable model state.
            for future in self._pending:
                future.result()
            self._pending = []
            record["queueMs"] = round((perf_counter()-started)*1000, 3)
            try:
                crop, layout, info = self.prepare(frame, deepcopy(metadata), unity_crop_bgr)
            except Exception as error:
                elapsed = round((perf_counter()-started)*1000, 3)
                reason = str(error)[:180] if isinstance(error, (GeometryError, ValueError)) else type(error).__name__
                record.update(complete=True, decisionMs=elapsed)
                for name in self.readers:
                    record[name] = {"status": "not_run", "word": None, "elapsedMs": elapsed, "reason": "geometry: " + reason}
                self._save(record)
                return self._failure(record, "Shared block geometry unavailable: " + reason)
            record["preprocessingMs"] = round((perf_counter()-started)*1000-record["queueMs"], 3)
            condition = threading.Condition()
            results = {}
            winner = [None]
            start_gate = threading.Event()

            def run(name):
                start_gate.wait()
                execution_start = perf_counter()
                try:
                    result = convert(self.readers[name](crop, layout), layout, info, name)
                except (GeometryError, RecognitionRejected) as error:
                    result = {"status": "retry_needed", "word": None, "reason": str(error)[:240],
                              "readerPath": getattr(error, "reader_path", "six_dot_faces" if name == "braille" else "calibrated_labels")}
                except Exception as error:
                    result = {"status": "error", "word": None, "reason": type(error).__name__ + ": " + str(error)[:150]}
                with condition:
                    elapsed = round((perf_counter()-started)*1000, 3)
                    record[name] = {"status": result["status"], "word": result["word"],
                                    "elapsedMs": elapsed, "executionMs": round((perf_counter()-execution_start)*1000, 3),
                                    "reason": result.get("reason", ""), "display": result.get("display", ""),
                                    "readerPath": result.get("readerPath", name), "geometryIssue": result.get("geometryIssue")}
                    results[name] = result
                    if winner[0] is None and result["status"] == "recognized":
                        winner[0] = name
                        record.update(selectedSource=name, selectedWord=result["word"], decisionMs=elapsed)
                    record["complete"] = len(results) == len(self.readers)
                    if record["complete"]:
                        record["disagreement"] = all(r["status"] == "recognized" for r in results.values()) and len({r["word"] for r in results.values()}) > 1
                        if record["decisionMs"] is None:
                            record["decisionMs"] = elapsed
                    self._save(record)
                    condition.notify_all()

            self._pending = [self._pool.submit(run, name) for name in self.readers]
            start_gate.set()
            with condition:
                condition.wait_for(lambda: winner[0] is not None or len(results) == len(self.readers))
                snapshot = deepcopy(record)
                if winner[0] is not None:
                    result = deepcopy(results[winner[0]])
                    result.update(recognitionMode="parallel", selectedSource=winner[0],
                                  proposalTimingMs=snapshot["decisionMs"], parallel=snapshot,
                                  message="Read " + result["word"] + " using " + winner[0] + "; both reader results are logged.")
                    return result
                return self._failure(snapshot, "Neither OCR nor braille accepted the complete word. Capture again.", info)

    @staticmethod
    def _failure(record, message, info=None):
        result = {"status": "retry_needed", "word": None, "letters": [], "display": "", "cells": [],
                  "automaticBlockDetection": True, "recognitionPerformed": any(record[k]["status"] != "not_run" for k in ("ocr", "braille")),
                  "coordinateSystem": "image_pixels_top_left", "recognitionMode": "parallel",
                  "algorithm_id": METHOD, "method": METHOD, "message": message,
                  "reason": "neither_reader_accepted", "proposalTimingMs": record["decisionMs"], "parallel": deepcopy(record)}
        if info is not None:
            result["recognitionInput"] = info
        return result

    def close(self):
        with self._call_lock:
            self._closed = True
            self._pool.shutdown(wait=True)
