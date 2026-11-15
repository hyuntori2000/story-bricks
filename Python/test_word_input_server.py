"""Run with: python3 -m unittest discover -s Python -v"""
import json
import socket
import time
import unittest

from word_input_server import JsonLines, MAX_LINE_BYTES, ProtocolError, WordInputServer, encode_line, point


class FramingTests(unittest.TestCase):
    def test_fragmented_multibyte_and_coalesced_messages(self):
        line = encode_line({"type": "status", "caption": "cat ☺"})
        decoder = JsonLines()
        self.assertEqual(decoder.feed(line[:len(line) - 3]), [])
        self.assertEqual(decoder.feed(line[len(line) - 3:] + b'{"type":"ack"}\r\n'),
                         [{"type": "status", "caption": "cat ☺"}, {"type": "ack"}])

    def test_oversized_unterminated_frame_is_rejected(self):
        decoder = JsonLines()
        decoder.feed(b"x" * MAX_LINE_BYTES)
        with self.assertRaises(ProtocolError):
            decoder.feed(b"x")

    def test_invalid_json_utf8_and_nonfinite_numbers_rejected(self):
        for raw in (b'{"a":\xff}\n', b'[1,2]\n', b'{"x":NaN}\n', b'{bad}\n'):
            with self.subTest(raw=raw), self.assertRaises(ProtocolError):
                JsonLines().feed(raw)
        with self.assertRaises(ProtocolError):
            encode_line({"x": float("nan")})
        with self.assertRaises(ProtocolError):
            point((0, float("inf"), 0))


class SocketTests(unittest.TestCase):
    def setUp(self):
        self.server = WordInputServer(port=0, report=None)
        self.server.start()
        self.client = None
        self.connect()

    def tearDown(self):
        self.reader.close()
        if self.client is not None:
            self.client.close()
        self.server.close()

    def connect(self):
        self.client = socket.create_connection(("127.0.0.1", self.server.port), timeout=1)
        self.client.settimeout(1)
        self.reader = self.client.makefile("rb")

    def status(self, session="session-a", revision=1, sequence=0, **fields):
        data = dict(type="status", protocolVersion=1, sessionId=session, frameRevision=revision,
                    lastSequence=sequence, inputMode="python", spatialReady=True, state="Ready", nextIndex=0)
        data.update(fields)
        self.client.sendall(encode_line(data))
        self.server.wait_status(lambda s: s == data, timeout=1)

    def read(self):
        return json.loads(self.reader.readline())

    def test_complete_and_pose_keep_word_identity_frame_and_monotonic_sequence(self):
        self.status(revision=7, sequence=12)
        request = self.server.complete("CAT", (-1, 0.09, 0), "cat-42", ["c-1", "a-2", "t-3"])
        message = self.read()
        self.assertEqual(message["requestId"], request)
        self.assertEqual(message["sequence"], 13)
        self.assertEqual(message["sessionId"], "session-a")
        self.assertEqual(message["frameRevision"], 7)
        self.assertEqual(message["componentIds"], ["c-1", "a-2", "t-3"])
        self.assertEqual(message["groupIndex"], 0)
        self.assertFalse(message["finishMarker"])
        self.client.sendall(encode_line(dict(type="ack", requestId=request, accepted=True, code="ok", detail="")))
        self.assertTrue(self.server.wait_ack(request)["accepted"])
        self.server.pose("cat-42", (-0.8, 0.09, 0))
        move = self.read()
        self.assertEqual(move["sequence"], 14)
        self.assertEqual(move["groupId"], "cat-42")
        self.assertEqual(move["position"]["x"], -0.8)

    def test_final_word_contains_distinct_repeated_letters_and_separate_marker(self):
        self.status()
        self.server.complete("SLEEP#", (0, 0.09, 0.8))
        final = self.read()
        self.assertEqual(final["word"], "SLEEP")
        self.assertTrue(final["finishMarker"])
        self.assertEqual(final["groupIndex"], 2)
        self.assertEqual(len(final["componentIds"]), 6)
        self.assertEqual(len(set(final["componentIds"])), 6)

    def test_no_observation_before_current_status_or_valid_frame(self):
        with self.assertRaises(ConnectionError):
            self.server.complete("CAT", (0, 0, 0))
        self.status(spatialReady=False)
        with self.assertRaises(ProtocolError):
            self.server.pose("cat", (0, 0, 0))
        self.status(inputMode="random")
        with self.assertRaises(ProtocolError):
            self.server.complete("CAT", (0, 0, 0))

    def test_reconnect_requires_fresh_status_and_does_not_replay_old_send(self):
        self.status(sequence=4)
        self.server.complete("CAT", (0, 0.09, 0))
        first = self.read()
        self.reader.close()
        self.client.shutdown(socket.SHUT_RDWR)
        self.client.close()
        deadline = time.monotonic() + 2
        while self.server.connected and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertFalse(self.server.connected)
        self.connect()
        deadline = time.monotonic() + 2
        while not self.server.connected and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertIsNone(self.server.status)
        with self.assertRaises(ConnectionError):
            self.server.pose("python-cat", (0, 0.09, 0))
        self.status(revision=2, sequence=5)
        self.server.pose("python-cat", (0.5, 0.09, 0))
        next_message = self.read()
        self.assertEqual(next_message["type"], "pose")
        self.assertEqual(next_message["sequence"], first["sequence"] + 1)
        self.assertEqual(next_message["frameRevision"], 2)

    def test_session_reset_changes_fence_and_sequence(self):
        self.status(sequence=20)
        self.server.complete("CAT", (0, 0.09, 0))
        self.read()
        self.status(session="session-b", revision=3, sequence=0)
        self.server.complete("CAT", (0, 0.09, 0))
        message = self.read()
        self.assertEqual(message["sessionId"], "session-b")
        self.assertEqual(message["sequence"], 1)

    def test_async_recognition_cannot_relabel_old_frame_as_current(self):
        self.status(revision=4)
        capture_reference = self.server.capture_reference()
        self.status(revision=5)
        with self.assertRaises(ProtocolError):
            self.server.complete("CAT", (0, 0.09, 0), reference=capture_reference)
        with self.assertRaises(ProtocolError):
            self.server.pose("python-cat", (0, 0.09, 0), reference=capture_reference)
        self.server.complete("CAT", (0, 0.09, 0), reference=self.server.capture_reference())
        self.assertEqual(self.read()["frameRevision"], 5)

    def test_invalid_frame_disconnects_and_shutdown_stops_worker(self):
        self.status()
        self.client.sendall(b"x" * (MAX_LINE_BYTES + 1))
        deadline = time.monotonic() + 2
        while self.server.connected and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertFalse(self.server.connected)
        self.server.close()
        self.assertFalse(self.server._worker.is_alive())


if __name__ == "__main__":
    unittest.main()
