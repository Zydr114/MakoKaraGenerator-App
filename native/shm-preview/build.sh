#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build
"${CXX:-c++}" -std=c++20 -Wall -Wextra -Werror -O2 -fPIC -shared -I"${NODE_INCLUDE_DIR:-/usr/include/node}" shm_bridge.cc -o build/shm_bridge.node
