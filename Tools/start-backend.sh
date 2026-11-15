#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
if [[ ! -x Python/.venv/bin/python ]]; then
  python3 -m venv Python/.venv
fi
Python/.venv/bin/python -m pip install -q -r Python/requirements.txt
exec Python/.venv/bin/python Python/live_backend.py
