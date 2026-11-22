#!/usr/bin/env python3
"""Fetch the exact Linux dependencies in the pinned Skia DEPS, without history."""
import concurrent.futures
import json
import os
from pathlib import Path
import subprocess
import sys

skia = Path(sys.argv[1]).resolve()
namespace = {}
exec("def Var(x): return vars[x]\n" + (skia / "DEPS").read_text(), namespace)
proxy = os.environ.get("MAKO_BUILD_PROXY") or os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy") or ""
GIT_TIMEOUT_SECONDS = 90
GIT_ENV = {**os.environ, "GIT_TERMINAL_PROMPT": "0"}


def fetch(item):
    name, value = item
    if not isinstance(value, str):
        return None
    repository, revision = value.rsplit("@", 1)
    target = skia / name
    target.mkdir(parents=True, exist_ok=True)
    def git(*args):
        cmd = ["git", *(["-c", "http.proxy=" + proxy] if proxy else []), *args]
        return subprocess.run(cmd,
                              cwd=target, check=True, stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              text=True, env=GIT_ENV, timeout=GIT_TIMEOUT_SECONDS).stdout.strip()
    if not (target / ".git").exists():
        git("init", "--quiet")
        git("remote", "add", "origin", repository)
    actual = git("rev-parse", "--verify", "HEAD") if (target / ".git/HEAD").exists() and (target / ".git/FETCH_HEAD").exists() else ""
    if actual != revision:
        git("fetch", "--quiet", "--depth=1", "origin", revision)
        git("checkout", "--quiet", "--detach", "FETCH_HEAD")
    actual = git("rev-parse", "HEAD")
    if actual != revision:
        raise RuntimeError(f"Wrong revision for {name}: {actual}")
    print(f"{name}: {actual}", flush=True)
    return {"path": name, "repository": repository, "revision": actual}


with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    results = list(pool.map(fetch, namespace["deps"].items()))
(skia.parent.parent.parent / "dependencies.json").write_text(json.dumps([r for r in results if r], indent=2) + "\n")
