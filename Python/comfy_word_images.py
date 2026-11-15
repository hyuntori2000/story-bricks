"""Bounded, durable Comfy word-card jobs. No dialogue, film or credential persistence.

A single backend process owns each workspace store. A paid submit is never retried
implicitly, including the ambiguous crash window before its prompt ID is saved.
"""
from __future__ import annotations

import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import queue
import re
import secrets
import socket
import ssl
import struct
import tempfile
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urljoin, urlsplit
from urllib.request import Request, HTTPRedirectHandler, build_opener
import zlib

from workspace_storage import (StorageError, owner_from, session_directory,
                               read_json, write_json, encode, now, WORKSPACE_DIRECTORIES)

MAX_IMAGE_BYTES = 8 * 1024 * 1024
MAX_JSON_BYTES = 2 * 1024 * 1024
IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]{1,96}$")
IMAGE_ROUTE = re.compile(r"^/word-images/([0-9a-f]{32})\.png$")
WORKFLOW = Path(__file__).resolve().parent / "workflows/comfy/card_api.json"
CARD_STYLE = (
    "children's picture-book watercolor illustration on textured paper, soft muted warm colors, "
    "thin loose pencil-brown outlines, simple rounded shapes. Single subject only, perfectly centered "
    "on a square canvas. The ENTIRE subject is fully visible from top to bottom and left to right - "
    "never cropped, nothing touching or cut by the image edges - with generous empty margin (about 15 "
    "percent) on every side. Plain solid white background, no ground, no shadow, no props, no text, "
    "no border. The whole object shown complete, in a clean front view. Subject: "
)


class ImageError(Exception):
    def __init__(self, code, message, status=400):
        super().__init__(message)
        self.code, self.message, self.status = code, message, status


def _identifier(value, name):
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise ImageError("invalid_image_input", name + " is invalid.")
    return value


def _input(value):
    if not isinstance(value, dict):
        raise ImageError("invalid_image_input", "Provide a word image object.")
    try:
        owner = owner_from(value, required=True)
    except StorageError as error:
        raise ImageError(error.code, error.message, error.status) from None
    identity = dict(owner.fields())
    for field in ("sessionId", "requestId", "groupId"):
        identity[field] = _identifier(value.get(field), field)
    word = value.get("word")
    if not isinstance(word, str) or not re.fullmatch(r"[A-Za-z.]{1,32}", word):
        raise ImageError("invalid_image_word", "A word image needs 1–32 letters or period markers.")
    identity["word"] = word
    return owner, identity


def validate_png(data):
    """Check bounded PNG framing, CRCs, dimensions and required image chunks."""
    if not isinstance(data, bytes) or not 57 <= len(data) <= MAX_IMAGE_BYTES or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ImageError("invalid_image", "Comfy did not return a bounded PNG image.", 502)
    offset, width, height, saw_idat, saw_end = 8, 0, 0, False, False
    first = True
    while offset + 12 <= len(data):
        length = struct.unpack(">I", data[offset:offset+4])[0]
        end = offset + 12 + length
        if end > len(data):
            break
        kind = data[offset+4:offset+8]
        body = data[offset+8:offset+8+length]
        crc = struct.unpack(">I", data[offset+8+length:end])[0]
        if zlib.crc32(kind + body) & 0xffffffff != crc:
            raise ImageError("invalid_image", "The PNG image failed its integrity check.", 502)
        if first:
            if kind != b"IHDR" or length != 13:
                raise ImageError("invalid_image", "The PNG image header is invalid.", 502)
            width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", body)
            allowed = {0: (1,2,4,8,16), 2: (8,16), 3: (1,2,4,8), 4: (8,16), 6: (8,16)}
            if not (1 <= width <= 4096 and 1 <= height <= 4096) or depth not in allowed.get(color, ()) or compression or filtering or interlace not in (0,1):
                raise ImageError("invalid_image", "The PNG format or dimensions are unsupported.", 502)
        elif kind == b"IHDR":
            raise ImageError("invalid_image", "The PNG contains duplicate image headers.", 502)
        if kind == b"IDAT" and length:
            saw_idat = True
        if kind == b"IEND":
            saw_end = length == 0 and end == len(data)
            break
        offset, first = end, False
    if not saw_idat or not saw_end:
        raise ImageError("invalid_image", "The PNG image is incomplete.", 502)
    return width, height


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        return None


def _public_https(url):
    parts = urlsplit(url)
    if parts.scheme != "https" or not parts.hostname or parts.username or parts.password or parts.port not in (None,443) or parts.fragment:
        raise ImageError("unsafe_image_url", "The image download URL is not an approved HTTPS target.", 502)
    try:
        addresses = socket.getaddrinfo(parts.hostname, 443, type=socket.SOCK_STREAM)
        if not addresses or any(not ipaddress.ip_address(item[4][0]).is_global for item in addresses):
            raise ValueError()
    except (OSError, ValueError):
        raise ImageError("unsafe_image_url", "The image download must use a public HTTPS host.", 502) from None
    return url


class _PublicHTTPSConnection(http.client.HTTPSConnection):
    """Pin a validated public IP while verifying TLS against the signed URL host."""
    def connect(self):
        addresses = socket.getaddrinfo(self.host, self.port, type=socket.SOCK_STREAM)
        if not addresses or any(not ipaddress.ip_address(item[4][0]).is_global for item in addresses):
            raise OSError("Download host is not public")
        # Connecting numerically avoids a second DNS lookup / rebinding window.
        address = addresses[0][4][0]
        sock = socket.create_connection((address, self.port), self.timeout, self.source_address)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


class ComfyCardProvider:
    def __init__(self, api_key, base_url="https://cloud.comfy.org"):
        if base_url.rstrip("/") != "https://cloud.comfy.org":
            raise ImageError("invalid_comfy_origin", "Comfy credentials may only be sent to cloud.comfy.org.")
        self._api_key = (api_key or "").strip()
        self.base_url = "https://cloud.comfy.org"
        self._opener = build_opener(_NoRedirect())

    @property
    def configured(self):
        return bool(self._api_key)

    def __repr__(self):
        return "ComfyCardProvider(api_key=<redacted>)"

    def _request(self, url, *, body=None, authenticated=False, maximum=MAX_JSON_BYTES, timeout=30):
        if authenticated and not url.startswith(self.base_url + "/api/"):
            raise ImageError("invalid_comfy_origin", "Comfy credential destination was rejected.", 502)
        headers = {"Accept": "application/json" if maximum == MAX_JSON_BYTES else "image/png"}
        if authenticated:
            if not self.configured:
                raise ImageError("comfy_key_missing", "Word images are not configured on the Mac backend.", 503)
            headers["X-API-Key"] = self._api_key
        if body is not None:
            headers["Content-Type"] = "application/json"
        if not authenticated:
            # Signed download has no API header, no implicit redirects, and a pinned public destination.
            _public_https(url)
            parts = urlsplit(url)
            connection = _PublicHTTPSConnection(parts.hostname, port=443, timeout=timeout,
                                                context=ssl.create_default_context())
            try:
                connection.request("GET", parts.path + (("?" + parts.query) if parts.query else ""), headers={"Accept": "image/png"})
                response = connection.getresponse()
                if response.status in (301,302,303,307,308):
                    location = response.getheader("Location")
                    if not location:
                        raise ImageError("comfy_redirect", "The image redirect has no destination.", 502)
                    return None, urljoin(url, location)
                if response.status != 200:
                    raise ImageError("comfy_download", "The signed image download failed.", 502)
                data = response.read(maximum + 1)
                if len(data) > maximum:
                    raise ImageError("image_response_size", "The image provider response exceeded its size limit.", 502)
                return data, None
            except (OSError, http.client.HTTPException, ValueError):
                raise ImageError("comfy_download", "The signed image download could not complete.", 502) from None
            finally:
                connection.close()
        request = Request(url, data=body, headers=headers)
        try:
            with self._opener.open(request, timeout=timeout) as response:
                data = response.read(maximum + 1)
                if len(data) > maximum:
                    raise ImageError("image_response_size", "The image provider response exceeded its size limit.", 502)
                return data, None
        except HTTPError as error:
            # Never read, log or persist provider exception bodies or request headers.
            if error.code in (301,302,303,307,308):
                location = error.headers.get("Location")
                error.close()
                if body is not None or not location:
                    raise ImageError("comfy_redirect", "The image provider redirected an unsupported request.", 502) from None
                return None, urljoin(url, location)
            status = error.code
            error.close()
            code = "comfy_access" if status in (401,403) else "comfy_limit" if status in (402,429) else "comfy_http"
            raise ImageError(code, "Comfy could not complete the image request (HTTP %d)." % status, 502) from None
        except (URLError, OSError, TimeoutError, ValueError):
            raise ImageError("comfy_unavailable", "Comfy could not be reached. The submit was not retried.", 502) from None

    def _json(self, path, body=None):
        data, redirect = self._request(self.base_url + path, body=encode(body) if body is not None else None, authenticated=True)
        if redirect:
            raise ImageError("comfy_redirect", "The Comfy API unexpectedly redirected the request.", 502)
        try:
            result = json.loads(data)
            if not isinstance(result, dict):
                raise ValueError()
            return result
        except (ValueError, TypeError, UnicodeError):
            raise ImageError("comfy_response", "Comfy returned an invalid response.", 502) from None

    def submit(self, word):
        workflow = read_json(WORKFLOW)
        workflow["104"]["inputs"]["prompt"] = CARD_STYLE + "one single " + word.rstrip(".").lower()
        workflow["104"]["inputs"]["seed"] = secrets.randbelow(2**31) + 1
        workflow["106"]["inputs"]["filename_prefix"] = "storybricks/card"
        result = self._json("/api/prompt", {"prompt": workflow, "extra_data": {"api_key_comfy_org": self._api_key}})
        prompt_id = result.get("prompt_id")
        if result.get("error") or result.get("node_errors") or not isinstance(prompt_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", prompt_id):
            raise ImageError("comfy_workflow", "Comfy did not accept the word-card workflow.", 502)
        return prompt_id

    @staticmethod
    def _output(history, prompt_id):
        # Never recursively walk arbitrary previews or extra_data. Only SaveImage 106.
        item = history.get(prompt_id, history)
        if not isinstance(item, dict):
            return None
        outputs = item.get("outputs", {})
        node = outputs.get("106", {}) if isinstance(outputs, dict) else {}
        images = node.get("images", []) if isinstance(node, dict) else []
        for candidate in images if isinstance(images, list) else []:
            if (isinstance(candidate, dict) and isinstance(candidate.get("filename"), str) and
                    candidate["filename"].lower().endswith(".png") and len(candidate["filename"]) <= 512 and
                    isinstance(candidate.get("subfolder", ""), str) and len(candidate.get("subfolder", "")) <= 512 and
                    candidate.get("type", "output") == "output"):
                return {"filename": candidate["filename"], "subfolder": candidate.get("subfolder", ""), "type": "output"}
        return None

    def finish(self, prompt_id):
        if not isinstance(prompt_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", prompt_id):
            raise ImageError("comfy_prompt_id", "The saved image job identity is invalid.", 502)
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            state = self._json("/api/job/" + quote(prompt_id, safe="") + "/status").get("status")
            if state == "success":
                break
            if state in ("error", "non_retryable_error", "lost", "cancelled"):
                raise ImageError("comfy_job_failed", "Comfy could not finish this word image.", 502)
            time.sleep(2)
        else:
            raise ImageError("comfy_timeout", "The image job did not finish within five minutes.", 504)
        output = None
        for path in ("/api/jobs/", "/api/history_v2/", "/api/history/"):
            try:
                output = self._output(self._json(path + quote(prompt_id, safe="")), prompt_id)
            except ImageError as error:
                if error.code != "comfy_http":
                    raise
            if output:
                break
        if not output:
            raise ImageError("comfy_output_missing", "The completed job has no final SaveImage card.", 502)
        data, redirect = self._request(self.base_url + "/api/view?" + urlencode(output), authenticated=True, maximum=MAX_IMAGE_BYTES)
        for _ in range(3):
            if not redirect:
                break
            data, redirect = self._request(_public_https(redirect), authenticated=False, maximum=MAX_IMAGE_BYTES)
        if redirect:
            raise ImageError("comfy_redirect", "Too many image-download redirects.", 502)
        validate_png(data)
        return data


class WordImageJobs:
    """One manager per backend; two daemon workers, no shared voice/SFX capacity."""
    def __init__(self, provider, workspace_directory, *, max_pending=16, workers=2):
        if not 1 <= workers <= 2 or not workers <= max_pending <= 64:
            raise ValueError("Use 1–2 workers and a bounded pending capacity.")
        self.provider, self.root = provider, Path(workspace_directory).resolve()
        self.lock = threading.RLock()
        self.queue = queue.Queue(maxsize=max_pending)
        self.limit = max_pending
        self.jobs, self.paths, self.active = {}, {}, set()
        self._logged_status = {}
        self.stop = threading.Event()
        self._recover()
        self.threads = [threading.Thread(target=self._worker, name="comfy-word-image-%d" % i, daemon=True) for i in range(workers)]
        for thread in self.threads:
            thread.start()

    def health(self):
        with self.lock:
            return {"wordImagesConfigured": bool(self.provider and self.provider.configured), "wordImagesProvider": "Comfy Cloud",
                    "wordImagesWorkers": len(self.threads), "wordImagesPending": len(self.active), "wordImagesQueueLimit": self.limit}

    def close(self):
        self.stop.set()
        for thread in self.threads:
            thread.join(timeout=.3)

    def _reply(self, job, request_id=None):
        fields = ("workspaceId", "experienceSessionId", "sessionId", "requestId", "groupId", "word", "status", "imagePath", "error", "code")
        result = {field: job[field] for field in fields}
        if request_id is not None:
            result["requestId"] = request_id
        return result

    def _save(self, job):
        job["updatedAtUtc"] = now()
        write_json(self.paths[job["jobId"]], job)
        state = (job["status"], job["code"])
        if self._logged_status.get(job["jobId"]) != state:
            self._logged_status[job["jobId"]] = state
            try:
                print(json.dumps({"type": "word_image_job", "jobId": job["jobId"],
                                  "status": state[0], "code": state[1], "timestampUtc": job["updatedAtUtc"]}), flush=True)
            except (OSError, ValueError):
                pass

    def _fail(self, job, code, message):
        job.update(status="failed", imagePath="", code=code, error=message)
        self._save(job)

    def _recover(self):
        for directory in WORKSPACE_DIRECTORIES.values():
            for path in (self.root / directory / "sessions").glob("*/images/*.json"):
                try:
                    job = read_json(path)
                    owner, identity = _input(job)
                    job_id = job.get("jobId")
                    if not isinstance(job_id, str) or not re.fullmatch(r"[0-9a-f]{32}", job_id) or path.resolve() != (session_directory(self.root, owner) / "images" / (job_id + ".json")).resolve():
                        continue
                    if job.get("status") not in ("queued", "running", "ready", "failed"):
                        continue
                    self.jobs[job_id], self.paths[job_id] = job, path
                    if job["status"] in ("queued", "running"):
                        if not job.get("promptId"):
                            self._fail(job, "image_interrupted", "The prior image submit outcome is unknown; it will not be submitted again.")
                        elif len(self.active) >= self.limit:
                            self._fail(job, "image_recovery_capacity", "Too many saved image jobs to resume safely.")
                        else:
                            self.active.add(job_id)
                            self.queue.put_nowait(job_id)
                except (ValueError, KeyError, OSError, ImageError, StorageError):
                    continue

    def _request_path(self, owner, session, request):
        digest = hashlib.sha256(encode([session, request])).hexdigest()
        return session_directory(self.root, owner) / "images" / "requests" / (digest + ".json")

    def submit(self, value):
        owner, identity = _input(value)
        with self.lock:
            request_path = self._request_path(owner, identity["sessionId"], identity["requestId"])
            if request_path.exists():
                previous = read_json(request_path)
                if previous["input"] != identity:
                    raise ImageError("image_request_conflict", "This image requestId was already used with different input.", 409)
                return self._reply(self.jobs[previous["jobId"]], identity["requestId"])
            job_id = hashlib.sha256(encode([owner.fields(), identity["sessionId"], identity["groupId"]])).hexdigest()[:32]
            job = self.jobs.get(job_id)
            if job is not None:
                if job["word"] != identity["word"]:
                    raise ImageError("image_group_conflict", "This captured group already has a different image word.", 409)
                write_json(request_path, {"input": identity, "jobId": job_id})
                return self._reply(job, identity["requestId"])
            if len(self.active) >= self.limit:
                raise ImageError("image_queue_full", "The word image queue is full. Try this same request later.", 503)
            job = dict(identity, jobId=job_id, status="queued", imagePath="", error="", code="", promptId="", createdAtUtc=now())
            self.jobs[job_id] = job
            self.paths[job_id] = session_directory(self.root, owner) / "images" / (job_id + ".json")
            try:
                if not re.search(r"[A-Za-z]", identity["word"]):
                    job.update(status="failed", code="image_finish_ignored", error="A standalone finish marker has no word image.")
                elif not self.provider or not self.provider.configured:
                    job.update(status="failed", code="comfy_key_missing", error="Word images are not configured on the Mac backend.")
                self._save(job)
                write_json(request_path, {"input": identity, "jobId": job_id})
            except OSError:
                # A saved group record still blocks a duplicate paid submit after restart.
                self.jobs.pop(job_id, None)
                raise ImageError("image_save_failed", "The image request could not be saved. No provider request was started.", 507) from None
            if job["status"] == "queued":
                self.active.add(job_id)
                self.queue.put_nowait(job_id)
            return self._reply(job)

    def status(self, query):
        value = {}
        for field in ("workspaceId", "experienceSessionId", "sessionId", "requestId"):
            part = query.get(field)
            if not isinstance(part, list) or len(part) != 1:
                raise ImageError("invalid_image_query", "Image ownership and request fields must occur exactly once.")
            value[field] = part[0]
        try:
            owner = owner_from(value, required=True)
        except StorageError as error:
            raise ImageError(error.code, error.message, error.status) from None
        _identifier(value["sessionId"], "sessionId"); _identifier(value["requestId"], "requestId")
        with self.lock:
            path = self._request_path(owner, value["sessionId"], value["requestId"])
            if not path.exists():
                raise ImageError("image_not_found", "No image job exists for this owned request.", 404)
            record = read_json(path)
            job = self.jobs.get(record["jobId"])
            if job is None:
                raise ImageError("image_record_invalid", "The saved image job could not be recovered.", 409)
            return self._reply(job, value["requestId"])

    def read_image(self, route):
        match = IMAGE_ROUTE.fullmatch(route)
        if not match:
            raise ImageError("image_not_found", "Image not found.", 404)
        with self.lock:
            job = self.jobs.get(match.group(1))
            if job is None or job["status"] != "ready":
                raise ImageError("image_not_ready", "This image is not ready.", 404)
            path = self.paths[job["jobId"]].with_suffix(".png")
        try:
            with path.open("rb") as stream:
                data = stream.read(MAX_IMAGE_BYTES + 1)
            validate_png(data)
            return data
        except OSError:
            raise ImageError("image_not_found", "The saved word image is unavailable.", 404) from None

    def _worker(self):
        while not self.stop.is_set():
            try:
                job_id = self.queue.get(timeout=.2)
            except queue.Empty:
                continue
            try:
                with self.lock:
                    job = self.jobs[job_id]
                    job["status"] = "running"
                    self._save(job)  # Durable uncertainty boundary before the paid submit.
                    prompt_id = job.get("promptId")
                if not prompt_id:
                    prompt_id = self.provider.submit(job["word"])
                    with self.lock:
                        job["promptId"] = prompt_id
                        self._save(job)  # Persist immediately; restart resumes polling, never submits again.
                image = self.provider.finish(prompt_id)
                width, height = validate_png(image)
                path = self.paths[job_id].with_suffix(".png")
                descriptor, temporary = tempfile.mkstemp(prefix=".image-", dir=str(path.parent))
                try:
                    with os.fdopen(descriptor, "wb") as stream:
                        stream.write(image); stream.flush(); os.fsync(stream.fileno())
                    os.replace(temporary, path)
                finally:
                    if os.path.exists(temporary):
                        os.unlink(temporary)
                with self.lock:
                    job.update(status="ready", imagePath="/word-images/" + job_id + ".png", error="", code="", width=width, height=height)
                    self._save(job)
            except ImageError as error:
                with self.lock:
                    try:
                        self._fail(job, error.code, error.message)
                    except OSError:
                        pass
            except Exception:
                # A fake/provider exception can carry credentials: persist no exception text.
                with self.lock:
                    try:
                        self._fail(job, "image_job_failed", "The word image job failed; it was not submitted again.")
                    except OSError:
                        pass
            finally:
                with self.lock:
                    self.active.discard(job_id)
                self.queue.task_done()
