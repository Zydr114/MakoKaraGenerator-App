#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build
node_headers="${NODE_INCLUDE_DIR:-/usr/include/node}"
vulkan_headers="${VULKAN_INCLUDE_DIR:-/usr/include}"
cxx="${CXX:-c++}"
"$cxx" -std=c++20 -Wall -Wextra -Werror -O2 -fPIC -shared -I"$node_headers" broker.cc -o build/gpu_preview_broker.node
if [[ "${1:-}" != "--addon-only" ]]; then
  if [[ ! -f "$vulkan_headers/vulkan/vulkan.h" ]]; then
    echo 'Vulkan headers unavailable: install vulkan-headers or set VULKAN_INCLUDE_DIR.' >&2
    exit 1
  fi
  "$cxx" -std=c++20 -Wall -Wextra -Werror -Wno-missing-field-initializers -O2 -I"$vulkan_headers" producer.cc -lvulkan -ldl -o build/gpu-preview-producer
fi
