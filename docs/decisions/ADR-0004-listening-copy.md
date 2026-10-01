# ADR-0004: Review plays a 48 kHz listening copy

Status: accepted (after 0.1.17)

## Context
The speech engines need a 16 kHz mono WAV, and Review played that same file, which sounds like a telephone call and makes difficult passages harder to judge.

## Decision
Preprocessing also writes `playback.m4a` beside `normalized.wav`: 48 kHz AAC at 192 kbps through FFmpeg's built-in encoder, mono staying mono and wider layouts becoming stereo. It is a lossy convenience copy; lossless FLAC would be about four times larger for hours of audio. It is made in parallel with the normalization, never fails a job, and is optional. Jobs without it keep playing the 16 kHz file. The waveform, seeking and all timing still use the 16 kHz file, since both are decoded from the same source and start at zero.

## Consequences
- About 1.4 MB per minute of extra disk per job.
- The pipeline smoke fails if the copy is missing or Review does not play it, which also proves Windows can decode it.
