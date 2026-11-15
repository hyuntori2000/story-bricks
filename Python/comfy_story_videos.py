"""Owned, durable story films using the supplied Comfy shot workflow.

Only completed local story records may start a film. Paid shot submissions have
durable uncertainty markers; a restart never repeats an ambiguous submission.
"""
from __future__ import annotations

import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import queue
import re
import secrets
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import wave
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urljoin, urlsplit
from urllib.request import Request

from comfy_word_images import (ComfyCardProvider, ImageError, MAX_IMAGE_BYTES,
                               MAX_JSON_BYTES, _public_https, _PublicHTTPSConnection,
                               validate_png)
from workspace_storage import (StorageError, owner_from, session_directory,
                               read_json, write_json, encode, now, WORKSPACE_DIRECTORIES)

MAX_SHOT_BYTES = 64 * 1024 * 1024
MAX_FILM_BYTES = 128 * 1024 * 1024
VIDEO_ROUTE = re.compile(r"^/story-videos/([0-9a-f]{32})\.mp4$")
IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]{1,96}$")
WORKFLOW = Path(__file__).resolve().parent / "workflows/comfy/shot_api.json"
FILM_STYLE = (
    "Soft hand-painted watercolour picture-book animation on textured paper, visible paper grain and "
    "pigment pooling, thin loose pencil-brown outlines, muted warm palette, simple rounded shapes, no "
    "hard shadows. Fixed side-on camera, gentle slow motion, 16:9, 24 fps. "
    "Use the reference images exactly for every character and object; keep them identical in every shot. "
)


class VideoError(ImageError):
    pass


def _identifier(value, name):
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise VideoError("invalid_video_input", name + " is invalid.")
    return value


def _input(value):
    if not isinstance(value, dict):
        raise VideoError("invalid_video_input", "Provide an owned story video request.")
    try:
        owner = owner_from(value, required=True)
    except StorageError as error:
        raise VideoError(error.code, error.message, error.status) from None
    identity = owner.fields()
    for key in ("sessionId", "requestId"):
        identity[key] = _identifier(value.get(key), key)
    for key, limit in (("storyText", 12000), ("storyAudioPath", 512)):
        item = value.get(key)
        if not isinstance(item, str) or not item.strip() or len(item) > limit:
            raise VideoError("invalid_video_input", key + " is missing or too long.")
        identity[key] = item
    return owner, identity


def _mp4(data, maximum=MAX_SHOT_BYTES):
    if not isinstance(data, bytes) or not 24 <= len(data) <= maximum or data[4:8] != b"ftyp":
        raise VideoError("invalid_video", "The video provider did not return a bounded MP4 file.", 502)
    return data


def find_ffmpeg(explicit=None):
    candidate = explicit or shutil.which("ffmpeg")
    if not candidate:
        try:
            import imageio_ffmpeg
            candidate = imageio_ffmpeg.get_ffmpeg_exe()
        except (ImportError, RuntimeError, OSError):
            return None
    path = Path(candidate).expanduser().resolve()
    return str(path) if path.is_file() and os.access(path, os.X_OK) else None


class ComfyVideoProvider(ComfyCardProvider):
    """Reuse the card client's restricted API origin and bounded JSON transport."""
    def __repr__(self):
        return "ComfyVideoProvider(api_key=<redacted>)"

    def upload(self, path):
        with Path(path).open("rb") as stream:
            data = stream.read(MAX_IMAGE_BYTES + 1)
        validate_png(data)
        boundary = "story-card-" + secrets.token_hex(16)
        name = "story-card-" + hashlib.sha256(data).hexdigest()[:24] + ".png"
        body = ("--" + boundary + '\r\nContent-Disposition: form-data; name="type"\r\n\r\ninput\r\n' +
                "--" + boundary + '\r\nContent-Disposition: form-data; name="overwrite"\r\n\r\ntrue\r\n' +
                "--" + boundary + '\r\nContent-Disposition: form-data; name="image"; filename="' + name +
                '"\r\nContent-Type: image/png\r\n\r\n').encode() + data + ("\r\n--" + boundary + "--\r\n").encode()
        if not self.configured:
            raise VideoError("comfy_key_missing", "Story videos are not configured on the Mac backend.", 503)
        request = Request(self.base_url + "/api/upload/image", data=body,
                          headers={"X-API-Key": self._api_key, "Accept": "application/json",
                                   "Content-Type": "multipart/form-data; boundary=" + boundary})
        try:
            with self._opener.open(request, timeout=120) as response:
                payload = response.read(MAX_JSON_BYTES + 1)
            if len(payload) > MAX_JSON_BYTES:
                raise ValueError()
            result = json.loads(payload)
            filename, folder = result.get("name"), result.get("subfolder", "")
            if (not isinstance(filename, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,256}", filename) or
                    filename in (".", "..") or not isinstance(folder, str) or len(folder) > 256 or
                    folder.startswith("/") or "\\" in folder or any(x in (".", "..") for x in folder.split("/"))):
                raise ValueError()
            return (folder + "/" if folder else "") + filename
        except HTTPError as error:
            error.close()
            raise VideoError("video_card_upload", "Comfy rejected a reference-card upload.", 502) from None
        except (URLError, OSError, ValueError, TypeError, AttributeError):
            raise VideoError("video_card_upload", "A reference card could not be uploaded.", 502) from None

    def submit(self, shot):
        workflow = read_json(WORKFLOW)
        workflow = {key: value for key, value in workflow.items() if not key.startswith("_")}
        node = workflow["116"]["inputs"]
        node["model.prompt"], node["model.duration"], node["seed"] = shot["prompt"], shot["duration"], shot["seed"]
        refs = shot["refs"]
        if not 1 <= len(refs) <= 5:
            raise VideoError("video_cards_unavailable", "The film needs at least one generated reference card.", 409)
        for index, reference in enumerate(refs, 1):
            key = str(200 + index)
            workflow[key] = {"class_type": "LoadImage", "inputs": {"image": reference}}
            node["model.reference_images.image_" + str(index)] = [key, 0]
        workflow["117"]["inputs"]["filename_prefix"] = "video/storybricks_shot"
        result = self._json("/api/prompt", {"prompt": workflow, "extra_data": {"api_key_comfy_org": self._api_key}})
        prompt_id = result.get("prompt_id")
        if (result.get("error") or result.get("node_errors") or not isinstance(prompt_id, str) or
                not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", prompt_id)):
            raise VideoError("comfy_video_workflow", "Comfy did not accept the story-video workflow.", 502)
        return prompt_id

    @staticmethod
    def _video_output(history, prompt_id):
        item = history.get(prompt_id, history)
        outputs = item.get("outputs", {}) if isinstance(item, dict) else {}
        node = outputs.get("117", {}) if isinstance(outputs, dict) else {}
        files = node.get("images", []) if isinstance(node, dict) else []
        for item in files if isinstance(files, list) else []:
            if (isinstance(item, dict) and isinstance(item.get("filename"), str) and
                    item["filename"].lower().endswith(".mp4") and len(item["filename"]) <= 512 and
                    isinstance(item.get("subfolder", ""), str) and len(item.get("subfolder", "")) <= 512 and
                    item.get("type", "output") == "output"):
                return {"filename": item["filename"], "subfolder": item.get("subfolder", ""), "type": "output"}
        return None

    def _download(self, output):
        request = Request(self.base_url + "/api/view?" + urlencode(output),
                          headers={"X-API-Key": self._api_key, "Accept": "video/mp4"})
        redirect = None
        try:
            with self._opener.open(request, timeout=120) as response:
                return _mp4(response.read(MAX_SHOT_BYTES + 1))
        except HTTPError as error:
            if error.code in (301, 302, 303, 307, 308):
                redirect = error.headers.get("Location")
            error.close()
            if not redirect:
                raise VideoError("video_download", "Comfy could not download the completed shot.", 502) from None
        except (URLError, OSError, ValueError):
            raise VideoError("video_download", "The completed shot could not be downloaded.", 502) from None
        url = urljoin(request.full_url, redirect)
        for _ in range(3):
            _public_https(url)
            parts = urlsplit(url)
            connection = _PublicHTTPSConnection(parts.hostname, port=443, timeout=180,
                                                context=ssl.create_default_context())
            try:
                # Signed URLs never receive the Comfy credential.
                connection.request("GET", parts.path + (("?" + parts.query) if parts.query else ""),
                                   headers={"Accept": "video/mp4"})
                response = connection.getresponse()
                if response.status in (301, 302, 303, 307, 308):
                    location = response.getheader("Location")
                    if not location:
                        raise VideoError("video_download", "The signed video redirect has no destination.", 502)
                    url = urljoin(url, location)
                    continue
                if response.status != 200:
                    raise VideoError("video_download", "The signed video download failed.", 502)
                return _mp4(response.read(MAX_SHOT_BYTES + 1))
            except (OSError, http.client.HTTPException, ValueError):
                raise VideoError("video_download", "The signed video download could not complete.", 502) from None
            finally:
                connection.close()
        raise VideoError("video_download", "Too many signed-video redirects.", 502)

    def finish(self, prompt_id):
        if not isinstance(prompt_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", prompt_id):
            raise VideoError("video_prompt_id", "The saved video job identity is invalid.", 502)
        deadline = time.monotonic() + 1200
        while time.monotonic() < deadline:
            state = self._json("/api/job/" + quote(prompt_id, safe="") + "/status").get("status")
            if state == "success":
                break
            if state in ("error", "non_retryable_error", "lost", "cancelled"):
                raise VideoError("video_generation_failed", "Comfy could not finish an animation shot.", 502)
            time.sleep(3)
        else:
            raise VideoError("video_timeout", "The animation has not completed within twenty minutes.", 504)
        output = None
        for prefix in ("/api/jobs/", "/api/history_v2/", "/api/history/"):
            try:
                output = self._video_output(self._json(prefix + quote(prompt_id, safe="")), prompt_id)
            except ImageError as error:
                if error.code != "comfy_http":
                    raise
            if output:
                break
        if not output:
            raise VideoError("video_output_missing", "The completed job has no SaveVideo MP4 output.", 502)
        return self._download(output)


def plan_shots(story, words, cards):
    sentences = [part.strip() for part in re.findall(r"[^.!?]+[.!?]", story)]
    if not 2 <= len(sentences) <= 8 or " ".join(sentences) != " ".join(story.split()):
        raise VideoError("video_story_format", "The saved story cannot be divided into animation scenes.", 409)
    if not cards:
        raise VideoError("video_cards_unavailable", "No completed word card is available for this story.", 409)
    # Always submit two scenes. The seven-sentence story is covered in order as
    # four sentences followed by three, without dropping or repeating a passage.
    midpoint = (len(sentences) + 1) // 2
    shots = []
    for group in (sentences[:midpoint], sentences[midpoint:]):
        passage = " ".join(group)
        relevant = [card for card in cards if re.search(r"\b" + re.escape(card["word"].rstrip(".")) + r"\b", passage, re.I)]
        selected = []
        # Keep the first story ingredient visually consistent, then favor this scene.
        for card in cards[:1] + relevant + cards:
            if card["reference"] not in selected:
                selected.append(card["reference"])
        prompt = (FILM_STYLE + "Continuity context, not extra scenes: " + story + "\n" +
                  "Animate only this chronological scene: " + passage + "\n" +
                  "Preserve the story's characters, actions and details. Light pencil background with sparse colour. "
                  "No captions, written words, spoken dialogue or narration. Gentle natural sound only.")
        shots.append({"prompt": prompt, "duration": max(6, min(10, math.ceil(len(passage.split()) / 2.5))),
                      "refs": selected[:5], "seed": secrets.randbelow(2**31),
                      "promptId": "", "submitStarted": False, "status": "pending"})
    return shots


def _atomic_bytes(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".video-", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def narration_file(root, owner, identity):
    """Resolve only the matching story's session-owned, already generated WAV."""
    match = re.fullmatch(r"/audio/([0-9a-f]{32})\.wav", identity["storyAudioPath"])
    directory = session_directory(root, owner)
    path = (directory / "audio" / (match.group(1) + ".wav")).resolve() if match else None
    if path is None or directory not in path.parents or not path.is_file():
        raise VideoError("video_narration_missing", "The saved full-story narration is unavailable.", 409)
    narration_duration(path)
    return path


def narration_duration(path):
    try:
        with wave.open(str(path), "rb") as audio:
            duration = audio.getnframes() / audio.getframerate()
        if not .1 <= duration < 299:
            raise ValueError()
        return duration
    except (OSError, ValueError, wave.Error, EOFError, ZeroDivisionError):
        raise VideoError("video_narration_invalid", "The saved story narration is not a playable bounded WAV.", 409) from None


def narrate_video(ffmpeg, source, narration, destination):
    """Preserve the full film and full narration; hold the final frame if needed."""
    duration = narration_duration(narration)
    probe = subprocess.run([ffmpeg, "-nostdin", "-hide_banner", "-i", str(source)],
                           capture_output=True, timeout=30)
    match = re.search(rb"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)", probe.stderr)
    if not match:
        raise VideoError("video_duration_invalid", "The saved film duration is unavailable.", 502)
    movie_duration = int(match[1]) * 3600 + int(match[2]) * 60 + float(match[3])
    if not 0 < movie_duration < 299:
        raise VideoError("video_duration_invalid", "The saved film duration is outside playback limits.", 502)
    total = max(movie_duration, duration)
    # Keep the existing film ambience under the original voice. Padding changes
    # neither the shot order nor its playback speed and never loops the voice.
    filters = ("[0:v:0]tpad=stop_mode=clone:stop_duration=%.6f[v];"
               "[0:a:0]volume=0.2[ambience];"
               "[1:a:0][ambience]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,apad[a]") % (max(0, total - movie_duration) + .1)
    pending = destination.with_name(destination.stem + ".narrated.pending.mp4")
    try:
        result = subprocess.run([ffmpeg, "-nostdin", "-y", "-v", "error", "-i", str(source), "-i", str(narration),
                                 "-filter_complex", filters, "-map", "[v]", "-map", "[a]", "-t", "%.6f" % total,
                                 "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                                 "-c:a", "aac", "-ar", "48000", "-ac", "2", "-movflags", "+faststart",
                                 "-fs", str(MAX_FILM_BYTES), str(pending)], capture_output=True, timeout=180)
        if result.returncode or not pending.is_file() or not 24 <= pending.stat().st_size < MAX_FILM_BYTES:
            raise VideoError("video_narration_failed", "The full story could not be added to the film.", 502)
        with pending.open("rb") as stream:
            _mp4(stream.read(64))
        os.replace(pending, destination)
    finally:
        pending.unlink(missing_ok=True)


def join_videos(ffmpeg, paths, destination, narration=None):
    """Normalize each real shot, then concatenate compatible H.264/AAC MP4s."""
    normalized = []
    for index, source in enumerate(paths):
        probe = subprocess.run([ffmpeg, "-nostdin", "-hide_banner", "-i", str(source)],
                               capture_output=True, timeout=30)
        has_audio = b"Audio:" in probe.stderr
        target = destination.parent / ("normalized-%02d.mp4" % index)
        command = [ffmpeg, "-nostdin", "-y", "-v", "error", "-i", str(source)]
        if not has_audio:
            command += ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]
        command += ["-map", "0:v:0", "-map", "0:a:0" if has_audio else "1:a:0", "-shortest",
                    "-vf", "scale=854:480:force_original_aspect_ratio=decrease,pad=854:480:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=24",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", "-fs", str(MAX_SHOT_BYTES), str(target)]
        result = subprocess.run(command, capture_output=True, timeout=120)
        if result.returncode or not target.is_file() or target.stat().st_size >= MAX_SHOT_BYTES:
            raise VideoError("video_encode_failed", "A generated shot could not be prepared for VR playback.", 502)
        normalized.append(target)
    playlist = destination.parent / "concat.txt"
    playlist.write_text("".join("file '%s'\n" % path.name for path in normalized), encoding="utf-8")
    pending = destination.parent / "story.pending.mp4"
    result = subprocess.run([ffmpeg, "-nostdin", "-y", "-v", "error", "-f", "concat", "-safe", "1", "-i", str(playlist),
                             "-c", "copy", "-movflags", "+faststart", "-fs", str(MAX_FILM_BYTES), str(pending)],
                            capture_output=True, timeout=120)
    if result.returncode or not pending.is_file() or not 24 <= pending.stat().st_size < MAX_FILM_BYTES:
        raise VideoError("video_join_failed", "The generated shots could not be joined into a playable film.", 502)
    with pending.open("rb") as stream:
        _mp4(stream.read(64))
    if narration is not None:
        narrate_video(ffmpeg, pending, narration, destination)
        pending.unlink(missing_ok=True)
    else:
        os.replace(pending, destination)
    for path in normalized:
        path.unlink(missing_ok=True)
    playlist.unlink(missing_ok=True)


class StoryVideoJobs:
    """One film worker; new films have two scenes, saved older plans resume as-is."""
    card_wait_seconds = 120

    def __init__(self, provider, workspace_directory, *, ffmpeg=None, preview=True):
        self.provider, self.root = provider, Path(workspace_directory).resolve()
        self.ffmpeg, self.preview = find_ffmpeg(ffmpeg), bool(preview)
        self.lock, self.stop = threading.RLock(), threading.Event()
        self.queue = queue.Queue(maxsize=2)
        self.jobs, self.paths, self.active = {}, {}, set()
        self._recover()
        self.thread = threading.Thread(target=self._worker, name="comfy-story-video", daemon=True)
        self.thread.start()

    def health(self):
        with self.lock:
            return {"storyVideosConfigured": bool(self.provider and self.provider.configured and self.ffmpeg),
                    "storyVideosProvider": "Comfy Cloud", "storyVideosWorkers": 1,
                    "storyVideosPending": len(self.active), "storyVideosQueueLimit": 2,
                    "ffmpegAvailable": bool(self.ffmpeg)}

    def close(self):
        self.stop.set(); self.thread.join(timeout=.3)

    def _path(self, owner, job_id):
        directory = session_directory(self.root, owner)
        path = (directory / "videos" / job_id / "job.json").resolve()
        if directory not in path.parents:
            raise VideoError("invalid_video_path", "Video storage must stay within its owned session.")
        return path

    def _request_path(self, owner, session, request):
        return session_directory(self.root, owner) / "videos" / "requests" / (hashlib.sha256(encode([session, request])).hexdigest() + ".json")

    def _save(self, job):
        job["updatedAtUtc"] = now()
        write_json(self.paths[job["jobId"]], job)

    def _state(self, job, status, code="", error=""):
        job.update(status=status, code=code, error=error)
        if status in ("ready", "failed"):
            job["finishedAtUnix"] = time.time()
        self._save(job)
        print(json.dumps({"type": "story_video_job", "jobId": job["jobId"], "status": status, "code": code,
                          "shotsReady": sum(x["status"] == "ready" for x in job.get("shots", [])), "timestampUtc": now()}), flush=True)

    def _reply(self, job, request_id=None):
        result = {key: job[key] for key in ("workspaceId", "experienceSessionId", "sessionId", "requestId", "status", "videoPath", "error", "code")}
        result.update(shotsTotal=len(job.get("shots", [])), shotsReady=sum(x["status"] == "ready" for x in job.get("shots", [])),
                      elapsedSeconds=round(max(0, job.get("finishedAtUnix", time.time()) - job["createdAtUnix"]), 1),
                      previewOpened=bool(job.get("previewOpened")))
        if request_id is not None:
            result["requestId"] = request_id
        return result

    def _story(self, owner, identity):
        directory = session_directory(self.root, owner) / "story"
        for path in sorted(directory.glob("*.json"))[:1024]:
            if path.stat().st_size > 256 * 1024:
                continue
            record = read_json(path)
            result = record.get("result", {})
            if (record.get("operation") == "story" and record.get("status") == "completed" and record.get("httpStatus") == 200 and
                    all(record.get(key) == identity[key] for key in ("workspaceId", "experienceSessionId", "sessionId")) and
                    isinstance(result, dict) and result.get("text") == identity["storyText"] and
                    result.get("audioPath") == identity["storyAudioPath"]):
                return path.name, record
        raise VideoError("video_story_not_saved", "A matching completed story must be saved before animation can start.", 409)

    def submit(self, value):
        owner, identity = _input(value)
        with self.lock:
            alias = self._request_path(owner, identity["sessionId"], identity["requestId"])
            if alias.exists():
                old = read_json(alias)
                if old["input"] != identity:
                    raise VideoError("video_request_conflict", "This video requestId already has different story input.", 409)
                return self._reply(self.jobs[old["jobId"]], identity["requestId"])
            archive, record = self._story(owner, identity)
            job_id = hashlib.sha256(encode([owner.fields(), identity["sessionId"], archive])).hexdigest()[:32]
            if job_id in self.jobs:
                write_json(alias, {"input": identity, "jobId": job_id})
                return self._reply(self.jobs[job_id], identity["requestId"])
            if len(self.active) >= 2:
                raise VideoError("video_queue_full", "Two animations are already preparing. Retry this same request later.", 503)
            job = dict(identity, jobId=job_id, storyArchive=archive, status="queued", videoPath="", error="", code="",
                       createdAtUnix=time.time(), createdAtUtc=now(), previewOpened=False, previewAttempted=False, shots=[])
            self.jobs[job_id], self.paths[job_id] = job, self._path(owner, job_id)
            try:
                if not self.ffmpeg:
                    self._state(job, "failed", "video_ffmpeg_missing", "Install the local video encoder before creating an animation.")
                elif not self.provider or not self.provider.configured:
                    self._state(job, "failed", "comfy_key_missing", "Story videos are not configured on the Mac backend.")
                else:
                    self._save(job)
                write_json(alias, {"input": identity, "jobId": job_id})
            except OSError:
                raise VideoError("video_save_failed", "The animation request could not be saved. No generation was started.", 507) from None
            if job["status"] == "queued":
                self.active.add(job_id); self.queue.put_nowait(job_id)
            return self._reply(job)

    def status(self, query):
        value = {}
        for key in ("workspaceId", "experienceSessionId", "sessionId", "requestId"):
            item = query.get(key)
            if not isinstance(item, list) or len(item) != 1:
                raise VideoError("invalid_video_query", "Video ownership fields must occur exactly once.")
            value[key] = item[0]
        try:
            owner = owner_from(value, required=True)
        except StorageError as error:
            raise VideoError(error.code, error.message, error.status) from None
        _identifier(value["sessionId"], "sessionId"); _identifier(value["requestId"], "requestId")
        with self.lock:
            path = self._request_path(owner, value["sessionId"], value["requestId"])
            if not path.is_file():
                raise VideoError("video_not_found", "No animation exists for this owned request.", 404)
            record = read_json(path)
            if record["jobId"] not in self.jobs:
                raise VideoError("video_record_invalid", "The saved animation job is unavailable.", 409)
            return self._reply(self.jobs[record["jobId"]], value["requestId"])

    def video_file(self, route):
        match = VIDEO_ROUTE.fullmatch(route)
        with self.lock:
            job = self.jobs.get(match.group(1)) if match else None
            if not job or job["status"] != "ready":
                raise VideoError("video_not_ready", "The animation is not ready.", 404)
            owner, _ = _input(job)
            path = self._path(owner, job["jobId"]).parent / "story.mp4"
            if not path.is_file() or not 24 <= path.stat().st_size <= MAX_FILM_BYTES or path.resolve().parent != path.parent:
                raise VideoError("video_not_found", "The saved animation is unavailable.", 404)
            return path

    def _recover(self):
        for folder in WORKSPACE_DIRECTORIES.values():
            for path in (self.root / folder / "sessions").glob("*/videos/*/job.json"):
                try:
                    job = read_json(path); owner, _ = _input(job); job_id = job.get("jobId", "")
                    if not re.fullmatch(r"[0-9a-f]{32}", job_id) or self._path(owner, job_id) != path.resolve():
                        continue
                    if job.get("status") not in ("queued", "running", "ready", "failed") or not isinstance(job.get("shots"), list):
                        continue
                    self.jobs[job_id], self.paths[job_id] = job, path
                    if job["status"] in ("queued", "running"):
                        if any(shot.get("submitStarted") and not shot.get("promptId") for shot in job["shots"]):
                            self._state(job, "failed", "video_submit_interrupted", "A prior paid shot outcome is unknown; it will not be submitted again.")
                        elif len(self.active) >= 2:
                            self._state(job, "failed", "video_recovery_capacity", "Too many animations were pending to resume safely.")
                        else:
                            self.active.add(job_id); self.queue.put_nowait(job_id)
                except (OSError, ValueError, KeyError, TypeError, StorageError, ImageError):
                    continue

    def _cards(self, job, record):
        owner, _ = _input(job)
        directory = session_directory(self.root, owner) / "images"
        words = record.get("input", {}).get("context", {}).get("words", [])
        wanted = {word.get("groupId"): word for word in words if isinstance(word, dict)}
        deadline = time.monotonic() + self.card_wait_seconds
        while True:
            available, pending = {}, False
            for path in directory.glob("*.json"):
                card = read_json(path)
                word = wanted.get(card.get("groupId"))
                if (word is None or card.get("sessionId") != job["sessionId"] or
                        any(card.get(key) != job[key] for key in ("workspaceId", "experienceSessionId")) or
                        card.get("word") != word.get("word")):
                    continue
                if card.get("status") == "ready" and path.with_suffix(".png").is_file():
                    available[card["groupId"]] = path.with_suffix(".png")
                elif card.get("status") in ("queued", "running"):
                    pending = True
            if not pending or time.monotonic() >= deadline or self.stop.wait(1):
                break
        if self.stop.is_set():
            raise VideoError("video_stopping", "Animation preparation stopped before submission.", 503)
        cards = []
        for word in words:
            path = available.get(word.get("groupId"))
            if path:
                cards.append({"word": word["word"], "groupId": word["groupId"], "reference": self.provider.upload(path)})
        return words, cards

    def _finish_shot(self, job, index):
        shot = job["shots"][index]
        path = self.paths[job["jobId"]].parent / ("shot-%02d.mp4" % index)
        if shot["status"] == "ready" and path.is_file():
            return path
        data = self.provider.finish(shot["promptId"])
        _atomic_bytes(path, _mp4(data))
        with self.lock:
            shot["status"] = "ready"; self._save(job)
        return path

    def _open_preview(self, job, path):
        if not self.preview or sys.platform != "darwin" or job.get("previewAttempted"):
            return
        with self.lock:
            job["previewAttempted"] = True; self._save(job)
        try:
            result = subprocess.run(["/usr/bin/open", "-a", "QuickTime Player", str(path)],
                                    stdin=subprocess.DEVNULL, capture_output=True, timeout=15)
            with self.lock:
                job["previewOpened"] = result.returncode == 0
                job["previewCode"] = "" if result.returncode == 0 else "preview_open_failed"
                self._save(job)
        except (OSError, subprocess.SubprocessError):
            with self.lock:
                job["previewCode"] = "preview_open_failed"; self._save(job)

    def _run(self, job):
        if not self.ffmpeg:
            raise VideoError("video_ffmpeg_missing", "The local video encoder is unavailable.", 503)
        owner, identity = _input(job)
        archive, record = self._story(owner, identity)
        if archive != job["storyArchive"]:
            raise VideoError("video_story_changed", "The owned story archive changed.", 409)
        narration = narration_file(self.root, owner, identity)
        with self.lock:
            self._state(job, "running")
        if not job["shots"]:
            words, cards = self._cards(job, record)
            shots = plan_shots(identity["storyText"], words, cards)
            with self.lock:
                job["shots"] = shots; self._save(job)
        for shot in job["shots"]:
            if self.stop.is_set():
                return
            if shot.get("promptId"):
                continue
            if shot.get("submitStarted"):
                raise VideoError("video_submit_interrupted", "A prior paid shot outcome is unknown; it will not be submitted again.", 409)
            with self.lock:
                shot["submitStarted"] = True; self._save(job)
            prompt_id = self.provider.submit(shot)
            with self.lock:
                shot.update(promptId=prompt_id, status="running"); self._save(job)
        paths = [None] * len(job["shots"])
        # Daemon polls let the backend shut down promptly; saved prompt IDs resume
        # after restart, without starting another paid shot.
        completed = queue.Queue()
        def finish(index):
            try:
                completed.put((index, self._finish_shot(job, index), None))
            except Exception as error:
                completed.put((index, None, error))
        for index in range(len(paths)):
            threading.Thread(target=finish, args=(index,), name="comfy-video-shot-%d" % index, daemon=True).start()
        failure = None
        for _ in paths:
            while True:
                if self.stop.is_set():
                    return
                try:
                    index, path, error = completed.get(timeout=.2)
                    break
                except queue.Empty:
                    continue
            paths[index] = path
            failure = failure or error
        if failure:
            raise failure
        destination = self.paths[job["jobId"]].parent / "story.mp4"
        join_videos(self.ffmpeg, paths, destination, narration=narration)
        with self.lock:
            job["narrationEmbedded"] = True
            job["videoPath"] = "/story-videos/" + job["jobId"] + ".mp4"
            self._state(job, "ready")
        self._open_preview(job, destination)

    def _worker(self):
        while not self.stop.is_set():
            try:
                job_id = self.queue.get(timeout=.2)
            except queue.Empty:
                continue
            job = self.jobs[job_id]
            try:
                self._run(job)
            except ImageError as error:
                with self.lock:
                    self._state(job, "failed", error.code, error.message)
            except Exception:
                with self.lock:
                    self._state(job, "failed", "video_job_failed", "The animation could not complete; no paid shot was automatically resubmitted.")
            finally:
                with self.lock:
                    self.active.discard(job_id)
                self.queue.task_done()
