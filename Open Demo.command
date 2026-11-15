#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")" && pwd)"
app="$project_dir/Builds/Mac/Words to Worlds.app"
if [[ ! -d "$app" ]]; then
  echo "The Mac app is not included here. Open the Unity project and choose Word Story Demo > Build Mac Demo."
  exit 1
fi
open "$app"
