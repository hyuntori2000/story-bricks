#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
color_python="${COLOR_PYTHON:-$project_dir/Python/.color-venv/bin/python}"
if [[ ! -x "$color_python" ]]; then
  echo "A separate Python 3.11 or 3.12 environment is required."
  echo "Create Python/.color-venv and install ../LEGO_OCR/requirements.txt, or set COLOR_PYTHON."
  exit 1
fi
"$color_python" -c 'import sys, cv2, numpy; assert sys.version_info[:2] in ((3, 11), (3, 12)), "Use Python 3.11 or 3.12"; assert hasattr(cv2, "aruco"), "Install the original opencv-contrib-python requirement"'
exec "$color_python" -u Python/camera_frame_server.py --recognizer lego_color_adapter:recognize "$@"
