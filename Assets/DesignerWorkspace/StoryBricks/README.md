# Story Bricks presentation and Judy cues

The watercolour view is shared visually. Judy and Create World have separate session owners in the core application. Presentation code must not start capture, change recognition, call an AI API, reset the session, or start another route.

## Assets to edit

- `Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab`: the imported watercolour layout. Change graphics, typography, spacing and visual transitions here. Its menu buttons request Judy or Create World from the core router. Keep the existing Finish menu owned by the core.
- `Assets/DesignerWorkspace/Resources/StoryBricks/JudySequence.asset`: Judy's ordered word cues and final prepared story. The initial words are `JUDY`, `FOX`, `RAIN`, `TREE`, using the supplied storyboard text.
- Keep new artwork, media and presentation prefabs inside `Assets/DesignerWorkspace/`. Preserve their `.meta` files when sharing or moving them.

The supplied UX contained captions but no prepared narration, sound effects or video. Until those assets are assigned, Judy displays an explicitly labelled text preview. Create World never substitutes Judy media or auto-plays a demonstration video.

## Author a Judy word cue

Select `JudySequence.asset` in Unity. Expand a word's `Steps`, then add one or more steps. Drag assets into these Inspector fields:

| Field | What it does |
| --- | --- |
| Expected Word | Starts this cue only when the real recognizer accepts the next expected word. A trailing period is ignored for this presentation match. |
| Caption | Text displayed during the step. |
| Prefab | A presentation-only prefab placed above that word's captured box. |
| Image | A texture displayed above the box. |
| Voice | Prepared spoken audio, played without spatial positioning. |
| Sound | A prepared sound effect played from the box. |
| Video | A prepared local video clip. It advances after playback ends. |
| Offset / Image Size Meters | Position and size of the word's visual content. |
| Caption Preview Seconds | Hold time for a step without timed media. This does not shorten audio, video or an explicit prefab completion signal. |

Steps run in order. Voice, sound and video assigned to the same step start together, so use separate steps when they should follow one another. The player waits for all assigned finite media to finish. Visuals remain attached to their word box until the session is ended.

For an authored prefab animation, add `JudyCueSignal` to that prefab, enable `Wait For Prefab Signal` on the step, and call `JudyCueSignal.Complete()` at the animation's end through an Animation Event, Timeline signal or UnityEvent. Without that call, the cue intentionally keeps waiting. Never put an XR rig, camera, AudioListener or core coordinator inside a presentation prefab.

Captures accepted while a cue plays are queued in the expected word order. An unexpected word is still retained by the core capture system, but does not advance Judy's authored cue sequence. A group is played only once. Cue editing does not change the recognizer's output.

## Final story and Finish

`Final Story Text` is the prepared story exported by Judy. `Final Story` contains the prepared media steps to play when Create Story is selected. Create Story waits for the already accepted word cues to finish, then plays these final steps and hands control back to the router for the next stage. It never resets the session itself.

The existing Finish menu controls both routes. End Session clears the active presentation and returns to selection; Create Story preserves progress. Create World's future animation playback is controlled by actual generation readiness in the core, not by a timer in this view.

## View and import rules

`ViewerFlow` is passive: `ShowWord`, `ShowCaption`, `SetListening`, `SetBusy`, `ShowStoryReady` and `ShowNextStage` display events from the router. Menu selection emits `JudyRequested` or `CreateWorldRequested`. It contains no keyboard demo, injected words, automatic Story Ready playback or reset-on-enable behavior. The word row retains accepted display entries and shows the most recent six to keep the panel readable.

The committed `View.prefab` is extracted from the latest supplied scene, not the older supplied `ParentUI.prefab`. `Story Bricks > Import supplied presentation once` is an initial import helper; it preserves an existing view. Normal team checkouts use the committed prefab and do not need the original Downloads folder. Do not run the original incoming project builders or copy its Packages, ProjectSettings or XR rig into the main application.

The copied font assets keep their original GUIDs and embedded atlas textures. The included TextMesh Pro mobile distance-field shader is the exact dependency used by those materials, compatible with the main project's existing renderer.
