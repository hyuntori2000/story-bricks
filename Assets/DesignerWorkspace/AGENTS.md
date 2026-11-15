# Designer-owned Unity content

Keep Unity design changes in this folder. Dashboard and design-document work belongs in `Dashboard/` and `Design/`.

Use the shared `StoryFunctionBridge` and presentation events. Do not change Python/OCR, shared runtime/API/security code, the XR rig, conversation/microphone logic, project settings, packages, or shared Editor/build scripts to make a prefab work. Report the required interface change to the project owner instead.

Preserve existing `.meta` files and GUIDs. Do not replace the rig, regenerate shared scenes, access API keys, call external AI services, or edit these policies.

Explicit project-owner authorization for core work takes precedence, including the currently authorized integration and handoff. Ordinary designer requests do not grant that authorization.
