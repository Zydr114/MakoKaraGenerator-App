#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
commit=b5c8f996196ba4aa6d8f97e52b5d3b6e70f7e4e2
archive=build/vulkan-headers.tar.gz
mkdir -p build
if [[ ! -f "build/Vulkan-Headers-$commit/include/vulkan/vulkan.h" ]]; then
  proxy_args=()
  proxy_url="${GPU_PREVIEW_PROXY:-${HTTPS_PROXY:-${https_proxy:-}}}"
  if [[ -n "$proxy_url" ]]; then
    proxy_args=(--proxy "$proxy_url")
  fi
  curl --fail --location "${proxy_args[@]}" \
    "https://codeload.github.com/KhronosGroup/Vulkan-Headers/tar.gz/$commit" --output "$archive"
  echo "7937d8ec21785736436d9ff52564db7d55992070b5ac98324472cce6e425a7ba  $archive" | sha256sum --check >&2
  tar -xzf "$archive" -C build
fi
printf '%s\n' "$PWD/build/Vulkan-Headers-$commit/include"
