using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Batch entry: -executeMethod TutorialPlaybackVerification.Run (do not pass -quit).
/// Exercises the real scene and coroutine transitions without Quest hardware.
[InitializeOnLoad]
public static class TutorialPlaybackVerification
{
    const string ActiveKey = "StoryBricks.TutorialVerification.Active";
    static ViewerFlow viewer;
    static int phase;
    static double entered, started;
    static bool openingObserved, capturedBrick;
    static readonly List<string> checks = new List<string>();
    static readonly HashSet<AudioClip> tutorialVoices = new HashSet<AudioClip>();

    [Serializable] class Report
    {
        public bool passed;
        public string mode = "Unity Editor Play Mode, no XR hardware";
        public string note = "Opening and initial narration use supplied audio. Repeat/phase tests shorten timings and omit voice clips in memory only.";
        public string[] checks;
        public string error;
    }

    static TutorialPlaybackVerification()
    {
        EditorApplication.update += Tick;
    }

    public static void Run()
    {
        SessionState.SetBool(ActiveKey, true);
        EditorSceneManager.OpenScene("Assets/StoryBricks/Scenes/TutorialPreview.unity");
        EditorApplication.isPlaying = true;
    }

    static void Next(int value)
    {
        phase = value;
        entered = EditorApplication.timeSinceStartup;
    }

    static bool Visible(CanvasGroup group) => group != null && group.gameObject.activeInHierarchy && group.alpha > 0.2f;

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void CheckCleanMenu()
    {
        Require(!viewer.IsTutorialActive && viewer.state == ViewerFlow.State.Menu, "Skip/completion did not return to menu.");
        Require(Visible(viewer.menu), "Menu is not visible.");
        Require(!Visible(viewer.bricks) && !Visible(viewer.period) && !Visible(viewer.fox) && !Visible(viewer.pinch), "A tutorial visual remained or reappeared after skip.");
        foreach (var source in viewer.GetComponents<AudioSource>())
            Require(!source.isPlaying || !tutorialVoices.Contains(source.clip), "Tutorial narration continued after skip.");
    }

    static void Skip(string stage)
    {
        viewer.HandleDoublePinch();
        Require(!viewer.IsTutorialActive && viewer.state == ViewerFlow.State.Menu, stage + ": double pinch did not consume skip exactly once.");
        checks.Add(stage + ": double pinch returned to menu without starting a story");
    }

    static void Tick()
    {
        if (!SessionState.GetBool(ActiveKey, false) || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
        try
        {
            if (started == 0)
            {
                started = entered = EditorApplication.timeSinceStartup;
                viewer = UnityEngine.Object.FindFirstObjectByType<ViewerFlow>(FindObjectsInactive.Include);
                Require(viewer != null, "Preview scene has no ViewerFlow.");
                if (viewer.systemVoice != null)
                    for (int i = 0; i < viewer.systemVoice.Length; i++)
                        if ((i >= 1 && i <= 7 || i == 21 || i == 22) && viewer.systemVoice[i] != null) tutorialVoices.Add(viewer.systemVoice[i]);
            }
            double now = EditorApplication.timeSinceStartup;
            Require(now - started < 180, "Verification exceeded 180 seconds, phase " + phase);
            Require(now - entered < 50, "Phase timed out: " + phase);
            switch (phase)
            {
                case 0:
                    var opening = UnityEngine.Object.FindFirstObjectByType<OpeningAnimation>(FindObjectsInactive.Include);
                    if (opening != null && opening.gameObject.activeInHierarchy && !viewer.gameObject.activeInHierarchy) openingObserved = true;
                    if (viewer.IsTutorialActive)
                    {
                        Require(openingObserved, "Tutorial started before the opening was observed.");
                        Require(opening == null || !opening.gameObject.activeInHierarchy, "Opening still active when tutorial began.");
                        checks.Add("Opening completed before automatic tutorial start");
                        Next(1);
                    }
                    break;
                case 1:
                    if (now - entered > 0.8)
                    {
                        Skip("Initial narration");
                        Next(2);
                    }
                    break;
                case 2:
                    if (now - entered > 1.2)
                    {
                        CheckCleanMenu();
                        checks.Add("No delayed tutorial voice or visuals after initial skip");
                        // Change only the live instance, never scene assets. Keep animation sequencing intact.
                        viewer.systemVoice = Array.Empty<AudioClip>();
                        viewer.playJudyVoice = false;
                        viewer.brickPause = 0.1f;
                        viewer.bloom = 0.08f;
                        viewer.dry = 0.05f;
                        viewer.hintHold = 0.1f;
                        viewer.OnTutorial();
                        Next(3);
                    }
                    break;
                case 3:
                    if (viewer.IsTutorialActive && Visible(viewer.bricks))
                    {
                        if (!capturedBrick)
                        {
                            ScreenCapture.CaptureScreenshot(Path.GetFullPath(Path.Combine(Application.dataPath, "../../tutorial-preview.png")));
                            capturedBrick = true;
                            entered = now;
                        }
                        else if (now - entered > 0.5)
                        {
                            Skip("Brick reveal");
                            Next(4);
                        }
                    }
                    break;
                case 4:
                    if (now - entered > 1.2) { CheckCleanMenu(); viewer.OnTutorial(); Next(5); }
                    break;
                case 5:
                    if (viewer.IsTutorialActive && Visible(viewer.period)) { Skip("Period explanation"); Next(6); }
                    break;
                case 6:
                    if (now - entered > 1.2) { CheckCleanMenu(); viewer.OnTutorial(); Next(7); }
                    break;
                case 7:
                    if (viewer.IsTutorialActive && Visible(viewer.fox)) { Skip("Fox animation"); Next(8); }
                    break;
                case 8:
                    if (now - entered > 1.2) { CheckCleanMenu(); viewer.OnTutorial(); Next(9); }
                    break;
                case 9:
                    if (viewer.IsTutorialActive) Next(10);
                    break;
                case 10:
                    if (!viewer.IsTutorialActive && viewer.state == ViewerFlow.State.Menu) Next(11);
                    break;
                case 11:
                    if (now - entered > 1.2)
                    {
                        CheckCleanMenu();
                        checks.Add("Replay completes automatically and returns to clean menu");
                        viewer.HandleDoublePinch();
                        Next(12);
                    }
                    break;
                case 12:
                    if (viewer.state == ViewerFlow.State.Building)
                    {
                        Require(!viewer.IsTutorialActive, "Starting story accidentally replayed tutorial.");
                        checks.Add("A new story starts normally after tutorial completion");
                        Finish(true, null);
                    }
                    break;
            }
        }
        catch (Exception e) { Finish(false, e.ToString()); }
    }

    static void Finish(bool passed, string error)
    {
        SessionState.SetBool(ActiveKey, false);
        var report = new Report { passed = passed, checks = checks.ToArray(), error = error };
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tutorial-verification.json"));
        File.WriteAllText(path, JsonUtility.ToJson(report, true));
        Debug.Log("STORY_BRICKS_TUTORIAL_VERIFICATION " + (passed ? "PASS" : "FAIL") + " " + path + (error == null ? "" : "\n" + error));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
