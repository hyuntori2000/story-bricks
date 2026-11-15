#!/usr/bin/env python3
"""Local, single-frame Quest camera receiver; no OCR or cloud call by default."""

import argparse
from collections import OrderedDict
from datetime import datetime, timezone
from email import policy
from email.parser import BytesParser
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib
import json
import math
import os
from pathlib import Path
import re
import socket
import tempfile
import threading
from typing import Any, Callable, Dict, Optional, Tuple
from urllib.parse import parse_qs, urlsplit

# Bound allocations from compressed images before OpenCV initializes its decoder.
os.environ["OPENCV_IO_MAX_IMAGE_PIXELS"] = "16777216"
os.environ["OPENCV_IO_MAX_IMAGE_WIDTH"] = "4096"
os.environ["OPENCV_IO_MAX_IMAGE_HEIGHT"] = "4096"
import cv2
import numpy as np


MAX_REQUEST_BYTES = 16 * 1024 * 1024
MAX_METADATA_BYTES = 32 * 1024
MAX_RESULT_BYTES = 64 * 1024
MAX_IMAGE_SIDE = 4096
MAX_SEEN_FRAMES = 256
DEFAULT_OUTPUT = Path(__file__).resolve().parents[1] / "runtime" / "camera-captures"
ID_PATTERN = re.compile(r"^[A-Za-z0-9_.:-]{1,128}$")
CAPTURE_TIMESTAMP_PATTERN = re.compile(
    r"(?P<seconds>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})"
    r"(?:\.(?P<fraction>[0-9]{1,9}))?(?P<zone>Z|[+-][0-9]{2}:[0-9]{2})\Z"
)


class RequestError(Exception):
    def __init__(self, code: str, detail: str, http_status: int = 400):
        super().__init__(detail)
        self.code = code
        self.detail = detail
        self.http_status = http_status


def encode_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")


def reject_json_constant(value: str) -> None:
    raise ValueError("Non-finite JSON number: " + value)


def require_number(value: Any, label: str, minimum: Optional[float] = None) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise RequestError("invalid_metadata", label + " must be a finite number")
    if minimum is not None and value < minimum:
        raise RequestError("invalid_metadata", label + " is below the minimum")
    return float(value)


def require_vector(value: Any, keys: str, label: str) -> None:
    if not isinstance(value, dict):
        raise RequestError("invalid_metadata", label + " must be an object")
    for key in keys:
        require_number(value.get(key), label + "." + key)


def validate_metadata(metadata: Any) -> Dict[str, Any]:
    if not isinstance(metadata, dict):
        raise RequestError("invalid_metadata", "metadata must be a JSON object")
    if type(metadata.get("schemaVersion")) is not int or metadata["schemaVersion"] != 1:
        raise RequestError("invalid_metadata", "schemaVersion must be 1")
    for key in ("sessionId", "frameId"):
        if not isinstance(metadata.get(key), str) or not ID_PATTERN.fullmatch(metadata[key]):
            raise RequestError("invalid_metadata", key + " must be a short identifier")
    if type(metadata.get("frameRevision")) is not int or metadata["frameRevision"] < 0:
        raise RequestError("invalid_metadata", "frameRevision must be a nonnegative integer")
    for key in ("imageWidth", "imageHeight"):
        if type(metadata.get(key)) is not int or not 1 <= metadata[key] <= MAX_IMAGE_SIDE:
            raise RequestError("invalid_metadata", key + " must be between 1 and 4096")
    if metadata.get("cameraEye") not in ("left", "right"):
        raise RequestError("invalid_metadata", "cameraEye must be left or right")
    if metadata.get("pixelOrigin") not in ("top-left", "bottom-left"):
        raise RequestError("invalid_metadata", "pixelOrigin must be top-left or bottom-left")
    if not isinstance(metadata.get("orientation"), str) or not 1 <= len(metadata["orientation"]) <= 128:
        raise RequestError("invalid_metadata", "orientation must describe the submitted image")
    timestamp = metadata.get("captureTimestampUtc")
    try:
        match = CAPTURE_TIMESTAMP_PATTERN.fullmatch(timestamp) if isinstance(timestamp, str) else None
        if match is None:
            raise ValueError("Expected an ISO timestamp with timezone")
        # Unity DateTime.ToString("O") emits seven fractional digits. Python
        # 3.9's fromisoformat only accepts three or six; validate a microsecond
        # representation without changing the original timestamp in metadata.
        fraction = match.group("fraction")
        normalized = match.group("seconds")
        if fraction is not None:
            normalized += "." + fraction[:6].ljust(6, "0")
        normalized += "+00:00" if match.group("zone") == "Z" else match.group("zone")
        parsed_timestamp = datetime.fromisoformat(normalized)
        if parsed_timestamp.tzinfo is None:
            raise ValueError("Missing timezone")
    except (AttributeError, TypeError, ValueError):
        raise RequestError("invalid_metadata", "captureTimestampUtc must be an ISO timestamp with timezone")
    require_vector(metadata.get("cameraPositionWorld"), "xyz", "cameraPositionWorld")
    require_vector(metadata.get("cameraRotationWorld"), "xyzw", "cameraRotationWorld")
    intrinsics = metadata.get("intrinsics")
    if not isinstance(intrinsics, dict):
        raise RequestError("invalid_metadata", "intrinsics must be an object")
    for key in ("focalLength", "principalPoint", "sensorResolution"):
        require_vector(intrinsics.get(key), "xy", "intrinsics." + key)
    for key in ("focalLength", "sensorResolution"):
        if any(intrinsics[key][axis] <= 0 for axis in "xy"):
            raise RequestError("invalid_metadata", "intrinsics." + key + " must be positive")
    board = metadata.get("board")
    if not isinstance(board, dict):
        raise RequestError("invalid_metadata", "board must be an object")
    require_vector(board.get("position"), "xyz", "board.position")
    require_vector(board.get("rotation"), "xyzw", "board.rotation")
    for key in ("width", "height"):
        if require_number(board.get(key), "board." + key, 0) <= 0:
            raise RequestError("invalid_metadata", "board." + key + " must be positive")
    require_number(board.get("blockHeight"), "board.blockHeight", 0)
    trigger = metadata.get("trigger")
    if not isinstance(trigger, dict) or trigger.get("type") not in ("hands_off", "manual"):
        raise RequestError("invalid_metadata", "trigger.type must be hands_off or manual")
    require_number(trigger.get("clearSeconds"), "trigger.clearSeconds", 0)
    return metadata


def parse_multipart(content_type: str, body: bytes) -> Tuple[Dict[str, Any], bytes, Optional[bytes]]:
    if not content_type.lower().startswith("multipart/form-data;"):
        raise RequestError("invalid_content_type", "Use multipart/form-data with metadata and image fields", 415)
    if "\r" in content_type or "\n" in content_type or len(content_type) > 512:
        raise RequestError("invalid_content_type", "Invalid Content-Type header")
    try:
        header = ("Content-Type: " + content_type + "\r\nMIME-Version: 1.0\r\n\r\n").encode("ascii")
    except UnicodeEncodeError:
        raise RequestError("invalid_content_type", "Content-Type must be ASCII")
    message = BytesParser(policy=policy.default).parsebytes(header + body)
    if not message.is_multipart() or message.defects:
        raise RequestError("invalid_multipart", "Malformed multipart body")
    fields = {}
    for part in message.iter_parts():
        name = part.get_param("name", header="content-disposition")
        if part.get_content_disposition() != "form-data" or name not in ("metadata", "image", "unityCrop"):
            raise RequestError("invalid_multipart", "Only metadata, image and optional unityCrop fields are supported")
        if name in fields or part.is_multipart() or part.defects:
            raise RequestError("invalid_multipart", "Duplicate or malformed form field")
        fields[name] = part.get_payload(decode=True)
    if not {"metadata", "image"}.issubset(fields) or any(value is None for value in fields.values()):
        raise RequestError("invalid_multipart", "Both metadata and image fields are required")
    if len(fields["metadata"]) > MAX_METADATA_BYTES:
        raise RequestError("metadata_too_large", "metadata exceeds 32 KB", 413)
    try:
        metadata = json.loads(fields["metadata"].decode("utf-8"), parse_constant=reject_json_constant)
    except (UnicodeDecodeError, ValueError, RecursionError):
        raise RequestError("invalid_metadata", "metadata must contain valid finite JSON")
    return validate_metadata(metadata), fields["image"], fields.get("unityCrop")


def default_recognizer(frame_bgr: np.ndarray, metadata: Dict[str, Any]) -> Dict[str, Any]:
    """Replace through --recognizer module:function; no word is fabricated here."""
    return {
        "status": "capture_only",
        "detections": [],
        "message": "Frame received. No recognition algorithm is connected.",
        "inputShape": list(frame_bgr.shape),
        "inputDtype": str(frame_bgr.dtype),
        "inputChannels": "BGR",
    }


def load_recognizer(specification: Optional[str]) -> Callable:
    if not specification:
        return default_recognizer
    module_name, separator, function_name = specification.partition(":")
    if not separator or not module_name or not function_name:
        raise ValueError("--recognizer must be module:function")
    function = getattr(importlib.import_module(module_name), function_name)
    if not callable(function):
        raise ValueError("The configured recognizer is not callable")
    return function


def atomic_write(path: Path, content: bytes) -> None:
    descriptor, temporary_path = tempfile.mkstemp(prefix=".incoming-", dir=str(path.parent))
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
        os.replace(temporary_path, path)
    finally:
        if os.path.exists(temporary_path):
            os.unlink(temporary_path)


class FrameStore:
    def __init__(self, output_dir: Path, recognizer: Callable = default_recognizer,
                 archive_dir: Optional[Path] = None):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        os.chmod(self.output_dir, 0o700)
        # Also protects a newly created runtime directory in ordinary git add flows.
        atomic_write(self.output_dir / ".gitignore", b"*\n")
        self.archive_dir = Path(archive_dir).resolve() if archive_dir is not None else None
        if self.archive_dir is not None:
            self.archive_dir.mkdir(parents=True, exist_ok=True)
            os.chmod(self.archive_dir, 0o700)
            atomic_write(self.archive_dir / ".gitignore", b"*\n")
        self.recognizer = recognizer
        self.color_session = None
        if getattr(recognizer, "supports_color_regions", False):
            from lego_color_adapter import ColorSession
            self.color_session = ColorSession(self.output_dir / "color-calibration.json")
        self.lock = threading.Lock()
        self.received_count = 0
        self.analysis_revision = 0
        self.latest = None
        self.latest_image = None
        self.latest_content_type = None
        self.seen = OrderedDict()
        self.restore_latest()
        # The intermediate preview was removed; retain only original + final
        # recognition input. This runtime-only file was created by that preview.
        try:
            (self.output_dir / "latest-unity-crop.png").unlink(missing_ok=True)
        except OSError:
            pass

    def restore_latest(self):
        """Restore only a hash-verified original/metadata capture pair."""
        try:
            latest_path = self.output_dir / "latest.json"
            if not latest_path.is_file() or latest_path.stat().st_size > 256 * 1024:
                return
            latest = json.loads(latest_path.read_text(), parse_constant=reject_json_constant)
            metadata = validate_metadata(latest.get("metadata"))
            response = latest.get("response")
            if not isinstance(response, dict) or (response.get("sessionId"), response.get("frameId")) != (metadata["sessionId"], metadata["frameId"]):
                return
            name = latest.get("imageFile")
            if name not in ("latest.jpg", "latest.png"):
                return
            path = self.output_dir / name
            if not path.is_file() or path.stat().st_size > MAX_REQUEST_BYTES:
                return
            original = path.read_bytes()
            expected_hash = latest.get("imageSha256")
            # Earlier runtime envelopes have no hash. Their durable archive
            # sidecar binds the original bytes to the same metadata/identity.
            if not expected_hash and self.archive_dir is not None:
                archive = response.get("archive") or {}
                sidecar = archive.get("metadataFile")
                if isinstance(sidecar, str) and Path(sidecar).name == sidecar and sidecar.endswith(".json"):
                    record_path = self.archive_dir / sidecar
                    if record_path.is_file() and record_path.stat().st_size <= 256 * 1024:
                        record = json.loads(record_path.read_text(), parse_constant=reject_json_constant)
                        if record.get("metadata") == metadata and record.get("imageFile") == archive.get("imageFile"):
                            expected_hash = record.get("imageSha256")
            if not expected_hash or hashlib.sha256(original).hexdigest() != expected_hash:
                return
            frame = cv2.imdecode(np.frombuffer(original, dtype=np.uint8), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            if frame is None or frame.shape != (metadata["imageHeight"], metadata["imageWidth"], 3):
                return
            content_type = "image/png" if original.startswith(b"\x89PNG\r\n\x1a\n") else "image/jpeg" if original.startswith(b"\xff\xd8\xff") else None
            if content_type is None:
                return
            latest.pop("unityCropImage", None)
            response.pop("unityCropPreview", None)
            self.latest, self.latest_image, self.latest_content_type = latest, original, content_type
            count = response.get("receivedCount")
            self.received_count = count if type(count) is int and count >= 0 else 1
            revision = (latest.get("colorAnalysis") or {}).get("analysisRevision")
            self.analysis_revision = revision if type(revision) is int and revision >= 0 else 0
        except (OSError, ValueError, TypeError, AttributeError, RequestError, cv2.error):
            # A crash during atomic-file updates may leave unmatched files.
            # Waiting for the next capture is safer than displaying mixed frames.
            return

    def archive_capture(self, metadata, image_bytes, extension, content_type, signature, received_at, frame_bgr=None, unity_crop_bgr=None, crop_error=None):
        """Keep the original upload before recognition, including unsuccessful reads.

        The content signature makes filenames stable across upload retries and
        receiver restarts. Raw session/frame identifiers never become paths.
        """
        if self.archive_dir is None:
            return None
        timestamp = re.sub(r"[^0-9A-Za-z]", "", metadata["captureTimestampUtc"])
        image_name = timestamp + "_" + signature + "." + extension
        image_path = self.archive_dir / image_name
        metadata_path = image_path.with_suffix(".json")
        try:
            if not image_path.exists():
                atomic_write(image_path, image_bytes)
            if not metadata_path.exists():
                record = {
                    "metadata": metadata, "imageFile": image_name, "contentType": content_type,
                    "receivedAtUtc": received_at, "captureSignature": signature,
                    "imageSha256": hashlib.sha256(image_bytes).hexdigest(),
                }
            else:
                record = json.loads(metadata_path.read_text())
            crop = record.get("targetCrop")
            if crop_error:
                crop = {"status": "unavailable", "reason": crop_error}
                record["targetCrop"] = crop
                record["captureError"] = crop_error
                atomic_write(metadata_path, encode_json(record))
            elif crop is None or (crop.get("status") == "saved" and not (self.archive_dir / crop["imageFile"]).is_file()):
                from capture_crop_archive import archive_target_crop
                if frame_bgr is None:
                    frame_bgr = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8),
                                            cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
                crop = archive_target_crop(self.archive_dir, image_name, frame_bgr, metadata, unity_crop_bgr)
                record["targetCrop"] = crop
                atomic_write(metadata_path, encode_json(record))
        except (OSError, ValueError, cv2.error) as error:
            print("Capture archive failed: " + type(error).__name__, flush=True)
            raise RequestError("archive_failed", "Photo could not be saved to the Mac archive; check the receiver console", 507) from error
        return {"imageFile": image_name, "metadataFile": metadata_path.name, "crop": crop}

    def accept(self, metadata: Dict[str, Any], image_bytes: bytes, unity_crop_bytes: Optional[bytes] = None) -> Dict[str, Any]:
        metadata = validate_metadata(metadata)
        identity = (metadata["sessionId"], metadata["frameId"])
        canonical_metadata = json.dumps(metadata, sort_keys=True, allow_nan=False, separators=(",", ":")).encode("utf-8")
        signature_bytes = canonical_metadata + b"\x00" + image_bytes
        if unity_crop_bytes is not None:
            signature_bytes += b"\x00unityCrop\x00" + unity_crop_bytes
        signature = hashlib.sha256(signature_bytes).hexdigest()
        with self.lock:
            if identity in self.seen:
                old_signature, old_response = self.seen[identity]
                if signature != old_signature:
                    raise RequestError("frame_id_conflict", "This session/frame ID was already used for different content", 409)
                return dict(old_response, duplicate=True)
            if image_bytes.startswith(b"\xff\xd8\xff"):
                extension, content_type = "jpg", "image/jpeg"
            elif image_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
                extension, content_type = "png", "image/png"
            else:
                raise RequestError("invalid_image", "image must be a JPEG or PNG", 415)
            try:
                frame_bgr = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8),
                                         cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            except cv2.error:
                frame_bgr = None
            if frame_bgr is None or frame_bgr.dtype != np.uint8 or frame_bgr.ndim != 3 or frame_bgr.shape[2] != 3:
                raise RequestError("invalid_image", "The image could not be decoded as an 8-bit color image")
            height, width = frame_bgr.shape[:2]
            if (width, height) != (metadata["imageWidth"], metadata["imageHeight"]):
                raise RequestError("dimension_mismatch", "Decoded image dimensions do not match metadata")
            received_at = datetime.now(timezone.utc).isoformat()
            unity_crop_bgr = None
            unity_crop_metadata = metadata.get("unityCrop")
            # JsonUtility serializes a null inline class as its default zero
            # rectangle. That is an absent optional crop, not a missing PNG.
            empty_unity_crop = (isinstance(unity_crop_metadata, dict) and
                                all(type(unity_crop_metadata.get(k)) is int and unity_crop_metadata[k] == 0
                                    for k in ("x", "y", "width", "height")))
            if unity_crop_bytes is not None or (unity_crop_metadata is not None and not empty_unity_crop):
                try:
                    if unity_crop_bytes is None or not unity_crop_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
                        raise ValueError("A PNG unityCrop part and matching metadata are required")
                    unity_crop_bgr = cv2.imdecode(np.frombuffer(unity_crop_bytes, dtype=np.uint8),
                                                cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
                    from capture_crop_archive import validate_unity_crop
                    validate_unity_crop(metadata, unity_crop_bgr)
                except (ValueError, cv2.error) as error:
                    self.archive_capture(metadata, image_bytes, extension, content_type, signature, received_at,
                                         frame_bgr, crop_error="invalid_unity_crop")
                    print("Capture crop rejected: invalid_unity_crop", flush=True)
                    raise RequestError("invalid_unity_crop", str(error)) from error
                if not getattr(self.recognizer, "supports_unity_crop", False):
                    self.archive_capture(metadata, image_bytes, extension, content_type, signature, received_at,
                                         frame_bgr, crop_error="unity_crop_not_supported")
                    raise RequestError("unity_crop_not_supported", "This receiver does not accept two-stage recognition crops")
            archive = self.archive_capture(metadata, image_bytes, extension, content_type, signature, received_at, frame_bgr, unity_crop_bgr)
            try:
                result = (self.recognizer(frame_bgr, metadata, unity_crop_bgr=unity_crop_bgr)
                          if unity_crop_bgr is not None else self.recognizer(frame_bgr, metadata))
                result_bytes = encode_json(result)
                if not isinstance(result, dict) or len(result_bytes) > MAX_RESULT_BYTES:
                    raise ValueError("Recognizer must return a JSON object of at most 64 KB")
            except Exception as error:
                print("Recognition hook failed: " + type(error).__name__, flush=True)
                raise RequestError("recognizer_failed", "The recognition hook failed; check the receiver console", 502)
            count = self.received_count + 1
            response = {
                "accepted": True, "frameId": metadata["frameId"], "sessionId": metadata["sessionId"],
                "status": result.get("status", "processed"), "duplicate": False,
                "receivedCount": count, "receivedAtUtc": received_at, "recognition": result,
            }
            if archive is not None:
                response["archive"] = archive
            if unity_crop_bgr is not None:
                direct_target = all(metadata["unityCrop"][key] == metadata["recognitionRegion"][key]
                                    for key in ("x", "y", "width", "height"))
                response["cropStages"] = ({"count": 1, "target": dict(metadata["unityCrop"], performedBy="Unity", resized=False),
                                          "mask": "exact_capture_box" if result.get("recognitionInput", {}).get("captureMask", {}).get("applied") else "none"}
                    if direct_target else {"count": 2,
                        "stage1": dict(metadata["unityCrop"], performedBy="Unity", resized=False),
                        "stage2": dict(metadata["recognitionRegion"], performedBy="Python", resized=False)})
            image_name = "latest." + extension
            envelope = {"metadata": metadata, "response": response, "imageFile": image_name, "contentType": content_type,
                        "imageSha256": hashlib.sha256(image_bytes).hexdigest()}
            if result.get("automaticBlockDetection") is True:
                self.analysis_revision += 1
                envelope["colorAnalysis"] = {"frameId": metadata["frameId"], "sessionId": metadata["sessionId"],
                    "analysisRevision": self.analysis_revision, "recognition": result}
            atomic_write(self.output_dir / image_name, image_bytes)
            atomic_write(self.output_dir / "latest.json", encode_json(envelope))
            other_image = self.output_dir / ("latest.png" if extension == "jpg" else "latest.jpg")
            if other_image.exists():
                other_image.unlink()
            self.received_count = count
            self.latest = envelope
            self.latest_image = image_bytes
            self.latest_content_type = content_type
            self.seen[identity] = (signature, response)
            while len(self.seen) > MAX_SEEN_FRAMES:
                self.seen.popitem(last=False)
            print("FRAME {} | {} | {}x{} | {} | {}".format(
                count, metadata["frameId"], width, height, metadata["trigger"]["type"], response["status"]), flush=True)
            return response

    def snapshot(self) -> Dict[str, Any]:
        with self.lock:
            return {"receivedCount": self.received_count, "latest": self.latest}

    def image(self, session_id=None, frame_id=None) -> Tuple[Optional[bytes], Optional[str]]:
        with self.lock:
            if session_id is not None or frame_id is not None:
                self.require_latest(session_id, frame_id)
            return self.latest_image, self.latest_content_type

    def crop_image(self, session_id, frame_id) -> bytes:
        """Serve the exact saved target PNG for one specific capture."""
        with self.lock:
            self.require_latest(session_id, frame_id)
            crop = self.latest["response"].get("archive", {}).get("crop", {})
            if self.archive_dir is None or crop.get("status") != "saved":
                raise RequestError("crop_unavailable", "This capture has no saved target crop", 404)
            name = crop.get("imageFile")
            if not isinstance(name, str):
                raise RequestError("crop_unavailable", "The saved crop is unavailable", 404)
            path = (self.archive_dir / name).resolve()
            if not path.is_relative_to((self.archive_dir / "crops").resolve()) or path.suffix != ".png":
                raise RequestError("crop_unavailable", "The saved crop is unavailable", 404)
            try:
                return path.read_bytes()
            except OSError as error:
                raise RequestError("crop_unavailable", "The saved crop could not be opened", 404) from error

    def require_latest(self, session_id, frame_id):
        if self.latest is None:
            raise RequestError("no_frame", "Capture a Quest frame first", 409)
        metadata = self.latest["metadata"]
        if (session_id, frame_id) != (metadata["sessionId"], metadata["frameId"]):
            raise RequestError("stale_frame", "A newer frame arrived. Select the corners on the new frame", 409)

    def analyze_regions(self, request):
        if not isinstance(request, dict):
            raise RequestError("invalid_regions", "Send a JSON object")
        with self.lock:
            if self.color_session is None:
                raise RequestError("color_disabled", "Start with --recognizer lego_color_adapter:recognize", 409)
            self.require_latest(request.get("sessionId"), request.get("frameId"))
            frame = cv2.imdecode(np.frombuffer(self.latest_image, dtype=np.uint8),
                                 cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            try:
                result = self.color_session.process(frame, self.latest["metadata"], request)
            except (ValueError, TypeError, KeyError, cv2.error) as error:
                raise RequestError("invalid_regions", str(error))
            self.analysis_revision += 1
            response = {"frameId": request["frameId"], "sessionId": request["sessionId"],
                        "analysisRevision": self.analysis_revision, "recognition": result}
            self.latest["colorAnalysis"] = response
            atomic_write(self.output_dir / "latest.json", encode_json(self.latest))
            print("COLOR {} | {} | word={} | {}".format(request["frameId"], result["status"],
                  result.get("word") or "-", result["message"]), flush=True)
            return response

    def color_result(self, session_id, frame_id):
        """Compact, frame-bound recognition for Unity; never emit calibration labels."""
        with self.lock:
            self.require_latest(session_id, frame_id)
            metadata = self.latest["metadata"]
            analysis = self.latest.get("colorAnalysis")
            response = {
                "ready": False, "sessionId": session_id, "frameId": frame_id,
                "status": "waiting_for_regions", "analysisRevision": 0,
                "message": "Select the brick faces on the Mac, then recognize colors.",
                "dictionarySource": "", "coordinateSystem": "image_pixels_top_left",
                "imageWidth": metadata["imageWidth"], "imageHeight": metadata["imageHeight"],
                "captureTimestampUtc": metadata["captureTimestampUtc"], "detections": [],
            }
            if not analysis:
                return response
            result = analysis["recognition"]
            response.update(status=result.get("status", "unavailable"),
                            analysisRevision=analysis.get("analysisRevision", 0),
                            message=result.get("message", ""),
                            dictionarySource=result.get("dictionarySource", ""))
            response["automaticBlockDetection"] = result.get("automaticBlockDetection") is True
            response["groupCenterPixel"] = result.get("groupCenterPixel")
            response["proposalTimingMs"] = result.get("proposalTimingMs", 0)
            response["recognitionMethod"] = result.get("result", {}).get("method", result.get("method", ""))
            for field in ("algorithm_id", "decoder_id"):
                if isinstance(result.get(field), str):
                    response[field] = result[field]
            if result.get("status") == "retry_needed":
                response["ready"] = True
                return response
            if result.get("status") not in ("recognized", "needs_review"):
                return response
            actual = result.get("result", {})
            if actual.get("method") not in ("example_color", "braille_dots", "ocr"):
                return response
            for cell in actual.get("cells", []):
                kind = cell.get("kind", "")
                candidate = cell.get("letter_candidate")
                valid_symbol = isinstance(candidate, str) and bool(re.fullmatch("[A-Z]", candidate))
                if actual["method"] in ("braille_dots", "ocr"):
                    if kind == "punctuation":
                        candidate = cell.get("punctuation_candidate")
                        valid_symbol = candidate == "."
                    else:
                        valid_symbol = kind == "letter" and valid_symbol
                else:
                    kind = "letter"
                accepted = cell.get("accepted") is True and valid_symbol
                response["detections"].append({
                    "letter": candidate if accepted else "?", "accepted": accepted, "kind": kind,
                    "quad": [{"x": float(point[0]), "y": float(point[1])} for point in cell["face_polygon"]],
                })
            response["ready"] = bool(response["detections"])
            return response


DASHBOARD = r"""<!doctype html>
<html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Quest Color Test</title>
<style>body{font:16px system-ui;margin:0;background:#101820;color:#eaf1f5}main{max-width:1100px;margin:28px auto;padding:0 24px}h1{font-size:28px}p{line-height:1.5;color:#bdcbd5}.status{padding:16px;background:#203543;border-radius:12px}canvas{display:none;max-width:100%;height:auto;margin:16px 0;background:#000;cursor:crosshair;touch-action:none}pre{padding:18px;background:#182832;border-radius:10px;overflow:auto;font-size:12px}.tag{color:#9eecd1}button,select{font:inherit;padding:8px 12px;border-radius:7px;border:0}button{background:#b5f2df;cursor:pointer;margin:4px}button:disabled{opacity:.4;cursor:default}select{max-width:100%}.tools{display:flex;align-items:center;gap:6px;flex-wrap:wrap}#result{white-space:pre-wrap;font-size:18px}ol{line-height:1.6}</style>
<main><h1>Quest Color Test</h1>
<p>One real camera frame per manual capture. Select brick faces here; Quest displays the recognized letters using the captured camera geometry and your surface reference. No automatic block detector or hands-off trigger is running.</p>
<div class="status" id="status">Waiting for the first Quest frame…</div>
<ol><li>In Quest, set the block-surface reference as instructed in the headset. Press the <b>right trigger</b> to capture.</li>
<li>On this frozen image, select each brick face in reading order. Click its four corners: <b>top-left → top-right → bottom-right → bottom-left</b>.</li>
<li>Click <b>Recognize colors</b>. Only the selected image regions are classified by the original LEGO_OCR algorithm.</li></ol>
<p>Recognition only: no expected letters are entered and no colors are calibrated. The original repository dictionary is selected by default. Existing saved camera references remain available as an explicit alternative. The order you select faces determines the reported word order.</p>
<div class="tools"><label>Color references <select id="dictionary"><option value="repository_example">Original repository</option><option value="quest_color_calibration">Saved camera references</option></select></label>
<button id="undo" disabled>Undo point / face</button><button id="clear" disabled>Clear faces</button><button id="recognize" disabled>Recognize colors</button></div>
<p id="selection">No frame yet.</p><canvas id="frame" aria-label="Select the four corners of each brick in the captured image"></canvas>
<div class="status" id="result">No recognition yet. Original repository color references selected; calibration is disabled.</div>
<p class="tag">Regions belong to this capture only. A new frame clears all selections. Images stay on this Mac; only the latest capture is retained.</p>
<details><summary>Latest metadata and recognition details</summary><pre id="metadata">No frame received.</pre></details>
</main>
<script>
const el=id=>document.getElementById(id), canvas=el('frame'), ctx=canvas.getContext('2d');
let revision='', active=null, picture=null, cells=[], points=[], loading=false, busy=false, analysisKey='';
function controls(){const ready=!!active&&!loading&&!busy; ['undo','clear'].forEach(id=>el(id).disabled=!ready); el('recognize').disabled=!ready||!cells.length||!!points.length; el('dictionary').disabled=busy;}
function draw(){if(!picture)return;ctx.clearRect(0,0,canvas.width,canvas.height);ctx.drawImage(picture,0,0);const scale=canvas.width/800;ctx.lineWidth=3*scale;ctx.font=(20*scale)+'px system-ui';function polygon(q,color,closed){ctx.strokeStyle=color;ctx.fillStyle=color;ctx.beginPath();q.forEach((p,i)=>i?ctx.lineTo(...p):ctx.moveTo(...p));if(closed)ctx.closePath();ctx.stroke();q.forEach(p=>{ctx.beginPath();ctx.arc(p[0],p[1],4*scale,0,Math.PI*2);ctx.fill();});}cells.forEach((c,i)=>{polygon(c.quad,'#57ffc6',true);ctx.fillText(String(i+1),c.quad[0][0],Math.max(20*scale,c.quad[0][1]-8*scale));});if(points.length)polygon(points,'#ffd06b',false);el('selection').textContent=cells.length+' face(s) selected · '+points.length+'/4 corners on next face · order: selected first to last';controls();}
canvas.addEventListener('pointerdown',event=>{if(!active||loading||busy)return;const rect=canvas.getBoundingClientRect();points.push([Math.max(0,Math.min(canvas.width-1,(event.clientX-rect.left)*canvas.width/rect.width)),Math.max(0,Math.min(canvas.height-1,(event.clientY-rect.top)*canvas.height/rect.height))]);if(points.length===4){cells.push({quad:points});points=[];}draw();});
el('undo').onclick=()=>{if(points.length)points.pop();else if(cells.length)cells.pop();draw();};
el('clear').onclick=()=>{cells=[];points=[];draw();};
async function submit(action){if(!active||loading||busy)return;busy=true;controls();const identity={...active};try{const response=await fetch('/color/analyze',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...identity,action,cells,dictionarySource:el('dictionary').value})});const data=await response.json();if(!response.ok)throw Error(data.detail||data.error);if(active&&active.frameId===identity.frameId&&active.sessionId===identity.sessionId){const r=data.recognition;el('result').textContent=(r.display?'Result: '+r.display+' · ':'')+r.status+'\n'+r.message+'\nDictionary: '+r.dictionarySource;el('metadata').textContent=JSON.stringify(data,null,2);}}catch(error){el('result').textContent=error.message;}finally{busy=false;controls();}}
el('recognize').onclick=()=>submit('recognize');
async function refresh(){try{const response=await fetch('/latest.json',{cache:'no-store'});const state=await response.json();if(!state.latest){el('status').textContent='Receiver ready — waiting for the first Quest frame.';return;}const latest=state.latest,meta=latest.metadata,reply=latest.response;const key=meta.sessionId+':'+meta.frameId+':'+state.receivedCount;el('status').textContent='Received '+state.receivedCount+' frame(s) · '+meta.imageWidth+' × '+meta.imageHeight+' · '+reply.status+' · '+new Date(reply.receivedAtUtc).toLocaleTimeString();if(revision!==key){revision=key;loading=true;active=null;cells=[];points=[];controls();el('result').textContent='New frame: select each brick face before recognition.';const img=new Image();img.onload=()=>{if(revision!==key)return;picture=img;canvas.width=img.naturalWidth;canvas.height=img.naturalHeight;canvas.style.display='block';active={sessionId:meta.sessionId,frameId:meta.frameId};loading=false;draw();};img.onerror=()=>{if(revision===key){loading=false;revision='';controls();}};img.src='/latest.jpg?sessionId='+encodeURIComponent(meta.sessionId)+'&frameId='+encodeURIComponent(meta.frameId);el('metadata').textContent=JSON.stringify(latest,null,2);}const newAnalysis=JSON.stringify(latest.colorAnalysis||null);if(newAnalysis!==analysisKey){analysisKey=newAnalysis;if(latest.colorAnalysis){const r=latest.colorAnalysis.recognition;el('result').textContent=(r.display?'Result: '+r.display+' · ':'')+r.status+'\n'+r.message+'\nDictionary: '+r.dictionarySource;el('metadata').textContent=JSON.stringify(latest,null,2);}}}catch(_){el('status').textContent='Receiver unavailable. Start the color receiver on this Mac.';}}
refresh();setInterval(refresh,1000);
</script></html>""".encode("utf-8")

class FrameHTTPServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address: Tuple[str, int], store: FrameStore):
        self.store = store
        super().__init__(address, FrameHandler)


class FrameHandler(BaseHTTPRequestHandler):
    server_version = "QuestFrameReceiver/1"

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(15)

    def log_message(self, format_string: str, *args: Any) -> None:
        # The frame event is logged once in FrameStore; dashboard polling stays quiet.
        pass

    def send_body(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def send_json(self, status: int, value: Any) -> None:
        self.send_body(status, encode_json(value), "application/json; charset=utf-8")

    def do_GET(self) -> None:
        path = urlsplit(self.path).path
        if path == "/":
            self.send_body(200, DASHBOARD, "text/html; charset=utf-8")
        elif path == "/health":
            self.send_json(200, {"status": "ready", "service": "quest-camera-frame-receiver", "schemaVersion": 1,
                                 "recognizer": "capture_only" if self.server.store.recognizer is default_recognizer else "custom",
                                 "recognitionMethod": getattr(self.server.store.recognizer, "recognition_method", "legacy_or_custom"),
                                 "recognitionInput": getattr(self.server.store.recognizer, "recognition_input", "full_frame"),
                                 "recognitionProvenance": getattr(self.server.store.recognizer, "recognition_provenance", {}),
                                 "recognitionHistoryUrl": "/recognition-history.json",
                                 "archiveEnabled": self.server.store.archive_dir is not None,
                                 "cropArchiveEnabled": self.server.store.archive_dir is not None,
                                 "archiveDirectory": str(self.server.store.archive_dir) if self.server.store.archive_dir else None,
                                 "receivedCount": self.server.store.snapshot()["receivedCount"]})
        elif path == "/latest.json":
            self.send_json(200, self.server.store.snapshot())
        elif path == "/recognition-history.json":
            query = parse_qs(urlsplit(self.path).query)
            try:
                limit = max(1, min(int(query.get("limit", ["200"])[0]), 1000))
            except ValueError:
                self.send_json(400, {"error": "invalid_limit", "detail": "limit must be an integer"})
                return
            # Do not call FrameStore.snapshot or acquire the capture lock here.
            history = getattr(self.server.store, "recognition_history", None)
            self.send_json(200, history.snapshot(limit) if history is not None else
                           {"schemaVersion": 1, "records": [], "retainedCount": 0})
        elif path == "/latest-crop.png":
            query = parse_qs(urlsplit(self.path).query)
            try:
                image_bytes = self.server.store.crop_image(query.get("sessionId", [None])[0], query.get("frameId", [None])[0])
                self.send_body(200, image_bytes, "image/png")
            except RequestError as error:
                self.send_json(error.http_status, {"error": error.code, "detail": error.detail})
        elif path in ("/color/result", "/recognition/result"):
            query = parse_qs(urlsplit(self.path).query)
            try:
                result = self.server.store.color_result(query.get("sessionId", [None])[0], query.get("frameId", [None])[0])
                self.send_json(200, result)
            except RequestError as error:
                self.send_json(error.http_status, {"ready": False, "error": error.code, "detail": error.detail})
        elif path in ("/latest.jpg", "/latest.png"):
            query = parse_qs(urlsplit(self.path).query)
            try:
                image_bytes, content_type = self.server.store.image(query.get("sessionId", [None])[0], query.get("frameId", [None])[0])
            except RequestError as error:
                self.send_json(error.http_status, {"error": error.code, "detail": error.detail})
                return
            if image_bytes is None:
                self.send_json(404, {"error": "no_frame", "detail": "No frame has been received in this process"})
            else:
                self.send_body(200, image_bytes, content_type)
        else:
            self.send_json(404, {"error": "not_found"})

    def do_POST(self) -> None:
        path = urlsplit(self.path).path
        if path not in ("/frames", "/color/analyze"):
            self.send_json(404, {"accepted": False, "error": "not_found"})
            return
        try:
            if self.headers.get("Transfer-Encoding"):
                raise RequestError("unsupported_transfer", "Send a Content-Length, not chunked encoding", 411)
            try:
                length = int(self.headers.get("Content-Length", ""))
            except ValueError:
                raise RequestError("missing_length", "A valid Content-Length is required", 411)
            if not 0 < length <= MAX_REQUEST_BYTES:
                raise RequestError("request_too_large", "Request must be nonempty and no larger than 16 MB", 413)
            body = self.rfile.read(length)
            if len(body) != length:
                raise RequestError("incomplete_request", "Request body ended early")
            if path == "/color/analyze":
                if length > MAX_METADATA_BYTES:
                    raise RequestError("regions_too_large", "Region request exceeds 32 KB", 413)
                if self.headers.get("Content-Type", "").split(";", 1)[0].strip() != "application/json":
                    raise RequestError("invalid_content_type", "Use application/json", 415)
                origin = self.headers.get("Origin")
                if origin and urlsplit(origin).netloc != self.headers.get("Host"):
                    raise RequestError("invalid_origin", "Use this receiver's own dashboard", 403)
                try:
                    request = json.loads(body.decode("utf-8"), parse_constant=reject_json_constant)
                except (ValueError, UnicodeDecodeError):
                    raise RequestError("invalid_json", "Send valid finite JSON")
                if isinstance(request, dict) and request.get("action") == "calibrate":
                    raise RequestError("calibration_disabled", "Color calibration is disabled in this recognition test", 403)
                self.send_json(200, self.server.store.analyze_regions(request))
                return
            metadata, image_bytes, unity_crop_bytes = parse_multipart(self.headers.get("Content-Type", ""), body)
            self.send_json(200, self.server.store.accept(metadata, image_bytes, unity_crop_bytes))
        except RequestError as error:
            self.send_json(error.http_status, {"accepted": False, "error": error.code, "detail": error.detail})
        except (socket.timeout, TimeoutError):
            self.send_json(408, {"accepted": False, "error": "request_timeout"})
        except Exception as error:
            print("Receiver failed: " + type(error).__name__, flush=True)
            self.send_json(500, {"accepted": False, "error": "receiver_failed", "detail": "Check the receiver console"})


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8767)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--recognizer", help="Optional local module:function accepting (frame_bgr, metadata)")
    args = parser.parse_args()
    store = FrameStore(args.output_dir, load_recognizer(args.recognizer))
    server = FrameHTTPServer(("127.0.0.1", args.port), store)
    print("Quest camera receiver ready: http://127.0.0.1:{}/".format(server.server_port), flush=True)
    print("Latest frame only: " + str(store.output_dir), flush=True)
    print("Recognition: " + (args.recognizer or "capture only; no OCR or cloud requests"), flush=True)
    print("Quest USB transport requires: adb reverse tcp:{0} tcp:{0}".format(server.server_port), flush=True)
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
