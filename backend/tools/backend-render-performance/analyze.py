#!/usr/bin/env python3
"""Validate short benchmark batches and summarize original and instrumented runs."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics

import bench


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def percentile(values, p):
    ordered = sorted(values)
    if not ordered:
        return None
    index = (len(ordered) - 1) * p
    low = math.floor(index)
    return ordered[low] + (ordered[math.ceil(index)] - ordered[low]) * (index - low)


def stats(values):
    return {"count": len(values), "mean": statistics.mean(values), "median": statistics.median(values),
            "p95": percentile(values, .95), "min": min(values), "max": max(values)} if values else None


def matrix_errors(manifest):
    args = manifest["arguments"]
    modes = tuple(manifest.get("monitoringModes", ["off"]))
    if not modes or any(mode not in {"off", "summary", "detailed"} for mode in modes):
        return ["invalid monitoring mode matrix"]
    expected = {(r, s, b, v) for r in range(1, args["rounds"] + 1)
                for s in args["scenarios"].split(",") for b in ("cpu", "vulkan")
                for v in args["variants"].split(",")}
    expected = {(r, s, b, v, mode) for r, s, b, v in expected for mode in modes}
    records = [(r["round"], r["scenario"], r["backend"], r["variant"], r.get("monitoringMode", "off")) for r in manifest["runs"]]
    if not expected or len(records) != len(expected) or set(records) != expected:
        return ["run matrix is incomplete, duplicated or unexpected"]
    return []


MONITORING_COUNTERS = ("prepareMs", "scriptMs", "graphMs", "materialMs", "particleMs", "rasterMs",
                       "validationMs", "readbackMs", "imageReads", "imageBytes", "surfaceReads", "surfaceBytes",
                       "processAllocatedBytes", "gc0", "gc1", "gc2")
MONITORING_PROTOCOL_KEYS = {"route", "method", "status", "requestBody", "responseBody", "sse"}


def validate_monitoring(folder, data, expected_mode, name, errors):
    """Validate the sidecar JSONL without treating it as HTTP/protocol evidence."""
    actual_mode = data.get("monitoringMode", "off")
    if actual_mode != expected_mode:
        errors.append(name + ": monitoring mode mismatch")
        return
    configuration = data.get("monitoringConfiguration")
    if expected_mode != "off" and not isinstance(configuration, dict):
        errors.append(name + ": monitoring configuration missing")
    if isinstance(configuration, dict):
        expected_configuration = {"mode": expected_mode, "detailEvery": 1 if expected_mode == "detailed" else 0,
                                  "intervalSeconds": 10, "maxEventsPerSecond": 10}
        if any(configuration.get(key) != value for key, value in expected_configuration.items()):
            errors.append(name + ": monitoring configuration differs from production defaults")
    file_name = data.get("monitoringFile")
    line_count = data.get("monitoringLines", 0)
    if expected_mode == "off":
        if file_name is not None or line_count != 0:
            errors.append(name + ": monitoring off produced sidecar output")
        return
    if not isinstance(file_name, str) or Path(file_name).name != file_name or not file_name.endswith(".monitoring.jsonl"):
        errors.append(name + ": invalid monitoring sidecar name")
        return
    sidecar = folder / file_name
    if not sidecar.is_file() or sidecar.is_symlink():
        errors.append(name + ": monitoring sidecar missing")
        return
    lines = sidecar.read_text(encoding="utf-8").splitlines()
    if line_count != len(lines) or not lines:
        errors.append(name + ": monitoring line count mismatch")
    kinds = set()
    summary_frames = 0
    for index, line in enumerate(lines):
        prefix = "MAKO_PERFORMANCE_V1 "
        if not line.startswith(prefix):
            errors.append(name + f": monitoring line {index} missing prefix")
            continue
        try:
            event = json.loads(line[len(prefix):])
        except json.JSONDecodeError:
            errors.append(name + f": monitoring line {index} is not JSON")
            continue
        # Raw sidecars retain the production wire bytes. The initial collector
        # uses .NET's PascalCase defaults; a camelCase serializer is also explicit
        # and unambiguous. Reject aliases instead of silently discarding fields.
        if isinstance(event, dict):
            normalized = {key[:1].lower() + key[1:]: value for key, value in event.items()}
            if len(normalized) != len(event):
                errors.append(name + f": monitoring line {index} has duplicate field aliases")
                continue
            event = normalized
        if not isinstance(event, dict) or event.get("version") != 1 or event.get("kind") not in {"frame", "summary"}:
            errors.append(name + f": monitoring line {index} has invalid event shape")
            continue
        if MONITORING_PROTOCOL_KEYS & set(event):
            errors.append(name + f": monitoring line {index} contains protocol fields")
        kinds.add(event["kind"])
        for key in MONITORING_COUNTERS:
            value = event.get(key)
            if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value < 0:
                errors.append(name + f": monitoring line {index} has invalid counter {key}")
        expected_backend = "gpu" if data["backend"] == "vulkan" else "cpu"
        if event.get("backend") != expected_backend and event.get("frames", 0) > 0:
            errors.append(name + f": monitoring line {index} has unexpected backend")
        if event.get("frames", 0) < 0:
            errors.append(name + f": monitoring line {index} has no frames")
        if event["kind"] == "summary":
            summary_frames += event.get("frames", 0)
    required = {"summary"} if expected_mode == "summary" else {"frame", "summary"}
    if not required.issubset(kinds):
        errors.append(name + ": monitoring sidecar lacks expected event kinds")
    if summary_frames != data["projectFrameCount"]:
        errors.append(name + ": monitoring summaries do not cover all project frames")


def analyze(root, label):
    folder = root / "runs" / label
    manifest = json.loads((folder / "manifest.json").read_text())
    if not manifest.get("complete"):
        raise ValueError("incomplete run manifest")
    errors = matrix_errors(manifest)
    variants = set(manifest["arguments"]["variants"].split(","))
    pairs = ({"baseline", "instrumented"}, {"old", "new"}, {"old-instrumented", "new-instrumented"})
    pair = next((p for p in pairs if p == variants), None)
    all_records = []
    paired = {}
    flat = []
    counts = {"projectFrames": 0, "probeFrames": 0, "matchedFrames": 0, "matchedMonitoringFrames": 0,
              "validatedTraces": 0, "exitedProcesses": 0}
    for record in manifest["runs"]:
        data = json.loads((folder / record["result"]).read_text())
        frames = data["frames"]
        name = record["name"]
        args = manifest["arguments"]
        if args.get("mode") == "matrix":
            try:
                expected_times = bench.parse_times(args.get("times_us"))
                if data.get("requestedTimesUs") != expected_times or [f["timeUs"] for f in frames] != expected_times:
                    errors.append(name + ": explicit timestamp sequence mismatch")
            except ValueError:
                errors.append(name + ": invalid explicit timestamp sequence")
        if record["scenario"] == "video":
            video = manifest.get("videoFixture", {})
            video_path = folder / video.get("file", "")
            if (not video_path.is_file() or video_path.is_symlink()
                    or video_path.parent != folder or bench.digest(video_path) != video.get("sha256")
                    or data.get("fixture", {}).get("videoSha256") != video.get("sha256")):
                errors.append(name + ": video fixture digest mismatch")
        monitoring_mode = record.get("monitoringMode", "off")
        if monitoring_mode not in manifest.get("monitoringModes", ["off"]):
            errors.append(name + ": unexpected monitoring mode")
        validate_monitoring(folder, data, monitoring_mode, name, errors)
        for field, expected in (("scenario", record["scenario"]), ("backend", record["backend"]),
                                ("width", args.get("width")), ("height", args.get("height")),
                                ("mode", args.get("mode")), ("transparent", args.get("transparent")),
                                ("counterfactual", args.get("counterfactual")),
                                ("requestedWarmupFrames", args.get("warmup")),
                                ("requestedMeasuredFrames", args.get("frames"))):
            if expected is not None and data.get(field) != expected:
                errors.append(name + ": result parameter mismatch: " + field)
        if args.get("frames") is not None and len(frames) != args["warmup"] + args["frames"]:
            errors.append(name + ": requested frame count mismatch")
        if not record["exited"] or record["exitCode"] != 0:
            errors.append(name + ": process did not exit cleanly")
        else:
            counts["exitedProcesses"] += 1
        if data["projectFrameCount"] != len(frames) or len(frames) > 48 or data["deviceProbeFrames"] != 1:
            errors.append(name + ": frame budget mismatch")
        counts["projectFrames"] += len(frames)
        counts["probeFrames"] += data["deviceProbeFrames"]
        process = data["process"]
        if process["pid"] != record["identity"]["pid"] or str(process["startTicks"]) != str(record["identity"]["startTicks"]):
            errors.append(name + ": process identity mismatch")
        if record["backend"] == "vulkan":
            libraries = " ".join(process["mappedLibraries"]).lower()
            if "vulkan" not in libraries or "radeon" not in libraries or not process["drmDevices"]:
                errors.append(name + ": missing same-process hardware evidence")
        if data["deviceProbe"]["backend"] != record["backend"]:
            errors.append(name + ": backend mismatch")
        expected_trace = record["variant"] == "instrumented" or record["variant"].endswith("-instrumented")
        if data["traceAvailable"] != expected_trace:
            errors.append(name + ": trace availability mismatch")
        for index, frame in enumerate(frames):
            if frame["index"] != index or frame["pixelBytes"] != data["width"] * data["height"] * 4 or frame["hasError"]:
                errors.append(name + ": invalid frame " + str(index))
            if not math.isfinite(frame["wallMs"]) or frame["wallMs"] <= 0:
                errors.append(name + ": invalid timing")
            for field in ("bgraSha256", "diagnosticSha256"):
                value = frame.get(field, "")
                if len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
                    errors.append(name + ": invalid digest " + field)
            flat.append({"run": name, "round": record["round"], "scenario": record["scenario"], "backend": record["backend"],
                         "variant": record["variant"], "monitoringMode": monitoring_mode,
                         **{k: frame[k] for k in ["index", "timeUs", "phase", "wallMs", "allocatedBytes", "bgraSha256"]}})
        for item in [data["create"], *frames]:
            trace = item.get("trace")
            if expected_trace:
                if not trace or not trace["valid"] or trace["errors"]:
                    errors.append(name + ": invalid trace " + str(trace))
                    continue
                stages = trace["stages"]
                root_stage = next((s for s in stages if s["name"] in ("create", "render")), None)
                if root_stage is None:
                    errors.append(name + ": missing root trace")
                else:
                    total = sum(s["exclusiveMs"] for s in stages)
                    # The exercised render path is serial; async context moves threads but does not overlap child work.
                    if abs(total - root_stage["inclusiveMs"]) > max(.001, root_stage["inclusiveMs"] * 1e-6):
                        errors.append(name + ": exclusive stages do not partition root")
                counts["validatedTraces"] += 1
            elif trace is not None:
                errors.append(name + ": original build unexpectedly traced")
        paired.setdefault((record["round"], record["scenario"], record["backend"], monitoring_mode), {})[record["variant"]] = data
        all_records.append((record, data))
    if pair:
        for key, versions in paired.items():
            if set(versions) != pair:
                errors.append(str(key) + ": missing paired variant")
                continue
            left, right = ("baseline", "instrumented") if "baseline" in pair else (("old", "new") if "old" in pair else ("old-instrumented", "new-instrumented"))
            before, after = versions[left], versions[right]
            metadata = ("fixtureSha256", "fixture", "scenario", "backend", "mode", "counterfactual", "width", "height", "transparent", "quality", "requestedWarmupFrames", "requestedMeasuredFrames", "deviceProbe")
            if any(before.get(k) != after.get(k) for k in metadata) or len(before["frames"]) != len(after["frames"]):
                errors.append(str(key) + ": fixture or frame count changed")
            if key[2] == "vulkan" and before["process"]["drmDevices"] != after["process"]["drmDevices"]:
                errors.append(str(key) + ": same-process DRM device mismatch")
            for a, b in zip(before["frames"], after["frames"]):
                keys = ["index", "timeUs", "phase", "bgraSha256", "pixelBytes", "hasError", "diagnosticCount", "imageDiagnosticCount", "materialDiagnosticCount", "diagnosticSha256", "diagnostics"]
                if any(a[k] != b[k] for k in keys):
                    errors.append(str(key) + ": pixels or diagnostics changed at " + str(a["index"]))
                else:
                    counts["matchedFrames"] += 1
    # Stable deterministic fixtures must also agree across independent rounds of the same build/backend.
    fingerprints = {}
    for record, data in all_records:
        key = (record["scenario"], record["backend"], record["variant"], record.get("monitoringMode", "off"))
        values = [data["fixtureSha256"], [(f["timeUs"], f["bgraSha256"], f["diagnosticSha256"], f["diagnostics"]) for f in data["frames"]]]
        if key in fingerprints and fingerprints[key] != values:
            errors.append(str(key) + ": repeated rounds changed pixels")
        fingerprints[key] = values
    # Monitoring overhead runs contain one production variant in three modes. Every
    # mode must preserve the exact BGRA and diagnostic sequence of the explicit off
    # control; this is separate from old/new A/B pairing above.
    mode_groups = {}
    for record, data in all_records:
        mode_groups.setdefault((record["round"], record["scenario"], record["backend"], record["variant"]), {})[record.get("monitoringMode", "off")] = data
    for key, modes in mode_groups.items():
        if len(manifest.get("monitoringModes", ["off"])) <= 1:
            continue
        control = modes.get("off")
        if control is None:
            errors.append(str(key) + ": monitoring off control missing")
            continue
        for mode in manifest.get("monitoringModes", ["off"]):
            candidate = modes.get(mode)
            if candidate is None:
                errors.append(str(key) + ": monitoring mode missing: " + mode)
                continue
            if len(control["frames"]) != len(candidate["frames"]):
                errors.append(str(key) + ": monitoring frame count changed in " + mode)
                continue
            metadata = ("fixtureSha256", "fixture", "scenario", "backend", "mode", "counterfactual", "width", "height",
                        "transparent", "quality", "requestedWarmupFrames", "requestedMeasuredFrames", "deviceProbe")
            if any(control.get(field) != candidate.get(field) for field in metadata):
                errors.append(str(key) + ": monitoring input or device changed in " + mode)
            if key[2] == "vulkan" and control["process"]["drmDevices"] != candidate["process"]["drmDevices"]:
                errors.append(str(key) + ": monitoring DRM device changed in " + mode)
            for before, after in zip(control["frames"], candidate["frames"]):
                fields = ["index", "timeUs", "phase", "bgraSha256", "pixelBytes", "hasError", "diagnosticCount",
                          "imageDiagnosticCount", "materialDiagnosticCount", "diagnosticSha256", "diagnostics"]
                if any(before[field] != after[field] for field in fields):
                    errors.append(str(key) + ": monitoring pixels or diagnostics changed in " + mode + " at " + str(before["index"]))
                elif mode != "off":
                    counts["matchedMonitoringFrames"] += 1
    groups = {}
    for record, data in all_records:
        key = (record["scenario"], record["backend"], record["variant"], record.get("monitoringMode", "off"))
        groups.setdefault(key, []).append((record, data))
    summaries = []
    stage_rows = []
    byte_rows = []
    for (scenario, backend, variant, monitoring_mode), rows in sorted(groups.items()):
        steady = [f for _, d in rows for f in d["frames"] if f["phase"] == "steady"]
        measured = steady or [f for _, d in rows for f in d["frames"] if f["phase"] != "warmup"]
        rounds = [statistics.median([f["wallMs"] for f in d["frames"] if f["phase"] == "steady"])
                  for _, d in rows if any(f["phase"] == "steady" for f in d["frames"])]
        item = {"scenario": scenario, "backend": backend, "variant": variant, "monitoringMode": monitoring_mode, "rounds": len(rows),
                "frameWallMs": stats([f["wallMs"] for f in measured]), "roundMedianWallMs": stats(rounds),
                "roundMedians": rounds, "createWallMs": stats([d["create"]["wallMs"] for _, d in rows]),
                "firstFrameWallMs": stats([d["frames"][0]["wallMs"] for _, d in rows]),
                "allocatedBytes": stats([f["allocatedBytes"] for f in measured]),
                "gcCollections": [sum(f["gcCollections"][g] for f in measured) for g in range(3)],
                "disposeMs": stats([d["disposeMs"] for _, d in rows]),
                "framesByIndex": [{"index": index, "timeUs": rows[0][1]["frames"][index]["timeUs"],
                    "phase": rows[0][1]["frames"][index]["phase"], "wallMs": stats([d["frames"][index]["wallMs"] for _, d in rows])}
                    for index in range(len(rows[0][1]["frames"]))]}
        summaries.append(item)
        if variant == "instrumented" or variant.endswith("-instrumented"):
            for phase, frames in [("measured", measured), ("create", [d["create"] for _, d in rows]), ("first", [d["frames"][0] for _, d in rows])]:
                names = sorted({s["name"] for f in frames for s in f["trace"]["stages"]})
                stage_maps = [{s["name"]: s for s in f["trace"]["stages"]} for f in frames]
                byte_maps = [f["trace"].get("bytes", {}) for f in frames]
                for name in sorted({name for values in byte_maps for name in values}):
                    values = [values.get(name, 0) for values in byte_maps]
                    if any(not isinstance(value, int) or value < 0 for value in values):
                        errors.append(str((scenario, backend, variant, phase, name)) + ": invalid byte counter")
                        continue
                    byte_rows.append({"scenario": scenario, "backend": backend, "variant": variant, "phase": phase,
                                      "counter": name, "frames": len(values), **stats(values)})
                for name in names:
                    stage_rows.append({"scenario": scenario, "backend": backend, "variant": variant, "phase": phase, "stage": name,
                        "frames": len(frames), "callsPerFrame": statistics.mean(m.get(name, {}).get("calls", 0) for m in stage_maps),
                        "meanInclusiveMs": statistics.mean(m.get(name, {}).get("inclusiveMs", 0) for m in stage_maps),
                        "medianInclusiveMs": statistics.median(m.get(name, {}).get("inclusiveMs", 0) for m in stage_maps),
                        "meanExclusiveMs": statistics.mean(m.get(name, {}).get("exclusiveMs", 0) for m in stage_maps),
                        "p95InclusiveMs": percentile([m.get(name, {}).get("inclusiveMs", 0) for m in stage_maps], .95)})
    overhead = []
    by_mode = {(item["scenario"], item["backend"], item["variant"], item["monitoringMode"]): item for item in summaries}
    for item in summaries:
        if item["monitoringMode"] == "off":
            continue
        control = by_mode.get((item["scenario"], item["backend"], item["variant"], "off"))
        if control is None:
            continue
        off_ms = control["frameWallMs"]["median"]
        on_ms = item["frameWallMs"]["median"]
        overhead.append({"scenario": item["scenario"], "backend": item["backend"], "variant": item["variant"],
                         "monitoringMode": item["monitoringMode"], "offMedianWallMs": off_ms,
                         "modeMedianWallMs": on_ms, "medianDifferenceMs": on_ms - off_ms,
                         "medianDifferencePercent": 100 * (on_ms / off_ms - 1),
                         "allocatedMedianDifferenceBytes": item["allocatedBytes"]["median"] - control["allocatedBytes"]["median"]})
    assessment = {"valid": not errors, "errors": errors, "counts": counts,
        "statistics": "P95 uses linear interpolation at (n-1)*0.95. Independent batch medians are also reported; pooled frames are correlated.",
        "scope": "Uninstrumented baseline wall time is distinct from instrumented step time; original and traced same-backend frames must match. No CPU/GPU pixel equivalence claim."}
    write_json(folder / "assessment.json", assessment)
    write_json(folder / "summary.json", {"assessment": assessment, "groups": summaries, "stages": stage_rows, "bytes": byte_rows,
                                          "monitoringOverhead": overhead})
    for name, rows in [("frames.csv", flat), ("stages.csv", stage_rows), ("bytes.csv", byte_rows)]:
        if rows:
            with (folder / name).open("w", encoding="utf-8", newline="") as stream:
                writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
                writer.writeheader()
                writer.writerows(rows)
    markdown = ["| 场景 | 后端 | 执行体 | 监控模式 | 帧数 | 单帧中位数 ms | P95 ms | 新会话创建中位数 ms | 首帧中位数 ms |", "|---|---|---|---|---:|---:|---:|---:|---:|"]
    for g in summaries:
        s = g["frameWallMs"]
        markdown.append(f"| {g['scenario']} | {g['backend']} | {g['variant']} | {g['monitoringMode']} | {s['count']} | {s['median']:.3f} | {s['p95']:.3f} | {g['createWallMs']['median']:.3f} | {g['firstFrameWallMs']['median']:.3f} |")
    (folder / "summary.md").write_text("\n".join(markdown) + "\n", encoding="utf-8")
    print(json.dumps(assessment, ensure_ascii=False))
    if errors:
        raise SystemExit(1)


def contribution(root, control_label, counterfactual_label):
    """Require an actual pixel contribution for each disabled effect/backend."""
    def read(label):
        folder = root / "runs" / label
        manifest = json.loads((folder / "manifest.json").read_text())
        assessment = json.loads((folder / "assessment.json").read_text())
        if not manifest.get("complete") or not assessment["valid"] or matrix_errors(manifest):
            raise ValueError("contribution requires complete, validated runs: " + label)
        return {(r["round"], r["scenario"], r["backend"], r["variant"]): json.loads((folder / r["result"]).read_text())
                for r in manifest["runs"]}
    controls, disabled = read(control_label), read(counterfactual_label)
    results, errors = [], []
    for key, sample in disabled.items():
        original = controls.get(key)
        if original is None:
            errors.append(str(key) + ": missing control")
            continue
        if original["counterfactual"] != "none" or sample["counterfactual"] == "none":
            errors.append(str(key) + ": invalid effect removal")
        metadata = ("scenario", "backend", "width", "height", "transparent", "mode")
        if any(original[k] != sample[k] for k in metadata):
            errors.append(str(key) + ": input metadata mismatch")
        a, b = original["frames"], sample["frames"]
        if len(a) != len(b) or any(x["timeUs"] != y["timeUs"] or x["pixelBytes"] != y["pixelBytes"] for x, y in zip(a, b)):
            errors.append(str(key) + ": frame sequence mismatch")
        changed = [x["index"] for x, y in zip(a, b) if x["bgraSha256"] != y["bgraSha256"]]
        results.append({"round": key[0], "scenario": key[1], "backend": key[2], "variant": key[3],
                        "removed": sample["counterfactual"], "comparedFrames": len(b), "changedFrames": changed})
        if not changed:
            errors.append(str(key) + ": disabled effect made no pixel contribution")
    assessment = {"valid": not errors, "errors": errors, "control": control_label,
                  "counterfactual": counterfactual_label, "comparisons": results}
    write_json(root / "runs" / counterfactual_label / "contribution.json", assessment)
    print(json.dumps(assessment, ensure_ascii=False))
    if errors:
        raise SystemExit(1)


def compare_trace(root, original_label, traced_label):
    def read(label):
        folder = root / "runs" / label
        manifest = json.loads((folder / "manifest.json").read_text())
        assessment = json.loads((folder / "assessment.json").read_text())
        if not manifest.get("complete") or not assessment["valid"] or matrix_errors(manifest):
            raise ValueError("trace comparison requires complete validated batches: " + label)
        return {(r["round"], r["scenario"], r["backend"], r["variant"]): json.loads((folder / r["result"]).read_text())
                for r in manifest["runs"]}
    originals, traced = read(original_label), read(traced_label)
    errors, matched = [], 0
    if not traced or any(key[3] not in ("old-instrumented", "new-instrumented") for key in traced):
        raise ValueError("trace comparison requires A/B instrumented variants")
    for key, after in traced.items():
        original_key = (*key[:3], key[3].removesuffix("-instrumented"))
        before = originals.get(original_key)
        if before is None:
            errors.append(str(key) + ": original run missing")
            continue
        metadata = ("fixtureSha256", "fixture", "scenario", "backend", "mode", "counterfactual", "width", "height", "transparent", "quality", "requestedWarmupFrames", "requestedMeasuredFrames", "deviceProbe")
        if any(before.get(k) != after.get(k) for k in metadata) or len(before["frames"]) != len(after["frames"]):
            errors.append(str(key) + ": input or device mismatch")
            continue
        if key[2] == "vulkan" and before["process"]["drmDevices"] != after["process"]["drmDevices"]:
            errors.append(str(key) + ": DRM device mismatch")
        fields = ("index", "timeUs", "phase", "bgraSha256", "pixelBytes", "hasError", "diagnosticCount", "imageDiagnosticCount", "materialDiagnosticCount", "diagnosticSha256", "diagnostics")
        for a, b in zip(before["frames"], after["frames"]):
            if any(a[k] != b[k] for k in fields):
                errors.append(str(key) + ": pixels or diagnostics changed at " + str(a["index"]))
            else:
                matched += 1
    assessment = {"valid": not errors, "errors": errors, "originalLabel": original_label,
                  "tracedLabel": traced_label, "matchedFrames": matched}
    write_json(root / "runs" / traced_label / "trace-comparison.json", assessment)
    print(json.dumps(assessment, ensure_ascii=False))
    if errors:
        raise SystemExit(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--label")
    parser.add_argument("--control-label")
    parser.add_argument("--counterfactual-label")
    parser.add_argument("--original-label")
    parser.add_argument("--traced-label")
    args = parser.parse_args()
    if args.label:
        analyze(args.output.resolve(), args.label)
    elif args.control_label and args.counterfactual_label:
        contribution(args.output.resolve(), args.control_label, args.counterfactual_label)
    elif args.original_label and args.traced_label:
        compare_trace(args.output.resolve(), args.original_label, args.traced_label)
    else:
        parser.error("provide --label or both contribution labels")


if __name__ == "__main__":
    main()
