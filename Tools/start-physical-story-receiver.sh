#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
physical_python="${PHYSICAL_PYTHON:-$project_dir/Python/.ocr-venv/bin/python}"
[[ -x "$physical_python" ]] || { echo "Run python3.12 Tools/setup-team.py --backend to install the pinned OCR dependencies."; exit 1; }
"$physical_python" -c 'import sys, cv2, numpy, rapidocr_onnxruntime; assert sys.version_info[:2] == (3, 12), "Use Python 3.12"'
exec "$physical_python" -u Python/physical_story_receiver.py --port 8769 \
  --output-dir "$project_dir/runtime/physical-story-captures" "$@"
