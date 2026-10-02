# ADR-0005: One chunk plan per job, built from a speech detector

Status: accepted. The plan is built for every job. Engines use it only when the job skips silence and music (Settings, off by default).

## Context
Whisper runs over the whole file, so silence, game sound and music reach it and it invents text there. Canary runs in windows of up to 20 s, cutting at the quietest point of the last quarter of each window; that is better than a blind cut but is not tied to real pauses, gives no timing, and cannot resume mid-file. Every job also has a single language.

## Decision
Preprocessing finds the speech with the Silero detector that ships with whisper.cpp (`whisper-vad-speech-segments.exe` with `ggml-silero-v5.1.2.bin`), builds a **chunk plan**, and saves it as `chunks.json` in the job folder. `ChunkPlanner` (pure code in Application) turns speech spans into chunks that tile the whole recording:

- A silence of 300 ms or more is a good place to cut; the plan is sized for Canary (`ChunkOptions.ForCanary`: at most 20 s of speech per chunk, hard cap 24 s including padding). Chunks are filled up to that target at the last good silence.
- A pause of 100 to 299 ms is used only if no good silence fits.
- A silence of 2 s or more is never packed into a chunk. It becomes a non-speech chunk of its own, and speech chunks keep 200 ms of padding.
- Only continuous speech with no pause for more than the cap is cut mid-speech, and then the next chunk starts exactly 1 s earlier (`OverlapsPrevious`); the repeated words are removed from the joined text (`OverlapText`).
- The plan is deterministic and has to satisfy these rules for any input; a property test checks 400 random recordings.

**Setting "Skip silence and music" (off by default).** The job keeps the choice it started with (`JobConfiguration.SkipNonSpeech`).
- On: Whisper skips non-speech itself (`--vad`, same thresholds as the plan, timestamps stay on the recording's timeline); Canary reads only the plan's speech chunks and saves every finished chunk (`Canary/windows`), so a restart continues; language is sampled on speech.
- Off: both engines behave exactly as before this decision, so results do not change. With a plan, Canary was also tried on every chunk including non-speech ones; on a song it returned 245 words instead of 300 (-18%), so that variant was rejected.

The detector and its model are bundled (0.9 MB, MIT; THIRD_PARTY_NOTICES.md). The detector tool is excluded from the runtime fingerprint, so adding it does not invalidate a user's tuned speed settings. A missing detector, or a skipping job without a plan, falls back to reading everything and says so (`skip-unavailable.json` plus a notice).

## Measurements (2 Oct 2026, same code, real pipeline, real recordings)
Default mode against the previous behaviour: final text identical (0.0% word difference) for all three recordings, and Whisper and Canary texts identical too.

| Recording | Skipping on, compared with the previous output |
| --- | --- |
| German speech, 57 s | Only the invented last line "Untertitelung des ZDF, 2020" is gone (-5 words). Sentences identical, finer timestamps (first sentence at 4.5 s, not 0 s), Whisper and Canary disagree 1.4% instead of 4.7%. |
| Song, English, 4.7 min | Almost all lyrics removed: Whisper 45 of 772 words. Singing counts as non-speech. |
| Hungarian video with game sound, 28 min | 53 regions instead of 836. 764 of the 836 old regions were the same invented phrase ("Egy kicsit, hogy mi történik.") repeated all over the recording; the detector found 2.2 min of speech in 28 min. What remains is the real narration. About 7 old regions near 1,500 to 1,660 s (a few quiet fragments) may have been real speech and are gone. |

There are no human-checked transcripts, so "accuracy" above means agreement with the previous output plus a reading of what differs.

## Known limits
- Skipping removes songs almost completely; Settings says so. The detector can also miss quiet speech.
- With skipping, Whisper may report one region stretching across a skipped gap (a 3-word region from 48 s to 118 s), so timestamps are coarse there.
- The detector program exits with 0 even when it rejects an option, prints times in hundredths of a second, and its built-in defaults differ from its help text. Every threshold is passed with the long option names, and one test runs the real tool.

## Next steps (not decided here)
1. Per-chunk language (needs language detection without reloading the model for every chunk).
2. Measure Canary with 30 s chunks against the 20 s it uses now.
3. A cheaper way to drop only true silence (not music) by default, and a lower detector threshold for speech over loud sound.
4. (Done: ADR-0006.) Whisper repetition loops ("Egy kicsit, hogy mi történik" hundreds of times) also happen without skipping; the loop guard removes them in every mode.
