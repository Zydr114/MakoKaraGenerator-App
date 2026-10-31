#!/usr/bin/env bash
set -euo pipefail
APP_ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TASK_ROOT="$APP_ROOT/.local/skia-p0"
SKIASHARP_ROOT="$TASK_ROOT/SkiaSharp"
SKIA_ROOT="$SKIASHARP_ROOT/externals/skia"
OUTPUT_ROOT="$SKIA_ROOT/out/mako-p0"
mkdir -p "$OUTPUT_ROOT"
cat > "$OUTPUT_ROOT/args.gn" <<EOF
target_os="linux"
target_cpu="x64"
is_official_build=true
skia_enable_tools=false
skia_enable_ganesh=true
skia_use_vulkan=true
skia_use_harfbuzz=false
skia_use_icu=false
skia_use_piex=true
skia_use_sfntly=false
skia_use_system_expat=false
skia_use_system_freetype2=false
skia_use_system_libjpeg_turbo=false
skia_use_system_libpng=false
skia_use_system_libwebp=false
skia_use_system_zlib=false
skia_enable_skottie=true
extra_asmflags=[]
extra_cflags=["-DSKIA_C_DLL", "-DHAVE_SYSCALL_GETRANDOM", "-DXML_DEV_URANDOM", "-mretpoline"]
extra_ldflags=["-static-libstdc++", "-static-libgcc", "-Wl,--version-script=$SKIASHARP_ROOT/native/linux/libSkiaSharp/libSkiaSharp.map"]
cc="clang"
cxx="clang++"
linux_soname_version="119.0.0"
EOF
cd "$SKIA_ROOT"
bin/gn gen out/mako-p0
ninja -j "${MAKO_BUILD_JOBS:-6}" -C out/mako-p0 SkiaSharp
cp out/mako-p0/libSkiaSharp.so.119.0.0 "$TASK_ROOT/libSkiaSharp.so"
ln -sfn libSkiaSharp.so "$TASK_ROOT/libSkiaSharp.so.119.0.0"
sha256sum "$TASK_ROOT/libSkiaSharp.so"
