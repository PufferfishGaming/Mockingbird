# Third-party components

Mockingbird Studio's own license does not replace dependency licenses.

| Component | Upstream | Packaging status |
| --- | --- | --- |
| .NET / WPF | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf | Bundled runtime; license and notice files must accompany it |
| Whisper.cpp | https://github.com/ggml-org/whisper.cpp | Bundled native runtime; upstream MIT notice required |
| transcribe.cpp / ggml / miniz | https://github.com/handy-computer/transcribe.cpp | Native license files copied from the installed runtime |
| llama.cpp / OpenMP | https://github.com/ggml-org/llama.cpp | Native runtime and OpenMP notice copied; upstream notices still require review |
| FFmpeg 9.0.2 full build (Gyan) | https://www.gyan.dev/ffmpeg/builds/ | Built with --enable-gpl --enable-version3; complete corresponding source, build materials and notices are release gates |
| Microsoft Visual C++ runtime | https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files | App-local DLLs; redistribution terms require review |
| NuGet dependencies | Directory.Packages.props and package lock files | Package metadata inventory generated; complete notice review required |
| Whisper / Canary / Qwen model weights | Pinned URLs in ModelStore.cs | Not bundled; respective model licenses apply to downloads |

The current notice inventory is not a completed redistribution bundle. See docs/PRE_RELEASE_CHECKLIST.md. FFmpeg requirements: https://ffmpeg.org/legal.html.

