# ADR-0007: Decode Whisper without previous text, drop unsupported text, make the correction model opt-in

Status: accepted (after 0.1.18)

## Context
An accuracy audit with the one recording that has a correct transcript: a 4.7 minute song, scored against its lyrics (290 words) supplied by the user. Word error rate (WER) counts wrong, missing and extra words. The other two test recordings, a 57 s German clip and a 28 minute Hungarian game video, have no reference; for them only agreement with other output can be measured.

At the start of this work the song's final transcript scored 173.4% (471 extra words). The loop guards of ADR-0006 brought it to 14.8%. This record covers what removed the rest.

## Findings and decisions

### 1. Whisper decodes without the previous text (`-mc 0`)
Whisper normally feeds the previous text back in as context, which is what lets one repeated phrase feed itself. Raw Whisper output on the song, no guards, scored against the lyrics:

| Setting | WER | Time |
| --- | ---: | ---: |
| default | 173.4% | 18.2 s |
| **no previous text (`-mc 0`)** | **5.2%** | **7.1 s** |
| beam search 8 | 30.7% | 17.1 s |
| no temperature fallback | 104.1% | 11.1 s |
| no previous text and beam 8 | 5.5% | 8.1 s |

On speech nothing is lost: the German clip comes out word for word the same, only segmented by sentence; on the Hungarian video the loop of one phrase (836 segments, 7 distinct texts) becomes 110 diverse segments, and in the first minute, which has real narration, agreement with the skipping-mode text improves from 105.7% to 15.7%. Long, clean speech has not been measured with no previous text (names and wording may carry less from window to window).

### 2. Text only Whisper wrote, where no speech was detected, is left out
Without the loop, game sound and silence still make Whisper write invented text (the German clip's last 1.5 s, hundreds of words on the Hungarian video). `UnsupportedTextGuard` leaves a segment out of the comparison when it has 3 or more words, lies wholly inside a stretch where the speech detector found no speech, and either fewer than half of its words, or fewer than 30% of its pairs of neighbouring words, occur in Canary's text. Both signals must agree: singing counts as non-speech for the detector, and Canary can miss a word. The pair test matters for long texts, where single common words occur by chance. The song's weakest genuine line still has half of its pairs in Canary's text; nothing in the song is touched. Removed segments are listed in `Whisper/unsupported.json`; `whisper.json` is not changed.

### 3. The correction model is opt-in (off by default)
With no previous text, Whisper alone scores 5.2% on the song. The final transcript with the correction model on scored 6.6%: the model made exactly three changes and all three were mistakes (15 errors became 19), each at 0.85 confidence; 11 of its 27 answers were rejected after the retry (41%); on the Hungarian video 6 of 18. Anywhere a reference exists it fixed no error. With the setting off, no model is started, Whisper's words stay, and every disagreement is marked as needing a listen. The subsystem is not deleted: that touches packaging, downloads, tuning and several pages, and rests on one reference, so it stays behind the setting until more references exist or the owner decides to remove it.

## Results (2 Oct 2026, real pipeline, default mode)

| Song against the lyrics | WER | wrong | missing | extra |
| --- | ---: | ---: | ---: | ---: |
| Start of this work | 173.4% | 27 | 5 | 471 |
| Loop guards | 14.8% | 25 | 17 | 1 |
| No previous text, correction model on | 6.6% | 10 | 7 | 2 |
| **Final configuration** | **5.2%** | 6 | 7 | 2 |
| Canary alone (for comparison) | 15.5% | 28 | 7 | 10 |

- German clip: the speech is identical to before; the invented last line is gone; 13 regions, all engines agreeing.
- Hungarian video, against the skipping-mode narration as a pseudo-reference (285 words, not truth): 77.2%, the same as the previous build's 76.8%, but missing words fell from 207 to 9 (97% of the narration is present, the old figure came from outputting almost nothing), regions rose from 7 to 60, and the whole job took 101 s instead of 179 s. 159 invented words remain in stretches without detected speech; the 87 disagreements are marked for listening.

## Limits
- One reference transcript. The settings were chosen on it and checked for harm on the others by agreement only. A correct transcript of a few minutes of speech would be the best next test, in particular for long clean speech with no previous text.
- Game sound still produces invented words; "Skip silence and music" (off by default) is the right tool for such recordings and recovers the narration cleanly, but removes songs.
- The loop guards stay as a safety net; none of them fired on any of the three recordings in the final configuration.
