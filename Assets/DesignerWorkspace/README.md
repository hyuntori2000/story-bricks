# Designer workspace

Put your existing Unity UX, prefabs, scenes, artwork, audio and video here. Keep every asset's `.meta` file.

Use the shared `StoryFunctionBridge` to connect your buttons, and `WordMediaPresenter` to attach media to word boxes. Read `Documentation/COMPONENTS.md` for the small public interface.

Bind **Home** to `GoHome()`, **Create Story** to `CreateStory()`, and **See previous story** to `SeePreviousStories()`. Home clears the active session and returns to landing; saved films remain on the Mac. Create Story uses saved progress immediately and continues to animation. Previous-story viewing replays completed films without generating again. Keep the shared short word-pair dialogue and period → final question → spoken answer → story flow in the core; presentation work should follow its events.

Start from the working `Assets/Scenes/PhysicalStoryDemo.unity`, save your own copy in this folder, and keep its capture coordinator and XR rig. Add or replace the presentation objects. Do not add a second XR rig, camera or AudioListener.

Do not edit `Assets/BrailleSoundDemo`, `Python`, `Packages` or `ProjectSettings`. Request a core change from the project owner when needed.
