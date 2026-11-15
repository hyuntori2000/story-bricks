using System.Collections;
using UnityEngine;

/// Plays the supplied Judy cut-outs, narration and sound track on one local clock.
/// It has no capture, recognition, microphone, session or backend responsibilities.
[DisallowMultipleComponent]
public sealed class JudyStoryPlayer : MonoBehaviour
{
    ViewerFlow view;
    AudioSource narrationSource, effectsSource;
    Coroutine playback;
    bool paused, applicationPaused, applicationUnfocused, playing;
    float elapsed;

    public bool IsPlaying => playing;
    bool IsPaused => paused || applicationPaused || applicationUnfocused;

    public void Play(ViewerFlow presentation, bool initiallyPaused = false)
    {
        Stop();
        view = presentation;
        if (view == null || view.stage == null || view.narrationClip == null || view.narration == null)
        {
            Debug.LogError("[Judy] Authored stage or narration is missing from the presentation prefab.");
            return;
        }
        narrationSource = Source(narrationSource);
        effectsSource = Source(effectsSource);
        elapsed = 0f;
        paused = initiallyPaused;
        applicationUnfocused = !Application.isFocused;
        playing = true;
        playback = StartCoroutine(PlayCo());
    }

    AudioSource Source(AudioSource source)
    {
        if (source != null) return source;
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
        return source;
    }

    void Update()
    {
        if (!playing || IsPaused) return;
        // Follow the same recording used to author StoryStage's cue times. Continue
        // smoothly if its audio tail ends just before the final authored frame.
        elapsed = narrationSource != null && narrationSource.isPlaying
            ? narrationSource.time : elapsed + Time.unscaledDeltaTime;
    }

    float StoryClock() => elapsed;

    IEnumerator PlayCo()
    {
        while (IsPaused) yield return null;
        narrationSource.clip = view.narrationClip;
        narrationSource.time = 0f;
        narrationSource.Play();
        if (view.storySfxClip != null)
        {
            effectsSource.clip = view.storySfxClip;
            effectsSource.volume = view.storySfxVolume;
            effectsSource.time = 0f;
            effectsSource.Play();
        }
        view.stage.Paused = false;
        view.stage.BeginStory(StoryClock);
        Debug.Log("[Judy] Playing authored cut-out story, duration=" + view.storyEnd);
        for (int line = 0; line < view.narration.Length; line++)
        {
            float start = view.narrationStart != null && line < view.narrationStart.Length
                ? view.narrationStart[line] : line * 5f;
            while (IsPaused || StoryClock() < start) yield return null;
            view.ShowJudyLine(line);
        }
        while (IsPaused || StoryClock() < view.storyEnd) yield return null;
        for (float hold = 0; hold < view.endHold;)
        {
            if (!IsPaused) hold += Time.unscaledDeltaTime;
            yield return null;
        }
        narrationSource.Stop(); effectsSource.Stop();
        playing = false;
        playback = null;
        view.FinishJudy();
        Debug.Log("[Judy] Authored story completed.");
    }

    public void SetPaused(bool value)
    {
        paused = value;
        ApplyPause();
    }

    void ApplyPause()
    {
        if (view != null && view.stage != null) view.stage.Paused = IsPaused;
        if (!playing) return;
        if (IsPaused)
        {
            narrationSource?.Pause(); effectsSource?.Pause();
        }
        else
        {
            narrationSource?.UnPause(); effectsSource?.UnPause();
        }
    }

    public void Stop()
    {
        if (playback != null) StopCoroutine(playback);
        playback = null;
        playing = false;
        elapsed = 0f;
        narrationSource?.Stop(); effectsSource?.Stop();
        if (view != null && view.stage != null)
        {
            view.stage.Paused = false;
            view.stage.ResetStage();
        }
    }

    void OnApplicationPause(bool value) { applicationPaused = value; ApplyPause(); }
    void OnApplicationFocus(bool value) { applicationUnfocused = !value; ApplyPause(); }
    void OnDisable() { Stop(); }
}
