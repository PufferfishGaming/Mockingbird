# Release process

How a Mockingbird version gets from the source to users. There are three editions (Studio, Server, Client; ADR-0014) with one version number; each has its own installer, checksum file and update manifest, and is built and uploaded on its own. Installers are published as assets of one GitHub pre-release tagged `download`; the README button and the in-app updater both read that tag, so its URLs never change.

## Rules

- **A version is never rebuilt.** Every build gets a new `<Version>` in `Directory.Build.props` and a matching `**Version x.y.z` line in `README.md`. The installer hash of a published version must not change.
- **Nothing is uploaded by the tooling.** The maintainer uploads the files in the GitHub web UI.
- The installer is unsigned. Windows SmartScreen will warn; the README explains how to continue.

## 1. Build and verify

```powershell
scripts\release-chain.ps1 -Notes "One short sentence for the update banner."                          # Studio; also builds and tests everything
scripts\release-chain.ps1 -Edition Server -SkipVerify -Notes "One short sentence for the update banner."
scripts\release-chain.ps1 -Edition Client -SkipVerify -Notes "One short sentence for the update banner."
```

Studio keeps its folder `artifacts\packages\<version>`; the others are built next to it as `<version>-server` and `<version>-client`. The Client's package has no speech programs (the audit fails if it has); the Server's and Studio's packages carry the same engines.

It runs, in order, and stops at the first failure (log: `artifacts\release-logs\<version>.log`):

| Step | What it proves |
| --- | --- |
| `verify.ps1` | locked restore and Release build with zero warnings, every test, the application smoke test |
| `package.ps1` | self-contained x64 payload; real-audio smoke of the packaged app |
| `build-msi.ps1` | per-user MSI of the payload |
| `build-setup.ps1` | the one-file setup wizard (WiX Burn) around the MSI, `SHA256SUMS.txt`, and `latest.json` (no byte-order mark) |
| `verify-package.ps1` | the MSI's payload matches its integrity manifest and a real German recording transcribes end to end through it |
| `release-audit.ps1` | automated checks: version, privacy policy, licence, payload integrity, Setup.exe checksum, `latest.json` matches the Setup.exe |

`-DryRun` checks the version and README and prints the plan without building. The audit's `PublishReady` stays `false` while the distribution gates (third-party source/notice bundle, clean-machine test, accessibility and accuracy acceptance, signing decision) are open.

## 2. Commit and push

Commit the version bump and release changes, then push `main` normally. Do not force-push.

## 3. Upload, in this order

Replace the same-named assets on the `download` release, waiting for each upload to finish:

| Edition | 1. installer | 2. checksums | 3. manifest, **last** |
| --- | --- | --- | --- |
| Studio | `Mockingbird-Studio-Setup.exe` | `SHA256SUMS.txt` | `latest.json` |
| Server | `Mockingbird-Server-Setup.exe` | `SHA256SUMS-server.txt` | `latest-server.json` |
| Client | `Mockingbird-Client-Setup.exe` | `SHA256SUMS-client.txt` | `latest-client.json` |

`scripts/install.ps1` (the one-line install in the README) downloads exactly these installer and checksum names from the `download` release, so a new name must be changed there too; a test compares it with the package scripts.

A manifest is how installed apps of that edition learn about a version (schema, version, URL, SHA256, size, notes). It must appear only after the installer it points to is available. Never edit it by hand; it carries the installer's real size and hash.

## 4. Verify what was published

With fresh URLs (add a cache-busting query), check that the asset names, sizes and SHA256 digests on GitHub equal the local files and that each edition's manifest was uploaded last. Then read the live manifest the way an installed app does and confirm an older version is offered the new one and a current or newer version is offered nothing.

## 5. Try the update

On a computer with the previous version installed (do it for each edition you released: Studio, Server and Client each find their own manifest): Settings, Updates, Check for updates now, then Update now. The app downloads and verifies the installer, closes, installs, and reopens. Afterwards confirm the installed version and that projects, settings and models are untouched. The automatic check only runs at startup and at most every 12 hours, so use the button when testing.

Versions before 0.1.16 have no updater and need one manual install.
