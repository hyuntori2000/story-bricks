#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
apk="$project_dir/Builds/Quest/WordsToWorlds.apk"
if [[ ! -x "$adb_tool" ]]; then
  echo "ADB not found. Set ADB to your Android platform-tools/adb executable."
  exit 1
fi
if [[ ! -f "$apk" ]]; then
  echo "Build the APK first with Tools/build-quest.sh."
  exit 1
fi
# An explicit serial avoids installing on the wrong Android device.
if [[ $# -ne 1 ]]; then
  "$adb_tool" devices -l
  echo "Usage: Tools/install-quest.sh QUEST_SERIAL"
  echo "Enable headset developer mode, connect USB, and allow USB debugging in the headset."
  exit 1
fi
serial="$1"
state="$("$adb_tool" -s "$serial" get-state)"
if [[ "$state" != "device" ]]; then
  echo "The selected headset is not authorized for ADB. Allow USB debugging inside it."
  exit 1
fi
"$adb_tool" -s "$serial" install -r "$apk"
# Quest's implicit launcher intent can fail even when the package is installed.
# Launch the verified Unity activity explicitly and wait for Android's result.
launch_output="$("$adb_tool" -s "$serial" shell am start -W \
  -n org.braillelab.storydemo/com.unity3d.player.UnityPlayerActivity \
  -a android.intent.action.MAIN -c com.oculus.intent.category.VR)"
printf '%s\n' "$launch_output"
if [[ "$launch_output" != *"Status: ok"* ]]; then
  echo "The APK was installed, but Android did not confirm a successful launch."
  exit 1
fi
echo "Press A on the right controller to place the mock board."
echo "Follow Documentation/TESTING.md; headset audio remains unverified until those tests pass."
