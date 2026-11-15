"""Live target-crop OCR using LEGO_OCR's engine, label rules and quad validator.

The upstream calibrated reader needs known full-face corners. This live adapter
instead uses its RapidOCR dependency's automatic text/character boxes. These are
printed-label bounds, NOT physical brick boundaries. Missing printed-period
regions are proposed from the label row and checked by the unchanged upstream
period reader. No expected word or manually specified image corners are used.
"""
from functools import lru_cache
import hashlib
import os
from pathlib import Path
import sys
import threading
from time import perf_counter

import cv2
import numpy as np

_PROJECT_SOURCE = Path(__file__).resolve().parents[1] / "External" / "LEGO_OCR"
SOURCE = Path(os.environ.get("LEGO_OCR_PATH", _PROJECT_SOURCE if _PROJECT_SOURCE.is_dir()
                             else Path(__file__).resolve().parents[2] / "LEGO_OCR"))
if not (SOURCE / "ocr_reader.py").is_file():
    raise RuntimeError("Run python3.12 Tools/setup-team.py --backend to install the pinned LEGO_OCR source")
if str(SOURCE) not in sys.path:
    sys.path.insert(0, str(SOURCE))
from braille_geometry import validate_layout, project  # noqa: E402
from ocr_label_rules import normalize_label  # noqa: E402
from ocr_reader import DECODER_ID, MIN_OCR_SCORE  # noqa: E402
from printed_period_bridge import propose_period_tokens  # noqa: E402

OCR_MODEL = "en_PP-OCRv3_rec_infer.onnx"
OCR_MODEL_SHA256 = "ef7abd8bd3629ae57ea2c28b425c1bd258a871b93fd2fe7c433946ade9b5d9ea"
METHOD = "RapidOCR automatic text boxes + LEGO_OCR label and printed-period readers"
PROVENANCE = {"algorithm_id": "automatic_ppocrv4_labels_period_v2", "decoder_id": DECODER_ID,
              "boundary_validator": "LEGO_OCR.braille_geometry.validate_layout",
              "period_verifier": "LEGO_OCR.period_detector.detect_period",
              "geometry_kind": "printed_label"}
_ENGINE_LOCK = threading.Lock()
BOUNDARY_MARGIN_PIXELS = 1.0


@lru_cache(maxsize=1)
def get_engine():
    from rapidocr_onnxruntime import RapidOCR
    # Keep the existing primary model and its accepted results unchanged.
    return RapidOCR(intra_op_num_threads=4, inter_op_num_threads=1,
                    use_cls=False, rec_batch_num=6, rec_img_shape=[3, 48, 96])


@lru_cache(maxsize=1)
def get_english_engine():
    from rapidocr_onnxruntime import RapidOCR
    # English labels need the matching English weights and embedded dictionary.
    # The pinned local asset keeps captures offline and acceptance at upstream's .85.
    model = Path(__file__).resolve().parent / "models" / OCR_MODEL
    if not model.is_file() or hashlib.sha256(model.read_bytes()).hexdigest() != OCR_MODEL_SHA256:
        raise RuntimeError("The bundled English OCR model is missing or damaged. Restore Python/models from Git.")
    return RapidOCR(intra_op_num_threads=4, inter_op_num_threads=1,
                    use_cls=False, rec_batch_num=6, rec_model_path=str(model), rec_img_shape=[3, 48, 320])


def _layout(quads, size):
    layout = {"version": 1, "image_size": list(size), "cells": [
        {"row_id": "word", "slot_index": i, "quad": np.asarray(q).tolist()}
        for i, q in enumerate(quads)]}
    validate_layout(layout, tuple(size))
    return layout


def _label_gap(left, right):
    """Spacing evidence for a suffix, independent of recognized word plausibility."""
    a, b = left["quad"], right["quad"]
    axis = (a[1] - a[0]) + (a[2] - a[3])
    axis /= max(float(np.linalg.norm(axis)), 1e-6)
    # RapidOCR estimates box widths from character spacing. Those boxes can
    # touch even for distant bricks, so edge gaps are not independent evidence.
    gap = float((b.mean(axis=0) - a.mean(axis=0)) @ axis)
    height = min(float(np.linalg.norm(a[3] - a[0])), float(np.linalg.norm(b[3] - b[0])))
    ratio = gap / max(height, 1)
    return "same" if ratio <= .95 else "separate" if ratio >= 1.35 else "ambiguous"


def _finite_quad(points):
    q = np.asarray(points, np.float32)
    if (q.shape != (4, 2) or not np.isfinite(q).all() or not cv2.isContourConvex(q)
            or cv2.contourArea(q, oriented=True) <= 0):
        raise ValueError("OCR corners are not a finite convex TL,TR,BR,BL quad")
    return q


def _tokens(record, size):
    """Use RapidOCR's actual character positions; never evenly divide a line."""
    if not isinstance(record, (list, tuple)) or len(record) != 6:
        raise ValueError("OCR character boxes and individual scores are required")
    parent, text, line_score, boxes, chars, scores = record
    if not (len(boxes) == len(chars) == len(scores)) or not chars:
        raise ValueError("OCR character geometry is incomplete")
    if "".join(chars).replace(" ", "").upper() != "".join(str(text).split()).upper():
        raise ValueError("OCR text and character geometry disagree")
    _finite_quad(parent)
    characters = []
    for box, char, score in zip(boxes, chars, scores):
        if str(char).isspace():
            continue
        if len(str(char)) != 1:
            raise ValueError("OCR did not return individual character boxes")
        q = _finite_quad(box)
        confidence = min(float(score), float(line_score))
        if not np.isfinite(confidence):
            confidence = 0.0
        characters.append({"raw": str(char).upper(), "quad": q, "score": confidence})
    tokens, index = [], 0
    while index < len(characters):
        first = characters[index]
        group = [first]
        if index + 1 < len(characters):
            following = characters[index + 1]
            # Upstream allows one numeric suffix (including 19->I) and JO->J.
            possible_pair = ((first["raw"] in "ABCDEFGHIJKLMNOPQRSTUVWXYZ1" and
                              following["raw"] in "0123456789") or
                             (first["raw"] == "J" and following["raw"] == "O"))
            if possible_pair:
                gap = _label_gap(first, following)
                if gap == "ambiguous":
                    raise ValueError("The spacing between a letter and its possible label suffix is ambiguous")
                if gap == "same":
                    group.append(following)
        raw = "".join(c["raw"] for c in group)
        # Keep the whole printed label, including its suffix, inside the box.
        q = np.float32([group[0]["quad"][0], group[-1]["quad"][1],
                        group[-1]["quad"][2], group[0]["quad"][3]])
        if len(group) == len(characters):
            q = np.asarray(parent, np.float32)
        _finite_quad(q)
        tokens.append({"raw": raw, "quad": q, "score": min(c["score"] for c in group),
                       "value": normalize_label(raw)})
        index += len(group)
    return tokens


def _retry(reason, message, started, diagnostics=None):
    return {"status": "retry_needed", "reason": reason, "message": message,
            "word": None, "letters": [], "display": "", "cells": [],
            "automaticBlockDetection": True, "recognitionPerformed": False,
            "coordinateSystem": "image_pixels_top_left", "method": METHOD, **PROVENANCE,
            "dictionarySource": "lego_ocr_letters_period", "orderSource": "capture_box_left_to_right",
            "diagnostics": diagnostics or {}, "proposalTimingMs": round((perf_counter()-started)*1000, 3)}


def _read_crop(crop, selection, started, *, engine=None):
    h, w = crop.shape[:2]
    with _ENGINE_LOCK:
        # Keep uncertain text in the response so a weak letter cannot silently
        # disappear from an otherwise accepted word. Apply upstream's score gate below.
        records, elapsed = (engine if engine is not None else get_engine())(crop, use_det=True, use_cls=False, use_rec=True,
                                       return_word_box=True, text_score=0.0)
    diagnostics = {"wholeBrickBoundaryChecked": False, "boundaryKind": "printed_label",
                   "boundaryValidator": PROVENANCE["boundary_validator"],
                   "insideLabels": 0, "ignoredBoundaryLabels": 0,
                   "rawOCR": [{"text": str(r[1]), "score": float(r[2])} for r in (records or [])],
                   "ocrStageSeconds": elapsed, "completenessVerified": False}
    if not records:
        return _retry("no_printed_labels", "No printed labels found. Move closer and capture again.", started, diagnostics)
    if len(records) > 64:
        return _retry("too_many_labels", "Keep one row of labels in the capture box.", started, diagnostics)
    transform = cv2.getPerspectiveTransform(selection, np.float32([[0, 0], [w-1, 0], [w-1, h-1], [0, h-1]]))
    tokens = []
    try:
        parents = [_finite_quad(r[0]) for r in records]
        # Printed labels form the user-confirmed single row. Low-confidence
        # marks away from that row (e.g. studs) are not missing word letters.
        # Weak text ON the row is retained and still invalidates the whole word.
        anchors = []
        for r, q in zip(records, parents):
            if float(r[2]) >= MIN_OCR_SCORE and all(
                    cv2.pointPolygonTest(selection, (float(px), float(py)), True) > BOUNDARY_MARGIN_PIXELS for px, py in q):
                anchors.extend(project(t["quad"], transform) for t in _tokens(r, (w, h))
                               if t["score"] >= MIN_OCR_SCORE and t["value"] is not None and t["value"] in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        row_top = max(float(q[:, 1].min()) for q in anchors) if len(anchors) >= 2 else 0
        row_bottom = min(float(q[:, 1].max()) for q in anchors) if len(anchors) >= 2 else 0
        if row_top < row_bottom:
            center = float(np.median([q[:, 1].mean() for q in anchors]))
            half_height = .5 * float(np.median([np.ptp(q[:, 1]) for q in anchors]))
            row_top = min(row_top, center-half_height)
            row_bottom = max(row_bottom, center+half_height)
            diagnostics["confidentLabelRowBand"] = [row_top, row_bottom]
        diagnostics["ignoredLowConfidenceOffRow"] = 0
        for record, parent in zip(records, parents):
            if cv2.intersectConvexConvex(selection, parent)[0] <= 0:
                diagnostics["ignoredBoundaryLabels"] += 1
                continue
            projected = project(parent, transform)
            if (float(record[2]) < MIN_OCR_SCORE and row_top < row_bottom and
                    (float(projected[:, 1].max()) < row_top or float(projected[:, 1].min()) > row_bottom)):
                diagnostics["ignoredLowConfidenceOffRow"] += 1
                continue
            tokens.extend(_tokens(record, (w, h)))
    except (ValueError, TypeError, IndexError, cv2.error) as error:
        diagnostics["geometryIssue"] = str(error)
        return _retry("uncertain_label_geometry", "Printed label spacing or corners are unclear. Capture again.", started, diagnostics)
    selected = []
    for token in tokens:
        if not all(cv2.pointPolygonTest(selection, (float(px), float(py)), True) > BOUNDARY_MARGIN_PIXELS
                   for px, py in token["quad"]):
            diagnostics["ignoredBoundaryLabels"] += 1
            continue
        try:
            _layout([token["quad"]], (w, h))
        except (ValueError, TypeError, cv2.error) as error:
            diagnostics["geometryIssue"] = str(error)
            return _retry("uncertain_label_geometry", "Printed label corners are unclear. Capture again.", started, diagnostics)
        selected.append(token)
    diagnostics["insideLabels"] = len(selected)
    if not selected:
        return _retry("capture_box_empty", "No complete printed labels inside the yellow box. Move the labels inside and capture again.", started, diagnostics)
    if not 1 <= len(selected) <= 32:
        return _retry("too_many_labels", "Use one row of one to 32 labels.", started, diagnostics)
    invalid = [t for t in selected if t["value"] is None or t["score"] < MIN_OCR_SCORE]
    if invalid:
        diagnostics["unreadLabels"] = [{"text": t["raw"], "score": t["score"]} for t in invalid]
        return _retry("uncertain_printed_label", "A printed label is unclear. Use letters A–Z and periods, then capture again.", started, diagnostics)
    # Automatic text detection can omit a tiny printed dot entirely. Supply its
    # label ROI to the original period reader instead of accepting only letters.
    periods, period_diagnostics = propose_period_tokens(crop, selection, selected)
    diagnostics["printedPeriodBridge"] = period_diagnostics
    if period_diagnostics.get("reason") in ("ambiguous_printed_period", "too_many_period_candidates"):
        return _retry("uncertain_printed_period", "The trailing period is unclear. Capture again.", started, diagnostics)
    selected.extend(periods)
    diagnostics["insideLabels"] = len(selected)
    if len(selected) > 32:
        return _retry("too_many_labels", "Use one row of one to 32 labels.", started, diagnostics)
    for token in selected:
        token["readingQuad"] = project(token["quad"], transform)
    selected.sort(key=lambda t: float(t["readingQuad"][:, 0].mean()))
    # The confirmed setup is one aligned row. Do not concatenate unrelated rows.
    quads = [t["readingQuad"] for t in selected]
    if len(quads) > 1 and max(float(q[:, 1].min()) for q in quads) >= min(float(q[:, 1].max()) for q in quads):
        return _retry("multiple_label_rows", "Align the printed labels in one row and capture again.", started, diagnostics)
    for index, q in enumerate(quads):
        for other in quads[index+1:]:
            if cv2.intersectConvexConvex(q, other)[0] > .02 * min(cv2.contourArea(q), cv2.contourArea(other)):
                return _retry("overlapping_labels", "Printed label boxes overlap. Move closer and capture again.", started, diagnostics)
    letters = [t["value"] for t in selected]
    if not any(v != "." for v in letters) and letters != ["."]:
        return _retry("unsupported_word", "Include at least one letter in the captured word.", started, diagnostics)
    _layout([t["quad"] for t in selected], (w, h))
    cells = [{"row_id": "word", "slot_index": i, "face_polygon": t["quad"].tolist(),
              "label_polygon": t["quad"].tolist(), "geometry_kind": "printed_label",
              "kind": "punctuation" if t["value"] == "." else "letter", "accepted": True,
              "value": t["value"], "letter_candidate": t["value"] if t["value"] != "." else None,
              "punctuation_candidate": "." if t["value"] == "." else None,
              "raw_ocr_text": t["raw"], "raw_ocr_score": t["score"], "score": t["score"],
              "reason": "isolated_filled_printed_dot" if t.get("source") == "printed_period_shape" else "recognized",
              "source": t.get("source", "ocr"),
              **({"period_diagnostics": t["period_diagnostics"]} if "period_diagnostics" in t else {})}
             for i, t in enumerate(selected)]
    points = np.concatenate([t["quad"] for t in selected])
    center = (points.min(axis=0) + points.max(axis=0)) * .5
    word = "".join(letters)
    return {"status": "recognized", "word": word, "letters": letters, "display": word,
            "automaticBlockDetection": True, "recognitionPerformed": True,
            "coordinateSystem": "image_pixels_top_left", "method": METHOD, **PROVENANCE,
            "dictionarySource": "lego_ocr_letters_period", "orderSource": "capture_box_left_to_right",
            "message": "Read printed labels: " + word + ".",
            "cells": [{"quad": c["face_polygon"]} for c in cells],
            "groupCenterPixel": {"x": float(center[0]), "y": float(center[1])},
            "result": {"method": "ocr", **PROVENANCE, "cells": cells, "image_size": [w, h]},
            "diagnostics": diagnostics}


def recognize(frame_bgr, metadata, unity_crop_bgr=None):
    """Read the exact archived native-color crop; emit full-image coordinates."""
    started = perf_counter()
    if (not isinstance(frame_bgr, np.ndarray) or frame_bgr.dtype != np.uint8 or
            frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3):
        return _retry("invalid_image", "A valid camera photo is required.", started)
    h, w = frame_bgr.shape[:2]
    if (not isinstance(metadata, dict) or metadata.get("imageWidth") != w or
            metadata.get("imageHeight") != h or metadata.get("pixelOrigin") != "top-left"):
        return _retry("invalid_metadata", "Camera coordinates do not match the photo.", started)
    region = metadata.get("recognitionRegion")
    if (not isinstance(region, dict) or region.get("pixelOrigin") != "top-left" or
            region.get("source") != "recent_hand_approach"):
        return _retry("missing_word_region", "Center the labels in the capture box.", started)
    bounds = [region.get(k) for k in ("x", "y", "width", "height")]
    if any(type(v) is not int for v in bounds):
        return _retry("invalid_word_region", "The saved capture region is invalid.", started)
    x, y, rw, rh = bounds
    if x < 0 or y < 0 or rw < 24 or rh < 24 or x+rw > w or y+rh > h:
        return _retry("invalid_word_region", "The capture box must fit inside the photo.", started)
    from capture_crop_archive import target_crop_pixels
    try:
        crop, mask = target_crop_pixels(frame_bgr, metadata, unity_crop_bgr)
        local = mask.get("quadInCrop") or [[0, 0], [rw-1, 0], [rw-1, rh-1], [0, rh-1]]
        selection = np.asarray(local, np.float32)
        # Target-crop extraction validates the acquisition polygon. Use the
        # upstream validator for every detected label below, without clamping.
        if selection.shape != (4, 2) or not cv2.isContourConvex(selection):
            raise ValueError("Invalid capture polygon")
        if cv2.contourArea(selection, oriented=True) <= 0:
            raise ValueError("Capture polygon must be TL,TR,BR,BL")
    except (ValueError, KeyError, TypeError, cv2.error):
        return _retry("invalid_capture_box", "The exact capture crop is unavailable. Capture again.", started)
    result = _read_crop(crop, selection, started)
    for cell in result.get("cells", []):
        cell["quad"] = [[px+x, py+y] for px, py in cell["quad"]]
    payload = result.get("result")
    if payload is not None:
        for cell in payload["cells"]:
            for key in ("face_polygon", "label_polygon"):
                cell[key] = [[px+x, py+y] for px, py in cell[key]]
        payload["image_size"] = [w, h]
    if result.get("groupCenterPixel"):
        result["groupCenterPixel"]["x"] += x
        result["groupCenterPixel"]["y"] += y
    direct = unity_crop_bgr is not None and all(metadata["unityCrop"][key] == region[key]
                                               for key in ("x", "y", "width", "height"))
    mask = dict(mask, selection="printed_labels_inside_capture_box")
    result["recognitionInput"] = {"type": "target_crop", "width": rw, "height": rh,
        "originInFullImage": {"x": x, "y": y}, "fullImageSize": [w, h], "pixelFormat": "BGR8",
        "coordinateSystem": "crop_pixels_top_left", "resized": False, "captureMask": mask,
        "pixelSource": "unity_target_png" if direct else "unity_stage1_png" if unity_crop_bgr is not None else "original_jpeg_or_png",
        "cropStages": 1 if direct or unity_crop_bgr is None else 2}
    result["proposalTimingMs"] = round((perf_counter()-started)*1000, 3)
    return result
