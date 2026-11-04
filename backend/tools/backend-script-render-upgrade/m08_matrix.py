#!/usr/bin/env python3
"""Sample the three real M08 packages in nine fresh, strictly serial processes; compare v2 determinism."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import statistics
import subprocess
import time

SAMPLES = ("CursorTrail", "VectorTrail", "EventBurst")


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def save(path, value):
    # Every matrix owns a newly created directory. Avoid publishing a truncated report on interruption.
    pending = path.with_name(path.name + ".pending")
    with pending.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")
    pending.replace(path)


def command_output(command):
    try:
        result = subprocess.run(command, text=True, capture_output=True, check=False, timeout=30)
        return {"command": command, "exitCode": result.returncode, "stdout": result.stdout, "stderr": result.stderr}
    except (OSError, subprocess.TimeoutExpired) as error:
        return {"command": command, "error": str(error)}


def device_load():
    result = {"cpuLoad": os.getloadavg() if hasattr(os, "getloadavg") else None, "gpuBusy": {}}
    for path in sorted(Path("/sys/class/drm").glob("card*/device/gpu_busy_percent")):
        try:
            result["gpuBusy"][str(path)] = path.read_text().strip()
        except OSError as error:
            result["gpuBusy"][str(path)] = {"error": str(error)}
    return result


def font_identity():
    result = command_output(["fc-match", "-f", "%{family}\n%{file}\n", "Noto Sans CJK SC"])
    lines = result.get("stdout", "").splitlines()
    if result.get("exitCode") == 0 and len(lines) >= 2:
        font = Path(lines[1])
        if font.is_file():
            result["matchedFileSha256"] = sha(font)
    result["scope"] = "fontconfig requested-family match; not an instrumentation of every Skia fallback glyph."
    return result


def stats(values, unit):
    ordered = sorted(values)
    return {"n": len(values), "unit": unit, "median": statistics.median(values),
            "p95": ordered[math.ceil(len(ordered) * .95) - 1], "minimum": ordered[0], "maximum": ordered[-1]}


def validate_report(output, record):
    path = output / record["file"]
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["schema"] != "mako-sdk-m08-finite-v1" or report["status"] != "completed" or report["sample"] != record["sample"]:
        raise RuntimeError(f"Unexpected M08 report identity: {path.name}")
    fixture = report["fixture"]
    if (fixture["id"] != "m08-two-token-cjk-ruby-v1" or fixture["repeats"] != record["repeats"]
            or not fixture["timesUs"] or fixture["timesUs"] != sorted(set(fixture["timesUs"]))
            or [order["name"] for order in fixture["orders"]] != ["ascending", "shuffled"]
            or any(sorted(order["timesUs"]) != fixture["timesUs"] for order in fixture["orders"])
            or fixture["orders"][0]["timesUs"] != fixture["timesUs"]):
        raise RuntimeError(f"Unexpected or empty M08 fixture/query matrix: {path.name}")
    expected_packages = {"CursorTrail": "makokara.v2.cursor-trail", "VectorTrail": "makokara.v2.vector-trail", "EventBurst": "makokara.v2.event-burst"}
    if report["PackageId"] != expected_packages[record["sample"]] or not report["binarySha256"]:
        raise RuntimeError(f"Unexpected package or empty binary provenance: {path.name}")
    assets = output / (path.name + ".assets")
    if sha(assets / report["fixture"]["asset"]) != report["fixture"]["sha256"]:
        raise RuntimeError(f"Fixture asset hash differs: {path.name}")
    for stage, key, asset_key in (("preparation", "sha256", "canonicalAsset"), ("rendering", "bgraSha256", "pixelAsset")):
        checked = {}
        rows = report[stage]
        for order in report["fixture"]["orders"]:
            for cycle in range(report["fixture"]["repeats"]):
                selected = [row for row in rows if row["order"] == order["name"] and row["cycle"] == cycle]
                if [row["index"] for row in selected] != list(range(len(order["timesUs"]))) or [row["timeUs"] for row in selected] != order["timesUs"]:
                    raise RuntimeError(f"Incomplete query order: {path.name}/{stage}/{order['name']}/{cycle}")
        expected_count = len(report["fixture"]["timesUs"]) * len(report["fixture"]["orders"]) * report["fixture"]["repeats"]
        if len(rows) != expected_count:
            raise RuntimeError(f"Unexpected query count: {path.name}/{stage}")
        for row in rows:
            asset = row[asset_key]
            if asset not in checked:
                checked[asset] = sha(assets / asset)
            if checked[asset] != row[key]:
                raise RuntimeError(f"Evidence asset hash differs: {path.name}/{asset}")
    active_offset = 0 if record["sample"] == "EventBurst" else 750_000
    active_times = [start + active_offset for start in (0, 180_000_000, 600_000_000)]
    # This is fixture coverage, not a performance threshold: deterministic empty output cannot stand in for particles.
    if any(not any(row["timeUs"] == active and row["births"] > 0 and row["alive"] > 0 for row in report["preparation"])
           for active in active_times):
        raise RuntimeError(f"The real sample produced no live particle coverage at an active fixture time: {path.name}")
    return report


def summarize(reports, input_binary_hashes):
    determinism, timings = [], []
    same_input = True
    for sample in SAMPLES:
        group = [reports[(round_index, sample)] for round_index in range(1, 4)]
        first = group[0]
        provenance = ("product", "Profile", "PackageId", "Revision", "Id", "manifestSha256", "binarySha256", "Identity", "ReferenceIdentity")
        for round_index, report in enumerate(group, 1):
            same = (all(report[key] == first[key] for key in provenance)
                    and report["fixture"]["sha256"] == first["fixture"]["sha256"]
                    and report["binarySha256"] == input_binary_hashes)
            same_input &= same
            determinism.append({"sample": sample, "round": round_index, "kind": "same-input-and-binaries", "equal": same})
            differences, compared = [], 0
            # Derive comparisons from validated frame records. Never trust an empty or incorrect comparison summary.
            for stage, key in (("preparation", "sha256"), ("rendering", "bgraSha256")):
                baseline = {row["timeUs"]: row[key] for row in report[stage] if row["order"] == "ascending" and row["cycle"] == 0}
                candidates = [row for row in report[stage] if row["order"] != "ascending" or row["cycle"] != 0]
                compared += len(candidates)
                differences.extend({"stage": stage, "order": row["order"], "cycle": row["cycle"], "index": row["index"],
                                    "timeUs": row["timeUs"], "expected": baseline.get(row["timeUs"]), "actual": row[key]}
                                   for row in candidates if baseline.get(row["timeUs"]) != row[key])
            determinism.append({"sample": sample, "round": round_index, "kind": "within-process-orders-and-repeats",
                                "equal": compared > 0 and not differences, "compared": compared, "differences": differences})
        for stage, key in (("preparation", "sha256"), ("rendering", "bgraSha256")):
            baseline = {row["timeUs"]: row[key] for row in first[stage] if row["order"] == "ascending" and row["cycle"] == 0}
            for round_index, report in enumerate(group[1:], 2):
                differences = [{"order": row["order"], "cycle": row["cycle"], "index": row["index"], "timeUs": row["timeUs"],
                                "expected": baseline.get(row["timeUs"]), "actual": row[key]}
                               for row in report[stage] if baseline.get(row["timeUs"]) != row[key]]
                determinism.append({"sample": sample, "round": round_index, "kind": "cross-process", "stage": stage,
                                    "equal": not differences, "compared": len(report[stage]), "differences": differences})
        setup = {}
        for key in ("packageLoadMs", "compilerCreationMs", "compileMs"):
            setup[key] = stats([report[key] for report in group], "ms")
        for key in ("packageLoadAllocatedBytes", "compilerCreationAllocatedBytes", "compileAllocatedBytes"):
            setup[key] = stats([report[key] for report in group], "managed process bytes")
        sessions, queries = [], []
        for stage in ("preparation", "rendering"):
            for order in first["fixture"]["orders"]:
                name = order["name"]
                session_rows = [row for report in group for row in report[stage + "Sessions"] if row["order"] == name]
                sessions.append({"stage": stage, "order": name, "setupMs": stats([row["setupMs"] for row in session_rows], "ms"),
                                 "setupAllocatedBytes": stats([row["processAllocatedBytes"] for row in session_rows], "managed process bytes"),
                                 "disposeMs": stats([row["disposeMs"] for row in session_rows], "ms")})
                for index, time_us in enumerate(order["timesUs"]):
                    for query_kind in ("first-in-session", "repeat-in-session"):
                        rows = [(round_index, row) for round_index, report in enumerate(group, 1) for row in report[stage]
                                if row["order"] == name and row["index"] == index
                                and ((row["cycle"] == 0) == (query_kind == "first-in-session"))]
                        if not rows:
                            continue
                        query = {"stage": stage, "order": name, "index": index, "timeUs": time_us, "queryKind": query_kind,
                                 "wallMs": stats([row["wallMs"] for _, row in rows], "ms"),
                                 "allocation": stats([row["processAllocatedBytes"] for _, row in rows], "managed process bytes")}
                        if stage == "preparation":
                            query["work"] = [{"round": round_index, "cycle": row["cycle"],
                                              **{key: row[key] for key in ("geometrySceneCalls", "geometryLineCalls", "births", "alive", "trailNodes", "anchor", "overlayItems")}}
                                             for round_index, row in rows]
                        queries.append(query)
        timings.append({"sample": sample, "setup": setup, "sessions": sessions, "queries": queries})
    equal = all(row["equal"] for row in determinism)
    return {"schema": "mako-sdk-m08-matrix-v1", "status": "completed", "rounds": 3, "processes": 9,
            "sameInputAndBinaries": same_input, "determinismStatus": "equal" if equal else "different",
            "determinism": determinism, "timings": timings, "performanceAcceptance": "not assessed; raw finite sampling only",
            "scope": "Only v2 CursorTrail/VectorTrail/EventBurst, fixed two-token CJK/Ruby fixture, same built binaries and ICD. Independent serial ascending/shuffled sessions and repeated requests; three fresh processes per sample. Exact structural and Vulkan pixel SHA256 comparisons. No v1 parity, performance pass threshold, OS/driver/font-cache reset, concurrency load, HTTP/Worker/UI or shared texture acceptance."}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--output", type=Path, required=True)
    cli.add_argument("--runner", type=Path, required=True)
    cli.add_argument("--backend", type=Path, required=True)
    cli.add_argument("--source-commit", required=True)
    cli.add_argument("--icd", type=Path, required=True)
    cli.add_argument("--repeats", type=int, choices=range(1, 7), default=2)
    args = cli.parse_args()
    runner, backend, icd = args.runner.resolve(strict=True), args.backend.resolve(strict=True), args.icd.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    environment = {**os.environ, "VK_DRIVER_FILES": str(icd), "VK_ICD_FILENAMES": str(icd),
                   "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"}
    binary_hashes = {str(path.relative_to(runner.parent)): sha(path) for path in sorted(runner.parent.rglob("*.dll"))}
    save(output / "inputs.json", {"schema": "mako-sdk-m08-inputs-v1", "sourceCommit": args.source_commit,
         "gitHead": command_output(["git", "-C", str(backend), "rev-parse", "HEAD"]),
         "gitStatus": command_output(["git", "-C", str(backend), "status", "--porcelain", "--untracked-files=normal"]),
         "runner": str(runner), "backend": str(backend), "binarySha256": binary_hashes,
         "icd": str(icd), "icdSha256": sha(icd), "font": font_identity(), "platform": platform.platform(),
         "python": platform.python_version(), "dotnetInfo": command_output(["dotnet", "--info"]),
         "environmentOverrides": {key: environment[key] for key in ("VK_DRIVER_FILES", "VK_ICD_FILENAMES", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO")},
         "loadStart": device_load(), "repeats": args.repeats,
         "scope": "Already-built binaries; no build/install/server. Three fresh processes per sample, serial. Requested source commit is a caller label; git head/status and exact DLL hashes are retained. No OS or GPU cache reset."})
    runs = []
    for round_index in range(1, 4):
        # Rotate sample order; no sample overlaps another process.
        order = SAMPLES[round_index - 1:] + SAMPLES[:round_index - 1]
        for sample in order:
            name = f"r{round_index}-v2-{sample}"
            path = output / (name + ".json")
            command = ["dotnet", str(runner), str(backend), sample, str(path), str(args.repeats)]
            record = {"round": round_index, "sample": sample, "file": path.name, "command": command, "repeats": args.repeats,
                      "loadBefore": device_load(), "startUnix": time.time()}
            print("RUN " + name, flush=True)
            with (output / (name + ".log")).open("x", encoding="utf-8") as log:
                with subprocess.Popen(command, env=environment, stdout=log, stderr=subprocess.STDOUT) as process:
                    record["pid"] = process.pid
                    stat_path = Path(f"/proc/{process.pid}/stat")
                    try:
                        # The comm field can contain spaces or parentheses; starttime is field 22.
                        record["linuxStartTicks"] = stat_path.read_text().rsplit(")", 1)[1].split()[19]
                    except (OSError, IndexError):
                        record["linuxStartTicks"] = None
                    save(output / (name + ".process.json"), record)
                    try:
                        record["exitCode"] = process.wait(timeout=960)
                    except subprocess.TimeoutExpired:
                        process.kill()  # Only the child process owned by this invocation.
                        record.update(exitCode=process.wait(), timedOut=True)
            record.update(endUnix=time.time(), loadAfter=device_load())
            runs.append(record)
            save(output / "runs.json", runs)
            if record["exitCode"]:
                save(output / "summary.json", {"status": "incomplete", "failedRun": name, "runs": len(runs),
                     "performanceAcceptance": "not assessed", "scope": "Failed process retains log/pending/assets; no complete matrix claim."})
                return 2
    try:
        reports = {(record["round"], record["sample"]): validate_report(output, record) for record in runs}
        process_ids = [report["processId"] for report in reports.values()]
        process_identities = {(report["processId"], report["processStartUtc"]) for report in reports.values()}
        if len(process_ids) != 9 or len(process_identities) != 9:
            raise RuntimeError("Expected nine independently started process identities.")
        result = summarize(reports, binary_hashes)
    except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        save(output / "summary.json", {"status": "incomplete", "runs": len(runs), "validationError": str(error),
             "performanceAcceptance": "not assessed", "scope": "Invalid/incomplete evidence is not determinism acceptance."})
        return 2
    save(output / "summary.json", result)
    print(json.dumps({"status": result["status"], "processes": len(runs), "determinismStatus": result["determinismStatus"],
                      "performanceAcceptance": result["performanceAcceptance"]}, ensure_ascii=False))
    return 0 if result["determinismStatus"] == "equal" else 1


if __name__ == "__main__":
    raise SystemExit(main())
