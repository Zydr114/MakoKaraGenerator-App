#!/usr/bin/env python3
"""Exercise analysis gates using synthetic results, without rendering."""

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock
from types import SimpleNamespace

import analyze
import bench


def write_json(path, value):
    path.write_text(json.dumps(value), encoding="utf-8")


def stage(name, inclusive=1.0, exclusive=1.0):
    return {"name": name, "calls": 1, "inclusiveMs": inclusive,
            "exclusiveMs": exclusive, "minMs": inclusive, "maxMs": inclusive}


def trace(name):
    return {"valid": True, "errors": [], "spanCount": 1, "stages": [stage(name)], "bytes": {"device.imageReadback.requested": 16}}


def sample(backend, variant, counterfactual="none", pixel="a", frame_count=2, monitoring_mode="off",
           monitoring_file=None, monitoring_lines=0):
    instrumented = variant == "instrumented" or variant.endswith("-instrumented")
    frames = []
    for index in range(frame_count):
        frames.append({
            "index": index, "timeUs": 1_000_000 + index * 33_333,
            "phase": "cold" if index == 0 else "steady", "wallMs": 1.0,
            "allocatedBytes": 0, "gcCollections": [0, 0, 0],
            "bgraSha256": pixel * 64, "pixelBytes": 16, "hasError": False,
            "diagnosticCount": 0, "imageDiagnosticCount": 0, "materialDiagnosticCount": 0,
            "diagnosticSha256": "d" * 64, "diagnostics": {"runtime": [], "image": [], "material": []},
            "trace": trace("render") if instrumented else None,
        })
    return {
        "scenario": "material", "backend": backend, "counterfactual": counterfactual,
        "monitoringMode": monitoring_mode, "monitoringFile": monitoring_file, "monitoringLines": monitoring_lines,
        "monitoringConfiguration": {"mode": monitoring_mode, "detailEvery": 1 if monitoring_mode == "detailed" else 0,
                                    "intervalSeconds": 10, "maxEventsPerSecond": 10},
        "width": 2, "height": 2, "transparent": False, "mode": "sequential",
        "fixtureSha256": "f" * 64, "projectFrameCount": frame_count, "deviceProbeFrames": 1,
        "process": {"pid": 42, "startTicks": 123,
                    "mappedLibraries": ["libvulkan.so", "libvulkan_radeon.so"] if backend == "vulkan" else [],
                    "drmDevices": ["/dev/dri/renderD128"] if backend == "vulkan" else []},
        "deviceProbe": {"backend": backend}, "traceAvailable": instrumented,
        "create": {"wallMs": 1.0, "trace": trace("create") if instrumented else None},
        "disposeMs": 1.0, "frames": frames,
    }


class AnalysisTests(unittest.TestCase):
    def test_timeout_terminates_owned_session_and_child(self):
        program = ("import subprocess,sys,time; "
                   "p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); "
                   "print(p.pid,flush=True); time.sleep(60)")
        process = subprocess.Popen([sys.executable, "-c", program], stdout=subprocess.PIPE,
                                   text=True, start_new_session=True)
        try:
            child_pid = int(process.stdout.readline())
            identity = bench.child_identity(process.pid)
            self.assertIsNotNone(identity["startTicks"])
            observed = {}
            self.assertTrue(bench.observe_owned_processes(identity, observed))
            cleanup = {}
            outcomes = bench.terminate_owned_group(process, identity, observed, cleanup)
            self.assertEqual(cleanup["signal"], "SIGKILL")
            child = next(item for item in outcomes if item["observed"]["pid"] == child_pid)
            self.assertIn(child["status"], {"exited", "zombie", "pid-reused"})
            self.assertEqual(child["observed"]["processGroup"], process.pid)
            self.assertEqual(child["observed"]["session"], process.pid)
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                stat = Path(f"/proc/{child_pid}/stat")
                if not stat.exists() or stat.read_text().rsplit(")", 1)[1].split()[0] == "Z":
                    break
                time.sleep(.01)
            else:
                self.fail("child from owned process group remained live")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, 9)
                process.wait(timeout=5)
            process.stdout.close()

    def test_cleanup_identity_failure_is_written_before_raising(self):
        (self.root / "prepare.json").write_text(json.dumps({"toolCommit": "test"}))
        (self.root / "build-inputs.json").write_text("{}")
        args = SimpleNamespace(output=str(self.root), label="failed", warmup=0, frames=1, rounds=1,
            scenarios="lyrics", variants="baseline", action="run", mode="sequential", times_us=None,
            width=2, height=2, transparent=False, counterfactual="none", timeout=1, pause=0)
        process = mock.Mock(pid=123, args=["runner"])
        with mock.patch.object(bench.subprocess, "Popen", return_value=process), \
             mock.patch.object(bench, "child_identity", return_value={"pid": 123, "startTicks": None}), \
             mock.patch.object(bench, "wait_observing", side_effect=subprocess.TimeoutExpired("runner", 1)), \
             mock.patch.object(bench, "terminate_owned_group", side_effect=RuntimeError("identity unavailable")), \
             contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(RuntimeError, "identity unavailable"):
                bench.run(args)
        result = json.loads((self.root / "runs/failed/manifest.json").read_text())
        self.assertFalse(result.get("complete", False))
        record = result["runs"][0]
        self.assertEqual(record["exitCode"], -999)
        self.assertIn("identity unavailable", record["timeoutCleanup"]["cleanupError"])

    def test_backend_archive_tree_and_bounded_matrix(self):
        self.assertEqual("abc", bench.archive_tree("abc", ""))
        self.assertEqual("abc:backend", bench.archive_tree("abc", "backend/"))
        self.assertEqual([1_000_000, 181_000_000, 361_000_000, 1_000_000],
                         bench.parse_times("1000000,181000000,361000000,1000000"))
        for invalid in (None, "", "-1", "370000000", ",".join(["1"] * 49), "1,x"):
            with self.subTest(value=invalid), self.assertRaises(ValueError):
                bench.parse_times(invalid)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def make_run(self, label, counterfactual="none", pixel="a", frame_count=2, variants=("baseline", "instrumented")):
        folder = self.root / "runs" / label
        folder.mkdir(parents=True)
        records = []
        for backend in ("cpu", "vulkan"):
            for variant in variants:
                filename = backend + "-" + variant + ".json"
                write_json(folder / filename, sample(backend, variant, counterfactual, pixel, frame_count))
                records.append({"name": backend + "-" + variant, "round": 1, "scenario": "material", "backend": backend,
                                "variant": variant, "identity": {"pid": 42, "startTicks": 123},
                                "exitCode": 0, "exited": True, "result": filename})
        write_json(folder / "manifest.json", {"complete": True, "arguments": {"rounds": 1, "scenarios": "material",
                                                                               "variants": ",".join(variants)},
                                               "runs": records})
        return folder

    def edit_json(self, path, edit):
        value = json.loads(path.read_text(encoding="utf-8"))
        edit(value)
        write_json(path, value)

    def test_matrix_timestamp_mismatch_is_rejected(self):
        folder = self.make_run("matrix-check")
        self.edit_json(folder / "manifest.json", lambda value: value["arguments"].update(
            mode="matrix", times_us="1000000,1000000", warmup=0, frames=2))
        for path in folder.glob("*-*.json"):
            self.edit_json(path, lambda value: value.update(mode="matrix", requestedTimesUs=[1_000_000, 1_000_000],
                requestedWarmupFrames=0, requestedMeasuredFrames=2))
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            analyze.analyze(self.root, "matrix-check")
        assessment = json.loads((folder / "assessment.json").read_text())
        self.assertFalse(assessment["valid"])
        self.assertTrue(any("timestamp sequence mismatch" in error for error in assessment["errors"]))

    def make_monitoring_run(self):
        folder = self.root / "runs" / "monitoring"
        folder.mkdir(parents=True)
        records = []
        for backend in ("cpu", "vulkan"):
            for mode in ("off", "summary", "detailed"):
                filename = f"{backend}-new-{mode}.json"
                events = []
                if mode == "summary":
                    kinds = ["summary"]
                elif mode == "detailed":
                    kinds = ["frame", "summary"]
                else:
                    kinds = []
                for kind in kinds:
                    events.append({"version": 1, "kind": kind, "backend": "gpu" if backend == "vulkan" else "cpu", "frames": 2 if kind == "summary" else 1,
                                   "wallMs": 1, "prepareMs": 0, "scriptMs": 0, "graphMs": 0, "materialMs": 0,
                                   "particleMs": 0, "rasterMs": 0, "validationMs": 0, "readbackMs": 0,
                                   "imageReads": 0, "imageBytes": 0, "surfaceReads": 0, "surfaceBytes": 0,
                                   "processAllocatedBytes": 0, "gc0": 0, "gc1": 0, "gc2": 0})
                sidecar = f"{backend}-new-{mode}.monitoring.jsonl" if events else None
                if sidecar:
                    (folder / sidecar).write_text("\n".join("MAKO_PERFORMANCE_V1 " + json.dumps(event) for event in events) + "\n")
                data = sample(backend, "new", monitoring_mode=mode, monitoring_file=sidecar, monitoring_lines=len(events))
                write_json(folder / filename, data)
                records.append({"name": f"{backend}-new-{mode}", "round": 1, "scenario": "material", "backend": backend,
                                "variant": "new", "monitoringMode": mode, "identity": {"pid": 42, "startTicks": 123},
                                "exitCode": 0, "exited": True, "result": filename})
        write_json(folder / "manifest.json", {"complete": True, "monitoringModes": ["off", "summary", "detailed"],
                                               "arguments": {"rounds": 1, "scenarios": "material", "variants": "new"},
                                               "runs": records})
        return folder

    def run_analyze(self, label, expected=None):
        with contextlib.redirect_stdout(io.StringIO()):
            if expected is None:
                analyze.analyze(self.root, label)
            else:
                with self.assertRaises(SystemExit):
                    analyze.analyze(self.root, label)
        assessment = json.loads((self.root / "runs" / label / "assessment.json").read_text())
        self.assertEqual(expected is None, assessment["valid"])
        if expected:
            self.assertTrue(any(expected in error for error in assessment["errors"]), assessment["errors"])
        return assessment

    def run_contribution(self, expected=None):
        with contextlib.redirect_stdout(io.StringIO()):
            if expected is None:
                analyze.contribution(self.root, "control", "disabled")
            else:
                with self.assertRaises(SystemExit):
                    analyze.contribution(self.root, "control", "disabled")
        assessment = json.loads((self.root / "runs" / "disabled" / "contribution.json").read_text())
        self.assertEqual(expected is None, assessment["valid"])
        if expected:
            self.assertTrue(any(expected in error for error in assessment["errors"]), assessment["errors"])
        return assessment

    def test_valid_pair_and_contribution(self):
        self.make_run("control")
        self.make_run("disabled", counterfactual="material", pixel="b")
        self.assertEqual(self.run_analyze("control")["counts"]["matchedFrames"], 4)
        self.run_analyze("disabled")
        self.assertEqual(len(self.run_contribution()["comparisons"]), 4)

    def test_pixel_mutation_rejected(self):
        folder = self.make_run("control")
        self.edit_json(folder / "cpu-instrumented.json", lambda data: data["frames"][0].update(bgraSha256="b" * 64))
        self.run_analyze("control", "pixels or diagnostics changed")

    def test_diagnostic_mutation_rejected(self):
        folder = self.make_run("control")
        def mutate(data):
            data["frames"][0]["diagnosticSha256"] = "e" * 64
            data["frames"][0]["diagnostics"]["runtime"].append({"code": "changed"})
        self.edit_json(folder / "cpu-instrumented.json", mutate)
        self.run_analyze("control", "pixels or diagnostics changed")

    def test_ab_original_and_instrumented_pairs(self):
        for variants in (("old", "new"), ("old-instrumented", "new-instrumented")):
            label = variants[0]
            self.make_run(label, variants=variants)
            self.assertEqual(self.run_analyze(label)["counts"]["matchedFrames"], 4)

    def test_ab_pixel_diagnostic_device_and_request_mutations_rejected(self):
        mutations = {
            "pixel": (lambda d: d["frames"][0].update(bgraSha256="b" * 64), "pixels or diagnostics changed"),
            "diagnostics": (lambda d: d["frames"][0]["diagnostics"]["material"].append({"code": "changed"}), "pixels or diagnostics changed"),
            "device": (lambda d: d["deviceProbe"].update(device="other"), "fixture or frame count changed"),
            "drm": (lambda d: d["process"].update(drmDevices=["/dev/dri/renderD129"]), "DRM device mismatch"),
            "request": (lambda d: d.update(transparent=True), "fixture or frame count changed"),
        }
        for label, (mutate, expected) in mutations.items():
            folder = self.make_run(label, variants=("old", "new"))
            self.edit_json(folder / ("vulkan-new.json" if label == "drm" else "cpu-new.json"), mutate)
            self.run_analyze(label, expected)

    def test_frame_budget_rejected(self):
        self.make_run("control", frame_count=49)
        self.run_analyze("control", "frame budget mismatch")

    def test_nonexited_process_rejected(self):
        folder = self.make_run("control")
        self.edit_json(folder / "manifest.json", lambda manifest: manifest["runs"][0].update(exited=False))
        self.run_analyze("control", "process did not exit cleanly")

    def test_invalid_trace_partition_rejected(self):
        folder = self.make_run("control")
        self.edit_json(folder / "cpu-instrumented.json",
                       lambda data: data["frames"][0]["trace"]["stages"][0].update(exclusiveMs=0.25))
        self.run_analyze("control", "exclusive stages do not partition root")

    def test_invalid_trace_byte_counter_rejected(self):
        folder = self.make_run("control")
        self.edit_json(folder / "cpu-instrumented.json",
                       lambda data: data["frames"][1]["trace"]["bytes"].update({"device.imageReadback.requested": -1}))
        self.run_analyze("control", "invalid byte counter")

    def test_cross_label_trace_comparison(self):
        self.make_run("raw", variants=("old", "new"))
        folder = self.make_run("traced", variants=("old-instrumented", "new-instrumented"))
        self.run_analyze("raw")
        self.run_analyze("traced")
        with contextlib.redirect_stdout(io.StringIO()):
            analyze.compare_trace(self.root, "raw", "traced")
        self.assertEqual(json.loads((folder / "trace-comparison.json").read_text())["matchedFrames"], 8)
        self.edit_json(folder / "cpu-new-instrumented.json", lambda data: data["frames"][0].update(bgraSha256="b" * 64))
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            analyze.compare_trace(self.root, "raw", "traced")

    def test_unchanged_contribution_rejected(self):
        self.make_run("control")
        self.make_run("disabled", counterfactual="material")
        self.run_analyze("control")
        self.run_analyze("disabled")
        self.run_contribution("disabled effect made no pixel contribution")

    def test_invalid_run_matrices_rejected(self):
        mutations = {
            "empty": lambda runs: runs.clear(),
            "missing": lambda runs: runs.pop(),
            "duplicate": lambda runs: runs.append(runs[0]),
            "duplicate-replacing": lambda runs: runs.__setitem__(-1, runs[0]),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name):
                folder = self.make_run(name)
                self.edit_json(folder / "manifest.json", lambda manifest: mutate(manifest["runs"]))
                self.run_analyze(name, "run matrix is incomplete, duplicated or unexpected")

    def test_monitoring_modes_preserve_pixels_and_validate_jsonl(self):
        folder = self.make_monitoring_run()
        # The sidecar must preserve .NET's actual property casing without making
        # the metrics or protocol-field gate dependent on a serializer policy.
        path = folder / "cpu-new-summary.monitoring.jsonl"
        prefix = "MAKO_PERFORMANCE_V1 "
        event = json.loads(path.read_text()[len(prefix):])
        path.write_text(prefix + json.dumps({key[:1].upper() + key[1:]: value for key, value in event.items()}) + "\n")
        with contextlib.redirect_stdout(io.StringIO()):
            analyze.analyze(self.root, "monitoring")
        assessment = json.loads((self.root / "runs" / "monitoring" / "assessment.json").read_text())
        self.assertTrue(assessment["valid"], assessment["errors"])
        self.assertEqual(assessment["counts"]["matchedMonitoringFrames"], 8)

    def test_monitoring_protocol_mutation_is_rejected(self):
        folder = self.make_monitoring_run()
        path = folder / "cpu-new-summary.monitoring.jsonl"
        path.write_text(path.read_text().replace('"kind": "summary"', '"kind": "summary", "route": "/api/v1/render"'))
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            analyze.analyze(self.root, "monitoring")
        assessment = json.loads((folder / "assessment.json").read_text())
        self.assertTrue(any("protocol fields" in error for error in assessment["errors"]))

    def test_monitoring_mode_pixels_and_counters_must_match_contract(self):
        folder = self.make_monitoring_run()
        self.edit_json(folder / "cpu-new-detailed.json", lambda data: data["frames"][0].update(bgraSha256="b" * 64))
        sidecar = folder / "cpu-new-summary.monitoring.jsonl"
        sidecar.write_text(sidecar.read_text().replace('"imageBytes": 0', '"imageBytes": -1'))
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            analyze.analyze(self.root, "monitoring")
        errors = json.loads((folder / "assessment.json").read_text())["errors"]
        self.assertTrue(any("monitoring pixels or diagnostics changed" in error for error in errors))
        self.assertTrue(any("invalid counter imageBytes" in error for error in errors))

    def test_monitoring_environment_does_not_inherit_external_selectors(self):
        env = bench.monitoring_environment({"PATH": "/bin", "MAKO_PERFORMANCE_MODE": "detailed",
                                            "MAKO_PERFORMANCE_DETAIL_EVERY": "1", "MAKO_PERFORMANCE_UNRECOGNIZED": "x"}, "off")
        self.assertEqual(env, {"PATH": "/bin", "MAKO_PERFORMANCE_MODE": "off"})

    def test_contribution_rejects_tampered_validated_manifest(self):
        control = self.make_run("control")
        disabled = self.make_run("disabled", counterfactual="material", pixel="b")
        self.run_analyze("control")
        self.run_analyze("disabled")
        mutations = {
            "empty": lambda runs: runs.clear(),
            "missing": lambda runs: runs.pop(),
            "duplicate": lambda runs: runs.append(runs[0]),
            "duplicate-replacing": lambda runs: runs.__setitem__(-1, runs[0]),
        }
        for label, folder in (("control", control), ("disabled", disabled)):
            path = folder / "manifest.json"
            original = path.read_text(encoding="utf-8")
            for name, mutate in mutations.items():
                with self.subTest(label=label, mutation=name):
                    self.edit_json(path, lambda manifest: mutate(manifest["runs"]))
                    with contextlib.redirect_stdout(io.StringIO()):
                        with self.assertRaisesRegex(ValueError, "contribution requires complete, validated runs"):
                            analyze.contribution(self.root, "control", "disabled")
                    path.write_text(original, encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
