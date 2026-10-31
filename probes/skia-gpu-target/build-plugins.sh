#!/usr/bin/env bash
set -euo pipefail
APP_ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TASK_ROOT="$APP_ROOT/.local/skia-p0"
SKIA_ROOT="$TASK_ROOT/SkiaSharp/externals/skia"
# These headers are pinned in the Skia revision's DEPS, not the system SDK.
HEADERS_ROOT="$SKIA_ROOT/third_party/externals/vulkan-headers/include"
for variant in draw blit; do
    DEFINES=()
    if [[ "$variant" == blit ]]; then DEFINES=(-DMAKO_SKIA_BLIT); fi
    g++ -shared -fPIC -std=c++20 -O2 -Wall -Wextra -Werror -Wno-missing-field-initializers \
        "${DEFINES[@]}" -I "$SKIA_ROOT" -I "$HEADERS_ROOT" \
        "$APP_ROOT/probes/skia-gpu-target/draw-plugin.cc" -L "$TASK_ROOT" -lSkiaSharp -lvulkan \
        -Wl,-rpath,'$ORIGIN' -o "$TASK_ROOT/libmako-skia-$variant.so"
done
