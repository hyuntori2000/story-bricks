#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_dir"
if [[ "${1:-}" == --help ]]; then
  echo "Usage: bash Tools/restart-quest.command [QUEST_SERIAL]"
  echo "Starts missing services, restores USB routes, opens the live screen, restarts the app, and shows live status."
  echo "Active app progress resets; archived stories and media are preserved."
  exit 0
fi
fail() { echo "ERROR: $*" >&2; exit 1; }
adb_tool="${ADB:-/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}"
[[ -x "$adb_tool" ]] || adb_tool="$(command -v adb || true)"
[[ -n "$adb_tool" && -x "$adb_tool" ]] || fail "ADB not found. Install Unity Android Build Support or set ADB."
if [[ $# -gt 1 ]]; then fail "Use --help for instructions."; fi
serial="${1:-}"
if [[ -z "$serial" ]]; then
  devices="$("$adb_tool" devices)"
  count="$(echo "$devices" | awk '$2 == "device" {n++} END {print n+0}')"
  if [[ "$count" != 1 ]]; then
    echo "$devices"
    fail "Connect one Quest by USB and accept USB debugging in the headset. With multiple devices, supply QUEST_SERIAL."
  fi
  serial="$(echo "$devices" | awk '$2 == "device" {print $1}')"
fi
[[ "$("$adb_tool" -s "$serial" get-state)" == device ]] || fail "Quest is not authorized or connected."
mkdir -p runtime/launcher
healthy() { curl --fail --silent --max-time 2 "http://127.0.0.1:$1/health" >/dev/null 2>&1; }
start_service() {
  local port="$1" label="$2" log="$3"
  shift 3
  if healthy "$port"; then echo "$label is already running."; return; fi
  if /usr/sbin/lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    fail "Port $port is occupied but unhealthy. Check the existing service; it was not stopped."
  fi
  echo "Starting $label..."
  nohup "$@" >>"$log" 2>&1 </dev/null &
  local service_pid=$!
  local attempt
  for attempt in {1..20}; do
    if healthy "$port"; then return; fi
    if ! kill -0 "$service_pid" 2>/dev/null; then fail "$label stopped. See $project_dir/$log"; fi
    sleep 1
  done
  fail "$label is not ready yet. See $project_dir/$log, then try this shortcut again."
}
[[ -x Python/.venv/bin/python && -x Python/.ocr-venv/bin/python ]] || fail "Run python3.12 Tools/setup-team.py --backend first."
start_service 8766 "Voice and media backend" runtime/launcher/voice.log Python/.venv/bin/python -u Python/live_backend.py
start_service 8769 "Recognition receiver" runtime/launcher/recognition.log /bin/bash Tools/start-physical-story-receiver.sh
"$adb_tool" -s "$serial" reverse tcp:8766 tcp:8766
"$adb_tool" -s "$serial" reverse tcp:8769 tcp:8769
echo "Opening the Quest live screen..."
ADB="$adb_tool" nohup /bin/bash "$project_dir/Tools/mirror-quest.command" "$serial" >>runtime/launcher/mirror.log 2>&1 </dev/null &
echo "Restarting Story Bricks. Active app progress resets; saved stories and videos remain."
"$adb_tool" -s "$serial" shell am force-stop org.braillelab.storydemo
"$adb_tool" -s "$serial" shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n org.braillelab.storydemo/com.unity3d.player.UnityPlayerActivity
echo "Story Bricks launched. Wear and unlock the headset. Keep USB connected for Mac services."
echo "Capture monitor: http://127.0.0.1:8769/"

exec /bin/bash "$project_dir/Tools/live-status.command" --serial "$serial" --adb "$adb_tool"
