# ADR-0003: Keep the computer usable while a job runs

Status: accepted (after 0.1.17)

## Context
Auto-tuning picked whatever was fastest, including every logical core, and engines ran at normal priority. A long job could make the computer feel frozen, and a laptop could go to sleep mid-job.

## Decision
- **Priority:** engines, FFmpeg and the LLM server start through the Job Object with a below-normal priority class that the whole process tree inherits. The interactive terminal does not use it, so commands the user types keep normal priority.
- **Thread budget (resource profile):** Quiet is half the physical cores, Default leaves four free, Max leaves two free (always at least one). Auto means Quiet on battery and Default otherwise. No profile uses every core. Saved thread counts are clamped when a job starts, and tuning never tests more threads than Default allows.
- **FFmpeg** is limited to two threads wherever it is started.
- **Sleep:** a process-wide Windows power request (system required, display may still sleep) is held during transcription, tuning and downloads.
- **No CPU-rate hard caps** on ggml workers: their spin barrier degrades by orders of magnitude when threads are descheduled. Thread counts are limited instead.

## Consequences
- Some peak speed is traded for a usable machine; speed rankings in Benchmark now sit next to CPU time and peak memory, so the trade-off is visible.
- GPU-heavy runs use few CPU threads, so the limit mainly affects CPU runs.
- Not done: EcoQoS for background work, adaptive back-off at chunk boundaries, and a scripted responsiveness gate.
