using System;
using BrailleSoundDemo.Presentation;
using BrailleSoundDemo.TableDepthTest;

namespace BrailleSoundDemo.Tests
{
    /// <summary>Offline pointer ownership regression; no XR, microphone or API calls.</summary>
    public static class SessionActionInputVerification
    {
        public static void Run()
        {
            var capture = new DoublePinchTimerPolicy();
            capture.Observe(1, true, true, true, false, false, false, true);
            capture.Observe(1.1, true, true, true, true, false, false, true);
            capture.Observe(1.2, true, true, true, false, false, false, true);
            capture.Observe(1.3, true, true, true, true, false, false, true);
            Require(capture.IsArmed, "Fixture must have an armed capture.");
            var pointer = new SessionFinishMenu.ActionPointer();
            Require(!pointer.Observe(2, true, false, 0, true) && !pointer.BlocksCapture(2),
                "Hover must not own capture input.");
            var photo = capture.Observe(4.31, true, !pointer.BlocksCapture(4.31f), true, false, false, false, true);
            Require(photo.CaptureNow, "An already armed capture survives button hover.");

            Require(pointer.Observe(5, true, false, .8f, true) && pointer.BlocksCapture(5),
                "Confident pinch strength clicks even before the native pinch bit arrives.");
            Require(!pointer.Observe(5.05f, true, false, .55f, false) && pointer.BlocksCapture(5.05f),
                "Pointer drift and intermediate strength keep ownership without repeating the click.");
            Require(!pointer.Observe(5.1f, true, true, 1, true), "The later native pinch bit cannot dispatch again.");
            capture.Observe(5.1, true, !pointer.BlocksCapture(5.1f), true, true, false, false, true);
            Require(!capture.IsArmed, "A UI press cannot arm a capture.");

            var other = new SessionFinishMenu.ActionPointer();
            other.Observe(5, true, false, 0, false);
            other.Observe(5.1f, true, true, 1, false);
            pointer.Observe(5.2f, true, false, 0, false);
            Require(pointer.BlocksCapture(5.2f), "The release frame stays protected.");
            Require(!pointer.BlocksCapture(5.41f) && !other.BlocksCapture(5.41f),
                "An unrelated held pinch cannot keep capture blocked after the UI hand releases.");
            Require(!pointer.Observe(6, false, true, 1, true) &&
                !pointer.Observe(6.1f, true, true, 1, true),
                "Lost tracking and a still-held pinch on reacquisition cannot click.");
            UnityEngine.Debug.Log("SESSION_ACTION_INPUT_VERIFICATION_OK");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
