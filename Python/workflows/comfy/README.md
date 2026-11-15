# Comfy word cards and story films

`card_api.json` is the unchanged card workflow supplied in
`/Users/jihyunyu/Downloads/storybricks-comfy/workflows/card_api.json`.
The adapter copies the supplied `CARD_STYLE` and changes only prompt/seed at node
104 and the output prefix at node 106. It keeps `gemini-3-pro-image-preview`,
`birefnet.safetensors`, all node IDs, and the transparent-alpha chain. It requests
only the final `SaveImage` node 106 PNG. Abstract words such as WIND still request
a visual representation.

The Python backend has its own two-worker, bounded queue. Requests are scoped by
workspace, experience session, conversation session, request ID and captured group.
Request aliases and group records prevent duplicate paid submits. Each session's
`images/` directory holds the original input word, status, prompt ID and PNG. Keys,
provider response bodies, workflow `extra_data`, and signed URLs are never stored.
A restart only resumes jobs that already have a persisted prompt ID. A pending job
without one becomes interrupted because its submit outcome may be unknown.

Credentialed requests go only to `https://cloud.comfy.org/api/`. Signed downloads
carry no key, use HTTPS and public IPs pinned for the TLS connection, follow at most
three audited redirects, and stop at 8 MiB. PNG framing, CRCs and dimensions
(1–4096 pixels per side) are checked before saving/serving.

This intentionally follows the supplied Comfy v1 client endpoints. The current
[official overview](https://docs.comfy.org/development/cloud/overview) marks v1 as
deprecated but still available. The [API reference](https://docs.comfy.org/development/cloud/api-reference)
documents `X-API-Key`, partner-node `extra_data.api_key_comfy_org`, polling and the
credential-free `/api/view` signed download. Job output reads prefer `/api/jobs/{id}`,
then fall back to the supplied history endpoints. API compatibility can change;
there is no silent model or provider fallback.

Offline checks:

```sh
PYTHONPATH=Python Python/.venv/bin/python -m unittest test_comfy_word_images
```

## Story films

`shot_api.json` is the unchanged supplied one-shot workflow. Node 116 is
`ByteDance2ReferenceNodeV2` using **Seedance 2.5 Draft**, 480p, 16:9, generated audio
and MP4 output; node 117 is `SaveVideo`. The film adapter changes only the scene
prompt, duration, seed, output prefix and up to five `LoadImage` reference inputs.
It reads only node 117's final MP4 from its `images` output list.

The Unity client starts the film after the story narration and animation invitation
finish. The Mac validates that the requested text/audio match a successful owned
story archive. Its saved seven sentences become exactly two chronological scenes,
grouped 4/3, with each shot lasting 6–10 seconds. The full story supplies continuity;
each shot is restricted to its own passage. There is no additional story-writing
API call or replacement plot.

References come from that story's captured groups and conversation session. The
adapter waits up to 120 seconds for pending cards, uploads the available saved
PNGs, and never regenerates them. The reference-video node requires at least one
usable card, so an experience with no ready cards fails before paid video submits.
Both shots are submitted once before waiting and polled concurrently. Actual render
concurrency depends on the Comfy account and service queue. Existing saved plans
resume unchanged, including older four-shot jobs; they are never resubmitted just
to change the scene count. One film worker and two active film slots stay independent
of voice and word images.

Each experience stores `videos/<job-id>/job.json`, the shot MP4s and `story.mp4`.
Request aliases deduplicate the same archived story. A durable marker precedes
each paid submit and its returned prompt ID is saved immediately. Restarts poll
known IDs; an unknown submission outcome fails instead of silently spending again.
Generated media, job records, API responses, and keys are not committed.

Downloads accept only the final bounded MP4, use credential-free public HTTPS
redirects and stop at 64 MiB per shot. Local ffmpeg normalizes and joins the real
shots into H.264/yuv420p with AAC and MP4 `faststart`, capped at 128 MiB. Install
ffmpeg on PATH or `imageio-ffmpeg`; missing ffmpeg is detected before paid calls.
The final file is saved atomically. On macOS, QuickTime Player is opened once for
the saved movie; a preview-launch failure does not invalidate a ready film.

Comfy generation latency and provider concurrency vary. Two minutes is a target,
not a playback timer: VR receives the completed saved movie only when it is ready.
The supplied package's credit/time estimates are not a fixed service guarantee.

Focused offline film checks (fake provider, temporary media, no paid requests):

```sh
PYTHONPATH=Python Python/.venv/bin/python -m unittest test_comfy_story_videos
```

## Full-story narration in the saved film

The final encoder uses the existing session-owned story WAV; it makes no new speech or video API request. Narration starts with the movie and plays once. Film ambience is mixed at 20% beneath it. Output duration is the longer of the complete film and narration: a short film holds its last frame, while short narration leaves the rest of the film intact. The narrated MP4 is saved atomically and used for both Quest and Mac playback. Missing narration fails before paid shot submission. Library selection refreshes the downloaded file so an archived film repaired on the Mac does not reuse a stale unnarrated Quest cache. Replay within that selection uses the downloaded copy.
