using System.Collections.Generic;
using BrailleSoundDemo.PhysicalStory;
using UnityEngine;
using UnityEngine.XR;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>Home and Create Story actions. Owns only its visuals and pointer input, never the XR rig.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(190)]
    public sealed class SessionFinishMenu : MonoBehaviour
    {
        enum Action { None, Home, Create }

        // Kept independent of the tracking SDK so input ownership can be checked offline.
        public struct ActionPointer
        {
            bool released, ownsPress;
            float releaseUntil;
            public bool Pressed { get; private set; }
            public bool BlocksCapture(float now) => ownsPress || now < releaseUntil;

            public bool Observe(float now, bool valid, bool nativePinch, float strength, bool overAction)
            {
                bool pressed = valid && (nativePinch || strength >= (Pressed ? .4f : .75f));
                bool rising = valid && released && pressed && !Pressed;
                if (ownsPress && !pressed) { ownsPress = false; releaseUntil = now + .2f; }
                if (!valid) released = false;
                else if (!pressed) released = true;
                Pressed = pressed;
                bool clicked = rising && overAction;
                if (clicked) ownsPress = true;
                return clicked;
            }
        }

        sealed class Button
        {
            public Transform hitArea;
            public MeshRenderer renderer;
            public TextMesh label;
            public Action action;
            public bool enabled = true, hovered, painted;
        }

        static readonly Color ButtonColor = new Color(.12f, .24f, .31f);
        static readonly Color HoverColor = new Color(.12f, .46f, .59f);
        static readonly Color DisabledColor = new Color(.09f, .12f, .14f);
        readonly List<Button> buttons = new List<Button>();
        readonly List<Material> paintedMaterials = new List<Material>();
        readonly ActionPointer[] pointers = new ActionPointer[4];
        readonly Button[] recentTargets = new Button[4];
        readonly float[] recentTargetTimes = new float[4];
        readonly LineRenderer[] rays = new LineRenderer[4];
        readonly OVRHand[] hands = new OVRHand[2];
        MaterialPropertyBlock colors;
        PhysicalStoryCoordinator coordinator;
        OVRCameraRig rig;
        Transform visualRoot, actionRoot;
        TextMesh message;
        Material material;
        Texture2D homeTexture, createStoryTexture;
        Font font;
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

        // Keep the existing integration surface while replacing the former
        // Finish dialog with the two direct actions requested for the demo.
        public bool DialogOpen => false;

        public void Open() => GoHome();

        public void GoHome()
        {
            if (coordinator != null && coordinator.ExperienceActive) Activate(Action.Home);
        }

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
            // Native Unity objects must be created after MonoBehaviour construction.
            colors = new MaterialPropertyBlock();
            material = new Material(Shader.Find("Unlit/Color"));
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            visualRoot = NewRoot("Session menu", rig.centerEyeAnchor, new Vector3(0, 0, .8f));
            actionRoot = NewRoot("Story actions", visualRoot, new Vector3(-.25f, .30f, 0));
            homeTexture = Resources.Load<Texture2D>("StoryBricks/Buttons/home");
            createStoryTexture = Resources.Load<Texture2D>("StoryBricks/Buttons/create_story");
            AddPaintedButton(actionRoot, "Home", Action.Home, new Vector3(-.065f, 0, 0), homeTexture);
            AddPaintedButton(actionRoot, "Create Story", Action.Create, new Vector3(.065f, 0, 0), createStoryTexture);
            message = Label(actionRoot, "", new Vector3(0, -.05f, -.01f), .014f);

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
            if (collider != null) { collider.enabled = false; Destroy(collider); }
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

        void AddPaintedButton(Transform parent, string text, Action action, Vector3 position, Texture2D texture)
        {
            const float width = .115f;
            var shader = Resources.Load<Shader>("WordImageAlpha");
            if (texture == null || shader == null)
            {
                Debug.LogError("[SessionFinishMenu] Missing painted button asset: " + text);
                AddButton(parent, text, action, position, new Vector2(width, .055f));
                return;
            }
            // The PNG includes its label. A separate, full rectangular target keeps
            // the painted transparent fringe from making pinches difficult to hit.
            var hit = NewRoot(text + " target", parent, position);
            hit.localScale = new Vector3(width, width * texture.height / texture.width, .006f);
            var picture = GameObject.CreatePrimitive(PrimitiveType.Quad);
            picture.name = text + " artwork";
            picture.transform.SetParent(hit, false);
            picture.transform.localPosition = new Vector3(0, 0, -.51f);
            var collider = picture.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
            var ink = new Material(shader) { name = text + " button", mainTexture = texture, color = Color.white };
            paintedMaterials.Add(ink);
            var renderer = picture.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = ink;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            buttons.Add(new Button { hitArea = hit, renderer = renderer, action = action, painted = true });
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
            if(!coordinator.ExperienceActive)
            {
                if(visualRoot!=null) visualRoot.gameObject.SetActive(false);
                foreach(var ray in rays) if(ray!=null) ray.enabled=false;
                System.Array.Clear(pointers, 0, pointers.Length);
                System.Array.Clear(recentTargets, 0, recentTargets.Length);
                coordinator.SetFinishMenuOpen(false);
                return;
            }
            if(visualRoot!=null) visualRoot.gameObject.SetActive(true);
            if (visualRoot == null) return;
            Refresh();
            if (Time.unscaledTime >= nextHandSearch)
            {
                hands[0] = FindActiveHand(rig.leftHandAnchor, hands[0]);
                hands[1] = FindActiveHand(rig.rightHandAnchor, hands[1]);
                nextHandSearch = Time.unscaledTime + 1;
            }

            bool ownsInput = false;
            Action selected = Action.None;
            foreach (var button in buttons) button.hovered = false;
            for (int i = 0; i < rays.Length; i++)
            {
                bool valid = ReadPointer(i, out var ray, out bool nativePinch, out float strength, out bool inputValid);
                if (!inputValid) recentTargets[i] = null;
                Button target = null;
                float distance = float.PositiveInfinity;
                if (valid)
                {
                    foreach (var button in buttons)
                        if (Hit(button.hitArea, ray, out float hit) && hit < distance)
                        { target = button; distance = hit; }
                }
                bool overUi = !float.IsPositiveInfinity(distance);
                rays[i].enabled = overUi;
                if (overUi)
                {
                    rays[i].SetPosition(0, ray.origin);
                    rays[i].SetPosition(1, ray.GetPoint(distance));
                }
                var pressedTarget = target;
                // A tracked pinch can briefly invalidate/move only its pointing ray.
                if (pressedTarget == null && Time.unscaledTime - recentTargetTimes[i] <= .35f)
                    pressedTarget = recentTargets[i];
                bool hasTarget = pressedTarget != null && pressedTarget.hitArea.gameObject.activeInHierarchy;
                bool clicked = pointers[i].Observe(Time.unscaledTime, inputValid, nativePinch, strength, hasTarget);
                ownsInput |= pointers[i].BlocksCapture(Time.unscaledTime);
                if (target != null)
                {
                    target.hovered = true;
                    if (inputValid && !pointers[i].Pressed)
                    { recentTargets[i] = target; recentTargetTimes[i] = Time.unscaledTime; }
                }
                if (clicked && pressedTarget.enabled && selected == Action.None)
                {
                    selected = pressedTarget.action;
                    Debug.Log("[SessionFinishMenu] Selected " + selected + " pointer=" + i);
                }
            }
            // Keep the capture gate closed through the release frame. The capture
            // driver then requires a fresh release and two fresh pinches.
            // Hover is visual only. Only the hand that pressed a button owns input;
            // an unrelated pinch with the other hand cannot extend the UI gate.
            coordinator.SetFinishMenuOpen(ownsInput || Time.unscaledTime < releaseGraceUntil);
            if (selected != Action.None) Activate(selected);
            foreach (var button in buttons)
            {
                var tint = button.painted
                    ? (!button.enabled ? new Color(.7f, .7f, .7f, .6f) : button.hovered ? new Color(1f, .90f, .76f) : Color.white)
                    : (!button.enabled ? DisabledColor : button.hovered ? HoverColor : ButtonColor);
                Tint(button.renderer, tint);
                if (button.label != null) button.label.color = button.enabled ? Color.white : new Color(.5f, .56f, .6f);
            }
        }

        static OVRHand FindActiveHand(Transform anchor, OVRHand current)
        {
            if (current != null && current.isActiveAndEnabled && current.IsTracked && current.IsDataValid) return current;
            if (anchor == null) return null;
            OVRHand active = null;
            foreach (var candidate in anchor.GetComponentsInChildren<OVRHand>(true))
            {
                if (!candidate.isActiveAndEnabled) continue;
                if (candidate.IsTracked && candidate.IsDataValid) return candidate;
                if (active == null) active = candidate;
            }
            return active;
        }

        bool ReadPointer(int index, out Ray ray, out bool pressed, out float strength, out bool inputValid)
        {
            ray = default;
            pressed = false;
            strength = 0;
            inputValid = false;
            Transform pose;
            if (index < 2)
            {
                var hand = hands[index];
                if (hand == null || !hand.isActiveAndEnabled || !hand.IsTracked || !hand.IsDataValid ||
                    hand.IsSystemGestureInProgress || hand.PoseSourceInferred) return false;
                inputValid = true;
                pressed = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (hand.GetFingerConfidence(OVRHand.HandFinger.Index) == OVRHand.TrackingConfidence.High &&
                    hand.GetFingerConfidence(OVRHand.HandFinger.Thumb) == OVRHand.TrackingConfidence.High)
                    strength = hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
                if (!hand.IsPointerPoseValid) return false;
                pose = hand.PointerPose;
            }
            else
            {
                var controller = index == 2 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
                if (!OVRInput.GetControllerPositionTracked(controller) || !OVRInput.GetControllerOrientationTracked(controller)) return false;
                inputValid = true;
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
            System.Array.Clear(recentTargets, 0, recentTargets.Length);
            coordinator.SetFinishMenuOpen(true);
            releaseGraceUntil = Time.unscaledTime + .2f;
            switch (action)
            {
                case Action.Home:
                    // EndSession clears active words/media and returns through the
                    // router; durable Mac/Quest story archives remain available.
                    coordinator.EndSessionToStart();
                    notice = "";
                    break;
                case Action.Create:
                    if (StorySubmitted) break;
                    if (coordinator.SavedWordCount == 0) { notice = "Capture at least one word first."; break; }
                    notice = coordinator.CreateStoryFromSavedProgress()
                        ? "" : "Unable to create a story.\nPlease try again.";
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
            message.text = notice;
            message.gameObject.SetActive(!string.IsNullOrEmpty(notice));
            foreach (var button in buttons)
                button.enabled = button.action != Action.Create || (!StorySubmitted && coordinator.SavedWordCount > 0);
        }

        void OnGUI()
        {
            if (coordinator == null || !coordinator.ExperienceActive || XRSettings.isDeviceActive) return;
            if (GUI.Button(new Rect(20, 20, 90, 42), "Home")) Activate(Action.Home);
            bool allowed = GUI.enabled;
            GUI.enabled = allowed && !StorySubmitted && coordinator.SavedWordCount > 0;
            if (GUI.Button(new Rect(120, 20, 120, 42), "Create Story")) Activate(Action.Create);
            GUI.enabled = allowed;
            if (!string.IsNullOrEmpty(notice)) GUI.Label(new Rect(20, 66, 220, 60), notice);
        }

        void OnDisable()
        {
            if (visualRoot != null) visualRoot.gameObject.SetActive(false);
            foreach (var ray in rays) if (ray != null) ray.enabled = false;
            System.Array.Clear(pointers, 0, pointers.Length);
            System.Array.Clear(recentTargets, 0, recentTargets.Length);
            coordinator?.SetFinishMenuOpen(false);
        }

        void OnEnable() { if (visualRoot != null) visualRoot.gameObject.SetActive(true); }

        void OnDestroy()
        {
            if (visualRoot != null) Destroy(visualRoot.gameObject);
            foreach (var ray in rays) if (ray != null) Destroy(ray.gameObject);
            if (material != null) Destroy(material);
            foreach (var painted in paintedMaterials) if (painted != null) Destroy(painted);
        }
    }
}
