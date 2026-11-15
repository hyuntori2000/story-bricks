"""Archive the exact hand-selected recognition crop without guessing a target."""

import hashlib
import os
from pathlib import Path
import tempfile

import cv2
import numpy as np


class InvalidCaptureBox(ValueError):
    """An explicit capture box cannot safely select native image pixels."""


def capture_box_mask(region):
    """Return an exact pixel-center polygon mask and crop-local quad.

    Legacy regions without a reading quad retain their rectangular input.
    Explicit physical capture boxes must be finite, convex and wholly inside
    their rectangular ROI. No corner ordering, warp or resize is invented.
    """
    quad = region.get("readingQuad")
    if quad is None or (isinstance(quad, list) and not quad):
        if region.get("readingOrientation") == "labels_toward_viewer":
            raise InvalidCaptureBox("The explicit capture box is missing its four corners")
        return None, None
    if not isinstance(quad, list) or len(quad) != 4:
        raise InvalidCaptureBox("The capture box requires four corners")
    if any(not isinstance(p, dict) or any(type(p.get(k)) not in (int, float) for k in ("x", "y")) for p in quad):
        raise InvalidCaptureBox("Capture box corners must be numeric points")
    values = [region.get(k) for k in ("x", "y", "width", "height")]
    if any(type(v) is not int for v in values) or values[2] < 1 or values[3] < 1:
        raise InvalidCaptureBox("The capture box ROI must have positive integer dimensions")
    x, y, width, height = values
    points = np.asarray([[p["x"] - x, p["y"] - y] for p in quad], dtype=np.float64)
    if not np.isfinite(points).all():
        raise InvalidCaptureBox("Capture box corners must be finite")
    if (np.any(points < -1e-5) or np.any(points[:, 0] > width + 1e-5) or
            np.any(points[:, 1] > height + 1e-5)):
        raise InvalidCaptureBox("The complete capture box must fit inside its ROI")
    edges = np.roll(points, -1, axis=0) - points
    following = np.roll(edges, -1, axis=0)
    turns = edges[:, 0] * following[:, 1] - edges[:, 1] * following[:, 0]
    if not (np.all(turns > 1e-7) or np.all(turns < -1e-7)):
        raise InvalidCaptureBox("Capture box corners must form one convex nondegenerate polygon")
    area_twice = np.sum(points[:, 0] * np.roll(points[:, 1], -1) - points[:, 1] * np.roll(points[:, 0], -1))
    if abs(area_twice) < 2.0:
        raise InvalidCaptureBox("The capture box has insufficient pixel area")
    direction = 1.0 if area_twice > 0 else -1.0
    xx = np.arange(width, dtype=np.float64)[None, :] + 0.5
    yy = np.arange(height, dtype=np.float64)[:, None] + 0.5
    inside = np.ones((height, width), dtype=bool)
    for point, edge in zip(points, edges):
        inside &= direction * (edge[0] * (yy - point[1]) - edge[1] * (xx - point[0])) >= -1e-7
    if not inside.any():
        raise InvalidCaptureBox("The capture box contains no native pixel centers")
    return inside.astype(np.uint8) * 255, points.tolist()


def target_crop_pixels(frame_bgr, metadata, unity_crop_bgr=None):
    """One shared native-pixel source for both saved PNG and recognition."""
    region = metadata["recognitionRegion"]
    x, y, width, height = (region[k] for k in ("x", "y", "width", "height"))
    if unity_crop_bgr is not None:
        origin_x, origin_y = validate_unity_crop(metadata, unity_crop_bgr)
        crop = unity_crop_bgr[y-origin_y:y-origin_y+height, x-origin_x:x-origin_x+width]
    else:
        crop = frame_bgr[y:y+height, x:x+width]
    if crop.shape != (height, width, 3):
        raise ValueError("The exact target crop is unavailable")
    crop = np.array(crop, dtype=np.uint8, order="C", copy=True)
    mask, local_quad = capture_box_mask(region)
    if mask is None:
        return crop, {"type": "legacy_rectangle", "applied": False}
    # Preserve every native color pixel. The reading quad selects complete
    # candidates by coordinates; it must not paint a border into the image.
    return crop, {"type": "rectangular_target_crop", "applied": False,
                  "outsidePixels": "original", "selection": "recognizer_geometry_inside_capture_box",
                  "quadInCrop": local_quad, "insidePixelCount": int(np.count_nonzero(mask)),
                  "resized": False, "warped": False}


def _unavailable(reason, metadata):
    result = {"status": "unavailable", "reason": reason}
    if isinstance(metadata, dict) and metadata.get("recognitionRegionIssue"):
        result["recognitionRegionIssue"] = metadata["recognitionRegionIssue"]
    return result


def _save_new(path, content):
    """Publish a complete file atomically; an existing capture is never replaced."""
    descriptor, temporary = tempfile.mkstemp(prefix=".incoming-", dir=str(path.parent))
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
        try:
            os.link(temporary, path)
        except FileExistsError:
            pass
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def validate_unity_crop(metadata, crop_bgr):
    """Validate optional stage-1 pixels and return their full-image origin.

    This does not change camera calibration or global recognition coordinates.
    The native PNG can differ slightly from the separately compressed JPEG.
    """
    region = metadata.get("unityCrop") if isinstance(metadata, dict) else None
    if not isinstance(region, dict):
        raise ValueError("Missing Unity crop metadata")
    if (region.get("pixelOrigin") != "top-left" or region.get("encoding") != "png" or
            region.get("source") != "unity_rgba_snapshot"):
        raise ValueError("Invalid Unity crop coordinate or encoding contract")
    values = [region.get(k) for k in ("x", "y", "width", "height")]
    if any(type(value) is not int for value in values):
        raise ValueError("Unity crop bounds must be integers")
    x, y, width, height = values
    fw, fh = metadata.get("imageWidth"), metadata.get("imageHeight")
    if (type(fw) is not int or type(fh) is not int or x < 0 or y < 0 or width < 1 or height < 1 or
            x + width > fw or y + height > fh):
        raise ValueError("Unity crop must fit inside the original")
    if (not isinstance(crop_bgr, np.ndarray) or crop_bgr.dtype != np.uint8 or
            crop_bgr.shape != (height, width, 3)):
        raise ValueError("Unity PNG dimensions must match its crop metadata")
    target = metadata.get("recognitionRegion")
    if not isinstance(target, dict) or target.get("pixelOrigin") != "top-left":
        raise ValueError("Unity crop requires a valid final target")
    target_values = [target.get(k) for k in ("x", "y", "width", "height")]
    if any(type(value) is not int for value in target_values):
        raise ValueError("Final target bounds must be integers")
    tx, ty, tw, th = target_values
    if tw < 1 or th < 1 or tx < x or ty < y or tx + tw > x + width or ty + th > y + height:
        raise ValueError("Unity crop must contain the complete final target")
    return x, y


def archive_target_crop(archive_dir, image_name, frame_bgr, metadata, unity_crop_bgr=None):
    """Save a lossless ROI PNG and return metadata for the original's sidecar.

    Coordinates are in the decoded full image. Missing or invalid regions do
    not create a crop; callers still retain the original photo. I/O and encoder
    failures propagate so the receiver cannot acknowledge an unsaved crop.
    """
    if (not isinstance(frame_bgr, np.ndarray) or frame_bgr.dtype != np.uint8 or
            frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3):
        return _unavailable("invalid_image", metadata)
    height, width = frame_bgr.shape[:2]
    if (not isinstance(metadata, dict) or metadata.get("pixelOrigin") != "top-left" or
            metadata.get("imageWidth") != width or metadata.get("imageHeight") != height):
        return _unavailable("invalid_image_coordinates", metadata)
    region = metadata.get("recognitionRegion")
    if not isinstance(region, dict):
        return _unavailable("missing_word_region", metadata)
    if region.get("pixelOrigin") != "top-left" or region.get("source") != "recent_hand_approach":
        return _unavailable("invalid_word_region_coordinates", metadata)
    values = [region.get(key) for key in ("x", "y", "width", "height")]
    if any(type(value) is not int for value in values):
        return _unavailable("invalid_word_region_coordinates", metadata)
    x, y, crop_width, crop_height = values
    if (x < 0 or y < 0 or crop_width < 1 or crop_height < 1 or
            x + crop_width > width or y + crop_height > height):
        return _unavailable("word_region_outside_image", metadata)
    if (not isinstance(image_name, str) or not image_name or
            Path(image_name).name != image_name or image_name in (".", "..") or
            "\\" in image_name or Path(image_name).suffix.lower() not in (".jpg", ".jpeg", ".png")):
        raise ValueError("image_name must be a JPEG/PNG archive basename")

    relative = Path("crops") / (Path(image_name).stem + "_crop.png")
    path = Path(archive_dir) / relative
    direct = unity_crop_bgr is not None and all(metadata["unityCrop"][key] == region[key] for key in ("x", "y", "width", "height"))
    source = "unity_target_png" if direct else "unity_stage1_png" if unity_crop_bgr is not None else "original_jpeg_or_png"
    try:
        crop, mask_info = target_crop_pixels(frame_bgr, metadata, unity_crop_bgr)
    except InvalidCaptureBox:
        return _unavailable("invalid_capture_box", metadata)
    try:
        encoded_ok, encoded = cv2.imencode(".png", crop)
    except cv2.error as error:
        raise ValueError("Could not encode the selected crop") from error
    if not encoded_ok:
        raise ValueError("Could not encode the selected crop")
    content = encoded.tobytes()
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        _save_new(path, content)
    # Read the persisted file so metadata always describes the saved bytes.
    saved_content = path.read_bytes()
    if saved_content != content:
        saved_crop = cv2.imdecode(np.frombuffer(saved_content, np.uint8), cv2.IMREAD_COLOR)
        if saved_crop is None or not np.array_equal(saved_crop, crop):
            raise ValueError("Existing crop does not match the original photo's target region")
    return {"status": "saved", "imageFile": relative.as_posix(),
            "width": crop_width, "height": crop_height, "sourceImageFile": image_name,
            "sourceRegion": dict(region), "pixelSource": source,
            "captureMask": mask_info,
            "sha256": hashlib.sha256(saved_content).hexdigest()}
