# Connect your UX to the working functions

## Buttons

Add `BrailleSoundDemo.Presentation.StoryFunctionBridge` to an object in your own UI prefab. Its coordinator reference can be assigned in the Inspector or found automatically in the loaded scene. Connect `Button.onClick` to:

| Function | Behavior |
| --- | --- |
| `UseJudyStory()` | Play the supplied 61.5-second Judy cutout animation without a live capture/API session. |
| `GoHome()` / `ReturnToMenu()` | Immediately clear the active session and return to the landing screen; saved movies and captures remain. |
| `ReplayStory()` | Replay the current Judy story, live movie or selected saved movie; no generation request. |
| `SeePreviousStories()` | Open the Mac-backed completed-movie library and select a film for VR playback. |
| `UseLiveDemo()` | Enter Create World, play its fixed introduction once, then use guided build-word / spoken-answer dialogue with the shared capture core. |
| `PlayCue(string cueKey)` | Play a named general presentation cue group; this is not a Judy word-sequence function. |
| `EmitEvent(string eventName)` | Trigger one presentation event manually. |
| `StartNextStory()` | Start the next conversation after completion/error, preserving other word boxes and queued captures without replaying the experience introduction. |
| `OpenFinishMenu()` | Compatibility alias for the direct Home action. New buttons should use `GoHome()`. |
| `CreateStory()` | Directly generate from captured words and saved answers, then continue to animation; no period or confirmation menu. |
| `EndSession()` | Clear the active session and return to landing; archived captures and movies remain on disk. |
| `ResetSession()` | Explicitly clear the current session, pending words and media. |

Only Create World currently starts a word-building session. Its capture remains the existing same-hand double pinch followed by a three-second wait. Words can be created while another word's dialogue is playing; the live conversation consumes accepted words from a queue only when it is ready for a `build_word` input. An active capture/recognition request, missing permissions or invalid tracking still prevents another simultaneous capture. The Judy menu entry starts its supplied Unity animation through the router. It does not invoke capture, conversation or generation APIs. The old prepared word-sequence API remains removed.

The demo repeats short word pairs: first word → spelling and one spoken question → one answer → invitation to build a related second word → spelling and a brief closing. The closing says **“Keep building another word, or press Create Story to make your story.”** A third word starts the next pair. `build_word` keeps the microphone closed; `speak_answer` opens it after prompt audio finishes. Use these explicit states, not question marks or word counts, to drive the UI. A repeated spelling can belong to a new capture. Preserve word and dialogue-turn identities; a word index is not an answer index.

There is no automatic ending after two, three or four words. A standalone `.` is a queue delimiter, never a word/sound input. The current answer and earlier queued words complete first; the delimiter then asks one final spoken question, and its saved answer starts the story. `TREE.` instead spells the word and asks that final question directly. Duplicate periods coalesce; an empty session cannot finish. Story text contains exactly seven English sentences, with a separate readiness announcement in narrated audio.

The supplied painted **Home** and **Create Story** buttons are direct actions. Home clears capture, words, conversation and local playback and returns to landing while preserving archived media. Create Story cancels unfinished dialogue and sends `completionMode: "manual_saved_progress"` with accepted words, queued words and completed answers; missing answers are not invented. Story narration then leads to automatic two-scene animation and VR playback without ending the experience. Button interaction suppresses capture through pointer release. Route designer buttons through `StoryFunctionBridge` rather than calling provider fixtures.

Sound generation uses only the recognized word. Concrete prompts describe characteristic actions such as a cat meowing or piano notes; subjects without a characteristic sound receive a quiet non-vocal hum. Bounded backend leveling improves weak audible clips but never boosts quiet-subject hums. Playback remains at 70% of the previous effect gain, with narration volume unchanged. Effects play when ready; displaying a word image replays its existing sound once.

## Attach media to a box

1. Create a prefab **inside `Assets/DesignerWorkspace/`** and add `WordMediaPresenter`.
2. Leave **Use Dashboard Catalog** off to work with Unity assets directly.
3. Add **Local Bindings**. Choose a target (`box` by default, or `stage` for the story display), event, optional exact word, action, asset, delay, volume and optional cue key.
4. Drag this prefab into **Media Presenter Prefab** on `PhysicalStoryCoordinator` in your designer-owned scene copy.
5. Capture a word. The core attaches the presenter to its anchor and emits events.

Events: `box.created`, `word.accepted`, `question.started`, `answer.saved`, `story.ready`, `video.ready`, `manual`.

The word is empty at `box.created`; use `word.accepted` for word-specific media. An empty cue key follows its event automatically. A named cue key runs only when explicitly selected and can group media bindings. Explicit cue keys override event selection; word and target filters still apply. In Create World, prepared voice is suppressed to avoid speaking over generated questions. Arbitrary authored video cues remain blocked; the verified story-video pipeline owns generated film playback. The generic binding schema supports Texture2D images, AudioClip sound/voice and VideoClip video; available actions remain subject to the runtime gates. A stage target is separate from a word-box target. Session reset cancels pending playback. These general media functions remain available; the removed Judy-specific word sequence must not be recreated through them.

## Optional dashboard adapter

The design team owns `Dashboard/`; no new media dashboard is required to use local Unity bindings. If you build one, enable the presenter's catalog and set its base URL. It requests:

`GET /api/manifest?workspace=demo` for the active Create World route. The older `judy` catalog namespace is retained for compatibility; the authored Judy playback uses its local Unity assets.

Return a JSON object with `schemaVersion: 1`, `revision`, `assets` and `cues`. Asset fields are `id`, `name`, `kind` (`audio`, `image`, `video`), `mimeType` and a same-origin `url`. Cue fields are `id`, `cueKey`, `event`, `wordFilter`, `action` (`sound`, `voice`, `image`, `video`), `target` (`box`, `stage`), `assetId`, `delayMs`, `order`, `volume`, `loop` and `enabled`. Keep any existing mode-specific catalogs separate. Use WAV/MP3, PNG/JPEG and H.264 MP4 for the Quest path; test target-device playback when that media action is enabled.

Generation is a separate backend responsibility. Create World rejects arbitrary authored video cues, even `video.ready`. Its `StoryVideoPipeline` waits for the actual generated movie, downloads it, and displays it in the supplied UX frame. `story.ready` means data is available before narration; `ConversationFlow.StoryNarrationCompleted` starts the actual two-scene Comfy film pipeline. Judy plays its local cutout animation independently. Orbis generation and public sharing are not connected.

## Ownership

Designers own `Assets/DesignerWorkspace/`, `Dashboard/` and `Design/`. Shared functions remain under `Assets/BrailleSoundDemo/` and `Python/`. Ask for an interface change instead of editing protected code. The Judy reference in `Design/judy-storyboard.json` remains content reference material, not a recognition-driven runtime sequence.


## Integrated UX and route ownership

`Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab` contains the latest scene-authored layout from `story-bricks-main-2`. The selection screen offers **Create Your World**, **Judy’s Story** and **See previous story**. The supplied 12-second MP4 and prototype automatic word/answer flow are excluded.

Judy plays the original **61.5-second Unity cutout presentation**, with its original narration, subtitle cues and synchronized effects. Its end screen offers replay, Create Your World and the menu. `JudyStoryPlayer` owns local playback; `StoryStage` owns its animated drawings. Neither owns a live session or API connection.

`ExperienceRouter` owns live creation, local Judy playback and saved-film viewing, using the single physical coordinator/rig. Create World receives real word, caption, listening and generation events. Home and Create Story use the shared direct actions. Navigation stops old playback and invalidates the previous live session before entering another route. Do not deactivate the shared core when hiding UI panels. Use the bridge functions instead of scene reloads or the source prototype's timers.

The supplied video frame displays the generated or selected saved movie. Replay uses its downloaded file, while a new Create World session starts fresh. See previous story calls `GET /story-library`; browsing and playback do not start a live session or generation job. The Mac index is `runtime/workspaces/story-library/index.json`, with original movies retained under each experience’s `videos/<jobId>/story.mp4` archive. The Mac backend must be reachable to load the library. The existing opening and accessible double-pinch entry remain connected to the shared input flow.

All new storage/API envelopes carry `workspaceId` and `experienceSessionId`; never classify a completed request using a global current-mode variable. `POST /session-state` mirrors `{workspaceId, experienceSessionId, revision, state}` with monotonically increasing revision. Audio URLs remain opaque `/audio/<id>.wav` and map to durable owned assets. The recognition monitor's `/workspaces.json` lists explicit scoped session links; its query includes both ownership fields. See the root README for archive locations and rollback instructions.

Create World’s storage behavior is unchanged. Existing Judy archives remain retained and readable; local Judy playback does not migrate or delete them. A rollback checkpoint is preserved as `rollback/before-judy-video-only-20261114` at `82aa499`; the root README also retains the original pre-integration rollback instructions.

Current demo development is on `dev/demo-flow-story-library`; the earlier UX integration is on `dev/ux-integration`, with designer content on `design/judy-ux-integration`. `rollback/pre-supplied-ux-20261115` (`ad86290`) restores the application immediately before this import. Save local changes before switching branches, then rebuild; no backend change is required to undo this presentation update.
