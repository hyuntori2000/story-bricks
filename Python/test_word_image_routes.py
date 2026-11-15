"""Local HTTP contracts for word images; no provider or credentials are used."""
import base64
import copy
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

import live_backend as lb
from test_live_backend import FakeUpstream


PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6rV8AAAAASUVORK5CYII="
)
IMAGE_PATH = "/word-images/" + "b" * 32 + ".png"
IMAGE_INPUT = {
    "workspaceId": "demo", "experienceSessionId": "a" * 32,
    "sessionId": "conversation-1", "requestId": "image-request-1",
    "groupId": "capture-group-1", "word": "FOX",
}


class FakeImageJobs:
    def __init__(self):
        self.calls = []
        self.failures = {}
        self.result = dict(IMAGE_INPUT, jobId="b" * 32, status="queued", imagePath="", error="", code="")

    def call(self, operation, value):
        self.calls.append((operation, copy.deepcopy(value)))
        if operation in self.failures:
            raise self.failures[operation]

    def submit(self, value):
        self.call("submit", value)
        return dict(self.result)

    def status(self, query):
        self.call("status", query)
        return dict(self.result)

    def read_image(self, route):
        self.call("read_image", route)
        return PNG


class WordImageRouteTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.upstream = FakeUpstream()
        self.images = FakeImageJobs()
        self.backend = lb.Backend(self.upstream, Path(self.temporary.name) / "audio", {}, image_jobs=self.images)
        self.server = lb.make_server(self.backend, 0)
        self.thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=.01), daemon=True)
        self.thread.start()
        self.base = "http://127.0.0.1:" + str(self.server.server_port)
        self.query = {key: IMAGE_INPUT[key] for key in ("workspaceId", "experienceSessionId", "sessionId", "requestId")}
        self.poll_path = "/word-image?" + urlencode(self.query)

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.temporary.cleanup()
        self.assertEqual(self.upstream.calls, [])

    def post_request(self, headers=None):
        return Request(self.base + "/word-image", data=json.dumps(IMAGE_INPUT).encode(),
                       headers=dict({"Content-Type": "application/json"}, **(headers or {})))

    def test_submit_and_poll_work_while_all_speech_slots_are_busy(self):
        # Keep both slots occupied until HTTP completes: image submission must
        # neither wait for speech nor return the speech busy response.
        self.assertTrue(self.backend.capacity.acquire(blocking=False))
        self.assertTrue(self.backend.capacity.acquire(blocking=False))
        try:
            with urlopen(self.post_request(), timeout=2) as response:
                self.assertEqual(response.status, 200)
                submitted = json.load(response)
            with urlopen(self.base + self.poll_path, timeout=2) as response:
                polled = json.load(response)
        finally:
            self.backend.capacity.release()
            self.backend.capacity.release()
        self.assertEqual(submitted, self.images.result)
        self.assertEqual(polled, submitted)
        self.assertEqual(self.images.calls, [
            ("submit", IMAGE_INPUT), ("status", {key: [value] for key, value in self.query.items()}),
        ])
        self.assertEqual(self.backend.requests, {})

    def test_png_download_preserves_bytes_and_content_type(self):
        with urlopen(self.base + IMAGE_PATH, timeout=2) as response:
            self.assertEqual(response.status, 200)
            self.assertEqual(response.headers.get_content_type(), "image/png")
            self.assertEqual(response.headers["X-Content-Type-Options"], "nosniff")
            self.assertEqual(response.read(), PNG)
        self.assertEqual(self.images.calls, [("read_image", IMAGE_PATH)])

    def test_manager_errors_keep_their_http_status_and_code(self):
        cases = [
            ("submit", self.post_request(), "image_save_failed", 507),
            ("status", self.base + self.poll_path, "image_not_found", 404),
            ("read_image", self.base + IMAGE_PATH, "image_not_ready", 404),
        ]
        for operation, request, code, status in cases:
            with self.subTest(operation=operation):
                self.images.failures[operation] = lb.ImageError(code, "Image operation failed.", status)
                with self.assertRaises(HTTPError) as raised:
                    urlopen(request, timeout=2)
                with raised.exception as response:
                    self.assertEqual(response.code, status)
                    result = json.load(response)
                self.assertEqual(result["code"], code)
                self.assertEqual(result["error"], "Image operation failed.")

    def test_browser_origin_is_rejected_before_image_submission(self):
        with self.assertRaises(HTTPError) as raised:
            urlopen(self.post_request({"Origin": "https://example.invalid"}), timeout=2)
        with raised.exception as response:
            self.assertEqual(response.code, 403)
            self.assertEqual(json.load(response)["code"], "origin_rejected")
        self.assertEqual(self.images.calls, [])


if __name__ == "__main__":
    unittest.main()
