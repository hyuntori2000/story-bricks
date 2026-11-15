# Story Bricks scan guide

Open `index.html` in a browser. This standalone English guide works offline, without the app, backend, camera access or API keys. It uses embedded SVG illustrations and includes four steps: align the bricks, pinch twice above the word, move hands away and wait three seconds, then listen and answer.

All copy, artwork and navigation live in `index.html`. Edit the `steps` array for captions and drawings. Buttons work with mouse, touch and keyboard; the mobile layout fits narrow screens and printing includes all four steps. The example question is illustrative. This page never scans or starts a headset session.

To preview over HTTP, run `python3 -m http.server 8791 --directory Dashboard/onboarding` from the repository root and open `http://localhost:8791`. No build or package installation is required. You can also upload this folder to any static host.
