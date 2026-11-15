#!/usr/bin/env python3
"""Read the Quest app's own JSONL diagnostics and display live table-relative hands.

No camera, microphone, API or device-setting mutations. Reconnects after app/ADB restarts.
"""
import argparse
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
ADB = "/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
LOG_NAME = re.compile(r"table-depth-[a-f0-9]{32}\.jsonl\Z")


def number(value, scale=1, digits=1):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return "--"
    return ("{:+." + str(digits) + "f}").format(value * scale)


def vector(value):
    if not isinstance(value, dict):
        return "X=--  Y=--  Z=--"
    return "  ".join(axis.upper() + "=" + number(value.get(axis), 100) for axis in "xyz")


def display(sample, age, fps=None):
    if not sample:
        return "Waiting for the main Quest app's depth log.\nKeep USB connected and open Words to Worlds."
    live = age <= 1.5
    current = "LIVE" if live else "STALE - no new app sample for {:.1f} s".format(age)
    has_xyz = "leftHandTablePosition" in sample
    lines = ["STORY BRICKS / LIVE HAND POSITIONS", current,
             "All coordinates below are cm. Origin = locked table reference point.",
             "Y = height above table. X/Z follow Quest world axes (not screen left/right).",
             "", "TABLE  current={}  depth hits={}/9  reference revision={}  app FPS={}".format(
                 sample.get("tableValid", False) and live, sample.get("supportingDepthHits", 0),
                 sample.get("referenceRevision", "--"),
                 number(sample.get("observedAppFps", fps), digits=1)),
             "  Reference world: " + vector(sample.get("tablePoint")),
             "  " + sample.get("surfaceStatus", "No surface status"),
             "INPUT  active={} appFocus={} VRFocus={} inputFocus={}".format(
                 sample.get("activeController", "--"), sample.get("appFocused", "--"),
                 sample.get("vrFocus", "--"), sample.get("inputFocus", "--")), ""]
    if "historyReferenceUsable" in sample:
        lines += ["HISTORY  plane usable={}  held={}  depth age={} s  contradicted={}".format(
            live and sample.get("historyReferenceUsable", False),
            live and sample.get("depthHeldForHistory", False),
            number(sample.get("referenceAgeSeconds"), digits=2),
            sample.get("referenceContradicted", False)),
            ("  Explicit pinch capture uses the saved table reference; fresh depth is reported separately."
             if sample.get("captureGesture") in ("double_pinch_raise_hands", "double_pinch_timer")
             else "  Held depth is only for hand history. A photo still requires fresh depth."), ""]
    if sample.get("captureGesture"):
        lines += ["GESTURE  {}  armed={}".format(sample["captureGesture"], sample.get("gestureArmed", False)),
                  "  " + str(sample.get("gestureStatus", "--")),
                  "  Clearance L: {} | R: {}".format(sample.get("leftClearanceStatus", "--"),sample.get("rightClearanceStatus", "--")),
                  "  Pinch tracked L/R={}/{} pressed L/R={}/{}".format(sample.get("leftPinchTracked"),sample.get("rightPinchTracked"),sample.get("leftPinched"),sample.get("rightPinched")), ""]
    landmark = "APPROACH index tip" if sample.get("handApproachLandmark") == "index_tip" else "CAPTURE closest tip"
    for side in ("left", "right"):
        valid = live and sample.get(side + "HandPositionValid", False)
        gap_valid = live and sample.get(side + "Valid", False)
        raw = sample.get(side + "RawHand")
        if isinstance(raw, dict):
            reference = "depth verified" if live and raw.get("referenceFresh") else "saved reference only"
            lines.append(side.upper() + " HAND / SDK index tip WORLD: " +
                         vector(raw.get("indexTipWorldPosition") if live and raw.get("indexTipValid") else None))
            lines.append("  SDK index tip TABLE: " + vector(raw.get("indexTipTablePosition")
                         if live and raw.get("indexTipTablePositionValid") else None) + " | " + reference)
            lines.append("  SDK wrist TABLE: " + vector(raw.get("wristTablePosition")
                         if live and raw.get("wristTablePositionValid") else None))
            lines.append("  SDK tracked={} data={} confidence={} inferred={} controller={}".format(
                raw.get("tracked"), raw.get("dataValid"), raw.get("highConfidence"),
                raw.get("poseInferred"), raw.get("controllerInHandState", "--")))
            lines.append("  " + str(raw.get("status", "--")))
        lines.append(side.upper() + " " + landmark + " TABLE: " + vector(sample.get(side + "HandTablePosition") if valid else None))
        lines.append("  Height gap: " + (number(sample.get(side + "GapMeters"), 100) + " cm" if gap_valid else "--") +
                     " | tracked={} visible={} confident={}".format(sample.get(side + "Tracked", "--"),
                     sample.get(side + "Visible", "--"), sample.get(side + "HighConfidence", "--")))
        lines.append("  " + str(sample.get(side + "Status", "Tracking details not included by this APK")))
        lines.append("  Lift: " + str(sample.get(side + "HistoryStatus", "--")) +
                     " | samples=" + str(sample.get(side + "HistorySampleCount", "--")))
    if not has_xyz:
        lines += ["", "This APK logs height gaps only. XYZ fields will appear after the telemetry update."]
    lines += ["", "CAMERA  playing={}  frame age={} s  ready={}  busy={}".format(
        sample.get("cameraPlaying", "--"), number(sample.get("cameraFrameAgeSeconds"), digits=2),
        sample.get("senderReady", "--"), sample.get("senderBusy", "--")),
        "CAPTURE  allowed={}  pending approach={}".format(sample.get("captureAllowed", "--"), sample.get("pendingApproach", "--")),
        "  " + str(sample.get("actionStatus", "--")),
        "  " + str(sample.get("senderStatus", "--")),
        "", "Frame {}  |  {}".format(sample.get("frame", "--"), sample.get("timestampUtc", "--")),
        "Invalid or stale positions are shown as --, never as zero distance.",
        "Ctrl+C stops this monitor only. The Quest app remains running."]
    return "\n".join(lines)


def adb(args, *command):
    result = subprocess.run([args.adb, "-s", args.serial, "shell", *command],
                            capture_output=True, text=True, timeout=4)
    if result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip()[:160])
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--adb", default=ADB)
    parser.add_argument("--package", default="org.braillelab.storydemo")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.]+", args.package):
        parser.error("Invalid app package")
    directory = "/sdcard/Android/data/" + args.package + "/files"
    output = PROJECT / "runtime" / "hand-table-monitor"
    output.mkdir(parents=True, exist_ok=True)
    sample = None
    previous = None
    identity = None
    path = None
    last_new = time.monotonic()
    next_discovery = 0
    fps = None
    if sys.stdout.isatty():
        print("\033]0;Story Bricks - Live Hand Positions\007", end="", flush=True)
    try:
        while True:
            started = time.monotonic()
            error = ""
            try:
                if started >= next_discovery or path is None:
                    names = adb(args, "ls", "-t", directory).splitlines()
                    names = [n.strip() for n in names if LOG_NAME.fullmatch(n.strip())]
                    if not names:
                        raise RuntimeError("Waiting for the app's first diagnostic file")
                    selected = directory + "/" + names[0]
                    if selected != path:
                        path = selected
                        sample = previous = identity = None
                        fps = None
                    next_discovery = started + 2
                rows = []
                for line in adb(args, "tail", "-n", "12", path).splitlines():
                    try:
                        row = json.loads(line)
                        if row.get("type") == "depth_sample":
                            rows.append(row)
                    except (ValueError, AttributeError):
                        pass
                if rows:
                    row = rows[-1]
                    key = (row.get("sessionId"), row.get("frame"), row.get("monotonicTime"))
                    if key != identity:
                        previous, sample = sample, row
                        identity = key
                        last_new = time.monotonic()
                        if previous and sample["sessionId"] == previous["sessionId"]:
                            dt = sample["monotonicTime"] - previous["monotonicTime"]
                            if dt > 0:
                                fps = (sample["frame"] - previous["frame"]) / dt
                        stream = output / "stream.jsonl"
                        if stream.exists() and stream.stat().st_size > 16 * 1024 * 1024:
                            stream.replace(output / "stream.previous.jsonl")
                        with stream.open("a") as writer:
                            writer.write(json.dumps(sample, separators=(",", ":")) + "\n")
            except (RuntimeError, subprocess.SubprocessError, OSError) as exc:
                error = "CONNECTION: " + str(exc)[:180] + " (retrying)"
                path = None
            text = display(sample, time.monotonic() - last_new, fps)
            if error:
                text = error + "\n\n" + text
            temporary = output / "latest.txt.tmp"
            temporary.write_text(text + "\n")
            temporary.replace(output / "latest.txt")
            if sys.stdout.isatty():
                print("\033[H\033[2J" + text, flush=True)
            elif args.once or identity:
                print(text + "\n", flush=True)
            if args.once:
                break
            time.sleep(max(.05, .5 - (time.monotonic() - started)))
    except KeyboardInterrupt:
        print("\nMonitor stopped; Quest app unchanged.")


if __name__ == "__main__":
    main()
