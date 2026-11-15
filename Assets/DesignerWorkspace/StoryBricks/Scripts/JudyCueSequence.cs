using System;
using UnityEngine;
using UnityEngine.Video;

namespace BrailleSoundDemo.Presentation
{
    [CreateAssetMenu(menuName = "Story Bricks/Judy cue sequence", fileName = "JudySequence")]
    public sealed class JudyCueSequence : ScriptableObject
    {
        [Tooltip("Words are matched in this order, after a real accepted capture.")]
        public JudyWordCue[] words = Array.Empty<JudyWordCue>();
        [TextArea(4, 12)] public string finalStoryText;
        [Tooltip("Played only after Create Story, after any already accepted word cues finish.")]
        public JudyMediaStep[] finalStory = Array.Empty<JudyMediaStep>();
    }

    [Serializable] public sealed class JudyWordCue
    {
        public string id, expectedWord;
        public JudyMediaStep[] steps = Array.Empty<JudyMediaStep>();
    }

    [Serializable] public sealed class JudyMediaStep
    {
        public string id;
        [TextArea(2, 5)] public string caption;
        [Tooltip("Optional presentation-only prefab, placed above this word's box. No XR rigs, cameras or listeners.")]
        public GameObject prefab;
        public Texture2D image;
        public AudioClip voice, sound;
        public VideoClip video;
        [Tooltip("Wait for JudyCueSignal.Complete() on the instantiated prefab; use an animation event or UnityEvent.")]
        public bool waitForPrefabSignal;
        [Tooltip("Caption-only preview hold. Real audio/video and prefab completion signals control their own duration.")]
        [Min(0)] public float captionPreviewSeconds = 2;
        public Vector3 offset = new Vector3(0, .12f, 0);
        public Vector2 imageSizeMeters = new Vector2(.24f, .16f);
        [Range(0, 1)] public float volume = .8f;
    }
}
