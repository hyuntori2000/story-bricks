#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
if [[ $# -ne 1 ]]; then
  "$adb_tool" devices -l
  echo "Usage: Tools/connect-quest.sh QUEST_SERIAL"
  echo "Enable Developer Mode, connect USB, and accept USB debugging in the headset."
  exit 1
fi
serial="$1"
[[ "$("$adb_tool" -s "$serial" get-state)" == device ]]
"$adb_tool" -s "$serial" reverse tcp:8766 tcp:8766
"$adb_tool" -s "$serial" reverse tcp:8769 tcp:8769
"$adb_tool" -s "$serial" reverse --list
echo "USB routes ready: speech 8766 and capture/OCR 8769."
echo "To install a locally built team APK: bash Tools/install-physical-story.sh $serial"
