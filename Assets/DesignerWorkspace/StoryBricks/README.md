# Story Bricks presentation and future Judy video

The watercolour view supplies the **selection menu**: an active **Create World** entry and a disabled **Judy — Coming soon** entry. Choosing Create World closes the imported view and uses the original capture, dialogue and word-box experience with its existing small Finish button. Judy is reserved for a future single prepared video of approximately 30 seconds; no Judy playback is implemented yet.

Presentation code must not start capture, change recognition, call an AI API, reset the session or start another route directly. Use the shared bridge/router for the supported Create World actions.

## Assets to edit

- `Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab`: the imported watercolour selection menu. Change its graphics, typography, spacing and visual transitions here. Keep Create World connected to the core router and Judy disabled with its **Coming soon** label. The other supplied panels are inactive during Create World. Keep the existing Finish menu owned by the core.
- Keep new artwork, media and presentation prefabs inside `Assets/DesignerWorkspace/`. Preserve their `.meta` files when sharing or moving them.
- The old Judy word-sequence authoring workflow is removed. Do not add a `JudySequence` asset/player, expected-word steps, per-word voice bindings or cue-completion signals for Judy.

The supplied Judy storyboard is historical reference material for creating the future finished video. It does not drive live word recognition or a caption preview. No finished Judy video is supplied. Create World never substitutes Judy media or automatically plays a demonstration video.

## Planned Judy video

The future flow is:

1. The viewer selects Judy once the real video and playback integration are available.
2. The assigned, approximately 30-second video plays.
3. After actual playback completion, the viewer is invited to try Create World.

This is a content and interaction plan, not an implemented playback function. For now, leave the Judy entry disabled and empty. Do not add an automatic sample video, a fake completion timer, word capture, recognition-driven cues or API calls. The future invitation must use the core router to enter Create World instead of copying its camera, microphone or recognition components into Judy.

## Create World and Finish

Create World's live capture, recognition, question/answer and story behavior remains unchanged. It uses the original capture view and Finish dialog, with no imported word row, captions, progress panels or authored story screens. **Create Story** uses accepted words and saved answers, then preserves the experience and records the next stage; **End Session** clears active objects and returns to selection without deleting saved archives.

Create World's animation playback remains disabled until an actual generation-result integration is added. Display progression must follow real completion events, not timers in this view. General `WordMediaPresenter` and named presentation cue functions remain available for their supported actions; they are not the removed Judy word-sequence system.

## View and import rules

`ViewerFlow` is a passive presentation component. In the current application the router uses only its selection menu, then hides the imported view while Create World runs. The supplied word-row, caption and ready-screen widgets remain disconnected from the live route. The view does not inject words, advance a keyboard demo, automatically play a story or reset a session on enable.

The committed `View.prefab` is extracted from the latest supplied scene, not the older supplied `ParentUI.prefab`. `Story Bricks > Import supplied presentation once` is an initial import helper; it preserves an existing view. Normal team checkouts use the committed prefab and do not need the original Downloads folder. Do not run the original incoming project builders or copy its Packages, ProjectSettings or XR rig into the main application.

The copied font assets keep their original GUIDs and embedded atlas textures. The included TextMesh Pro mobile distance-field shader is the exact dependency used by those materials, compatible with the main project's existing renderer.

For headset testing, launch the main application **`org.braillelab.storydemo`**, installed by `Tools/install-physical-story.sh`. The separately installed **`com.storybricks.parentui`** is the original designer prototype and still has its own scripted demonstration flow; it is not the integrated Create World application.

Create World's storage layout is unchanged. Previously saved Judy data is retained; disabling its menu entry does not delete or reclassify archives. An additional rollback checkpoint is preserved as `rollback/before-judy-video-only-20261114` at `82aa499`. See the root README for that checkpoint and the original pre-integration rollback instructions.
