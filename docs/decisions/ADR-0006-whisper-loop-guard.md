# ADR-0006: Remove runaway repetition from Whisper's output

Status: accepted (after 0.1.18)

## Context
Over stretches without speech (game sound, music, the end of a song) Whisper can fall into a loop: each window is conditioned on the previous text, so a repeated phrase feeds itself. Two real cases:
- A 28 minute Hungarian video with game sound: 830 segments in a row were one phrase, every 2.0 s from 35 s to 1,696 s.
- A 4.7 minute song: from 186 s to the end, two lines alternated 38 times each (76 segments).

All of it went to the comparison, the correction model and the exports. The Review page already warned about "a possible recognition loop", but it only flagged: the text stayed.

Things that are not fixes:
- **Running the stretch again.** A fresh start (with or without the previous text) gives different invented text each time. There is no speech there to find.
- **Asking Canary.** Canary has no timestamps, so its leftover words are attached to the looping regions; "Canary heard something there" is not evidence about that stretch.

Correction: an earlier version of this record, and the message of commit `99566e6`, said the older repetition flag "also fires on a sung chorus" because it flagged 68 of the song's 134 regions. That was an unverified guess and it was wrong: those 68 regions were the alternating loop above. The older flag was right about them.

## Decision
`LoopGuard` (pure, in Fusion) removes the repeats and keeps the first copy of each run. The rule is narrow on purpose: at least 8 copies in a row of a pattern of 1 to 4 segments (the same text, or the same few texts in the same order; case and punctuation ignored), every segment starting within 1 s of the end of the one before, covering at least 12 s. A chorus sung 3 or 4 times, a chant with breaths, or alternating lines with pauses are left alone. Measured on every Whisper output available, a run of even three identical segments in a row only ever occurred in the broken runs.

It runs in the Whisper stage, before anything else sees the transcript:
- `Whisper/raw.json` (Whisper's own output) is never touched, so the evidence is complete.
- `Whisper/loops.json` lists the removed runs (period, segments, times).
- `whisper.json`, which the later stages read, has the repeats removed.
- A notice tells the user, says that speech inside the stretch may be missing, and suggests "Skip silence and music" unless it is already on.

## Results (2 Oct 2026, real pipeline, default mode)
- German clip: identical to the build without the guard (0.00% difference, no `loops.json`).
- Hungarian video: 836 regions became 7, disagreements for the correction model fell from 863 to 18, the correction stage took 26 s instead of 271 s (the whole job 179 s instead of 425 s). The transcript no longer contains the invented phrase. It is still short: the loop had displaced the real narration after about 38 s, which only skipping recovers (53 regions).
- Song, scored against the lyrics supplied by the user (290 words; word error rate counts wrong, missing and extra words):

| | WER | wrong | missing | extra |
| --- | ---: | ---: | ---: | ---: |
| Earlier build (1 Oct), final | 173.4% | 27 | 5 | 471 |
| Single-phrase guard only, final | 173.1% | 29 | 3 | 470 |
| Extended guard (patterns up to 4), final | 33.4% | 30 | 4 | 63 |
| Canary alone | 15.5% | 28 | 7 | 10 |
| Skipping on, final | 87.9% | 10 | 245 | 0 |

## Limits and what is next
- The song's final transcript is still worse than Canary alone (33% against 15.5%). 63 words are extra, mostly one 30 s stretch (about 180 to 210 s) where Whisper wrote 1 s segments of 5 to 7 words, a block of about 7 lines repeated 4 times. That is a longer pattern with fewer copies than the rule allows. A speech-rate check (more than about 5 words per second held across a segment run is not singing or speech) is a candidate; it has not been built or measured.
- The final text follows Whisper; the correction model only chooses at disagreements, so Canary's better recall on this song is not used. A different weighting is a separate decision.
- A chant said 8 or more times back to back for 12 s or more, each copy its own segment, would be reduced to one copy. None was found in the recordings measured; the copies stay in `Whisper/raw.json` and `loops.json`.
- It removes the garbage; it cannot recover speech the loop displaced.
