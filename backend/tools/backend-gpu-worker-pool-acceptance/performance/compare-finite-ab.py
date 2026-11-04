#!/usr/bin/env python3
"""Offline only. Compare exactly three matched finite runs per arm; launch nothing."""
import argparse
import hashlib
import json
import math
import statistics
from pathlib import Path

STREAM = ("streamId", "hostInstanceId", "producerEpoch", "inputGeneration", "consumerEpoch", "poolGeneration")
LEASE = ("streamId", "hostInstanceId", "producerEpoch", "inputGeneration", "consumerEpoch", "timelineEpoch", "poolGeneration", "slotId", "slotEpoch", "frameSeq")
COUNTS = {"input-replacement": 20, "attach": 20, "detach-reattach": 20, "ui-resolution-recreate": 10, "public-resize-seek": 10}
GATED_KINDS = ("input-replacement", "attach", "ui-resolution-recreate", "public-resize-seek")
SOURCE_COMMON = ("scripts/gpu-preview-p3.mjs", "scripts/app.mjs", "desktop/gpu-preview/ordinary-exec.cjs", "desktop/main.cjs", "desktop/gpu-preview/preload.cjs", "frontend/src/editor/preview/PreviewPanel.tsx", "frontend/src/editor/preview/gpu-preview-controller.ts")


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text())


def ns(value):
    require(isinstance(value, str) and value.isdecimal() and str(int(value)) == value, "Noncanonical monotonic timestamp")
    return int(value)


def rank(values, q):
    return sorted(values)[max(0, math.ceil(len(values) * q) - 1)]


def distribution(values):
    require(values and all(isinstance(v, (int, float)) and math.isfinite(v) and v >= 0 for v in values), "Missing/invalid duration")
    return {"count": len(values), "min": min(values), "median": statistics.median(values), "p95": rank(values, .95), "max": max(values)}


def load_run(path, expected_commit):
    manifest, report = read(path / "manifest.json"), read(path / "report.json")
    records = [json.loads(line) for line in (path / "events.jsonl").read_text().splitlines() if line.strip()]
    require(manifest.get("runClass") == "finite-correctness-not-performance-acceptance", f"{path}: expected finite runClass")
    require(manifest.get("sourceCommit") == expected_commit, f"{path}: unexpected source commit")
    require(manifest.get("provider") == "runtime", f"{path}: expected real runtime provider")
    require(manifest.get("finishedAt") and "runError" not in manifest, f"{path}: unfinished/failed run")
    require(manifest.get("connectionOwnership") == "owned-isolated-session", f"{path}: expected owned isolated process")
    shutdown = manifest.get("shutdown", {})
    require(shutdown.get("launcherExitCode") == 0 and "error" not in shutdown and shutdown.get("remaining") == [], f"{path}: missing exact process drain")
    require(shutdown.get("observedIdentities"), f"{path}: no owned PID/startTicks inventory")
    require(manifest.get("deployment", {}).get("verified") is True, f"{path}: unmatched deployment")
    require(manifest.get("schedulerBaseline", {}).get("explicitOrdinaryScheduling") is True, f"{path}: ordinary scheduler flag missing")
    require(manifest.get("platformEvidence", {}).get("waylandConfirmed") is True and manifest.get("platformEvidence", {}).get("renderer", {}).get("visibility") == "visible", f"{path}: real visible Wayland evidence missing")
    require(manifest.get("warmupSeconds") == 2 and manifest.get("measureSeconds") == 5 and manifest.get("stabilitySeconds") == 5, f"{path}: finite scenario differs")
    require(len(report.get("phaseReports", [])) == 3, f"{path}: finite phase matrix incomplete")
    require(not report.get("eventGaps"), f"{path}: incomplete completion event history")
    require(report.get("measuredRequirements", {}).get("ordinarySchedulerBaseline") is True, f"{path}: ordinary pipeline scheduler evidence failed")
    require("one validation-only" not in manifest.get("readbacks", ""), f"{path}: screenshot run cannot enter timing comparison")
    for phase in report.get("phaseReports", []):
        require(phase.get("schedulerCoverage", {}).get("passed") is True, f"{path}: sampled phase pipeline scheduler mismatch")
    samples = {}
    for record in records:
        if record.get("type") != "snapshot":
            continue
        snapshot = record["snapshot"]
        require(not snapshot.get("sampleCursorExpired"), f"{path}: completion cursor expired")
        for frame in snapshot.get("samples", []):
            key = tuple(frame.get(k) for k in LEASE)
            require(None not in key, f"{path}: incomplete frame lease")
            before = samples.get(key)
            require(not before or before["completedMonotonicNs"] == frame["completedMonotonicNs"], f"{path}: changed original completion timestamp")
            samples[key] = {**(before or {}), **frame, "drawn": frame.get("drawn") is True or (before or {}).get("drawn") is True}
    actions = [record for record in records if record.get("type") == "finite-action"]
    require(len(actions) == sum(COUNTS.values()), f"{path}: extra/missing finite action records")
    groups = {}
    for kind, count in COUNTS.items():
        group = sorted([a for a in actions if a.get("kind") == kind], key=lambda a: a["index"])
        require([a["index"] for a in group] == list(range(count)), f"{path}: {kind} missing/duplicate index")
        for action in group:
            require(all(action.get(k) is not None for k in STREAM), f"{path}: incomplete action stream identity")
            start = ns(action["uiInvocationMonotonicNs"])
            matching = sorted((f for f in samples.values() if f.get("drawn") is True and all(f[k] == action[k] for k in STREAM) and ns(f["completedMonotonicNs"]) >= start and ("expectedTimelineResetId" not in action or f.get("timelineResetId") == action["expectedTimelineResetId"])), key=lambda f: ns(f["completedMonotonicNs"]))
            require(matching, f"{path}: {kind} has no original exact drawn/native completed sample")
            first = matching[0]
            require(first.get("provider") == "runtime" and first.get("counters", {}).get("cpuPixelReadbacks") == 0, f"{path}: measured completion has wrong provider or nonzero/unmeasured CPU pixel readbacks")
            if "previousInputGeneration" in action:
                if kind == "ui-resolution-recreate":
                    transitions = [r for r in records if r.get("type") == "action" and r.get("kind") == kind and r.get("index") == action["index"]]
                    require(len(transitions) == 1, f"{path}: missing/duplicate resolution transition")
                    transition = transitions[0]
                    require(transition.get("nextStreamId") == action["streamId"] and transition.get("inputGeneration") == action["inputGeneration"] and transition.get("previousInputGeneration") == action["previousInputGeneration"] and transition.get("nextWorkerPid") == action["workerPid"], f"{path}: resolution timing/transition identity mismatch")
                    require(transition.get("previousStreamId") and (transition["previousStreamId"], transition["previousInputGeneration"]) != (action["streamId"], action["inputGeneration"]), f"{path}: resolution stream/input identity was not replaced")
                else:
                    require(action["previousInputGeneration"] != action["inputGeneration"], f"{path}: generation was not replaced")
            if kind == "input-replacement":
                require(action.get("expectedSourceRevision") is not None, f"{path}: no authoritative returned source revision")
            require(json.loads(action["firstMatchingCompletedLease"]) == [first[k] for k in LEASE] and action["firstMatchingCompletedMonotonicNs"] == first["completedMonotonicNs"], f"{path}: {kind} endpoint is not earliest original matching event")
            elapsed = (ns(first["completedMonotonicNs"]) - start) / 1e6
            uncertainty = action.get("uiInvocationClockUncertaintyMs")
            require(isinstance(uncertainty, (int, float)) and math.isfinite(uncertainty) and uncertainty >= 0, f"{path}: missing clock uncertainty")
            require(abs(action["uiInvocationToMatchingCompletionMs"] - elapsed) < 1e-6 and abs(action["uiInvocationToMatchingCompletionUpperBoundMs"] - elapsed - uncertainty) < 1e-6, f"{path}: derived duration mismatch")
            require(action.get("workerPid", 0) > 0 and str(action.get("workerStartTicks", "")).isdecimal(), f"{path}: missing new Worker identity")
        groups[kind] = group
    for kind, count, predicate in (("attach-detach", 20, lambda a: a.get("newGenerationVerified") is True and a.get("oldWorkerExited") is True), ("public-resize-seek", 10, lambda a: a.get("exactGenerationCompleted") is True), ("ui-resolution-recreate", 10, lambda a: a.get("exactGenerationCompleted") is True and a.get("oldWorkerExited") is True)):
        rows = [r for r in records if r.get("type") == "action" and r.get("kind") == kind]
        require(len(rows) == count and all(predicate(a) for a in rows), f"{path}: {kind} correctness assertions incomplete")
    common = {"sources": {name: manifest["sourceSnapshotCurrentOnDisk"][name] for name in SOURCE_COMMON}, "frontendAndElectron": sorted((f["path"], f["sha256"]) for f in manifest["deployment"]["files"] if f["path"].startswith("frontend/dist/") or f["path"] == "desktop/node_modules/electron/package.json"), "audioSha256": manifest.get("fixture", {}).get("audioSha256"), "phases": manifest.get("phases"), "platform": [manifest.get(k) for k in ("platform", "arch", "kernel")]}
    require(common["audioSha256"] and common["frontendAndElectron"] and all(common["sources"].values()), f"{path}: incomplete common workload/tool provenance")
    return {"path": str(path), "sourceCommit": expected_commit, "inputFiles": {name: sha(path / name) for name in ("manifest.json", "events.jsonl", "report.json")}, "deployment": manifest["deployment"], "common": common, "groups": groups}


def summarize(rows):
    return {"rawMs": distribution([a["uiInvocationToMatchingCompletionMs"] for a in rows]), "withClockUncertaintyUpperBoundMs": distribution([a["uiInvocationToMatchingCompletionUpperBoundMs"] for a in rows]), "pollObservationLagMs": distribution([a["completionToObservationMs"] for a in rows]), "samples": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    plan = read(args.plan)
    arms = {}
    for arm in ("baseline", "candidate"):
        item = plan[arm]
        require(len(item["runs"]) == 3, "Exactly three runs per arm are required")
        arms[arm] = [load_run(Path(p).resolve(), item["sourceCommit"]) for p in item["runs"]]
    all_runs = arms["baseline"] + arms["candidate"]
    require(len({r["path"] for r in all_runs}) == 6, "Six unique run directories are required")
    common = all_runs[0]["common"]
    require(all(r["common"] == common for r in all_runs), "Runner/preload/main/frontend/Electron/fixture/platform differs across the six runs")
    comparisons = {}
    for kind in COUNTS:
        rounds = [{arm: summarize(arms[arm][i]["groups"][kind]) for arm in arms} for i in range(3)]
        pooled = {arm: summarize([a for r in arms[arm] for a in r["groups"][kind]]) for arm in arms}
        # The approved hard gate is pooled three-round data, never a new per-round gate.
        gates = {}
        for metric in ("median", "p95") if kind in GATED_KINDS else ():
            baseline = pooled["baseline"]["rawMs"][metric]
            candidate = pooled["candidate"]["rawMs"][metric]
            gates[metric] = {"baselineMs": baseline, "candidateMs": candidate, "limitMs": baseline * 1.10 + 5, "passed": candidate <= baseline * 1.10 + 5}
        comparisons[kind] = {"pooled": pooled, "roundObservationsNoSeparateHardGate": rounds, "pooledGates": gates}
    passed = all(g["passed"] for result in comparisons.values() for g in result["pooledGates"].values())
    result = {"schemaVersion": 1, "comparisonClass": "approved-finite-three-round-action-ab", "formalP3Passed": False, "finiteTimingPassed": passed, "hardwareInitializationProof": "separate symmetric diagnostic archives required; never inferred from timing", "threshold": "Three rounds pooled per gated action kind: candidate median/p95 <= baseline * 1.10 + 5 ms; statistics.median and nearest-rank p95. Clock uncertainty bounds are reported separately.", "gatedKinds": GATED_KINDS, "observationOnlyKinds": ["detach-reattach"], "boundary": "CDP invocation -> first exact six-identity canvas-drawn original native allReferencesReleased completion; polling return excluded. Combined detach-reattach additionally includes the explicit old PID drain wait and is observational only.", "common": common, "comparatorSha256": sha(Path(__file__)), "planSha256": sha(args.plan), "runs": {arm: [{k: v for k, v in r.items() if k not in ("groups", "common")} for r in runs] for arm, runs in arms.items()}, "comparisons": comparisons}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
    print(json.dumps({"output": str(args.output), "finiteTimingPassed": passed, "formalP3Passed": False}))
    return 0 if passed else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, TypeError) as error:
        raise SystemExit(f"Finite A/B evidence rejected: {error}")
