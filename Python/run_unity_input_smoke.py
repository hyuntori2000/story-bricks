#!/usr/bin/env python3
"""Drive the real Unity/Python socket pipeline with explicit offline voice fixtures.

Requires a Mac player built with PythonInputSmokeCheck. No API or microphone calls.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import socket
import subprocess
import time
import uuid

from word_input_server import WordInputServer, encode_line, point


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", required=True, type=Path, help="Path to the Mac app's Contents/MacOS/Words to Worlds executable.")
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--log", required=True, type=Path)
    arguments = parser.parse_args()
    player = arguments.player.resolve()
    report_path = arguments.report.resolve()
    log_path = arguments.log.resolve()
    if not player.is_file():
        parser.error("Player executable does not exist.")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    if report_path.exists():
        report_path.unlink()
    server = WordInputServer()
    process = None
    evidence = {"passed": False, "actualTcpInput": True, "microphoneUsed": False, "apiUsed": False,
                "headsetTested": False, "acceptedObservations": 0, "rejectedProbes": [], "duplicateAcceptedOnce": False}
    sequence = 0

    def observation(kind: str, group: str, position, **fields) -> dict:
        nonlocal sequence
        state = server.wait_status(lambda s: s.get("inputMode") == "python" and s.get("spatialReady"), timeout=15)
        sequence = max(sequence, state["lastSequence"]) + 1
        return dict(type=kind, protocolVersion=1, requestId=uuid.uuid4().hex, sequence=sequence,
                    sessionId=state["sessionId"], frameRevision=state["frameRevision"], groupId=group,
                    position=point(position), **fields)

    def exchange(message: dict, accepted: bool) -> dict:
        # White-box transport probe: bypass the producer's client-side guards so
        # Unity itself must reject stale/mismatched observations.
        with server._send_lock, server._condition:
            if server._connection is None:
                raise ConnectionError("No Unity socket for acceptance probe.")
            server._connection.sendall(encode_line(message))
        acknowledgment = server.wait_ack(message["requestId"], timeout=8)
        if bool(acknowledgment.get("accepted")) != accepted:
            raise AssertionError(f"Unexpected acknowledgment: {acknowledgment}")
        if accepted:
            evidence["acceptedObservations"] += 1
        return acknowledgment

    try:
        server.start()
        process = subprocess.Popen([str(player), "--python-input-smoke", str(report_path), "-logFile", str(log_path)],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        server.wait_status(lambda s: s.get("state") == "Ready" and s.get("inputMode") == "python" and s.get("spatialReady"), timeout=30)
        cat = observation("complete", "smoke-cat", (-1.0, .09, -.4), groupIndex=0, word="CAT", finishMarker=False,
                          componentIds=["smoke-cat-c", "smoke-cat-a", "smoke-cat-t"])
        exchange(cat, True)
        # Remove the previous ack to require a response to the actual duplicate
        # packet rather than accidentally reading the first acknowledgment again.
        with server._condition:
            server._acks.pop(cat["requestId"], None)
        exchange(cat, True)
        evidence["duplicateAcceptedOnce"] = True
        stale_session = observation("pose", "smoke-cat", (0, .09, 0))
        stale_session["sessionId"] = "stale-session"
        evidence["rejectedProbes"].append(exchange(stale_session, False)["code"])
        stale_frame = observation("pose", "smoke-cat", (0, .09, 0))
        stale_frame["frameRevision"] -= 1
        evidence["rejectedProbes"].append(exchange(stale_frame, False)["code"])
        unrelated = observation("pose", "unrelated-group", (0, .09, 0))
        evidence["rejectedProbes"].append(exchange(unrelated, False)["code"])
        exchange(observation("pose", "smoke-cat", (-.75, .09, -.35)), True)
        server.wait_status(lambda s: s.get("state") == "PlayingWord" and s.get("activeGroupId") == "smoke-cat", timeout=5)
        with server._condition:
            connection = server._connection
        connection.shutdown(socket.SHUT_RDWR)
        connection.close()
        state = server.wait_status(lambda s: s.get("state") == "PlayingWord" and not s.get("audioReady"), timeout=10)
        if not state.get("spatialReady"):
            raise AssertionError("Socket loss incorrectly invalidated the underlying board frame.")
        # The Unity harness observes actual sample positions throughout this hold.
        time.sleep(.45)
        exchange(observation("pose", "smoke-cat", (-.55, .09, -.2)), True)
        server.wait_status(lambda s: s.get("audioReady") and s.get("state") == "PlayingWord", timeout=5)
        for index, word, group, position in [
            (1, "RAIN", "smoke-rain", (1.1, .09, -.35)),
            (2, "SLEEP", "smoke-sleep", (0.0, .09, .8)),
        ]:
            server.wait_status(lambda s, expected=index: s.get("state") == "Silent" and s.get("nextIndex") == expected, timeout=35)
            letters = word + ("#" if index == 2 else "")
            component_ids = [f"{group}-{i}-{'finish' if letter == '#' else letter.lower()}" for i, letter in enumerate(letters)]
            exchange(observation("complete", group, position, groupIndex=index, word=word, finishMarker=index == 2,
                                 componentIds=component_ids), True)
        exit_code = process.wait(timeout=70)
        if not report_path.exists():
            raise AssertionError("Unity exited without writing its acceptance report.")
        unity = json.loads(report_path.read_text())
        if exit_code != 0 or not unity.get("passed"):
            raise AssertionError(f"Unity smoke failed: exit={exit_code}, failures={unity.get('failures')}")
        if unity.get("recognizedWords") != 3 or unity.get("savedAnswers") != 3 or unity.get("requests") != 4:
            raise AssertionError("Duplicate packet changed the expected conversation counts.")
        evidence["passed"] = True
        evidence["unityReport"] = str(report_path)
        print("PASS: actual TCP, positional updates, duplicate/stale guards, disconnect/fresh-pose recovery, and full offline-audio conversation.")
        return 0
    except (OSError, ValueError, ConnectionError, TimeoutError, AssertionError, subprocess.TimeoutExpired) as error:
        evidence["error"] = str(error)
        print(f"FAIL: {error}")
        return 1
    finally:
        server.close()
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        evidence_path = report_path.with_name(report_path.stem + "_PYTHON.json")
        evidence_path.write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    raise SystemExit(main())
