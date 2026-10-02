# ADR-0006: Remove runaway repetition from Whisper's output (loop guard and speech-rate guard)

Status: accepted (after 0.1.18). Since ADR-0007 Whisper decodes without the previous text, which prevents these loops at the source; the guards below did not fire on any of the three test recordings in that configuration and stay as a safety net.

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

`RateGuard` (also pure, in Fusion) handles what the pattern rule cannot: at the end of a song Whisper lost its timestamps and wrote lines of five to seven words into segments of exactly one second, a block of about seven lines repeated four times. Nobody sings five words in one second. A cluster qualifies only when it has at least 8 segments of 5 or more words at 4.5 words per second or faster, each starting within 6 s of the end of the previous one, and at least 75% of them share one length (to a tenth of a second); in a qualifying cluster each distinct line is kept once and its exact repeats are removed. Lines that occur once are kept, because the words may be genuine even when their timestamps are not. Measured first on the song, offline: keeping each line once gave 18.3% word error rate, dropping the whole cluster gave 28.6% (it throws away real lyrics with the repeats). On every other Whisper output available (a German clip, skipping-mode transcripts, the Hungarian video) the fastest ordinary segment was 3.9 words per second and no cluster qualified.

Both guards run in the Whisper stage, before anything else sees the transcript:
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
| With the speech-rate guard, Whisper alone | 18.3% | 20 | 33 | 0 |
| **With the speech-rate guard, final** | **14.8%** | 25 | 17 | 1 |
| Canary alone | 15.5% | 28 | 7 | 10 |
| Skipping on, final | 87.9% | 10 | 245 | 0 |

## Limits and what is next
- The rate guard removes exact repeats inside a collapsed cluster, so a line sung twice in that stretch appears once (17 words are missing from the song, against 5 before). That is the price of removing 62 extra words; the song's own repeats elsewhere are untouched.
- The final text follows Whisper; the correction model only chooses at disagreements. On the song the final transcript (14.8%) is now better than either engine alone (18.3% and 15.5%).
- Only one reference transcript exists (this song). The guards were chosen on it and checked for harm on the other recordings by agreement, not by a second reference; a clip of speech with a correct transcript would be a better test.
- A chant said 8 or more times back to back for 12 s or more, each copy its own segment, would be reduced to one copy. None was found in the recordings measured; the copies stay in `Whisper/raw.json` and `loops.json`.
- It removes the garbage; it cannot recover speech the loop displaced.
