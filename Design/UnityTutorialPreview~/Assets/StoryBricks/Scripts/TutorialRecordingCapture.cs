#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// Captures the actual rendered Game view and its listener audio; never fabricates frames.
/// Added only for a requested editor recording, not saved into the scene or player builds.
public class TutorialRecordingCapture : MonoBehaviour
{
    [Serializable] public class Frame { public string file; public double time; public int width, height; }
    [Serializable] public class Report
    {
        public bool completed;
        public string note = "Actual Unity Game-view frames; original voice clips and timings. Replay skip invokes the same HandleDoublePinch route as hardware and P.";
        public string error;
        public int sampleRate, channels;
        public double duration, audioFirstTime = -1;
        public bool audioHasSignal;
        public Frame[] frames;
    }
    readonly Stopwatch clock = new Stopwatch();
    readonly object audioLock = new object();
    readonly List<Frame> frames = new List<Frame>();
    readonly MemoryStream pcm = new MemoryStream();
    readonly Report report = new Report();
    BinaryWriter audioWriter;
    string folder, framesDir;
    ViewerFlow viewer;
    int phase;
    double phaseAt;
    volatile bool recording;
    bool finishing;
    string status = "Opening → full tutorial playback";

    void Awake()
    {
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../work/tutorial-recording"));
        // The destination can be overridden for projects opened from another folder.
        string overridePath = Environment.GetEnvironmentVariable("STORY_BRICKS_RECORDING_DIR");
        if (!string.IsNullOrEmpty(overridePath)) folder = Path.GetFullPath(overridePath);
        framesDir = Path.Combine(folder, "frames");
        Directory.CreateDirectory(framesDir);
        report.sampleRate = AudioSettings.outputSampleRate;
        audioWriter = new BinaryWriter(pcm, Encoding.UTF8, true);
        viewer = FindFirstObjectByType<ViewerFlow>(FindObjectsInactive.Include);
        Application.runInBackground = true;
        clock.Start();
        recording = true;
        WriteProgress();
        StartCoroutine(CaptureFrames());
    }

    IEnumerator CaptureFrames()
    {
        double next = 0;
        while (recording)
        {
            yield return new WaitForEndOfFrame();
            double now = clock.Elapsed.TotalSeconds;
            if (now < next || !recording) continue;
            next = now + 1.0 / 15.0;
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            string file = string.Format("frame-{0:D5}.jpg", frames.Count);
            File.WriteAllBytes(Path.Combine(framesDir, file), texture.EncodeToJPG(90));
            frames.Add(new Frame { file = file, time = now, width = texture.width, height = texture.height });
            Destroy(texture);
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!recording || audioWriter == null) return;
        lock (audioLock)
        {
            if (!recording) return;
            if (report.audioFirstTime < 0) report.audioFirstTime = clock.Elapsed.TotalSeconds;
            report.channels = channels;
            for (int i = 0; i < data.Length; i++)
            {
                float value = data[i];
                if (Math.Abs(value) > 0.0001f) report.audioHasSignal = true;
                value = Math.Max(-1f, Math.Min(1f, value));
                audioWriter.Write((short)(value * 32767));
            }
        }
    }

    static bool Visible(CanvasGroup group) => group != null && group.gameObject.activeInHierarchy && group.alpha > 0.4f;
    void Next(int value, string text)
    {
        phase = value; phaseAt = clock.Elapsed.TotalSeconds; status = text; WriteProgress();
    }
    void Update()
    {
        if (!recording || finishing) return;
        double now = clock.Elapsed.TotalSeconds;
        if (now > 180) { Finish("Recording timed out at phase " + phase); return; }
        if (viewer == null) { Finish("Preview scene has no ViewerFlow."); return; }
        switch (phase)
        {
            case 0:
                if (viewer.IsTutorialActive) Next(1, "Full tutorial • original narration and timing");
                break;
            case 1:
                if (!viewer.IsTutorialActive && viewer.state == ViewerFlow.State.Menu) Next(2, "Tutorial complete • returned to menu");
                break;
            case 2:
                if (now - phaseAt > 2) { viewer.OnTutorial(); Next(3, "Replaying tutorial • preparing skip test"); }
                break;
            case 3:
                if (viewer.IsTutorialActive && Visible(viewer.bricks)) Next(4, "Simulating double pinch to skip…");
                break;
            case 4:
                if (now - phaseAt > 1)
                {
                    viewer.HandleDoublePinch();
                    if (viewer.IsTutorialActive || viewer.state != ViewerFlow.State.Menu) { Finish("Double pinch failed to skip."); return; }
                    Next(5, "Double pinch → tutorial skipped • menu restored");
                }
                break;
            case 5:
                if (now - phaseAt > 3) Finish(null);
                break;
        }
    }

    void OnGUI()
    {
        if (!recording) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(0.32f, 0.27f, 0.22f);
        GUI.Label(new Rect(20, 6, Screen.width - 40, 30), "UNITY TEST RECORDING  |  " + status, style);
    }

    void WriteProgress()
    {
        File.WriteAllText(Path.Combine(folder, "progress.txt"), status);
    }

    void Finish(string error)
    {
        if (finishing) return;
        finishing = true; recording = false;
        if (error == null && frames.Count < 30) error = "Too few actual rendered frames were captured.";
        report.completed = error == null;
        report.error = error;
        report.duration = clock.Elapsed.TotalSeconds;
        report.frames = frames.ToArray();
        lock (audioLock)
        {
            audioWriter.Flush();
            if (pcm.Length > 0 && report.channels > 0)
            {
                using (var stream = File.Create(Path.Combine(folder, "audio.wav")))
                using (var writer = new BinaryWriter(stream))
                {
                    int length = checked((int)pcm.Length);
                    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(length + 36);
                    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                    writer.Write((short)1); writer.Write((short)report.channels);
                    writer.Write(report.sampleRate); writer.Write(report.sampleRate * report.channels * 2);
                    writer.Write((short)(report.channels * 2)); writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(length);
                    pcm.Position = 0; pcm.CopyTo(stream);
                }
            }
        }
        File.WriteAllText(Path.Combine(folder, "capture.json"), JsonUtility.ToJson(report, true));
        WriteProgress();
        Debug.Log("Tutorial recording " + (error == null ? "complete" : "failed: " + error) + ": " + folder + ", frames=" + frames.Count + ", audio=" + report.audioHasSignal);
        EditorApplication.delayCall += () => EditorApplication.isPlaying = false;
    }
    void OnDestroy()
    {
        if (!finishing) Finish("Recording interrupted before test completion.");
        audioWriter?.Dispose(); pcm.Dispose();
    }
}
#endif
