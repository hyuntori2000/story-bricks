# Connect your UX to the working functions

## Buttons

Add `BrailleSoundDemo.Presentation.StoryFunctionBridge` to an object in your own UI prefab. Its coordinator reference can be assigned in the Inspector or found automatically in the loaded scene. Connect `Button.onClick` to:

| Function | Behavior |
| --- | --- |
| `UseJudyStory()` | Play the supplied 61.5-second Judy cutout animation without a live capture/API session. |
| `ReturnToMenu()` | Stop the current presentation, end any active live session, and return to selection. |
| `ReplayStory()` | Replay the current authored Judy story or the already downloaded live movie; no new generation request. |
| `UseLiveDemo()` | Enter Create World, play its fixed introduction once, then use guided build-word / spoken-answer dialogue with the shared capture core. |
| `PlayCue(string cueKey)` | Play a named general presentation cue group; this is not a Judy word-sequence function. |
| `EmitEvent(string eventName)` | Trigger one presentation event manually. |
| `StartNextStory()` | Start the next conversation after completion/error, preserving other word boxes and queued captures without replaying the experience introduction. |
| `OpenFinishMenu()` | Show the shared English Create Story / End Session / Cancel confirmation. |
| `CreateStory()` | In Create World, generate from captured words and saved answers, preserving the experience for the next stage. |
| `EndSession()` | Cancel the active session and return to the Judy/Create World selection screen. Archived captures remain on disk. |
| `ResetSession()` | Explicitly clear the current session, pending words and media. |

Only Create World currently starts a word-building session. Its capture remains the existing same-hand double pinch followed by a three-second wait. Words can be created while another word's dialogue is playing; the live conversation consumes accepted words from a queue only when it is ready for a `build_word` input. An active capture/recognition request, missing permissions or invalid tracking still prevents another simultaneous capture. The Judy menu entry starts its supplied Unity animation through the router. It does not invoke capture, conversation or generation APIs. The old prepared word-sequence API remains removed.

The guided dialogue has two input modes. `build_word` keeps the microphone closed and waits for a captured word; `speak_answer` finishes playing its prompt before opening the microphone. Do not infer microphone readiness from the presence of a question mark or from the number of captured words. The ordinary first word can have zero spoken answers, and a later word can have several. A new capture can repeat an earlier word's spelling and answer a different build prompt. Preserve every saved input's word and dialogue-turn identity; a word index is not an answer index.

There is no automatic ending after three or four words. A standalone `.` is a queue delimiter, not a question/sound word. Its pending latch lets the current spoken answer finish and prevents additional follow-ups; earlier queued words still pass through the dialogue before the delimiter starts the story. A trailing period such as `TREE.` requires that word's final spoken question and answer. A period before any accepted word cannot generate an empty story. The final story text contains exactly seven English sentences. A separate readiness announcement is included in spoken audio, not exported story text.

Create World retains the small **Finish** button with **Create Story / End Session / Cancel**, selectable by hand ray/pinch or controller ray/trigger. Create Story cancels unfinished dialogue and sends `completionMode: "manual_saved_progress"` with captured words and only completed answers already saved. Missing answers are not fabricated. Story creation does not end the experience. The standalone ConversationFlow API still supports manual provider use for fixtures; route through StoryFunctionBridge in the actual UX. End Session resets capture, conversation, audio and presentation and returns to the initial screen. The menu suppresses capture gestures through pointer release. Designer buttons can call the same shared functions above.

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

`Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab` contains the latest scene-authored layout from `story-bricks-main-2`. The selection screen offers **Create Your World** and **Judy’s Story**. The supplied 12-second MP4 and prototype automatic word/answer flow are excluded.

Judy plays the original **61.5-second Unity cutout presentation**, with its original narration, subtitle cues and synchronized effects. Its end screen offers replay, Create Your World and the menu. `JudyStoryPlayer` owns local playback; `StoryStage` owns its animated drawings. Neither owns a live session or API connection.

`ExperienceRouter` owns both routes and uses the single physical coordinator/rig. Create World receives real word, caption, listening and generation events. Finish retains its existing implementation. Navigation stops old playback and invalidates the previous live session before entering another route. Do not deactivate the shared core when hiding UI panels. Use the bridge functions instead of scene reloads or the source prototype's timers.

The supplied video frame displays the actual generated movie. Replay uses its downloaded file, while a new Create World session starts fresh. The existing opening and accessible double-pinch entry remain connected to the shared input flow.

All new storage/API envelopes carry `workspaceId` and `experienceSessionId`; never classify a completed request using a global current-mode variable. `POST /session-state` mirrors `{workspaceId, experienceSessionId, revision, state}` with monotonically increasing revision. Audio URLs remain opaque `/audio/<id>.wav` and map to durable owned assets. The recognition monitor's `/workspaces.json` lists explicit scoped session links; its query includes both ownership fields. See the root README for archive locations and rollback instructions.

Create World’s storage behavior is unchanged. Existing Judy archives remain retained and readable; local Judy playback does not migrate or delete them. A rollback checkpoint is preserved as `rollback/before-judy-video-only-20261114` at `82aa499`; the root README also retains the original pre-integration rollback instructions.

The current UX integration is on `dev/ux-integration`, with designer content on `design/judy-ux-integration`. `rollback/pre-supplied-ux-20261115` (`ad86290`) restores the application immediately before this import. Save local changes before switching branches, then rebuild; no backend change is required to undo this presentation update.
