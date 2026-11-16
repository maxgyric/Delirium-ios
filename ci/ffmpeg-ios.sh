#!/usr/bin/env bash
# FFmpeg's XMA decoders, nothing else, for the iPhone (arm64), and ci/xma.c on them: <out>/lib/libavcodec.a,
# <out>/lib/libavutil.a, <out>/libxma.a. On the macOS runner (Xcode's clang, the iPhone SDK); the ipa job merges them
# into libdelirium_ios.a. FFmpeg is LGPL 2.1+.
set -euo pipefail
out="$1"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
git clone -q --depth 1 -b n7.1 https://github.com/FFmpeg/FFmpeg "$work/ffmpeg"
cd "$work/ffmpeg"
sdk="$(xcrun -sdk iphoneos --show-sdk-path)"
flags="-arch arm64 -miphoneos-version-min=13.0 -isysroot $sdk"
./configure --prefix="$out" --enable-cross-compile --target-os=darwin --arch=aarch64 \
  --cc="xcrun -sdk iphoneos clang" --extra-cflags="$flags" --extra-ldflags="$flags" \
  --disable-everything --enable-decoder=xma1,xma2 \
  --disable-programs --disable-doc --disable-avformat --disable-avdevice --disable-swresample --disable-swscale \
  --disable-avfilter --disable-network --disable-autodetect --disable-asm --disable-debug \
  --enable-pic --enable-static --disable-shared \
  || { tail -40 ffbuild/config.log; exit 1; }
make -j"$(sysctl -n hw.ncpu)" install > make.log 2>&1 || { tail -60 make.log; exit 1; }
xcrun -sdk iphoneos clang $flags -O2 -I"$out/include" -c "$here/xma.c" -o "$work/xma.o"
xcrun -sdk iphoneos libtool -static -o "$out/libxma.a" "$work/xma.o"
ls -la "$out/lib" "$out/libxma.a"
