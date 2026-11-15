"""Supply missing trailing-dot proposals to the unchanged LEGO_OCR reader.

Automatic OCR can omit a small printed period entirely. Existing confident
letter labels supply a row and scale for a *candidate* label crop; the upstream
period detector still makes the decision. This does not recover brick faces or
support a standalone dot without letter anchors. Import after the adapter has
configured the pinned LEGO_OCR path.
"""

import cv2
import numpy as np

from ocr_reader import CalibratedOCRReader, LABEL_DESTINATION, LABEL_SIZE, MIN_OCR_SCORE


ALGORITHM_ID = "row_anchored_printed_period_bridge_v1"


def _inside(quad, selection):
    return all(cv2.pointPolygonTest(selection, (float(x), float(y)), True) > 1.0
               for x, y in quad)


def propose_period_tokens(crop, selection, tokens):
    """Return extra tokens and diagnostics; ambiguity produces no extra token.

    Shape tokens deliberately have score=None: a shape decision is not an OCR
    confidence score. Consumers must check their explicit accepted/source fields.
    """
    diagnostic = {"algorithmId": ALGORITHM_ID, "accepted": False,
                  "brickFaceConfirmed": False, "candidates": []}

    def abstain(reason):
        diagnostic["reason"] = reason
        return [], diagnostic

    if any(t.get("value") == "." for t in tokens):
        return abstain("period_already_read")
    selection = np.asarray(selection, np.float32)
    anchors = []
    for token in tokens:
        value, score = token.get("value"), token.get("score")
        if not isinstance(value, str) or len(value) != 1 or not "A" <= value <= "Z":
            continue
        if score is None or not np.isfinite(score) or score < MIN_OCR_SCORE:
            continue
        quad = np.asarray(token["quad"], np.float32)
        if _inside(quad, selection):
            anchors.append(quad)
    diagnostic["anchorCount"] = len(anchors)
    if len(anchors) < 2:
        return abstain("insufficient_letter_anchors")
    anchors.sort(key=lambda q: float(q[:, 0].mean()))
    centers = np.float32([q.mean(axis=0) for q in anchors])
    steps = np.diff(centers[:, 0])
    pitch = float(np.median(steps))
    label_height = float(np.median([np.ptp(q[:, 1]) for q in anchors]))
    if pitch < 24 or label_height <= 0 or not 1.0 <= pitch / label_height <= 4.0:
        return abstain("unreliable_label_scale")
    if np.max(np.abs(steps - pitch)) > pitch * .25:
        return abstain("irregular_label_spacing")
    slope, intercept = np.polyfit(centers[:, 0], centers[:, 1], 1)
    if abs(slope) > .35 or np.max(np.abs(centers[:, 1] - (slope * centers[:, 0] + intercept))) > label_height * .30:
        return abstain("uncertain_label_baseline")
    height = pitch * LABEL_SIZE[1] / LABEL_SIZE[0]
    last_x = float(centers[-1, 0])
    diagnostic.update(labelPitchPixels=pitch, rowSlope=float(slope), rowIntercept=float(intercept))

    # This mask only proposes components. The detector receives original color
    # pixels, never a binary image or an outside-blackened capture.
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    window = max(15, min(81, round(pitch * .60))) | 1
    mask = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                                 cv2.THRESH_BINARY_INV, window, 18)
    count, _, stats, centroids = cv2.connectedComponentsWithStats(mask, 8)
    proposals = []
    for index in range(1, count):
        x, y, width, component_height, area = (int(v) for v in stats[index])
        cx, cy = (float(v) for v in centroids[index])
        if not (3 <= area <= pitch * height * .06 and .5 <= width / component_height <= 1.8):
            continue
        # Only the immediately following label slot can supply the story-ending
        # period. Never search the stud rows or arbitrary gaps inside the word.
        if not last_x + pitch * .55 <= cx <= last_x + pitch * 1.45:
            continue
        row_y = float(slope * cx + intercept)
        if abs(cy - row_y) > height * .36:
            continue
        if any(cv2.pointPolygonTest(np.asarray(t["quad"], np.float32), (cx, cy), True) >= -pitch * .05
               for t in tokens):
            continue
        quad = np.float32([[cx - pitch / 2, row_y - height / 2],
                           [cx + pitch / 2, row_y - height / 2],
                           [cx + pitch / 2, row_y + height / 2],
                           [cx - pitch / 2, row_y + height / 2]])
        if _inside(quad, selection):
            proposals.append((cx, cy, quad, [x, y, width, component_height]))
    if len(proposals) > 8:
        return abstain("too_many_period_candidates")

    accepted = []
    for cx, cy, quad, component_box in proposals:
        matrix = cv2.getPerspectiveTransform(quad, LABEL_DESTINATION)
        patch = cv2.warpPerspective(crop, matrix, LABEL_SIZE, flags=cv2.INTER_CUBIC)
        reading = CalibratedOCRReader._interpret("", 0.0, patch)
        period = reading["period_diagnostics"]
        candidate = {"sourceComponentBox": component_box, "labelRegion": quad.tolist(),
                     "period": period}
        diagnostic["candidates"].append(candidate)
        if not reading["accepted"] or reading["value"] != ".":
            continue
        match = next(c for c in period["candidates"] if c["accepted"])
        px, py, pw, ph = match["box_xywh"]
        inverse = cv2.getPerspectiveTransform(LABEL_DESTINATION, quad)
        dot_quad = cv2.perspectiveTransform(np.float32([[
            [px - .5, py - .5], [px + pw + .5, py - .5],
            [px + pw + .5, py + ph + .5], [px - .5, py + ph + .5]]]), inverse)[0]
        # Tie the detector's accepted ink back to the proposed component.
        if np.linalg.norm(dot_quad.mean(axis=0) - [cx, cy]) > max(3.0, pitch * .12):
            candidate["rejectedByBridge"] = "different_ink_component"
            continue
        if cv2.contourArea(dot_quad, oriented=True) < 32 or not _inside(dot_quad, selection):
            candidate["rejectedByBridge"] = "period_geometry_too_small_or_outside"
            continue
        accepted.append({"raw": "", "quad": dot_quad, "score": None, "value": ".",
                         "source": "printed_period_shape", "accepted": True,
                         "period_diagnostics": period})
    if len(accepted) != 1:
        return abstain("ambiguous_printed_period" if accepted else "no_verified_trailing_period")
    diagnostic.update(accepted=True, reason="unique_trailing_printed_period")
    return accepted, diagnostic
