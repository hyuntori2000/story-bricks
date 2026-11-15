"""Local HTTP delivery contracts, using fake jobs and bytes, never generation APIs."""
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


class StoryVideoRoutes(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "test.mp4"
        self.data = b"local-http-file-fixture"
        self.path.write_bytes(self.data)
        self.identity = dict(workspaceId="demo", experienceSessionId="a" * 32,
                             sessionId="conversation", requestId="film-request")
        self.result = dict(self.identity, status="ready", videoPath="/story-videos/" + "b" * 32 + ".mp4")
        test = self

        class Jobs:
            def submit(self, value):
                test.submissions.append(value)
                return test.result

            def status(self, query):
                test.assertEqual(query, {key: [value] for key, value in test.identity.items()})
                return test.result

            def video_file(self, route):
                if test.file_error:
                    raise lb.VideoError("video_not_ready", "Animation is not ready.", 404)
                return test.path

        self.submissions, self.file_error = [], False
        self.upstream = FakeUpstream()
        self.backend = lb.Backend(self.upstream, Path(self.temp.name) / "audio", {}, video_jobs=Jobs())
        self.server = lb.make_server(self.backend, 0)
        self.thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=.01), daemon=True)
        self.thread.start()
        self.base = "http://127.0.0.1:" + str(self.server.server_port)

    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join(timeout=2)
        self.temp.cleanup()
        self.assertEqual(self.upstream.calls, [])

    def test_job_routes_do_not_wait_for_voice_slots(self):
        self.backend.capacity.acquire(); self.backend.capacity.acquire()
        try:
            request = Request(self.base + "/story-video", data=json.dumps(self.identity).encode(),
                              headers={"Content-Type": "application/json"})
            with urlopen(request, timeout=2) as response:
                self.assertEqual(json.load(response), self.result)
            with urlopen(self.base + "/story-video?" + urlencode(self.identity), timeout=2) as response:
                self.assertEqual(json.load(response), self.result)
        finally:
            self.backend.capacity.release(); self.backend.capacity.release()
        self.assertEqual(self.submissions, [self.identity])

    def test_full_video_and_ranges_preserve_bytes(self):
        for header, expected, code in [(None, self.data, 200), ("bytes=2-7", self.data[2:8], 206),
                                       ("bytes=-4", self.data[-4:], 206), ("bytes=3-", self.data[3:], 206)]:
            request = Request(self.base + self.result["videoPath"], headers={"Range": header} if header else {})
            with urlopen(request, timeout=2) as response:
                self.assertEqual(response.status, code)
                self.assertEqual(response.headers.get_content_type(), "video/mp4")
                self.assertEqual(response.headers["Accept-Ranges"], "bytes")
                self.assertEqual(int(response.headers["Content-Length"]), len(expected))
                self.assertEqual(response.read(), expected)

    def test_invalid_ranges_and_unready_video_are_explicit(self):
        for value in ("bytes=999-", "bytes=8-2", "bytes=0-1,4-5", "bytes=-0", "bytes=" + "9" * 150 + "-"):
            with self.assertRaises(HTTPError) as raised:
                urlopen(Request(self.base + self.result["videoPath"], headers={"Range": value}), timeout=2)
            with raised.exception as response:
                self.assertEqual(response.code, 416)
                self.assertEqual(response.headers["Content-Range"], "bytes */" + str(len(self.data)))
        self.file_error = True
        with self.assertRaises(HTTPError) as raised:
            urlopen(self.base + self.result["videoPath"], timeout=2)
        with raised.exception as response:
            self.assertEqual(response.code, 404)
            self.assertEqual(json.load(response)["code"], "video_not_ready")

    def test_browser_cannot_start_generation(self):
        request = Request(self.base + "/story-video", data=json.dumps(self.identity).encode(),
                          headers={"Content-Type": "application/json", "Origin": "https://example.invalid"})
        with self.assertRaises(HTTPError) as raised:
            urlopen(request, timeout=2)
        with raised.exception as response:
            self.assertEqual(response.code, 403)
            self.assertEqual(json.load(response)["code"], "origin_rejected")
        self.assertEqual(self.submissions, [])


if __name__ == "__main__":
    unittest.main()
