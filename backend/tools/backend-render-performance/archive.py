#!/usr/bin/env python3
"""Verify and archive completed short render benchmark evidence."""
from __future__ import annotations

import argparse
import difflib
import gzip
import hashlib
import io
import json
from pathlib import Path
import tarfile

import bench
import analyze


def load(path):
    return json.loads(path.read_text(encoding="utf-8"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify_sources(root, prepare, instrumentation):
    baseline = root / "sources" / "baseline"
    instrumented = root / "sources" / "instrumented"
    production_filter = lambda p: p.startswith("src/") or p in {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"}
    before = bench.inventory(baseline, production_filter)
    after = bench.inventory(instrumented, production_filter)
    require(bool(before) and before == prepare["productionInputs"], "baseline production input mismatch")
    for variant in (baseline, instrumented):
        require(bench.inventory(variant / "samples" / "ScriptEffects") == prepare["sampleInputs"], "sample inputs mismatch")
        require(bench.inventory(variant / "tools" / bench.TOOL.name) == prepare["toolInputs"], "tool inputs mismatch")
    files = instrumentation["files"]
    require(bool(files), "empty instrumentation file list")
    by_path = {item["path"]: item for item in files}
    require(len(by_path) == len(files), "duplicate instrumentation path")
    expected_changes = {item["path"]: item for item in prepare["instrumentedChanges"]}
    require(len(expected_changes) == len(prepare["instrumentedChanges"]), "duplicate prepared change")
    changed = {path for path in before.keys() | after.keys() if before.get(path) != after.get(path)}
    require(changed == set(by_path) == set(expected_changes), "instrumented change set mismatch")
    patches = []
    for path in sorted(changed):
        require(path.startswith("src/") and ".." not in Path(path).parts, "invalid instrumented path")
        item = by_path[path]
        old = (baseline / path).read_bytes() if path in before else b""
        new = (instrumented / path).read_bytes() if path in after else b""
        require(item["beforeSha256"] == (sha(old) if path in before else None), "instrumentation before hash mismatch: " + path)
        require(item["afterSha256"] == (sha(new) if path in after else None), "instrumentation after hash mismatch: " + path)
        change = expected_changes[path]
        require(change["baseline"] == before.get(path) and change["instrumented"] == after.get(path), "prepared change mismatch: " + path)
        patches.extend(difflib.unified_diff(old.decode("utf-8").splitlines(keepends=True),
            new.decode("utf-8").splitlines(keepends=True), fromfile="a/" + path if old else "/dev/null", tofile="b/" + path))
    patch = "".join(patches).encode("utf-8")
    recorded_patch = (root / "instrumentation.patch").read_bytes()
    require(patch == recorded_patch == instrumentation["patch"].encode("utf-8"), "instrumentation patch mismatch")
    require(sha(patch) == instrumentation["patchSha256"], "instrumentation patch hash mismatch")
    builds = load(root / "build-inputs.json")
    require(set(builds) == set(bench.VARIANTS), "build manifest variants mismatch")
    for variant in bench.VARIANTS:
        binary = root / "sources" / variant / "tools" / bench.TOOL.name / "Runner" / "bin" / "Release" / "net10.0"
        require(bool(builds[variant]) and bench.inventory(binary) == builds[variant], "build outputs mismatch: " + variant)
    records = load(root / "build.json")["records"]
    require(len(records) == len(bench.VARIANTS) and {r["variant"] for r in records} == set(bench.VARIANTS)
        and all(r["exitCode"] == 0 for r in records), "build records incomplete or failed")
    return records


def verify_runs(root, variants=bench.VARIANTS):
    runs_root = root / "runs"
    require(runs_root.is_dir(), "runs directory missing")
    directories = sorted(path for path in runs_root.iterdir() if path.is_dir())
    require(bool(directories), "no run labels")
    totals = {name: {"processes": 0, "projectFrames": 0, "probeFrames": 0} for name in variants}
    for directory in directories:
        require(not directory.is_symlink(), "run label cannot be a symlink")
        manifest = load(directory / "manifest.json")
        video = manifest.get("videoFixture")
        if video is not None:
            file = directory / video["file"]
            require(file.parent == directory and file.is_file() and not file.is_symlink()
                    and file.stat().st_size == video["bytes"] and bench.digest(file) == video["sha256"],
                    "video fixture mismatch: " + directory.name)
        analyze.analyze(root, directory.name)
        assessment = load(directory / "assessment.json")
        require(manifest.get("complete") is True and assessment.get("valid") is True, "incomplete or invalid run: " + directory.name)
        require(not analyze.matrix_errors(manifest), "incomplete or duplicate run matrix: " + directory.name)
        records = manifest["runs"]
        require(bool(records), "empty run manifest: " + directory.name)
        referenced = set()
        for record in records:
            variant = record["variant"]
            require(variant in totals and record["exitCode"] == 0 and record["exited"] is True, "invalid run record")
            require(record.get("monitoringMode", "off") in manifest.get("monitoringModes", ["off"]), "invalid monitoring mode")
            identity = record["identity"]
            require(isinstance(identity.get("pid"), int) and identity.get("startTicks") is not None,
                "missing process identity: " + record["name"])
            require(not bench.exists(identity), "process remains alive: " + record["name"])
            name = record["result"]
            require(isinstance(name, str) and Path(name).name == name and name.endswith(".json") and name not in referenced,
                "invalid or duplicate result name")
            referenced.add(name)
            data = load(directory / name)
            require(data["process"]["pid"] == identity["pid"] and str(data["process"]["startTicks"]) == str(identity["startTicks"]),
                "result process identity mismatch: " + name)
            require(data["scenario"] == record["scenario"] and data["backend"] == record["backend"], "result parameters mismatch: " + name)
            frame_count = data["projectFrameCount"]
            require(isinstance(frame_count, int) and 1 <= frame_count <= 48 and frame_count == len(data["frames"])
                and data["deviceProbeFrames"] == 1, "frame count mismatch: " + name)
            totals[variant]["processes"] += 1
            totals[variant]["projectFrames"] += frame_count
            totals[variant]["probeFrames"] += 1
        for path in directory.glob("*.json"):
            if path.name in referenced or path.name in {"manifest.json", "assessment.json", "summary.json", "contribution.json"}:
                continue
            data = load(path)
            require(not (isinstance(data, dict) and ("process" in data or "frames" in data)), "unreferenced run result: " + path.name)
        contribution = directory / "contribution.json"
        if contribution.exists():
            require(load(contribution).get("valid") is True, "invalid contribution: " + directory.name)
        comparison = directory / "trace-comparison.json"
        traced_ab = set(manifest["arguments"]["variants"].split(",")) == {"old-instrumented", "new-instrumented"}
        if traced_ab:
            require(comparison.exists(), "missing original/trace comparison: " + directory.name)
            evidence = load(comparison)
            require(evidence.get("tracedLabel") == directory.name, "wrong trace comparison label")
            analyze.compare_trace(root, evidence["originalLabel"], directory.name)
            require(load(comparison).get("valid") is True, "invalid original/trace comparison: " + directory.name)
    return directories, totals


def collect(root, directories, records, ab=False):
    instruments = ("instrumentation-old.json", "instrumentation-old.patch", "instrumentation-new.json", "instrumentation-new.patch") if ab else ("instrumentation.json", "instrumentation.patch")
    paths = [root / name for name in ("prepare.json", *instruments, "build.json", "build-inputs.json")]
    for record in records:
        relative = Path(record["log"])
        require(not relative.is_absolute() and ".." not in relative.parts and relative.parts[0] == "build", "invalid build log path")
        paths.append(root / relative)
    environment = root / "environment"
    require(environment.is_dir(), "environment directory missing")
    paths.extend(sorted(path for path in environment.rglob("*") if path.is_file()))
    require(any(path.parent == environment for path in paths), "empty environment directory")
    for directory in directories:
        paths.extend(sorted(path for path in directory.iterdir() if path.is_file() and path.suffix in {".json", ".jsonl", ".csv", ".md", ".log", ".mkv"}))
    entries = {}
    for path in paths:
        require(path.is_file() and not path.is_symlink() and path.resolve().is_relative_to(root), "invalid evidence path: " + str(path))
        relative = path.relative_to(root).as_posix()
        require(relative not in entries, "duplicate evidence path: " + relative)
        entries[relative] = path.read_bytes()
    return entries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="completed evidence directory")
    parser.add_argument("--destination", required=True, type=Path, help="new documentation directory")
    args = parser.parse_args()
    root = args.output.resolve(strict=True)
    destination = args.destination.resolve()
    require(root.is_dir() and not destination.exists() and not destination.is_relative_to(root),
        "destination must be new and outside the evidence directory")
    prepare = load(root / "prepare.json")
    ab = prepare.get("schema") == "render-performance-ab-source-v1"
    if ab:
        bench.verify_ab_sources(root, prepare)
        variants = prepare["variants"]
        require(set(variants) == set(bench.AB_VARIANTS), "A/B variants mismatch")
        builds = load(root / "build-inputs.json")
        require(set(builds) == set(variants), "build manifest variants mismatch")
        for variant in variants:
            binary = root / "sources" / variant / "tools" / bench.TOOL.name / "Runner" / "bin" / "Release" / "net10.0"
            require(bool(builds[variant]) and bench.inventory(binary) == builds[variant], "build outputs mismatch: " + variant)
        records = load(root / "build.json")["records"]
        require(len(records) == len(variants) and {r["variant"] for r in records} == set(variants)
                and all(r["exitCode"] == 0 for r in records), "build records incomplete or failed")
    else:
        variants = bench.VARIANTS
        instrumentation = load(root / "instrumentation.json")
        records = verify_sources(root, prepare, instrumentation)
    directories, totals = verify_runs(root, variants)
    entries = collect(root, directories, records, ab)
    destination.mkdir(parents=True, exist_ok=False)
    archive = destination / "evidence.tar.gz"
    with archive.open("xb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as tar:
                for name, data in sorted(entries.items()):
                    info = tarfile.TarInfo(name)
                    info.size = len(data)
                    info.mode = 0o644
                    info.mtime = info.uid = info.gid = 0
                    info.uname = info.gname = ""
                    tar.addfile(info, io.BytesIO(data))
    index = {
        "schema": "render-performance-evidence-index-v1",
        "sourceCommit": prepare.get("sourceCommit"), "sourceCommits": {k: v["commit"] for k, v in prepare.get("sources", {}).items()}, "toolCommit": prepare["toolCommit"],
        "runs": [path.name for path in directories], "totals": totals,
        "files": {name: {"sha256": sha(data), "bytes": len(data)} for name, data in sorted(entries.items())},
        "archive": {"path": archive.name, "sha256": bench.digest(archive), "bytes": archive.stat().st_size},
        "scope": "Verified recorded inputs, builds and completed run assessments; archived direct Runtime evidence only. No HTTP, browser, platform or long-duration acceptance is implied.",
    }
    with (destination / "evidence-index.json").open("x", encoding="utf-8") as stream:
        json.dump(index, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"destination": str(destination), "archiveSha256": index["archive"]["sha256"], "totals": totals}, ensure_ascii=False))


if __name__ == "__main__":
    main()
