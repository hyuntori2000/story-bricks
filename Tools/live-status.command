#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
python_tool="$project_dir/Python/.venv/bin/python"
[[ -x "$python_tool" ]] || python_tool="$(command -v python3)"
exec "$python_tool" Tools/live-status.py "$@"
