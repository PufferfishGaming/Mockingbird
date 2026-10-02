# Mockingbird privacy policy

Effective date: 1 October 2026

## Local processing
Mockingbird Studio, Mockingbird Server and Mockingbird Client share this policy; "the app" means the one you installed. Studio and Server transcribe recordings and correct transcripts on your computer. The Client does not transcribe anything itself: the recordings you choose are sent to the server you connect to, which transcribes them, and the transcripts come back; they go nowhere else. The application does not upload media or transcripts to a transcription service. It has no built-in analytics, account system, advertising or automatic crash-report upload.

## Files stored on your computer
Projects may contain a source-media path and hash, normalized audio, raw engine output, transcripts, the changes you made while reviewing, checkpoints, hardware measurements and application logs. Models, partial downloads, records of checksum checks and settings are saved locally. These files can include personal information from your recordings or file paths. They are not encrypted by the application.

Installed versions of the app use the legacy LocalAppData/TriASR directory unless you choose another project or model folder. This preserves existing installations. Uninstalling the app leaves projects, settings and downloaded models in place. To remove your data, close the app and delete the chosen folders yourself. Keep any recordings or exports you want to retain. Server keeps its files in LocalAppData/TriASR-Server and Client in LocalAppData/TriASR-Client.

Recording: the app uses the microphone only between pressing Start recording and Stop recording. In the program a recording is saved as a WAV file in the Recordings folder of the data folder and stays there until you delete it; in the web page the browser asks for your permission first and the recording stays in the browser until you send it. A recording is not sent anywhere unless you send it to a server yourself.

## Network connections
When you request model or runtime downloads, the app connects to Hugging Face or GitHub and their download infrastructure. Those providers receive normal connection information such as your IP address and the requested asset. Media and transcript contents are not included in these download requests. Provider privacy policies apply to their services:
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Servers and the network
Off by default; you start it with Start hosting. While a server is on, the app listens on a port of your computer so that other computers and programs can send it recordings and fetch transcripts. With "This computer only" chosen, only programs on this computer can connect. With "Computers on the network", the server announces its name, address and certificate fingerprint to your local network (a UDP broadcast every two seconds), and every connection over the network is encrypted with a certificate the app makes for itself; a computer that connects for the first time shows its user the fingerprint to compare. A server can be protected with a password; without one, anyone who can reach it can use it. A password you ask a client to remember is stored protected by your Windows account; the password of a server is kept in its settings file, which is not encrypted, like the other files. Recordings sent to a server are saved in its projects folder like any other project and can be read by whoever runs that server. A server also serves a web page, so that a computer without the program can use it in a browser; the page loads nothing from anywhere else, sets no cookies, and keeps the password only in the browser tab (and the chosen language in the browser's storage). The app does not report any of this to anyone else.

### Links
You can paste the address of a video or an audio file on the web. The computer that fetches it (your own in Studio, or the server you are connected to in the Client and the web page) connects to that address, and to any address it redirects to, and downloads the sound. The site that receives the request sees normal connection information such as the IP address of that computer, and the address you pasted. Links to web pages (for example on a video platform) are fetched with a helper program of the yt-dlp project (https://github.com/yt-dlp/yt-dlp). It is not part of Mockingbird: it is downloaded once from GitHub when you choose Install the link helper, is checked against the SHA-256 checksum published with it, and is kept in the Runtimes/YtDlp folder of the data folder; Check for a newer link helper downloads a newer version the same way. It contacts the site you named and nothing else. The fetched sound is saved in the Links folder of the data folder (on a server, with its other projects) and stays there until you delete it. A server fetches links only when it has a password, and not from addresses inside a private network. Only fetch what you may download and transcribe: the terms of the sites you use apply.

### Update checks
Unless you turn it off in Settings, the app asks GitHub for a small version file (latest.json) when it starts, at most every 12 hours, and whenever you choose Check for updates now. GitHub receives normal connection information such as your IP address, and the request carries the app's name and version. No recordings, transcripts, project data, hardware details or identifiers are sent. The app never installs an update by itself: you choose Update now, and the downloaded installer is checked against its published SHA256 checksum before it runs.

The local correction server communicates over the loopback interface (127.0.0.1); it is not cloud inference. Downloading the app installer also connects to GitHub. After the needed models and runtimes are installed, transcription can work offline.

## Terminal, exports and support
Commands you enter in the PowerShell panel have the permissions of your Windows user. They can access files, contact network services or transmit data according to the commands you run. This policy's local-processing statement describes the app's transcription features, not arbitrary terminal commands.

The app does not automatically submit logs. Review diagnostics, engine output and transcripts before sharing them in GitHub issues. Copying or exporting data is initiated by you. Your operating system, backups, synchronized folders, security software and third-party runtimes can handle files under their own policies.

## Changes and questions
Future releases may revise this policy. Review the policy bundled with the version you install. Privacy questions can be raised through the project's GitHub issues without attaching private recordings, transcripts or logs.

