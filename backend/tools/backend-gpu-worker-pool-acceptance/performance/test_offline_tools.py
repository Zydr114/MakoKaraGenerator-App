#!/usr/bin/env python3
"""Synthetic offline-tool regression only. These fixtures are never GPU or performance evidence."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded

ab = module("finite_ab", HERE / "compare-finite-ab.py")
init = module("init_diagnostic", HERE / "init-diagnostic/analyze.py")

def save(path, value):
    path.write_text(json.dumps(value) + "\n")


def fake_run(path, commit, slow_tail=False):
    path.mkdir()
    manifest = {"runClass": "finite-correctness-not-performance-acceptance", "sourceCommit": commit, "provider": "runtime", "finishedAt": "synthetic-unit-fixture", "connectionOwnership": "owned-isolated-session", "shutdown": {"launcherExitCode": 0, "remaining": [], "observedIdentities": [{"pid": 123, "startTicks": "1"}]}, "deployment": {"verified": True, "files": [{"path": "frontend/dist/index.html", "sha256": "fixture"}, {"path": "desktop/node_modules/electron/package.json", "sha256": "fixture"}]}, "schedulerBaseline": {"explicitOrdinaryScheduling": True}, "platformEvidence": {"waylandConfirmed": True, "renderer": {"visibility": "visible"}}, "warmupSeconds": 2, "measureSeconds": 5, "stabilitySeconds": 5, "readbacks": "none", "sourceSnapshotCurrentOnDisk": {k: "fixture" for k in ab.SOURCE_COMMON}, "fixture": {"audioSha256": "fixture"}, "phases": [{"name": "fixture"}], "platform": "synthetic-test", "arch": "synthetic-test", "kernel": "synthetic-test"}
    records, frames = [], []
    serial = 0
    for kind, count in ab.COUNTS.items():
        for index in range(count):
            serial += 1
            elapsed = 300 if slow_tail and kind == "input-replacement" and index >= 18 else 100
            start = 1_000_000_000 * serial
            frame = dict(zip(ab.LEASE, [f"s-{serial}", "host", "1", "1", "1", "1", "1", 0, "1", "1"]))
            frame.update(drawn=True, provider="runtime", counters={"cpuPixelReadbacks": 0}, completedMonotonicNs=str(start + elapsed * 1_000_000))
            frames.append(frame)
            action = {"type": "finite-action", "kind": kind, "index": index, "uiInvocationMonotonicNs": str(start), "uiInvocationClockUncertaintyMs": 1, **{k: frame[k] for k in ab.STREAM}, "firstMatchingCompletedLease": json.dumps([frame[k] for k in ab.LEASE]), "firstMatchingCompletedMonotonicNs": frame["completedMonotonicNs"], "uiInvocationToMatchingCompletionMs": elapsed, "uiInvocationToMatchingCompletionUpperBoundMs": elapsed + 1, "completionToObservationMs": 10, "workerPid": 123, "workerStartTicks": "1", "previousInputGeneration": "0", "expectedSourceRevision": "fixture"}
            records.append(action)
    for kind, count in (("attach-detach", 20), ("public-resize-seek", 10), ("ui-resolution-recreate", 10)):
        for i in range(count):
            row = {"type": "action", "kind": kind, "index": i, "newGenerationVerified": True, "oldWorkerExited": True, "exactGenerationCompleted": True}
            if kind == "ui-resolution-recreate":
                action = next(r for r in records if r.get("type") == "finite-action" and r["kind"] == kind and r["index"] == i)
                action["previousInputGeneration"] = "1"
                row.update(previousStreamId="retired-stream", nextStreamId=action["streamId"], previousInputGeneration="1", inputGeneration="1", nextWorkerPid=action["workerPid"])
            records.append(row)
    records.append({"type": "snapshot", "snapshot": {"samples": frames}})
    save(path / "manifest.json", manifest)
    save(path / "report.json", {"eventGaps": [], "measuredRequirements": {"ordinarySchedulerBaseline": True}, "phaseReports": [{"schedulerCoverage": {"passed": True}} for _ in range(3)]})
    (path / "events.jsonl").write_text("".join(json.dumps(r) + "\n" for r in records))


def fake_init(path, old):
    path.mkdir()
    rows = []
    device_count = context_count = 0
    def event(phase, pair=None, session=None):
        rows.append({"pid": 123, "startTicks": "4", "sequence": len(rows) + 1, "phase": phase, "pairId": pair, "sessionId": session, "deviceCreations": device_count, "contextCreations": context_count})
    def pair(name):
        nonlocal device_count, context_count
        device_count += 1; event("device-created", name)
        context_count += 1; event("context-created", name)
        event("probe-device", name)
    event("worker-start")
    pair("startup-a")
    if old:
        pair("startup-b")
    event("business-before", session="business")
    selected = "startup-a"
    if old:
        device_count += 1; event("device-created", "business-c")
        context_count += 1; event("context-created", "business-c")
        selected = "business-c"
    event("business-device", selected)
    event("business-after", session="business")
    (path / "123-4.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows))


class OfflineToolTests(unittest.TestCase):
    def test_even_sample_median_uses_two_center_values(self):
        self.assertEqual(2, ab.distribution([1, 3])["median"])
        self.assertEqual(3, ab.distribution([1, 3])["p95"])

    def test_recreated_stream_may_restart_generation_but_not_keep_same_identity(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "run"
            fake_run(path, "b" * 40)
            ab.load_run(path, "b" * 40)
            file = path / "events.jsonl"
            rows = [json.loads(line) for line in file.read_text().splitlines()]
            transition = next(r for r in rows if r.get("type") == "action" and r["kind"] == "ui-resolution-recreate")
            transition["previousStreamId"] = transition["nextStreamId"]
            file.write_text("".join(json.dumps(r) + "\n" for r in rows))
            with self.assertRaisesRegex(ValueError, "identity was not replaced"):
                ab.load_run(path, "b" * 40)

    def test_three_round_pooled_gate_does_not_add_per_round_gate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            plan = {}
            for arm, commit in (("baseline", "b" * 40), ("candidate", "c" * 40)):
                paths = []
                for i in range(3):
                    path = root / f"{arm}-{i}"
                    fake_run(path, commit, slow_tail=arm == "candidate" and i == 0)
                    paths.append(str(path))
                plan[arm] = {"sourceCommit": commit, "runs": paths}
            save(root / "plan.json", plan)
            result = subprocess.run([sys.executable, str(HERE / "compare-finite-ab.py"), "--plan", str(root / "plan.json"), "--output", str(root / "result.json")], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            output = json.loads((root / "result.json").read_text())
            self.assertTrue(output["finiteTimingPassed"])
            self.assertFalse(output["formalP3Passed"])
            row = output["comparisons"]["input-replacement"]
            self.assertEqual(300, row["roundObservationsNoSeparateHardGate"][0]["candidate"]["rawMs"]["p95"])
            self.assertEqual(100, row["pooled"]["candidate"]["rawMs"]["p95"])

    def test_endpoint_must_be_earliest_original_drawn_completion(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "run"
            fake_run(path, "b" * 40)
            file = path / "events.jsonl"
            rows = [json.loads(line) for line in file.read_text().splitlines()]
            earlier = dict(rows[-1]["snapshot"]["samples"][0])
            earlier["frameSeq"] = "2"
            earlier["completedMonotonicNs"] = str(int(earlier["completedMonotonicNs"]) - 1)
            rows[-1]["snapshot"]["samples"].append(earlier)
            file.write_text("".join(json.dumps(r) + "\n" for r in rows))
            with self.assertRaisesRegex(ValueError, "earliest"):
                ab.load_run(path, "b" * 40)

    def test_missing_generation_and_original_identity_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "run"
            fake_run(path, "b" * 40)
            file = path / "events.jsonl"
            rows = [json.loads(line) for line in file.read_text().splitlines()]
            rows[0]["previousInputGeneration"] = rows[0]["inputGeneration"]
            file.write_text("".join(json.dumps(r) + "\n" for r in rows))
            with self.assertRaisesRegex(ValueError, "generation was not replaced"):
                ab.load_run(path, "b" * 40)

    def test_hard_gate_still_fails_when_pooled_tail_regresses(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            plan = {}
            for name, commit in (("baseline", "b" * 40), ("candidate", "c" * 40)):
                paths = []
                for i in range(3):
                    path = root / f"{name}-{i}"
                    fake_run(path, commit, slow_tail=name == "candidate")
                    paths.append(str(path))
                plan[name] = {"sourceCommit": commit, "runs": paths}
            save(root / "plan.json", plan)
            result = subprocess.run([sys.executable, str(HERE / "compare-finite-ab.py"), "--plan", str(root / "plan.json"), "--output", str(root / "result.json")], capture_output=True, text=True)
            self.assertEqual(1, result.returncode, result.stderr)
            self.assertFalse(json.loads((root / "result.json").read_text())["finiteTimingPassed"])

    def test_common_runner_hash_mismatch_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            plan = {}
            for name, commit in (("baseline", "b" * 40), ("candidate", "c" * 40)):
                paths = []
                for i in range(3):
                    path = root / f"{name}-{i}"; fake_run(path, commit); paths.append(str(path))
                plan[name] = {"sourceCommit": commit, "runs": paths}
            target = Path(plan["candidate"]["runs"][0]) / "manifest.json"
            manifest = json.loads(target.read_text()); manifest["sourceSnapshotCurrentOnDisk"]["scripts/gpu-preview-p3.mjs"] = "different"
            save(target, manifest); save(root / "plan.json", plan)
            result = subprocess.run([sys.executable, str(HERE / "compare-finite-ab.py"), "--plan", str(root / "plan.json"), "--output", str(root / "result.json")], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("differs across", result.stderr)
            self.assertFalse((root / "result.json").exists())

    def test_symmetric_init_counts_actual_old_three_and_new_one(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            fake_init(root / "old", True); fake_init(root / "new", False)
            old, new = init.arm(root / "old")[0], init.arm(root / "new")[0]
            self.assertEqual(3, old["actualDeviceCreations"])
            self.assertEqual(1, old["business"][0]["businessDeviceCreations"])
            self.assertEqual(1, new["actualDeviceCreations"])
            self.assertEqual(0, new["business"][0]["businessDeviceCreations"])
            self.assertTrue(new["business"][0]["sameOriginalPreviouslyProbedPair"])

    def test_init_counter_cannot_claim_unrecorded_actual_creation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "old"; fake_init(root, True)
            file = root / "123-4.jsonl"
            rows = [json.loads(line) for line in file.read_text().splitlines()]
            rows[-1]["deviceCreations"] += 1
            file.write_text("".join(json.dumps(r) + "\n" for r in rows))
            with self.assertRaisesRegex(ValueError, "actual successful creation"):
                init.arm(root)


if __name__ == "__main__":
    unittest.main(verbosity=2)
