#!/usr/bin/env python3
"""Local word-observation server for the Unity demo (Python 3, no dependencies).

Replace calls to complete()/pose() with outputs from your own recognizer.
This transports observations; it performs no OCR, speech capture, or AI calls.
"""
from __future__ import annotations

import argparse
import json
import math
import random
import shlex
import socket
import threading
import time
import uuid
from collections import OrderedDict
from typing import Callable, Optional, Sequence


MAX_LINE_BYTES = 16384
DEFAULT_PORT = 8765
WORDS = ("CAT", "RAIN", "SLEEP")


class ProtocolError(ValueError):
    pass


def reject_nonfinite(value: str):
    raise ProtocolError(f"Non-finite JSON number: {value}")


class JsonLines:
    """Handles split/coalesced UTF-8 lines without allowing an unbounded buffer."""

    def __init__(self) -> None:
        self.pending = bytearray()

    def feed(self, data: bytes) -> list[dict]:
        messages = []
        for byte in data:
            if byte == 10:
                line = bytes(self.pending).rstrip(b"\r")
                self.pending.clear()
                if line:
                    try:
                        message = json.loads(line.decode("utf-8"), parse_constant=reject_nonfinite)
                    except (UnicodeDecodeError, json.JSONDecodeError) as error:
                        raise ProtocolError(f"Invalid UTF-8 JSON: {error}") from error
                    if not isinstance(message, dict):
                        raise ProtocolError("Expected a JSON object.")
                    messages.append(message)
            else:
                if len(self.pending) >= MAX_LINE_BYTES:
                    raise ProtocolError("JSON line exceeds 16 KB.")
                self.pending.append(byte)
        return messages


def encode_line(message: dict) -> bytes:
    try:
        line = json.dumps(message, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    except (TypeError, ValueError) as error:
        raise ProtocolError(str(error)) from error
    if len(line) > MAX_LINE_BYTES:
        raise ProtocolError("JSON line exceeds 16 KB.")
    return line + b"\n"


def point(values: Sequence[float]) -> dict:
    if len(values) != 3 or any(not math.isfinite(float(v)) for v in values):
        raise ProtocolError("Position must contain three finite numbers in board-local metres.")
    return dict(zip(("x", "y", "z"), map(float, values)))


class WordInputServer:
    """One Unity connection, no retained send backlog, explicit session/frame fencing.

    The app's latest status is copied into each observation. After disconnect the
    status is cleared, so a recognizer must wait for a new status before sending.
    """

    def __init__(self, port: int = DEFAULT_PORT, report: Optional[Callable[[str], None]] = print) -> None:
        self.port = port
        self.report = report or (lambda _: None)
        self._stop = threading.Event()
        self._condition = threading.Condition(threading.RLock())
        self._send_lock = threading.Lock()
        self._listener: Optional[socket.socket] = None
        self._connection: Optional[socket.socket] = None
        self._worker: Optional[threading.Thread] = None
        self._status: Optional[dict] = None
        self._acks: OrderedDict[str, dict] = OrderedDict()
        self._sequence = 0
        self._session = ""
        self._last_display = None

    @property
    def status(self) -> Optional[dict]:
        with self._condition:
            return dict(self._status) if self._status is not None else None

    @property
    def connected(self) -> bool:
        with self._condition:
            return self._connection is not None

    def start(self) -> None:
        if self._worker is not None:
            raise RuntimeError("Server already started.")
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            listener.bind(("127.0.0.1", self.port))
            listener.listen(1)
            listener.settimeout(0.25)
        except Exception:
            listener.close()
            raise
        self._listener = listener
        self.port = listener.getsockname()[1]
        self._worker = threading.Thread(target=self._run, name="Unity word input", daemon=True)
        self._worker.start()
        self.report(f"Listening on 127.0.0.1:{self.port}. Select PYTHON USB in Unity.")

    def close(self) -> None:
        self._stop.set()
        with self._condition:
            connection = self._connection
            self._connection = None
            self._status = None
            self._condition.notify_all()
        for stream in (connection, self._listener):
            if stream is not None:
                try:
                    stream.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                stream.close()
        if self._worker is not None:
            self._worker.join(timeout=2)

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                connection, _ = self._listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            connection.settimeout(0.25)
            connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            with self._condition:
                self._connection = connection
                self._status = None
                self._last_display = None
            self.report("Unity connected. Waiting for current session/frame status.")
            framer = JsonLines()
            try:
                while not self._stop.is_set():
                    try:
                        data = connection.recv(4096)
                    except socket.timeout:
                        continue
                    if not data:
                        break
                    for message in framer.feed(data):
                        self._received(message)
            except (OSError, ProtocolError) as error:
                if not self._stop.is_set():
                    self.report(f"Connection ended: {error}")
            finally:
                with self._condition:
                    if self._connection is connection:
                        self._connection = None
                        self._status = None
                    self._condition.notify_all()
                connection.close()
                if not self._stop.is_set():
                    self.report("Unity disconnected. Unsent data is not replayed; waiting for reconnect.")

    def _received(self, message: dict) -> None:
        kind = message.get("type")
        with self._condition:
            if kind == "status":
                if message.get("protocolVersion") != 1:
                    raise ProtocolError("Unsupported Unity input protocol version.")
                session = message.get("sessionId")
                revision = message.get("frameRevision")
                sequence = message.get("lastSequence", 0)
                if not isinstance(session, str) or not session or not isinstance(revision, int) or not isinstance(sequence, int):
                    raise ProtocolError("Unity status must contain sessionId, integer frameRevision, and integer lastSequence.")
                if session != self._session:
                    self._session = session
                    self._sequence = sequence
                    self._acks.clear()
                self._sequence = max(self._sequence, sequence)
                self._status = dict(message)
                display = (session, revision, message.get("inputMode"), message.get("state"), message.get("spatialReady"), message.get("nextIndex"))
                if display != self._last_display:
                    self._last_display = display
                    self.report(f"Unity: {message.get('state')} | mode={message.get('inputMode')} | spatialReady={message.get('spatialReady')} | nextIndex={message.get('nextIndex')} | frame={revision} | session={session}")
            elif kind == "ack":
                request = message.get("requestId")
                if not isinstance(request, str):
                    raise ProtocolError("Ack has no requestId.")
                self._acks[request] = dict(message)
                while len(self._acks) > 256:
                    self._acks.popitem(last=False)
                self.report(f"Ack {request[:8]}: {'accepted' if message.get('accepted') else 'REJECTED'} {message.get('code', '')} {message.get('detail', '')}")
            self._condition.notify_all()

    def wait_status(self, predicate: Callable[[dict], bool] = lambda _: True, timeout: float = 30) -> dict:
        deadline = time.monotonic() + timeout
        with self._condition:
            while not self._stop.is_set():
                if self._status is not None and predicate(self._status):
                    return dict(self._status)
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Timed out waiting for the required Unity state.")
                self._condition.wait(min(remaining, 0.25))
        raise ConnectionError("Server stopped.")

    def capture_reference(self) -> dict:
        """For a simulated producer. Real camera frames must carry their own capture-time reference."""
        with self._condition:
            if self._status is None:
                raise ConnectionError("No current Unity status.")
            return {"sessionId": self._status["sessionId"], "frameRevision": self._status["frameRevision"]}

    def _send_observation(self, observation: dict, reference: Optional[dict] = None) -> str:
        with self._send_lock, self._condition:
            status = self._status
            connection = self._connection
            if connection is None or status is None:
                raise ConnectionError("No current Unity status. Connect Unity and wait for its status.")
            if status.get("inputMode") != "python":
                raise ProtocolError("Select PYTHON USB input mode in Unity first.")
            if not status.get("spatialReady"):
                raise ProtocolError("Unity's spatial frame is unavailable. Place the mock board in the headset first.")
            reference = reference or status
            if reference.get("sessionId") != status["sessionId"] or reference.get("frameRevision") != status["frameRevision"]:
                raise ProtocolError("Observation belongs to an old session/frame. Discard it and process a new capture.")
            self._sequence += 1
            request_id = uuid.uuid4().hex
            message = dict(observation, protocolVersion=1, requestId=request_id, sequence=self._sequence,
                           sessionId=reference["sessionId"], frameRevision=reference["frameRevision"])
            packet = encode_line(message)
            try:
                connection.sendall(packet)
            except OSError as error:
                raise ConnectionError("Observation was not confirmed. It is not queued for replay.") from error
            return request_id

    def complete(self, word: str, position: Sequence[float], group_id: Optional[str] = None,
                 component_ids: Optional[Sequence[str]] = None, *, reference: Optional[dict] = None) -> str:
        word = word.strip().upper()
        if word.endswith("#"):
            word = word[:-1]
        if word not in WORDS:
            raise ProtocolError("This conversation fixture accepts CAT, RAIN, then SLEEP# only.")
        index = WORDS.index(word)
        group_id = group_id or f"python-{word.lower()}"
        raw = word + ("#" if index == 2 else "")
        ids = list(component_ids) if component_ids is not None else [f"{group_id}-{i}-{'finish' if letter == '#' else letter}" for i, letter in enumerate(raw)]
        return self._send_observation(dict(type="complete", groupIndex=index, groupId=group_id, word=word,
                                           finishMarker=index == 2, componentIds=ids, position=point(position)), reference)

    def pose(self, group_id: str, position: Sequence[float], *, reference: Optional[dict] = None) -> str:
        return self._send_observation(dict(type="pose", groupId=group_id, position=point(position)), reference)

    def wait_ack(self, request_id: str, timeout: float = 5) -> dict:
        deadline = time.monotonic() + timeout
        with self._condition:
            while request_id not in self._acks:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Unity did not acknowledge this observation; do not assume it was applied.")
                if self._connection is None:
                    raise ConnectionError("Disconnected before acknowledgment. No automatic replay.")
                self._condition.wait(min(remaining, 0.25))
            return dict(self._acks[request_id])

    def demo(self, timeout: float = 300) -> None:
        """Complete each word when allowed. Live mode records the user after each question; offline tests use prepared answers."""
        positions = [(-1.05, 0.09, -0.55), (1.05, 0.09, -0.35), (0.0, 0.09, 0.80)]
        for index, word in enumerate(WORDS):
            self.report(f"Demo: waiting to complete {word}. Speak after each question; Unity opens the microphone automatically in live mode.")
            self.wait_status(lambda s: s.get("inputMode") == "python" and s.get("spatialReady") and s.get("nextIndex") == index
                             and s.get("state") in ("Ready", "Silent"), timeout)
            request = self.complete(word, positions[index])
            acknowledgment = self.wait_ack(request)
            if not acknowledgment.get("accepted"):
                raise ProtocolError(f"Demo observation rejected: {acknowledgment}")
        self.report("Final word sent. Answer the final question in Unity to start the story.")
        self.wait_status(lambda s: s.get("state") == "Finished", timeout)
        self.report("Demo completed. No microphone or AI service was invoked by this Python server.")


HELP = """Commands (board-local metres, +X right, +Y up, +Z away from the front of the board):
  status
  complete CAT -1.0 0.09 -0.5
  complete RAIN 1.0 0.09 -0.3
  complete SLEEP# 0 0.09 0.8
  pose python-cat -0.7 0.09 -0.5
  random CAT
  demo        Send each fixture word when allowed; speak your answers in Unity live mode.
  quit
Replace server.complete(...) / server.pose(...) with results from your recognizer.
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--demo", action="store_true", help="Run CAT/RAIN/SLEEP# observations; speak your answers in Unity live mode.")
    arguments = parser.parse_args()
    server = WordInputServer(arguments.port)
    try:
        server.start()
        if arguments.demo:
            server.demo()
            return 0
        print(HELP)
        while True:
            try:
                parts = shlex.split(input("word-input> "))
                if not parts:
                    continue
                if parts[0] in ("quit", "exit"):
                    break
                if parts[0] == "status":
                    print(json.dumps(server.status, indent=2))
                elif parts[0] == "complete" and len(parts) == 5:
                    server.wait_ack(server.complete(parts[1], list(map(float, parts[2:]))))
                elif parts[0] == "pose" and len(parts) == 5:
                    server.wait_ack(server.pose(parts[1], list(map(float, parts[2:]))))
                elif parts[0] == "random" and len(parts) == 2:
                    server.wait_ack(server.complete(parts[1], (random.uniform(-1.25, 1.25), 0.09, random.uniform(-0.6, 0.8))))
                elif parts[0] == "demo":
                    server.demo()
                else:
                    print(HELP)
            except (ValueError, ConnectionError, TimeoutError) as error:
                print(f"Not sent/applied: {error}")
    except (EOFError, KeyboardInterrupt):
        print("\nStopping the local bridge.")
    except (OSError, ValueError, TimeoutError, ConnectionError) as error:
        print(f"Bridge error: {error}")
        return 1
    finally:
        server.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
