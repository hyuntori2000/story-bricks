#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
exec /usr/bin/python3 -u Python/hand_table_monitor.py --serial "${QUEST_SERIAL:-340YC10G960LYD}"
