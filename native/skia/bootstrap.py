#!/usr/bin/env python3
"""Prepare fixed, verified native sources entirely under App/.local/skia-p0."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile

app = Path(__file__).resolve().parents[2]
native_skia = app / "native/skia"
task = app / ".local/skia-p0"
task.mkdir(parents=True, exist_ok=True)
proxy = os.environ.get("MAKO_BUILD_PROXY") or os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy") or ""
skia_revision = "7dbfc07dd33181f84e0958afb7ee805c6c769f0b"
skiasharp_revision = "f568ac94dd768ef9a2f593537cfde2dd0d348ef5"
CURL_CONNECT_TIMEOUT_SECONDS = 15
DOWNLOAD_TIMEOUT_SECONDS = 180
PATCH_TIMEOUT_SECONDS = 90
FETCH_DEPS_TIMEOUT_SECONDS = 300


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def download(url, path, expected=None):
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".part")
        cmd = [
            "curl",
            "-fL",
            "--retry",
            "3",
            "--connect-timeout",
            str(CURL_CONNECT_TIMEOUT_SECONDS),
            "--max-time",
            str(DOWNLOAD_TIMEOUT_SECONDS),
        ]
        if proxy:
            cmd.extend(["--proxy", proxy])
        cmd.extend([url, "-o", str(partial)])
        subprocess.run(cmd, check=True, timeout=DOWNLOAD_TIMEOUT_SECONDS + 10)
        if expected and digest(partial) != expected:
            raise RuntimeError(f"Downloaded checksum mismatch: {path.name}")
        partial.rename(path)
    if expected and digest(path) != expected:
        raise RuntimeError(f"Cached checksum mismatch: {path.name}")


def prepare_archive(repository, revision, archive, expected, target, patches=()):
    download(f"https://codeload.github.com/mono/{repository}/tar.gz/{revision}", archive, expected)
    stage = task / ("verify-" + repository)
    if stage.exists():
        raise RuntimeError(f"Previous verification staging directory exists; inspect it first: {stage}")
    stage.mkdir()
    with tarfile.open(archive) as source:
        source.extractall(stage, filter="data")
    extracted = stage / f"{repository}-{revision}"
    for patch in patches:
        subprocess.run(
            ["patch", "--batch", "--forward", "-p1", "-i", str(native_skia / patch)],
            cwd=extracted,
            check=True,
            timeout=PATCH_TIMEOUT_SECONDS,
        )
    if target.is_dir() and not target.is_symlink() and not any(target.iterdir()):
        target.rmdir()
    if target.exists():
        for source in extracted.rglob("*"):
            if source.is_file():
                actual = target / source.relative_to(extracted)
                if not actual.is_file() or digest(actual) != digest(source):
                    raise RuntimeError(f"Existing source differs from the exact approved archive and patches: {actual}")
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        extracted.rename(target)
    shutil.rmtree(stage)


skiasharp = task / "SkiaSharp"
skia = skiasharp / "externals/skia"
prepare_archive("SkiaSharp", skiasharp_revision, task / "skiasharp-f568ac9.tar.gz",
                "0c701134ba124d7b41b9fd2ce1ef13c4de28e0dbc6fa856de4b77cbff5529012", skiasharp)
prepare_archive("skia", skia_revision, task / "skia-7dbfc07.tar.gz",
                "5265180696746618c344acda2916c9ad1022a0313289018197876cf2612743a9", skia,
                ("skia-vulkan-state.patch", "skcms-clang23.patch"))
subprocess.run(["python3", str(native_skia / "fetch-deps.py"), str(skia)], check=True, timeout=FETCH_DEPS_TIMEOUT_SECONDS)
gn_archive = task / "gn-fe330c0.zip"
download("https://chrome-infra-packages.appspot.com/dl/gn/gn/linux-amd64/+/git_revision:fe330c0ae1ec29db30b6f830e50771a335e071fb", gn_archive)
with zipfile.ZipFile(gn_archive) as archive:
    gn = archive.read("gn")
if hashlib.sha256(gn).hexdigest() != "249db55c188a7f715c570dd1994f25c07b5d09be0c75ccbcc9f521eb63a5a50b":
    raise RuntimeError("Pinned GN executable checksum mismatch")
(skia / "bin/gn").write_bytes(gn)
(skia / "bin/gn").chmod(0o755)
print("Pinned SkiaSharp, Skia, C ABI patch, compiler compatibility patch, DEPS and GN verified.")
