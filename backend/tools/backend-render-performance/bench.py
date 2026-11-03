#!/usr/bin/env python3
"""Build immutable production and instrumented copies for bounded render sampling."""
from __future__ import annotations

import argparse
import datetime as dt
import fcntl
import hashlib
import json
import os
import signal
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
from factory_layout import adapt, detect, FACTORY

TOOL = Path(__file__).resolve().parent
REPO = TOOL.parent.parent
SCENARIOS = ("lyrics", "csharp", "graph", "material", "particle", "combined")
EXTRA_SCENARIOS = ("ruby", "multi", "video")
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


def tool_changes(template, generated):
    return {path: {"template": template.get(path), "generated": generated.get(path)}
            for path in sorted(template.keys() | generated.keys()) if template.get(path) != generated.get(path)}


def archive_tree(commit, prefix):
    """git archive accepts a tree object; this also strips the App backend prefix."""
    return commit + ":" + prefix.rstrip("/") if prefix else commit


def extract_source(commit, destination):
    prefix = git("rev-parse", "--show-prefix")
    git_root = git("rev-parse", "--show-toplevel")
    with tempfile.TemporaryFile() as archive:
        subprocess.run(["git", "-C", git_root, "archive", "--format=tar", archive_tree(commit, prefix)], stdout=archive, check=True)
        archive.seek(0)
        with tarfile.open(fileobj=archive) as tree:
            tree.extractall(destination, filter="data")
    if not (destination / "src").is_dir() or not (destination / "Directory.Build.props").is_file():
        raise ValueError("archived commit does not contain the backend root at " + (prefix or "."))
    return prefix


def parse_times(value):
    if value is None:
        raise ValueError("matrix requires --times-us")
    try:
        times = [int(part) for part in value.split(",")]
    except ValueError as error:
        raise ValueError("times must be comma-separated integer microseconds") from error
    if not 1 <= len(times) <= 48 or any(time < 0 or time >= 370_000_000 for time in times):
        raise ValueError("matrix requires 1..48 times within the 370-second fixture")
    return times


def prepare(args):
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("prepare requires a new evidence directory")
    commit = git("rev-parse", args.commit + "^{commit}")
    output.mkdir(parents=True)
    baseline = output / "sources" / "baseline"
    baseline.mkdir(parents=True)
    prefix = extract_source(commit, baseline)
    tool_copy(baseline / "tools" / TOOL.name)
    factory_layout = adapt(baseline)
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
        "schema": "render-performance-source-v1", "createdUtc": stamp(), "sourceCommit": commit, "sourceSubdirectory": prefix,
        "toolCommit": git("rev-parse", "HEAD"), "toolWorkingTreeStatus": git("status", "--short", "--", str(TOOL)),
        "productionInputs": production, "sampleInputs": inventory(baseline / "samples" / "ScriptEffects"),
        "toolInputs": inventory(baseline / "tools" / TOOL.name), "toolTemplateInputs": inventory(TOOL),
        "factoryLayout": factory_layout, "instrumentedChanges": differences,
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
    template = inventory(TOOL)
    for name, commit in commits.items():
        root = output / "sources" / name
        root.mkdir(parents=True)
        prefix = extract_source(commit, root)
        tool_copy(root / "tools" / TOOL.name)
        factory_layout = adapt(root)
        production = inventory(root, lambda p: p.startswith("src/") or p in {
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"})
        generated_tool = inventory(root / "tools" / TOOL.name)
        sources[name] = {"commit": commit, "sourceSubdirectory": prefix, "productionInputs": production,
                         "factoryLayout": factory_layout, "toolInputs": generated_tool,
                         "toolChanges": tool_changes(template, generated_tool),
                         "sampleInputs": inventory(root / "samples" / "ScriptEffects")}
        traced = output / "sources" / (name + "-instrumented")
        shutil.copytree(root, traced)
        subprocess.run([sys.executable, str(TOOL / "instrument.py"), "--source", str(traced),
                        "--output", str(output / ("instrumentation-" + name + ".json"))], check=True)
    if sources["old"]["sampleInputs"] != sources["new"]["sampleInputs"]:
        raise ValueError("sample inputs differ between commits")
    write_json(output / "prepare.json", {"schema": "render-performance-ab-source-v1", "createdUtc": stamp(),
        "sources": sources, "variants": AB_VARIANTS, "toolCommit": git("rev-parse", "HEAD"),
        "toolWorkingTreeStatus": git("status", "--short", "--", str(TOOL)), "toolInputs": template,
        "monitoringPolicy": MONITORING_POLICY})
    print(json.dumps({"prepared": str(output), "commits": commits}), flush=True)


def verify_ab_sources(output, preparation):
    production_filter = lambda p: p.startswith("src/") or p in {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"}
    for name, source in preparation["sources"].items():
        changes = tool_changes(preparation["toolInputs"], source["toolInputs"])
        expected = {"Runner/Program.cs", "Runner/Runner.csproj"} if source["factoryLayout"] == "legacy" else set()
        if changes != source["toolChanges"] or set(changes) != expected:
            raise ValueError(name + ": tool adaptation mismatch")
        original = output / "sources" / name
        traced = output / "sources" / (name + "-instrumented")
        before = inventory(original, production_filter)
        if before != source["productionInputs"]:
            raise ValueError(name + ": production inputs changed")
        after = inventory(traced, production_filter)
        instrumentation = json.loads((output / ("instrumentation-" + name + ".json")).read_text())
        if detect((original / FACTORY).read_text(encoding="utf-8")) != source["factoryLayout"] or instrumentation.get("factoryLayout") != source["factoryLayout"]:
            raise ValueError(name + ": factory layout mismatch")
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
            if inventory(root / "samples" / "ScriptEffects") != source["sampleInputs"] or inventory(root / "tools" / TOOL.name) != source["toolInputs"]:
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


def process_snapshot(pid):
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return {"pid": pid, "startTicks": fields[19], "state": fields[0],
                "parentPid": int(fields[1]), "processGroup": int(fields[2]), "session": int(fields[3])}
    except (OSError, IndexError, ValueError):
        return None


def observe_owned_processes(leader, observed):
    """Sample the leader session and descendants, retaining identities after exit."""
    processes = {}
    for entry in Path("/proc").iterdir():
        if entry.name.isdecimal():
            item = process_snapshot(int(entry.name))
            if item is not None:
                processes[item["pid"]] = item
    current = processes.get(leader["pid"])
    if current is None or current["startTicks"] != leader["startTicks"]:
        return False
    selected = {leader["pid"]}
    selected.update(pid for pid, item in processes.items()
                    if item["session"] == leader["pid"] and item["processGroup"] == leader["pid"])
    changed = True
    while changed:
        before = len(selected)
        selected.update(pid for pid, item in processes.items() if item["parentPid"] in selected)
        changed = len(selected) != before
    for pid in selected:
        item = processes[pid]
        observed[(pid, item["startTicks"])] = item
    return True


def observed_outcomes(observed):
    outcomes = []
    for (_, _), item in sorted(observed.items()):
        current = process_snapshot(item["pid"])
        if current is None:
            status = "exited"
        elif current["startTicks"] != item["startTicks"]:
            status = "pid-reused"
        elif current["state"] == "Z":
            status = "zombie"
        else:
            status = "alive"
        outcomes.append({"observed": item, "status": status,
                         "current": current if status in {"alive", "zombie"} else None})
    return outcomes


def terminate_owned_group(process, identity, observed, cleanup):
    """Kill only the new session owned by this still-identical, unreaped leader."""
    if not exists(identity) or os.getpgid(process.pid) != process.pid or os.getsid(process.pid) != process.pid:
        raise RuntimeError("cannot verify timed-out benchmark process group identity")
    observe_owned_processes(identity, observed)
    os.killpg(process.pid, signal.SIGKILL)
    cleanup["signal"] = "SIGKILL"
    process.wait(timeout=10)
    deadline = time.monotonic() + 5
    while True:
        outcomes = observed_outcomes(observed)
        if not any(item["status"] == "alive" for item in outcomes) or time.monotonic() >= deadline:
            return outcomes
        time.sleep(.05)


def wait_observing(process, identity, timeout, observed):
    deadline = time.monotonic() + timeout
    while True:
        observe_owned_processes(identity, observed)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise subprocess.TimeoutExpired(process.args, timeout)
        try:
            return process.wait(timeout=min(.2, remaining))
        except subprocess.TimeoutExpired:
            continue


def monitoring_environment(environment, mode):
    """Do not let desktop/server selectors silently contaminate an A/B process."""
    if mode not in MONITORING_MODES:
        raise ValueError("unknown monitoring mode")
    result = {key: value for key, value in environment.items() if not key.startswith("MAKO_PERFORMANCE")}
    result["MAKO_PERFORMANCE_MODE"] = mode
    return result


def create_video_fixture(run_root, width, height):
    video = run_root / "fixture-video.mkv"
    command = ["ffmpeg", "-v", "error", "-f", "lavfi", "-i", f"testsrc2=size={width}x{height}:rate=10",
               "-t", "2", "-an", "-c:v", "ffv1", "-threads", "1", "-y", str(video)]
    subprocess.run(command, check=True, timeout=60)
    version = subprocess.check_output(["ffmpeg", "-version"], text=True).splitlines()[0]
    return video, {"file": video.name, "sha256": digest(video), "bytes": video.stat().st_size,
                   "width": width, "height": height, "frameRate": 10, "durationSeconds": 2,
                   "command": command, "ffmpegVersion": version}


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
    if (args.video_width < 2 or args.video_height < 2 or args.video_width > 4096 or args.video_height > 4096
            or args.video_width % 2 or args.video_height % 2 or args.video_width * args.video_height > 4096 * 2160):
        raise ValueError("video dimensions must be even, 2..4096, and at most 4096x2160 pixels")
    scenarios = args.scenarios.split(",")
    if not scenarios or any(x not in SCENARIOS + EXTRA_SCENARIOS for x in scenarios):
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
    if args.mode == "matrix":
        times = parse_times(args.times_us)
        if args.warmup != 0 or args.frames != len(times):
            raise ValueError("matrix requires --warmup 0 and --frames equal to the number of times")
    elif args.times_us is not None:
        raise ValueError("--times-us requires --mode matrix")
    run_root.mkdir(parents=True)
    video = None
    video_fixture = None
    if "video" in scenarios:
        video, video_fixture = create_video_fixture(run_root, args.video_width, args.video_height)
    manifest = {"schema": "render-performance-runs-v1", "startedUtc": stamp(), "arguments": vars(args), "runs": [],
        "monitoringModes": list(MONITORING_MODES) if monitoring else ["off"], "monitoringPolicy": MONITORING_POLICY,
        "provenance": {"prepareSha256": digest(output / "prepare.json"),
                       "buildInputsSha256": digest(output / "build-inputs.json"),
                       "sourceCommits": {key: value["commit"] for key, value in preparation.get("sources", {}).items()},
                       "sourceCommit": preparation.get("sourceCommit"), "toolCommit": preparation["toolCommit"]},
        "frameBudget": {"maximumRenderedFramesPerProcess": 48, "deviceProbeFramesPerProcess": 1},
        "scope": "Direct Runtime BGRA wall-clock. Rendering is sequential; short independent processes, no HTTP or video encoding."}
    if video is not None:
        manifest["videoFixture"] = video_fixture
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
                    if args.mode == "matrix":
                        command += ["--times-us", args.times_us]
                    if scenario == "video":
                        if not video.is_file() or digest(video) != manifest["videoFixture"]["sha256"]:
                            raise ValueError("shared video fixture changed before " + name)
                        command += ["--video", str(video)]
                    env = monitoring_environment(os.environ, monitoring_mode)
                    env.update({"MAKO_RENDER_BACKEND": backend, "VK_DRIVER_FILES": "/usr/share/vulkan/icd.d/radeon_icd.json",
                                "VK_ICD_FILENAMES": "/usr/share/vulkan/icd.d/radeon_icd.json"})
                    started = stamp()
                    clock = time.perf_counter()
                    with (run_root / (name + ".log")).open("w", encoding="utf-8") as log:
                        process = subprocess.Popen(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                        identity = child_identity(process.pid)
                        observed = {}
                        timeout_evidence = None
                        cleanup_error = None
                        try:
                            exit_code = wait_observing(process, identity, args.timeout, observed)
                        except subprocess.TimeoutExpired:
                            exit_code = -999
                            timeout_evidence = {"signal": None, "cleanupError": None,
                                "scope": "periodically observed identities; processes that escaped observation are unknown"}
                            try:
                                outcomes = terminate_owned_group(process, identity, observed, timeout_evidence)
                            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                                cleanup_error = f"{type(error).__name__}: {error}"
                                outcomes = observed_outcomes(observed)
                            timeout_evidence.update(cleanupError=cleanup_error, processes=outcomes)
                    record = {"name": name, "round": round_index + 1, "scenario": scenario, "backend": backend, "variant": variant,
                              "monitoringMode": monitoring_mode,
                              "startedUtc": started, "elapsedSeconds": time.perf_counter() - clock, "identity": identity,
                              "exitCode": exit_code, "exited": not exists(identity), "command": command,
                              "result": result_path.name if result_path.exists() else None,
                              "observedProcesses": observed_outcomes(observed), "timeoutCleanup": timeout_evidence}
                    manifest["runs"].append(record)
                    write_json(run_root / "manifest.json", manifest)
                    print(json.dumps({k: record[k] for k in ["name", "exitCode", "elapsedSeconds", "exited"]}), flush=True)
                    if cleanup_error is not None:
                        raise RuntimeError("timeout cleanup failed: " + cleanup_error)
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
    prepare_args.add_argument("--commit", default="HEAD" if git("rev-parse", "--show-prefix") else "8681bc09d59fc72c11d62810940324a6a44eefd2")
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
        run_args.add_argument("--video-width", type=int, default=320)
        run_args.add_argument("--video-height", type=int, default=240)
        run_args.add_argument("--mode", choices=["sequential", "seek", "matrix"], default="sequential")
        run_args.add_argument("--times-us", help="matrix timestamps in microseconds, in request order")
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
