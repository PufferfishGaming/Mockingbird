# Mockingbird Studio

A local transcription workstation for Windows x64, using Whisper and Canary to recognize speech independently and help you review uncertain wording.

Source: [github.com/PufferfishGaming/Mockingbird](https://github.com/PufferfishGaming/Mockingbird)

**Version 0.1.13 — release candidate**

<div align="center">

[![Download for Windows](https://img.shields.io/badge/Windows-Download_the_installer-2ea44f?style=for-the-badge)](https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Install-Mockingbird.cmd)

One click, one file. It verifies the MSI checksum, installs Mockingbird Studio for your Windows user and launches it. Re-run it to update or repair.

</div>

## Install

Download the Windows MSI from GitHub Releases and double-click it. Alternatively, download `install-mockingbird.cmd` from the `Installer` release and double-click it. Installation is per user; the .NET runtime is included. Download models inside the app.

Native Linux support is pending; there is currently no native Linux build.

## Features

- Audio/video import with progress, cancellation and resumable jobs.
- Whisper's 100-language catalog and independent Canary comparison for 25 languages.
- Local disagreement correction, waveform playback and manual transcript review.
- TXT, Markdown, JSON, CSV, SRT, VTT and DOCX export.
- Persistent model downloads, CPU/Vulkan backends, optional compatible CUDA/ROCm runtimes and measured auto-tuning.
- Live activity output, an interactive PowerShell panel and light/dark/system themes.

Recognition can be wrong, particularly with music, noise or silence. Review important transcripts. Readable export normalizes spacing without rewriting wording.

## Privacy

Transcription runs locally. Requested model/runtime downloads contact their providers. Terminal commands can access the network. Projects and logs can contain private information. See [PRIVACY.md](PRIVACY.md), also available in Settings.

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
```

Version metadata comes from `Directory.Build.props`. Preserve both normal and publish package lock files. Native runtime binaries and model weights are external prerequisites and are excluded from Git.

Legacy `TriAsr.*` names, storage paths and `TRIASR_*` environment variables remain for upgrade compatibility. Upgrades and uninstall preserve user projects, settings and models.

## License and release status

Copyright © 2026 PufferfishGaming. Original source is **GPL-3.0-or-later**; see [LICENSE](LICENSE). Dependencies retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

This candidate is unsigned. Before public binary distribution, complete third-party license/source requirements, clean-machine installation tests and remaining accuracy/accessibility acceptance checks.

