# Story Bricks presentation

This presentation comes from the supplied `story-bricks-main-2` Unity project. The active layout was extracted from its `ParentUI.unity` scene, rather than the older `ParentUI.prefab`. The main application's existing XR rig, capture system and backend remain shared services.

## The two routes

**Judy** plays the supplied 61.5-second Unity cutout story: its original artwork, narrator, subtitles and synchronized sound-effect track. `StoryStage` animates the drawings against the narration clock. This is an authored Unity presentation, not the supplied 12-second MP4; that MP4 is not included. The end screen offers replay, Create Your World and return to the menu. Judy does not capture words, record answers, or call generation APIs.

**Create Your World** uses actual captured words and the existing conversation and generation functions. Its word tags, captions and listening/progress indicators follow live events. It never runs the source prototype's JUDY/MOON/WIND/KITE inputs, automatic recorded answers, simulated preparation timer or tutorial. Keep the existing small core Finish button and its Create Story / End Session / Cancel dialog. Generated animation starts after the real story narration finishes, and playback waits for the actual saved movie.

## What designers can edit

- `Assets/DesignerWorkspace/Resources/StoryBricks/View.prefab`: the supplied watercolour layout and its serialized presentation references.
- `Assets/DesignerWorkspace/StoryBricks/Art`, `UI`, `Fonts`, `Audio`: authored graphics and sound. Keep `.meta` files when moving or sharing assets.
- `Scripts/StoryStage.cs`: the authored Judy cutout animation.
- `Scripts/JudyStoryPlayer.cs`: Judy narration-clock playback and cleanup.
- `Scripts/ViewerFlow.cs`: passive UI transitions and navigation requests. It does not own recognition, microphone input or backend calls.

Use `StoryFunctionBridge` for core actions. `ExperienceRouter` owns navigation and listens to the view's requests. Do not copy a second XR rig, camera, EventSystem, capture loop or microphone controller into this prefab. Changes to shared functions require the owner's integration branch.

## Opening and input

The existing opening animation and accessible narrator prompt remain in place. After the prompt, a fresh double pinch enters Create Your World. The two menu buttons provide explicit route selection. The shared hand input handles UI buttons; the imported prototype's independent pinch and keyboard listeners are not included.

## Importing and opening

Normal checkouts use the committed prefab and assets; the original Downloads folder is not required. Open the main project with Unity **6000.3.28f1** and run `git lfs pull` to obtain the media.

`Story Bricks > Update supplied presentation` is an explicit reimport operation. It reads `STORY_BRICKS_UX_SOURCE` (default: the owner's `story-bricks-main-2` folder), preserves the existing prefab GUID, reuses existing asset GUIDs, and extracts only presentation objects. Reimport can replace prefab layout edits; commit your design work before using it. Normal builds use the idempotent initial importer and preserve the committed view.

Do not run the original source builders or replace the main project's Packages, ProjectSettings or shared scenes. Source URP/XR settings are not part of this integration.

## Branch handoff

The supplied assets and presentation code are on `design/judy-ux-integration`; the working application integration is on `dev/ux-integration`. Start further design work from the integrated development branch, then create a new `design/<task>` branch. Review design diffs against that branch's actual base commit, so inherited owner changes are not mistaken for new design edits. Do not merge into `main` without the owner's approval.

Run the main app (`org.braillelab.storydemo`) for the integrated routes. The separate `com.storybricks.parentui` app is the original scripted prototype.
