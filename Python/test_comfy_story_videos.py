"""Focused film integration checks with temporary media and fake providers only."""
import copy
import io
import json
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

import comfy_story_videos as video
from comfy_word_images import ImageError
from test_comfy_word_images import png
from workspace_storage import read_json, write_json

STORY = "A cat woke up. She wanted to fly. A kite came near. She held its string. They rose above trees. The cat smiled softly. They landed safely."
MP4 = b"\x00\x00\x00\x18ftypisom\x00\x00\x00\x00isomiso2" + b"\x00\x00\x00\x08mdat"


def request(request_id="r1", **changes):
    value = dict(workspaceId="demo", experienceSessionId="a" * 32, sessionId="conversation", requestId=request_id,
                 storyText=STORY, storyAudioPath="/workspace-audio/demo/" + "a" * 32 + "/story.wav")
    value.update(changes)
    return value


def query(value):
    return {key: [value[key]] for key in ("workspaceId", "experienceSessionId", "sessionId", "requestId")}


def fake_join(ffmpeg, paths, destination):
    assert len(paths) == 2 and all(path.is_file() for path in paths)
    destination.write_bytes(MP4)


class FakeProvider:
    configured = True
    def __init__(self, blocked=False, fail_submit=False):
        self.submits, self.finishes, self.uploads = [], [], []
        self.release = threading.Event()
        self.fail_submit = fail_submit
        if not blocked:
            self.release.set()
    def upload(self, path):
        self.uploads.append(str(path))
        return "story-card.png"
    def submit(self, shot):
        self.submits.append(copy.deepcopy(shot))
        if self.fail_submit:
            raise RuntimeError("PRIVATE_CREDENTIAL_AND_PROVIDER_BODY")
        return "prompt-" + str(len(self.submits))
    def finish(self, prompt_id):
        self.finishes.append(prompt_id)
        self.release.wait(3)
        return MP4


class FilmJobsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.session = self.root / "create-world/sessions" / ("a" * 32)
        self.managers, self.providers = [], []
        self.joiner = patch.object(video, "join_videos", side_effect=fake_join)
        self.joiner.start()
        value = request()
        record = dict(workspaceId=value["workspaceId"], experienceSessionId=value["experienceSessionId"], sessionId=value["sessionId"],
                      requestId="story-request", operation="story", status="completed", httpStatus=200,
                      result={"text": STORY, "audioPath": value["storyAudioPath"]},
                      input={"context": {"words": [{"word": "CAT", "groupId": "group-cat"}], "turns": []}})
        write_json(self.session / "story/saved.json", record)
        card = dict(workspaceId=value["workspaceId"], experienceSessionId=value["experienceSessionId"], sessionId=value["sessionId"],
                    groupId="group-cat", word="CAT", status="ready")
        write_json(self.session / "images/card.json", card)
        (self.session / "images/card.png").write_bytes(png())
    def tearDown(self):
        for provider in self.providers:
            provider.release.set()
        for manager in self.managers:
            manager.queue.join(); manager.close()
        self.joiner.stop()
        self.temp.cleanup()
    def manager(self, provider=None, **options):
        provider = provider or FakeProvider()
        self.providers.append(provider)
        manager = video.StoryVideoJobs(provider, self.root, ffmpeg="/usr/bin/true", preview=False, **options)
        self.managers.append(manager)
        return manager, provider
    def done(self, manager, value=None):
        for _ in range(250):
            result = manager.status(query(value or request()))
            if result["status"] in ("ready", "failed"):
                return result
            time.sleep(.01)
        self.fail("Film did not finish")

    def test_owned_story_deduplicates_requests_and_retains_ready_video(self):
        manager, provider = self.manager(FakeProvider(blocked=True))
        first = manager.submit(request())
        self.assertIn(first["status"], ("queued", "running"))
        manager.submit(request()); manager.submit(request("r2"))
        provider.release.set()
        result = self.done(manager)
        self.assertEqual(result["status"], "ready")
        self.assertEqual((result["shotsTotal"], result["shotsReady"]), (2, 2))
        self.assertEqual(len(provider.submits), 2)
        self.assertEqual(len(provider.uploads), 1)
        self.assertIsInstance(manager.video_file(result["videoPath"]), Path)
        self.assertEqual(manager.video_file(result["videoPath"]).read_bytes(), MP4)
        self.assertEqual(manager.status(query(request("r2")))["requestId"], "r2")

    def test_unarchived_or_foreign_story_is_rejected_before_provider(self):
        manager, provider = self.manager()
        for value in (request(storyText=STORY + " Changed."), request(storyAudioPath="/elsewhere.wav"),
                      request(experienceSessionId="b" * 32), request(sessionId="other")):
            with self.assertRaises(video.VideoError) as caught:
                manager.submit(value)
            self.assertEqual(caught.exception.code, "video_story_not_saved")
        self.assertEqual(provider.submits, [])

    def test_request_alias_cannot_change_its_input(self):
        manager, _ = self.manager()
        manager.submit(request())
        with self.assertRaises(video.VideoError) as caught:
            manager.submit(request(storyText=STORY + " Changed."))
        self.assertEqual(caught.exception.code, "video_request_conflict")
        with self.assertRaises(video.VideoError):
            manager.status({**query(request()), "requestId": ["r1", "r2"]})

    def test_missing_encoder_or_cards_does_not_start_paid_shots(self):
        with patch.object(video, "find_ffmpeg", return_value=None):
            manager, provider = self.manager()
        result = manager.submit(request())
        self.assertEqual(result["code"], "video_ffmpeg_missing")
        self.assertEqual(provider.submits, [])
        manager.close()
        # Separate owned archive avoids the deliberately durable first failure.
        (self.session / "images/card.png").unlink()
        for path in (self.session / "videos").glob("*/*"):
            if path.is_file():
                path.unlink()
        manager, provider = self.manager()
        manager.submit(request())
        self.assertEqual(self.done(manager)["code"], "video_cards_unavailable")
        self.assertEqual(provider.submits, [])

    def test_ambiguous_submit_is_not_retried_and_errors_are_sanitized(self):
        manager, provider = self.manager(FakeProvider(fail_submit=True))
        manager.submit(request())
        result = self.done(manager)
        self.assertEqual(result["status"], "failed")
        self.assertNotIn("PRIVATE", json.dumps(result))
        manager.submit(request("r2"))
        self.assertEqual(len(provider.submits), 1)
        manager.close()
        record_path = next((self.session / "videos").glob("*/job.json"))
        record = read_json(record_path); record["status"] = "running"; write_json(record_path, record)
        recovered, new_provider = self.manager()
        self.assertEqual(self.done(recovered)["code"], "video_submit_interrupted")
        self.assertEqual(new_provider.submits, [])

    def test_saved_prompt_ids_resume_polling_without_new_submit(self):
        manager, provider = self.manager()
        manager.submit(request()); self.done(manager); manager.close()
        record_path = next((self.session / "videos").glob("*/job.json"))
        record = read_json(record_path); record["status"] = "running"
        for shot in record["shots"]:
            shot["status"] = "running"
        write_json(record_path, record)
        for path in record_path.parent.glob("shot-*.mp4"):
            path.unlink()
        recovered, new_provider = self.manager()
        self.assertEqual(self.done(recovered)["status"], "ready")
        self.assertEqual(new_provider.submits, [])
        self.assertEqual(len(new_provider.finishes), 2)
        self.assertEqual(new_provider.uploads, [])

    def test_preview_failure_keeps_film_ready_and_is_attempted_once(self):
        manager, _ = self.manager()
        manager.submit(request()); result = self.done(manager)
        job = next(iter(manager.jobs.values())); path = manager.video_file(result["videoPath"])
        manager.preview = True
        with patch.object(video.sys, "platform", "darwin"), patch.object(video.subprocess, "run", side_effect=OSError("PRIVATE")) as opening:
            manager._open_preview(job, path); manager._open_preview(job, path)
        self.assertEqual(opening.call_count, 1)
        self.assertEqual(manager.status(query(request()))["status"], "ready")
        self.assertFalse(manager.status(query(request()))["previewOpened"])


class ProviderTests(unittest.TestCase):
    def test_plan_covers_all_sentences_once_and_scopes_the_scene(self):
        cards = [{"word": "CAT", "reference": "cat.png"}, {"word": "KITE", "reference": "kite.png"}]
        shots = video.plan_shots(STORY, [], cards)
        self.assertEqual(len(shots), 2)
        self.assertTrue(all(6 <= s["duration"] <= 10 for s in shots))
        passages = [s["prompt"].split("Animate only this chronological scene: ")[1].split("\n")[0] for s in shots]
        self.assertEqual(passages, ["A cat woke up. She wanted to fly. A kite came near. She held its string.",
                                    "They rose above trees. The cat smiled softly. They landed safely."])
        for sentence in STORY.split(". "):
            self.assertEqual(sum(sentence.rstrip(".") in s["prompt"].split("Animate only this chronological scene: ")[1] for s in shots), 1)
        self.assertTrue(all(1 <= len(s["refs"]) <= 5 for s in shots))
        sentences = STORY.split(". ")
        for count in range(2, 9):
            story = ". ".join(sentences[:count]).rstrip(".") + "."
            if count == 8:
                story += " A bird waved."
            with self.subTest(sentences=count):
                plan = video.plan_shots(story, [], cards)
                self.assertEqual(len(plan), 2)
                self.assertEqual(" ".join(s["prompt"].split("Animate only this chronological scene: ")[1].split("\n")[0] for s in plan), story)
        with self.assertRaises(video.VideoError):
            video.plan_shots("A cat woke up.", [], cards)

    def test_submit_preserves_supplied_model_and_only_extracts_final_video(self):
        provider = video.ComfyVideoProvider("fake-test-key")
        shot = video.plan_shots(STORY, [], [{"word": "CAT", "reference": "cat.png"}])[0]
        with patch.object(provider, "_json", return_value={"prompt_id": "pid"}) as call:
            self.assertEqual(provider.submit(shot), "pid")
        graph = call.call_args.args[1]["prompt"]
        self.assertEqual(graph["116"]["inputs"]["model"], "Seedance 2.5 Draft")
        self.assertEqual(graph["116"]["inputs"]["model.resolution"], "480p")
        self.assertEqual(graph["116"]["inputs"]["model.reference_images.image_1"], ["201", 0])
        outputs = {"outputs": {"106": {"images": [{"filename": "wrong.mp4"}]},
                               "117": {"images": [{"filename": "final.mp4", "type": "output"}]}}}
        self.assertEqual(provider._video_output(outputs, "pid")["filename"], "final.mp4")
        self.assertIsNone(provider._video_output({"outputs": {"106": outputs["outputs"]["106"]}}, "pid"))

    def test_bad_media_and_wrong_origin_are_rejected(self):
        for data in (b"not an mp4", b"\0" * 40):
            with self.assertRaises(video.VideoError):
                video._mp4(data)
        with self.assertRaises(ImageError):
            video.ComfyVideoProvider("fake-test-key", "https://example.com")

    def test_signed_video_download_does_not_forward_comfy_credentials(self):
        provider = video.ComfyVideoProvider("fake-test-key")
        requests = []
        class Response:
            status = 200
            def read(self, maximum):
                return MP4
        class Connection:
            def __init__(self, *args, **kwargs):
                pass
            def request(self, method, path, headers):
                requests.append((method, path, headers))
            def getresponse(self):
                return Response()
            def close(self):
                pass
        error = HTTPError(provider.base_url + "/api/view", 302, "redirect", {"Location": "https://media.example.test/movie.mp4?signature=sample"}, io.BytesIO(b""))
        with patch.object(provider._opener, "open", side_effect=error), patch.object(video, "_public_https", side_effect=lambda url: url), \
                patch.object(video, "_PublicHTTPSConnection", Connection):
            self.assertEqual(provider._download({"filename": "film.mp4", "type": "output"}), MP4)
        self.assertEqual(requests[0][2], {"Accept": "video/mp4"})

    def test_non_https_video_redirect_is_rejected_before_connecting(self):
        provider = video.ComfyVideoProvider("fake-test-key")
        error = HTTPError(provider.base_url + "/api/view", 302, "redirect", {"Location": "http://127.0.0.1/private"}, io.BytesIO(b""))
        with patch.object(provider._opener, "open", side_effect=error), patch.object(video, "_PublicHTTPSConnection") as connection:
            with self.assertRaises(ImageError) as caught:
                provider._download({"filename": "film.mp4", "type": "output"})
        self.assertEqual(caught.exception.code, "unsafe_image_url")
        connection.assert_not_called()

    def test_real_encoder_joins_silent_shots_into_decodable_mp4(self):
        ffmpeg = video.find_ffmpeg()
        if not ffmpeg:
            self.skipTest("Local encoder unavailable")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); paths = []
            for index in range(2):
                path = root / ("source%d.mp4" % index)
                subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64:d=0.25:r=24",
                                "-c:v", "libx264", "-pix_fmt", "yuv420p", str(path)], check=True, capture_output=True, timeout=20)
                paths.append(path)
            output = root / "story.mp4"
            video.join_videos(ffmpeg, paths, output)
            result = subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-i", str(output), "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"],
                                    capture_output=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))


if __name__ == "__main__":
    unittest.main()
