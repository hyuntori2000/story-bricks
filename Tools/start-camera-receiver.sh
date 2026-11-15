#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
camera_python="Python/.camera-venv/bin/python"
if [[ ! -x "$camera_python" ]]; then
  python3 -m venv Python/.camera-venv
fi
if ! "$camera_python" -c 'import cv2, numpy' >/dev/null 2>&1; then
  "$camera_python" -m pip install -r Python/requirements-camera.txt
fi
exec "$camera_python" Python/camera_frame_server.py "$@"
