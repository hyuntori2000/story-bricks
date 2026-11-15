"""Local automatic face geometry -> original LEGO_OCR.BrailleReader.

No color-to-letter mapping, OCR, word dictionary, cloud call or per-photo corners.
The capture-box reading frame supplies orientation when the user arranges one
aligned row with printed labels facing the viewer. Legacy captures
retain their label-end geometry check. Uncertain faces invalidate the whole word.
"""
from pathlib import Path
import copy
import os
import re
import sys
from time import perf_counter

import cv2
import numpy as np

_PROJECT_SOURCE = Path(__file__).resolve().parents[1] / "External" / "LEGO_OCR"
SOURCE = Path(os.environ.get("LEGO_OCR_PATH", _PROJECT_SOURCE if _PROJECT_SOURCE.is_dir()
                             else Path(__file__).resolve().parents[2] / "LEGO_OCR"))
if not (SOURCE / "braille_reader.py").is_file():
    raise RuntimeError("LEGO_OCR/braille_reader.py is required; preserve the original repository")
if str(SOURCE) not in sys.path:
    sys.path.insert(0, str(SOURCE))
from braille_reader import BrailleReader, DESTINATION  # noqa: E402
from braille_code import DECODER_ID, PUNCTUATION_DOTS  # noqa: E402
from stud_detector import ALGORITHM_ID  # noqa: E402
from braille_face_locator import locate_faces, capture_selection_quad, capture_face_membership  # noqa: E402

METHOD = "automatic_gray_face_geometry + LEGO_OCR.BrailleReader"
GRID = np.float32([[20, 20], [20, 60], [20, 100], [60, 20], [60, 60], [60, 100], [20, 140], [60, 140]])
LABEL_CENTERS = GRID[6:]
LABEL_RING_ANGLES = np.arange(48) * np.pi / 24
PROVENANCE = {"algorithm_id": ALGORITHM_ID, "decoder_id": DECODER_ID}
DICTIONARY_SOURCE = "lego_english_six_dot_letters_and_punctuation"


def _retry(reason, message, started, cells=None, diagnostics=None):
    return {"status": "retry_needed", "reason": reason, "message": message,
            "word": None, "letters": [], "display": "", "automaticBlockDetection": True,
            "recognitionPerformed": False, "coordinateSystem": "image_pixels_top_left",
            "method": METHOD, **PROVENANCE, "dictionarySource": DICTIONARY_SOURCE,
            "orderSource": "image_left_to_right", "cells": cells or [],
            "diagnostics": diagnostics or {}, "proposalTimingMs": round((perf_counter() - started) * 1000, 3)}


def _label_end_evidence(face):
    """Check only the fourth row for physical studs, never the six-dot pattern.

    The full 2x4 face is rectified here to inspect its printed-label quarter.
    BrailleReader separately crops that same full face to 80x120 and owns all
    six-dot decisions, including its radial verification and recovery pass.
    """
    raw = cv2.cvtColor(face, cv2.COLOR_BGR2GRAY)
    sharpness = float(cv2.Laplacian(cv2.GaussianBlur(raw, (3, 3), .5), cv2.CV_32F).var())
    if sharpness < 8 or float(raw.std()) < 3:
        return {"bits": [None] * 2, "reason": "low_detail", "sharpness": sharpness}
    enhanced = cv2.GaussianBlur(cv2.createCLAHE(clipLimit=2, tileGridSize=(4, 4)).apply(raw), (5, 5), .7)
    edges = cv2.Canny(enhanced, 30, 60)
    distance = cv2.distanceTransform(255 - edges, cv2.DIST_L2, 3)
    found = [{"strict": False, "weak": False} for _ in range(2)]
    for name, votes in (("strict", 12), ("weak", 10)):
        circles = cv2.HoughCircles(enhanced, cv2.HOUGH_GRADIENT, dp=1, minDist=22,
                                   param1=60, param2=votes, minRadius=8, maxRadius=17)
        if circles is None:
            continue
        for x, y, radius in circles[0]:
            errors = np.linalg.norm(LABEL_CENTERS - [x, y], axis=1)
            index = int(errors.argmin())
            # Printed 6/X/O arcs away from the two nominal stud centers are
            # not fourth-row studs. No decisions are made on the top six slots.
            if errors[index] > 5:
                continue
            yy_disk, xx_disk = np.ogrid[:160, :80]
            disk = (xx_disk - x) ** 2 + (yy_disk - y) ** 2 <= radius ** 2
            cx, cy = LABEL_CENTERS[index].astype(int)
            local = raw[cy - 17:cy + 18, cx - 17:cx + 18]
            surface = float(np.percentile(local, 80))
            # Dark printed strokes inside a ring are not same-surface relief.
            # This is grayscale contrast evidence, never text/OCR or color ID.
            if surface > 0 and float((raw[disk] < surface * .3).mean()) > .12:
                continue
            xx = np.round(x + radius * np.cos(LABEL_RING_ANGLES)).astype(int)
            yy = np.round(y + radius * np.sin(LABEL_RING_ANGLES)).astype(int)
            inside = (xx >= 0) & (xx < 80) & (yy >= 0) & (yy < 160)
            if int(inside.sum()) < 24:
                continue
            coverage = float((distance[yy[inside], xx[inside]] <= 2).mean())
            cx, cy = LABEL_CENTERS[index].astype(int)
            patch = raw[cy - 15:cy + 16, cx - 15:cx + 16]
            contrast = float(np.percentile(patch, 95) - np.percentile(patch, 5))
            threshold = .7 if name == "strict" else .75
            if coverage >= threshold and contrast >= 15:
                found[index][name] = True
    bits = [True if e["strict"] else None if e["weak"] else False for e in found]
    return {"bits": bits, "reason": "ambiguous_stud" if any(x is None for x in bits) else "ok",
            "sharpness": sharpness}


def _read_cell(frame, quad):
    h, w = frame.shape[:2]
    layout = {"version": 1, "image_size": [w, h], "cells": [{"row_id": "word", "slot_index": 0, "quad": quad.tolist()}]}
    result, _ = BrailleReader(layout, mode="letters").read(frame)
    return result["cells"][0]


def _orientation(frame, quad):
    """Do not use decoded word plausibility to select orientation."""
    q = np.asarray(quad, np.float32)
    # These are source-image pixels: upsampling is not new detail.
    if .3 * min(np.linalg.norm(q[1] - q[0]), np.linalg.norm(q[2] - q[3])) < 8:
        return None, "too_small"
    face = cv2.warpPerspective(frame, cv2.getPerspectiveTransform(q, DESTINATION), (80, 160), flags=cv2.INTER_CUBIC)
    evidence = _label_end_evidence(face)
    if any(b is not False for b in evidence["bits"]):
        return None, "label_end_contains_studs_or_is_uncertain"
    # Orientation candidates retain the upstream punctuation identities. The
    # story allowlist must not turn an upside-down question mark into a period.
    cell, reason = _read_oriented_cell(frame, q, story_symbols_only=False)
    if cell is None:
        return None, reason
    cell["orientationEvidence"] = {"labelRowStuds": evidence["bits"],
                                   "topRowStuds": [cell["bits"][0], cell["bits"][3]]}
    return cell, "ok"


def _read_oriented_cell(frame, quad, *, story_symbols_only=True):
    """The unchanged upstream reader alone decides the six dots and symbol."""
    cell = _read_cell(frame, np.asarray(quad, np.float32))
    value = cell.get("value")
    punctuation = value == "." if story_symbols_only else value in PUNCTUATION_DOTS
    supported = (cell.get("kind") == "letter" and re.fullmatch(r"[A-Z]", value or "")) or (
        cell.get("kind") == "punctuation" and punctuation)
    if not cell.get("accepted") or not supported:
        return None, cell.get("reason") if cell.get("reason") not in (None, "ok") else "unsupported_symbol_pattern"
    if any(b is None for b in cell["bits"]):
        return None, "uncertain_dots"
    return cell, "ok"


def _reading_order(located, region):
    """Orient complete face rectangles using the explicit acquisition frame.

    This verifies rectangular axes and row geometry, not printed text or dots.
    All bricks must physically follow the user-confirmed placement convention;
    an individual 180-degree placement error is not observable from rectangles.
    """
    if region.get("readingOrientation") != "labels_toward_viewer":
        raise ValueError("Unsupported reading orientation")
    points = region.get("readingQuad")
    if not isinstance(points, list) or len(points) != 4:
        raise ValueError("The reading frame requires four ordered corners")
    if any(not isinstance(p, dict) or any(type(p.get(k)) not in (int, float) for k in ("x", "y")) for p in points):
        raise ValueError("Reading corners must contain numeric x and y")
    frame = np.float32([[p["x"], p["y"]] for p in points])
    if not np.isfinite(frame).all() or not cv2.isContourConvex(frame) or cv2.contourArea(frame, oriented=True) <= 1:
        raise ValueError("The reading frame must be finite, clockwise and nondegenerate")
    width, depth = region.get("physicalWidthMeters"), region.get("physicalDepthMeters")
    if any(type(v) not in (int, float) or not np.isfinite(v) or v <= 0 for v in (width, depth)):
        raise ValueError("The reading frame requires positive physical dimensions")
    destination = np.float32([[0, 0], [width, 0], [width, depth], [0, depth]])
    transform = cv2.getPerspectiveTransform(frame, destination)
    if not np.isfinite(transform).all() or np.linalg.det(transform) == 0:
        raise ValueError("The reading frame cannot be projected")
    oriented = []
    for item in located:
        quad = np.asarray(item["quad"], np.float32)
        if quad.shape != (4, 2) or not np.isfinite(quad).all() or not cv2.isContourConvex(quad) or cv2.contourArea(quad, oriented=True) <= 0:
            raise ValueError("A face has invalid or crossing corners")
        homogeneous = np.column_stack((quad, np.ones(4))) @ transform.T
        if not np.isfinite(homogeneous).all() or np.any(np.abs(homogeneous[:, 2]) < 1e-9) or np.ptp(np.sign(homogeneous[:, 2])):
            raise ValueError("A face crosses the reading-plane horizon")
        physical = (homogeneous[:, :2] / homogeneous[:, 2:]).astype(np.float32)
        if not cv2.isContourConvex(physical) or cv2.contourArea(physical, oriented=True) <= 0:
            raise ValueError("A projected face is invalid")
        # The locator's first/opposite edges are the two short ends. Rectangles
        # cannot establish polarity: the user-confirmed capture frame supplies it.
        across = ((physical[1] - physical[0]) + (physical[2] - physical[3])) * .5
        down = ((physical[3] - physical[0]) + (physical[2] - physical[1])) * .5
        if down[1] < 0:
            quad, physical = np.roll(quad, -2, axis=0), np.roll(physical, -2, axis=0)
            across, down = -across, -down
        if down[1] <= abs(down[0]) or across[0] <= abs(across[1]) or np.linalg.norm(down) <= np.linalg.norm(across):
            raise ValueError("Align the brick's long axis with the capture-box depth")
        oriented.append((quad, physical, physical.mean(axis=0)))
    oriented.sort(key=lambda item: float(item[2][0]))
    if len(oriented) > 1:
        if max(float(item[1][:, 1].min()) for item in oriented) >= min(float(item[1][:, 1].max()) for item in oriented):
            raise ValueError("Faces do not share one row")
        for previous, current in zip(oriented, oriented[1:]):
            displacement = current[2] - previous[2]
            if displacement[0] <= abs(displacement[1]):
                raise ValueError("The row does not read left to right in the capture box")
        for index, (_, physical, _) in enumerate(oriented):
            for _, other, _ in oriented[index + 1:]:
                # Shared touching edges have zero area; overlapping interiors
                # make the proposed partition ambiguous. Tolerance is numerical.
                overlap = float(cv2.intersectConvexConvex(physical, other)[0])
                if overlap > min(cv2.contourArea(physical), cv2.contourArea(other)) * 1e-5:
                    raise ValueError("Face interiors overlap in the reading frame")
    return [item[0] for item in oriented]


def _compact(cell, slot):
    keys = ("bits", "mask", "raised_dots", "unicode", "letter_candidate", "punctuation_candidate",
            "value", "kind", "mapping_source", "interpretation_reason", "accepted", "reason",
            "face_polygon", "estimated_source_stud_diameter_px", "orientationEvidence")
    return {"row_id": "word", "slot_index": slot, **{k: cell[k] for k in keys if k in cell}}


def recognize(frame_bgr, metadata, unity_crop_bgr=None):
    """Use the exact archived ROI as reader input; return full-photo geometry."""
    started = perf_counter()
    if not isinstance(frame_bgr, np.ndarray) or frame_bgr.dtype != np.uint8 or frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3:
        return _retry("invalid_image", "A valid camera photo is required.", started)
    h, w = frame_bgr.shape[:2]
    if not isinstance(metadata, dict) or metadata.get("imageWidth") != w or metadata.get("imageHeight") != h or metadata.get("pixelOrigin") != "top-left":
        return _retry("invalid_metadata", "Camera photo coordinates do not match.", started)
    region = metadata.get("recognitionRegion")
    if not isinstance(region, dict) or region.get("pixelOrigin") != "top-left" or region.get("source") != "recent_hand_approach":
        return _retry("missing_word_region", "Photo received. Center the whole word in the box and capture again.", started)
    coords = [region.get(k) for k in ("x", "y", "width", "height")]
    if any(type(v) is not int for v in coords):
        return _retry("invalid_word_region", "The saved word region is invalid. Capture again.", started)
    x, y, rw, rh = coords
    if x < 0 or y < 0 or rw < 24 or rh < 24 or x + rw > w or y + rh > h:
        return _retry("invalid_word_region", "The whole word must fit inside the camera photo.", started)
    crop_started = perf_counter()
    from capture_crop_archive import target_crop_pixels, InvalidCaptureBox
    try:
        crop, capture_mask = target_crop_pixels(frame_bgr, metadata, unity_crop_bgr)
    except InvalidCaptureBox:
        return _retry("invalid_capture_box", "The complete yellow capture box could not be projected safely. Capture again.", started)
    except ValueError:
        return _retry("invalid_unity_crop", "The first crop does not contain the complete target.", started)
    crop_ms = (perf_counter() - crop_started) * 1000
    local_region = copy.deepcopy(region)
    local_region.update(x=0, y=0)
    reading_quad = local_region.get("readingQuad")
    if (isinstance(reading_quad, list) and len(reading_quad) == 4 and
            all(isinstance(p, dict) and all(type(p.get(k)) in (int, float) for k in ("x", "y"))
                for p in reading_quad)):
        local_region["readingQuad"] = [dict(p, x=p["x"] - x, y=p["y"] - y) for p in reading_quad]
    result = _recognize_region(crop, local_region, started)

    def full_image_points(points):
        return [[float(px) + x, float(py) + y] for px, py in points]

    # Assign fresh lists: quad and face_polygon can reference the same list.
    for cell in result.get("cells", []):
        if "quad" in cell:
            cell["quad"] = full_image_points(cell["quad"])
    payload = result.get("result")
    if payload is not None:
        for cell in payload.get("cells", []):
            for field in ("face_polygon", "dot_centers_image"):
                if field in cell:
                    cell[field] = full_image_points(cell[field])
        payload["image_size"] = [w, h]
    center = result.get("groupCenterPixel")
    if center is not None:
        result["groupCenterPixel"] = {"x": center["x"] + x, "y": center["y"] + y}
    diagnostics = result.get("diagnostics", {})
    for geometry in (diagnostics, diagnostics.get("geometry", {})):
        if "unassignedBoundaries" in geometry:
            geometry["unassignedBoundaries"] = [
                [a + x, b + y, c + x, d + y] for a, b, c, d in geometry["unassignedBoundaries"]]
        for partition in geometry.get("edgeFallback", {}).get("partitions", []):
            partition["quad"] = full_image_points(partition["quad"])
    direct_unity_crop = unity_crop_bgr is not None and all(metadata["unityCrop"][key] == region[key] for key in ("x", "y", "width", "height"))
    result["recognitionInput"] = {
        "type": "target_crop", "width": rw, "height": rh,
        "originInFullImage": {"x": x, "y": y}, "fullImageSize": [w, h],
        "pixelFormat": "BGR8", "resized": False,
        "coordinateSystem": "crop_pixels_top_left",
        "pixelSource": "unity_target_png" if direct_unity_crop else "unity_stage1_png" if unity_crop_bgr is not None else "original_jpeg_or_png",
        "cropStages": 1 if direct_unity_crop or unity_crop_bgr is None else 2,
        "captureMask": capture_mask,
    }
    if unity_crop_bgr is not None and not direct_unity_crop:
        result["recognitionInput"]["stage1Size"] = [unity_crop_bgr.shape[1], unity_crop_bgr.shape[0]]
    result["pythonCropMs"] = round(crop_ms, 3)
    result["proposalTimingMs"] = round((perf_counter() - started) * 1000, 3)
    return result


def _recognize_region(frame_bgr, region, started):
    """All localization, orientation and upstream dot reads use crop pixels."""
    h, w = frame_bgr.shape[:2]
    try:
        localization = locate_faces(frame_bgr, region)
    except (ValueError, TypeError, cv2.error) as error:
        return _retry("localization_failed", "Block faces could not be located reliably. Capture again.", started,
                      diagnostics={"errorType": type(error).__name__})
    if localization.get("status") != "localized":
        return _retry(localization.get("reason", "uncertain_faces"), localization.get("message", "Separate and center the complete word, then capture again."),
                      started, diagnostics=localization.get("diagnostics", {}))
    located = localization.get("cells", [])
    # Final selection defense before any six-dot read. The locator normally
    # already filtered these, but no alternate locator may bypass the box.
    try:
        capture_quad = capture_selection_quad(region)
        if capture_quad is not None:
            located = [cell for cell in located if capture_face_membership(cell["quad"], capture_quad) == "inside"]
            if not located:
                return _retry("capture_box_empty", "No complete brick is fully inside the capture box. Move the word inside and capture again.",
                              started, diagnostics=localization.get("diagnostics", {}))
    except (ValueError, TypeError, KeyError, cv2.error):
        return _retry("invalid_reading_geometry", "The saved capture box is invalid. Capture again.", started)
    if not 1 <= len(located) <= 32:
        return _retry("invalid_face_count", "A complete row of one to 32 braille bricks is required.", started)
    # Unity may serialize unassigned legacy fields as null/empty strings.
    explicit_orientation = not (region.get("readingOrientation") in (None, "") and region.get("readingQuad") in (None, []))
    if explicit_orientation:
        try:
            oriented = _reading_order(located, region)
        except (ValueError, TypeError, KeyError, OverflowError, cv2.error, np.linalg.LinAlgError) as error:
            return _retry("invalid_reading_geometry", "Align one row with printed labels facing you, then capture again.",
                          started, diagnostics={"geometryIssue": str(error)})
    else:
        oriented = None
    cells = []
    try:
        for index, located_cell in enumerate(located):
            if explicit_orientation:
                cell, reason = _read_oriented_cell(frame_bgr, oriented[index])
                if cell is None:
                    return _retry("uncertain_dot_pattern", "A brick's dots could not be read. Move closer and capture the complete row again.",
                                  started, diagnostics={"uncertainSlot": index, "readerReason": reason})
                cell["orientationEvidence"] = {"source": "capture_box", "readingOrientation": "labels_toward_viewer"}
                cells.append(_compact(cell, index))
                continue
            q = np.asarray(located_cell["quad"], np.float32)
            candidates = located_cell.get("orientationCandidates", [q.tolist(), np.roll(q, -2, axis=0).tolist()])
            if len(candidates) != 2:
                return _retry("ambiguous_geometry", "A brick's orientation is ambiguous. Align the row and capture again.", started)
            accepted, reasons = [], []
            for orientation in candidates:
                cell, reason = _orientation(frame_bgr, orientation)
                reasons.append(reason)
                if cell is not None:
                    accepted.append((cell, np.asarray(orientation, np.float32)))
            if len(accepted) != 1:
                return _retry("ambiguous_orientation_or_pattern", "A brick's dot pattern or label end is uncertain. Use distinct braille-dot bricks and capture again.",
                              started, diagnostics={"uncertainSlot": index, "orientationReasons": reasons})
            cell, oriented_quad = accepted[0]
            if cell["kind"] == "punctuation" and cell["value"] != ".":
                return _retry("unsupported_symbol_pattern", "Use letters A-Z and periods only. Capture the complete word again.",
                              started, diagnostics={"uncertainSlot": index})
            cells.append(_compact(cell, index))
    except (ValueError, TypeError, KeyError, cv2.error, np.linalg.LinAlgError):
        return _retry("invalid_face_geometry", "The brick geometry is uncertain. Capture again.", started)
    letters = [c["value"] for c in cells]
    word = "".join(letters)
    if not any(c["kind"] == "letter" for c in cells):
        return _retry("unsupported_word", "Include at least one letter in the captured word.", started)
    points = np.concatenate([np.asarray(c["face_polygon"], np.float32) for c in cells])
    minimum, maximum = points.min(axis=0), points.max(axis=0)
    center = (minimum + maximum) * .5
    return {"status": "recognized", "word": word, "letters": letters, "display": word,
            "automaticBlockDetection": True, "recognitionPerformed": True, "coordinateSystem": "image_pixels_top_left",
            "method": METHOD, **PROVENANCE, "dictionarySource": DICTIONARY_SOURCE,
            "orderSource": "capture_box_left_to_right" if explicit_orientation else "image_left_to_right",
            "message": "Read braille dots: " + word + ".", "cells": [{"quad": c["face_polygon"]} for c in cells],
            "groupCenterPixel": {"x": float(center[0]), "y": float(center[1])},
            "result": {"method": "braille_dots", **PROVENANCE, "cells": cells, "image_size": [w, h]},
            "diagnostics": {"geometry": localization.get("diagnostics", {}), "completenessVerified": False,
                            "orientation": "capture_box_labels_toward_viewer" if explicit_orientation else "stud geometry excludes fourth-row label end",
                            "dotReaderWarpsPerOrientation": 1},
            "proposalTimingMs": round((perf_counter() - started) * 1000, 3)}
