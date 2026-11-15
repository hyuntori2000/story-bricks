# Story Bricks

This repository combines the supplied Story Bricks watercolour UX with the working capture/recognition core. **Judy** plays the supplied 61.5-second Unity cutout animation, narration and sound track. **Create Your World** uses live word-building, conversation and generated animation. Designers own presentation assets; the shared core remains protected.

## Current demo checkpoint — November 15, 2026

The current implementation is on **`dev/demo-flow-story-library`**. `main` remains the shared baseline and has not received these development changes. Clone the current demo directly:

```sh
git clone --branch dev/demo-flow-story-library https://github.com/hyuntori2000/story-bricks.git
git -C story-bricks lfs install
git -C story-bricks lfs pull
```

The checkpoint tag **`story-bricks-demo-2026-11-15`** preserves this source version. Check out that tag in a clean checkout to inspect or rebuild it; create a new development branch before editing.

- The latest Quest APK built and installed successfully. The focused single-pinch input verification passed; a real Quest log confirmed **Create Story** selection and story generation. Home still needs direct device confirmation.
- Recognition uses parallel OCR and braille with a bounded English OCR retry. These changes are experimental; the capture samples are not a general recognition-accuracy benchmark.
- **Known image-display limitation:** a tracking-origin reset cancels pending Unity image delivery and hides old spatial anchors. The Mac can finish and archive the image, but the app does not automatically restore it. This explains the latest FOX image missing in VR even though generation succeeded. This checkpoint records the issue; it does not fix it.
- Comfy images, two-scene story videos, speech and effects require the existing local backend configuration. External generation time and output quality vary. Review generated dialogue and story text before filming; the latest saved story included a stray non-English/self-correction fragment.
- Secrets, captures, generated media, local environments and APKs are not part of this source checkpoint. Configure your own ignored local environment files using the setup below.

Resolve or explicitly accept these demo limitations before merging into `main`.

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

Create World uses the shared double-pinch capture, three-second wait and cropped-image recognition. Entering Create World plays its fixed introduction once. The short demo conversation repeats in word pairs: first word → spelling and one question → one spoken answer → invitation to build a related second word → spelling and a brief closing. The closing says **“Keep building another word, or press Create Story to make your story.”** A third word starts the next pair; no word-count limit ends the story automatically. Every newly accepted word is spelled once, including repeated letters (for example, **“M, O, O, N. Moon!”**); the trailing period is not spoken. The explicit **`build_word`** state keeps the microphone closed and consumes queued captures when ready. **`speak_answer`** opens the microphone only after the narrator finishes.

Each accepted word, including the first, starts an optional sound effect using only the recognized word (no spoken-answer context). Recognizable sound sources receive concrete descriptions, such as a cat meowing or a few acoustic piano notes. Common words use a local description catalog; other words use a word-only sound-description request. People, quiet creatures and objects without an inherent sound request only a brief, very quiet non-vocal hum with a soft fade; the prompt excludes invented speech, breathing, footsteps or handling sounds for these quiet subjects. Bounded audio leveling improves weak audible effects without clipping; near-silent output is reported as unavailable. Quiet-subject hums are never boosted. Generated sound quality still depends on the provider. The effect plays from its box as soon as it is ready, including during questions, recording or story narration, at 70% of the previous effect gain. Speech volume is unchanged. Manual Create Story preserves these jobs; ending/resetting the session cancels them. Headset pause or unavailable spatial tracking pauses playback.

Word images run independently through the supplied Comfy Cloud card workflow. Each recognized word (except a standalone period) starts a background job while conversation and sound generation continue. The PNG appears on that word's transparent 65° plane, keeping its aspect ratio and its own alpha channel. Before the image arrives, the plane is invisible rather than a white placeholder. Displaying the image requests one replay of the existing word effect at the same reduced volume; if the effect is still preparing or playing, the replay waits for that real clip and its first playback to finish. No second sound-generation request is made. Reset/end or invalid tracking prevents late image delivery and replay on an old anchor; completed images remain archived on the Mac.

Comfy runs only on the Mac voice backend. Copy `.env.comfy.example` to ignored `.env.comfy.local` and set `COMFY_API_KEY` there, then restart `Tools/start-backend.sh`. The APK contains only local job IDs and image paths. `POST /word-image` queues work; `GET /word-image` polls the same owned request, and `/word-images/<id>.png` serves the saved image. Each experience stores image jobs and PNGs under `runtime/workspaces/create-world/sessions/<experienceSessionId>/images/`. Two workers and a bounded queue are separate from voice and effect capacity. Retried local requests reuse their IDs; ambiguous upstream submissions are never automatically sent again. Health checks and menu entry do not generate images. The supplied card graph is in `Python/workflows/comfy/card_api.json`; its image model and alpha-removal nodes are preserved. See that folder's README for provider setup and compatibility.

The capture target stays yellow until its photo is acknowledged, then turns blue for two seconds before only its outline becomes invisible. A transparent 20 × 12 cm plane is created at that same position, rising 65° above the original tabletop toward the far side. The plane and sound anchor remain after the outline disappears; later recognition does not restart the two-second timer or create another plane. This presentation never changes the image crop or recognition geometry.

Word boxes can still be captured during dialogue or playback and wait in the queue. Separate captures may repeat the same spelling to answer different build prompts. A recognized standalone `.` lets the current spoken answer and preceding queued words finish, then asks one final question. Its real spoken answer must be saved before the story starts. A word ending in `.` receives spelling and that final question instead of its ordinary question or closing. Duplicate period signals coalesce, and a period before any word cannot generate an empty story. There is no three/four-word automatic ending; later captures remain available for the next story. The generated story contains **exactly seven English sentences**, using the accepted words and actual saved contributions; a separate readiness announcement precedes its narration. Judy does not offer word capture, recognition, per-word cues or conversation API calls.

The physical Quest app builds in **Hands Only** mode with high-frequency tracking and both hand inputs enabled. Controllers are not required. Every build reapplies hand, passthrough, camera and scene capabilities, including when opening an existing scene. Headset sleep, tracking loss or a system permission dialog can still pause tracking.

The app first plays the supplied **Story Bricks opening** (braille dots, title and synthesized chime), followed by the bundled narrator line **“Are you ready for the new worlds? pinch twice!”** in the existing Marin voice. After narration, a fresh double pinch with either hand enters Create World immediately without pointing at a button, finding a table or starting a photo timer. The watercolour **selection menu** remains available at that point, with **Create Your World**, **Judy’s Story** and **See previous story** enabled. Ending a session repeats the invitation; the opening animation plays once per app launch. Onboarding pauses when the app loses focus or the headset sleeps. Selecting Create Your World connects the supplied word tags, captions and listening/progress panels to real capture and dialogue events. Adjacent **Home** and **Create Story** buttons use the supplied painted style. The imported prototype’s automatic words, recorded answers and simulated readiness timers do not run in this route.

**Create Story** directly calls the story backend using accepted words (including queued captures) and only answers already saved; unfinished speech is discarded. At least one accepted word is required, but no period is needed. Story creation keeps the experience open. After narration, the narrator announces the animation and automatically starts the two-scene Comfy job. The finished movie opens in VR and is saved on the Mac, where QuickTime Player opens one preview. **Home** immediately cancels local requests/playback, clears the active words and session, and returns to the landing screen. Saved captures and movies remain on disk. Repeated Create Story presses do not submit a second running story; a failed request retains saved progress for retry. Both buttons are small and placed high on the left. Hovering does not interrupt a photo countdown or queued dialogue; an actual button pinch suppresses capture until that hand releases.

The Judy flow is **select Judy’s Story → play the original 61.5-second Unity cutout story → Watch again / Create Your World / Back to menu**. Narration, subtitles and effects use the supplied timeline. The supplied 12-second MP4 is excluded. Switching routes stops Judy playback before starting the live experience; Judy does not open a microphone or submit recognition/generation requests.

## Story animation

Animation starts only from the current experience's `StoryNarrationCompleted` event, after the story audio has finished. `StoryOutputReady` only archives the story. The Mac backend matches the requested story text and audio path to its own successful story archive, then covers the seven sentences with exactly two chronological shots: sentences 1–4 and 5–7. Each shot lasts 6–10 seconds and uses the supplied `shot_api.json` (Seedance 2.5 Draft, 480p, 16:9). It reuses the current story's saved word-card PNGs as references and never generates replacement cards for a movie. At least one usable reference card is required.

Both shots are submitted before waiting and polled concurrently; actual render concurrency depends on the Comfy account and service queue. ffmpeg then joins and normalizes them into one H.264/AAC MP4. Two minutes is a target, not a timer or guarantee. `POST /story-video` starts or rejoins the owned job, `GET /story-video` polls it, and `/story-videos/<job-id>.mp4` serves the completed movie. Existing saved films keep their original shot plans and are not regenerated by this change. Install the updated Python requirements for the bundled ffmpeg executable. Movie jobs, prompts, shot files and the final MP4 stay in the same experience's `videos/` archive, outside Git. The Mac preview opens only after the final file is saved.

The Quest downloads the final movie before playing it from local storage. Headset sleep or tracking loss pauses playback and resumes it when ready; ending/resetting a session cancels local viewing without deleting archived movies. Cloud renders already submitted can finish into their original archive. Repeated local requests reuse the same job; a paid shot whose submission outcome is unknown is never automatically resubmitted. Failures remain explicit instead of playing a placeholder movie.

**See previous story** opens the saved movie library from the landing menu. Choose a completed film to replay it in VR without recognition, conversation or generation requests. The Mac backend must be reachable. `GET /story-library` lists the latest completed films and refreshes `runtime/workspaces/story-library/index.json`; the MP4 files stay in `runtime/workspaces/create-world/sessions/<experienceSessionId>/videos/<jobId>/story.mp4`. Existing compatible movies are discovered from their archives, including after a backend restart. Home clears only the current viewing/session state, never this library.

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
bash Tools/build-experiences.sh
bash Tools/install-physical-story.sh YOUR_QUEST_SERIAL
```

These commands build the existing physical scene and load the designer-owned presentation prefab. They do not replace the rig or regenerate the saved scene. The one-time presentation importer preserves an already committed prefab on subsequent builds. See [designer presentation setup](Assets/DesignerWorkspace/StoryBricks/README.md).

Launch the main application, Android package **`org.braillelab.storydemo`**. The separate **`com.storybricks.parentui`** application is the original designer UX prototype; its scripted buttons and sample story flow are not the live Create World integration. The install script above launches the main application.

The scripts support `UNITY_EDITOR` and `ADB` overrides. Designers do not need API keys to edit prefabs or prepared media. The OCR source is pinned in `Config/recognition-source.json` and installed under ignored `External/LEGO_OCR`.

### Parallel recognition experiment (development branch)

The receiver now defaults to `--recognition parallel`. OCR and six-dot reading run concurrently on the same saved native target crop. The first complete accepted word is returned to Unity; a fast failure waits for the other reader. Both failing requests a new photo. A later result never replaces the selected word.

The pinned upstream reader code, primary OCR model and braille path are unchanged. Their supplied `CombinedReader` has OCR-priority behavior, so this experiment uses a separate first-success dispatcher. Each branch uses the existing automatic full-face locator. If faces cannot be resolved, OCR uses the existing automatic printed-label path. Only when the existing OCR path rejects a word, the same native crop gets one English-model row retry using the pinned local asset in `Python/models/`. With known faces, every label must match one face in order and agree with already accepted letters or periods; missing labels cannot form a partial accepted word. The dashboard history records the reader path. OCR's threshold remains 0.85 on every character and line. Only A–Z and periods are accepted; a standalone period is an explicit final-question signal. No dictionary correction is applied.

When automatic text detection omits a trailing period, the connection adapter can propose a label region from a confidently aligned letter row and pass the original color pixels to the unchanged upstream `detect_period()` function. Shape results retain their own source and diagnostics, without an invented OCR score. This fallback requires reliable row spacing; standalone periods still require a detected block face or an actual OCR detection. It does not guess a period from an empty capture.

The existing capture monitor has a plain history table showing the winner, both results/failure reasons, both elapsed times, and disagreement. The slower reader always finishes. Elapsed time starts at receiver recognition entry and includes any previous-worker wait, crop preparation, geometry and decoding; it excludes the three-second capture timer, network upload and archive saving. `executionMs` additionally records each worker's execution time. These are latency measurements, not measured recognition accuracy.

All updates append to `runtime/physical-story-captures/recognition-history.jsonl` and survive a receiver restart. The dashboard shows the latest 200 captures through `/recognition-history.json`; logs and images stay out of Git. Only one pair of readers is active at a time, so a new capture drains the previous late result before starting. Use `--recognition ocr` or `--recognition braille` for a single-reader comparison.

Focused offline checks: `Python/.ocr-venv/bin/python -m unittest discover -s Python -p test_parallel_recognition.py`. Real Quest captures are still needed before recommending this experiment for `main`.

## Keep changes separate

Owner/backend development uses `dev/core-functions` (or `dev/<task>` for a separate task). Commit and push development changes there. Keep `main` as the shared baseline; recommend merging after the relevant checks and device testing, and merge only when the owner explicitly requests it.

Designers use `design/*` branches and only their three folders above. Preserve Unity `.meta` files. The repository includes English AI instructions, CODEOWNERS and a design-boundary check. Those are review controls; they are not a local filesystem lock. Do not merge core changes without the owner's review.

GitHub declined enforced branch protection for this private repository on the current account plan. Review design pull requests manually before merging, and do not push design work directly to `main`.

Capture files, local keys, virtual environments, generated builds and temporary experiments are excluded from Git. Unity scenes are preserved during normal builds.

The supplied Judy storyboard remains a content reference. Judy playback uses the authored Unity components from `story-bricks-main-2`, not a recognition-driven word-cue sequence. Create Your World uses the supplied Comfy film workflow; Orbis and public video sharing are not connected.


## Experience storage and rollback

New requests carry immutable `workspaceId` (`judy` or `demo`) and `experienceSessionId`. The active UI uses `demo` for **Create World**; its Mac directory is `create-world`. The backend’s existing `judy` namespace and archived Judy data remain intact for compatibility; authored Judy playback sends no capture or API requests. Camera frame IDs and conversation IDs remain linked sub-identities. Home closes the old experience; late results stay in its archive and cannot enter the new UI.

- Mac records: `runtime/workspaces/{judy,create-world}/sessions/<experience-id>/` contains session-state snapshots, request/results, generated audio, story results and capture state/history.
- Photos: the existing configured `capturedimage2026` directory gains the same mode/session hierarchy, with originals, exact valid crops and metadata under `captures/`. Failed recognition remains archived; invalid crops are recorded as unavailable.
- Quest: `ExperienceSessions/<workspace>/<experience-id>/` retains local session snapshots, generated speech and story exports. A durable outbox synchronizes snapshots to Mac and survives mode changes/offline time. Live app-state restoration after force-kill is not implemented.
- Generated audio for owned experiences is durable; the old 64-file cache is used only by legacy unowned clients. Raw microphone recordings remain transient.
- Old flat archives and old unowned requests remain compatible and are never guessed into a new mode. The capture monitor at port 8769 provides an explicit session selector; its unscoped view is legacy data.

`Home` clears live objects, not saved files. Tracking reset cancels pending captures and invalidates old box positions, while retaining words/answers; old spatial presentation must not be trusted after re-localization. Centered narration can continue. Only Create Your World activates word capture, microphone recording, recognition and conversation requests. Judy playback remains independent of those word-building functions.

Current development branch: `dev/demo-flow-story-library`. To undo this dialogue, Home, library and sound update, use `rollback/pre-demo-flow-20261115` at `e7565ca`, rebuild and restart its voice backend. The earlier UX integration is on `dev/ux-integration`; supplied design content: `design/judy-ux-integration`. The earlier application is also preserved as `rollback/pre-supplied-ux-20261115` at `ad86290`. Save local edits before switching checkpoints. Archived media remain on disk.

Earlier development branch: `dev/guided-dialogue`. The pre-integration source is preserved on `rollback/pre-judy-create-world-20261114` at `4b13db6`. After committing/stashing any current edits, switch to that rollback branch and run its `Tools/build-physical-story.sh` to rebuild the previous app. Restart its Python services when reverting the backend too. Switch back to `dev/guided-dialogue` to resume the current implementation. Existing saved files are not deleted by switching branches. No change has been merged to `main`.

The integration before Judy was simplified is preserved on `rollback/before-judy-video-only-20261114` at `82aa499`, also pushed to GitHub. After saving any current edits, switch to that branch to restore the previous source. The original pre-integration rollback above remains available.

The working Create World integration immediately before guided dialogue is preserved on `rollback/pre-guided-dialogue-20261114` at `f099a8c`, pushed to GitHub. Use that checkpoint to return to the prior conversation behavior while keeping the menu-only UX integration. Guided-dialogue work stays on `dev/guided-dialogue`; merging into `main` still requires the owner's explicit instruction.
