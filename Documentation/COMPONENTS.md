# Connect your UX to the working functions

## Buttons

Add `BrailleSoundDemo.Presentation.StoryFunctionBridge` to an object in your own UI prefab. Its coordinator reference can be assigned in the Inspector or found automatically in the loaded scene. Connect `Button.onClick` to:

| Function | Behavior |
| --- | --- |
| `UseLiveDemo()` | Shared capture/recognition plus real conversation. A recognized period ends the story after earlier queued answers finish. |
| `UsePreparedStory()` | Shared capture/OCR plus authored media; no automatic conversation API calls. |
| `PlayCue(string cueKey)` | Play a named authored cue group. |
| `EmitEvent(string eventName)` | Trigger one presentation event manually. |
| `StartNextStory()` | Start the next conversation after completion/error, preserving other word boxes. |
| `OpenFinishMenu()` | Show the shared English Create Story / End Session / Cancel confirmation. |
| `CreateStory()` | Create World: generate from captured words and saved answers. Judy: finish queued authored cues, then play the authored final sequence. Neither operation ends the experience. |
| `EndSession()` | Cancel the active session and return to the Judy/Create World selection screen. Archived captures remain on disk. |
| `ResetSession()` | Explicitly clear the current session, pending words and media. |

Changing modes starts a new session. Capture remains the existing same-hand double pinch followed by a three-second wait. Words can be created while another word's dialogue is playing; the live conversation consumes accepted words from a queue. An active capture/recognition request, missing permissions or invalid tracking still prevents another simultaneous capture.

There is no automatic ending after three or four words. A standalone `.` is a queue delimiter, not a question/sound word. A trailing period such as `TREE.` also finishes after that word's answer. A period before any answered word cannot generate an empty story. Prepared-story mode keeps authored cue control.

The baseline scene includes a small **Finish** button with **Create Story / End Session / Cancel**, selectable by hand ray/pinch or controller ray/trigger. Create Story cancels unfinished dialogue and sends `completionMode: "manual_saved_progress"` with captured words and the saved answer prefix. Missing answers are not fabricated. This explicit action calls the real backend only in Create World. Judy uses the prepared sequence. The standalone ConversationFlow API still supports manual provider use for fixtures; route through StoryFunctionBridge in the actual UX. End Session resets capture, conversation, audio and presentation and returns to the initial screen. The menu suppresses capture gestures through pointer release. Designer buttons can call the same shared functions above.

## Attach media to a box

1. Create a prefab **inside `Assets/DesignerWorkspace/`** and add `WordMediaPresenter`.
2. Leave **Use Dashboard Catalog** off to work with Unity assets directly.
3. Add **Local Bindings**. Choose a target (`box` by default, or `stage` for the story display), event, optional exact word, action, asset, delay, volume and optional cue key.
4. Drag this prefab into **Media Presenter Prefab** on `PhysicalStoryCoordinator` in your designer-owned scene copy.
5. Capture a word. The core attaches the presenter to its anchor and emits events.

Events: `box.created`, `word.accepted`, `question.started`, `answer.saved`, `story.ready`, `video.ready`, `manual`.

The word is empty at `box.created`; use `word.accepted` for word-specific media. An empty cue key follows its event automatically. A named cue key runs only when explicitly selected and can group image, sound, voice and video bindings. Explicit cue keys override event selection; word and target filters still apply. In Demo mode, prepared voice is suppressed to avoid speaking over generated questions. Prepared-story mode permits authored voice. Images are Texture2D assets; sound/voice use AudioClip; video uses VideoClip. A stage target is separate from a word-box target. Session reset cancels pending playback.

## Optional dashboard adapter

The design team owns `Dashboard/`; no new media dashboard is required to use local Unity bindings. If you build one, enable the presenter's catalog and set its base URL. It requests:

`GET /api/manifest?workspace=demo` or `?workspace=judy`.

Return a JSON object with `schemaVersion: 1`, `revision`, `assets` and `cues`. Asset fields are `id`, `name`, `kind` (`audio`, `image`, `video`), `mimeType` and a same-origin `url`. Cue fields are `id`, `cueKey`, `event`, `wordFilter`, `action` (`sound`, `voice`, `image`, `video`), `target` (`box`, `stage`), `assetId`, `delayMs`, `order`, `volume`, `loop` and `enabled`. Keep Demo/Judy catalogs separate. Use WAV/MP3, PNG/JPEG and H.264 MP4 for the Quest path; test target-device playback with your final assets.

Generation is a separate backend responsibility. Create World currently rejects all authored video cues, even `video.ready`; only a future verified animation-result integration should enable playback. Judy can play supplied videos. `story.ready` means data is available before narration; `ConversationFlow.StoryNarrationCompleted` marks actual narration completion. Orbis generation/public sharing are not connected in this handoff.

## Ownership

Designers own `Assets/DesignerWorkspace/`, `Dashboard/` and `Design/`. Shared functions remain under `Assets/BrailleSoundDemo/` and `Python/`. Ask for an interface change instead of editing protected code. The Judy reference in `Design/judy-storyboard.json` contains four words, 15 interaction steps and 12 narration beats; no finished media files are fabricated or supplied.


## Judy sequence and route ownership

The imported view is `Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab`. Edit authored content in `JudySequence.asset` beside it. `JudySequencePlayer` matches real accepted words in order; a trailing period is ignored only for cue matching, never rewritten in recognition/storage. Add media steps for each word or the final story. Steps wait for actual clip/video completion or a `JudyCueSignal.Complete()` event from a prefab. Media within a step can run together; different steps run in order. Caption-only missing-media previews are explicit, not generated speech/video.

Only `ExperienceRouter` selects modes and advances their presentation. Keep one physical coordinator/rig. Do not deactivate the core when hiding UI panels. Use `ResetSession()` through the bridge to restart the selected route with a new owning session, or `EndSession()` to return to selection. Subscribe to typed events rather than the imported prototype's former automatic timers.

All new storage/API envelopes carry `workspaceId` and `experienceSessionId`; never classify a completed request using a global current-mode variable. `POST /session-state` mirrors `{workspaceId, experienceSessionId, revision, state}` with monotonically increasing revision. Audio URLs remain opaque `/audio/<id>.wav` and map to durable owned assets. The recognition monitor's `/workspaces.json` lists explicit scoped session links; its query includes both ownership fields. See the root README for archive locations and rollback instructions.
