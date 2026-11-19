#!/usr/bin/env bash
# FFmpeg's XMA decoders, nothing else, for the Quest (aarch64 Android, the runner's NDK), and ci/xma.c on them:
# <out>/lib/libavcodec.a, <out>/lib/libavutil.a, <out>/libxma.a. native/android's build.rs links them into
# libdelirium.so (DELIRIUM_XMA_DIR=<out>). FFmpeg is LGPL 2.1+.
set -euo pipefail
out="$1"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
NDK="${ANDROID_NDK_LATEST_HOME:-$ANDROID_NDK_HOME}"
BIN="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin"
CC="$BIN/aarch64-linux-android29-clang"
git clone -q --depth 1 -b n7.1 https://github.com/FFmpeg/FFmpeg "$work/ffmpeg"
cd "$work/ffmpeg"
./configure --prefix="$out" --enable-cross-compile --target-os=android --arch=aarch64 \
  --cc="$CC" --ar="$BIN/llvm-ar" --nm="$BIN/llvm-nm" --ranlib="$BIN/llvm-ranlib" --strip="$BIN/llvm-strip" \
  --disable-everything --enable-decoder=xma1,xma2 \
  --disable-programs --disable-doc --disable-avformat --disable-avdevice --disable-swresample --disable-swscale \
  --disable-avfilter --disable-network --disable-autodetect --disable-asm --disable-debug \
  --enable-pic --enable-static --disable-shared \
  || { tail -40 ffbuild/config.log; exit 1; }
make -j"$(nproc)" install > make.log 2>&1 || { tail -60 make.log; exit 1; }
"$CC" -fPIC -O2 -I"$out/include" -c "$here/xma.c" -o "$work/xma.o"
"$BIN/llvm-ar" rcs "$out/libxma.a" "$work/xma.o"
ls -la "$out/lib" "$out/libxma.a"
