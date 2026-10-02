# Mockingbird Studio

A local transcription workstation for Windows x64, using Whisper and Canary to recognize speech independently and help you review uncertain wording.

Source: [github.com/PufferfishGaming/Mockingbird](https://github.com/PufferfishGaming/Mockingbird)

**Version 0.1.19 — release candidate**

<div align="center">

[![Download for Windows](https://img.shields.io/badge/Windows-Download_the_installer-2ea44f?style=for-the-badge)](https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Studio-Setup.exe)

One file. Download `Mockingbird-Studio-Setup.exe`, double-click it, accept the license and click **Install**. No administrator permission is needed.

</div>

## Install

Download `Mockingbird-Studio-Setup.exe` from the `download` release and double-click it. The setup wizard installs for your user only and includes the .NET runtime, so nothing else is required. Download models inside the app after installing.

The installer is unsigned, so Windows SmartScreen may show "Windows protected your PC". Choose **More info**, then **Run anyway**. To check the download first, compare `SHA256SUMS.txt` from the same release with `Get-FileHash .\Mockingbird-Studio-Setup.exe`.

To remove the app, use **Settings → Apps → Installed apps → Mockingbird Studio**. Uninstalling keeps your projects, settings and models.

### Updates

From version 0.1.16 the app checks GitHub for a newer release when it starts (at most every 12 hours) and shows a banner with **Update now**, **Later** and **Skip this version**. Updating downloads the new `Mockingbird-Studio-Setup.exe`, checks its SHA256 against the published `latest.json`, closes the app, installs it and reopens it. It never updates in the middle of a transcription, model download or benchmark, and your projects, settings and models are kept. Turn the automatic check off, or check manually, in **Settings → Updates**. Each update downloads the full installer; there are no partial updates yet. Earlier versions must be updated once by hand.

Native Linux support is pending; there is currently no native Linux build.

## Features

- Audio/video import with progress, cancellation and resumable jobs.
- Whisper's 100-language catalog and independent Canary comparison for 25 languages.
- Waveform playback and manual transcript review, with the places where the two engines disagree marked for listening. A local correction model chooses between them where it is at least 90% sure (on by default; it can be switched off in Settings).
- Removal of Whisper's runaway repetition, and an optional "Skip silence and music" setting for recordings with long quiet or noisy stretches (it removes songs, so it is off by default).
- TXT, Markdown, JSON, CSV, SRT, VTT and DOCX export.
- Persistent model downloads, CPU/Vulkan backends, optional compatible CUDA/ROCm runtimes and measured auto-tuning.
- Live activity output, an interactive PowerShell panel and light/dark/system themes.
- The interface in English, Hungarian, German, Spanish and French: chosen on the first start, switchable in Settings without a restart.
- In-app update checks with a verified one-click update.
- An optional HTTP API, off by default, so that other programs can send recordings and fetch transcripts (see Network API below).

Recognition can be wrong, particularly with music, noise or silence. Review important transcripts. Readable export normalizes spacing without rewriting wording.

## Network API

Settings → **Network API** → *Turn on the API* makes the app answer HTTP requests while it is open (default port 8642). Without *Allow other computers on the network* only programs on the same computer can connect; with it anyone on your network who has the key can. Every request except the health check needs the key (`Authorization: Bearer <key>` or `X-Api-Key`); *Copy key* and *New key* are in the same card. The connection is plain HTTP, so use the network option only on a network you trust. Recordings sent through the API are kept in the projects folder (`Api/Incoming`) and listed under Projects; the API shows only what was sent through it, and works on one recording at a time.

```powershell
curl.exe -H "Authorization: Bearer KEY" http://127.0.0.1:8642/v1/health
curl.exe -X POST --data-binary "@meeting.mp3" -H "Authorization: Bearer KEY" "http://127.0.0.1:8642/v1/transcriptions?language=auto&name=meeting.mp3"
curl.exe -H "Authorization: Bearer KEY" "http://127.0.0.1:8642/v1/transcriptions/ID?wait=60"
curl.exe -H "Authorization: Bearer KEY" "http://127.0.0.1:8642/v1/transcriptions/ID/transcript?format=srt"
```

| Request | Answer |
| --- | --- |
| `GET /v1/health` | server check, no key needed |
| `GET /v1/languages`, `GET /v1/models` | the 100 languages (and which have a second engine); an OpenAI-style model list |
| `POST /v1/transcriptions?language=auto&name=file.mp3` | the request body is the recording; answers `202` with an `id` |
| `GET /v1/transcriptions`, `GET /v1/transcriptions/{id}` | state (`queued`, `running`, `complete`, `failed`, `cancelled`), stage and percent; `?wait=30` waits for the end |
| `GET /v1/transcriptions/{id}/transcript?format=json\|txt\|md\|srt\|vtt\|csv\|docx\|full-json&mode=strict\|readable` | the transcript (`json` has segments and a `needsListening` flag per segment) |
| `POST /v1/transcriptions/{id}/cancel` | stops a waiting or running recording |
| `POST /v1/audio/transcriptions` | OpenAI-compatible: a multipart form with `file`, `language`, `response_format` (`json`, `text`, `srt`, `vtt`, `verbose_json`); answers when the transcript is ready, so existing tools that speak that API can use it with the base URL `http://127.0.0.1:8642/v1` |

Errors are `{"error":{"code":"...","message":"...","type":"..."}}`. More in `docs/decisions/ADR-0013-network-api.md`.

## Privacy

Transcription runs locally. Requested model/runtime downloads and the update check (which can be turned off) contact their providers. The network API listens only when you switch it on. Terminal commands can access the network. Projects and logs can contain private information. See [PRIVACY.md](PRIVACY.md), also available in Settings.

## Build

Install the .NET SDK specified in `global.json`. From PowerShell:

```powershell
./scripts/build.ps1
./scripts/test.ps1
```

For complete app smoke checks, supply the Windows native runtimes under `Runtimes/` and run `scripts/verify.ps1`. Packaging additionally requires the app-local Visual C++ runtime inputs:

```powershell
./scripts/package.ps1
./scripts/build-msi.ps1
./scripts/build-setup.ps1
```

`build-setup.ps1` wraps the MSI in the setup wizard and needs the WiX bootstrapper extension (`WixToolset.BootstrapperApplications.wixext` 6.0.2) placed under `.tools/wix-extensions`; the script prints the exact path if it is missing.

Version metadata comes from `Directory.Build.props`. Preserve both normal and publish package lock files. Native runtime binaries and model weights are external prerequisites and are excluded from Git.

Legacy `TriAsr.*` names, storage paths and `TRIASR_*` environment variables remain for upgrade compatibility. Upgrades and uninstall preserve user projects, settings and models.

## License and release status

Copyright © 2026 PufferfishGaming. Original source is **GPL-3.0-or-later**; see [LICENSE](LICENSE). Dependencies retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

This candidate is unsigned. Before public binary distribution, complete third-party license/source requirements, clean-machine installation tests and remaining accuracy/accessibility acceptance checks.

