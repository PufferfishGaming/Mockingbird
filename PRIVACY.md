# Mockingbird Studio privacy policy

Effective date: 1 October 2026

## Local processing
Mockingbird Studio processes recordings, speech recognition and transcript correction on your computer. The application does not upload media or transcripts to a transcription service. It has no built-in analytics, account system, advertising or automatic crash-report upload.

## Files stored on your computer
Projects may contain a source-media path and hash, normalized audio, raw engine output, transcripts, review revisions, checkpoints, hardware measurements and application logs. Models, partial downloads, verification receipts and settings are saved locally. These files can include personal information from your recordings or file paths. They are not encrypted by the application.

Packaged builds use the legacy LocalAppData/TriASR directory unless you choose another project or model folder. This preserves existing installations. Uninstalling the app leaves projects, settings and downloaded models in place. To remove your data, close the app and delete the chosen folders yourself. Keep any recordings or exports you want to retain.

## Network connections
When you request model or runtime downloads, the app connects to Hugging Face or GitHub and their download infrastructure. Those providers receive normal connection information such as your IP address and the requested asset. Media and transcript contents are not included in these download requests. Provider privacy policies apply to their services:
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Update checks
Unless you turn it off in Settings, the app asks GitHub for a small version file (latest.json) when it starts, at most every 12 hours, and whenever you choose Check for updates now. GitHub receives normal connection information such as your IP address, and the request carries the app's name and version. No recordings, transcripts, project data, hardware details or identifiers are sent. The app never installs an update by itself: you choose Update now, and the downloaded installer is checked against its published SHA256 checksum before it runs.

The local correction server communicates over the loopback interface (127.0.0.1); it is not cloud inference. GitHub installation downloads also contact GitHub. After the needed models and runtimes are installed, transcription can work offline.

## Terminal, exports and support
Commands you enter in the PowerShell panel have the permissions of your Windows user. They can access files, contact network services or transmit data according to the commands you run. This policy's local-processing statement describes the app's transcription features, not arbitrary terminal commands.

The app does not automatically submit logs. Review diagnostics, engine output and transcripts before sharing them in GitHub issues. Copying or exporting data is initiated by you. Your operating system, backups, synchronized folders, security software and third-party runtimes can handle files under their own policies.

## Changes and questions
Future releases may revise this policy. Review the policy bundled with the version you install. Privacy questions can be raised through the project's GitHub issues without attaching private recordings, transcripts or logs.

