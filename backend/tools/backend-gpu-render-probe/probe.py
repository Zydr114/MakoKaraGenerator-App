#!/usr/bin/env python3
"""Finite Linux hardware-Vulkan acceptance through the published HTTP/Worker path."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/backend-public-package-preview-probe"))
spec = importlib.util.spec_from_file_location(
    "gpu_package_export", ROOT / "tools/backend-package-export-http-probe/probe.py")
exports = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exports)
preview = exports.preview
save_json = preview.save_json
DEFAULT_ORACLE = (ROOT / "tools/backend-public-package-preview-probe/FixtureOracle/bin/Release/net10.0/FixtureOracle.dll")
BACKEND = "MAKO_RENDER_BACKEND"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def process_identity(pid):
    try:
        stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return int(pid), stat[19]
    except (OSError, IndexError):
        return None


def hardware_icd(path):
    path = path.resolve(strict=True)
    model = json.loads(path.read_text())
    library = model["ICD"]["library_path"]
    if any(part in library.lower() for part in ("lvp", "lavapipe", "swiftshader", "llvmpipe")):
        raise ValueError("This hardware probe rejects a software Vulkan ICD")
    return {"path": str(path), "sha256": sha(path.read_bytes()),
            "apiVersion": model["ICD"].get("api_version"), "library": Path(library).name}


DEFAULT_CONTROLS = ("Initialize-cancel", "Initialize-deadline", "Initialize-close",
                    "Evaluate-cancel", "Evaluate-deadline", "Evaluate-close")


class GpuProbe(exports.PackageExportProbe):
    def __init__(self, output, dist, oracle, icd):
        super().__init__(output, dist, oracle)
        self.icd = icd
        self.gpu_samples = {}
        self.gpu_attempts = {}
        self.gpu_assignments = []
        self.gpu_lock = threading.Lock()
        self.driver_hashes = {}
        self.export_baselines = {}
        self.completed_stages = []

    def observe(self, pid):
        super().observe(pid)
        # Only descendants discovered from this launcher's process tree are
        # examined. Never enumerate unrelated users' processes or environments.
        for identity in list(self.worker_identities):
            if process_identity(identity[0]) != identity:
                continue
            with self.gpu_lock:
                if self.gpu_samples.get(identity, {}).get("hardwareConfirmed"):
                    continue
                now = time.monotonic()
                if now - self.gpu_attempts.get(identity, 0) < .05:
                    continue
                self.gpu_attempts[identity] = now
            sample = self.inspect_worker(identity)
            with self.gpu_lock:
                if sample.get("hardwareConfirmed") or not self.gpu_samples.get(identity, {}).get("hardwareConfirmed"):
                    self.gpu_samples[identity] = sample

    def worker_identities_now(self):
        self.observe(self.current_ready["pid"])
        # The monitor can add identities while a foreground HTTP/control phase
        # enumerates them. Always iterate a stable snapshot.
        return [{"pid": pid, "startTicks": ticks} for pid, ticks in list(self.worker_identities)
                if process_identity(pid) == (pid, ticks)]

    def inspect_worker(self, identity):
        pid, ticks = identity
        sample = {"pid": pid, "startTicks": ticks, "hardwareConfirmed": False}
        try:
            proc = Path(f"/proc/{pid}")
            executable = proc.joinpath("exe").resolve(strict=True)
            sample["executable"] = str(executable)
            sample["publishedWorker"] = executable == (self.dist / "worker/MakoKaraGenerator.Backend.Worker").resolve()
            # Filter raw bytes before decoding or retaining any environment data.
            allowed = {BACKEND.encode(), b"VK_DRIVER_FILES"}
            environment = {}
            for part in proc.joinpath("environ").read_bytes().split(b"\0"):
                key, separator, value = part.partition(b"=")
                if separator and key in allowed:
                    environment[key.decode()] = value.decode("utf-8", "replace")
            sample["environment"] = environment
            maps = proc.joinpath("maps").read_text()
            paths = sorted({line.split()[-1] for line in maps.splitlines()
                            if len(line.split()) >= 6 and ("libvulkan" in line or self.icd["library"] in line)})
            sample["vulkanMappings"] = paths
            sample["hardwareDriverFiles"] = []
            for path in paths:
                if self.icd["library"] not in path:
                    continue
                with self.gpu_lock:
                    digest = self.driver_hashes.get(path)
                if digest is None:
                    digest = sha(Path(path).read_bytes())
                    with self.gpu_lock:
                        self.driver_hashes[path] = digest
                sample["hardwareDriverFiles"].append({"path": path, "sha256": digest})
            render_nodes = []
            for fd in proc.joinpath("fd").iterdir():
                try:
                    target = os.readlink(fd)
                    if not target.startswith("/dev/dri/renderD"):
                        continue
                    node = Path(target).name
                    device = Path("/sys/class/drm") / node / "device"
                    entry = {"fd": fd.name, "path": target}
                    for key in ("vendor", "device", "revision"):
                        value = device / key
                        if value.exists():
                            entry[key] = value.read_text().strip()
                    if (device / "driver").exists():
                        entry["driver"] = (device / "driver").resolve().name
                    render_nodes.append(entry)
                except OSError:
                    continue
            sample["renderNodes"] = render_nodes
            sample["identityRechecked"] = process_identity(pid) == identity
            sample["hardwareConfirmed"] = bool(
                sample["publishedWorker"] and sample["identityRechecked"]
                and environment.get(BACKEND) == "vulkan"
                and environment.get("VK_DRIVER_FILES") == self.icd["path"]
                and any("libvulkan.so" in path for path in paths)
                and any(self.icd["library"] in path for path in paths)
                and any(node.get("driver") for node in render_nodes))
        except (OSError, IndexError) as error:
            sample["observationError"] = type(error).__name__
        return sample

    def require_gpu(self, label, identities=None, baseline=None):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            self.observe(self.current_ready["pid"])
            with self.gpu_lock:
                samples = list(self.gpu_samples.items())
            candidates = [sample for identity, sample in samples
                          if sample.get("hardwareConfirmed")
                          and (identities is None or identity in identities)
                          and (baseline is None or identity not in baseline)]
            if candidates:
                self.gpu_assignments.append({"name": label, "workers": [
                    {"pid": item["pid"], "startTicks": item["startTicks"]} for item in candidates]})
                self.check(True, label + ": own published Worker has Vulkan selector/loader/pinned hardware driver/DRM render fd")
                return candidates
            time.sleep(.05)
        raise AssertionError(label + ": no matching actual hardware-Vulkan Worker evidence")

    def require_workers_drained(self, label):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline and self.worker_identities_now():
            time.sleep(.05)
        self.check(not self.worker_identities_now(), label + ": all prior Worker identities drained")

    def compare_frame(self, label, state, expected, kind, contribution=None):
        frame = state["session"]["latestFrame"]
        oracle = json.loads(expected.with_suffix(".json").read_text())
        self.check(not oracle["hasError"], label + ": independent direct Runtime has no domain error")
        deployment = json.loads((self.dist / "deployment.json").read_text())
        self.check(oracle["engineIdentity"] == "MakoKaraGenerator:" + deployment["version"], label + ": direct Runtime engine version matches deployment")
        _, png, _ = self.fetch(label + "-content", frame["content"]["contentUrl"])
        self.check(sha(png) == frame["content"]["sha256"] and len(png) == frame["content"]["sizeBytes"], label + ": immutable PNG full size/SHA")
        self.check(png[:8] == b"\x89PNG\r\n\x1a\n", label + ": PNG signature")
        width, height = frame["width"], frame["height"]
        self.check(int.from_bytes(png[16:20], "big") == width and int.from_bytes(png[20:24], "big") == height, label + ": PNG dimensions")
        self.check(png == expected.with_suffix(".png").read_bytes(), label + ": complete direct GPU Runtime PNG byteexact on this device")
        result = subprocess.run([shutil.which("ffmpeg"), "-v", "error", "-i", "pipe:0", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"], input=png, capture_output=True, check=True)
        pixels = result.stdout
        self.check(len(pixels) == width * height * 4, label + ": full straight BGRA decode length")
        alpha = pixels[3::4]
        if frame["alphaMode"] == "opaque":
            self.check(set(alpha) == {255}, label + ": opaque PNG alpha is 255 throughout")
        else:
            self.check(0 in alpha and any(0 < value < 255 for value in alpha), label + ": transparent PNG retains zero and partial alpha")
        self.check(len({pixels[index:index + 3] for index in range(0, len(pixels), 4) if pixels[index + 3]}) > 1, label + ": visible nonuniform content")
        raw = expected.with_suffix(".bgra").read_bytes()
        self.check(len(raw) == width * height * 4 and sha(raw) == oracle["bgraSha256"], label + ": direct GPU Runtime premultiplied BGRA size/SHA")
        self.check(all(max(raw[index:index + 3]) <= raw[index + 3] for index in range(0, len(raw), 4)), label + ": direct Runtime readback obeys premultiplied alpha")
        if contribution is not None:
            self.check(raw != contribution.with_suffix(".bgra").read_bytes(), label + ": package contributes actual pixels against disabled instance/material")
        actual_path = self.output / (label + "-http-straight.bgra")
        actual_path.write_bytes(pixels)
        live = {(item["pid"], item["startTicks"]) for item in self.worker_identities_now()}
        self.check(len(live) == 1, label + ": one live preview Worker binds this frame")
        self.require_gpu(label, identities=live)
        record = {"name": label, "kind": kind, "timeUs": frame["timeUs"], "width": width, "height": height,
                  "alphaMode": frame["alphaMode"], "pngSha256": sha(png), "httpStraightBgraSha256": sha(pixels),
                  "directPremultipliedBgraSha256": sha(raw), "directSourceSha256": oracle["sourceSha256"],
                  "counterfactual": contribution is not None, "alphaMin": min(alpha), "alphaMax": max(alpha)}
        self.comparisons.append(record)
        return oracle

    def export_job(self, name, source, fmt, include_audio=False, **kwargs):
        self.require_workers_drained(name + "-before")
        baseline = set(self.worker_identities)
        result = super().export_job(name, source, fmt, include_audio, **kwargs)
        self.export_baselines[result["jobId"]] = baseline
        return result

    def wait_terminal(self, name, accepted, expected="succeeded", timeout=60):
        result = super().wait_terminal(name, accepted, expected, timeout)
        # Successful exports have completed and may already have exited. Their
        # complete samples were captured while alive by the descendant monitor.
        if expected == "succeeded":
            self.require_gpu(name, baseline=self.export_baselines[accepted["jobId"]])
        return result

    def wait_export_active(self, name, accepted):
        deadline = time.monotonic() + 15
        baseline = self.export_baselines[accepted["jobId"]]
        while time.monotonic() < deadline:
            state = self.request(name + "-active", "GET", "/api/v1/jobs/" + accepted["jobId"])
            self.check(state["state"] not in ("succeeded", "failed", "cancelled", "unknown"), name + ": export remains active before control")
            workers = self.worker_identities_now()
            if state["state"] == "running":
                for worker in sorted(workers, key=lambda item: item["pid"], reverse=True):
                    identity = worker["pid"], worker["startTicks"]
                    if identity in baseline:
                        continue
                    # A short-lived capability Worker can overlap the export
                    # Worker. Require a fresh, live hardware sample rather than
                    # selecting the newest PID or a previously cached sample.
                    sample = self.inspect_worker(identity)
                    if not sample.get("hardwareConfirmed"):
                        continue
                    with self.gpu_lock:
                        self.gpu_samples[identity] = sample
                    self.export_workers[accepted["jobId"]] = worker
                    return state, worker
            time.sleep(.03)
        raise AssertionError(name + ": no active hardware-Vulkan export Worker")

    def freeze_active_export(self, name, accepted, worker):
        identity = worker["pid"], worker["startTicks"]
        sample = self.inspect_worker(identity)
        self.check(sample.get("hardwareConfirmed"), name + ": selected control Worker still has actual hardware-Vulkan state")
        with self.gpu_lock:
            self.gpu_samples[identity] = sample
        self.require_gpu(name, identities={identity})
        state = self.request(name + "-before-freeze", "GET", "/api/v1/jobs/" + accepted["jobId"])
        self.check(state["state"] == "running", name + ": job still running immediately before SIGSTOP")
        self.check(process_identity(worker["pid"]) == identity, name + ": selected hardware Worker still live immediately before SIGSTOP")
        self.signal_worker(name, worker, signal.SIGSTOP)
        state = self.request(name + "-after-freeze", "GET", "/api/v1/jobs/" + accepted["jobId"])
        self.check(state["state"] == "running" and process_identity(worker["pid"]) == identity,
                   name + ": same Worker and running job retained after SIGSTOP")

    def decode_bgra_frames(self, name, media_path, report):
        super().decode_bgra_frames(name, media_path, report)
        raw = subprocess.run([shutil.which("ffmpeg"), "-v", "error", "-i", str(media_path), "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "bgra", "-"], capture_output=True, check=True).stdout
        frame_bytes = int(report["width"]) * int(report["height"]) * 4
        frames = []
        for index in range(int(report["expectedFrames"])):
            frame = raw[index * frame_bytes:(index + 1) * frame_bytes]
            self.check(len({frame[offset:offset + 3] for offset in range(0, len(frame), 4) if frame[offset + 3]}) > 1, name + f": decoded frame {index} contains content")
            frames.append({"index": index, "sha256": sha(frame), "alphaMin": min(frame[3::4]), "alphaMax": max(frame[3::4])})
        (self.output / (name + "-decoded.bgra")).write_bytes(raw)
        save_json(self.output / (name + "-decoded.json"), {"pixelFormat": "straight BGRA", "frameBytes": frame_bytes,
                  "frames": frames, "sha256": sha(raw), "scope": "Independent full media decoding and content/alpha checks; no video CPU/Runtime pixel oracle."})

    def export_controls(self):
        self.prepare_long_fixture()
        source = self.open_directory("gpu-controls-long", "scale-long")
        results = []
        for action in ("cancel", "deadline", "close"):
            label = "gpu-export-" + action
            accepted = self.export_job(label, source, "opaqueMp4", False)
            _, worker = self.wait_export_active(label, accepted)
            self.freeze_active_export(label, accepted, worker)
            if action == "cancel":
                self.cancel_job(label + "-request", accepted)
                terminal = self.wait_terminal(label, accepted, "cancelled", 35)
            elif action == "deadline":
                terminal = self.wait_terminal(label, accepted, "failed", 35)
                self.check(terminal.get("failureCode") == "WorkerExportTimedOut" and not terminal.get("cancellationRequested"), label + ": stable WorkerExportTimedOut")
            else:
                closing = self.request(label + "-project-close", "POST", "/api/v1/project-sessions/" + source["sourceId"] + "/close", status=202,
                    value={"expectedRevision": source["sourceRevision"], "policy": "discard", "activeJobs": "cancelAndWait"})
                self.job(label + "-project-close", closing)
                terminal = self.wait_terminal(label, accepted, "cancelled", 35)
            self.check(terminal.get("exportResult") is None, label + ": interrupted export publishes no artifact")
            self.request(label + "-no-artifact", "GET", "/api/v1/jobs/" + accepted["jobId"] + "/export-content", status=404, code="exportArtifactNotFound")
            self.require_workers_drained(label)
            results.append({"action": action, "worker": worker, "jobId": accepted["jobId"], "state": terminal["state"], "failureCode": terminal.get("failureCode"), "drained": True})
        save_json(self.output / "export-controls.json", results)

    def run(self, stages, kinds, controls=None):
        self.prepare()
        self.start("gpu-render")
        self.connect("gpu-render")
        if "previews" in stages:
            for kind in kinds:
                for transparent in (False, True):
                    self.healthy(kind, transparent)
                    self.require_workers_drained(kind + ("-transparent" if transparent else "-opaque"))
            self.completed_stages.append("previews")
        if "exports" in stages:
            # Both the static and package production inputs go through the
            # published renderer and the existing CPU-side FFmpeg boundary.
            for kind in ("static", "scale", "graph", "material"):
                source = self.projects.get(kind) or self.open_fixture(kind)
                for fmt in ("opaqueMp4", "transparentProRes4444"):
                    label = "gpu-export-" + kind + "-" + fmt
                    terminal = self.wait_terminal(label, self.export_job(label, source, fmt))
                    self.export_report_and_media(label, terminal)
                    self.require_workers_drained(label)
            self.completed_stages.append("exports")
        if "preview-controls" in stages:
            selected_controls = list(DEFAULT_CONTROLS) if controls is None else controls
            for item in selected_controls:
                stage, action = item.split("-", 1)
                preview.lifecycle.exercise_runaway(self, stage, action)
                self.require_workers_drained("runaway-" + stage + "-" + action)
            self.completed_stages.append("preview-controls")
        if "export-controls" in stages:
            self.export_controls()
            self.completed_stages.append("export-controls")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--oracle", type=Path, default=DEFAULT_ORACLE, help="Release FixtureOracle.dll; fixture generation and direct GPU Runtime references")
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"), help="one explicit hardware Vulkan ICD, inherited by Server/Worker/oracle")
    parser.add_argument("--stages", default="previews,exports,preview-controls,export-controls")
    parser.add_argument("--kinds", default="static,scale,graph,sprite,material", help="preview fixture subset; only selected kinds are claimed")
    parser.add_argument("--controls", default=",".join(DEFAULT_CONTROLS), help="preview-controls subset; only selected controls are claimed")
    args = parser.parse_args()
    stages, kinds, controls = args.stages.split(","), args.kinds.split(","), args.controls.split(",")
    if not stages or set(stages) - {"previews", "exports", "preview-controls", "export-controls"}:
        parser.error("unknown or empty --stages")
    if not kinds or set(kinds) - {"static", "scale", "graph", "sprite", "material"}:
        parser.error("unknown or empty --kinds")
    if not controls or set(controls) - set(DEFAULT_CONTROLS):
        parser.error("unknown or empty --controls")
    if not args.oracle.is_file():
        parser.error("build Release FixtureOracle first, or supply --oracle")
    if not shutil.which("ffmpeg") or not shutil.which("ffprobe"):
        parser.error("ffmpeg and ffprobe are required")
    icd = hardware_icd(args.icd)
    os.environ[BACKEND] = "vulkan"
    os.environ["VK_DRIVER_FILES"] = icd["path"]
    # The shared launcher explicitly passes this process-scoped override.
    os.environ["MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS"] = "20"
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    probe = GpuProbe(output, args.dist.resolve(), args.oracle.resolve(), icd)
    failure = None
    try:
        save_json(output / "gpu-environment.json", {"backend": "vulkan", "icd": icd,
                  "oracle": str(args.oracle.resolve()), "oracleSha256": sha(args.oracle.read_bytes()),
                  "dist": str(args.dist.resolve()), "deploymentSha256": sha((args.dist / "deployment.json").read_bytes())})
        probe.run(stages, kinds, controls)
    except BaseException as error:
        failure = error
        (output / "failure.txt").write_text(str(error) + "\n")
    finally:
        try:
            probe.stop("gpu-render-final")
        except BaseException as error:
            failure = failure or error
    with probe.gpu_lock:
        samples = [value for _, value in sorted(probe.gpu_samples.items())]
    save_json(output / "gpu-worker-evidence.json", {"icd": icd, "workers": samples, "assignments": probe.gpu_assignments,
              "scope": "Only this launcher's published Worker descendants; only two environment keys retained; loader + pinned hardware driver + DRM fd checked under the same PID/start-ticks identity."})
    summary = {"status": "failed" if failure else "passed", "error": str(failure) if failure else None,
               "httpRequests": probe.request_count, "assertions": len(probe.assertions),
               "requestedStages": stages, "completedStages": probe.completed_stages, "previewKinds": kinds,
               "frameComparisons": probe.comparisons,
               "previewControls": [{**value, "renderBackendConfigured": "vulkan", "runawayWorkerHardwareConfirmed":
                   any(sample["pid"] == value["worker"]["pid"] and sample["startTicks"] == value["worker"]["startTicks"]
                       and sample.get("hardwareConfirmed") for sample in samples)} for value in probe.runaway_results],
               "exportJobs": [{"jobId": value["jobId"], "state": value["state"], "failureCode": value.get("failureCode")} for value in probe.export_results],
               "runs": probe.runs, "observedIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.observed)],
               "hardwareWorkerCount": sum(bool(value.get("hardwareConfirmed")) for value in samples),
               "scope": "Finite local hardware Vulkan HTTP/Worker pipeline only. Preview PNGs compare to direct GPU Runtime on the same machine; video checks decode all frames without a video pixel oracle. No CPU/GPU equality, cross-platform matrix, performance claim, GPU codec or browser acceptance."}
    save_json(output / "summary.json", summary)
    try:
        summary["credentialScannedFiles"] = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        summary.update(status="failed", error=str(failure))
    summary.update(assertions=len(probe.assertions), assertionLabels=probe.assertions)
    save_json(output / "summary.json", summary)
    save_json(output / "evidence-manifest.json", {"files": {str(path.relative_to(output)): sha(path.read_bytes())
              for path in sorted(output.rglob("*")) if path.is_file() and path.name != "evidence-manifest.json"}})
    print(json.dumps({key: summary.get(key) for key in ("status", "error", "httpRequests", "assertions", "completedStages", "hardwareWorkerCount", "credentialScannedFiles")}))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
