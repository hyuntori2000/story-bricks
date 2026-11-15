# Connect your UX to the working functions

## Buttons

Add `BrailleSoundDemo.Presentation.StoryFunctionBridge` to an object in your own UI prefab. Its coordinator reference can be assigned in the Inspector or found automatically in the loaded scene. Connect `Button.onClick` to:

| Function | Behavior |
| --- | --- |
| `UseLiveDemo()` | Shared capture/OCR plus real conversation. Four words and four answers complete a story. |
| `UsePreparedStory()` | Shared capture/OCR plus authored media; no automatic conversation API calls. |
| `PlayCue(string cueKey)` | Play a named authored cue group. |
| `EmitEvent(string eventName)` | Trigger one presentation event manually. |
| `StartNextStory()` | Start the next conversation after completion/error, preserving other word boxes. |
| `ResetSession()` | Explicitly clear the current session, pending words and media. |

Changing modes starts a new session. Capture remains the existing same-hand double pinch followed by a three-second wait. Words can be created while another word's dialogue is playing; the live conversation consumes accepted words from a queue. An active capture/recognition request, missing permissions or invalid tracking still prevents another simultaneous capture.

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

Generation is a separate backend responsibility. `video.ready` can play a supplied video; it does not generate one. Orbis generation/public sharing are not connected in this handoff.

## Ownership

Designers own `Assets/DesignerWorkspace/`, `Dashboard/` and `Design/`. Shared functions remain under `Assets/BrailleSoundDemo/` and `Python/`. Ask for an interface change instead of editing protected code. The Judy reference in `Design/judy-storyboard.json` contains four words, 15 interaction steps and 12 narration beats; no finished media files are fabricated or supplied.
