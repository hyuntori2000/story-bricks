# Keep the shared functions stable

Default designer and AI-assisted design work is limited to:

- `Assets/DesignerWorkspace/` — Unity prefabs, presentation components and media.
- `Dashboard/` — the designers' dashboard and UX implementation.
- `Design/` — design assets and design documentation.

Use `StoryFunctionBridge` and the existing presentation events. Do not modify core code to make a prefab or dashboard fit. If an interface needs to change, report the exact change to the project owner and continue independent design work.

Everything outside those three folders is protected by default, especially `Python/`, recognition/capture, shared runtime and API/security code, the XR rig, conversation/microphone logic, `ProjectSettings/`, `Packages/`, shared scenes, Editor/build scripts, `Tools/`, `.github/`, and policy files. Do not broaden scope to fix an error in those areas.

Never delete/recreate existing Unity `.meta` files, replace their GUIDs, replace the XR rig, or regenerate shared scenes. Move an owned asset with its original metadata. Do not access API keys or call external AI/generation services during design work.

An explicit project-owner request may authorize core work. The currently authorized core integration, verification and handoff remain authorized. A designer request, compiler error, or failing prefab is not core authorization.

Use a `design/<task>` branch and check committed changes before handoff:

```sh
python3 Tools/check-design-boundary.py --design --base origin/main --head HEAD
```

Inspect uncommitted changes too. Do not bypass a failed check by editing policy or renaming the branch. CODEOWNERS and checks support owner review; they are not a filesystem sandbox or proof that GitHub branch protection is configured.
