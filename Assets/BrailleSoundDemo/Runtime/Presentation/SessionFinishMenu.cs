using System.Collections.Generic;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;
using UnityEngine.XR;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Small rig-local menu. Owns only its visuals and pointer input, never the XR rig.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(190)]
    public sealed class SessionFinishMenu : MonoBehaviour
    {
        enum Action { None, Open, Create, End, Cancel, Start }

        sealed class Button
        {
            public Transform hitArea;
            public MeshRenderer renderer;
            public TextMesh label;
            public Action action;
            public bool enabled = true, hovered;
        }

        static readonly Color PanelColor = new Color(.035f, .055f, .075f);
        static readonly Color ButtonColor = new Color(.12f, .24f, .31f);
        static readonly Color HoverColor = new Color(.12f, .46f, .59f);
        static readonly Color DisabledColor = new Color(.09f, .12f, .14f);
        readonly List<Button> buttons = new List<Button>();
        readonly bool[] wasPressed = new bool[4], sawRelease = new bool[4];
        readonly LineRenderer[] rays = new LineRenderer[4];
        readonly OVRHand[] hands = new OVRHand[2];
        readonly MaterialPropertyBlock colors = new MaterialPropertyBlock();
        PhysicalStoryCoordinator coordinator;
        OVRCameraRig rig;
        Transform visualRoot, finishRoot, dialogRoot, startRoot;
        Transform dialogSurface, startSurface;
        TextMesh message;
        Material material;
        Font font;
        bool dialogOpen, uiPressHeld;
        float releaseGraceUntil, nextHandSearch;
        string notice = "";

        public static SessionFinishMenu Attach(PhysicalStoryCoordinator owner, OVRCameraRig cameraRig)
        {
            if (owner == null || cameraRig == null) return null;
            var menu = owner.GetComponent<SessionFinishMenu>();
            if (menu == null) menu = owner.gameObject.AddComponent<SessionFinishMenu>();
            menu.coordinator = owner;
            menu.rig = cameraRig;
            menu.enabled = true;
            menu.EnsureVisuals();
            return menu;
        }

        public void Open()
        {
            if (coordinator != null && !AtStart) Activate(Action.Open);
        }

        bool AtStart => coordinator != null && coordinator.SessionAtStart;
        bool StorySubmitted
        {
            get
            {
                var flow = coordinator?.Flow;
                return flow == null || flow.StoryFinishRequested || flow.State == ConversationState.RequestingStory ||
                    flow.State == ConversationState.Story || flow.State == ConversationState.Finished;
            }
        }

        void EnsureVisuals()
        {
            if (visualRoot != null || rig == null || rig.centerEyeAnchor == null) return;
            material = new Material(Shader.Find("Unlit/Color"));
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            visualRoot = NewRoot("Session menu", rig.centerEyeAnchor, new Vector3(0, 0, .8f));
            finishRoot = NewRoot("Finish", visualRoot, new Vector3(.27f, .18f, 0));
            AddButton(finishRoot, "Finish", Action.Open, Vector3.zero, new Vector2(.135f, .06f));

            dialogRoot = NewRoot("Finish dialog", visualRoot, Vector3.zero);
            dialogSurface = Surface("Dialog background", dialogRoot, Vector3.zero, new Vector2(.64f, .33f), PanelColor).transform;
            Label(dialogRoot, "Would you like to finish?", new Vector3(0, .108f, -.01f), .024f);
            Label(dialogRoot, "Create a story from your saved progress,\nor end this session?", new Vector3(0, .042f, -.01f), .017f);
            AddButton(dialogRoot, "Create Story", Action.Create, new Vector3(-.205f, -.054f, -.01f), new Vector2(.185f, .065f));
            AddButton(dialogRoot, "End Session", Action.End, new Vector3(0, -.054f, -.01f), new Vector2(.185f, .065f));
            AddButton(dialogRoot, "Cancel", Action.Cancel, new Vector3(.205f, -.054f, -.01f), new Vector2(.185f, .065f));
            message = Label(dialogRoot, "", new Vector3(0, -.121f, -.01f), .016f);

            startRoot = NewRoot("Start screen", visualRoot, Vector3.zero);
            startSurface = Surface("Start background", startRoot, Vector3.zero, new Vector2(.4f, .2f), PanelColor).transform;
            Label(startRoot, "Story Bricks", new Vector3(0, .05f, -.01f), .028f);
            AddButton(startRoot, "Start Story", Action.Start, new Vector3(0, -.035f, -.01f), new Vector2(.23f, .065f));
            for (int i = 0; i < rays.Length; i++)
            {
                var rayObject = new GameObject("Session menu pointer " + i);
                rayObject.transform.SetParent(transform, false);
                var line = rayObject.AddComponent<LineRenderer>();
                line.sharedMaterial = material;
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = .0015f;
                line.endWidth = .0025f;
                line.startColor = line.endColor = new Color(.35f, .85f, 1f);
                line.enabled = false;
                Tint(line, new Color(.35f, .85f, 1f));
                rays[i] = line;
            }
            Refresh();
        }

        static Transform NewRoot(string name, Transform parent, Vector3 position)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            root.localPosition = position;
            return root;
        }

        MeshRenderer Surface(string name, Transform parent, Vector3 position, Vector2 size, Color color)
        {
            var surface = GameObject.CreatePrimitive(PrimitiveType.Cube);
            surface.name = name;
            surface.transform.SetParent(parent, false);
            surface.transform.localPosition = position;
            surface.transform.localScale = new Vector3(size.x, size.y, .006f);
            var collider = surface.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            var renderer = surface.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Tint(renderer, color);
            return renderer;
        }

        TextMesh Label(Transform parent, string text, Vector3 position, float size)
        {
            var label = NewRoot(text.Length > 0 ? text.Replace('\n', ' ') : "Menu message", parent, position).gameObject.AddComponent<TextMesh>();
            label.font = font;
            label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 64;
            // TextMesh's glyph metrics use font pixels / 10 world units.
            // Keep the requested em size in metres independent of atlas size.
            label.characterSize = size * 10f / label.fontSize;
            label.color = Color.white;
            label.text = text;
            return label;
        }

        void AddButton(Transform parent, string text, Action action, Vector3 position, Vector2 size)
        {
            var renderer = Surface(text + " button", parent, position, size, ButtonColor);
            var label = Label(parent, text, position + new Vector3(0, 0, -.005f), .02f);
            buttons.Add(new Button { hitArea = renderer.transform, renderer = renderer, label = label, action = action });
        }

        void Tint(Renderer renderer, Color color)
        {
            colors.SetColor("_Color", color);
            renderer.SetPropertyBlock(colors);
        }

        void Update()
        {
            if (coordinator == null || rig == null) return;
            EnsureVisuals();
            if (visualRoot == null) return;
            if (AtStart) { dialogOpen = false; notice = ""; }
            Refresh();
            if (Time.unscaledTime >= nextHandSearch)
            {
                if (hands[0] == null && rig.leftHandAnchor != null) hands[0] = rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
                if (hands[1] == null && rig.rightHandAnchor != null) hands[1] = rig.rightHandAnchor.GetComponentInChildren<OVRHand>(true);
                nextHandSearch = Time.unscaledTime + 1;
            }

            bool hovering = false, anyPressed = false;
            Action selected = Action.None;
            foreach (var button in buttons) button.hovered = false;
            for (int i = 0; i < rays.Length; i++)
            {
                bool valid = ReadPointer(i, out var ray, out bool pressed);
                anyPressed |= pressed;
                bool rising = valid && sawRelease[i] && pressed && !wasPressed[i];
                if (valid && !pressed) sawRelease[i] = true;
                if (!valid) sawRelease[i] = false;
                wasPressed[i] = pressed;
                Button target = null;
                float distance = float.PositiveInfinity;
                if (valid)
                {
                    foreach (var button in buttons)
                        if (Hit(button.hitArea, ray, out float hit) && hit < distance)
                        { target = button; distance = hit; }
                    Transform panel = AtStart ? startSurface : dialogOpen ? dialogSurface : null;
                    if (target == null && Hit(panel, ray, out float panelHit)) distance = panelHit;
                }
                bool overUi = !float.IsPositiveInfinity(distance);
                hovering |= overUi;
                rays[i].enabled = overUi;
                if (overUi)
                {
                    rays[i].SetPosition(0, ray.origin);
                    rays[i].SetPosition(1, ray.GetPoint(distance));
                    if (pressed) uiPressHeld = true;
                }
                if (target == null) continue;
                target.hovered = true;
                if (rising && target.enabled && selected == Action.None) selected = target.action;
            }
            // Keep the capture gate closed through the release frame. The capture
            // driver then requires a fresh release and two fresh pinches.
            if (uiPressHeld && !anyPressed)
            { uiPressHeld = false; releaseGraceUntil = Time.unscaledTime + .2f; }
            coordinator.SetFinishMenuOpen(AtStart || dialogOpen || hovering || uiPressHeld || Time.unscaledTime < releaseGraceUntil);
            if (selected != Action.None) Activate(selected);
            foreach (var button in buttons)
            {
                Tint(button.renderer, !button.enabled ? DisabledColor : button.hovered ? HoverColor : ButtonColor);
                button.label.color = button.enabled ? Color.white : new Color(.5f, .56f, .6f);
            }
        }

        bool ReadPointer(int index, out Ray ray, out bool pressed)
        {
            ray = default;
            pressed = false;
            Transform pose;
            if (index < 2)
            {
                var hand = hands[index];
                if (hand == null || !hand.isActiveAndEnabled || !hand.IsTracked || !hand.IsDataValid) return false;
                pressed = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (!hand.IsPointerPoseValid || hand.IsSystemGestureInProgress || hand.PoseSourceInferred) return false;
                pose = hand.PointerPose;
            }
            else
            {
                var controller = index == 2 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
                if (!OVRInput.GetControllerPositionTracked(controller) || !OVRInput.GetControllerOrientationTracked(controller)) return false;
                pressed = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, controller);
                pose = index == 2 ? rig.leftControllerAnchor : rig.rightControllerAnchor;
            }
            if (pose == null) return false;
            ray = new Ray(pose.position, pose.forward);
            return true;
        }

        // Intersect only menu-owned boxes; physical bricks and scene colliders
        // cannot intercept these rays, and the menu adds no physics targets.
        static bool Hit(Transform box, Ray ray, out float distance)
        {
            distance = 0;
            if (box == null || !box.gameObject.activeInHierarchy) return false;
            var origin = box.InverseTransformPoint(ray.origin);
            var direction = box.InverseTransformVector(ray.direction);
            if (Mathf.Abs(direction.z) < .0001f) return false;
            float t = (-.5f - origin.z) / direction.z;
            if (t <= 0 || t > 3) return false;
            var point = origin + direction * t;
            if (Mathf.Abs(point.x) > .5f || Mathf.Abs(point.y) > .5f) return false;
            distance = t;
            return true;
        }

        void Activate(Action action)
        {
            coordinator.SetFinishMenuOpen(true);
            releaseGraceUntil = Time.unscaledTime + .2f;
            switch (action)
            {
                case Action.Open: dialogOpen = true; notice = ""; break;
                case Action.Cancel: dialogOpen = false; notice = ""; break;
                case Action.Start: coordinator.StartSession(); notice = ""; break;
                case Action.End:
                    coordinator.EndSessionToStart();
                    dialogOpen = false; notice = "";
                    break;
                case Action.Create:
                    if (StorySubmitted) break;
                    if (coordinator.SavedWordCount == 0) { notice = "Capture at least one word first."; break; }
                    if (coordinator.CreateStoryFromSavedProgress())
                    { dialogOpen = false; notice = ""; }
                    else notice = "Unable to create a story yet. Please try again.";
                    break;
            }
            // Session resets may clear coordinator state. This selection still
            // owns the input until the release/grace check in the next Update.
            coordinator.SetFinishMenuOpen(true);
            Refresh();
        }

        void Refresh()
        {
            if (visualRoot == null) return;
            finishRoot.gameObject.SetActive(!AtStart && !dialogOpen);
            dialogRoot.gameObject.SetActive(!AtStart && dialogOpen);
            startRoot.gameObject.SetActive(AtStart);
            message.text = notice;
            foreach (var button in buttons) button.enabled = button.action != Action.Create || !StorySubmitted;
        }

        void OnGUI()
        {
            if (coordinator == null || XRSettings.isDeviceActive) return;
            if (!AtStart && !dialogOpen)
            {
                if (GUI.Button(new Rect(Screen.width - 120, 20, 100, 42), "Finish")) Activate(Action.Open);
                return;
            }
            var panel = new Rect((Screen.width - 540) / 2f, (Screen.height - 220) / 2f, 540, 220);
            GUILayout.BeginArea(panel, GUI.skin.box);
            GUILayout.Space(20);
            GUILayout.Label(AtStart ? "Story Bricks" : "Would you like to finish?");
            GUILayout.Space(12);
            if (AtStart)
            {
                if (GUILayout.Button("Start Story", GUILayout.Height(45))) Activate(Action.Start);
            }
            else
            {
                GUILayout.Label("Create a story from your saved progress, or end this session?");
                GUILayout.Space(20);
                GUILayout.BeginHorizontal();
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && !StorySubmitted;
                if (GUILayout.Button("Create Story", GUILayout.Height(45))) Activate(Action.Create);
                GUI.enabled = enabled;
                if (GUILayout.Button("End Session", GUILayout.Height(45))) Activate(Action.End);
                if (GUILayout.Button("Cancel", GUILayout.Height(45))) Activate(Action.Cancel);
                GUILayout.EndHorizontal();
                GUILayout.Label(notice);
            }
            GUILayout.EndArea();
        }

        void OnDisable()
        {
            if (visualRoot != null) visualRoot.gameObject.SetActive(false);
            foreach (var ray in rays) if (ray != null) ray.enabled = false;
            coordinator?.SetFinishMenuOpen(false);
        }

        void OnEnable() { if (visualRoot != null) visualRoot.gameObject.SetActive(true); }

        void OnDestroy()
        {
            if (visualRoot != null) Destroy(visualRoot.gameObject);
            foreach (var ray in rays) if (ray != null) Destroy(ray.gameObject);
            if (material != null) Destroy(material);
        }
    }
}
