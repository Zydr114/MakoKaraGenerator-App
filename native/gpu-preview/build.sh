#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build
node_headers="${NODE_INCLUDE_DIR:-/usr/include/node}"
vulkan_headers="${VULKAN_INCLUDE_DIR:-/usr/include}"
cxx="${CXX:-c++}"
"$cxx" -std=c++20 -Wall -Wextra -Werror -O2 -fPIC -shared -I"$node_headers" broker.cc ipc-core.cc -o build/gpu_preview_broker.node
if [[ "${1:-}" != "--addon-only" ]]; then
  if [[ ! -f "$vulkan_headers/vulkan/vulkan.h" ]]; then
    echo 'Vulkan headers unavailable: install vulkan-headers or set VULKAN_INCLUDE_DIR.' >&2
    exit 1
  fi
  common_flags=(-std=c++20 -Wall -Wextra -Werror -Wno-missing-field-initializers -O2 -I"$vulkan_headers")
  "$cxx" "${common_flags[@]}" -fPIC -shared gpu-preview.cc gpu-core.cc ipc-core.cc -Wl,--version-script=exports.map -lvulkan -o build/libmako_gpu_preview.so
  "$cxx" "${common_flags[@]}" producer.cc gpu-core.cc ipc-core.cc -lvulkan -ldl -o build/gpu-preview-producer
fi
