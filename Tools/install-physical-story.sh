#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
if [[ $# -ne 1 ]]; then
  echo "Usage: Tools/install-physical-story.sh QUEST_SERIAL"
  exit 1
fi
serial="$1"
[[ "$("$adb_tool" -s "$serial" get-state)" == device ]] || { echo "Allow USB debugging inside the Quest."; exit 1; }
curl --fail --silent --show-error http://127.0.0.1:8766/health >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8769/health >/dev/null
"$adb_tool" -s "$serial" reverse tcp:8766 tcp:8766
"$adb_tool" -s "$serial" reverse tcp:8769 tcp:8769
"$adb_tool" -s "$serial" install -r "$project_dir/Builds/Quest/WordsToWorldsPhysical.apk"
"$adb_tool" -s "$serial" shell am force-stop org.braillelab.storydemo
"$adb_tool" -s "$serial" shell am start -W -n org.braillelab.storydemo/com.unity3d.player.UnityPlayerActivity -a android.intent.action.MAIN -c com.oculus.intent.category.VR
echo "Allow camera, spatial-data and microphone access."
echo "Shared capture and story functions are running. Capture monitor: http://127.0.0.1:8769/."
