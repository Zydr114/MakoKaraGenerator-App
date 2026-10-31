#!/usr/bin/env python3
"""Real Server -> managed Worker -> Runtime.Hosting PNG preview acceptance.

Credentials stay in memory. Requests use the existing HTTP probe redaction rules;
no server store is injected and no placeholder renderer is used.
"""
import argparse
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import secrets
import signal
import struct
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request
import wave

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("existing_backend_probe", ROOT / "tools/backend-http-probe/probe.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PreviewProbe(MODULE.Probe):
    def __init__(self, output, server, oracle):
        super().__init__(output, server)
        self.oracle = oracle
        self.comparisons = []
        self.server_process = None
        self.worker_processes = {}
        self.registration_secrets = []
        self.transport_failure = None
        self.startup_failure = None
        self.owned_worker_launcher = output / "owned-worker-launcher"
        self.worker_launcher_sha256 = None

    def register(self, *args, **kwargs):
        result = super().register(*args, **kwargs)
        self.registration_secrets.extend([result["accessToken"], result["resumeToken"]])
        return result

    def capture_workers(self):
        if self.server_process is None:
            return
        for proc in Path("/proc").iterdir():
            if not proc.name.isdigit():
                continue
            try:
                stat = (proc / "stat").read_text().rsplit(")", 1)[1].split()
                command = (proc / "cmdline").read_bytes()
                if int(stat[1]) == self.server_process.pid and b"MakoKaraGenerator.Backend.Worker" in command:
                    self.worker_processes[int(proc.name)] = stat[19]
            except (FileNotFoundError, ProcessLookupError, PermissionError, IndexError):
                pass

    def live_captured_workers(self):
        live = []
        for pid, started in self.worker_processes.items():
            try:
                stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
                if stat[19] == started:
                    live.append(pid)
            except (FileNotFoundError, ProcessLookupError, IndexError):
                pass
        return live

    def mutate(self, name, method, path, value, *, metadata=None, **kwargs):
        return self.request(name, method, path,
                            value={**value, "metadata": metadata or self.metadata(name)}, **kwargs)

    def status(self, name, preview):
        body = self.request(name, "GET", "/api/v1/preview-sessions/" + preview)
        return body.get("session", body)

    def ready(self, name, preview, sequence):
        deadline = time.monotonic() + 35
        polls = 0
        while time.monotonic() < deadline:
            polls += 1
            current = self.status(name + f"-poll-{polls}", preview)
            if current.get("latestFrame", {}).get("requestSeq") == str(sequence):
                self.check(current["state"] == "ready", name + ": actual frame ready")
                self.capture_workers()
                return current
            if current.get("state") == "failed":
                raise AssertionError(name + ": real worker render failed: " + json.dumps(current))
            time.sleep(0.05)
        raise AssertionError(name + ": frame did not become ready")

    def binary_method(self, name, method, path, *, headers=None, status=200, expected=b""):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + path, method=method, headers={
            "X-Mako-Client-Id": self.client, "X-Mako-Host-Instance-Id": self.host,
            "Authorization": "Bearer " + self.tokens[self.client], **(headers or {})})
        try:
            response = self.http.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            data, actual, received = response.read(), response.status, dict(response.headers.items())
        MODULE.save_json(self.output / f"{self.request_count:03d}-{name}.response.json",
                         {"method": method, "path": path, "status": actual, "headers": received,
                          "bodyBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        self.check(actual == status, name + f": HTTP{status} actual {actual}")
        self.check(data == expected, name + ": exact expected response bytes")
        return received

    def compare(self, name, frame, saved):
        path = frame["content"]["contentUrl"]
        headers, png = self.binary(name + "-png", path)
        self.check(len(png) == frame["content"]["sizeBytes"], name + ": actual PNG size")
        self.check(hashlib.sha256(png).hexdigest() == frame["content"]["sha256"], name + ": actual PNG SHA")
        self.check(headers["ETag"] == '"' + frame["content"]["sha256"] + '"', name + ": immutable SHA ETag")
        with Image.open(io.BytesIO(png)) as image:
            image.load()
            self.check(image.size == (frame["width"], frame["height"]), name + ": decoded IHDR dimensions")
            rgba = image.convert("RGBA").tobytes()
        prefix = self.output / (name + "-independent-runtime")
        result = subprocess.run(self.oracle + [str(saved), str(prefix), frame["timeUs"],
                                str(frame["width"]), str(frame["height"]), "true"],
                                capture_output=True, timeout=35, check=False)
        (self.output / (name + "-oracle.stdout.log")).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ": independent Runtime.Hosting actual render")
        bgra = Path(str(prefix) + ".bgra").read_bytes()
        self.check(len(bgra) == len(rgba), name + ": runtime and decoded byte count")
        max_error, alpha_error = 0, 0
        transparent = opaque = partial = 0
        for offset in range(0, len(bgra), 4):
            alpha = rgba[offset + 3]
            alpha_error = max(alpha_error, abs(alpha - bgra[offset + 3]))
            transparent += alpha == 0
            opaque += alpha == 255
            partial += 0 < alpha < 255
            for channel, source in ((0, 2), (1, 1), (2, 0)):
                premultiplied = (rgba[offset + channel] * alpha + 127) // 255
                max_error = max(max_error, abs(premultiplied - bgra[offset + source]))
        self.check(alpha_error == 0, name + ": exact runtime alpha for every decoded pixel")
        self.check(max_error <= 1, name + ": every decoded RGB matches runtime premul within one-byte quantization")
        self.check(transparent > 0 and partial + opaque > 0, name + ": actual transparent background and rendered lyrics")
        comparison = {"name": name, "pixels": len(rgba) // 4, "maxPremulRgbError": max_error,
                      "maxAlphaError": alpha_error, "transparentPixels": transparent,
                      "partialPixels": partial, "opaquePixels": opaque,
                      "pngSha256": hashlib.sha256(png).hexdigest(),
                      "decodedRgbaSha256": hashlib.sha256(rgba).hexdigest(),
                      "independentRuntimeBgraSha256": hashlib.sha256(bgra).hexdigest()}
        self.comparisons.append(comparison)
        MODULE.save_json(self.output / (name + "-pixel-comparison.json"), comparison)
        return headers, png

    def preview_events(self, cursor, frame):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + "/api/v1/events", headers={
            "X-Mako-Client-Id": self.client, "X-Mako-Host-Instance-Id": self.host,
            "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": cursor})
        messages, current = [], []
        with self.http.open(request, timeout=8) as response:
            self.check(response.status == 200, "SSE: real authenticated replay response")
            while len(messages) < 16:
                line = response.readline().decode("utf-8").rstrip("\r\n")
                if line:
                    current.append(line)
                    continue
                if not current:
                    continue
                message = "\n".join(current)
                messages.append(message)
                current = []
                if "event: preview.frameReady" in message and frame["frameId"] in message:
                    self.check(frame["previewGeneration"] in message and frame["sourceRevision"] in message,
                               "SSE: actual ready frame references same generation and revision")
                    MODULE.save_json(self.output / "actual-preview-sse.json", messages)
                    return
        raise AssertionError("SSE did not replay the actual completed frame")

    def exercise(self):
        health = self.request("health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.register("owner-handshake")
        runtime = self.request("preview-runtime-info", "GET", "/api/v1/runtime-info")
        self.check(runtime["enabled"] and runtime["executionMode"] == "worker-process", "runtime: real managed Worker configured")
        lyrics_bytes = b"[00:00.00]Real Worker PNG\n[00:01.00]Second rendered line\n"
        (self.output / "fixture.lrc").write_bytes(lyrics_bytes)
        wav = self.output / "fixture.wav"
        with wave.open(str(wav), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(44100)
            audio.writeframes(b"".join(struct.pack("<h", int(1200 * math.sin(2 * math.pi * 440 * i / 44100)))
                                       for i in range(88200)))
        asset = self.request("audio-upload", "POST", "/api/v1/assets", raw=wav.read_bytes(),
                             content_type="audio/wav", headers={"X-Mako-Asset-Name": "fixture.wav"}, status=201)
        lyrics = self.request("lyrics-upload", "POST", "/api/v1/assets", raw=lyrics_bytes,
                              content_type="text/plain", headers={"X-Mako-Asset-Name": "fixture.lrc"}, status=201)
        project = self.mutate("project-create", "POST", "/api/v1/project-sessions",
                              {"name": "Real preview HTTP acceptance", "audioAssetId": asset["assetId"],
                               "lyricsAssetId": lyrics["assetId"]}, status=201)
        self.check(project["audioDurationUs"] == 2000000, "new project: real audio duration")
        session_id = project["projectSessionId"]
        source = {"kind": "project", "sourceId": session_id, "sourceRevision": project["projectRevision"]}
        handle = self.mutate("oracle-directory-handle", "POST", "/api/v1/file-handles",
                             {"rootGrantId": "preview-probe-root", "relativePath": "same-project"}, status=201)
        saved = self.mutate("save-same-project", "POST", "/api/v1/project-sessions/" + session_id + "/save-jobs",
                            {"expectedRevision": source["sourceRevision"], "targetHandleId": handle["handleId"]}, status=202)
        self.job("same-project-save", saved)
        saved_directory = self.output / "projects/same-project"

        create_meta = self.metadata("preview-create-once")
        create_value = {"source": source, "size": {"width": 320, "height": 180}, "alphaMode": "straight"}
        created = self.mutate("preview-create", "POST", "/api/v1/preview-sessions", create_value,
                              metadata=create_meta, status=201)
        retry = self.mutate("preview-create-retry", "POST", "/api/v1/preview-sessions", create_value,
                            metadata=create_meta, status=201)
        self.check(retry == created, "preview create receipt: exact original response")
        preview = created.get("session", created)
        preview_id = preview["previewId"]
        path = "/api/v1/preview-sessions/" + preview_id
        before_frame_state = self.request("before-frame-client-state", "GET", f"/api/v1/clients/{self.client}/state")
        first_meta = self.metadata("first-frame-once")
        first_value = {"expectedSourceRevision": source["sourceRevision"],
                       "previewGeneration": preview["previewGeneration"], "requestSeq": "1",
                       "timeUs": "200000", "intent": "playback"}
        accepted = self.mutate("first-frame", "POST", path + "/frame-requests", first_value,
                               metadata=first_meta, status=202)
        ready = self.ready("first-frame", preview_id, 1)
        duplicate = self.mutate("first-frame-retry", "POST", path + "/frame-requests", first_value,
                                metadata=first_meta, status=202)
        self.check(duplicate == accepted, "frame receipt: retry returns same accepted request")
        first = ready["latestFrame"]
        self.preview_events(before_frame_state["eventCursor"], first)
        headers, png = self.compare("first", first, saved_directory)
        url = first["content"]["contentUrl"]
        head = self.binary_method("head-frame", "HEAD", url)
        self.check(int(head["Content-Length"]) == len(png), "HEAD: full immutable byte length")
        self.binary_method("head-range", "HEAD", url, headers={"Range": "bytes=0-7"}, status=206)
        self.binary("frame-signature-range", url, headers={"Range": "bytes=0-7"}, status=206, expected=png[:8])
        self.binary("frame-suffix-range", url, headers={"Range": "bytes=-12"}, status=206, expected=png[-12:])
        self.binary("frame-etag-304", url, headers={"If-None-Match": headers["ETag"]}, status=304, expected=b"")
        self.binary("frame-invalid-range", url, headers={"Range": f"bytes={len(png)}-"}, status=416, expected=b"")
        diagnostics = self.request("frame-diagnostics", "GET", path + "/diagnostics?limit=128")
        self.check(diagnostics["previewGeneration"] == ready["previewGeneration"], "diagnostics: same generation")
        self.check(diagnostics["sourceRevision"] == source["sourceRevision"], "diagnostics: exact source revision")
        self.check(len(diagnostics["diagnostics"]) <= 128, "diagnostics: bounded published page")

        other = self.register("second-client", select=False)["clientId"]
        self.request("cross-client-frame", "GET", url, client=other, status=401, code="resourceUnauthorized")
        self.request("cross-client-preview", "GET", path, client=other, status=404, code="previewSessionNotFound")
        self.request("cross-client-diagnostics", "GET", path + "/diagnostics", client=other,
                     status=401, code="resourceUnauthorized")
        self.mutate("second-frame", "POST", path + "/frame-requests", {**first_value, "requestSeq": "2", "timeUs": "1200000"}, status=202)
        second_ready = self.ready("second-frame", preview_id, 2)
        second_headers, second_png = self.compare("second", second_ready["latestFrame"], saved_directory)
        self.check(second_png != png, "multi-frame: two lyric/time frames contain different real pixels")

        seek_value = {**first_value, "requestSeq": "3", "timeUs": "200000", "intent": "seek"}
        seek_meta = self.metadata("seek-once")
        seek = self.mutate("seek-backward", "POST", path + "/frame-requests", seek_value, metadata=seek_meta, status=202)
        self.check(seek["previewGeneration"] != ready["previewGeneration"], "seek: explicit new generation")
        self.request("old-frame-invalidated", "GET", url, status=404, code="previewFrameNotFound")
        self.mutate("old-generation-rejected", "POST", path + "/frame-requests",
                    {**first_value, "requestSeq": "4"}, status=409, code="previewGenerationConflict")
        seek_ready = self.ready("seek", preview_id, 3)
        _, seek_png = self.compare("seek", seek_ready["latestFrame"], saved_directory)
        self.check(seek_png == png, "seek: same pinned input/time returns exactly the same actual PNG")
        seek_retry = self.mutate("seek-retry", "POST", path + "/frame-requests", seek_value,
                                 metadata=seek_meta, status=202)
        self.check(seek_retry == seek, "seek receipt: old request generation retry cannot execute again")

        expires = MODULE.datetime.fromisoformat(seek_ready["latestFrame"]["content"]["expiresAtUtc"].replace("Z", "+00:00"))
        wait = max(0, (expires - MODULE.utc_now()).total_seconds()) + 0.1
        time.sleep(wait)
        seek_url = seek_ready["latestFrame"]["content"]["contentUrl"]
        self.request("frame-TTL-expired", "GET", seek_url, status=410, code="previewFrameExpired")
        self.mutate("post-expiry-next-frame", "POST", path + "/frame-requests",
                    {**first_value, "previewGeneration": seek_ready["previewGeneration"], "requestSeq": "4"}, status=202)
        after_expiry = self.ready("post-expiry", preview_id, 4)
        after_url = after_expiry["latestFrame"]["content"]["contentUrl"]
        close_value = {"expectedPreviewGeneration": after_expiry["previewGeneration"],
                       "expectedSourceRevision": source["sourceRevision"]}
        close_meta = self.metadata("preview-close-once")
        closed = self.mutate("close-preview", "POST", path + "/close", close_value, metadata=close_meta)
        closed_retry = self.mutate("close-preview-retry", "POST", path + "/close", close_value, metadata=close_meta)
        self.check(closed_retry == closed, "close receipt: exact original completion")
        self.check(bool(self.worker_processes), "worker lifecycle: observed actual child Worker processes")
        deadline = time.monotonic() + 5
        while self.live_captured_workers() and time.monotonic() < deadline:
            time.sleep(0.05)
        self.check(not self.live_captured_workers(), "worker lifecycle: close releases all observed Worker processes")
        self.request("closed-frame-cleanup", "GET", after_url, status=404, code="previewFrameNotFound")
        self.request("closed-diagnostic-cleanup", "GET", path + "/diagnostics", status=404, code="previewSessionNotFound")
        self.request("closed-preview-no-live-session", "GET", path, status=404, code="previewSessionNotFound")
        final = closed["session"]
        self.check(final["state"] == "closed", "close: terminal closed state")
        MODULE.save_json(self.output / "final-state.json", final)
        heartbeat = self.request("lease-cleanup-heartbeat", "POST", f"/api/v1/clients/{self.client}/heartbeat", value={})
        lease_created = self.mutate("lease-cleanup-preview", "POST", "/api/v1/preview-sessions", create_value, status=201)
        lease_id = lease_created["previewId"]
        self.mutate("lease-cleanup-frame", "POST", f"/api/v1/preview-sessions/{lease_id}/frame-requests",
                    {**first_value, "previewGeneration": lease_created["previewGeneration"]}, status=202)
        lease_ready = self.ready("lease-cleanup", lease_id, 1)
        self.check(bool(self.live_captured_workers()), "client lease: actual Worker running before dormancy")
        lease_expires = MODULE.datetime.fromisoformat(heartbeat["leaseExpiresAtUtc"].replace("Z", "+00:00"))
        time.sleep(max(0, (lease_expires - MODULE.utc_now()).total_seconds()) + 1.2)
        self.request("dormant-preview-removed", "GET", f"/api/v1/preview-sessions/{lease_id}",
                     status=404, code="previewSessionNotFound")
        drained = self.request("dormant-client-state", "GET", f"/api/v1/clients/{self.client}/state")
        self.check(drained["dormant"], "client lease: actual owner lease expired")
        self.request("dormant-frame-cleanup", "GET", lease_ready["latestFrame"]["content"]["contentUrl"],
                     status=404, code="previewFrameNotFound")
        self.request("dormant-diagnostic-cleanup", "GET", f"/api/v1/preview-sessions/{lease_id}/diagnostics",
                     status=404, code="previewSessionNotFound")
        self.check(not self.live_captured_workers(), "client lease: dormant preview has no surviving observed Worker")
        retained_project = self.request("project-retained-after-dormant-preview", "GET", "/api/v1/project-sessions/" + session_id)
        self.check(retained_project["projectRevision"] == source["sourceRevision"], "client lease: closing preview preserves project")
        MODULE.save_json(self.output / "dormant-state.json", drained)
        self.exercise_transport_failure(source, create_value)
        self.exercise_startup_failure(source, create_value)

    def exercise_transport_failure(self, source, create_value):
        self.request("failure-phase-heartbeat", "POST", f"/api/v1/clients/{self.client}/heartbeat", value={})
        preview = self.mutate("failure-preview-create", "POST", "/api/v1/preview-sessions", create_value, status=201)
        path = "/api/v1/preview-sessions/" + preview["previewId"]
        request = {"expectedSourceRevision": source["sourceRevision"], "previewGeneration": preview["previewGeneration"],
                   "requestSeq": "1", "timeUs": "200000", "intent": "playback"}
        self.mutate("failure-phase-real-first-frame", "POST", path + "/frame-requests", request, status=202)
        ready = self.ready("failure-phase-first-frame", preview["previewId"], 1)
        live = self.live_captured_workers()
        self.check(len(live) == 1, "controlled failure: one owned active Worker before termination")
        pid = live[0]
        stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        self.check(int(stat[1]) == self.server_process.pid and stat[19] == self.worker_processes[pid],
                   "controlled failure: target still belongs to this probe Server with exact start identity")
        os.kill(pid, signal.SIGTERM)
        deadline = time.monotonic() + 5
        while pid in self.live_captured_workers() and time.monotonic() < deadline:
            time.sleep(0.05)
        self.check(pid not in self.live_captured_workers(), "controlled failure: owned Worker actually terminated")
        failed_request = {**request, "requestSeq": "2", "timeUs": "1200000"}
        self.mutate("request-after-worker-termination", "POST", path + "/frame-requests", failed_request, status=202)
        deadline = time.monotonic() + 25
        polls, state = 0, None
        while time.monotonic() < deadline:
            polls += 1
            state = self.request(f"transport-failure-state-{polls}", "GET", path)
            if state["session"]["state"] == "failed":
                break
            time.sleep(0.05)
        self.check(state is not None and state["session"]["state"] == "failed", "controlled failure: real transport failure state")
        self.check(state["session"]["latestRequestSeq"] == "2" and "latestFrame" not in state["session"],
                   "controlled failure: failed request publishes no fabricated or previous frame")
        self.check(bool(state.get("failure")), "controlled failure: stable service failure response")
        page = self.request("transport-failure-diagnostics", "GET", path + "/diagnostics?limit=128")
        self.check(page["previewGeneration"] == ready["previewGeneration"] and page["sourceRevision"] == source["sourceRevision"],
                   "controlled failure: diagnostics bind actual request generation/source")
        self.check(len(page["diagnostics"]) == 1 and not page["hasMore"], "controlled failure: complete bounded failure page")
        diagnostic = page["diagnostics"][0]
        self.check(diagnostic["originKind"] == "transportFailure", "controlled failure: true origin metadata is transportFailure")
        self.check(diagnostic.get("occurrence") is None, "controlled failure: no invented Runtime snapshot epoch or occurrence")
        self.check(diagnostic["severity"] == "error" and bool(diagnostic["code"]), "controlled failure: actual failure code and severity")
        if state.get("renderFailureCode") is not None:
            self.check(diagnostic["code"] == state["renderFailureCode"], "controlled failure: neutral render code preserved in diagnostic")
        self.transport_failure = {"terminatedOwnedWorkerPid": pid, "state": state, "diagnostics": page,
                                  "actualRuntimeSnapshotOccurred": False,
                                  "scope": "Controlled termination of this probe's actual child Worker; transport failure, not a script diagnostic."}
        MODULE.save_json(self.output / "controlled-transport-failure.json", self.transport_failure)
        closed = self.mutate("close-failed-preview", "POST", path + "/close",
                              {"expectedPreviewGeneration": ready["previewGeneration"], "expectedSourceRevision": source["sourceRevision"]})
        self.check(closed["session"]["state"] == "closed", "controlled failure: failed preview closes cleanly")
        self.request("failed-diagnostic-closed-cleanup", "GET", path + "/diagnostics", status=404, code="previewSessionNotFound")
        self.check(not self.live_captured_workers(), "controlled failure: no observed Worker survives final close")

    def exercise_startup_failure(self, source, create_value):
        self.request("startup-failure-heartbeat", "POST", f"/api/v1/clients/{self.client}/heartbeat", value={})
        self.check(self.owned_worker_launcher.parent.resolve() == self.output.resolve()
                   and self.owned_worker_launcher.is_file(), "startup failure: only this probe's owned launcher will be removed")
        self.check(hashlib.sha256(self.owned_worker_launcher.read_bytes()).hexdigest() == self.worker_launcher_sha256,
                   "startup failure: launcher has exact bytes created by this probe")
        self.owned_worker_launcher.unlink()
        self.check(not self.owned_worker_launcher.exists(), "startup failure: configured owned launcher actually removed")
        preview = self.mutate("unavailable-preview-create", "POST", "/api/v1/preview-sessions", create_value, status=201)
        path = "/api/v1/preview-sessions/" + preview["previewId"]
        self.mutate("unavailable-first-frame", "POST", path + "/frame-requests",
                    {"expectedSourceRevision": source["sourceRevision"], "previewGeneration": preview["previewGeneration"],
                     "requestSeq": "1", "timeUs": "200000", "intent": "playback"}, status=202)
        deadline = time.monotonic() + 25
        polls, state = 0, None
        while time.monotonic() < deadline:
            polls += 1
            state = self.request(f"unavailable-startup-state-{polls}", "GET", path)
            if state["session"]["state"] == "failed":
                break
            time.sleep(0.05)
        self.check(state is not None and state["session"]["state"] == "failed", "startup failure: actual first frame preparation failed")
        self.check(state.get("renderFailureCode") == "WorkerUnavailable", "startup failure: stable neutral WorkerUnavailable survives factory Create/open")
        self.check(state["session"]["latestRequestSeq"] == "1" and "latestFrame" not in state["session"],
                   "startup failure: no fabricated first frame")
        page = self.request("unavailable-startup-diagnostics", "GET", path + "/diagnostics?limit=128")
        self.check(len(page["diagnostics"]) == 1 and not page["hasMore"], "startup failure: complete bounded failure page")
        diagnostic = page["diagnostics"][0]
        self.check(diagnostic["code"] == "WorkerUnavailable" and diagnostic["originKind"] == "transportFailure",
                   "startup failure: real typed factory failure diagnostic")
        self.check(diagnostic.get("occurrence") is None, "startup failure: no invented Runtime snapshot/epoch")
        self.check(not self.live_captured_workers(), "startup failure: no owned Worker process could start or survive")
        self.startup_failure = {"state": state, "diagnostics": page, "actualRuntimeSnapshotOccurred": False,
                                "deletedOwnedLauncherSha256": self.worker_launcher_sha256,
                                "scope": "Only the probe's 0700 exec launcher was removed after host configuration; repository Worker binary unchanged."}
        MODULE.save_json(self.output / "controlled-startup-failure.json", self.startup_failure)
        closed = self.mutate("close-unavailable-preview", "POST", path + "/close",
                              {"expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": source["sourceRevision"]})
        self.check(closed["session"]["state"] == "closed", "startup failure: unavailable preview closes cleanly")

    def run(self):
        environment = os.environ.copy()
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "MAKO_BACKEND_FAKE_MEDIA", "MAKO_BACKEND_CREDENTIAL_FILE"):
            environment.pop(key, None)
        self.bootstrap = secrets.token_hex(32)
        worker = ROOT / "src/MakoKaraGenerator.Backend.Worker/bin/Debug/net10.0/MakoKaraGenerator.Backend.Worker"
        self.check(worker.is_file(), "worker deployment: real absolute apphost exists")
        # The trusted host runs the actual Worker, with no protocol/data substitution.
        # Owning this tiny launcher lets the last case remove only probe-owned files.
        launcher = ("#!/bin/sh\nexec '" + str(worker).replace("'", "'\\''") + "' \"$@\"\n").encode()
        self.owned_worker_launcher.write_bytes(launcher)
        self.owned_worker_launcher.chmod(0o700)
        self.worker_launcher_sha256 = hashlib.sha256(launcher).hexdigest()
        MODULE.save_json(self.output / "owned-worker-launcher-original.json",
                         {"path": str(self.owned_worker_launcher), "mode": "0700", "bytes": len(launcher),
                          "sha256": self.worker_launcher_sha256, "executesRealWorker": str(worker)})
        environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap,
                           MAKO_BACKEND_RESOURCE_ROOT=str(self.output / "resources"),
                           MAKO_BACKEND_STATE_ROOT=str(self.output / "state"),
                           MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({"preview-probe-root": str(self.output / "projects")}),
                           MAKO_BACKEND_CLIENT_TOKEN_SECONDS="3600", MAKO_BACKEND_CLIENT_LEASE_SECONDS="20",
                           MAKO_BACKEND_PREVIEW_FRAME_LEASE_SECONDS="10",
                           MAKO_BACKEND_WORKER_PATH=str(self.owned_worker_launcher))
        (self.output / "projects").mkdir()
        process, failure, code, forced = None, None, None, False
        secrets_in_memory = []
        try:
            with (self.output / "server.stdout.log").open("w") as log:
                process = subprocess.Popen(self.server_command + ["--urls", "http://127.0.0.1:0"],
                                           env=environment, stdout=log, stderr=subprocess.STDOUT)
                self.server_process = process
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    lines = [line for line in (self.output / "server.stdout.log").read_text().splitlines()
                             if line.startswith("BACKEND_READY ")]
                    if lines:
                        self.base_url = lines[-1].split()[1]
                        break
                    if process.poll() is not None:
                        raise AssertionError("Server exited before readiness")
                    time.sleep(0.05)
                self.check(self.base_url is not None, "startup: real loopback Server ready")
                self.exercise()
        except Exception as error:
            failure = str(error)
            (self.output / "failure.txt").write_text(traceback.format_exc())
        finally:
            secrets_in_memory = [self.bootstrap, *self.tokens.values(), *self.registration_secrets]
            if self.registration:
                secrets_in_memory.append(self.registration["resumeToken"])
            if process is not None:
                if process.poll() is None:
                    process.terminate()
                try:
                    code = process.wait(timeout=25)
                except subprocess.TimeoutExpired:
                    forced = True
                    process.kill()
                    code = process.wait(timeout=5)
            if self.owned_worker_launcher.exists():
                self.owned_worker_launcher.unlink()
            if failure is None:
                self.check(code == 0 and not forced, "shutdown: owned Server exited cleanly without forced kill")
                self.check(not (self.output / "resources/bootstrap.json").exists(), "credentials: inherited bootstrap produced no file")
                for path in self.output.rglob("*"):
                    if path.is_file():
                        data = path.read_bytes()
                        self.check(not any(secret and secret.encode() in data for secret in secrets_in_memory),
                                   "credentials absent from artifact: " + str(path.relative_to(self.output)))
            manifest = {"succeeded": failure is None, "failure": failure, "requestCount": self.request_count,
                        "assertionCount": len(self.assertions), "assertions": self.assertions,
                        "comparisons": self.comparisons, "serverExitCode": code, "forcedKill": forced,
                        "serverPid": process.pid if process else None,
                        "observedWorkerPids": list(self.worker_processes), "survivingWorkerPids": self.live_captured_workers(),
                        "controlledTransportFailure": self.transport_failure,
                        "controlledStartupFailure": self.startup_failure,
                        "scope": "Real HTTP static lyrics -> managed Worker -> real Runtime.Hosting -> actual PNG; no script admission or native GUI acceptance."}
            MODULE.save_json(self.output / "manifest.json", manifest)
        if failure is not None:
            raise SystemExit(failure)
        print(json.dumps({"succeeded": True, "requests": self.request_count, "assertions": len(self.assertions),
                          "pixelComparisons": len(self.comparisons), "serverExitCode": code}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--server", nargs="+", required=True)
    parser.add_argument("--oracle", nargs="+", required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    PreviewProbe(args.output.resolve(), args.server, args.oracle).run()
