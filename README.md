# Words to Worlds — shared functions

This repository shares the working functions. The designers build their own UX and dashboard.

| Folder | Owner / purpose |
| --- | --- |
| `Assets/BrailleSoundDemo/` | Protected Unity core: capture, word anchors, conversation and media functions. |
| `Python/` | Protected backend: local OCR, speech and API integration. |
| `Assets/DesignerWorkspace/` | Designers: Unity scenes, UX prefabs, art and media. |
| `Dashboard/` | Designers: their own dashboard implementation. |
| `Design/` | Designers: storyboard and content references. |

**Do not modify recognition or Demo logic for a design task.** Read [AGENTS.md](AGENTS.md). Request shared-function changes from the project owner.

## Use the Unity functions

Use **Unity 6000.3.28f1** with Android Build Support and **Git LFS**. Run `git lfs install` and `git lfs pull` after cloning. Open `Assets/Scenes/PhysicalStoryDemo.unity`; save a designer-owned scene copy under `Assets/DesignerWorkspace/` before adding UX.

Attach **StoryFunctionBridge** to your UI and bind its public methods to your own buttons. Attach **WordMediaPresenter** to your media prefab and assign that prefab to the coordinator. Local images/audio/video work without a media dashboard. See [Component connections](Documentation/COMPONENTS.md).

Both live and prepared modes use the shared double-pinch capture, three-second wait and cropped-image recognition. Word boxes can be created during conversation/playback. Live stories continue until a **recognized period**: a standalone `.` ends the preceding word queue without adding a question, and a word ending in `.` ends after that word's answer. Earlier queued questions and answers finish before story generation. There is no three/four-word automatic ending; later captures remain available for the next story. Prepared mode does not automatically call the conversation API.

The physical Quest app builds in **Hands Only** mode with high-frequency tracking and both hand inputs enabled. Controllers are not required. Every build reapplies hand, passthrough, camera and scene capabilities, including when opening an existing scene. Headset sleep, tracking loss or a system permission dialog can still pause tracking.

The app opens directly into the existing capture view. Its small **Finish** button opens an English dialog with **Create Story**, **End Session** and **Cancel**. Create Story immediately calls the real story backend using accepted words (including the conversation queue) and only answers already saved; unfinished speech is discarded. At least one accepted word is required. End Session cancels local requests/playback and clears the active session back to the initial capture state without deleting archived captures. Cancel returns to the session. Repeated Create Story clicks cannot submit a second story while one is running; a failed manual request keeps saved progress for retry. Menu pinches do not trigger the capture gesture.

## Backend setup

Install **Python 3.12**, then run:

```sh
python3.12 Tools/setup-team.py --backend
```

Copy `.env.local.example` to `.env.local` and `.env.elevenlabs.example` to `.env.elevenlabs.local` on the backend machine; supply your own authorized keys. Never commit keys. In separate terminals:

```sh
bash Tools/start-physical-story-receiver.sh
bash Tools/start-backend.sh
```

The existing capture monitor opens at **http://127.0.0.1:8769/**. Connect/build/install a Quest with:

```sh
bash Tools/connect-quest.sh YOUR_QUEST_SERIAL
bash Tools/build-physical-story.sh
bash Tools/install-physical-story.sh YOUR_QUEST_SERIAL
```

These commands build the shared baseline scene. To test your own UX scene on Quest, ask the owner to select that scene for a separate build; the baseline build script does not include designer scene copies automatically.

The scripts support `UNITY_EDITOR` and `ADB` overrides. Designers do not need API keys to edit prefabs or prepared media. The OCR source is pinned in `Config/recognition-source.json` and installed under ignored `External/LEGO_OCR`.

### Parallel recognition experiment (development branch)

The receiver now defaults to `--recognition parallel`. OCR and six-dot reading run concurrently on the same saved native target crop. The first complete accepted word is returned to Unity; a fast failure waits for the other reader. Both failing requests a new photo. A later result never replaces the selected word.

The pinned upstream OCR and braille readers are unchanged. Their supplied `CombinedReader` has OCR-priority behavior, so this experiment uses a separate first-success dispatcher. Each branch uses the existing automatic full-face locator. If faces cannot be resolved, the OCR branch retains automatic printed-label detection with the upstream model and normalization rules; the dashboard records which path ran. OCR's upstream score threshold is 0.85. Only A–Z and periods are accepted; a standalone period is an explicit finish command. No dictionary correction is applied.

The existing capture monitor has a plain history table showing the winner, both results/failure reasons, both elapsed times, and disagreement. The slower reader always finishes. Elapsed time starts at receiver recognition entry and includes any previous-worker wait, crop preparation, geometry and decoding; it excludes the three-second capture timer, network upload and archive saving. `executionMs` additionally records each worker's execution time. These are latency measurements, not measured recognition accuracy.

All updates append to `runtime/physical-story-captures/recognition-history.jsonl` and survive a receiver restart. The dashboard shows the latest 200 captures through `/recognition-history.json`; logs and images stay out of Git. Only one pair of readers is active at a time, so a new capture drains the previous late result before starting. Use `--recognition ocr` or `--recognition braille` for a single-reader comparison.

Focused offline checks: `Python/.ocr-venv/bin/python -m unittest discover -s Python -p test_parallel_recognition.py`. Real Quest captures are still needed before recommending this experiment for `main`.

## Keep changes separate

Owner/backend development uses `dev/core-functions` (or `dev/<task>` for a separate task). Commit and push development changes there. Keep `main` as the shared baseline; recommend merging after the relevant checks and device testing, and merge only when the owner explicitly requests it.

Designers use `design/*` branches and only their three folders above. Preserve Unity `.meta` files. The repository includes English AI instructions, CODEOWNERS and a design-boundary check. Those are review controls; they are not a local filesystem lock. Do not merge core changes without the owner's review.

GitHub declined enforced branch protection for this private repository on the current account plan. Review design pull requests manually before merging, and do not push design work directly to `main`.

Capture files, local keys, virtual environments, generated builds and temporary experiments are excluded from Git. Unity scenes are preserved during normal builds.

The supplied Judy reference is **JUDY → FOX → RAIN → TREE**. Actual designer UX/media files still need to be added. Automatic Orbis generation and public video sharing are not connected. Extra dashboard/UX scaffolding is intentionally not included in this simple handoff.
