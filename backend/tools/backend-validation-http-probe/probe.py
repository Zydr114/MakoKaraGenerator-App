#!/usr/bin/env python3
"""Bounded real HTTP -> owned Worker -> existing RuntimeFrameValidator probe.

The shared HTTP helper supplies authentication and artifact redaction only.
Credentials remain in memory; the oracle uses independently loaded saved input.
"""
import argparse
import hashlib
import importlib.util
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
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("backend_http_helper", ROOT / "tools/backend-http-probe/probe.py")
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)


class ValidationProbe(HELPER.Probe):
    def __init__(self, output, server, worker, oracle):
        super().__init__(output, server)
        self.worker = Path(worker).resolve()
        self.oracle = oracle
        self.process = None
        self.workers = {}
        self.credentials = []
        self.results = []
        self.comparisons = []
        self.server_runs = []
        self.launcher = output / "owned-worker-launcher"

    def register(self, *args, **kwargs):
        result = super().register(*args, **kwargs)
        self.credentials.extend([result["accessToken"], result["resumeToken"]])
        return result

    def mutate(self, name, method, path, value, *, metadata=None, **kwargs):
        return self.request(name, method, path, value={**value, "metadata": metadata or self.metadata(name)}, **kwargs)

    def capture_workers(self):
        for entry in Path("/proc").iterdir():
            if not entry.name.isdigit():
                continue
            try:
                stat = (entry / "stat").read_text().rsplit(")", 1)[1].split()
                command = (entry / "cmdline").read_bytes()
                if int(stat[1]) == self.process.pid and b"MakoKaraGenerator.Backend.Worker" in command:
                    self.workers[int(entry.name)] = stat[19]
            except (FileNotFoundError, ProcessLookupError, PermissionError, IndexError):
                pass

    def live_workers(self):
        live = []
        for pid, started in self.workers.items():
            try:
                stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
                if stat[19] == started:
                    live.append(pid)
            except (FileNotFoundError, ProcessLookupError, IndexError):
                pass
        return live

    def worker_signal(self, pid, action):
        stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        self.check(int(stat[1]) == self.process.pid and stat[19] == self.workers[pid],
                   "signal: exact observed process is still owned by this probe Server")
        os.kill(pid, action)

    def heartbeat(self, name):
        return self.request(name, "POST", f"/api/v1/clients/{self.client}/heartbeat", value={})

    def wait_job(self, name, accepted, expected="succeeded"):
        deadline, polls = time.monotonic() + 20, 0
        while time.monotonic() < deadline:
            polls += 1
            self.capture_workers()
            current = self.request(f"{name}-poll-{polls}", "GET", "/api/v1/jobs/" + accepted["jobId"])
            if current["state"] in ("succeeded", "failed", "cancelled", "unknown"):
                self.check(current["state"] == expected, name + ": expected drained terminal " + expected)
                self.check(not current["canCancel"], name + ": terminal cannot be cancelled")
                self.results.append(current)
                return current
            time.sleep(0.05)
        raise AssertionError(name + ": bounded task did not converge")

    def start_validation(self, name, session, revision, frames, *, metadata=None, **kwargs):
        self.heartbeat(name + "-heartbeat")
        return self.mutate(name, "POST", f"/api/v1/project-sessions/{session}/validation-jobs",
                           {"expectedRevision": revision, "framesPerOutput": frames},
                           metadata=metadata, status=202, **kwargs)

    def wait_active(self, name, accepted):
        deadline, polls = time.monotonic() + 8, 0
        while time.monotonic() < deadline:
            self.capture_workers()
            polls += 1
            state = self.request(f"{name}-active-{polls}", "GET", "/api/v1/jobs/" + accepted["jobId"])
            live = self.live_workers()
            if state["state"] == "running" and live:
                self.check(len(live) == 1, name + ": one actual owned Worker for one active task")
                return state, live[0]
            self.check(state["state"] not in ("succeeded", "failed", "cancelled"), name + ": task still active before control")
            time.sleep(0.01)
        raise AssertionError(name + ": no actual Worker became active")

    def cancel(self, name, job, metadata=None):
        return self.request(name, "POST", "/api/v1/jobs/" + job["jobId"] + "/cancel",
                            value=metadata or self.metadata(name))

    def read_report(self, name, job):
        reference = job["validationResult"]["report"]
        headers, raw = self.binary(name, "/api/v1/jobs/" + job["jobId"] + "/validation-result")
        self.check(len(raw) == reference["sizeBytes"], name + ": exact immutable sidecar size")
        self.check(hashlib.sha256(raw).hexdigest() == reference["sha256"], name + ": exact immutable sidecar SHA256")
        self.check(headers["Content-Type"].split(";")[0] == "application/json", name + ": JSON report media type")
        report = json.loads(raw)
        self.check(report["succeeded"] == job["validationResult"]["passed"], name + ": result and complete report agree")
        page = self.request(name + "-diagnostics", "GET", "/api/v1/jobs/" + job["jobId"] + "/validation-diagnostics?limit=1")
        self.check(page["jobId"] == job["jobId"] and page["reportSha256"] == reference["sha256"],
                   name + ": diagnostics page binds job and immutable report")
        self.check(page["diagnostics"] == report["diagnostics"] == [] and page.get("nextCursor") is None,
                   name + ": real static diagnostics are empty and complete")
        return report, raw

    def report_boundaries(self, terminal, raw):
        path = "/api/v1/jobs/" + terminal["jobId"]
        self.request("report-with-invalid-auth", "GET", path + "/validation-result",
                     headers={"Authorization": "Bearer " + "0" * 64}, status=401, code="unauthorizedClient")
        for limit in (0, 129):
            self.request("diagnostic-page-budget-" + str(limit), "GET", path + "/validation-diagnostics?limit=" + str(limit),
                         status=400, code="diagnosticLimitExceeded")
        self.request("foreign-report-cursor", "GET", path + "/validation-diagnostics?cursor=foreign&limit=1",
                     status=409, code="diagnosticCursorExpired")
        sidecar = self.output / "state/validation-reports" / (terminal["validationResult"]["report"]["reportId"] + ".json")
        self.check(sidecar.read_bytes() == raw, "sidecar: owned persisted bytes equal authenticated report")
        try:
            corrupted = bytes([raw[0] ^ 1]) + raw[1:]
            sidecar.write_bytes(corrupted)
            self.request("corrupt-owned-sidecar-rejected", "GET", path + "/validation-result", status=500, code="taskExecutionFailed")
            retained = self.request("corrupt-sidecar-job-retained", "GET", path)
            self.check(retained == terminal, "integrity failure: immutable job identity and completed result are not rewritten")
        finally:
            sidecar.write_bytes(raw)
        self.binary("restored-exact-owned-sidecar", path + "/validation-result", expected=raw)
        self.request("current-host-not-a-recovery-report", "GET", "/api/v1/recovery-jobs/" + terminal["jobId"] + "/validation-result",
                     status=404, code="jobNotFound")

    def terminal_events(self, name, cursor, job):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + "/api/v1/events", headers={
            "X-Mako-Client-Id": self.client, "X-Mako-Host-Instance-Id": self.host,
            "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": cursor})
        values, current = [], []
        with self.http.open(request, timeout=8) as response:
            self.check(response.status == 200, name + ": authenticated SSE replay")
            while len(values) < 64:
                line = response.readline().decode("utf-8").rstrip("\r\n")
                if line:
                    current.append(line)
                    continue
                if not current:
                    continue
                data = [value[6:] for value in current if value.startswith("data: ")]
                current = []
                if not data:
                    continue
                event = json.loads("\n".join(data))
                values.append(event)
                if event.get("type") == "job.terminal" and (event.get("job") or {}).get("jobId") == job["jobId"]:
                    self.check(event["job"]["state"] == job["state"], name + ": SSE terminal and authoritative job agree")
                    HELPER.save_json(self.output / (name + "-sse.json"), values)
                    return values
        raise AssertionError(name + ": missing replayed task terminal")

    def oracle_compare(self, name, saved, frames, job, report, events):
        prefix = self.output / (name + "-oracle")
        completed = subprocess.run(self.oracle + [str(saved), str(prefix), str(frames)],
                                   capture_output=True, timeout=35, check=False)
        (self.output / (name + "-oracle.stdout.log")).write_bytes(completed.stdout + completed.stderr)
        self.check(completed.returncode == 0, name + ": independent real RuntimeFrameValidator completed")
        oracle = json.loads(Path(str(prefix) + ".json").read_text())
        for key in ("succeeded", "opaqueFrames", "transparentFrames", "issues", "diagnostics"):
            self.check(report[key] == oracle[key], name + ": HTTP and independent Runtime report " + key)
        source = oracle["source"]
        self.check(source["canvasWidth"] == job["input"]["canvasWidth"] == 1920
                   and source["canvasHeight"] == job["input"]["canvasHeight"] == 1080,
                   name + ": actual local FullHD validation geometry")
        self.check(job["input"]["framesPerOutput"] == source["framesPerOutput"] == frames,
                   name + ": immutable accepted frame plan")
        self.check(all(job["input"][key] == source[key] for key in ("frameRateNumerator", "frameRateDenominator", "outputDurationUs")),
                   name + ": persisted oracle and accepted task share exact frame timing and duration")
        self.check(source["sourceProfile"] == job["input"]["sourceProfile"]
                   and source["snapshotSha256"] == job["input"]["sourceSha256"],
                   name + ": independent loaded pinned closure matches exact task profile and encoded input SHA")
        last = oracle["progress"][-1]
        rendering = [value["job"]["progress"] for value in events if (value.get("job") or {}).get("jobId") == job["jobId"]
                     and (value["job"].get("progress") or {}).get("stage") == "rendering"]
        self.check(bool(rendering) and all(rendering[-1][key] == last[key] for key in last),
                   name + ": actual final rendering SSE progress matches Runtime oracle")
        self.check(job["progress"]["completedFrames"] == last["completedFrames"]
                   and job["progress"]["totalFrames"] == last["totalFrames"], name + ": terminal disposing progress preserves actual frame counts")
        self.comparisons.append({"name": name, "framesPerOutput": frames, "reportMatches": True,
                                 "canvasWidth": 1920, "canvasHeight": 1080, "actualRuntimeDiagnostics": len(oracle["diagnostics"]),
                                 "reportSha256": job["validationResult"]["report"]["sha256"]})

    def exercise(self):
        health = self.request("health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.register("owner-handshake")
        capability = self.request("validation-capabilities", "GET", "/api/v1/validation-capabilities")
        for key, value in {"enabled": True, "sourceProfile": "static-lyrics-scene-v1", "maxFramesPerOutput": 600,
                           "maxCanvasWidth": 1920, "maxCanvasHeight": 1080, "maxRawFrameBytes": 8 * 1024 * 1024,
                           "operationTimeoutSeconds": 8, "maxReportBytes": 256 * 1024,
                           "maxIssues": 256, "maxOccurrences": 512, "maxRunning": 2,
                           "maxRunningPerClient": 1, "maxRunningPerProject": 1, "maxQueued": 16,
                           "maxQueuedPerClient": 4, "maxDiagnosticsPageSize": 128}.items():
            self.check(capability[key] == value, "capabilities: actual " + key)
        lyrics_bytes = b"[00:00.00]Actual formal validation\n[00:01.00]Fixed accepted source\n"
        wav = self.output / "fixture.wav"
        with wave.open(str(wav), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(44100)
            cycle = b"".join(struct.pack("<h", int(1200 * math.sin(2 * math.pi * 440 * i / 44100))) for i in range(44100))
            audio.writeframes(cycle * 20)
        asset = self.request("real-audio-upload", "POST", "/api/v1/assets", raw=wav.read_bytes(),
                             content_type="audio/wav", headers={"X-Mako-Asset-Name": "fixture.wav"}, status=201)
        lyrics = self.request("lyrics-upload", "POST", "/api/v1/assets", raw=lyrics_bytes,
                              content_type="text/plain", headers={"X-Mako-Asset-Name": "fixture.lrc"}, status=201)
        self.create_input = {"name": "Owned static validation HTTP", "audioAssetId": asset["assetId"], "lyricsAssetId": lyrics["assetId"]}
        created = self.mutate("project-create", "POST", "/api/v1/project-sessions", self.create_input, status=201)
        self.check(created["audioDurationUs"] == 20_000_000, "fixed input: actual decoded audio duration supports the bounded 600-frame plan")
        session, revision = created["projectSessionId"], created["projectRevision"]
        path = "/api/v1/project-sessions/" + session
        handle = self.mutate("oracle-save-handle", "POST", "/api/v1/file-handles",
                             {"rootGrantId": "validation-probe-root", "relativePath": "same-project"}, status=201)
        save = self.mutate("save-oracle-source", "POST", path + "/save-jobs",
                           {"expectedRevision": revision, "targetHandleId": handle["handleId"]}, status=202)
        self.wait_job("saved-source", save)
        saved = self.output / "projects/same-project"
        self.mutate("created-source-missing-duration-rejected", "POST", path + "/validation-jobs",
                    {"expectedRevision": revision, "framesPerOutput": 3}, status=400, code="invalidRequest")
        closed = self.mutate("close-owned-preparation-source", "POST", path + "/close",
                             {"expectedRevision": revision, "policy": "requireClean", "activeJobs": "reject"}, status=202)
        self.wait_job("preparation-source-close", closed)
        # The default creation omitted its optional composition duration.
        # Change only this owned saved fixture, then exercise authorized open.
        owned_project_file = saved / "project.json"
        stored = json.loads(owned_project_file.read_text())
        self.check(stored["composition"].get("outputDurationUs") is None, "preparation: created fixture has no implicit output duration")
        stored["composition"]["outputDurationUs"] = 20_000_000
        owned_project_file.write_text(json.dumps(stored, ensure_ascii=False, indent=2) + "\n")
        self.saved_handle = handle
        opened = self.open_owned_saved_project("open-fixed-duration-source")
        session, revision = opened["projectSessionId"], opened["projectRevision"]
        path = "/api/v1/project-sessions/" + session
        oracle_input = self.output / "state/sessions" / session / "loaded-input"
        self.check(oracle_input.joinpath("project.json").is_file(), "oracle input: real authorized open produced its owned pinned closure")
        cursor = self.request("before-validation-state", "GET", f"/api/v1/clients/{self.client}/state")["eventCursor"]
        metadata = self.metadata("formal-once")
        accepted = self.start_validation("formal-real", session, revision, 3, metadata=metadata)
        _, success_pid = self.wait_active("formal-real", accepted)
        self.worker_signal(success_pid, signal.SIGSTOP)
        replay = self.start_validation("formal-identical-retry", session, revision, 3, metadata=metadata)
        self.check(replay == accepted, "receipt: exact original accepted response before/after task completion")
        self.mutate("formal-receipt-payload-conflict", "POST", path + "/validation-jobs",
                    {"expectedRevision": revision, "framesPerOutput": 4}, metadata=metadata, status=409, code="requestIdReuse")
        lines = self.request("fixed-input-lines", "GET", path + "/lyrics/lines?branchId=primary")
        target = {"branchId": lines["branchId"], "lineId": lines["lines"][0]["id"]}
        edited = self.request("edit-after-acceptance", "POST", path + "/commands",
                              value=self.command(revision, "edit-after-validation-accepted",
                                                 {"type": "lyrics.setTimeRange", "target": target, "startUs": "10000", "endUs": "700000"}))
        next_revision = edited["projectRevision"]
        self.check(next_revision != revision, "fixed input: current project changed after task acceptance")
        self.worker_signal(success_pid, signal.SIGCONT)
        terminal = self.wait_job("formal-real", accepted)
        self.check(terminal["input"]["sourceRevision"] == revision and terminal["input"]["sourceId"] == session,
                   "fixed input: result keeps exact accepted source identity after edit")
        self.check(terminal["input"]["sourceProfile"] == "static-lyrics-scene-v1"
                   and len(terminal["input"]["sourceSha256"]) == 64, "fixed input: snapshot profile and SHA identity")
        report, raw = self.read_report("formal-real-report", terminal)
        events = self.terminal_events("formal-real", cursor, terminal)
        self.oracle_compare("formal-real", oracle_input, 3, terminal, report, events)
        self.report_boundaries(terminal, raw)
        outsider = self.register("outsider-handshake", select=False)
        self.request("cross-client-job-rejected", "GET", "/api/v1/jobs/" + terminal["jobId"],
                     client=outsider["clientId"], status=404, code="jobNotFound")
        self.request("cross-client-report-rejected", "GET", "/api/v1/jobs/" + terminal["jobId"] + "/validation-result",
                     client=outsider["clientId"], status=404, code="jobNotFound")
        for frames in (0, 601):
            self.mutate("plan-rejected-" + str(frames), "POST", path + "/validation-jobs",
                        {"expectedRevision": next_revision, "framesPerOutput": frames}, status=400)
        self.mutate("unknown-validation-member", "POST", path + "/validation-jobs",
                    {"expectedRevision": next_revision, "framesPerOutput": 1, "timeoutSeconds": 1}, status=400)
        self.mutate("stale-validation-revision", "POST", path + "/validation-jobs",
                    {"expectedRevision": revision, "framesPerOutput": 1}, status=409, code="revisionConflict")
        self.exercise_cancellation(session, next_revision)
        self.exercise_timeout(session, next_revision)
        self.exercise_worker_failure(session, next_revision)
        self.exercise_report_publication_failure(session, next_revision)
        self.exercise_dormancy(session, next_revision)
        self.exercise_close(session, next_revision, terminal, raw)
        self.exercise_shutdown_recovery(terminal, raw)

    def exercise_cancellation(self, session, revision):
        running = self.start_validation("active-cancel-task", session, revision, 600)
        _, pid = self.wait_active("active-cancel", running)
        self.worker_signal(pid, signal.SIGSTOP)
        queued_jobs = [self.start_validation("queued-cancel-task-" + str(index), session, revision, 600) for index in range(4)]
        self.check(all(job["state"] == "queued" for job in queued_jobs), "queue: four per-client queued tasks keep the one active slot")
        self.mutate("queue-client-budget-rejected", "POST", f"/api/v1/project-sessions/{session}/validation-jobs",
                    {"expectedRevision": revision, "framesPerOutput": 600}, status=429, code="requestQuotaExceeded")
        for index, queued in enumerate(queued_jobs):
            self.cancel("cancel-queued-" + str(index), queued)
            terminal = self.wait_job("queued-cancel-" + str(index), queued, "cancelled")
            self.check(terminal.get("progress") is None and terminal.get("validationResult") is None,
                       "queued cancellation: no actual rendered frames or invented report")
        metadata = self.metadata("cancel-active-once")
        cancelled = self.cancel("cancel-active", running, metadata)
        repeated = self.cancel("cancel-active-repeat", running, metadata)
        self.check(cancelled == repeated, "cancel receipt: one accepted cancellation response")
        self.check(cancelled["cancellationRequested"], "active cancellation: accepted request marks intent")
        terminal = self.wait_job("active-cancel", running, "cancelled")
        self.check(pid not in self.live_workers(), "active cancellation: actual stopped Worker is killed and drained before terminal")
        self.check(terminal.get("validationResult") is None, "active cancellation: no fabricated success or partial complete report")

    def exercise_timeout(self, session, revision):
        running = self.start_validation("deadline-task", session, revision, 600)
        _, pid = self.wait_active("deadline", running)
        self.worker_signal(pid, signal.SIGSTOP)
        terminal = self.wait_job("deadline", running, "failed")
        self.check(terminal["failureCode"] == "WorkerTaskTimedOut" and not terminal["cancellationRequested"],
                   "deadline: stable timeout failure differs from explicit cancellation")
        self.check(pid not in self.live_workers(), "deadline: actual stopped Worker drained before failure terminal")
        self.check(terminal.get("validationResult") is None, "deadline: no placeholder formal validation report")

    def exercise_worker_failure(self, session, revision):
        running = self.start_validation("owned-worker-exit-task", session, revision, 600)
        _, pid = self.wait_active("owned-worker-exit", running)
        self.worker_signal(pid, signal.SIGTERM)
        failed = self.wait_job("owned-worker-exit", running, "failed")
        self.check(bool(failed.get("failureCode")) and failed["failureCode"] != "WorkerTaskTimedOut",
                   "worker exit: actual transport failure stable code differs from timeout")
        self.check(not failed["cancellationRequested"] and failed.get("validationResult") is None,
                   "worker exit: no user cancellation or fabricated Runtime report")
        self.check(pid not in self.live_workers(), "worker exit: exact owned terminated process is drained")
        original = self.launcher.read_bytes()
        self.check(self.launcher.parent.resolve() == self.output.resolve(), "unavailable: only probe-owned launcher is removed")
        try:
            self.launcher.unlink()
            unavailable = self.start_validation("owned-worker-unavailable-task", session, revision, 1)
            failed = self.wait_job("owned-worker-unavailable", unavailable, "failed")
            self.check(failed["failureCode"] == "WorkerUnavailable" and failed.get("validationResult") is None,
                       "unavailable: first Worker open preserves stable failure without a placeholder report")
        finally:
            self.launcher.write_bytes(original)
            self.launcher.chmod(0o700)

    def exercise_report_publication_failure(self, session, revision):
        directory = self.output / "state/validation-reports"
        original_mode = directory.stat().st_mode & 0o777
        before = set(directory.iterdir())
        try:
            directory.chmod(0o500)
            accepted = self.start_validation("owned-sidecar-publish-failure", session, revision, 1)
            failed = self.wait_job("owned-sidecar-publish", accepted, "failed")
            self.check(failed.get("validationResult") is None and failed["failureCode"] == "TaskExecutionFailed",
                       "sidecar publication: actual filesystem refusal cannot become success or a downloadable report")
            self.check(set(directory.iterdir()) == before, "sidecar publication: previous reports remain and no candidate is leaked")
        finally:
            directory.chmod(original_mode)

    def exercise_dormancy(self, session, revision):
        running = self.start_validation("dormant-owner-task", session, revision, 600)
        _, pid = self.wait_active("dormant-owner", running)
        self.worker_signal(pid, signal.SIGSTOP)
        lease = self.heartbeat("before-dormancy-heartbeat")
        expires = HELPER.datetime.fromisoformat(lease["leaseExpiresAtUtc"].replace("Z", "+00:00"))
        time.sleep(max(0, (expires - HELPER.utc_now()).total_seconds()) + 0.2)
        dormant = self.request("dormant-client-state", "GET", f"/api/v1/clients/{self.client}/state")
        self.check(dormant["dormant"], "lease: actual owner lease became dormant")
        retained = self.request("job-survives-client-dormancy", "GET", "/api/v1/jobs/" + running["jobId"])
        self.check(retained["state"] == "running" and not retained["cancellationRequested"],
                   "lease: accepted formal task remains active after HTTP client dormancy")
        original_client = self.client
        self.register("same-host-owner-resume", resume=self.registration)
        self.check(self.client == original_client, "resume: original task ownership is retained")
        self.cancel("cancel-resumed-active-task", running)
        terminal = self.wait_job("resumed-active-cancel", running, "cancelled")
        self.check(pid not in self.live_workers() and terminal.get("validationResult") is None,
                   "resume: owned Worker drained without a fabricated result")

    def exercise_close(self, session, revision, successful, raw):
        running = self.start_validation("close-owned-task", session, revision, 600)
        _, pid = self.wait_active("close-owned", running)
        self.worker_signal(pid, signal.SIGSTOP)
        path = f"/api/v1/project-sessions/{session}/close"
        self.mutate("close-with-active-rejected", "POST", path,
                    {"expectedRevision": revision, "policy": "discard", "activeJobs": "reject"}, status=409, code="activeJobs")
        closed = self.mutate("close-cancel-and-wait", "POST", path,
                             {"expectedRevision": revision, "policy": "discard", "activeJobs": "cancelAndWait"}, status=202)
        self.wait_job("explicit-close", closed)
        self.wait_job("task-cancelled-by-close", running, "cancelled")
        self.check(pid not in self.live_workers(), "close: project close waits for actual Worker drain")
        self.binary("completed-report-survives-project-close", "/api/v1/jobs/" + successful["jobId"] + "/validation-result", expected=raw)
        self.mutate("closed-source-rejected", "POST", f"/api/v1/project-sessions/{session}/validation-jobs",
                    {"expectedRevision": revision, "framesPerOutput": 1}, status=404, code="sessionNotFound")

    def open_owned_saved_project(self, name):
        accepted = self.mutate(name, "POST", "/api/v1/project-sessions/open-jobs",
                              {"projectDirectoryHandleId": self.saved_handle["handleId"]}, status=202)
        opened = self.wait_job(name, accepted)["result"]
        self.check(not opened["isReadOnly"], name + ": authorized opened fixed fixture is writable")
        return opened

    def start_server(self, environment, name):
        log_path = self.output / (name + ".stdout.log")
        with log_path.open("w") as log:
            self.process = subprocess.Popen(self.server_command + ["--urls", "http://127.0.0.1:0"],
                                            env=environment, stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        self.base_url = None
        while time.monotonic() < deadline:
            ready = [line for line in log_path.read_text().splitlines() if line.startswith("BACKEND_READY ")]
            if ready:
                self.base_url = ready[-1].split()[1]
                break
            if self.process.poll() is not None:
                raise AssertionError("Server exited before readiness")
            time.sleep(0.05)
        self.check(self.base_url is not None, name + ": isolated self-contained Server ready")

    def stop_server(self, name):
        self.capture_workers()
        self.process.terminate()
        code = self.process.wait(timeout=25)
        self.check(code == 0, name + ": normal owned Server shutdown exit0")
        self.check(not self.live_workers(), name + ": shutdown waits for all observed actual Workers")
        self.server_runs.append({"name": name, "pid": self.process.pid, "exitCode": code, "forcedKill": False})

    def exercise_shutdown_recovery(self, successful, raw):
        old_host, owner = self.host, self.client
        source = self.open_owned_saved_project("shutdown-owned-source-open")
        running = self.start_validation("shutdown-owned-task", source["projectSessionId"], source["projectRevision"], 600)
        _, pid = self.wait_active("shutdown-owned", running)
        self.worker_signal(pid, signal.SIGSTOP)
        self.stop_server("shutdown-with-active-validation")
        self.start_server(self.environment, "server-restarted")
        health = self.request("restart-health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.check(self.host != old_host, "restart: new authoritative host identity")
        self.register("new-host-profile-recovery-authorized-client")
        self.check(self.client != owner, "restart: new bootstrap-authorized profile client gets a new identity")
        recovered = self.request("restart-recovered-jobs", "GET", "/api/v1/recovery-jobs")["jobs"]
        prior = next(value for value in recovered if value["job"]["jobId"] == successful["jobId"])
        self.check(prior["previousHostInstanceId"] == old_host and prior["job"]["state"] == "succeeded",
                   "restart: successful registered report remains an old-host terminal fact")
        self.check(prior["job"]["input"] == successful["input"]
                   and prior["job"]["validationResult"] == successful["validationResult"],
                   "restart: fixed source plan and complete report hash/size are retained")
        cancelled = next(value["job"] for value in recovered if value["job"]["jobId"] == running["jobId"])
        self.check(cancelled["state"] == "cancelled" and cancelled.get("validationResult") is None,
                   "shutdown: active task cancellation is drained and persisted without an invented report")
        path = "/api/v1/recovery-jobs/" + successful["jobId"] + "/validation-result"
        self.binary("restart-authenticated-report", path, expected=raw)
        outsider = self.register("restart-outsider", select=False)
        self.binary("restart-second-profile-authorized-client-report", path, client=outsider["clientId"], expected=raw)
        self.request("restart-nonauthenticated-report-rejected", "GET", path,
                     headers={"Authorization": "Bearer " + "0" * 64}, status=401, code="unauthorizedClient")
        self.check(not self.live_workers(), "restart: no recovered task is automatically replayed in a Worker")

    def run(self):
        environment = os.environ.copy()
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "MAKO_BACKEND_FAKE_MEDIA", "MAKO_BACKEND_CREDENTIAL_FILE"):
            environment.pop(key, None)
        self.bootstrap = secrets.token_hex(32)
        self.check(self.worker.is_file(), "deployment: explicitly configured real Worker apphost exists")
        launcher = ("#!/bin/sh\nexec '" + str(self.worker).replace("'", "'\\''") + "' \"$@\"\n").encode()
        self.launcher.write_bytes(launcher)
        self.launcher.chmod(0o700)
        environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap,
                           MAKO_BACKEND_RESOURCE_ROOT=str(self.output / "resources"),
                           MAKO_BACKEND_STATE_ROOT=str(self.output / "state"),
                           MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({"validation-probe-root": str(self.output / "projects")}),
                           MAKO_BACKEND_CLIENT_TOKEN_SECONDS="3600", MAKO_BACKEND_CLIENT_LEASE_SECONDS="2",
                           MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS="8", MAKO_BACKEND_WORKER_PATH=str(self.launcher))
        self.environment = environment
        (self.output / "projects").mkdir()
        failure, code, forced = None, None, False
        try:
            self.start_server(environment, "server")
            self.exercise()
        except Exception as error:
            failure = str(error)
            (self.output / "failure.txt").write_text(traceback.format_exc())
        finally:
            if self.process is not None:
                self.capture_workers()
                if self.process.poll() is None:
                    self.process.terminate()
                try:
                    code = self.process.wait(timeout=25)
                except subprocess.TimeoutExpired:
                    forced = True
                    self.process.kill()
                    code = self.process.wait(timeout=5)
                self.server_runs.append({"name": "final-shutdown", "pid": self.process.pid, "exitCode": code, "forcedKill": forced})
            # Even a failed probe must not leave one of its previously verified
            # private children stopped under a reparented process identity.
            for pid in self.live_workers():
                try:
                    stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
                    if stat[19] == self.workers[pid]:
                        os.kill(pid, signal.SIGKILL)
                        forced = True
                except (FileNotFoundError, ProcessLookupError):
                    pass
            drain_deadline = time.monotonic() + 5
            while self.live_workers() and time.monotonic() < drain_deadline:
                time.sleep(0.05)
            self.launcher.unlink(missing_ok=True)
            bootstrap_files = [self.output / "resources/bootstrap.json", self.output / "resources/bootstrap.cap"]
            created_bootstrap_file = any(path.exists() for path in bootstrap_files)
            for path in bootstrap_files:
                path.unlink(missing_ok=True)
            scan_start = len(self.assertions)
            if failure is None:
                self.check(code == 0 and not forced, "shutdown: owned Server exited cleanly without forced kill")
                self.check(not self.live_workers(), "shutdown: all observed actual owned Workers exited")
                self.check(not created_bootstrap_file, "credentials: no inherited bootstrap credential file")
                scan_start = len(self.assertions)
                for path in self.output.rglob("*"):
                    if path.is_file():
                        raw = path.read_bytes()
                        self.check(not any(value.encode() in raw for value in [self.bootstrap, *self.credentials]),
                                   "credential artifact scan: " + str(path.relative_to(self.output)))
            HELPER.save_json(self.output / "manifest.json", {"succeeded": failure is None, "failure": failure,
                "requestCount": self.request_count, "workflowAssertions": scan_start, "credentialScanAssertions": len(self.assertions) - scan_start,
                "assertionCount": len(self.assertions), "assertions": self.assertions, "results": self.results,
                "comparisons": self.comparisons, "serverPid": self.process.pid if self.process else None,
                "serverRuns": self.server_runs,
                "serverExitCode": code, "forcedKill": forced, "observedWorkerPids": list(self.workers),
                "survivingWorkerPids": self.live_workers(), "serverSha256": hashlib.sha256(Path(self.server_command[0]).read_bytes()).hexdigest(),
                "workerSha256": hashlib.sha256(self.worker.read_bytes()).hexdigest(), "launcherSha256": hashlib.sha256(launcher).hexdigest(),
                "recoveryAuthorization": "Previous-host registered report uses existing bootstrap-authorized local profile recovery; old client identities and credentials are not restored across hosts. Live job/report routes enforce client ownership.",
                "scope": "Fixed static project real RuntimeFrameValidator dual outputs in task Worker; controlled owned process pause/cancel/timeout; no script/HTTP export/audio-analysis admission."})
        if failure:
            raise SystemExit(failure)
        print(json.dumps({"succeeded": True, "requests": self.request_count, "assertions": len(self.assertions), "serverExitCode": code}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--server", nargs="+", required=True)
    parser.add_argument("--worker", required=True)
    parser.add_argument("--oracle", nargs="+", required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    ValidationProbe(args.output.resolve(), args.server, args.worker, args.oracle).run()
