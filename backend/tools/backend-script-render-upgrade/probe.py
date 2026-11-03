#!/usr/bin/env python3
"""Finite authenticated HTTP seek baseline; no UI/display or SDK v2 claim."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import subprocess
import sys
import threading
import time
import traceback
from urllib.parse import urlencode
import wave
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("seek_smoke", ROOT / "tools/backend-web-host/smoke.py")
smoke = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(smoke)
save_json = smoke.existing.save_json


class TimedResponse:
    """Stamp completion of the full read before evidence is written to disk."""
    def __init__(self, response, sample):
        self.response, self.sample = response, sample

    def __getattr__(self, key):
        return getattr(self.response, key)

    def __enter__(self):
        self.response.__enter__()
        return self

    def __exit__(self, *args):
        return self.response.__exit__(*args)

    def read(self, *args):
        value = self.response.read(*args)
        self.sample["pngDownloadedMonotonicNs"] = time.monotonic_ns()
        return value


class TimedOpener:
    """Keep Smoke's direct-loopback opener and add local timing observations."""
    def __init__(self, opener, probe):
        self.opener, self.probe = opener, probe

    def open(self, request, *args, **kwargs):
        if self.probe.sending_sample is not None and request.full_url.endswith("/frame-requests"):
            self.probe.sending_sample["requestDispatchedMonotonicNs"] = time.monotonic_ns()
        response = self.opener.open(request, *args, **kwargs)
        if self.probe.downloading_sample is not None:
            return TimedResponse(response, self.probe.downloading_sample)
        return response


def identity(pid):
    """Read Linux process identity without persisting arguments or environment."""
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return {"pid": int(pid), "startTicks": fields[19]}
    except (OSError, IndexError):
        return None


class SeekProbe(smoke.Smoke):
    def __init__(self, output, dist, args):
        super().__init__(output, dist)
        self.args = args
        self.last_maintenance = time.monotonic()
        self.maintaining = False
        self.worker_events = {}
        self.worker_gate = threading.Lock()
        self.scenarios = []
        self.samples = []
        self.active_preview = None
        self.active_project = None
        self.template = None
        self.stage = "initialize"
        self.last_request = None
        self.failed_check = None
        self.sending_sample = None
        self.downloading_sample = None
        self.http = TimedOpener(self.http, self)

    def safe_text(self, value):
        text = str(value)
        secrets = set(self.secrets) | set(self.tokens.values())
        if self.bootstrap:
            secrets.add(self.bootstrap)
        if self.registration:
            secrets.update(self.registration.get(key, "") for key in ("accessToken", "resumeToken"))
        for secret in sorted(filter(None, secrets), key=len, reverse=True):
            text = text.replace(secret, "<redacted>")
        return "".join(character if character >= " " else " " for character in text)[:2048]

    def failure(self, error, stage=None):
        frames = [{"file": str(Path(frame.filename).resolve().relative_to(ROOT)) if Path(frame.filename).resolve().is_relative_to(ROOT)
                   else Path(frame.filename).name, "line": frame.lineno, "function": frame.name}
                  for frame in traceback.extract_tb(error.__traceback__)]
        return {"stage": stage or self.stage, "errorType": type(error).__name__,
            "message": self.safe_text(error), "checkLabel": self.failed_check if isinstance(error, AssertionError)
            and self.safe_text(error) == self.failed_check else None,
            "lastRequest": self.last_request, "trace": frames}

    def check(self, condition, label):
        if not condition:
            self.failed_check = self.safe_text(label)
        return super().check(condition, label)

    def register(self, *args, **kwargs):
        value = super().register(*args, **kwargs)
        self.secrets.extend((value["accessToken"], value["resumeToken"]))
        return value

    def request(self, name, method, path, **kwargs):
        # Long v1 replays can outlast access TTL. Renew only between requests;
        # credentials stay in memory and inherited evidence is redacted.
        if self.client and not self.maintaining and time.monotonic() - self.last_maintenance >= 30:
            self.maintaining = True
            try:
                super().request(name + "-heartbeat", "POST", "/api/v1/clients/" + self.client + "/heartbeat")
                self.register(name + "-resume", resume=self.registration)
                self.last_maintenance = time.monotonic()
            finally:
                self.maintaining = False
        value = kwargs.get("value")
        if method == "POST" and isinstance(value, dict) and path != "/api/v1/clients" and not (
                path.endswith("/commands") or path == "/api/v1/effect-packages/inspect"):
            kwargs["value"] = {**value, "metadata": value.get("metadata", self.metadata(name))}
        self.last_request = {"name": name, "method": method, "path": path}
        return super().request(name, method, path, **kwargs)

    def observe(self, pid):
        current = identity(pid)
        if current:
            try:
                executable = Path(f"/proc/{pid}/exe").resolve(strict=True)
                if executable.name == "MakoKaraGenerator.Backend.Worker" and executable.is_relative_to(self.dist):
                    key = (pid, current["startTicks"])
                    now = time.monotonic_ns()
                    with self.worker_gate:
                        self.worker_events.setdefault(key, {**current, "firstObservedNs": now})["lastObservedNs"] = now
            except OSError:
                pass
        super().observe(pid)

    def workers(self):
        if self.current_ready:
            self.observe(self.current_ready["pid"])
        with self.worker_gate:
            events = [dict(item) for item in self.worker_events.values()]
        return sorted([item for item in events if identity(item["pid"]) == {
            "pid": item["pid"], "startTicks": item["startTicks"]}], key=lambda item: item["pid"])

    def upload(self, name, filename, content_type, data):
        return self.request(name, "POST", "/api/v1/assets?" + urlencode({"fileName": filename}), raw=data,
            content_type=content_type, status=201, headers={"X-Mako-Request-Id": name,
                "X-Mako-Valid-Until-Utc": self.metadata(name)["validUntilUtc"]})

    def prepare_template(self):
        directory = ROOT / "samples/ScriptEffects/WordEmphasis"
        manifest = json.loads((directory / "manifest.json").read_text())
        files = {entry["path"]: (directory / entry["path"]).read_bytes() for entry in manifest["files"]}
        for entry in manifest["files"]:
            entry["sha256"] = hashlib.sha256(files[entry["path"]]).hexdigest()
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False))
            for path, data in files.items():
                package.writestr(path, data)
        asset = self.upload("script-package", "seek-word-emphasis.mkfx", "application/octet-stream", archive.getvalue())
        inspected = self.request("script-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], "C# package structure valid")
        draft = self.request("script-draft", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": "word-emphasis", "name": "HTTP seek C# fixture"})
        self.check(draft["canCompile"], "fixture contains C# controller")
        builtin = self.request("template-builtin", "GET", "/api/v1/template-libraries/builtin/templates")
        base = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request("template-detail", "GET", "/api/v1/template-libraries/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": base["templateId"]}))
        definition = copy.deepcopy(detail["definition"])
        config = draft["configuration"]
        definition.update(name="HTTP seek WordEmphasis", kind="line", properties=["opacity"], packages=[inspected["reference"]])
        definition["effectRecipes"] = [{"recipeId": "seek-controller", "name": "Seek WordEmphasis",
            "target": config["relativeTarget"], "packageId": config["packageId"], "revision": config["sourceRevision"],
            "exportId": config["exportId"], "parametersJson": config["parametersJson"], "enabled": True,
            "overlayLayer": config["overlayLayer"], "readScope": config["readScope"],
            "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"), "configurationId": None}]
        library = self.request("template-personal", "GET", "/api/v1/template-libraries/personal/templates")
        self.template = self.request("template-create", "POST", "/api/v1/template-libraries/personal/commands", value={
            "expectedRevision": library["revision"], "metadata": self.metadata("template-create"),
            "command": {"type": "create", "definitionFormat": "full", "definition": definition}})
        save_json(self.output / "fixture.json", {"sample": "WordEmphasis", "packageId": inspected["packageId"],
            "packageRevision": inspected["packageRevision"], "archiveSha256": hashlib.sha256(archive.getvalue()).hexdigest(),
            "sourceFiles": {path: hashlib.sha256(data).hexdigest() for path, data in files.items()},
            "scope": "Repository C# sample imported through HTTP and applied to each project line; no source/parameter mutation benchmark."})

    def send(self, name, command):
        project = self.active_project
        result = self.request(name, "POST", project["path"] + "/commands", value=self.command(project["revision"], name, command))
        project["revision"] = result["projectRevision"]
        return result

    def create_project(self, name, duration_us):
        stream = io.BytesIO()
        with wave.open(stream, "wb") as audio:
            audio.setnchannels(1); audio.setsampwidth(2); audio.setframerate(8000)
            audio.writeframes(b"\0\0" * math.ceil(duration_us * 8000 / 1_000_000))
        asset = self.upload(name + "-audio", name + ".wav", "audio/wav", stream.getvalue())
        lyrics = self.upload(name + "-lyrics", name + ".lrc", "text/plain", b"[00:00.00]HTTP script seek fixture\n")
        created = self.request(name + "-project", "POST", "/api/v1/project-sessions", status=201, value={
            "name": "HTTP seek " + name, "audioAssetId": asset["assetId"], "lyricsAssetId": lyrics["assetId"],
            "composition": {"canvasWidth": self.args.width, "canvasHeight": self.args.height,
                "frameRateNumerator": 30, "frameRateDenominator": 1, "outputDurationUs": duration_us}})
        self.active_project = {"path": "/api/v1/project-sessions/" + created["projectSessionId"], "revision": created["projectRevision"]}
        lines = self.request(name + "-lines", "GET", self.active_project["path"] + "/lyrics/lines?branchId=primary")["lines"]
        self.check(len(lines) == 1 and bool(lines[0]["tokens"]), name + ": one fixed lyric line with tokens")
        line = lines[0]
        self.send(name + "-line-time", {"type": "lyrics.setTimeRange", "target": {"branchId": "primary", "lineId": line["id"]},
            "startUs": "0", "endUs": str(duration_us)})
        self.send(name + "-token-time", {"type": "lyrics.setTokenTimes", "changes": [{"target": {
            "branchId": "primary", "lineId": line["id"], "tokenId": token["id"]},
            "startUs": str(duration_us * index // len(line["tokens"])),
            "endUs": str(duration_us * (index + 1) // len(line["tokens"]))} for index, token in enumerate(line["tokens"])]})
        self.send(name + "-apply-script", {"type": "presetApplications.apply", "templateLibraryId": "personal",
            "templateId": self.template["templateId"], "templateRevision": self.template["revision"],
            "targets": [{"kind": "line", "branchId": "primary", "lineId": line["id"]}]})
        source = {"kind": "project", "sourceId": created["projectSessionId"], "sourceRevision": self.active_project["revision"]}
        preview = self.request(name + "-preview", "POST", "/api/v1/preview-sessions", status=201, value={"source": source,
            "size": {"width": self.args.width, "height": self.args.height}, "alphaMode": "opaque", "purpose": "preview", "renderBackend": self.args.backend})
        self.active_preview = preview.get("session", preview)
        return self.active_preview

    def sample(self, name, sequence, time_us, hashes):
        preview = self.active_preview
        path = "/api/v1/preview-sessions/" + preview["previewId"]
        revision = preview["source"]["sourceRevision"]
        previous = preview["previewGeneration"]
        self.workers()
        started = time.monotonic_ns()
        sample = {"name": name, "requestSeq": str(sequence), "timeUs": str(time_us), "sourceRevision": revision,
            "previousGeneration": previous, "startedMonotonicNs": started, "status": "pending"}
        self.samples.append(sample)
        try:
            self.sending_sample = sample
            try:
                accepted = self.request(name + "-seek", "POST", path + "/frame-requests", status=202, value={
                    "expectedSourceRevision": revision, "previewGeneration": previous,
                    "requestSeq": str(sequence), "timeUs": str(time_us), "intent": "seek"})
            finally:
                self.sending_sample = None
            dispatched = sample["requestDispatchedMonotonicNs"]
            preview["previewGeneration"] = accepted["previewGeneration"]
            sample["previewGeneration"] = preview["previewGeneration"]
            sample["acceptedMs"] = (time.monotonic_ns() - dispatched) / 1e6
            self.check(int(preview["previewGeneration"]) > int(previous), name + ": seek changes generation")
            self.check(accepted["previewId"] == preview["previewId"] and accepted["requestSeq"] == str(sequence)
                and accepted["timeUs"] == str(time_us) and accepted["state"] == "accepted", name + ": exact accepted identity")
            deadline = dispatched / 1e9 + self.args.timeout_seconds
            count = 0
            while time.monotonic() < deadline:
                count += 1
                state = self.request(name + "-poll-" + str(count), "GET", path)
                current = state["session"]
                if current["state"] == "failed":
                    sample["failure"] = self.redact(state)
                    raise AssertionError(name + ": Worker reported failed")
                frame = current.get("latestFrame")
                if current["state"] == "ready" and frame and frame["requestSeq"] == str(sequence):
                    expected = {"previewId": preview["previewId"], "previewGeneration": preview["previewGeneration"],
                        "sourceRevision": revision, "requestSeq": str(sequence), "timeUs": str(time_us)}
                    self.check(all(frame.get(key) == value for key, value in expected.items()), name + ": ready frame exact identity")
                    self.check(current["previewGeneration"] == preview["previewGeneration"] and current["source"] == preview["source"], name + ": ready session exact fixed source")
                    sample["readyMs"] = (time.monotonic_ns() - dispatched) / 1e6
                    self.downloading_sample = sample
                    try:
                        headers, png = self.binary(name + "-png", frame["content"]["contentUrl"])
                    finally:
                        self.downloading_sample = None
                    sample["downloadCompleteMs"] = (sample["pngDownloadedMonotonicNs"] - dispatched) / 1e6
                    self.check(len(png) == frame["content"]["sizeBytes"] and hashlib.sha256(png).hexdigest() == frame["content"]["sha256"], name + ": complete PNG size/SHA")
                    self.check(headers.get("Content-Type", "").split(";")[0] == "image/png" and png[:8] == b"\x89PNG\r\n\x1a\n"
                        and tuple(int.from_bytes(png[pos:pos + 4], "big") for pos in (16, 20)) == (self.args.width, self.args.height), name + ": PNG dimensions/type")
                    decoded = subprocess.run([self.args.ffmpeg, "-v", "error", "-i", "pipe:0", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"],
                        input=png, capture_output=True, timeout=30, check=True).stdout
                    self.check(len(decoded) == self.args.width * self.args.height * 4, name + ": full PNG RGBA decode")
                    digest = hashlib.sha256(decoded).hexdigest()
                    self.check(time_us not in hashes or hashes[time_us] == digest, name + ": repeated time pixel SHA identical")
                    hashes[time_us] = digest
                    sample.update(status="ready", frameId=frame["frameId"], pngSha256=frame["content"]["sha256"], rgbaSha256=digest,
                        workersAtReady=self.workers(), pngBytes=len(png))
                    if self.args.require_reuse:
                        self.check(len(sample["workersAtReady"]) == 1, name + ": exactly one live rendering Worker")
                    return sample
                time.sleep(self.args.poll_ms / 1000)
            sample["status"] = "timeout"
            sample["timeoutSeconds"] = self.args.timeout_seconds
            raise TimeoutError(name + ": frame not ready within finite probe deadline")
        except Exception as error:
            if sample["status"] != "timeout":
                sample["status"] = "failed"
            # Persist bounded, credential-scrubbed details and trace locations;
            # never serialize traceback locals or source text.
            sample["failureDetail"] = self.failure(error)
            raise
        finally:
            sample["elapsedMs"] = (time.monotonic_ns() - started) / 1e6
            with self.worker_gate:
                sample["workersObservedDuringRequest"] = [dict(event) for event in self.worker_events.values() if event["lastObservedNs"] >= started]
            save_json(self.output / (name + "-sample.json"), sample)

    def close_resources(self, name):
        errors = []
        if self.active_preview:
            preview = self.active_preview
            try:
                closed = self.request(name + "-preview-close", "POST", "/api/v1/preview-sessions/" + preview["previewId"] + "/close", value={
                    "expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": preview["source"]["sourceRevision"]})
                self.check(closed.get("session", closed).get("state") == "closed", name + ": preview close terminal")
                save_json(self.output / (name + "-preview-closed.json"), self.redact(closed))
            except Exception as error:
                errors.append(self.failure(error, name + ":preview-close"))
            self.active_preview = None
        if self.active_project:
            project = self.active_project
            try:
                accepted = self.request(name + "-project-close", "POST", project["path"] + "/close", status=202, value={
                    "expectedRevision": project["revision"], "policy": "discard", "activeJobs": "cancelAndWait"})
                terminal = self.job(name + "-project-close", accepted)
                save_json(self.output / (name + "-project-closed.json"), self.redact(terminal))
            except Exception as error:
                errors.append(self.failure(error, name + ":project-close"))
            self.active_project = None
        save_json(self.output / (name + "-resource-close.json"), {"status": "failed" if errors else "closed", "failures": errors})
        if errors:
            raise AssertionError("resource close failed; see " + name + "-resource-close.json")

    def run(self):
        deployment = json.loads((self.dist / "deployment.json").read_text())
        save_json(self.output / "provenance.json", {"deployment": deployment, "probeSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "parameters": vars(self.args) | {"dist": str(self.dist), "output": str(self.output)},
            "scope": "Explicit HTTP seek dispatch to exact ready frame/full PNG read. Poll resolution and JSON evidence I/O before the ready observation are included; PNG read timestamp excludes its evidence write/decode. No UI-visible timing."})
        self.stage = "start"
        self.start("seek")
        self.stage = "connect"
        self.connect("seek")
        self.check(self.current_ready["version"] == deployment["version"], "running version matches deployment manifest")
        self.stage = "prepare-template"
        self.prepare_template()
        kinds = ["near", "180s", "360s"] if self.args.scenario == "all" else [self.args.scenario]
        for kind in kinds:
            duration = {"near": 8_000_000, "180s": 181_000_000, "360s": 361_000_000}[kind]
            pattern = [500_000, 1_000_000, 2_000_000, 3_000_000] if kind == "near" else [500_000, int(kind[:-1]) * 1_000_000, 1_000_000, int(kind[:-1]) * 1_000_000]
            scene = {"name": kind, "durationUs": str(duration), "requestedSamples": self.args.count, "status": "running"}
            self.scenarios.append(scene)
            try:
                self.stage = kind + ":create-project"
                self.create_project(kind, duration)
                hashes = {}
                first = len(self.samples)
                for index in range(self.args.count):
                    self.stage = kind + ":seek-" + str(index + 1)
                    self.sample(kind + "-" + str(index + 1).zfill(2), index + 1, pattern[index % len(pattern)], hashes)
                selected = self.samples[first:]
                identities = {(worker["pid"], worker["startTicks"]) for sample in selected for worker in sample["workersObservedDuringRequest"]}
                scene["workerIdentities"] = [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(identities)]
                scene["reuseObserved"] = len(identities) == 1
                if self.args.require_reuse:
                    self.check(scene["reuseObserved"], kind + ": every seek reused the exact Worker PID/startTicks")
                scene["status"] = "passed"
            except Exception as error:
                scene.update(status="failed", failure=self.failure(error))
            finally:
                try:
                    self.stage = kind + ":close"
                    self.close_resources(kind)
                except Exception as error:
                    scene.update(status="failed", cleanupFailure=self.failure(error))
                save_json(self.output / (kind + "-scenario.json"), scene)


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--scenario", choices=("near", "180s", "360s", "all"), default="near")
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=180)
    parser.add_argument("--count", type=int, default=20)
    parser.add_argument("--timeout-seconds", type=float, default=90)
    parser.add_argument("--poll-ms", type=int, default=50)
    parser.add_argument("--backend", choices=("cpu", "gpu"), default="cpu")
    parser.add_argument("--ffmpeg", default="/usr/bin/ffmpeg")
    parser.add_argument("--require-reuse", action="store_true")
    args = parser.parse_args()
    if not (64 <= args.width <= 2560 and 64 <= args.height <= 1440 and 2 <= args.count <= 100
            and 1 <= args.timeout_seconds <= 600 and 10 <= args.poll_ms <= 1000):
        parser.error("size/count/deadline/poll outside finite probe bounds")
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    return args


def main():
    args = arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    probe = SeekProbe(args.output, args.dist, args)
    failures = []
    try:
        probe.run()
    except Exception as error:
        failures.append(probe.failure(error))
    finally:
        if probe.active_preview or probe.active_project:
            try:
                probe.close_resources("failure")
            except Exception as error:
                failures.append(probe.failure(error, "resource-cleanup"))
        try:
            probe.stop("seek")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        try:
            scanned = probe.scan_credentials()
        except Exception as error:
            failures.append(probe.failure(error, "credential-scan"))
            scanned = None
        summary = {"status": "passed" if not failures and probe.scenarios and all(s["status"] == "passed" for s in probe.scenarios) else "failed",
            "failures": failures, "scenarios": probe.scenarios, "samples": probe.samples,
            "credentialScannedFiles": scanned, "httpRequests": probe.request_count, "assertions": len(probe.assertions),
            "scope": "HTTP seek -> exact frame ready -> full PNG download; not UI-visible latency. Baseline observes PID reuse unless --require-reuse."}
        save_json(args.output / "summary.json", summary)
        # Scan again to cover the summary itself, including failure paths.
        try:
            probe.scan_credentials()
        except Exception:
            summary["status"] = "failed"
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "scenarios": len(probe.scenarios), "samples": len(probe.samples)}))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
