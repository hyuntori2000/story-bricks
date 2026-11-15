# Words to Worlds — shared functions

This repository shares the working functions. The designers build their own UX and dashboard.

| Folder | Owner / purpose |
| --- | --- |
| `Assets/BrailleSoundDemo/` | Protected Unity core: capture, word anchors, four-word conversation and media functions. |
| `Python/` | Protected backend: local OCR, speech and API integration. |
| `Assets/DesignerWorkspace/` | Designers: Unity scenes, UX prefabs, art and media. |
| `Dashboard/` | Designers: their own dashboard implementation. |
| `Design/` | Designers: storyboard and content references. |

**Do not modify recognition or Demo logic for a design task.** Read [AGENTS.md](AGENTS.md). Request shared-function changes from the project owner.

## Use the Unity functions

Use **Unity 6000.3.28f1** with Android Build Support and **Git LFS**. Run `git lfs install` and `git lfs pull` after cloning. Open `Assets/Scenes/PhysicalStoryDemo.unity`; save a designer-owned scene copy under `Assets/DesignerWorkspace/` before adding UX.

Attach **StoryFunctionBridge** to your UI and bind its public methods to your own buttons. Attach **WordMediaPresenter** to your media prefab and assign that prefab to the coordinator. Local images/audio/video work without a media dashboard. See [Component connections](Documentation/COMPONENTS.md).

Both live and prepared modes use the shared double-pinch capture, three-second wait and cropped-image OCR. Word boxes can be created during conversation/playback. Live stories use **four accepted words and four saved answers**; extra words wait for the next story. Prepared mode does not automatically call the conversation API.

## Backend setup

Install **Python 3.12**, then run:

```sh
python3.12 Tools/setup-team.py --backend
```

Copy `.env.local.example` to `.env.local` and `.env.elevenlabs.example` to `.env.elevenlabs.local` on the backend machine; supply your own authorized keys. Never commit keys. In separate terminals:

```sh
bash Tools/start-physical-story-receiver.sh
bash Tools/start-backend.sh
```

The existing capture monitor opens at **http://127.0.0.1:8769/**. Connect/build/install a Quest with:

```sh
bash Tools/connect-quest.sh YOUR_QUEST_SERIAL
bash Tools/build-physical-story.sh
bash Tools/install-physical-story.sh YOUR_QUEST_SERIAL
```

These commands build the shared baseline scene. To test your own UX scene on Quest, ask the owner to select that scene for a separate build; the baseline build script does not include designer scene copies automatically.

The scripts support `UNITY_EDITOR` and `ADB` overrides. Designers do not need API keys to edit prefabs or prepared media. The OCR source is pinned in `Config/recognition-source.json` and installed under ignored `External/LEGO_OCR`.

## Keep changes separate

Designers use `design/*` branches and only their three folders above. Preserve Unity `.meta` files. The repository includes English AI instructions, CODEOWNERS and a design-boundary check. Those are review controls; they are not a local filesystem lock. Do not merge core changes without the owner's review.

Capture files, local keys, virtual environments, generated builds and temporary experiments are excluded from Git. Unity scenes are preserved during normal builds.

The supplied Judy reference is **JUDY → FOX → RAIN → TREE**. Actual designer UX/media files still need to be added. Automatic Orbis generation and public video sharing are not connected. Extra dashboard/UX scaffolding is intentionally not included in this simple handoff.
