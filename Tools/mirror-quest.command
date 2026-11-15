#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
mirror_tool="${SCRCPY:-$(command -v scrcpy || true)}"
[[ -n "$mirror_tool" ]] || mirror_tool=/opt/homebrew/bin/scrcpy
[[ -x "$mirror_tool" ]] || { echo "Install the Mac mirror once: brew install scrcpy"; exit 1; }
[[ -x "$adb_tool" ]] || { echo "ADB unavailable. Set ADB to the installed platform-tools executable."; exit 1; }
serial="${1:-}"
if [[ -z "$serial" ]]; then
  devices="$("$adb_tool" devices)"
  count="$(echo "$devices" | awk '$2 == "device" {n++} END {print n+0}')"
  [[ "$count" == 1 ]] || { echo "$devices"; echo "Connect one authorized Quest, or pass its serial."; exit 1; }
  serial="$(echo "$devices" | awk '$2 == "device" {print $1}')"
fi
mkdir -p runtime/launcher
mirror_pid="$(cat runtime/launcher/mirror.pid 2>/dev/null || true)"
mirror_command=""
if [[ "$mirror_pid" =~ ^[0-9]+$ ]]; then
  mirror_command="$(ps -p "$mirror_pid" -o command= 2>/dev/null || true)"
fi
if [[ "$mirror_command" == *scrcpy* && "$mirror_command" == *"--serial=$serial"* ]]; then
  echo "The Quest live window is already running."
  exit 0
fi
printf '%s\n' "$$" > runtime/launcher/mirror.pid
export ADB="$adb_tool"
echo "Quest live view: wear and unlock the headset. Audio remains in the headset."
echo "Closing the mirror does not stop Story Bricks or the Mac servers."
exec "$mirror_tool" --serial="$serial" --no-control --no-audio --max-size=1600 --max-fps=30 --video-bit-rate=4M --window-title="Story Bricks - Quest Live"
