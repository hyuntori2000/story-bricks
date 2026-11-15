"""Conservative color-code regions inside the frozen hand ROI.

This adds a geometric proposal stage, not OCR or a new letter classifier. All
letters still come from the unmodified LEGO_OCR ColorReader and user-approved
camera references. It never rearranges letters to obtain a known word. This
prototype assumes one separated, approximately horizontal row; touching bricks
of the same color, clutter and uncertain colors require a fresh capture.
"""
from time import perf_counter

import cv2
import numpy as np


def _retry(reason, started, candidates=None):
    return {
        "status": "retry_needed", "word": None, "letters": [], "display": "",
        "message": reason, "automaticBlockDetection": True,
        "recognitionPerformed": False, "coordinateSystem": "image_pixels_top_left",
        "method": "ROI color-region proposals + LEGO_OCR.ColorReader",
        "orderSource": "image_left_to_right", "cells": [],
        "candidateCenters": candidates or [],
        "proposalTimingMs": round((perf_counter() - started) * 1000, 3),
    }


def _ordered_box(rect):
    # minAreaRect corners ordered cyclically; rotate so the top-left starts.
    points = cv2.boxPoints(rect).astype(float)
    center = points.mean(axis=0)
    points = points[np.argsort(np.arctan2(points[:, 1] - center[1], points[:, 0] - center[0]))]
    start = int(np.argmin(points.sum(axis=1)))
    return np.roll(points, -start, axis=0)


def _components(mask, offset, shape, boundary_fragments):
    height, width = shape
    kernel = np.ones((5, 5), np.uint8)
    closed = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    found = []
    for contour in contours:
        area = cv2.contourArea(contour)
        rect = cv2.minAreaRect(contour)
        short, long = sorted(rect[1])
        x, y, w, h = cv2.boundingRect(contour)
        touches_boundary = x < 3 or y < 3 or x + w > width - 3 or y + h > height - 3
        # Keep evidence of a cropped face even when it is too narrow to classify.
        # Silently dropping it could turn FOX into FO. Large background regions
        # still fail these bounds; relevance to the detected row is checked later.
        if (touches_boundary and short >= 3 and long <= height * .45 and
                60 <= area <= width * height * .06 and
                long / max(short, 1) <= 20 and area / max(short * long, 1) >= .55):
            boundary_fragments.append((x + offset[0], y + offset[1], w, h))
        if (short < 12 or long > height * .45 or area < 200 or
                area > width * height * .06 or long / max(short, 1) > 3.7 or
                touches_boundary):
            continue
        fill = area / max(short * long, 1)
        if fill < .68:
            continue
        quad = _ordered_box(rect) + np.asarray(offset)
        if (np.min(quad[:, 0]) < offset[0] + 1 or np.min(quad[:, 1]) < offset[1] + 1 or
                np.max(quad[:, 0]) > offset[0] + width - 1 or np.max(quad[:, 1]) > offset[1] + height - 1):
            continue
        center = quad.mean(axis=0)
        found.append({"quad": quad, "center": center, "area": float(area),
                      "short": short, "long": long, "fill": fill})
    return found


def _intersection_fraction(a, b):
    overlap, _ = cv2.intersectConvexConvex(a["quad"].astype(np.float32), b["quad"].astype(np.float32))
    return float(overlap) / max(1, min(cv2.contourArea(a["quad"].astype(np.float32)),
                                     cv2.contourArea(b["quad"].astype(np.float32))))


def _deduplicate(candidates):
    # Keep the widest complete region among nested masks. Different locations
    # with the same color remain separate; no deduplication by letter occurs.
    selected = []
    for candidate in sorted(candidates, key=lambda c: c["area"], reverse=True):
        if not any(_intersection_fraction(candidate, other) > .55 for other in selected):
            selected.append(candidate)
    return selected


def _row_touches_boundary(candidates, fragments):
    """Reject cropped color evidence adjacent to, or crossing, the proposed row."""
    if not candidates:
        return False
    points = np.concatenate([c["quad"] for c in candidates])
    left, top = points.min(axis=0)
    right, bottom = points.max(axis=0)
    typical_width = float(np.median([np.ptp(c["quad"][:, 0]) for c in candidates]))
    typical_height = float(np.median([np.ptp(c["quad"][:, 1]) for c in candidates]))
    for x, y, width, height in fragments:
        overlap_y = max(0, min(bottom, y + height) - max(top, y))
        horizontal_gap = max(0, left - (x + width), x - right)
        if (overlap_y >= min(height, typical_height) * .4 and
                horizontal_gap <= typical_width * .65):
            return True
        # A top/bottom crop may leave only a thin strip outside the main faces.
        overlap_x = max(0, min(right, x + width) - max(left, x))
        vertical_gap = max(0, top - (y + height), y - bottom)
        if (overlap_x >= min(width, typical_width) * .4 and
                vertical_gap <= typical_height * .3):
            return True
    return False


def recognize(frame_bgr, metadata, color_session):
    """Return ColorSession-compatible recognition plus image-space geometry.

    Inputs are a BGR numpy frame, its frozen capture metadata and a read-only
    ColorSession with the approved camera dictionary. No file writes or network
    calls occur. Detected uncertainty returns retry_needed. This color-only
    proposal stage cannot guarantee completeness when an unknown low-saturation
    brick or a whole letter lies outside its search region. Successful
    result['cells'] are internal full-image rectangular crops.
    """
    started = perf_counter()
    if (not isinstance(frame_bgr, np.ndarray) or frame_bgr.dtype != np.uint8 or
            frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3):
        return _retry("A valid BGR camera frame is required. Capture again.", started)
    if not isinstance(metadata, dict):
        return _retry("Capture metadata is missing. Capture again.", started)
    height, width = frame_bgr.shape[:2]
    region = metadata.get("recognitionRegion", {})
    values = [region.get(key) for key in ("x", "y", "width", "height")]
    if (any(type(value) is not int for value in values) or
            metadata.get("imageWidth") != width or metadata.get("imageHeight") != height or
            width > 4096 or height > 4096 or not metadata.get("sessionId") or not metadata.get("frameId") or
            region.get("pixelOrigin") != "top-left" or region.get("source") != "recent_hand_approach"):
        return _retry("Capture and hand-region coordinates do not match. Capture again.", started)
    x, y, w, h = values
    retained = region.get("retainedFraction", 1.0)
    if (x < 0 or y < 0 or w < 24 or h < 24 or x + w > width or y + h > height or
            not isinstance(retained, (int, float)) or not np.isfinite(retained) or
            (region.get("clipped") and retained < .6)):
        return _retry("The hand region is mostly clipped or too small. Center the word and capture again.", started)
    if color_session is None or color_session.source != "quest_color_calibration":
        return _retry("Approved camera color references are required before automatic input.", started)
    dictionary = color_session.dictionary
    entries = dictionary.get("entries", [])
    if len(entries) < 2:
        return _retry("Camera color references are incomplete.", started)
    identity = {"sessionId": metadata["sessionId"], "frameId": metadata["frameId"]}
    if identity in color_session.calibration_frames:
        return _retry("This photo registered the colors. Capture a new photo for recognition.", started)

    roi = frame_bgr[y:y + h, x:x + w]
    lab = cv2.cvtColor(roi.astype(np.float32) / 255, cv2.COLOR_BGR2LAB)
    candidates = []
    boundary_fragments = []
    for entry in entries:
        prototype = np.asarray(entry["lab"], dtype=np.float32)
        radius = float(entry["acceptance_radius_delta_e76"])
        if prototype.shape != (3,) or not np.all(np.isfinite(prototype)) or not 0 < radius <= 30:
            return _retry("Camera color references are invalid.", started)
        distance = np.linalg.norm(lab - prototype, axis=2)
        # Tight masks separate yellow bricks from wood; wider masks recover
        # shadowed faces. Only compact, interior rectangular regions survive.
        for fraction in (.45, .6, .8, 1.0):
            mask = (distance < radius * fraction).astype(np.uint8) * 255
            candidates.extend(_components(mask, (x, y), (h, w), boundary_fragments))

    # Color-independent saturated regions also propose unknown-colored faces;
    # these must pass the SAME original ColorReader or the whole result retries.
    hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
    hue = hsv[:, :, 0].astype(np.float32)
    for center_hue in range(0, 180, 12):
        difference = np.abs(hue - center_hue)
        distance = np.minimum(difference, 180 - difference)
        mask = ((distance < 9) & (hsv[:, :, 1] > 80) & (hsv[:, :, 2] > 40)).astype(np.uint8) * 255
        candidates.extend(_components(mask, (x, y), (h, w), boundary_fragments))
    candidates = _deduplicate(candidates)
    centers = [{"x": round(float(c["center"][0]), 3), "y": round(float(c["center"][1]), 3)} for c in candidates]
    if not 1 <= len(candidates) <= 26:
        return _retry("No unambiguous brick row was found. Center the completed word and capture again.", started, centers)
    if _row_touches_boundary(candidates, boundary_fragments):
        return _retry("A possible letter is cut off by the hand region. Center the whole word and capture again.", started, centers)
    candidates.sort(key=lambda c: float(c["center"][0]))
    # One approximately horizontal row, with no overlapping projected faces or
    # unexplained large gaps. More than one plausible row is not auto-selected.
    if len(candidates) > 1:
        heights = np.asarray([np.ptp(c["quad"][:, 1]) for c in candidates])
        widths = np.asarray([np.ptp(c["quad"][:, 0]) for c in candidates])
        center_y = np.asarray([c["center"][1] for c in candidates])
        if np.ptp(center_y) > np.median(heights) * .55 or max(widths) > min(widths) * 2.1:
            return _retry("The region contains more than one row or inconsistent objects. Separate the word and retry.", started, centers)
        for left, right in zip(candidates, candidates[1:]):
            gap = float(np.min(right["quad"][:, 0]) - np.max(left["quad"][:, 0]))
            if gap > float(np.median(widths)) * .65 or _intersection_fraction(left, right) > .15:
                return _retry("The row has an unexplained gap or overlapping faces. Keep letters in a clear row and retry.", started, centers)

    cells = [{"quad": c["quad"].tolist()} for c in candidates]
    result = color_session.process(frame_bgr, metadata, {
        **identity, "action": "recognize", "dictionarySource": "quest_color_calibration", "cells": cells,
    })
    if result.get("status") != "recognized" or any(letter is None for letter in result.get("letters", [])):
        pending = _retry("At least one visible color is unknown or ambiguous. Use registered colors and capture again.", started, centers)
        pending["classification"] = result
        return pending
    points = np.concatenate([c["quad"] for c in candidates])
    minimum, maximum = points.min(axis=0), points.max(axis=0)
    group_center = (minimum + maximum) * .5
    result.update(
        automaticBlockDetection=True, recognitionPerformed=True,
        method="ROI color-region proposals + LEGO_OCR.ColorReader",
        orderSource="image_left_to_right", cells=cells,
        groupCenterPixel={"x": float(group_center[0]), "y": float(group_center[1])},
        groupBoxPixel={"x": float(minimum[0]), "y": float(minimum[1]),
                       "width": float(maximum[0] - minimum[0]), "height": float(maximum[1] - minimum[1])},
        candidateCenters=[{"x": float(c["center"][0]), "y": float(c["center"][1])} for c in candidates],
        proposalTimingMs=round((perf_counter() - started) * 1000, 3),
        message="Automatically read " + result["word"] + " from the completed row.",
    )
    return result
