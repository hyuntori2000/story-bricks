# Connect your UX to the working functions

## Buttons

Add `BrailleSoundDemo.Presentation.StoryFunctionBridge` to an object in your own UI prefab. Its coordinator reference can be assigned in the Inspector or found automatically in the loaded scene. Connect `Button.onClick` to:

| Function | Behavior |
| --- | --- |
| `UseLiveDemo()` | Shared capture/recognition plus real conversation. A recognized period ends the story after earlier queued answers finish. |
| `PlayCue(string cueKey)` | Play a named general presentation cue group; this is not a Judy word-sequence function. |
| `EmitEvent(string eventName)` | Trigger one presentation event manually. |
| `StartNextStory()` | Start the next conversation after completion/error, preserving other word boxes. |
| `OpenFinishMenu()` | Show the shared English Create Story / End Session / Cancel confirmation. |
| `CreateStory()` | In Create World, generate from captured words and saved answers, preserving the experience for the next stage. |
| `EndSession()` | Cancel the active session and return to the Judy/Create World selection screen. Archived captures remain on disk. |
| `ResetSession()` | Explicitly clear the current session, pending words and media. |

Only Create World currently starts a word-building session. Its capture remains the existing same-hand double pinch followed by a three-second wait. Words can be created while another word's dialogue is playing; the live conversation consumes accepted words from a queue. An active capture/recognition request, missing permissions or invalid tracking still prevents another simultaneous capture. The Judy menu entry is disabled and labelled **Coming soon**; do not wire it to capture, cue or API functions. The former `UsePreparedStory()` / Judy selection API is removed rather than retained as a compatibility entry point.

There is no automatic ending after three or four words. A standalone `.` is a queue delimiter, not a question/sound word. A trailing period such as `TREE.` also finishes after that word's answer. A period before any answered word cannot generate an empty story.

Create World retains the small **Finish** button with **Create Story / End Session / Cancel**, selectable by hand ray/pinch or controller ray/trigger. Create Story cancels unfinished dialogue and sends `completionMode: "manual_saved_progress"` with captured words and the saved answer prefix. Missing answers are not fabricated. Story creation does not end the experience. The standalone ConversationFlow API still supports manual provider use for fixtures; route through StoryFunctionBridge in the actual UX. End Session resets capture, conversation, audio and presentation and returns to the initial screen. The menu suppresses capture gestures through pointer release. Designer buttons can call the same shared functions above.

## Attach media to a box

1. Create a prefab **inside `Assets/DesignerWorkspace/`** and add `WordMediaPresenter`.
2. Leave **Use Dashboard Catalog** off to work with Unity assets directly.
3. Add **Local Bindings**. Choose a target (`box` by default, or `stage` for the story display), event, optional exact word, action, asset, delay, volume and optional cue key.
4. Drag this prefab into **Media Presenter Prefab** on `PhysicalStoryCoordinator` in your designer-owned scene copy.
5. Capture a word. The core attaches the presenter to its anchor and emits events.

Events: `box.created`, `word.accepted`, `question.started`, `answer.saved`, `story.ready`, `video.ready`, `manual`.

The word is empty at `box.created`; use `word.accepted` for word-specific media. An empty cue key follows its event automatically. A named cue key runs only when explicitly selected and can group media bindings. Explicit cue keys override event selection; word and target filters still apply. In Create World, prepared voice is suppressed to avoid speaking over generated questions, and authored video playback is blocked pending the animation integration. The generic binding schema supports Texture2D images, AudioClip sound/voice and VideoClip video; available actions remain subject to the runtime gates. A stage target is separate from a word-box target. Session reset cancels pending playback. These general media functions remain available; the removed Judy-specific word sequence must not be recreated through them.

## Optional dashboard adapter

The design team owns `Dashboard/`; no new media dashboard is required to use local Unity bindings. If you build one, enable the presenter's catalog and set its base URL. It requests:

`GET /api/manifest?workspace=demo` for the active Create World route. The older `judy` catalog namespace is reserved for compatibility, not an active Judy capture/cue route.

Return a JSON object with `schemaVersion: 1`, `revision`, `assets` and `cues`. Asset fields are `id`, `name`, `kind` (`audio`, `image`, `video`), `mimeType` and a same-origin `url`. Cue fields are `id`, `cueKey`, `event`, `wordFilter`, `action` (`sound`, `voice`, `image`, `video`), `target` (`box`, `stage`), `assetId`, `delayMs`, `order`, `volume`, `loop` and `enabled`. Keep any existing mode-specific catalogs separate. Use WAV/MP3, PNG/JPEG and H.264 MP4 for the Quest path; test target-device playback when that media action is enabled.

Generation is a separate backend responsibility. Create World currently rejects all authored video cues, even `video.ready`; only a future verified animation-result integration should enable playback. Judy has no video playback implementation yet. `story.ready` means data is available before narration; `ConversationFlow.StoryNarrationCompleted` marks actual narration completion. Orbis generation/public sharing are not connected in this handoff.

## Ownership

Designers own `Assets/DesignerWorkspace/`, `Dashboard/` and `Design/`. Shared functions remain under `Assets/BrailleSoundDemo/` and `Python/`. Ask for an interface change instead of editing protected code. The Judy reference in `Design/judy-storyboard.json` remains historical source material for the future finished video, not an active runtime sequence.


## Future Judy video and route ownership

The imported view is `Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab`. Judy is now only a disabled **Coming soon** menu entry. It has no word capture, recognition-triggered cues, prepared conversation, final sequence or playback implementation. Do not add a `JudySequence` asset/player or reconnect the former per-word workflow.

The planned flow is **select Judy → play one real assigned video of approximately 30 seconds → invite the viewer to Create World**. A future implementation must wait for actual playback completion before showing that invitation. No placeholder clip, timer-only completion, generation API or word-building action belongs in this route. This flow is a plan, not an available function.

Only `ExperienceRouter` starts Create World and advances its presentation. Keep one physical coordinator/rig. Do not deactivate the core when hiding UI panels. Use `ResetSession()` through the bridge to restart Create World with a new owning session, or `EndSession()` to return to selection. Subscribe to typed events rather than the imported prototype's former automatic timers.

All new storage/API envelopes carry `workspaceId` and `experienceSessionId`; never classify a completed request using a global current-mode variable. `POST /session-state` mirrors `{workspaceId, experienceSessionId, revision, state}` with monotonically increasing revision. Audio URLs remain opaque `/audio/<id>.wav` and map to durable owned assets. The recognition monitor's `/workspaces.json` lists explicit scoped session links; its query includes both ownership fields. See the root README for archive locations and rollback instructions.

Create World's storage behavior is unchanged. Existing Judy archives remain retained and readable; disabling Judy does not migrate or delete them. A rollback checkpoint is preserved as `rollback/before-judy-video-only-20261114` at `82aa499`; the root README also retains the original pre-integration rollback instructions.
