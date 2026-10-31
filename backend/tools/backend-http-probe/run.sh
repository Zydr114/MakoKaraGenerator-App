#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
artifact_base=${BACKEND_PROBE_BUILD_ROOT:-"$repo_root/artifacts/backend-extraction/b2-final/http"}
# A caller-controlled path is never a recursive deletion target. Restrict even new
# runs to repository artifacts, and preserve each run for review.
artifact_base=$(python3 - "$repo_root" "$artifact_base" <<'PY'
from pathlib import Path
import sys

allowed = (Path(sys.argv[1]) / "artifacts").resolve()
selected = Path(sys.argv[2]).resolve()
if selected == allowed or allowed not in selected.parents:
    raise SystemExit("BACKEND_PROBE_BUILD_ROOT must be a directory below repository artifacts")
print(selected)
PY
)
mkdir -p "$artifact_base"
run_root=$(mktemp -d "$artifact_base/run-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")
project="$repo_root/src/MakoKaraGenerator.Backend.Server/MakoKaraGenerator.Backend.Server.csproj"

flock -o /tmp/mkg-b2-dotnet.lock dotnet build "$project" \
  -m:1 /p:UseSharedCompilation=false > "$run_root/build.log" 2>&1

server_command=(dotnet "$repo_root/src/MakoKaraGenerator.Backend.Server/bin/Debug/net10.0/MakoKaraGenerator.Backend.Server.dll")
dotnet --list-runtimes > "$run_root/runtimes.txt"
if ! python3 - "$run_root/runtimes.txt" <<'PY'
from pathlib import Path
import sys
raise SystemExit(0 if any(line.startswith("Microsoft.AspNetCore.App 10.")
                         for line in Path(sys.argv[1]).read_text().splitlines()) else 1)
PY
then
  publish_root="$run_root/self-contained"
  flock -o /tmp/mkg-b2-dotnet.lock dotnet publish "$project" \
    -c Debug -r linux-x64 --self-contained true -o "$publish_root" \
    -m:1 /p:UseSharedCompilation=false > "$run_root/publish.log" 2>&1
  server_command=("$publish_root/MakoKaraGenerator.Backend.Server")
fi

python3 "$repo_root/tools/backend-http-probe/probe.py" \
  --output "$run_root" --server "${server_command[@]}"
