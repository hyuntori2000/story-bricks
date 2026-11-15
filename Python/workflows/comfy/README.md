# Comfy word cards

`card_api.json` is the unchanged card workflow supplied in
`/Users/jihyunyu/Downloads/storybricks-comfy/workflows/card_api.json`.
The adapter copies the supplied `CARD_STYLE` and changes only prompt/seed at node
104 and the output prefix at node 106. It keeps `gemini-3-pro-image-preview`,
`birefnet.safetensors`, all node IDs, and the transparent-alpha chain. It requests
only the final `SaveImage` node 106 PNG. No film workflow or bundled-card upload is
included. Abstract words such as WIND still request a visual representation.

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
