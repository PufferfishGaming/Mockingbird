# ADR-0010: Watch folder

Status: accepted (after 0.1.19, unreleased).

## Context
Features.md asks for a directory watcher: audio put into a chosen folder is transcribed automatically.

## Decision
- **One folder, top level only**, chosen on New transcription ("Watch a folder"), with a language (default auto-detect) and an output: a `.txt` or `.srt` saved next to the recording, or the project only. The transcript is always kept in Projects. An export never replaces a file: an existing `name.txt` makes the new one `name (2).txt`. Subtitles need native timestamps, so a Canary-only transcript is saved as text instead of failing.
- **Only new recordings.** Choosing a folder records what is in it as already seen (`Config/watch-ledger.json`, keyed by full path, size and write time). Recordings that arrive later, including while watching is switched off or the app is closed, are transcribed when watching resumes; the ledger is reset only when the folder changes. A file replaced under the same name counts as new.
- **A recording is handed over only when it has finished arriving:** its size and write time unchanged for four seconds and not open for writing by another program (`FileShare.Read` probe). A slow rescan every 30 s backs up the file system events, which are unreliable on network shares. Hidden files, temporary names and in-progress downloads (`.part`, `.crdownload`) are ignored.
- **One at a time,** through the same pipeline as a manual job, so progress shows in the usual banner. A watched job does not move the user's selection in Projects. While one runs, model downloads, tuning and setup are blocked (`SetupBusy`).
- **Failures:** a recording that fails is recorded as seen so a broken file is not retried forever; its error is in Projects as usual. If a speech model is missing when a recording arrives, watching switches itself off with a message and the recording stays waiting. Switching watching off or closing the app while a recording is being transcribed leaves it for next time (a new project is made then; the interrupted one stays as Cancelled).
- The watcher (`FolderWatcher`, `WatchLedger`, `WatchRules`) lives in `TriAsr.Infrastructure` and uses only the .NET class library; no package was added.

## Limits
Watching runs while the app is open; there is no background or tray mode and no start-with-Windows yet. Subfolders are not watched.

## Verification
Unit tests cover the file rules, the ledger, completion detection (a file written in pieces, a file held open), sequential handling, retry versus failure and stopping mid-recording. `scripts/watch-smoke.ps1` runs the real flow with the real engines: a recording copied in pieces into a watched folder is transcribed after it is complete (the transcript equals the spoken sentence), a file that was already there is ignored, a text file is ignored, subtitles are written for a second recording, a recording added while watching is off is picked up on switching on, and nothing is transcribed twice.
