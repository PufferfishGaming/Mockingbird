# The build environment of Mockingbird's FFmpeg: Debian with the MinGW-w64 cross-compiler for 64-bit Windows, linking against the
# Universal C Runtime (UCRT) that Windows 10 and 11 carry. The maths functions of the UCRT are what the FFmpeg builds before this one used;
# with the older msvcrt a few samples in a million came out 1 step apart, enough to move a Whisper timestamp now and then.
# scripts/build-ffmpeg.ps1 builds this image and runs build.sh in it; nothing is downloaded while FFmpeg itself is built.
# FFmpeg's configure also needs a compiler for this Linux (gcc, libc6-dev) for the small tools it runs while building.
FROM debian:trixie-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc-mingw-w64-ucrt64 binutils-mingw-w64-ucrt64 mingw-w64-ucrt64-dev libz-mingw-w64-dev gcc libc6-dev make nasm xz-utils \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /opt/zlib/include /opt/zlib/lib \
 && cp /usr/x86_64-w64-mingw32/include/zlib.h /usr/x86_64-w64-mingw32/include/zconf.h /opt/zlib/include/ \
 && cp /usr/x86_64-w64-mingw32/lib/libz.a /opt/zlib/lib/
COPY build.sh /build.sh
