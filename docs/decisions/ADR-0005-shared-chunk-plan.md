# ADR-0005: One chunk plan per job, built from a speech detector

Status: accepted for the plan; engines do not use it yet (step 1 of Tier B)

## Context
Whisper runs over the whole file, so silence and music reach it and it invents text there (a 57 s test recording ended with "Untertitelung des ZDF, 2020" in the quiet tail). Canary runs in windows of up to 20 s, cutting at the quietest point of the last quarter of each window; that is better than a blind cut but is not tied to real pauses, gives no timing, and cannot resume mid-file. Every job also has a single language.

## Decision
Preprocessing finds the speech with the Silero detector that ships with whisper.cpp (`whisper-vad-speech-segments.exe` with `ggml-silero-v5.1.2.bin`), builds a **chunk plan**, and saves it as `chunks.json` in the job folder. `ChunkPlanner` (pure code in Application) turns speech spans into chunks that tile the whole recording:

- A silence of 300 ms or more is a good place to cut; a speech chunk holds at most 30 s of speech (hard cap 35 s including padding). Chunks are filled up to that target at the last good silence.
- A pause of 100 to 299 ms is used only if no good silence fits.
- A silence of 2 s or more is never packed into a chunk. It becomes a non-speech chunk of its own, and speech chunks keep 200 ms of padding. Non-speech chunks are kept so the timeline is complete, and are meant not to be sent to the engines.
- Only continuous speech with no pause for more than the cap is cut mid-speech, and then the next chunk starts exactly 1 s earlier (`OverlapsPrevious`).
- The plan is deterministic and has to satisfy the rules above for any input; a property test checks 400 random recordings.

The detector and its model are bundled (0.9 MB, MIT; listed in THIRD_PARTY_NOTICES.md) instead of downloaded. They are excluded from the runtime fingerprint: each job saves its own plan, so a different detector build cannot make a running job inconsistent, and adding it must not invalidate a user's tuned speed settings.

## Status of this step
Nothing reads the plan yet. A missing detector or a failed run only writes `chunks.skipped.json` or `chunks.failed.json` and never stops a job. Results are unchanged.

## Next steps (not decided here)
1. Canary reads the plan (per chunk, with real chunk times, checkpoint and resume per chunk).
2. Whisper reads the plan. Running `whisper-cli` once per chunk reloads the model each time; the alternative is its built-in `--vad`. To be measured.
3. Per-chunk language (needs a way to detect language without reloading the model for every chunk).
4. A setting for skipping non-speech chunks. Silero marks most of a song as non-speech, so singing must not be dropped silently.
