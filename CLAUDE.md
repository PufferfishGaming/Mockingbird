# Mockingbird Studio

A local Windows transcription app (WPF, .NET 10, x64). Two speech engines (Whisper and Canary) run independently, the app compares them, and an embedded llama.cpp model settles only the disagreements. Includes a review UI, a setup wizard, and in-app updates. All inference stays on the computer.

## Commands (PowerShell, from the repository root)

```powershell
scripts\build.ps1           # locked restore + Release build (use -UpdateLockFiles only when a package changed on purpose)
scripts\test.ps1            # all tests (run build first; uses --no-build)
scripts\verify.ps1          # build + tests + app smoke test: run this before every commit
scripts\release-chain.ps1 -Notes "..."   # verify, package, MSI, Setup.exe, MSI check, release audit (see docs/RELEASE_PROCESS.md)
```

Tests use temporary data folders (`TRIASR_DATA_ROOT`). Never point a test, script or smoke run at the real `%LOCALAPPDATA%\TriASR` folder or the installed app.

## Layout and architecture rules

`src/` holds one project per concern; `tests/Shared/ArchitectureTests.cs` enforces the rules below in every test project.

- Dependencies point inward: App -> Application -> Domain. Infrastructure implements interfaces from inside.
- Domain, Alignment, Fusion, Application and Export are pure: no UI, network, process, registry or database APIs.
- Only `TriAsr.App` and `TriAsr.Worker` are executables, and both declare a RuntimeIdentifier.
- Production projects may use only the NuGet packages listed in that test. A new package is a deliberate decision: update the test, the lock files, and say why in the commit.
- Package versions live in `Directory.Packages.props`; every project has a `packages.lock.json`.

## Product rules (do not break)

- Raw engine output is kept as evidence. Every final region keeps its provenance (Whisper text, Canary text, source).
- Never invent timestamps, confidence values, languages or device names. Canary has no native timestamps; it is aligned to the Whisper timeline.
- Requested backend and actual backend are separate; a mismatch is an error, never silently relabelled.
- The LLM may only choose between candidates or recombine their words (`ArbitrationValidation`). It never adds wording.
- Source media is never modified or overwritten. The engines read a normalized 16 kHz mono WAV; the review player prefers `playback.m4a`.
- Hungarian and German accents are never stripped in comparisons.
- Privacy: no telemetry, no uploads. The only network use is requested model/runtime downloads and the opt-out update check, which reads one small `latest.json`.
- A change to the pipeline must not change the default results. Prove it on real recordings before committing: run the old and new build on the same audio and compare the final text (the default must come out identical). A fake runner cannot know what a real program accepts, so every external program the app starts needs at least one test that runs the real tool (skipped when it is not installed).
- Speech detection (`chunks.json`, ADR-0005): the Silero tool and model are bundled in `Runtimes`. `whisper-vad-speech-segments.exe` exits with 0 even when it rejects an option, prints times in hundredths of a second, and its built-in defaults differ from its help text; always pass every threshold with the long option names (`VadSegmenter.Thresholds`). "Skip silence and music" is off by default because singing counts as non-speech and songs lose almost all their lyrics.
- Be kind to the computer: engines run at below-normal priority, thread counts follow the resource profile, FFmpeg gets two threads, and Windows is asked not to sleep during jobs. Do not use CPU-rate hard caps on ggml workers (they degrade badly); limit threads instead.

## Working agreements

- Add or update tests with every behaviour change. Baseline: 338 tests, all passing. Tests that need local runtimes (FFmpeg, the speech detector) skip on a machine without them.
- Source files are UTF-8 without BOM with LF endings. Windows PowerShell 5.1 `Get-Content` / `Set-Content` use the ANSI code page and corrupt non-ASCII text: edit with the editor tools or `[IO.File]::ReadAllText(path, [Text.Encoding]::UTF8)` and `WriteAllText` with `UTF8Encoding($false)`.
- Make exact-match edits and check the match count; do not rewrite files wholesale to change a line.
- One logical change per commit, message explains why. No `Co-Authored-By` or "Generated with" lines.
- Push only when the user asked for it in this task, as a normal push. Never force-push; the user does that.
- Do not download models, runtimes or other large files without asking first.
- Only stop processes you started, by PID. Never select windows or processes by a name fragment (a browser tab title once matched and the browser was closed).
- Some sandboxes block commands that look like deletions inside the repository (`Remove-Item`, `git rm`). If one is blocked, ask the user to run it; do not look for a way around it.
- Report faithfully: say what was run, what passed, and what was not verified.

## Releases

Use `/release <version> "<notes>"`. The user uploads the files to GitHub (there is no `gh`); the order is `Mockingbird-Studio-Setup.exe`, then `SHA256SUMS.txt`, then `latest.json` last, because installed apps read `latest.json` to learn about a version. A published version is never rebuilt. Decisions are recorded in `docs/decisions/`.
