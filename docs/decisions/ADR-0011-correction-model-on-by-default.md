# ADR-0011: The correction model is on by default

Status: accepted (after 0.1.19, unreleased). Supersedes the "opt-in" part of ADR-0007; ADR-0008 (how the model is asked) is unchanged.

## Context
ADR-0007 made the correction model opt-in because its first design changed words wrongly. ADR-0008 redesigned the call (one letter, real probabilities, Canary wins only at 0.9, no rejected answers). On the test song it then changed no word wrongly and took 4 s instead of 15 s, but no measurement showed that it improves a transcript either. The maintainer decided that it should be on by default regardless.

## Decision
- `UseCorrectionModel` defaults to true (new installs, `AppSettings`, the Settings checkbox). Settings files from before this change (`version` 1) hold the old default, not a choice, because the model was never on in them; they move to the new default once when they are read (`AppSettings.CurrentVersion` is 2). A choice saved by this version, on or off, is kept.
- A job whose setting is on while the model is not downloaded behaves as if the setting were off: no alert, disagreements are marked for listening. The readiness card on New transcription says that the model is missing, but it never blocks a transcription.
- First-run setup downloads the correction model with Whisper and Canary while the setting is on (about 3 GB more) and tunes it. `LocalOptimizer` still copes with an uninstalled model (it falls back to CPU for that setting) for people who switch the setting off.
- Jobs keep the choice they started with (`JobConfiguration.UseCorrectionModel`), as before.

## Consequences
Every job with disagreements starts the local model server (a few seconds). Accuracy is unchanged on the available references; the reason to keep scoring pipeline changes against a transcript (CLAUDE.md) stands, and if a reference ever shows the model making a transcript worse, switch the default back here.
