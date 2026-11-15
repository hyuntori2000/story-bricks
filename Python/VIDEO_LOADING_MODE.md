# Video loading demonstration

On the Mac, create `runtime/video-loading-only.flag` and restart the voice/media backend to show the animation loading screen without calling the external video provider. This file is local runtime configuration and is not committed. Normal word recognition, conversation, word images and sounds are unchanged.

The backend acknowledges the saved story with `status: running` and `code: video_loading_only`. Unity shows “Loading your animation... Double pinch to go home.” It does not poll, play the normal two-minute announcement, or download a video. Home remains available. There is intentionally no completion in this mode.

To restore real generation for new stories, remove the flag and restart the backend. Existing demonstration jobs remain loading-only and are never automatically submitted to the provider. Previously finished videos remain available in the story library.
