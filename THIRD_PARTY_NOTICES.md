# Third-party components

Mockingbird Studio's own license does not replace dependency licenses.

| Component | Upstream | Packaging status |
| --- | --- | --- |
| .NET / WPF | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf | Bundled runtime; license and notice files must accompany it |
| Whisper.cpp | https://github.com/ggml-org/whisper.cpp | Bundled native runtime; upstream MIT notice required |
| Silero VAD (ggml conversion, ggml-silero-v5.1.2.bin) | https://github.com/snakers4/silero-vad and https://huggingface.co/ggml-org/whisper-vad | Bundled 0.9 MB speech-detection model (MIT); used by whisper-vad-speech-segments.exe from the Whisper.cpp release. Upstream MIT notice required |
| sherpa-onnx 1.13.8 (sherpa-onnx-offline-speaker-diarization.exe) | https://github.com/k2-fsa/sherpa-onnx | Bundled speaker program in Runtimes/Speakers (Apache-2.0); upstream license notice required |
| ONNX Runtime (onnxruntime.dll, from the sherpa-onnx 1.13.8 build) | https://github.com/microsoft/onnxruntime | Bundled with the speaker program (MIT); upstream notice required |
| pyannote segmentation 3.0 (ONNX conversion by sherpa-onnx) | https://huggingface.co/pyannote/segmentation-3.0 and https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-segmentation-models | Bundled 6 MB speaker-turn model (MIT); its license file ships beside it |
| NVIDIA NeMo TitaNet small (ONNX conversion by sherpa-onnx) | https://catalog.ngc.nvidia.com/orgs/nvidia/teams/nemo/models/titanet_small and https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models | Bundled 40 MB speaker-embedding model; covered by the NeMo Toolkit license (Apache-2.0) |
| transcribe.cpp / ggml / miniz | https://github.com/handy-computer/transcribe.cpp | Native license files copied from the installed runtime |
| llama.cpp / OpenMP | https://github.com/ggml-org/llama.cpp | Native runtime and OpenMP notice copied; upstream notices still require review |
| FFmpeg 9.0.2 (ffmpeg.exe in Runtimes/FFmpeg, Studio and Server) | https://ffmpeg.org | LGPL-2.1-or-later. Built by Mockingbird from the unmodified official source release with only FFmpeg's own code and zlib (zlib License), without `--enable-gpl` ([scripts/build-ffmpeg.ps1](scripts/build-ffmpeg.ps1), [scripts/ffmpeg](scripts/ffmpeg)). Its licence and build information (configure line, compiler, SHA256) ship beside it. The complete source, the release tarball with its signature and the build files, is published as `FFmpeg-9.0.2-source.zip` on the [release page](https://github.com/PufferfishGaming/Mockingbird/releases/tag/download) next to the installers, and the tarball is also at https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz |
| Microsoft Visual C++ runtime | https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files | App-local DLLs; redistribution terms require review |
| NuGet dependencies | Directory.Packages.props and package lock files | Package metadata inventory generated; complete notice review required |
| Whisper / Canary / Qwen model weights | Pinned URLs in ModelStore.cs | Not bundled; respective model licenses apply to downloads |
| yt-dlp (optional link helper) | https://github.com/yt-dlp/yt-dlp | Not bundled and not installed with Mockingbird. Downloaded on request from the project's latest release and checked against the SHA-256 published with it; the project's own code is under the Unlicense and its standalone build carries its own third-party notices |

The current notice inventory is not a completed redistribution bundle: the FFmpeg source and licence are complete; the other rows marked as needing review are not yet. FFmpeg's licence terms: https://ffmpeg.org/legal.html.

