# Mockingbird Studio

A local transcription workstation for Windows x64, using Whisper and Canary to recognize speech independently and help you review uncertain wording.

Source: [github.com/PufferfishGaming/Mockingbird](https://github.com/PufferfishGaming/Mockingbird)

**Version 0.1.20 — release candidate**

<div align="center">

[![Download Mockingbird Studio](https://img.shields.io/badge/Studio-Download_for_Windows-2ea44f?style=for-the-badge)](https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Studio-Setup.exe)
[![Download Mockingbird Server](https://img.shields.io/badge/Server-Download_for_Windows-1f6feb?style=for-the-badge)](https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Server-Setup.exe)
[![Download Mockingbird Client](https://img.shields.io/badge/Client-Download_for_Windows-8250df?style=for-the-badge)](https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Client-Setup.exe)

One file each. Download it, double-click it, accept the license and click **Install**. No administrator permission is needed. The installers are not signed yet, so Windows warns before it runs them: see [Installing an unsigned program](#installing-an-unsigned-program). Studio does everything on one computer; Server and Client are for working across computers (see Editions). Or install from PowerShell in one line (Quick install, below).

</div>

## Editions

| Edition | What it is | Installer |
| --- | --- | --- |
| **Mockingbird Studio** | The whole program on one computer. It can also host a server and connect to one. | `Mockingbird-Studio-Setup.exe` |
| **Mockingbird Server** | Only the server: the models and the speech programs live on that computer, in a small light window. Other computers send their recordings to it. | `Mockingbird-Server-Setup.exe` |
| **Mockingbird Client** | Only the window you work in. Nothing is transcribed on that computer: recordings go to a server and the transcripts come back to be read, edited and exported. | `Mockingbird-Client-Setup.exe` |

Server and Client are new in 0.1.20. Each edition installs, updates and keeps its data on its own (`TriASR`, `TriASR-Server`, `TriASR-Client`), and has its own checksum file (`SHA256SUMS.txt`, `SHA256SUMS-server.txt`, `SHA256SUMS-client.txt`) and update file (`latest.json`, `latest-server.json`, `latest-client.json`).

## Quick install

Open PowerShell (Windows 10 or 11, x64) and paste one line. It downloads the installer from this project's `download` release, checks it against the published SHA256 file, and opens the setup wizard. No administrator permission is needed.

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/PufferfishGaming/Mockingbird/main/scripts/install.ps1))) -Edition Studio
```

Use `-Edition Server` or `-Edition Client` for the other two. Add `-Quiet` to install without any window (choosing it means you accept the license, GPL-3.0-or-later), `-NoLaunch` to not start the program afterwards, or `-DownloadOnly` to stop after the check. The command runs [scripts/install.ps1](scripts/install.ps1) from this repository, so read it first if you like; it changes nothing else on the computer. The installer is not signed and a file downloaded this way is not marked as coming from the internet, so Windows SmartScreen does not warn about it: the SHA256 check is what the script gives you instead. If the checksum does not match, the file is deleted and nothing is installed.

## Install

Download `Mockingbird-Studio-Setup.exe` from the `download` release and double-click it. The setup wizard installs for your user only and includes the .NET runtime, so nothing else is required. Download models inside the app after installing.

The Server edition downloads its models after installing, like Studio; the Client needs none.

To remove the app, use **Settings → Apps → Installed apps → Mockingbird Studio**. Uninstalling keeps your projects, settings and models.

### Installing an unsigned program

The installers are **not code-signed** yet. Windows trusts a program it has not seen often only when it carries a code-signing certificate, which costs money every year, so for now Windows warns before Mockingbird is installed. The warnings do not mean anything was found in the file: Mockingbird is open source, and every release publishes the SHA256 of each installer, so you can check that your download is exactly the file built here.

1. **Check the download** (recommended). Open PowerShell in the folder you saved it to and run `Get-FileHash .\Mockingbird-Studio-Setup.exe`. The hash must equal the line for that file in `SHA256SUMS.txt` from the same release (`SHA256SUMS-server.txt` and `SHA256SUMS-client.txt` for the other editions). If it differs, delete the file and download it again.
2. **If the browser holds the download back.** Edge may say the file "isn't commonly downloaded": point at the download, click **…** → **Keep**, then **Show more** → **Keep anyway**. Other browsers ask in a similar way; choose to keep the file.
3. **If "Windows protected your PC" appears** when you open the installer (Microsoft Defender SmartScreen): click **More info**, then **Run anyway**. The setup wizard opens.
4. **If "Smart App Control blocked an app" appears** (Windows 11 with Smart App Control on): there is no *Run anyway* button, and Smart App Control cannot make an exception for one program; it only lets through signed programs or ones Microsoft already knows. Mockingbird can be installed only with Smart App Control turned off (**Windows Security → App & browser control → Smart App Control settings → Off**). That is your choice to make, since it switches the protection off for every program; on Windows versions from before the 2026 change it cannot be switched on again without reinstalling Windows. Otherwise, wait for a signed release.

Instead of steps 2 and 3 you can install with the PowerShell line under [Quick install](#quick-install): it checks the SHA256 for you, and a file it downloads is not marked as coming from the internet, so SmartScreen does not ask. You can also clear that mark yourself after checking the hash: right-click the installer → **Properties** → tick **Unblock** → **OK** (or `Unblock-File .\Mockingbird-Studio-Setup.exe`). Updates made from inside the app check the SHA256 the same way and do not show these warnings.

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
- Karaoke-style playback in the review: while the recording plays, the words already said are coloured, the word being said is bold, and the selection follows the recording (in Studio, the Client and the web page).
- A history of your transcripts: the Projects page (in Studio, the Client and the web page) lists every project with its date and state; click a finished one to open its transcript in Review, and delete a project you no longer need (its transcript, edits and working files go; the recording file you chose stays).
- An in-app recorder: record with the microphone in Studio, in the Client or in the browser; the recording becomes the file to transcribe or to send.
- Dictation: a small window floats above your other programs (it never takes the keyboard from them); start it with its button or with keys (`Ctrl+Alt+Space` to begin with; press *Change keys*, press the keys you want, click *Done*), speak, and every phrase is typed (or pasted) where your cursor is, in any program. Studio reads the phrases on this computer; the Client, and Studio's *Remote server* page, send each phrase to the server. It needs a Whisper model; it types only into programs that are not running as administrator.
- Two languages: if you speak (or a recording switches between) two languages, English and Hungarian say, choose both, and each part is written in the language it was spoken in, in dictation, notes and recordings alike. With one language chosen, whatever is said in another one is written translated into it, and auto-detection can mistake an accent for another language. A phrase the program is not sure about is read in both languages and takes a moment longer; a recording is divided into stretches of one language, and each is transcribed in its own. A recording that turns out to be in one of the two only is transcribed exactly as with that language chosen.
- Speakers: choose *Tell speakers apart* (or how many people speak) when you start a transcription, and every part of the transcript is marked with its speaker, cut where the speaker changes; the exports say who speaks ("Speaker 1: ...", WebVTT voice tags, a CSV column). Studio's watched folder has the same choice for the recordings it picks up. In the review (Studio, the Client and the web page) give the speakers their names, *Anna* instead of *Speaker 1*: they show in the transcript at once, are saved with the edits and are used in every export. It runs on the processor and takes a few percent of the recording's length. Measured on English meetings of four people, 95 % of the words went to the right speaker.
- Notes with live transcription: a *Notes* page in Studio (this computer's notes) and a *Notes* tab on a server's pages (the Client, Studio's Remote server page and the web page; the notes are kept on the server, so every window sees the same ones). A note saves itself as you type; press *Record* and every phrase you say is added to the end of it. Keys of your own choosing start and stop recording from any program in Studio and the Client (and while the page is open in the browser), and a little window shows while they record. A note that was changed on another computer is never overwritten: what you wrote is kept as a note of its own.
- Transcribe a link: paste the address of a video, a podcast episode or an audio file on any site (not only one video site) in Studio, in the Client or in the browser. A link straight to an audio or video file just works; for web pages Mockingbird can install a helper program (yt-dlp, downloaded once from its project and checked against its published checksum). A server fetches links for the computers that use it only when it has a password.
- The interface in English, Hungarian, German, Spanish and French: chosen on the first start, switchable in Settings without a restart.
- In-app update checks with a verified one-click update.
- Three editions: Studio does everything on one computer, Server holds the models and serves them, Client is only the window (see Editions).
- A server you can name and protect with an optional password, found on your network by the computers that want to use it, with every connection over the network encrypted (see Servers on the network).
- An optional HTTP API, so that other programs can send recordings and fetch transcripts.

Recognition can be wrong, particularly with music, noise or silence. Review important transcripts. Readable export normalizes spacing without rewriting wording.

## Servers on the network

In Studio the **Servers** page (in the sidebar, below Models) lists the servers found on your network, and next to it you can host one of your own. The Server edition is that hosting box on a small page of its own; the Client edition has only the list, in a panel on the right.

**Hosting.** Press **Start hosting** (it becomes *Stop hosting* while the server runs). Give the server a **name** (shown to the others; empty uses the computer's name) and, if you like, a **password**: type one or press *Make a password*; with none, anyone who can reach the server may use it. *Who can use it* chooses between *This computer only* and *Computers on the network*; with the first only programs on the same computer can connect. The server runs while the app is open, and starts again next time if you left it on. A newly installed Server edition does not host until you press Start hosting. The first time on the network, Windows may ask whether to let it through its firewall.

**Encrypted.** Every connection over the network is encrypted (TLS). The server makes a certificate for itself, and its **fingerprint** is shown in its *Identity* box. The first time a computer connects it shows the same fingerprint; compare the two, and confirm only if they match. After that the server must show the same fingerprint, or the connection is refused with a warning. The password is sent only after you have confirmed the fingerprint, and it can be remembered (protected by your Windows account) or asked every time. *New identity* makes a new certificate; every computer then asks again.

**Finding servers.** A server reachable from the network announces its name and address on the local network every two seconds; the list shows what it hears. A server on another network is added by typing its address (`192.168.1.20` or `kitchen:8642`). The announcement is only a hint and is never trusted without the fingerprint check.

**Using a server.** Choose it and press *Connect*. In Studio a *Remote server* page appears in the sidebar, in the Client it fills the window: *New* sends a recording and a language to the server, *Projects* follows the recordings and their progress, and *Review* opens a transcript with its audio, the two engines' wording and your edits, which are saved on the server and can be exported. The speech programs run on the server; the client only sends and reads.

### Mockingbird Client Webview

Every hosted server also serves a web page, **Mockingbird Client Webview**, at its own address, for computers that do not have Mockingbird installed. Open the address in a browser (*Open web page* in the hosting box does it for this computer), enter the password if there is one, and you can send a recording, follow the recordings on the server, review and edit a transcript with its audio, and export it: the same pages as the Client, in the five languages of the program. On the network the address starts with `https://`. The certificate is the server's own, so the browser warns the first time; compare the fingerprint in the certificate details with the one in the server's *Identity* box before you continue. The page loads nothing from any other address, sets no cookies and keeps the password only for the browser tab. A server without a password only answers requests addressed to its IP address, `localhost` or its computer's name.

### The HTTP API

The same server answers programs. On this computer `http://127.0.0.1:8642` works without encryption; from the network use `https://` (a self-signed certificate, so `curl.exe -k`, or pin the fingerprint). When a password is set, every request except the health check needs it (`Authorization: Bearer <password>` or `X-Api-Key`). Recordings sent through the API are kept in the projects folder (`Api/Incoming`) and listed under Projects; the API shows only what was sent through it, and works on one recording at a time.

```powershell
curl.exe http://127.0.0.1:8642/v1/health
curl.exe -X POST --data-binary "@meeting.mp3" -H "Authorization: Bearer PASSWORD" "http://127.0.0.1:8642/v1/transcriptions?language=auto&name=meeting.mp3"
curl.exe -H "Authorization: Bearer PASSWORD" "http://127.0.0.1:8642/v1/transcriptions/ID?wait=60"
curl.exe -H "Authorization: Bearer PASSWORD" "http://127.0.0.1:8642/v1/transcriptions/ID/transcript?format=srt"
```

| Request | Answer |
| --- | --- |
| `GET /v1/health`, `GET /v1/server` | server check (name, edition, version, whether a password is needed and the connection is encrypted); no password needed |
| `GET /v1/languages`, `GET /v1/models` | the 100 languages (and which have a second engine); an OpenAI-style model list |
| `POST /v1/transcriptions?language=auto&name=file.mp3` | the request body is the recording; answers `202` with an `id`. `language` is `auto`, one code, or two joined with `+` (`en%2Bhu`) for a recording that switches between them; the transcript's `language` is then `en+hu` (or the one it turned out to be in). `speakers` (optional) is `auto` to tell the speakers apart, or how many there are (2 to 8); `409 speakers_unavailable` on a server without the speaker program, which says `speakers` in `GET /v1/server` |
| `GET /v1/transcriptions`, `GET /v1/transcriptions/{id}` | state (`queued`, `running`, `complete`, `failed`, `cancelled`), stage and percent; `?wait=30` waits for the end |
| `GET /v1/transcriptions/{id}/transcript?format=json\|txt\|md\|srt\|vtt\|csv\|docx\|full-json&mode=strict\|readable` | the transcript (`json` has segments and a `needsListening` flag per segment, and the `speaker` of each segment when the speakers were told apart, with the `speakerName` given to it and every name in `speakerNames`) |
| `GET` and `PUT /v1/transcriptions/{id}/review`, `GET .../audio` | the review as the window shows it, saving edits (with a revision history; `{"edits": [...], "speakerNames": {"1": "Anna"}}` also names the speakers: send the names as they now stand, `{}` takes them all away, and leave it out to keep them), the recording for playback |
| `POST /v1/links` | JSON `{"url": "https://...", "language": "auto", "speakers": "auto"}` (`language` and the optional `speakers` as for an upload): the server downloads the sound of the address and transcribes it; answers `202` like an upload (the job shows the stage "Downloading the link" first). Only on a server with a password; `GET /v1/server` has `linksEnabled` and `linkPages` |
| `POST /v1/live?language=en&speech=900` | dictation: the request body is one short phrase as a WAV file (up to 2 MB); answers `{"text": "...", "language": "en"}` straight away, without the program's markers like `[BLANK_AUDIO]`. `language` is `auto`, one code, or two joined with `+` (`en%2Bhu`) for a person who switches between them: the phrase is written in the one it was spoken in; `recent` (optional) is the language the previous phrase came back in, which settles a phrase that reads about as well in both. `speech` (optional) is how many milliseconds of the phrase were speech: with it, the few words Whisper invents for a cough or a click are answered as an empty text. Nothing is stored and it does not join the queue. `409 models_missing` without a model, `413` for a phrase that is too long, `501 live_unavailable` on a server that cannot read phrases; `GET /v1/server` has `liveEnabled` and `languagePairs` |
| `GET /v1/notes`, `POST /v1/notes` | the notes kept on this server (id, title, the first words, times, revision); `POST` makes one from JSON `{"title": "...", "text": "..."}` and answers `201`. `GET /v1/server` has `notesEnabled` |
| `GET`, `PUT` and `DELETE /v1/notes/{id}` | read a note with its text, save it (JSON `{"title", "text", "revision"}`: the revision you read; `409 note_changed` if someone saved it since), delete it |
| `POST /v1/transcriptions/{id}/cancel` | stops a waiting or running recording |
| `DELETE /v1/transcriptions/{id}` | deletes a finished, failed or cancelled recording with its transcript, edits and the uploaded copy; `409 still_running` while it is being worked on |
| `POST /v1/audio/transcriptions` | OpenAI-compatible: a multipart form with `file`, `language`, `response_format` (`json`, `text`, `srt`, `vtt`, `verbose_json`); answers when the transcript is ready, so existing tools that speak that API can use it with the base URL `http://127.0.0.1:8642/v1` |

Errors are `{"error":{"code":"...","message":"...","type":"..."}}`.

## Privacy

Transcription runs locally. Requested model/runtime downloads and the update check (which can be turned off) contact their providers. A hosted server listens only when you switch it on, encrypts what it sends over the network and announces itself on your local network only while the network option is on. The microphone is used only while recording, dictation or a note is being recorded; dictated words are typed into whichever program has the keyboard. Terminal commands can access the network. Projects and logs can contain private information. See [PRIVACY.md](PRIVACY.md), also available in Settings.

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

This candidate is unsigned. FFmpeg, the audio converter inside Studio and Server, is Mockingbird's own LGPL build of the official FFmpeg source, and its complete source (`FFmpeg-<version>-source.zip`) is published on the release page with the installers ([scripts/build-ffmpeg.ps1](scripts/build-ffmpeg.ps1) builds it). Before public binary distribution, complete the review of the other third-party notices, clean-machine installation tests and remaining accuracy/accessibility acceptance checks.

