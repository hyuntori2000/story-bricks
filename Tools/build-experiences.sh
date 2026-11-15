#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
unity_editor="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity}"
mkdir -p "$project_dir/Logs"
"$unity_editor" -batchmode -projectPath "$project_dir" -buildTarget Android \
  -executeMethod BrailleSoundDemo.Editor.BuildExperienceDemo.BuildQuest -quit \
  -logFile "$project_dir/Logs/experience-build.log"
echo "Judy / Create World APK: $project_dir/Builds/Quest/WordsToWorldsPhysical.apk"
