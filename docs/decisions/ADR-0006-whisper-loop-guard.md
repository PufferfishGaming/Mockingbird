# ADR-0006: Remove runaway repetition from Whisper's output

Status: accepted (after 0.1.18)

## Context
Over stretches without speech (game sound, music, silence) Whisper can fall into a loop: each window is conditioned on the previous text, so one phrase feeds itself. In a 28 minute Hungarian video with game sound, 830 segments in a row were "Egy kicsit, hogy mi történik.", every 2.0 s from 35 s to 1,696 s. All of it went to the comparison, the correction model (863 disagreements) and the exports. The Review page already warned about "a possible recognition loop", but it only flagged: the text stayed.

Two things looked like fixes and are not:
- **Running the stretch again.** A fresh start (with or without the previous text) gives different invented text each time ("*személyes csapás*", "Két gírt szerezni...", "miért nem tudom..."). There is no speech there to find.
- **Acting on the existing repetition flag.** It also fires on a sung chorus: 68 of the 134 regions of a test song. Removing flagged text would delete lyrics.
- **Asking Canary.** Canary has no timestamps, so its leftover words are attached to the looping regions; "Canary heard something there" is not evidence about that stretch.

## Decision
`LoopGuard` (pure, in Fusion) removes the repeats and keeps the first copy of each run. The rule is deliberately narrow: at least 8 segments in a row with the same text (case and punctuation ignored), each starting within 1 s of the end of the one before, covering at least 12 s. Measured on every Whisper output available (a song, a German clip, the skipping-mode transcripts, the broken run), the only run of even three identical segments in a row was the broken one.

It runs in the Whisper stage, before anything else sees the transcript:
- `Whisper/raw.json` (Whisper's own output) is never touched, so the evidence is complete.
- `Whisper/loops.json` lists the removed runs.
- `whisper.json`, which the later stages read, has the repeats removed.
- A notice tells the user, says that speech inside the stretch may be missing, and suggests "Skip silence and music" unless it is already on.

## Result (2 Oct 2026, real pipeline)
- German clip and song, default mode: identical to the build without the guard (0.00% difference, no `loops.json`).
- Hungarian video, default mode: 836 regions became 7, disagreements for the correction model fell from 863 to 18, the correction stage took 26 s instead of 271 s (the whole job 179 s instead of 425 s), and the transcript no longer contains the invented phrase. It is still short: the loop had swallowed the real narration after about 38 s, which only skipping recovers (53 regions).

## Limits
- Only a phrase repeated on its own (period 1). Two phrases alternating in a loop are still only flagged by the older repetition warning.
- A chant said eight or more times back to back for 12 s or more, with each copy as its own segment, would be reduced to one copy. None was found in the recordings measured; the copies stay in `Whisper/raw.json` and `loops.json`.
- It removes the garbage; it cannot recover speech the loop displaced.
