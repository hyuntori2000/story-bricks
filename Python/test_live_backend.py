"""Offline backend contract tests; no credentials or live APIs are used."""
import base64
import copy
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import wave

import live_backend as lb


def wav_bytes(seconds=.2):
    return lb.pcm_wav(b"\x01\x00" * int(24000 * seconds))


def context(count=1, answered=None):
    if answered is None:
        answered = count - 1
    words = []
    fixtures = [("CAT", "Pink."), ("RAIN", "No, she is inside."), ("SLEEP", "On a soft blanket.")]
    for i in range(count):
        word = fixtures[i][0]
        words.append({"index": i, "word": word, "groupId": "group-" + str(i),
                      "componentIds": [f"letter-{i}-{n}" for n in range(len(word) + (i == 2))],
                      "finishMarker": i == 2})
    turns = [{"wordIndex": i, "groupId": words[i]["groupId"], "word": words[i]["word"],
              "componentIds": words[i]["componentIds"], "question": "What happens?", "answer": fixtures[i][1]}
             for i in range(answered)]
    return {"currentWord": words[-1]["word"], "words": words, "turns": turns}


def envelope(value, request="request-1"):
    return json.dumps({"sessionId": "session-1", "requestId": request, "context": value}).encode()


class FakeUpstream:
    settings = lb.Settings("not-a-real-key")
    configured = True

    def __init__(self):
        self.calls = []
        self.delay = 0
        self.failure = None

    def generate_text(self, operation, instructions, value):
        self.calls.append((operation, copy.deepcopy(value)))
        time.sleep(self.delay)
        if self.failure:
            raise self.failure
        return "What does your cat sound like?" if operation == "question" else "The pink cat slept inside on a soft blanket while rain tapped the window."

    def synthesize(self, text, metrics=None):
        self.calls.append(("speech", text))
        return wav_bytes()

    def transcribe(self, data):
        self.calls.append(("transcribe", len(data)))
        return "On a soft blanket."


class FakeSocket:
    def __init__(self, spoken="What does your cat sound like?", status="completed"):
        self.events = [
            {"type": "session.created"}, {"type": "session.updated"},
            {"type": "response.output_audio.delta", "delta": base64.b64encode(b"\x01\x00" * 4800).decode()},
            {"type": "response.output_audio_transcript.done", "transcript": spoken},
            {"type": "response.done", "response": {"status": status}},
        ]
        self.sent, self.closed = [], False

    def recv(self):
        return json.dumps(self.events.pop(0)) if self.events else ""

    def send(self, text):
        self.sent.append(json.loads(text))

    def settimeout(self, _value):
        pass

    def close(self):
        self.closed = True


class BackendTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.upstream = FakeUpstream()
        self.backend = lb.Backend(self.upstream, Path(self.temporary.name) / "audio", {"question": "question prompt", "story": "story prompt"})

    def tearDown(self):
        self.temporary.cleanup()

    def test_final_story_requires_third_saved_answer(self):
        with self.assertRaises(lb.BackendError) as raised:
            self.backend.dispatch("/story", envelope(context(3, 2)), "application/json")
        self.assertEqual(raised.exception.code, "answer_order")
        self.assertEqual(self.upstream.calls, [])
        status, result = self.backend.dispatch("/story", envelope(context(3, 3)), "application/json")
        self.assertEqual(status, 200)
        self.assertEqual(self.upstream.calls[0][1]["turns"][2]["answer"], "On a soft blanket.")
        self.assertTrue(result["audioPath"].startswith("/audio/"))
        with wave.open(io.BytesIO(self.backend.audio.read(result["audioPath"]))) as wav:
            self.assertEqual((wav.getnchannels(), wav.getsampwidth(), wav.getframerate()), (1, 2, 24000))

    def test_question_uses_prior_context_and_preserves_ids(self):
        status, result = self.backend.dispatch("/question", envelope(context(3, 2)), "application/json")
        self.assertEqual(status, 200)
        self.assertEqual((result["sessionId"], result["requestId"]), ("session-1", "request-1"))
        self.assertEqual(self.upstream.calls[0][1]["turns"][1]["answer"], "No, she is inside.")
        self.assertEqual(len(self.upstream.calls[0][1]["words"][2]["componentIds"]), 6)

    def test_text_payload_preserves_all_story_material_without_geometry_or_ids(self):
        value = context(3, 3)
        answers = ["  I want to ride a submarine!  ", "It is a dragon, not a cat.", "A robot bakes a cake for the moon. 🌙"]
        for index, word in enumerate(value["words"]):
            word.update(worldPosition={"x": 4, "y": .7, "z": -2}, capturedAt="private-time")
            value["turns"][index].update(answer=answers[index], answeredAt="private-time")
        value["turns"][0]["question"] = "  What does your cat want to do?  "
        original = copy.deepcopy(value)
        lb.validate_context(value, "story")
        upstream = lb.OpenAIUpstream(lb.Settings("not-a-real-key"))
        response = {"status": "completed", "output": [{"type": "message", "content": [
            {"type": "output_text", "text": "The child took a submarine on an adventure."}]}]}
        with patch.object(upstream, "_http", return_value=json.dumps(response).encode()) as http:
            upstream.generate_text("story", "unchanged instructions", value)
        request = json.loads(http.call_args.args[1])
        projected = json.loads(request["input"])
        self.assertEqual(value, original)
        self.assertEqual(projected["currentWord"], "SLEEP")
        self.assertEqual([w["word"] for w in projected["words"]], ["CAT", "RAIN", "SLEEP"])
        self.assertEqual([w["finishMarker"] for w in projected["words"]], [False, False, True])
        self.assertEqual([t["answer"] for t in projected["turns"]], answers)
        self.assertEqual(projected["turns"][0]["question"], value["turns"][0]["question"])
        self.assertTrue(all(set(w) == {"index", "word", "finishMarker"} for w in projected["words"]))
        self.assertTrue(all(set(t) == {"wordIndex", "word", "question", "answer"} for t in projected["turns"]))
        self.assertEqual(request["reasoning"], {"effort": "low"})
        self.assertEqual(request["max_output_tokens"], 4000)
        self.assertFalse(request["store"])

    def test_arbitrary_first_words_keep_spelling_order_and_empty_prior_turns(self):
        upstream = lb.OpenAIUpstream(lb.Settings("not-a-real-key"))
        response = {"status": "completed", "output": [{"type": "message", "content": [
            {"type": "output_text", "text": "What would you like to happen with your word?"}]}]}
        # Character, event, place, name and invented sequence all use the same live path.
        for word_index, first_word in enumerate(("FOX", "RAIN", "FOREST", "JUDY", "OFX", "JOY",
                                                "APPLE", "ELEPHANT", "TREE.", "WHAT?", "HELLO!",
                                                "O'NEIL", "X-RAY", "A" * 32)):
            with self.subTest(first_word=first_word):
                value = context()
                value["currentWord"] = value["words"][0]["word"] = first_word
                value["words"][0]["componentIds"] = ["letter-" + str(i) for i in range(len(first_word))]
                status, _ = self.backend.dispatch("/question", envelope(value, "first-" + str(word_index)), "application/json")
                self.assertEqual(status, 200)
                self.assertEqual(self.upstream.calls[-2][1], value)
                with patch.object(upstream, "_http", return_value=json.dumps(response).encode()) as http:
                    upstream.generate_text("question", "question prompt", value)
                payload = json.loads(json.loads(http.call_args.args[1])["input"])
                self.assertEqual(payload["currentWord"], first_word)
                self.assertEqual(payload["words"], [{"index": 0, "word": first_word, "finishMarker": False}])
                self.assertEqual(payload["turns"], [])

    def test_stage_metrics_are_numeric_and_console_excludes_conversation_and_ids(self):
        output = io.StringIO()
        value = context(2)
        value["turns"][0]["answer"] = "PRIVATE_CHILD_ANSWER"
        with patch.object(lb.time, "perf_counter", side_effect=[10, 11, 12, 13, 15, 16, 16.5, 17]), redirect_stdout(output):
            status, result = self.backend.dispatch("/question", envelope(value), "application/json")
        self.assertEqual(status, 200)
        self.assertEqual(result["metrics"], {"textMs": 1000.0, "speechMs": 2000.0,
            "audioStoreMs": 500.0, "totalMs": 7000.0, "transcriptionMs": 0.0})
        event = json.loads(output.getvalue())
        timestamp = event.pop("timestampUtc")
        self.assertIsInstance(timestamp, str)
        self.assertTrue(timestamp.endswith("+00:00"))
        self.assertEqual(event, {"type": "voice_latency", "operation": "question", "status": 200,
                                 "code": "", "metrics": result["metrics"]})
        for private in ("PRIVATE_CHILD_ANSWER", "session-1", "request-1", "not-a-real-key", result["text"]):
            self.assertNotIn(private, output.getvalue())

    def test_failed_stage_has_elapsed_metrics_without_private_error_logging(self):
        self.upstream.failure = RuntimeError("PRIVATE_UPSTREAM_DETAIL")
        output = io.StringIO()
        with patch.object(lb.time, "perf_counter", side_effect=[1, 2, 3, 4]), redirect_stdout(output):
            status, result = self.backend.dispatch("/question", envelope(context()), "application/json")
        self.assertEqual(status, 500)
        self.assertEqual(result["metrics"]["textMs"], 1000.0)
        self.assertEqual(result["metrics"]["speechMs"], 0.0)
        self.assertEqual(result["metrics"]["totalMs"], 3000.0)
        self.assertNotIn("PRIVATE_UPSTREAM_DETAIL", output.getvalue() + json.dumps(result))

    def test_concurrent_duplicate_request_makes_one_upstream_call(self):
        self.upstream.delay = .08
        body = envelope(context())
        results = []
        threads = [threading.Thread(target=lambda: results.append(self.backend.dispatch("/question", body, "application/json"))) for _ in range(3)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(2)
        self.assertEqual(len(results), 3)
        self.assertEqual(results[0], results[1])
        self.assertEqual(results[1], results[2])
        self.assertEqual([c[0] for c in self.upstream.calls], ["question", "speech"])

    def test_request_id_cannot_change_body_or_operation(self):
        self.backend.dispatch("/question", envelope(context()), "application/json")
        with self.assertRaises(lb.BackendError) as raised:
            self.backend.dispatch("/question", envelope(context(2)), "application/json")
        self.assertEqual(raised.exception.code, "request_conflict")
        with self.assertRaises(lb.BackendError):
            self.backend.dispatch("/transcribe?sessionId=session-1&requestId=request-1", wav_bytes(), "audio/wav")
        self.assertEqual(len(self.upstream.calls), 2)

    def test_component_link_mismatch_and_unhashable_ids_rejected(self):
        value = context(2)
        value["turns"][0]["componentIds"] = ["different"]
        with self.assertRaises(lb.BackendError):
            self.backend.dispatch("/question", envelope(value), "application/json")
        value = context()
        value["words"][0]["componentIds"] = [{}]
        with self.assertRaises(lb.BackendError):
            self.backend.dispatch("/question", envelope(value), "application/json")
        self.assertEqual(self.upstream.calls, [])

    def test_missing_finish_marker_rejected(self):
        value = context(3, 3)
        value["words"][2]["finishMarker"] = False
        with self.assertRaises(lb.BackendError) as raised:
            self.backend.dispatch("/story", envelope(value), "application/json")
        self.assertEqual(raised.exception.code, "finish_required")

    def test_transcription_validates_recording_and_does_not_store_raw_audio(self):
        with self.assertRaises(lb.BackendError):
            self.backend.dispatch("/transcribe?sessionId=s&requestId=r", b"not audio", "audio/wav")
        self.assertEqual(self.upstream.calls, [])
        status, result = self.backend.dispatch("/transcribe?sessionId=s&requestId=r", wav_bytes(), "audio/wav")
        self.assertEqual(status, 200)
        self.assertEqual(result["text"], "On a soft blanket.")
        self.assertFalse(self.backend.audio.directory.exists())

    def test_upstream_error_is_not_replaced_with_fixture_or_retried(self):
        self.upstream.failure = lb.BackendError("api_limit", "Usage limit.", 503)
        body = envelope(context())
        status, result = self.backend.dispatch("/question", body, "application/json")
        self.assertEqual(status, 503)
        self.assertEqual(result["text"], "")
        self.assertEqual(result["audioPath"], "")
        self.assertEqual(self.backend.dispatch("/question", body, "application/json"), (status, result))
        self.assertEqual(len(self.upstream.calls), 1)

    def test_unexpected_exception_does_not_leak_details(self):
        self.upstream.failure = RuntimeError("example-private-credential-value")
        status, result = self.backend.dispatch("/question", envelope(context()), "application/json")
        self.assertEqual(status, 500)
        self.assertNotIn("example-private", json.dumps(result))

    def test_missing_key_is_explicit_and_health_does_not_call_api(self):
        upstream = lb.OpenAIUpstream(lb.Settings(""))
        backend = lb.Backend(upstream, Path(self.temporary.name), {"question": "question"})
        self.assertFalse(backend.health()["configured"])
        status, result = backend.dispatch("/question", envelope(context()), "application/json")
        self.assertEqual(status, 503)
        self.assertEqual(result["code"], "key_missing")
        self.assertNotIn("not-a-real-key", repr(self.upstream.settings))

    def test_request_ledger_and_audio_cache_are_bounded(self):
        self.backend.maximum_requests = 1
        self.backend.dispatch("/question", envelope(context()), "application/json")
        with self.assertRaises(lb.BackendError) as raised:
            self.backend.dispatch("/question", envelope(context(), "different"), "application/json")
        self.assertEqual(raised.exception.code, "request_capacity")
        store = lb.AudioStore(Path(self.temporary.name) / "bounded", maximum_files=2)
        first = store.write(wav_bytes())
        store.write(wav_bytes())
        store.write(wav_bytes())
        self.assertEqual(len(list(store.directory.glob("*.wav"))), 2)
        with self.assertRaises(lb.BackendError) as raised:
            store.read(first)
        self.assertEqual(raised.exception.status, 410)
        with self.assertRaises(lb.BackendError):
            store.read("/audio/../../.env.local")

    def test_real_http_contract_and_audio_download(self):
        server = lb.make_server(self.backend, 0)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        base = "http://127.0.0.1:" + str(server.server_port)
        try:
            with urlopen(base + "/health") as response:
                self.assertTrue(json.load(response)["configured"])
            self.assertEqual(self.upstream.calls, [])
            request = Request(base + "/question", data=envelope(context()), headers={"Content-Type": "application/json"})
            with urlopen(request) as response:
                result = json.load(response)
            with urlopen(base + result["audioPath"]) as response:
                self.assertEqual(response.headers["Content-Type"], "audio/wav")
                self.assertTrue(response.read().startswith(b"RIFF"))
            request = Request(base + "/question", data=envelope(context(), "browser"), headers={"Content-Type": "application/json", "Origin": "https://example.invalid"})
            with self.assertRaises(HTTPError) as raised:
                urlopen(request)
            self.assertEqual(raised.exception.code, 403)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(2)

    def test_realtime_pcm_synthesis_and_verbatim_fence(self):
        socket = FakeSocket()
        arguments = []
        def factory(*args, **kwargs):
            arguments.append((args, kwargs))
            return socket
        upstream = lb.OpenAIUpstream(lb.Settings("not-a-real-key"), websocket_factory=factory)
        audio = upstream.synthesize("What does your cat sound like?")
        self.assertTrue(audio.startswith(b"RIFF"))
        self.assertTrue(socket.closed)
        self.assertIn("gpt-realtime-2.1-mini", arguments[0][0][0])
        self.assertEqual(socket.sent[0]["session"]["audio"]["output"]["format"], {"type": "audio/pcm", "rate": 24000})
        self.assertEqual(socket.sent[1]["response"]["input"][0]["content"][0]["text"], "What does your cat sound like?")
        bad_socket = FakeSocket(spoken="Your cat is orange.")
        upstream.websocket_factory = lambda *_args, **_kwargs: bad_socket
        with self.assertRaises(lb.BackendError) as raised:
            upstream.synthesize("What does your cat sound like?")
        self.assertEqual(raised.exception.code, "speech_text_mismatch")
        self.assertTrue(bad_socket.closed)

    def test_speech_connect_setup_and_first_nonempty_audio_metrics(self):
        socket = FakeSocket()
        socket.events.insert(2, {"type": "response.output_audio.delta", "delta": ""})
        upstream = lb.OpenAIUpstream(lb.Settings("not-a-real-key"), websocket_factory=lambda *_args, **_kwargs: socket)
        metrics = {}
        with patch.object(lb.time, "perf_counter", side_effect=[10, 10.1, 10.2, 10.4]):
            audio = upstream.synthesize("What does your cat sound like?", metrics=metrics)
        self.assertTrue(audio.startswith(b"RIFF"))
        self.assertEqual(metrics, {"speechConnectMs": 100.0, "speechSetupMs": 200.0, "speechFirstAudioMs": 400.0})
        self.assertTrue(socket.closed)

    def test_environment_parser_preserves_process_values_without_execution(self):
        path = Path(self.temporary.name) / "local.env"
        path.write_text("OPENAI_API_KEY='local-example'\nOPENAI_VOICE=marin # comment\nCUSTOM=$(touch unsafe)\n")
        environment = {"OPENAI_API_KEY": "existing-example"}
        lb.load_local_environment(path, environment)
        self.assertEqual(environment["OPENAI_API_KEY"], "existing-example")
        self.assertEqual(environment["OPENAI_VOICE"], "marin")
        self.assertEqual(environment["CUSTOM"], "$(touch unsafe)")


if __name__ == "__main__":
    unittest.main()
