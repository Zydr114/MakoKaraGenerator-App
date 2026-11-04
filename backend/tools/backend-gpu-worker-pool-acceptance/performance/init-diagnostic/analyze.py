#!/usr/bin/env python3
"""Offline original actual-native-creation event verifier; never start a process."""
import argparse
import hashlib
import json
from pathlib import Path


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def arm(directory):
    result = []
    files = sorted(directory.glob("*-*.jsonl"))
    require(files, f"No actual creation events in {directory}")
    for path in files:
        rows = [json.loads(line) for line in path.read_text().splitlines() if line]
        require(rows and [r["sequence"] for r in rows] == list(range(1, len(rows) + 1)), f"Event sequence gap in {path}")
        identity = (rows[0]["pid"], rows[0]["startTicks"])
        require(all((r["pid"], r["startTicks"]) == identity for r in rows), f"Process identity changed in {path}")
        require(path.name == f"{identity[0]}-{identity[1]}.jsonl", f"Identity filename mismatch in {path}")
        devices, contexts = [], []
        device_count = context_count = 0
        for row in rows:
            if row["phase"] == "device-created":
                require(row.get("pairId") and row["pairId"] not in devices, "Missing/reused actual device id")
                devices.append(row["pairId"]); device_count += 1
            if row["phase"] == "context-created":
                require(row.get("pairId") in devices and row["pairId"] not in contexts, "Context without exact original device")
                contexts.append(row["pairId"]); context_count += 1
            require(row["deviceCreations"] == device_count and row["contextCreations"] == context_count, "Counter is not actual successful creation event count")
            if row["phase"] in ("probe-device", "business-device") and row.get("pairId") is not None:
                require(row["pairId"] in contexts, "Selected device lacks original context pair")
        business = []
        for before in [r for r in rows if r["phase"] == "business-before"]:
            matches = [r for r in rows if r["phase"] == "business-after" and r["sessionId"] == before["sessionId"] and r["sequence"] > before["sequence"]]
            require(len(matches) <= 1, "Business session reused or duplicate completion")
            if not matches:
                business.append({"sessionId": before["sessionId"], "status": "business-open-did-not-complete", "before": before})
                continue
            after = matches[0]
            selected = [r for r in rows if r["phase"] == "business-device" and before["sequence"] < r["sequence"] < after["sequence"]]
            require(len(selected) == 1 and selected[0].get("pairId"), "Completed GPU business did not select one real device")
            selection = selected[0]
            previous_probe = [r for r in rows if r["phase"] == "probe-device" and r.get("pairId") == selection["pairId"] and r["sequence"] < before["sequence"]]
            delta_device = after["deviceCreations"] - before["deviceCreations"]
            delta_context = after["contextCreations"] - before["contextCreations"]
            business.append({"sessionId": before["sessionId"], "status": "completed", "before": before, "after": after, "selectedDevice": selection,
                "businessDeviceCreations": delta_device, "businessContextCreations": delta_context,
                "sameOriginalPreviouslyProbedPair": bool(previous_probe), "probeEventsForOriginalPairBeforeBusiness": previous_probe})
        result.append({"path": str(path), "sha256": digest(path), "pid": identity[0], "startTicks": identity[1], "actualDeviceCreations": device_count,
            "actualContextCreations": context_count, "probeEvents": [r for r in rows if r["phase"] == "probe-device"], "business": business, "rawEvents": rows})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-manifest", type=Path, required=True)
    parser.add_argument("--candidate-manifest", type=Path, required=True)
    parser.add_argument("--baseline-events", type=Path, required=True)
    parser.add_argument("--candidate-events", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifests = [json.loads(p.read_text()) for p in (args.baseline_manifest, args.candidate_manifest)]
    require(manifests[0]["toolHash"] == manifests[1]["toolHash"] and manifests[0]["toolFiles"] == manifests[1]["toolFiles"], "Symmetric instrumentation tool SHA mismatch")
    baseline, candidate = arm(args.baseline_events), arm(args.candidate_events)
    old_business = [s for p in baseline for s in p["business"] if s["status"] == "completed"]
    new_business = [s for p in candidate for s in p["business"] if s["status"] == "completed"]
    require(old_business and new_business, "Both sides require completed real business opens")
    # This diagnostic observes actual counts; zero-reinit assertion is specific to
    # the candidate retention mechanism and never assigns a baseline count.
    retained = all(s["businessDeviceCreations"] == 0 and s["businessContextCreations"] == 0 and s["sameOriginalPreviouslyProbedPair"] for s in new_business)
    result = {"schemaVersion": 1, "comparisonClass": "separate-symmetric-actual-initialization-diagnostic", "formalP3Passed": False,
        "candidateBusinessReusesOriginalPairWithoutCreation": retained, "toolHash": manifests[0]["toolHash"], "analyzerSha256": digest(Path(__file__)),
        "manifests": [{"path": str(p), "sha256": digest(p)} for p in (args.baseline_manifest, args.candidate_manifest)],
        "baseline": baseline, "candidate": candidate,
        "boundary": "Actual successful VkDevice/GRContext creation events per exact PID/startTicks, including independent availability processes. No assumed old count, no timing claim, no inference across process identities. Admission/WarmupBinding provenance comes from the matched private driver archive."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"output": str(args.output), "candidateBusinessReusesOriginalPairWithoutCreation": retained}))


if __name__ == "__main__":
    main()
