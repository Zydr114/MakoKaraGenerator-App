#!/usr/bin/env python3
"""Prepare an isolated exact-byte deployment copy. Never launches a process."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

p = argparse.ArgumentParser()
p.add_argument("--deployment", required=True)
p.add_argument("--output", required=True)
args = p.parse_args()
source, output = Path(args.deployment).resolve(), Path(args.output).resolve()
repo = Path(__file__).resolve().parents[3]
output.relative_to(repo / ".local")
if not (source / "worker/MakoKaraGenerator.Backend.Worker.dll").is_file():
    raise SystemExit("deployment must contain worker/MakoKaraGenerator.Backend.Worker.dll")
if not (source / "deployment.json").is_file():
    raise SystemExit("deployment.json required")
if output.exists():
    raise SystemExit("output must be new")
output.mkdir(mode=0o700, parents=True)
subprocess.run(["cp", "-a", "--reflink=auto", str(source), str(output / "dist")], check=True)
files = []
for file in sorted(source.rglob("*")):
    if not file.is_file():
        continue
    relative = file.relative_to(source)
    original = hashlib.sha256(file.read_bytes()).hexdigest()
    copied = hashlib.sha256((output / "dist" / relative).read_bytes()).hexdigest()
    if original != copied:
        raise SystemExit("copied content mismatch: " + str(relative))
    files.append({"path": str(relative), "sha256": original, "bytes": file.stat().st_size})
(output / "copy-provenance.json").write_text(json.dumps({
    "boundary": "exact-byte isolated copy; no process launched; no product build",
    "source": str(source), "files": files,
}, indent=2) + "\n")
print(json.dumps({"output": str(output), "worker": str(output / "dist/worker/MakoKaraGenerator.Backend.Worker.dll"),
                  "fileCount": len(files), "launched": False}))
