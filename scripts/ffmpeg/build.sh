#!/bin/sh
# Builds Mockingbird's ffmpeg.exe from the official FFmpeg source release, inside the image of the Dockerfile next to this file.
# Only FFmpeg's own code is built in (no external libraries except zlib), and not with --enable-gpl, so the program is under the
# LGPL 2.1 or later. Mockingbird uses it to read the sound of any audio or video file, write the 16 kHz WAV the speech programs read,
# and write the AAC listening copy, so every decoder, demuxer, parser and filter is kept and only those two encoders and their muxers.
# Usage: build.sh <version>, with /work holding ffmpeg-<version>.tar.xz; the program and its licence land in /work/out.
set -eu
version="$1"
rm -rf /tmp/build && mkdir -p /tmp/build && cd /tmp/build
tar -xf "/work/ffmpeg-$version.tar.xz"
cd "ffmpeg-$version"
set -- --prefix=/tmp/build/install --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32ucrt- --pkg-config=false \
  --enable-static --disable-shared --disable-autodetect --enable-zlib --enable-w32threads \
  --disable-doc --disable-ffplay --disable-ffprobe --disable-network --disable-devices --disable-hwaccels \
  --disable-protocols --enable-protocol=file,pipe \
  --disable-encoders --enable-encoder=aac,pcm_s16le \
  --disable-muxers --enable-muxer=wav,ipod,mp4,mov,null \
  --disable-debug --extra-cflags=-I/opt/zlib/include --extra-ldflags="-static -L/opt/zlib/lib"
./configure "$@" | tee /tmp/build/configure.txt
grep -q '^License: LGPL version 2.1 or later' /tmp/build/configure.txt || { echo 'The build is not LGPL 2.1 or later.' >&2; exit 1; }
make -j"$(nproc)"
make install
rm -rf /work/out && mkdir -p /work/out
cp /tmp/build/install/bin/ffmpeg.exe /work/out/ffmpeg.exe
cp COPYING.LGPLv2.1 /work/out/COPYING.LGPLv2.1.txt
cp LICENSE.md /work/out/LICENSE.md
{
  echo "FFmpeg $version, built by Mockingbird's scripts/ffmpeg/build.sh"
  echo "Source: https://ffmpeg.org/releases/ffmpeg-$version.tar.xz (unmodified)"
  echo "Source SHA256: $(sha256sum "/work/ffmpeg-$version.tar.xz" | cut -d' ' -f1)"
  echo "Configure: ./configure $*"
  grep '^License' /tmp/build/configure.txt
  echo "Compiler: $(x86_64-w64-mingw32ucrt-gcc --version | head -n 1) (UCRT)"
  echo "Tools:"
  dpkg-query -W -f='  ${Package} ${Version}\n' gcc-mingw-w64-ucrt64 binutils-mingw-w64-ucrt64 mingw-w64-ucrt64-dev libz-mingw-w64-dev make nasm
  echo "Program SHA256: $(sha256sum /work/out/ffmpeg.exe | cut -d' ' -f1)"
  echo "Windows libraries it uses:"
  x86_64-w64-mingw32ucrt-objdump -p /work/out/ffmpeg.exe | grep 'DLL Name' | sed 's/^[[:space:]]*/  /'
} > /work/out/BUILD-INFO.txt
cat /work/out/BUILD-INFO.txt
