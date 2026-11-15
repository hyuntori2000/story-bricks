# Words to Worlds — shared functions

This repository contains the working capture/recognition core and the active **Create World** experience for live word-building and conversation. **Judy** is reserved for a future single prepared video of approximately 30 seconds; its current menu entry is disabled and labelled **Coming soon**. Designers own the presentation assets; the shared core remains protected.

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

Create World uses the shared double-pinch capture, three-second wait and cropped-image recognition. Entering Create World starts its fixed spoken introduction once; opening the selection menu does not. Guided dialogue then requests either **`build_word`** or **`speak_answer`**. A build prompt keeps the microphone closed and consumes the next accepted word only when that prompt is ready. A spoken-answer prompt opens answer capture after its audio finishes. Every newly accepted word, including the first, is spelled aloud once before its acknowledgment and a spoken-answer question (for example, **“M, O, O, N. Moon!”**). Spelling and question share one narrator clip. Repeated letters are kept, the trailing period is not spoken, and spoken-answer follow-ups do not repeat the spelling. After hearing the child's answer, the narrator may ask one useful follow-up before inviting a next word connected to that contribution. It does not immediately request another build after accepting the first word. Manual finish can still use words with no saved answers.

Each accepted word, including the first, starts an optional sound effect using only the recognized word (no spoken-answer context). Recognizable sound sources such as cats, instruments, engines and wind request their characteristic sound. People, quiet creatures and objects without an inherent sound request only a brief, very quiet non-vocal hum with a soft fade; the prompt excludes invented speech, breathing, footsteps or handling sounds for these quiet subjects. Generated audio quality still depends on the sound provider. The effect plays from its box as soon as it is ready, including during questions, recording or story narration, at 70% of the previous effect gain. Speech volume is unchanged. Manual Create Story preserves these jobs; ending/resetting the session cancels them. Headset pause or unavailable spatial tracking pauses playback.

Word images run independently through the supplied Comfy Cloud card workflow. Each recognized word (except a standalone period) starts a background job while conversation and sound generation continue. The PNG appears on that word's transparent 65° plane, keeping its aspect ratio and its own alpha channel. Before the image arrives, the plane is invisible rather than a white placeholder. Displaying the image requests one replay of the existing word effect at the same reduced volume; if the effect is still preparing or playing, the replay waits for that real clip and its first playback to finish. No second sound-generation request is made. Reset/end or invalid tracking prevents late image delivery and replay on an old anchor; completed images remain archived on the Mac.

Comfy runs only on the Mac voice backend. Copy `.env.comfy.example` to ignored `.env.comfy.local` and set `COMFY_API_KEY` there, then restart `Tools/start-backend.sh`. The APK contains only local job IDs and image paths. `POST /word-image` queues work; `GET /word-image` polls the same owned request, and `/word-images/<id>.png` serves the saved image. Each experience stores image jobs and PNGs under `runtime/workspaces/create-world/sessions/<experienceSessionId>/images/`. Two workers and a bounded queue are separate from voice and effect capacity. Retried local requests reuse their IDs; ambiguous upstream submissions are never automatically sent again. Health checks and menu entry do not generate images. The supplied card graph is in `Python/workflows/comfy/card_api.json`; its image model and alpha-removal nodes are preserved. See that folder's README for provider setup and compatibility.

The capture target stays yellow until its photo is acknowledged, then turns blue for two seconds before only its outline becomes invisible. A transparent 20 × 12 cm plane is created at that same position, rising 65° above the original tabletop toward the far side. The plane and sound anchor remain after the outline disappears; later recognition does not restart the two-second timer or create another plane. This presentation never changes the image crop or recognition geometry.

Word boxes can still be captured during dialogue or playback and wait in the queue. Separate captures may repeat the same spelling to answer different build prompts. A recognized standalone `.` requests completion without interrupting the current spoken answer or adding another follow-up; preceding queued words are handled before the marker is consumed. A word ending in `.` still receives its final question and saved answer before the story. There is no three/four-word automatic ending; later captures remain available for the next story. The generated story contains **exactly seven English sentences**, using the accepted words and actual saved contributions; a separate readiness announcement precedes its narration. Judy does not offer word capture, recognition, per-word cues or conversation API calls.

The physical Quest app builds in **Hands Only** mode with high-frequency tracking and both hand inputs enabled. Controllers are not required. Every build reapplies hand, passthrough, camera and scene capabilities, including when opening an existing scene. Headset sleep, tracking loss or a system permission dialog can still pause tracking.

The app first plays the supplied **Story Bricks opening** (braille dots, title and synthesized chime), followed by the bundled narrator line **“Are you ready for the new worlds? pinch twice!”** in the existing Marin voice. After narration, a fresh double pinch with either hand enters Create World immediately without pointing at a button, finding a table or starting a photo timer. The watercolour **selection menu** remains available at that point, with **Create World** enabled and **Judy — Coming soon** disabled. Ending a session repeats the invitation; the opening animation plays once per app launch. Onboarding pauses when the app loses focus or the headset sleeps. Selecting Create World closes that view and returns to the original capture, dialogue and word-box experience with the existing small Finish button. The imported word tags, captions, busy/ready panels and authored story screens are not shown during Create World.

The existing **Finish** button opens an English dialog with **Create Story**, **End Session** and **Cancel**. Create Story calls the real story backend using accepted words (including the conversation queue) and only answers already saved; unfinished speech is discarded. Story completion preserves the experience. After the final narration ends, the narrator says “Let's turn your story into an animation. I'll show it when it's ready.” and automatically starts the Comfy animation job. The finished movie opens in a VR screen and is saved on the Mac, where QuickTime Player opens a preview once. At least one accepted word is required. End Session cancels local requests/playback and clears the active session back to the selection menu without deleting archived captures. Cancel returns to the session. Repeated Create Story clicks cannot submit a second story while one is running; a failed manual request keeps saved progress for retry. Menu pinches do not trigger the capture gesture.

The future Judy flow is **select Judy → play one real assigned, approximately 30-second video → invite the viewer to try Create World**. This video playback and invitation flow is planned, not implemented. The placeholder does not play a sample video or start the core word-building functions.

## Story animation

Animation starts only from the current experience's `StoryNarrationCompleted` event, after the story audio has finished. `StoryOutputReady` only archives the story. The Mac backend matches the requested story text and audio path to its own successful story archive, then covers the seven sentences with exactly two chronological shots: sentences 1–4 and 5–7. Each shot lasts 6–10 seconds and uses the supplied `shot_api.json` (Seedance 2.5 Draft, 480p, 16:9). It reuses the current story's saved word-card PNGs as references and never generates replacement cards for a movie. At least one usable reference card is required.

Both shots are submitted before waiting and polled concurrently; actual render concurrency depends on the Comfy account and service queue. ffmpeg then joins and normalizes them into one H.264/AAC MP4. Two minutes is a target, not a timer or guarantee. `POST /story-video` starts or rejoins the owned job, `GET /story-video` polls it, and `/story-videos/<job-id>.mp4` serves the completed movie. Existing saved films keep their original shot plans and are not regenerated by this change. Install the updated Python requirements for the bundled ffmpeg executable. Movie jobs, prompts, shot files and the final MP4 stay in the same experience's `videos/` archive, outside Git. The Mac preview opens only after the final file is saved.

The Quest downloads the final movie before playing it from local storage. Headset sleep or tracking loss pauses playback and resumes it when ready; ending/resetting a session cancels local viewing without deleting archived movies. Cloud renders already submitted can finish into their original archive. Repeated local requests reuse the same job; a paid shot whose submission outcome is unknown is never automatically resubmitted. Failures remain explicit instead of playing a placeholder movie.

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

The pinned upstream OCR and braille readers are unchanged. Their supplied `CombinedReader` has OCR-priority behavior, so this experiment uses a separate first-success dispatcher. Each branch uses the existing automatic full-face locator. If faces cannot be resolved, the OCR branch retains automatic printed-label detection with the upstream model and normalization rules; the dashboard records which path ran. OCR's upstream score threshold is 0.85. Only A–Z and periods are accepted; a standalone period is an explicit finish command. No dictionary correction is applied.

When automatic text detection omits a trailing period, the connection adapter can propose a label region from a confidently aligned letter row and pass the original color pixels to the unchanged upstream `detect_period()` function. Shape results retain their own source and diagnostics, without an invented OCR score. This fallback requires reliable row spacing; standalone periods still require a detected block face or an actual OCR detection. It does not guess a period from an empty capture.

The existing capture monitor has a plain history table showing the winner, both results/failure reasons, both elapsed times, and disagreement. The slower reader always finishes. Elapsed time starts at receiver recognition entry and includes any previous-worker wait, crop preparation, geometry and decoding; it excludes the three-second capture timer, network upload and archive saving. `executionMs` additionally records each worker's execution time. These are latency measurements, not measured recognition accuracy.

All updates append to `runtime/physical-story-captures/recognition-history.jsonl` and survive a receiver restart. The dashboard shows the latest 200 captures through `/recognition-history.json`; logs and images stay out of Git. Only one pair of readers is active at a time, so a new capture drains the previous late result before starting. Use `--recognition ocr` or `--recognition braille` for a single-reader comparison.

Focused offline checks: `Python/.ocr-venv/bin/python -m unittest discover -s Python -p test_parallel_recognition.py`. Real Quest captures are still needed before recommending this experiment for `main`.

## Keep changes separate

Owner/backend development uses `dev/core-functions` (or `dev/<task>` for a separate task). Commit and push development changes there. Keep `main` as the shared baseline; recommend merging after the relevant checks and device testing, and merge only when the owner explicitly requests it.

Designers use `design/*` branches and only their three folders above. Preserve Unity `.meta` files. The repository includes English AI instructions, CODEOWNERS and a design-boundary check. Those are review controls; they are not a local filesystem lock. Do not merge core changes without the owner's review.

GitHub declined enforced branch protection for this private repository on the current account plan. Review design pull requests manually before merging, and do not push design work directly to `main`.

Capture files, local keys, virtual environments, generated builds and temporary experiments are excluded from Git. Unity scenes are preserved during normal builds.

The supplied Judy storyboard remains a historical content reference for producing the future finished video. It no longer defines an active word-cue experience. No Judy video or playback flow is supplied yet. Create World now uses the supplied Comfy film workflow; Orbis and public video sharing are not connected.


## Experience storage and rollback

New requests carry immutable `workspaceId` (`judy` or `demo`) and `experienceSessionId`. The active UI uses `demo` for **Create World**; its Mac directory is `create-world`. The backend's existing `judy` namespace and archived Judy data remain intact for compatibility; the disabled Judy entry sends no capture or API requests. Camera frame IDs and conversation IDs remain linked sub-identities. End Session closes the old experience; late results stay in its archive and cannot enter the new UI.

- Mac records: `runtime/workspaces/{judy,create-world}/sessions/<experience-id>/` contains session-state snapshots, request/results, generated audio, story results and capture state/history.
- Photos: the existing configured `capturedimage2026` directory gains the same mode/session hierarchy, with originals, exact valid crops and metadata under `captures/`. Failed recognition remains archived; invalid crops are recorded as unavailable.
- Quest: `ExperienceSessions/<workspace>/<experience-id>/` retains local session snapshots, generated speech and story exports. A durable outbox synchronizes snapshots to Mac and survives mode changes/offline time. Live app-state restoration after force-kill is not implemented.
- Generated audio for owned experiences is durable; the old 64-file cache is used only by legacy unowned clients. Raw microphone recordings remain transient.
- Old flat archives and old unowned requests remain compatible and are never guessed into a new mode. The capture monitor at port 8769 provides an explicit session selector; its unscoped view is legacy data.

`End Session` clears live objects, not saved files. Tracking reset cancels pending captures and invalidates old box positions, while retaining words/answers; old spatial presentation must not be trusted after re-localization. Centered narration can continue. Only Create World currently activates the camera, microphone, recognition and conversation backend. The planned Judy video must remain independent of those word-building functions.

Development branch: `dev/guided-dialogue`. The pre-integration source is preserved on `rollback/pre-judy-create-world-20261114` at `4b13db6`. After committing/stashing any current edits, switch to that rollback branch and run its `Tools/build-physical-story.sh` to rebuild the previous app. Restart its Python services when reverting the backend too. Switch back to `dev/guided-dialogue` to resume the current implementation. Existing saved files are not deleted by switching branches. No change has been merged to `main`.

The integration before Judy was simplified is preserved on `rollback/before-judy-video-only-20261114` at `82aa499`, also pushed to GitHub. After saving any current edits, switch to that branch to restore the previous source. The original pre-integration rollback above remains available.

The working Create World integration immediately before guided dialogue is preserved on `rollback/pre-guided-dialogue-20261114` at `f099a8c`, pushed to GitHub. Use that checkpoint to return to the prior conversation behavior while keeping the menu-only UX integration. Guided-dialogue work stays on `dev/guided-dialogue`; merging into `main` still requires the owner's explicit instruction.
