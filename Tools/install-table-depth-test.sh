#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
if [[ $# -ne 1 ]]; then
  echo "Usage: Tools/install-table-depth-test.sh QUEST_SERIAL"
  echo "Installs only org.braillelab.tabledepthtest."
  exit 1
fi
serial="$1"
apk="$project_dir/Builds/Quest/StoryBricksTableDepthTest.apk"
[[ -f "$apk" ]] || { echo "Build the table depth test first."; exit 1; }
[[ "$("$adb_tool" -s "$serial" get-state)" == device ]] || { echo "Allow USB debugging inside the Quest."; exit 1; }
curl --fail --silent --show-error http://127.0.0.1:8768/health >/dev/null || {
  echo "Start Tools/start-table-depth-receiver.sh on port 8768 before installing."
  exit 1
}
"$adb_tool" -s "$serial" reverse tcp:8768 tcp:8768
"$adb_tool" -s "$serial" install -r "$apk"
launch_output="$("$adb_tool" -s "$serial" shell am start -W -n org.braillelab.tabledepthtest/com.unity3d.player.UnityPlayerActivity -a android.intent.action.MAIN -c com.oculus.intent.category.VR)"
printf '%s\n' "$launch_output"
[[ "$launch_output" == *"Status: ok"* ]] || { echo "Android did not confirm launch."; exit 1; }
echo "Allow spatial-data and camera access. Look at a clear, flat part of the tabletop."
echo "The app estimates distance automatically; no A-button plane placement is needed."
echo "Put the controllers aside to inspect tracked hand distance above the measured surface."
echo "Keep USB connected for single-frame delivery to the capture-only Python receiver."
echo "The app does not recognize blocks or words, call AI APIs, or adjust optical autofocus."
