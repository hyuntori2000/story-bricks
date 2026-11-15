using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace BrailleSoundDemo
{
    public sealed class MicrophoneCapture
    {
        public byte[] wav;
        public string error;
        public string code;
        public string endedBy;
        public float seconds;
        public float meanRms, peakRms, detectedSpeechSeconds;
        public bool retryable;
    }

    /// <summary>Turn-based capture: silence endpointing plus a manual finish control. Audio is never monitored to a speaker.</summary>
    public sealed class MicrophoneAnswerInput : MonoBehaviour
    {
        public bool IsRecording { get; private set; }
        public bool IsBusy { get; private set; }
        public string Status { get; private set; } = "Microphone idle";
        public event Action StateChanged;
        public bool SpeechDetected { get; private set; }
        public bool CanFinishAnswer => CanFinishManually(IsRecording, RecordingSeconds, SpeechDetected, MinimumManualSeconds);
        public float RecordingSeconds => IsRecording ? Mathf.Max(0, Time.realtimeSinceStartup - recordingStartedAt) : 0;
        public float SilenceSeconds = 1.2f;
        public float MaximumSeconds = 30;
        public float NoSpeechSeconds = 10;
        public float SpeechRmsThreshold = .012f;
        public float MinimumManualSeconds = 1;
        AudioClip clip;
        bool finishRequested;
        float recordingStartedAt;
        int revision;
        const string Device = null;
        const float StartupTimeoutSeconds = 3;
        const float ReadySignalReserveSeconds = 5;

        public void FinishAnswer() { TryFinishAnswer(); }
        public bool TryFinishAnswer()
        {
            if (!CanFinishAnswer)
            {
                Debug.Log("[MicrophoneCapture] Manual finish ignored: seconds=" + RecordingSeconds.ToString("F2") +
                    " speechDetected=" + SpeechDetected + ". Recording continues.");
                if (IsRecording) SetStatus("Listening. Keep speaking; pause when you are finished.");
                return false;
            }
            finishRequested = true;
            Debug.Log("[MicrophoneCapture] Manual finish accepted after " + RecordingSeconds.ToString("F2") + " seconds.");
            return true;
        }
        /// <summary>
        /// Opens the native device before the optional ready signal. The signal
        /// must include its quiet tail; only samples arriving afterward become
        /// the answer. IsRecording stays false until that answer window opens.
        /// </summary>
        public IEnumerator Capture(Action<MicrophoneCapture> completed, Func<IEnumerator> readySignal = null)
        {
            if (IsBusy) { completed(new MicrophoneCapture { error = "A microphone capture is already in progress." }); yield break; }
            int token = ++revision; IsBusy = true; finishRequested = false; SpeechDetected = false;
            SetStatus("Preparing microphone permission.");
            if (token != revision) yield break;
            bool allowed = false;
            yield return Permission(result => allowed = result, token);
            if (token != revision) yield break;
            if (!allowed) { EndWithError("Microphone permission was not granted. Allow access, then press A to retry.", completed, "permission_denied"); yield break; }
            if (Microphone.devices.Length == 0) { EndWithError("No microphone device is available. Press A to retry after checking the headset.", completed, "device_missing"); yield break; }
            // A non-looping buffer avoids overwriting answer samples. Reserve
            // time for native startup and the local cue before the answer limit.
            try { clip = Microphone.Start(Device, false,
                Mathf.CeilToInt(MaximumSeconds + StartupTimeoutSeconds + ReadySignalReserveSeconds) + 2, 24000); }
            catch (Exception) { clip = null; }
            if (clip == null) { EndWithError("The microphone could not start. Press A to retry.", completed, "start_failed"); yield break; }
            SetStatus("Starting microphone; wait a moment.");
            if (token != revision) yield break;
            float startingAt = Time.realtimeSinceStartup;
            while (token == revision && Microphone.GetPosition(Device) <= 0 &&
                Time.realtimeSinceStartup - startingAt < StartupTimeoutSeconds) yield return null;
            if (token != revision) yield break;
            if (Microphone.GetPosition(Device) <= 0) { EndWithError("The microphone did not deliver audio. Press A to retry.", completed, "startup_timeout"); yield break; }
            if (!Microphone.IsRecording(Device))
            { EndWithError("The microphone stopped during startup. Press A to retry.", completed, "device_stopped"); yield break; }

            int firstBufferPosition = Microphone.GetPosition(Device);
            SetStatus("Microphone buffer ready. Listen for the ready tone before speaking.");
            if (token != revision) yield break;
            Debug.Log("[MicrophoneCapture] id=" + token + " firstBufferReady=true startupSeconds=" +
                (Time.realtimeSinceStartup - startingAt).ToString("F3") + " frame=" + firstBufferPosition +
                "; answer input is still closed.");
            if (readySignal != null)
            {
                IEnumerator signal = null;
                bool signalFailed = false;
                try { signal = readySignal(); }
                catch (Exception) { signalFailed = true; }
                if (token != revision) yield break;
                if (signalFailed)
                { EndWithError("The microphone ready signal could not start. Press A to retry.", completed, "ready_signal_failed"); yield break; }
                if (signal != null) yield return signal;
                if (token != revision) yield break;
            }

            int answerStartFrame = Microphone.GetPosition(Device);
            if (!Microphone.IsRecording(Device) || answerStartFrame < firstBufferPosition)
            { EndWithError("The microphone stopped before your answer began. Press A to retry.", completed, "device_stopped"); yield break; }
            int maximumAnswerFrames = Mathf.CeilToInt(MaximumSeconds * clip.frequency);
            if (answerStartFrame > clip.samples - maximumAnswerFrames)
            { EndWithError("Microphone preparation took too long. Press A to retry.", completed, "startup_timeout"); yield break; }
            int answerEndFrame = answerStartFrame + maximumAnswerFrames;

            IsRecording = true; recordingStartedAt = Time.realtimeSinceStartup;
            float beganAt = recordingStartedAt, lastSpeech = beganAt, speechDuration = 0, peakRms = 0;
            double totalSquared = 0;
            long measuredSamples = 0;
            SetStatus("Listening now. Speak your answer, then pause.");
            if (token != revision) yield break;
            Debug.Log("[MicrophoneCapture] id=" + token + " started utc=" + DateTime.UtcNow.ToString("O") +
                " sampleRate=" + clip.frequency + " channels=" + clip.channels + " silenceSeconds=" + SilenceSeconds.ToString("F1") +
                " noSpeechSeconds=" + NoSpeechSeconds.ToString("F1") + " rmsThreshold=" + SpeechRmsThreshold.ToString("F4") +
                " excludedPrefixFrames=" + answerStartFrame);
            int readFrame = answerStartFrame, lastPosition = answerStartFrame;
            var chunk = new float[1024 * clip.channels];
            bool heardSpeech = false; string endedBy = "manual";
            while (token == revision)
            {
                int position = Microphone.GetPosition(Device);
                if (position < lastPosition || !Microphone.IsRecording(Device)) { endedBy = "device stopped"; break; }
                position = Math.Min(position, answerEndFrame);
                lastPosition = position;
                while (readFrame < position)
                {
                    int frames = Math.Min(1024, position - readFrame);
                    if (!clip.GetData(chunk, readFrame)) { EndWithError("Could not read microphone samples. Press A to retry.", completed, "read_failed"); yield break; }
                    double squared = 0; for (int i = 0; i < frames * clip.channels; i++) squared += chunk[i] * chunk[i];
                    float rms = (float)Math.Sqrt(squared / (frames * clip.channels));
                    totalSquared += squared; measuredSamples += frames * clip.channels;
                    peakRms = Mathf.Max(peakRms, rms);
                    if (rms >= SpeechRmsThreshold)
                    {
                        speechDuration += frames / (float)clip.frequency;
                        lastSpeech = Time.realtimeSinceStartup;
                        heardSpeech = speechDuration >= .12f;
                        if (heardSpeech && !SpeechDetected)
                        {
                            SpeechDetected = true;
                            SetStatus("Hearing your answer. Pause when you are finished.");
                            if (token != revision) yield break;
                        }
                    }
                    readFrame += frames;
                }
                float elapsed = Time.realtimeSinceStartup - beganAt;
                if (finishRequested) { endedBy = "manual"; break; }
                if (heardSpeech && Time.realtimeSinceStartup - lastSpeech >= SilenceSeconds) { endedBy = "silence"; break; }
                if (!heardSpeech && elapsed >= NoSpeechSeconds) { endedBy = "no speech"; break; }
                if (elapsed >= MaximumSeconds || position >= answerEndFrame) { endedBy = "time limit"; break; }
                yield return null;
            }
            if (token != revision) yield break;
            int finalPosition = Math.Min(Math.Max(lastPosition, Microphone.GetPosition(Device)), answerEndFrame);
            int framesRecorded = Math.Max(0, finalPosition - answerStartFrame);
            Microphone.End(Device); IsRecording = false;
            float seconds = framesRecorded / (float)clip.frequency;
            var result = new MicrophoneCapture { endedBy = endedBy, seconds = seconds,
                meanRms = measuredSamples > 0 ? (float)Math.Sqrt(totalSquared / measuredSamples) : 0,
                peakRms = peakRms, detectedSpeechSeconds = speechDuration };
            Debug.Log("[MicrophoneCapture] id=" + token + " endedBy=" + endedBy + " seconds=" + seconds.ToString("F2") +
                " speechSeconds=" + speechDuration.ToString("F2") + " meanRms=" + result.meanRms.ToString("F5") +
                " peakRms=" + peakRms.ToString("F5") + " heardSpeech=" + heardSpeech + " frames=" + framesRecorded);
            result.code = FailureCodeForEnd(endedBy, heardSpeech, framesRecorded);
            if (result.code == "device_stopped")
            {
                result.error = "The microphone stopped unexpectedly. Earlier answers are kept. Press A to retry.";
                Finish(result, completed); yield break;
            }
            if (result.code == "no_speech")
            {
                result.error = "No clear speech was detected. Your answer has not been saved.";
                result.retryable = true;
                Finish(result, completed); yield break;
            }
            try
            {
                var samples = new float[framesRecorded * clip.channels];
                if (!clip.GetData(samples, answerStartFrame)) throw new InvalidOperationException();
                result.wav = WavCodec.Encode(samples, clip.channels, clip.frequency);
            }
            catch (Exception) { result.code = "encoding_failed"; result.error = "Could not encode microphone audio. Press A to retry."; }
            Finish(result, completed);
        }
        IEnumerator Permission(Action<bool> completed, int token)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone)) { completed(true); yield break; }
            bool responded = false, approved = false;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += permission => { responded = true; approved = true; };
            callbacks.PermissionDenied += permission => { responded = true; };
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
            float start = Time.realtimeSinceStartup;
            while (!responded && token == revision && Time.realtimeSinceStartup - start < 60) yield return null;
            if (token == revision) completed(approved);
#else
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (token == revision) completed(Application.HasUserAuthorization(UserAuthorization.Microphone));
#endif
        }
        void EndWithError(string message, Action<MicrophoneCapture> completed, string code = "capture_failed")
        {
            if (clip != null) Microphone.End(Device);
            Debug.Log("[MicrophoneCapture] id=" + revision + " endedBy=" + code + " recordingSeconds=" + RecordingSeconds.ToString("F2"));
            Finish(new MicrophoneCapture { error = message, code = code, endedBy = code, seconds = RecordingSeconds }, completed);
        }
        void Finish(MicrophoneCapture result, Action<MicrophoneCapture> completed)
        {
            ReleaseClip(); IsRecording = false; IsBusy = false;
            SetStatus(result.error ?? "Recording complete. Preparing transcription.");
            completed(result);
        }
        void SetStatus(string value)
        {
            Status = value;
            StateChanged?.Invoke();
        }
        void ReleaseClip() { if (clip != null) Destroy(clip); clip = null; }
        public void Cancel()
        {
            if (IsBusy) Debug.Log("[MicrophoneCapture] id=" + revision + " endedBy=canceled recordingSeconds=" + RecordingSeconds.ToString("F2"));
            revision++;
            if (clip != null) Microphone.End(Device);
            ReleaseClip(); IsRecording = false; IsBusy = false; finishRequested = false; SpeechDetected = false;
            SetStatus("Microphone idle");
        }
        public static bool CanFinishManually(bool recording, float seconds, bool speechDetected, float minimumSeconds = 1)
            => recording && speechDetected && float.IsFinite(seconds) && seconds >= minimumSeconds;
        public static string FailureCodeForEnd(string endedBy, bool heardSpeech, int framesRecorded)
            => endedBy == "device stopped" ? "device_stopped" : !heardSpeech || framesRecorded <= 0 ? "no_speech" : null;
        void OnDisable() { Cancel(); }
        void OnApplicationPause(bool paused) { if (paused) Cancel(); }
    }
}
