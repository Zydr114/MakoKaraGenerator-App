"""Exact, read-only input inventories shared by this finite acceptance tool."""
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE_PATHS = ("src", "Directory.Build.props", "Directory.Build.targets",
                "Directory.Packages.props", "global.json", "NuGet.Config", "NuGet.config")
HELPERS = ("tools/backend-package-validation-http-probe/probe.py",
           "tools/backend-validation-http-probe/probe.py", "tools/backend-http-probe/probe.py",
           "tools/backend-public-package-preview-probe/FixtureOracle/FixtureSources.cs",
           "tests/Shared/EffectPackageFixture.cs", "tests/Shared/MixedEffectPackageFixture.cs",
           "tests/Shared/EffectPackageShaderFixture.cs", "tests/Shared/EffectPackageGraphFixture.cs")


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def relative(root, name):
    path = Path(name)
    if path.is_absolute() or ".." in path.parts or str(path) != name or not path.parts:
        raise ValueError("non-canonical relative manifest path: " + name)
    return root / path


def inventory(root):
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError("inventory cannot follow symbolic links: " + str(path))
        if path.is_file():
            result[path.relative_to(root).as_posix()] = digest(path)
    return result


def production_inputs():
    paths = subprocess.run(["git", "ls-files", "-co", "--exclude-standard", "--", *SOURCE_PATHS],
                           cwd=ROOT, check=True, capture_output=True, text=True).stdout.splitlines()
    return {name: digest(relative(ROOT, name)) for name in sorted(set(paths))
            if relative(ROOT, name).is_file()}


def tool_inputs():
    paths = [*HELPERS]
    paths += [path.relative_to(ROOT).as_posix()
              for path in (ROOT / "tools/backend-project-image-validation-probe").rglob("*")
              if path.is_file() and "__pycache__" not in path.parts
              and path.suffix in (".py", ".cs", ".csproj", ".md")]
    return {name: digest(relative(ROOT, name)) for name in sorted(set(paths))}


def validate_dist(dist):
    manifest = json.loads((dist / "deployment.json").read_text())
    if manifest.get("sourceDirty") is not False or manifest.get("rid") != "linux-x64" \
            or manifest.get("selfContained") is not True or manifest.get("configuration") != "Release":
        raise ValueError("requires a source-clean Release linux-x64 self-contained deployment")
    files = manifest.get("files")
    sources = manifest.get("sourceInputs")
    if not isinstance(files, dict) or not files or not isinstance(sources, dict) or not sources:
        raise ValueError("deployment must declare complete files and production sourceInputs")
    actual = inventory(dist)
    actual.pop("deployment.json", None)
    if actual != files:
        raise ValueError("deployment has changed, extra or missing files")
    if production_inputs() != sources:
        raise ValueError("current complete production inputs differ from matched deployment")
    # sourceDirty=false is a claim. Check the declared source bytes against the
    # actual committed tree as well as the live workspace before trusting it.
    archive = subprocess.run(["git", "archive", manifest["sourceCommit"], "--", *sources],
                             cwd=ROOT, check=True, capture_output=True).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        committed = {item.name: hashlib.sha256(source.extractfile(item).read()).hexdigest()
                     for item in source if item.isfile()}
    if committed != sources:
        raise ValueError("deployment sourceInputs do not equal sourceCommit tree bytes")
    return manifest


def validate_oracle(oracle, dist):
    manifest_path = oracle.parent.parent / "input-manifest.json"
    manifest = json.loads(manifest_path.read_text())
    if digest(dist / "deployment.json") != manifest["deploymentManifestSha256"]:
        raise ValueError("Oracle build references another deployment manifest")
    if inventory(oracle.parent) != manifest["files"]:
        raise ValueError("Oracle output contains changed, extra or missing files")
    if tool_inputs() != manifest["toolSources"]:
        raise ValueError("Oracle tool/helper sources changed after the independent build")
    for name, expected in manifest["sources"].items():
        if digest(relative(ROOT, name)) != expected:
            raise ValueError("Oracle source changed: " + name)
    for name, expected in manifest["references"].items():
        if digest(relative(dist, name)) != expected:
            raise ValueError("Oracle assembly reference changed: " + name)
    if digest(oracle.parent.parent / "FixtureOracle.csproj") != manifest["projectSha256"]:
        raise ValueError("Oracle generated build project changed")
    return manifest
