#!/usr/bin/env python3
"""Read-only terminal monitor. No generation requests or device mutations."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
SESSIONS = ROOT / 'runtime/workspaces/create-world/sessions'
DEFAULT_ADB = '/Applications/Unity/Hub/Editor/6000.3.28f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb'


def clean(value, length=120):
    return ''.join(c if c.isprintable() else ' ' for c in str(value if value is not None else '-'))[:length]


def read_json(path):
    try:
        if path.stat().st_size > 2 * 1024 * 1024:
            return {}
        value = json.loads(path.read_text())
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def newest(pattern, count=3):
    paths = []
    for path in SESSIONS.glob(pattern):
        try:
            paths.append((path.stat().st_mtime, path))
        except OSError:
            pass
    return [p for _, p in sorted(paths, reverse=True)[:count]]


def stamp(path):
    try:
        return datetime.fromtimestamp(path.stat().st_mtime).strftime('%H:%M:%S')
    except OSError:
        return '--:--:--'


def health(port):
    try:
        with urlopen('http://127.0.0.1:%d/health' % port, timeout=1.5) as reply:
            data = json.loads(reply.read(256 * 1024))
        return data if isinstance(data, dict) else None
    except (OSError, ValueError):
        return None


def device(adb, serial):
    def run(args):
        return subprocess.run([adb] + args, capture_output=True, text=True, timeout=2)
    try:
        if not serial:
            rows = [line.split() for line in run(['devices']).stdout.splitlines()[1:]]
            connected = [row[0] for row in rows if len(row) >= 2 and row[1] == 'device']
            if len(connected) != 1:
                return 'Connect one authorized Quest by USB.'
            serial = connected[0]
        state = run(['-s', serial, 'get-state'])
        if state.returncode or state.stdout.strip() != 'device':
            return clean(serial) + ' | OFFLINE / USB authorization needed'
        pid = run(['-s', serial, 'shell', 'pidof', 'org.braillelab.storydemo']).stdout.strip()
        return clean(serial) + (' | app running (PID ' + clean(pid) + ')' if pid else ' | app not running')
    except (OSError, subprocess.TimeoutExpired):
        return 'ADB unavailable or device not responding.'


def recognition_rows(count):
    files = newest('*/recognition/history.jsonl', 1)
    if not files:
        files = [ROOT / 'runtime/physical-story-captures/recognition-history.jsonl']
    try:
        with files[0].open('rb') as stream:
            stream.seek(max(0, files[0].stat().st_size - 128 * 1024))
            lines = stream.read().decode('utf-8', errors='replace').splitlines()
    except OSError:
        return []
    rows, seen = [], set()
    for line in reversed(lines):
        try:
            item = json.loads(line)
            key = (item.get('sessionId'), item.get('frameId'))
            if key in seen:
                continue
            seen.add(key)
            branches = []
            for name in ('ocr', 'braille'):
                branch = item.get(name) or {}
                branches.append('%s=%s (%s ms, %s)' % (name, branch.get('word') or branch.get('status', '-'),
                                                      branch.get('elapsedMs', '-'), branch.get('reason', '-')))
            rows.append('%s via %s | %s%s' % (item.get('selectedWord') or 'NO WORD', item.get('selectedSource') or '-',
                         ' / '.join(branches), ' | DISAGREE' if item.get('disagreement') else ''))
            if len(rows) >= count:
                break
        except (ValueError, AttributeError, TypeError):
            continue
    return rows


def snapshot(pool, args):
    voice_future = pool.submit(health, 8766)
    recognition_future = pool.submit(health, 8769)
    device_future = pool.submit(device, args.adb, args.serial)
    voice, recognition, quest = voice_future.result(), recognition_future.result(), device_future.result()
    width, height = shutil.get_terminal_size((125, 40))
    count = 2 if height < 34 else 3
    images = (voice or {}).get('wordImages') or {}
    videos = (voice or {}).get('storyVideos') or {}
    lines = ['STORY BRICKS | LIVE STATUS | ' + datetime.now().astimezone().strftime('%H:%M:%S %Z'),
             'Read-only. Ctrl+C closes this monitor; the app and servers keep running.',
             'Quest: ' + quest,
             'Servers: voice/media 8766=%s | recognition 8769=%s | speech=%s | SFX=%s' % (
                 'UP' if voice else 'OFFLINE', 'UP' if recognition is not None else 'OFFLINE',
                 'configured' if (voice or {}).get('configured') else 'unavailable',
                 'configured' if (voice or {}).get('soundEffectsConfigured') else 'unavailable'),
             'Pending jobs: images=%s | films=%s' % (images.get('wordImagesPending', '-'), videos.get('storyVideosPending', '-'))]
    states = newest('*/session-state.json', 1)
    if states:
        record = read_json(states[0]); state = record.get('state') or {}
        lines.append('Session %s | %s | ended=%s | words=%s' % (record.get('experienceSessionId', '')[:8],
                     state.get('stage', '-'), state.get('ended', False), len(state.get('words') or [])))
    sections = [('RECOGNITION — most recent saved session; not a new capture', recognition_rows(count))]
    jobs = []
    for path in newest('*/requests/*.json', count):
        row = read_json(path); result = row.get('result') or {}
        jobs.append('%s %s | %s HTTP %s | %s' % (stamp(path), row.get('operation', '-'), row.get('status', '-'),
                    row.get('httpStatus', '-'), result.get('code') or result.get('error') or 'OK'))
    sections.append(('RECENT API REQUESTS', jobs))
    jobs = []
    for path in newest('*/images/*.json', count):
        row = read_json(path)
        jobs.append('%s %s | %s | %s' % (stamp(path), row.get('word', '-'), row.get('status', '-'), row.get('code') or '-'))
    sections.append(('WORD IMAGES', jobs))
    jobs = []
    for path in newest('*/videos/*/job.json', count):
        row = read_json(path); shots = row.get('shots') or []
        ready = sum(shot.get('status') == 'ready' for shot in shots)
        jobs.append('%s %s | %s | scenes %d/%d | %s' % (stamp(path), row.get('jobId', '')[:8],
                    row.get('status', '-'), ready, len(shots), row.get('code') or '-'))
    sections.append(('STORY FILMS', jobs))
    for title, rows in sections:
        lines.extend(['', title] + (rows or ['No saved events yet.']))
    lines.extend(['', 'Saved rows remain visible after restart. Monitor: http://127.0.0.1:8769/'])
    return '\n'.join(clean(line, max(40, width - 1)) for line in lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--serial', default='')
    parser.add_argument('--adb', default=os.environ.get('ADB', DEFAULT_ADB))
    parser.add_argument('--once', action='store_true', help='Print one read-only snapshot and exit.')
    args = parser.parse_args()
    interactive = sys.stdout.isatty() and not args.once
    try:
        if interactive:
            print('\033[?25l', end='', flush=True)
        with ThreadPoolExecutor(max_workers=3) as pool:
            while True:
                start = time.monotonic()
                view = snapshot(pool, args)
                print(('\033[H\033[2J' if interactive else '') + view, flush=True)
                if args.once:
                    break
                time.sleep(max(.1, 2 - (time.monotonic() - start)))
    except KeyboardInterrupt:
        pass
    finally:
        if interactive:
            print('\033[?25h\nMonitor closed. App and services are still running.', flush=True)


if __name__ == '__main__':
    main()
