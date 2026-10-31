#!/usr/bin/env python3
"""Build immutable production and instrumented copies for bounded render sampling."""
from __future__ import annotations

import argparse
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time

TOOL = Path(__file__).resolve().parent
REPO = TOOL.parent.parent
SCENARIOS = ("lyrics", "csharp", "graph", "material", "particle", "combined")
VARIANTS = ("baseline", "instrumented")
BACKENDS = ("cpu", "vulkan")
AB_VARIANTS = ("old", "new", "old-instrumented", "new-instrumented")
MONITORING_MODES = ("off", "summary", "detailed")
MONITORING_POLICY = "All inherited MAKO_PERFORMANCE* selectors are removed. Main A/B and isolated traces explicitly use off; monitoring overhead uses separate off/summary/detailed processes."


def stamp():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def write_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(path: Path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inventory(root: Path, include=None):
    result = {}
    for path in sorted(root.rglob("*")):
        if not path.is_file() or any(part in {"bin", "obj", "__pycache__", ".git"} for part in path.relative_to(root).parts):
            continue
        relative = path.relative_to(root).as_posix()
        if include is None or include(relative):
            result[relative] = {"sha256": digest(path), "bytes": path.stat().st_size}
    return result


def git(*args):
    return subprocess.check_output(["git", "-C", str(REPO), *args], text=True).strip()


def tool_copy(destination: Path):
    shutil.copytree(TOOL, destination, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))


def prepare(args):
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("prepare requires a new evidence directory")
    commit = git("rev-parse", args.commit + "^{commit}")
    output.mkdir(parents=True)
    baseline = output / "sources" / "baseline"
    baseline.mkdir(parents=True)
    with tempfile.TemporaryFile() as archive:
        subprocess.run(["git", "-C", str(REPO), "archive", "--format=tar", commit], stdout=archive, check=True)
        archive.seek(0)
        with tarfile.open(fileobj=archive) as tree:
            tree.extractall(baseline, filter="data")
    tool_copy(baseline / "tools" / TOOL.name)
    production = inventory(baseline, lambda p: p.startswith("src/") or p in {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"})
    instrumented = output / "sources" / "instrumented"
    shutil.copytree(baseline, instrumented)
    command = [sys.executable, str(TOOL / "instrument.py"), "--source", str(instrumented),
               "--output", str(output / "instrumentation.json")]
    subprocess.run(command, check=True)
    differences = []
    before = inventory(baseline / "src")
    after = inventory(instrumented / "src")
    for relative in sorted(before.keys() | after.keys()):
        if before.get(relative) != after.get(relative):
            differences.append({"path": "src/" + relative, "baseline": before.get(relative), "instrumented": after.get(relative)})
    write_json(output / "prepare.json", {
        "schema": "render-performance-source-v1", "createdUtc": stamp(), "sourceCommit": commit,
        "toolCommit": git("rev-parse", "HEAD"), "toolWorkingTreeStatus": git("status", "--short", "--", str(TOOL)),
        "productionInputs": production, "sampleInputs": inventory(baseline / "samples" / "ScriptEffects"),
        "toolInputs": inventory(TOOL), "instrumentedChanges": differences,
        "monitoringPolicy": MONITORING_POLICY,
        "scope": "Original production source is from git archive; instrumentation changes only the second isolated copy.",
    })
    print(json.dumps({"prepared": str(output), "sourceCommit": commit, "changedFiles": len(differences)}), flush=True)


def prepare_ab(args):
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("prepare-ab requires a new evidence directory")
    commits = {name: git("rev-parse", value + "^{commit}") for name, value in
               (("old", args.old_commit), ("new", args.new_commit))}
    output.mkdir(parents=True)
    sources = {}
    for name, commit in commits.items():
        root = output / "sources" / name
        root.mkdir(parents=True)
        with tempfile.TemporaryFile() as archive:
            subprocess.run(["git", "-C", str(REPO), "archive", "--format=tar", commit], stdout=archive, check=True)
            archive.seek(0)
            with tarfile.open(fileobj=archive) as tree:
                tree.extractall(root, filter="data")
        tool_copy(root / "tools" / TOOL.name)
        production = inventory(root, lambda p: p.startswith("src/") or p in {
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"})
        sources[name] = {"commit": commit, "productionInputs": production,
                         "sampleInputs": inventory(root / "samples" / "ScriptEffects")}
        traced = output / "sources" / (name + "-instrumented")
        shutil.copytree(root, traced)
        subprocess.run([sys.executable, str(TOOL / "instrument.py"), "--source", str(traced),
                        "--output", str(output / ("instrumentation-" + name + ".json"))], check=True)
    if sources["old"]["sampleInputs"] != sources["new"]["sampleInputs"]:
        raise ValueError("sample inputs differ between commits")
    write_json(output / "prepare.json", {"schema": "render-performance-ab-source-v1", "createdUtc": stamp(),
        "sources": sources, "variants": AB_VARIANTS, "toolCommit": git("rev-parse", "HEAD"),
        "toolWorkingTreeStatus": git("status", "--short", "--", str(TOOL)), "toolInputs": inventory(TOOL),
        "monitoringPolicy": MONITORING_POLICY})
    print(json.dumps({"prepared": str(output), "commits": commits}), flush=True)


def verify_ab_sources(output, preparation):
    production_filter = lambda p: p.startswith("src/") or p in {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"}
    for name, source in preparation["sources"].items():
        original = output / "sources" / name
        traced = output / "sources" / (name + "-instrumented")
        before = inventory(original, production_filter)
        if before != source["productionInputs"]:
            raise ValueError(name + ": production inputs changed")
        after = inventory(traced, production_filter)
        instrumentation = json.loads((output / ("instrumentation-" + name + ".json")).read_text())
        if instrumentation.get("readbackLayout") not in {"old", "lease"}:
            raise ValueError(name + ": missing or unknown readback layout")
        if instrumentation.get("runtimeLayout", "legacy") not in {"legacy", "telemetry"}:
            raise ValueError(name + ": unknown runtime instrumentation layout")
        changes = {f["path"]: f for f in instrumentation["files"]}
        changed = {p for p in before.keys() | after.keys() if before.get(p) != after.get(p)}
        if not changed or changed != set(changes) or len(changes) != len(instrumentation["files"]):
            raise ValueError(name + ": instrumentation change set mismatch")
        for path in changed:
            item = changes[path]
            if item["beforeSha256"] != (before[path]["sha256"] if path in before else None) or item["afterSha256"] != (after[path]["sha256"] if path in after else None):
                raise ValueError(name + ": instrumentation hash mismatch: " + path)
        patch = (output / ("instrumentation-" + name + ".patch")).read_bytes()
        if hashlib.sha256(patch).hexdigest() != instrumentation["patchSha256"] or patch != instrumentation["patch"].encode():
            raise ValueError(name + ": instrumentation patch mismatch")
        for root in (original, traced):
            if inventory(root / "samples" / "ScriptEffects") != source["sampleInputs"] or inventory(root / "tools" / TOOL.name) != preparation["toolInputs"]:
                raise ValueError(name + ": sample or tool inputs changed")


def build(args):
    output = Path(args.output).resolve()
    preparation = json.loads((output / "prepare.json").read_text())
    variants = preparation.get("variants", VARIANTS)
    if preparation.get("schema") == "render-performance-ab-source-v1":
        verify_ab_sources(output, preparation)
    environment = os.environ.copy()
    environment.update({"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
    records = []
    with open("/tmp/mkg-performance-optimization-dotnet.lock", "a", encoding="utf-8") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        for variant in variants:
            root = output / "sources" / variant
            project = root / "tools" / TOOL.name / "Runner" / "Runner.csproj"
            command = ["dotnet", "build", str(project), "-c", "Release", "-m:1", "/p:UseSharedCompilation=false", "/nodeReuse:false"]
            log = output / "build" / (variant + ".log")
            log.parent.mkdir(exist_ok=True)
            start = time.perf_counter()
            with log.open("w", encoding="utf-8") as stream:
                result = subprocess.run(command, cwd=root, env=environment, stdout=stream, stderr=subprocess.STDOUT)
            records.append({"variant": variant, "command": command, "exitCode": result.returncode,
                            "elapsedSeconds": time.perf_counter() - start, "log": str(log.relative_to(output))})
            write_json(output / "build.json", {"createdUtc": stamp(), "records": records})
            print(json.dumps(records[-1]), flush=True)
            if result.returncode:
                raise RuntimeError("build failed; see " + str(log))
    capture_environment(output)
    manifests = {}
    for variant in variants:
        binary = output / "sources" / variant / "tools" / TOOL.name / "Runner" / "bin" / "Release" / "net10.0"
        manifests[variant] = inventory(binary)
    write_json(output / "build-inputs.json", manifests)


def capture_environment(output: Path):
    folder = output / "environment"
    folder.mkdir(exist_ok=True)
    commands = {"dotnet-info": ["dotnet", "--info"], "uname": ["uname", "-a"],
                "lscpu": ["lscpu", "-J"], "vulkan-summary": ["vulkaninfo", "--summary"],
                "font-match": ["fc-match", "-f", "%{family}\n%{file}\n", "Noto Sans CJK SC"]}
    env = os.environ.copy()
    env["VK_DRIVER_FILES"] = "/usr/share/vulkan/icd.d/radeon_icd.json"
    for name, command in commands.items():
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, timeout=30)
        (folder / (name + ".txt")).write_bytes(result.stdout)
        if result.returncode:
            raise RuntimeError("environment capture failed: " + name)
    font_lines = (folder / "font-match.txt").read_text().splitlines()
    font = Path(font_lines[-1])
    sensors = {}
    for pattern in ["/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor", "/sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference", "/sys/firmware/acpi/platform_profile"]:
        path = Path(pattern)
        if path.exists():
            sensors[pattern] = path.read_text().strip()
    write_json(folder / "fixed.json", {"recordedUtc": stamp(), "font": {"path": str(font), "sha256": digest(font)},
        "icd": {"path": env["VK_DRIVER_FILES"], "sha256": digest(Path(env["VK_DRIVER_FILES"]))}, "powerConfiguration": sensors,
        "runtimeSelectors": {key: os.environ.get(key) for key in ["DOTNET_TieredCompilation", "DOTNET_TC_QuickJit", "COMPlus_TieredCompilation", "VK_INSTANCE_LAYERS", "MANGOHUD"]}})


def child_identity(pid: int):
    try:
        stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return {"pid": pid, "startTicks": stat[19]}
    except (OSError, IndexError):
        return {"pid": pid, "startTicks": None}


def exists(identity):
    current = child_identity(identity["pid"])
    return current["startTicks"] is not None and current["startTicks"] == identity["startTicks"]


def monitoring_environment(environment, mode):
    """Do not let desktop/server selectors silently contaminate an A/B process."""
    if mode not in MONITORING_MODES:
        raise ValueError("unknown monitoring mode")
    result = {key: value for key, value in environment.items() if not key.startswith("MAKO_PERFORMANCE")}
    result["MAKO_PERFORMANCE_MODE"] = mode
    return result


def run(args):
    output = Path(args.output).resolve()
    preparation = json.loads((output / "prepare.json").read_text())
    available_variants = preparation.get("variants", VARIANTS)
    if preparation.get("schema") == "render-performance-ab-source-v1":
        verify_ab_sources(output, preparation)
        builds = json.loads((output / "build-inputs.json").read_text())
        for variant in available_variants:
            binary = output / "sources" / variant / "tools" / TOOL.name / "Runner" / "bin" / "Release" / "net10.0"
            if not builds.get(variant) or inventory(binary) != builds[variant]:
                raise ValueError(variant + ": build outputs changed")
    run_root = output / "runs" / args.label
    if run_root.exists():
        raise ValueError("run label requires a new directory; failed runs must be retained")
    if args.warmup < 0 or args.frames < 1 or args.warmup + args.frames > 48:
        raise ValueError("each process must render at most 48 frames, including warmup")
    if not 1 <= args.rounds <= 10:
        raise ValueError("round count must be between 1 and 10")
    scenarios = args.scenarios.split(",")
    if not scenarios or any(x not in SCENARIOS for x in scenarios):
        raise ValueError("unknown scenario")
    variants = args.variants.split(",")
    if not variants or len(set(variants)) != len(variants) or any(x not in available_variants for x in variants):
        raise ValueError("unknown build variant")
    monitoring = args.action == "run-monitoring"
    if monitoring and (len(variants) != 1 or variants[0] not in {"new", "baseline"}):
        raise ValueError("monitoring overhead requires one uninstrumented production variant")
    if not monitoring and preparation.get("schema") == "render-performance-ab-source-v1" and set(variants) not in ({"old", "new"}, {"old-instrumented", "new-instrumented"}):
        raise ValueError("A/B batches must contain exactly the two original or two instrumented variants")
    if len(set(scenarios)) != len(scenarios):
        raise ValueError("duplicate scenario")
    if args.mode == "seek" and (args.warmup != 0 or args.frames != 8):
        raise ValueError("seek requires --warmup 0 --frames 8")
    run_root.mkdir(parents=True)
    manifest = {"schema": "render-performance-runs-v1", "startedUtc": stamp(), "arguments": vars(args), "runs": [],
        "monitoringModes": list(MONITORING_MODES) if monitoring else ["off"], "monitoringPolicy": MONITORING_POLICY,
        "provenance": {"prepareSha256": digest(output / "prepare.json"),
                       "buildInputsSha256": digest(output / "build-inputs.json"),
                       "sourceCommits": {key: value["commit"] for key, value in preparation.get("sources", {}).items()},
                       "sourceCommit": preparation.get("sourceCommit"), "toolCommit": preparation["toolCommit"]},
        "frameBudget": {"maximumRenderedFramesPerProcess": 48, "deviceProbeFramesPerProcess": 1},
        "scope": "Direct Runtime BGRA wall-clock. Rendering is sequential; short independent processes, no HTTP or video encoding."}
    write_json(run_root / "manifest.json", manifest)
    for round_index in range(args.rounds):
        # Alternate both backend and build ordering to reduce a fixed first/last bias.
        backend_order = BACKENDS if round_index % 2 == 0 else tuple(reversed(BACKENDS))
        schedule = [(variant, mode) for variant in variants for mode in (MONITORING_MODES if monitoring else ("off",))]
        variant_order = schedule if round_index % 2 == 0 else list(reversed(schedule))
        for scenario in scenarios:
            for backend in backend_order:
                for variant, monitoring_mode in variant_order:
                    name = f"r{round_index + 1:02}-{scenario}-{backend}-{variant}"
                    if monitoring:
                        name += "-monitoring-" + monitoring_mode
                    root = output / "sources" / variant
                    dll = root / "tools" / TOOL.name / "Runner" / "bin" / "Release" / "net10.0" / "Runner.dll"
                    result_path = run_root / (name + ".json")
                    command = ["dotnet", str(dll), "--source-root", str(root), "--scenario", scenario, "--backend", backend,
                        "--output", str(result_path), "--cache-root", str(run_root / "cache" / name), "--mode", args.mode,
                        "--warmup", str(args.warmup), "--frames", str(args.frames), "--width", str(args.width),
                        "--height", str(args.height), "--transparent", str(args.transparent).lower()]
                    command += ["--counterfactual", args.counterfactual, "--monitoring", monitoring_mode]
                    env = monitoring_environment(os.environ, monitoring_mode)
                    env.update({"MAKO_RENDER_BACKEND": backend, "VK_DRIVER_FILES": "/usr/share/vulkan/icd.d/radeon_icd.json",
                                "VK_ICD_FILENAMES": "/usr/share/vulkan/icd.d/radeon_icd.json"})
                    started = stamp()
                    clock = time.perf_counter()
                    with (run_root / (name + ".log")).open("w", encoding="utf-8") as log:
                        process = subprocess.Popen(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                        identity = child_identity(process.pid)
                        try:
                            exit_code = process.wait(timeout=args.timeout)
                        except subprocess.TimeoutExpired:
                            # Only the explicitly owned benchmark process is terminated.
                            process.terminate()
                            try:
                                process.wait(timeout=10)
                            except subprocess.TimeoutExpired:
                                process.kill()
                                process.wait(timeout=10)
                            exit_code = -999
                    record = {"name": name, "round": round_index + 1, "scenario": scenario, "backend": backend, "variant": variant,
                              "monitoringMode": monitoring_mode,
                              "startedUtc": started, "elapsedSeconds": time.perf_counter() - clock, "identity": identity,
                              "exitCode": exit_code, "exited": not exists(identity), "command": command,
                              "result": result_path.name if result_path.exists() else None}
                    manifest["runs"].append(record)
                    write_json(run_root / "manifest.json", manifest)
                    print(json.dumps({k: record[k] for k in ["name", "exitCode", "elapsedSeconds", "exited"]}), flush=True)
                    if exit_code != 0 or not record["exited"] or not result_path.exists():
                        raise RuntimeError("short batch failed: " + name)
                    if args.pause:
                        time.sleep(args.pause)
    manifest["finishedUtc"] = stamp()
    manifest["complete"] = True
    write_json(run_root / "manifest.json", manifest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    prepare_args = sub.add_parser("prepare")
    prepare_args.add_argument("--commit", default="8681bc09d59fc72c11d62810940324a6a44eefd2")
    prepare_args.add_argument("--output", required=True)
    prepare_args.set_defaults(callback=prepare)
    ab_args = sub.add_parser("prepare-ab")
    ab_args.add_argument("--old-commit", required=True)
    ab_args.add_argument("--new-commit", required=True)
    ab_args.add_argument("--output", required=True)
    ab_args.set_defaults(callback=prepare_ab)
    build_args = sub.add_parser("build")
    build_args.add_argument("--output", required=True)
    build_args.set_defaults(callback=build)
    for action in ("run", "run-monitoring"):
        run_args = sub.add_parser(action)
        run_args.add_argument("--output", required=True)
        run_args.add_argument("--label", required=True)
        run_args.add_argument("--scenarios", default=",".join(SCENARIOS))
        if action == "run":
            run_args.add_argument("--variants", default=",".join(VARIANTS))
        else:
            run_args.add_argument("--variant", dest="variants", choices=["new", "baseline"], default="new")
        run_args.add_argument("--rounds", type=int, default=5)
        run_args.add_argument("--warmup", type=int, default=8)
        run_args.add_argument("--frames", type=int, default=24)
        run_args.add_argument("--width", type=int, default=1920)
        run_args.add_argument("--height", type=int, default=1080)
        run_args.add_argument("--mode", choices=["sequential", "seek"], default="sequential")
        run_args.add_argument("--transparent", action="store_true")
        run_args.add_argument("--counterfactual", choices=["none", "all", "csharp", "graph", "material", "particle"], default="none")
        run_args.add_argument("--timeout", type=int, default=120)
        run_args.add_argument("--pause", type=float, default=0.5)
        run_args.set_defaults(callback=run, action=action)
    args = parser.parse_args()
    options = vars(args).copy()
    callback = options.pop("callback")
    delattr(args, "callback")
    callback(args)


if __name__ == "__main__":
    main()
