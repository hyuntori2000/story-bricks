"""Save a Quest-selected hand region. No letters, OCR, calibration, or cloud calls."""
import json
import math
import os
from pathlib import Path
import time

import cv2
import numpy as np

from camera_frame_server import atomic_write, encode_json


def validate_region(frame_bgr, metadata):
    if not isinstance(frame_bgr, np.ndarray) or frame_bgr.dtype != np.uint8 or frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3:
        raise ValueError("Expected a uint8 BGR camera frame")
    if metadata.get("pixelOrigin") != "top-left":
        raise ValueError("Only top-left image coordinates are supported")
    region = metadata.get("recognitionRegion")
    if not isinstance(region, dict) or region.get("pixelOrigin") != "top-left" or region.get("source") != "recent_hand_approach":
        raise ValueError("A hand-selected recognitionRegion is required")
    values = [region.get(key) for key in ("x", "y", "width", "height")]
    if any(type(value) is not int for value in values):
        raise ValueError("Region coordinates must be integers")
    x, y, width, height = values
    image_height, image_width = frame_bgr.shape[:2]
    if x < 0 or y < 0 or width < 1 or height < 1 or x + width > image_width or y + height > image_height:
        raise ValueError("Region lies outside the received full image")
    approach = metadata.get("handApproach")
    if not isinstance(approach, dict) or approach.get("hand") not in ("left", "right"):
        raise ValueError("Valid handApproach metadata is required")
    for key in ("positionWorld", "headPositionWorld"):
        point = approach.get(key)
        if not isinstance(point, dict) or any(type(point.get(axis)) not in (int, float) or not math.isfinite(point[axis]) for axis in "xyz"):
            raise ValueError("Finite hand/head positions are required")
    previous = -math.inf
    for key in ("sampleMonotonicSeconds", "liftStartedMonotonicSeconds", "confirmedMonotonicSeconds"):
        value = approach.get(key)
        if type(value) not in (int, float) or not math.isfinite(value) or value < 0 or value < previous:
            raise ValueError("Hand times must be finite, ordered monotonic seconds")
        previous = value
    for key in ("signedGapMeters", "surfaceHeightMeters", "headToPointDistanceMeters"):
        value = approach.get(key)
        if type(value) not in (int, float) or not math.isfinite(value):
            raise ValueError("Finite hand distance metadata is required")
    return x, y, width, height


def recognize(frame_bgr, metadata, output_dir=None):
    started = time.perf_counter()
    x, y, width, height = validate_region(frame_bgr, metadata)
    output = Path(output_dir) if output_dir is not None else Path(os.environ.get("TABLE_DEPTH_OUTPUT", str(Path(__file__).resolve().parents[1] / "runtime" / "table-depth-captures")))
    output.mkdir(parents=True, exist_ok=True)
    crop = frame_bgr[y:y + height, x:x + width]
    success, encoded = cv2.imencode(".jpg", crop, [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not success:
        raise ValueError("Could not encode the selected region")
    atomic_write(output / "latest-roi.jpg", encoded.tobytes())
    envelope = {"sessionId": metadata["sessionId"], "frameId": metadata["frameId"],
                "captureTimestampUtc": metadata["captureTimestampUtc"],
                "recognitionRegion": metadata["recognitionRegion"], "handApproach": metadata["handApproach"],
                "captureTimings": metadata.get("captureTimings"),
                "roiFile": "latest-roi.jpg", "fullImageFile": "latest.jpg",
                "recognitionPerformed": False, "opticalFocusChanged": False}
    atomic_write(output / "latest-roi.json", encode_json(envelope))
    elapsed = (time.perf_counter() - started) * 1000
    print("HAND_REGION " + json.dumps({"frameId": metadata["frameId"], "hand": metadata["handApproach"]["hand"],
          "positionWorld": metadata["handApproach"]["positionWorld"],
          "sampleMonotonicSeconds": metadata["handApproach"]["sampleMonotonicSeconds"],
          "headToPointDistanceMeters": metadata["handApproach"]["headToPointDistanceMeters"],
          "frameAgeAtConfirmation": metadata["handApproach"].get("frameAgeAtConfirmation"),
          "region": [x, y, width, height], "pythonCropMs": round(elapsed, 2)}, separators=(",", ":")), flush=True)
    return {"status": "region_received", "message": "Photo and hand-selected region saved; no block recognition.",
            "recognitionPerformed": False, "roiWidth": width, "roiHeight": height, "pythonCropMs": round(elapsed, 2)}
