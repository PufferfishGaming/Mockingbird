# ADR-0008: The correction model answers with one letter and real probabilities

Status: accepted (after 0.1.18). The correction model stays opt-in and off by default (ADR-0007).

## Context
ADR-0007 made the correction model opt-in because on a test song it changed three words and all three were mistakes, and a third of its answers were rejected. This record is the work to make it reliable and harmless, measured on a bench instead of by eye.

**The bench.** 75 real disagreements between Whisper and Canary, collected from the test song's pipeline runs, each labelled against the song's known transcript: Whisper's wording is right in 57, Canary's in 7, and 11 are ties (both right or both wrong). Only cases where both engines wrote words and each side is at most 6 words are included (an empty side can never be decided from text alone). Always keeping Whisper's wording scores a net 0; the best possible arbiter scores +7; an arbiter that changes a right word to a wrong one scores -1 for each.

## Findings about the old design
- **56% of the answers were rejected** (42 of 75), and every rejection had the same cause: the model was asked to put the chosen candidate in a `text` field, and it wrote the whole surrounding sentence there, so the "must be preserved exactly" check failed. The JSON itself was always valid.
- **It had no skill.** Of the 33 answers that were accepted, when Whisper was right the model chose Canary 9 times and Whisper 11 times. Of the 10 words it changed, 9 made the transcript worse (net -8; in the app's own runs 3 of 3).
- **Its confidence meant nothing.** Self-reported confidence was 0.85 on every wrong change.
- A bigger quantisation (Q8_0 instead of Q6_K) made no difference.

## Decision
`LlamaArbiter` asks one question: "which option was spoken: A, B or U (cannot tell)?", and the model answers with one token. The code reads the probabilities of the three possible tokens from the server (`logprobs`), asks twice with the options swapped, and averages (this cancels a leaning towards the first option). The code, not the model, supplies the resulting text, so the result is always exactly one of the two candidates and nothing can be rejected or invented.

- Canary's wording replaces Whisper's only if its averaged probability is at least 0.9.
- Whisper's wording counts as resolved if its probability is at least 0.75; otherwise it is kept and marked as needing a listen.
- A disagreement where either engine wrote nothing is never decided from text and the model is not called.
- `ArbitrationValidation` (the exact-copy check) is gone; `ArbitrationScoring` (pure code) holds the rule and is unit-tested. One test starts the real model on CPU and asserts that candidates with commas and capitals are answered without a rejection.

## Results
On the bench, taking Canary's wording only above a probability threshold (best possible net +7):

| Threshold | changes | fixes | harms | net |
| --- | ---: | ---: | ---: | ---: |
| 0.5 | 8 | 1 | 6 | -5 |
| 0.8 | 2 | 0 | 1 | -1 |
| **0.9 (used)** | **0** | 0 | 0 | **0** |

End to end on the test song with the model switched on: word error rate 5.2%, the same as with the model off (the old design gave 6.6%); 0 answers rejected (the old design 11 of 27); 0 words changed; the correction stage took 4 s instead of 15 s (on the 28 minute video the old design took 271 s). The German clip is identical with the model on or off.

## Why there is no accuracy gain, and what that means
On this song, the candidates where the engines differ are almost always both plausible words, and the audio is what decides; the 7 cases where Canary was right are mostly places where Whisper missed words. A model that sees only text cannot recover those. So the redesign makes the feature reliable, fast and harmless, but it is not shown to improve a transcript, and it stays off by default. A text model should help where one candidate is not a word (spelling and morphology errors, perhaps in Hungarian); no recording with such a case and a correct transcript exists yet.

Retraction: an earlier message claimed that the German test clip is such a case ("Schulternhalle" against "Schulturnhalle"). It is not: both engines write "Schulternhalle" there. That pair is only the fixed example used by the app's own benchmark and smoke tests.

## Next steps (not decided here)
- A recording with a correct transcript where one engine writes non-words, to find out whether the model earns its place.
- Scoring the candidates against the audio instead of reading text (for example by teacher-forcing Whisper on each candidate), which is where the information actually is.
