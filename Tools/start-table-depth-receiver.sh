#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
export TABLE_DEPTH_OUTPUT="$project_dir/runtime/table-depth-captures"
camera_python="$project_dir/Python/.camera-venv/bin/python"
if [[ ! -x "$camera_python" ]]; then
  python3 -m venv Python/.camera-venv
fi
if ! "$camera_python" -c 'import cv2, numpy' >/dev/null 2>&1; then
  "$camera_python" -m pip install -r Python/requirements-camera.txt
fi
exec "$camera_python" -u Python/table_depth_receiver.py --port 8768 --output-dir "$TABLE_DEPTH_OUTPUT" --recognizer table_roi_adapter:recognize
