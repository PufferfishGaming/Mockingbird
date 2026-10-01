# ADR-0002: In-app updates from a GitHub release manifest

Status: accepted (0.1.16)

## Context
Users had to find and run a new installer by hand.

## Decision
The app reads `latest.json` from the `download` release (at startup, at most every 12 hours, or on demand) and offers **Update now / Later / Skip this version**. Nothing installs without a click, and never during a transcription, download or benchmark.

Safety rules, all enforced in `UpdateService` and covered by tests:
- HTTPS only; the manifest and the installer must live on `github.com` under this project's release path; no credentials in URLs; manifest at most 64 KB.
- The version must be numerically newer; downgrades and equal versions are never offered.
- The installer's size and SHA256 must match the manifest before anything runs; a failed or cancelled download leaves nothing behind.
- A hidden PowerShell helper (encoded command, quoted paths) waits for the app to exit, runs `Setup.exe /passive /norestart`, records the exit code, removes the installer and reopens the app.
- The check can be turned off in Settings. It sends only an ordinary request with the app name and version.
- `TRIASR_UPDATE_MANIFEST` accepts a loopback address only; it exists for end-to-end tests.

## Consequences
- Trust rests on the GitHub account and HTTPS: the installer is not code-signed. Signing would add a second independent check.
- The manifest is uploaded last (see `docs/RELEASE_PROCESS.md`) so no app is told about a version that is not yet downloadable.
- Only the installed copy updates itself; a portable or development copy only reports availability.
