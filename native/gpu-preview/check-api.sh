#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
vulkan_headers="${VULKAN_INCLUDE_DIR:-/usr/include}"
"${CXX:-c++}" -std=c++20 -Wall -Wextra -Werror -Wno-missing-field-initializers \
  -O2 -I"$vulkan_headers" check-api.cc gpu-core.cc ipc-core.cc \
  -Lbuild -lmako_gpu_preview -lvulkan -ldl -Wl,-rpath,'$ORIGIN' \
  -o build/check-api
build/check-api
