using System;
using System.Collections;
using BrailleSoundDemo.TableDepthTest;
using UnityEngine;

namespace BrailleSoundDemo.Presentation
{
    /// <summary>The existing opening, a bundled narrator prompt, then a hand-only route gesture.</summary>
    [DisallowMultipleComponent]
    public sealed class AccessibleOnboarding : MonoBehaviour
    {
        public const string PromptText = "Are you ready for the new worlds? pinch twice!";
        public bool ReadyForPinch { get; private set; }
        public bool Running { get; private set; }
        public event Action Ready;
        public event Action EnterRequested;
        readonly DoublePinchTimerPolicy gesture = new DoublePinchTimerPolicy();
        readonly OVRHand[] hands = new OVRHand[2];
        OVRCameraRig rig;
        OpeningAnimation opening;
        AudioSource voice;
        bool applicationPaused, focused = true;
        bool paused => applicationPaused || !focused;

        public void Configure(OVRCameraRig cameraRig)
        {
            rig = cameraRig;
            focused = Application.isFocused;
            if (voice != null) return;
            var node = new GameObject("Onboarding narrator");
            node.transform.SetParent(transform, false);
            voice = node.AddComponent<AudioSource>();
            voice.playOnAwake = false; voice.loop = false;
            voice.spatialBlend = 0; voice.volume = .8f;
        }

        public void Begin(bool playOpening)
        {
            Cancel();
            Running = true;
            StartCoroutine(Sequence(playOpening));
        }

        IEnumerator Sequence(bool playOpening)
        {
            while (paused) yield return null;
            if (playOpening)
            {
                var prefab = Resources.Load<GameObject>("StoryBricks/Opening");
                if (prefab == null) { Unavailable("Opening prefab is missing."); yield break; }
                var instance = Instantiate(prefab, transform, false);
                opening = instance.GetComponent<OpeningAnimation>();
                if (opening == null)
                { Destroy(instance); Unavailable("Opening animation is missing."); yield break; }
                opening.Head = rig.centerEyeAnchor;
                bool finished = false;
                opening.Completed += () => finished = true;
                Debug.Log("[Onboarding] Playing the authored braille opening and chime.");
                while (!finished) yield return null;
                Destroy(opening.gameObject); opening = null;
            }
            var clip = Resources.Load<AudioClip>("StoryAudio/onboarding_ready");
            if (clip == null) { Unavailable("Onboarding narration is missing."); yield break; }
            clip.LoadAudioData();
            while (clip.loadState == AudioDataLoadState.Loading || paused) yield return null;
            if (clip.loadState != AudioDataLoadState.Loaded)
            { Unavailable("Onboarding narration could not be loaded."); yield break; }
            voice.clip = clip; voice.Play();
            Debug.Log("[Onboarding] Narrator: " + PromptText);
            yield return null;
            while (paused || voice.isPlaying) yield return null;
            // Pinches made during the opening/narration cannot count toward entry.
            gesture.Reset(); ReadyForPinch = true;
            Debug.Log("[Onboarding] Ready. Double pinch with either hand; no button or target is required.");
            Ready?.Invoke();
        }

        void Update()
        {
            if (!Running || !ReadyForPinch || paused || rig == null) return;
            if (hands[0] == null) hands[0] = rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
            if (hands[1] == null) hands[1] = rig.rightHandAnchor.GetComponentInChildren<OVRHand>(true);
            bool left = Tracked(hands[0]), right = Tracked(hands[1]);
            var step = gesture.Observe(Time.realtimeSinceStartupAsDouble, true, true,
                left, left && hands[0].GetFingerIsPinching(OVRHand.HandFinger.Index),
                right, right && hands[1].GetFingerIsPinching(OVRHand.HandFinger.Index), true);
            if (!step.ArmedNow) return;
            // Reuse only the double-pinch decision. No capture countdown or camera request is started.
            gesture.Reset(); ReadyForPinch = false;
            Debug.Log("[Onboarding] Double pinch accepted: enter Create World.");
            EnterRequested?.Invoke();
        }

        static bool Tracked(OVRHand hand) => hand != null && hand.isActiveAndEnabled &&
            hand.IsTracked && hand.IsDataValid && !hand.IsSystemGestureInProgress &&
            !hand.PoseSourceInferred &&
            hand.GetFingerConfidence(OVRHand.HandFinger.Index) == OVRHand.TrackingConfidence.High &&
            hand.GetFingerConfidence(OVRHand.HandFinger.Thumb) == OVRHand.TrackingConfidence.High;

        void OnApplicationPause(bool value) { applicationPaused = value; RefreshPause(); }
        void OnApplicationFocus(bool value) { focused = value; RefreshPause(); }
        void RefreshPause()
        {
            gesture.Reset();
            if (opening != null) opening.SetPaused(paused);
            if (voice != null) { if (paused) voice.Pause(); else voice.UnPause(); }
        }
        void Unavailable(string detail)
        {
            Debug.LogError("[Onboarding] " + detail);
            // Retain the existing menu as recovery if an installed asset is damaged.
            Running = false; Ready?.Invoke();
        }
        public void Cancel()
        {
            StopAllCoroutines(); gesture.Reset(); ReadyForPinch = false; Running = false;
            if (voice != null) voice.Stop();
            if (opening != null) { Destroy(opening.gameObject); opening = null; }
        }
        void OnDisable() => Cancel();
    }
}
