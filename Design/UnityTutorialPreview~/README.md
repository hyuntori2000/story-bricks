# Story Bricks — Tutorial Preview

**[실행 방법 / Start here](START-HERE.md)** · Open `Assets/StoryBricks/Scenes/TutorialPreview.unity` for headset-free playback. The opening now leads to an automatic tutorial. Press **P** to simulate a double pinch and skip; **T** replays from the menu.

The original viewer-project notes follow; the tutorial flow and Unity version are superseded by the guide above.

Immerse the Bay 2026 XR hackathon project. A blind or low-vision child builds words with braille bricks; an AI asks follow-up questions and narrates a story; a viewer wearing a Meta Quest 3 / 3S sees the story as a watercolour animation above the bricks.

This repository holds the Unity project for the viewer's headset UI.

## Requirements

- Unity 6000.0.85f2 (URP, Android build target)
- Meta XR Core SDK, MR Utility Kit and Interaction SDK v209
- Meta Quest 3 or 3S

## Open and run

1. Open the project folder in Unity Hub. Unity regenerates the `Library` folder on first open.
2. Open `Assets/StoryBricks/Scenes/ParentUI.unity`.
3. Press Play. After the opening animation (about 9 seconds) the menu appears with three options.

| Menu option | What it does |
| --- | --- |
| Make a new story | The child's flow for JUDY · MOON · WIND · KITE. A double pinch finishes each word; the period brick plus a double pinch, or the Finish button, finishes the story. |
| Play a saved story | Plays the narrated story with the painted cut-outs, subtitles and sound effects. |
| Try a practice word | Tutorial: build FOX, pinch twice, learn the period brick, meet the fox. |

The build does not read the bricks. Each double pinch reveals the next expected word.

### Controls

| Action | Headset | Keyboard (editor) |
| --- | --- | --- |
| Finish a word, start a new story from the menu | Double pinch (index finger, either hand) | P |
| Operator: next beat | A on a Touch controller | Space or right arrow |
| Operator: start the tutorial from the menu | B on a Touch controller | T |

## Where things are

- `Assets/StoryBricks/Scripts/ViewerFlow.cs`: the whole flow: menu, tutorial, story building, playback, voices, double pinch.
- `Assets/StoryBricks/Scripts/StoryStage.cs`: the cut-out picture and its movements, cued to the narration.
- `Assets/StoryBricks/Scripts/OpeningAnimation.cs`: opening logo animation.
- `Assets/StoryBricks/Scripts/WashButton.cs`: hover and press feedback for buttons.
- `Assets/StoryBricks/Editor/WatercolourUIBuilder.cs`: menu item **StoryBricks > Build Watercolour UI** rebuilds the UI in the scene. Run it after changing layout, art or audio files.
- `Assets/StoryBricks/Art/`: painted cut-outs (hill, Judy, moon, wind, kite, fox, hand) and brick images rendered from the brick models.
- `Assets/StoryBricks/Audio/Voice/System/`: Story Bricks' lines, numbered. `Voice/Judy/`: Judy's lines. `Voice/narration_judy_moon.mp3`: the story narration.
- `Assets/StoryBricks/Audio/SFX/story_sfx_judy_moon.wav`: all story sound effects on one track, timed to the narration. `Tools/make_story_sfx.py` regenerates it (needs Python with numpy).
- `Assets/StoryBricks/UI/`: generated watercolour sprites (palette A).

## Not in the repository

`Assets/Resources/DevAgentSettings.asset` is ignored because it holds a local access token. Unity's Meta tooling recreates it.

## Button images

`Design/buttons/button_finish.png` is the Finish button as a transparent PNG (648 x 312, drawn for 216 x 104), in the same wash and pencil style as the UI. `button_blank.png` is the same button without a label.
