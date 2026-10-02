# ADR-0009: One-click first-run setup

Status: accepted (after 0.1.19, unreleased).

## Context
A fresh install needed three manual steps on two pages (check the computer, download the recommended models, choose a recording and tune), and tuning needed a recording of the user's own. Features.md asks for a one-click setup that downloads the recommended models and tunes on a test audio.

## Decision
- **A banner, not a wizard.** On a fresh install, and while a speech model is missing or speed was never tuned, a banner on every page offers "Set up now" and "Not now". The answer is saved (`setupState` in settings.json: `pending`, `done`, `skipped`); once `done` or `skipped` it never comes back by itself. "Set up automatically" on the Models page runs it again at any time. Existing installs that already have the models and a tuning result are not offered it.
- **Four steps, one button:** check the computer, download Whisper and Canary of the recommended selection, make a test recording, tune and apply the best settings. Cancel pauses it; a partial download resumes on "Try again". A failed tuning does not fail setup: the models are what matter, safe defaults are used and Benchmark can be run later.
- **The correction model is not downloaded.** It is optional and off by default (ADR-0007, ADR-0008). `LocalOptimizer` therefore verifies and measures it only when it is installed, and falls back to CPU for its (unused) execution setting. Installing it later changes the configuration fingerprint, so Benchmark then asks for a new tuning run.
- **The test recording comes from Windows' own voice** (System.Speech through Windows PowerShell, passed as an encoded command so script policies do not apply), 16 kHz mono, about 12 s, in the voice's language if it is one of English, Hungarian, German, Spanish or French. Nothing is bundled or downloaded and no package is added. Only speed is measured, so synthetic speech is enough. Without such a voice, tuning is skipped and the banner says so.
- Setup blocks the other long operations while it runs (`SetupBusy`), and Windows stays awake.

## Verification
`scripts/first-run-smoke.ps1` runs the real flow against the machine's models in a fresh data folder (Vulkan tuning, settings applied, state saved). `scripts/first-run-download-smoke.ps1` points `TRIASR_MODEL_ROOT` at an empty folder, starts the real download, checks that the banner follows it, cancels, and checks the partial file is kept. Unit tests cover the offer rules, the saved state, the progress arithmetic and the test-speech script; a live test makes a real recording.
