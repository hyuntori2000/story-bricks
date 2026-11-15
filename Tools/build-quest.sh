#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
unity_editor="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity}"
if [[ ! -x "$unity_editor" ]]; then
  echo "Unity 6000.3.28f1 was not found. Set UNITY_EDITOR to its executable."
  exit 1
fi
mkdir -p "$project_dir/Logs"
echo "Building the Quest APK. Close this project in Unity first."
"$unity_editor" -batchmode -projectPath "$project_dir" -buildTarget Android \
  -executeMethod BrailleSoundDemo.Editor.BuildDemo.BuildQuest -quit \
  -logFile "$project_dir/Logs/quest-build.log"
echo "APK: $project_dir/Builds/Quest/WordsToWorlds.apk"
echo "A successful build does not verify sound localization on a headset."
