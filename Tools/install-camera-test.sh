#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
if [[ $# -ne 1 ]]; then
  echo "Usage: Tools/install-camera-test.sh QUEST_SERIAL"
  echo "Installs only org.braillelab.cameratest. The story demo is not replaced."
  exit 1
fi
serial="$1"
apk="$project_dir/Builds/Quest/WordsToWorldsCameraTest.apk"
[[ -f "$apk" ]] || { echo "Build the camera test first."; exit 1; }
[[ "$("$adb_tool" -s "$serial" get-state)" == device ]] || { echo "Allow USB debugging inside the Quest."; exit 1; }
curl --fail --silent http://127.0.0.1:8767/health >/dev/null || { echo "Start Tools/start-camera-receiver.sh first."; exit 1; }
"$adb_tool" -s "$serial" reverse tcp:8767 tcp:8767
"$adb_tool" -s "$serial" install -r "$apk"
launch_output="$("$adb_tool" -s "$serial" shell am start -W -n org.braillelab.cameratest/com.unity3d.player.UnityPlayerActivity -a android.intent.action.MAIN -c com.oculus.intent.category.VR)"
printf '%s\n' "$launch_output"
[[ "$launch_output" == *"Status: ok"* ]] || { echo "Android did not confirm launch."; exit 1; }
echo "Allow camera access. Hold the right controller near the block-top height and press A to set the reference plane."
echo "Use the right thumbstick to adjust the visible plane height, then press the RIGHT INDEX TRIGGER to send one new frame."
echo "B clears the plane and labels. New captures replace previous labels."
echo "Open http://127.0.0.1:8767/ on this Mac to select brick corners and test colors."
echo "Recognition results return to Quest as world-positioned letter overlays. Unknown colors show ?."
echo "This version uses manual capture and manual image regions, with no new color calibration."
