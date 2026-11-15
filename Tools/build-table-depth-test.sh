#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
unity_editor="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity}"
mkdir -p "$project_dir/Logs"
"$unity_editor" -batchmode -projectPath "$project_dir" -buildTarget Android \
  -executeMethod BrailleSoundDemo.Editor.BuildTableDepthTest.BuildQuest -quit \
  -logFile "$project_dir/Logs/table-depth-test-build.log"
echo "Table depth diagnostic APK: $project_dir/Builds/Quest/StoryBricksTableDepthTest.apk"
echo "Nothing has been installed on a headset."
