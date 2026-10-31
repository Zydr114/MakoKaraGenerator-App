#!/usr/bin/env python3
"""Finite real HTTP probe for package-lyrics-scene-v1 validation.

It opens probe-owned package fixtures through the public project session API,
checks the advertised validation profile, and records the complete report and
diagnostic projection returned by the matching Worker.  An old or mismatched
Worker is exercised with ``--expect-package no`` and must withhold the profile
and reject package validation explicitly.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import signal
import subprocess
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("validation_probe", ROOT / "tools/backend-validation-http-probe/probe.py")
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)

PACKAGE_PROFILE = "package-lyrics-scene-v1"


class PackageValidationProbe(MOD.ValidationProbe):
    def __init__(self, output, server, worker, oracle, expect_package=True):
        super().__init__(output, server, worker, oracle)
        self.package_jobs = []
        self.expect_package = expect_package
        self.observed_processes = {}
        self.control_workers = {}
        self.terminal_drain_checks = []
        self.crash_drain_checks = []
        self.forced_cleanup = []

    @staticmethod
    def process_identity(pid):
        try:
            stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
            return {"pid": pid, "parentPid": int(stat[1]), "startTicks": stat[19],
                    "state": stat[0], "commandSha256": hashlib.sha256(
                        Path(f"/proc/{pid}/cmdline").read_bytes()).hexdigest()}
        except (FileNotFoundError, ProcessLookupError, PermissionError, IndexError):
            return None

    def capture_workers(self):
        if self.process is None:
            return
        # Walk actual PPID relationships while identities are observable. Keep
        # descendants after reparenting by PID+startTicks, never by PID alone.
        current = {}
        for entry in Path("/proc").iterdir():
            if entry.name.isdigit():
                value = self.process_identity(int(entry.name))
                if value is not None:
                    current[value["pid"]] = value
        owned = {self.process.pid}
        for identity in self.observed_processes.values():
            now = current.get(identity["pid"])
            if now and now["startTicks"] == identity["startTicks"]:
                owned.add(identity["pid"])
        while True:
            children = {pid for pid, value in current.items() if value["parentPid"] in owned}
            if children <= owned:
                break
            owned |= children
        for pid in owned - {self.process.pid}:
            value = current.get(pid)
            if value is None:
                continue
            key = str(pid) + ":" + value["startTicks"]
            if key not in self.observed_processes:
                parent = current.get(value["parentPid"])
                self.observed_processes[key] = {**value, "observedServerPid": self.process.pid,
                    "parentIdentity": (str(parent["pid"]) + ":" + parent["startTicks"]) if parent else None}
            try:
                if b"MakoKaraGenerator.Backend.Worker" in Path(f"/proc/{pid}/cmdline").read_bytes():
                    self.workers[pid] = value["startTicks"]
            except (FileNotFoundError, ProcessLookupError, PermissionError):
                pass

    def live_observed(self, identities=None):
        values = self.observed_processes.values() if identities is None else identities
        live = []
        for identity in values:
            now = self.process_identity(identity["pid"])
            if now and now["startTicks"] == identity["startTicks"]:
                live.append(now)
        return live

    def controlled_identities(self, identity):
        keys = {str(identity["pid"]) + ":" + identity["startTicks"]}
        while True:
            children = {key for key, value in self.observed_processes.items()
                        if value.get("parentIdentity") in keys}
            if children <= keys:
                break
            keys |= children
        return [value for key, value in self.observed_processes.items() if key in keys]

    def controlled_worker(self, name, accepted):
        state, pid = self.wait_active(name, accepted)
        self.check(state.get("input", {}).get("sourceProfile") == PACKAGE_PROFILE,
                   name + ": running control job retains package source profile")
        self.capture_workers()
        identity = self.process_identity(pid)
        self.check(identity is not None and identity["startTicks"] == self.workers[pid],
                   name + ": exact owned Worker identity captured before control")
        self.control_workers[accepted["jobId"]] = identity
        self.worker_signal(pid, signal.SIGSTOP)
        return identity

    def request(self, name, method, path, **kwargs):
        self.capture_workers()
        result = super().request(name, method, path, **kwargs)
        # Assert drain immediately when terminal state is first observed, before
        # cleanup, report reads or further polling can hide a late exit.
        if method == "GET" and path.startswith("/api/v1/jobs/") and path.count("/") == 4:
            identity = self.control_workers.get(result.get("jobId"))
            if identity and result.get("state") in ("succeeded", "failed", "cancelled", "unknown"):
                self.capture_workers()
                identities = self.controlled_identities(identity)
                live = self.live_observed(identities)
                self.terminal_drain_checks.append({"jobId": result["jobId"], "state": result["state"],
                    "worker": identity, "observedWorkerTree": identities, "liveAtTerminal": live})
                self.check(not live, name + ": controlled Worker and observed descendants absent at first terminal observation")
        return result

    def assert_no_report(self, name, terminal):
        self.request(name + "-report-unavailable", "GET",
                     "/api/v1/jobs/" + terminal["jobId"] + "/validation-result",
                     status=404, code="jobNotFound")

    def oracle_generate(self):
        projects = self.output / "projects"
        projects.mkdir(parents=True, exist_ok=True)
        result = subprocess.run(["flock", "-o", "/tmp/mkg-ttml-dotnet.lock", "dotnet", str(self.oracle),
                                 "generate", str(projects)], capture_output=True, text=True)
        (self.output / "fixture-generation.log").write_text(result.stdout + result.stderr)
        self.check(result.returncode == 0, "fixture generation: independent FixtureOracle exit0")
        self.check((projects / "scale").is_dir(), "fixture generation: package scale project exists")

    def open_fixture(self, name, *, request_prefix=None):
        request_prefix = request_prefix or name
        handle = self.mutate(request_prefix + "-handle", "POST", "/api/v1/file-handles",
                             {"rootGrantId": "validation-probe-root", "relativePath": name}, status=201)
        accepted = self.mutate(request_prefix + "-open", "POST", "/api/v1/project-sessions/open-jobs",
                               {"projectDirectoryHandleId": handle["handleId"]}, status=202)
        terminal = self.wait_job(request_prefix + "-open", accepted)
        self.check(terminal.get("result") is not None, request_prefix + ": open job returned project session")
        return terminal["result"]

    def package_validation(self, name, source, frames=1):
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs"
        accepted = self.mutate(name, "POST", path,
                               {"expectedRevision": source["projectRevision"], "framesPerOutput": frames}, status=202)
        self.package_jobs.append(accepted)
        terminal = self.wait_job(name + "-terminal", accepted, expected="succeeded")
        input_value = terminal["input"]
        self.check(input_value["sourceKind"] == "project"
                   and input_value["sourceId"] == source["projectSessionId"]
                   and input_value["sourceRevision"] == source["projectRevision"]
                   and input_value["sourceProfile"] == PACKAGE_PROFILE
                   and input_value["framesPerOutput"] == frames,
                   name + ": accepted package source/profile/revision/frame plan are persisted")
        self.check(isinstance(input_value.get("sourceSha256"), str)
                   and len(input_value["sourceSha256"]) == 64
                   and all(character in "0123456789abcdef" for character in input_value["sourceSha256"]),
                   name + ": accepted package source SHA is a canonical hex digest")
        self.check(input_value["canvasWidth"] == 320 and input_value["canvasHeight"] == 180
                   and input_value["frameRateNumerator"] == 30
                   and input_value["frameRateDenominator"] == 1
                   and input_value["outputDurationUs"] == "700000",
                   name + ": accepted package geometry/timing is preserved")
        self.check(terminal.get("validationResult") is not None,
                   name + ": successful package validation has an immutable report")
        result = terminal["validationResult"]
        self.check(result["passed"] is True
                   and result["opaqueFrames"] == frames
                   and result["transparentFrames"] == frames,
                   name + ": successful package report has the planned opaque/transparent frame counts")
        progress = terminal.get("progress") or {}
        self.check(progress.get("stage") == "completed"
                   and progress.get("completedFrames") == frames * 2
                   and progress.get("totalFrames") == frames * 2,
                   name + ": successful package progress covers both output plans")
        reference = result["report"]
        headers, raw = self.binary(name + "-report", "/api/v1/jobs/" + terminal["jobId"] + "/validation-result")
        self.check(headers["Content-Type"].split(";")[0] == "application/json",
                   name + ": package report media type is JSON")
        self.check(len(raw) == reference["sizeBytes"] and hashlib.sha256(raw).hexdigest() == reference["sha256"],
                   name + ": package report bytes match immutable reference")
        report = json.loads(raw)
        self.check(report["succeeded"] is True
                   and report["opaqueFrames"] == frames
                   and report["transparentFrames"] == frames
                   and report["succeeded"] == result["passed"],
                   name + ": package result and report agree")
        page = self.request(name + "-diagnostics", "GET",
                            "/api/v1/jobs/" + terminal["jobId"] + "/validation-diagnostics?limit=128")
        self.check(page["jobId"] == terminal["jobId"] and page["reportSha256"] == reference["sha256"],
                   name + ": package diagnostics page binds immutable report")
        # The page DTO omits optional null union members while the immutable
        # report retains them; compare their semantic JSON after normalizing
        # those transport-only omissions.
        normalize = lambda value: ({key: normalize(item) for key, item in value.items() if item is not None}
                                   if isinstance(value, dict) else
                                   [normalize(item) for item in value] if isinstance(value, list) else value)
        self.check(normalize(page["diagnostics"]) == normalize(report["diagnostics"])
                   and page.get("nextCursor") is None,
                   name + ": package diagnostics are complete and untruncated")
        return terminal

    def package_validation_outcome(self, name, fixture, *, expected_state="succeeded", frames=1):
        source = self.open_fixture(fixture)
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs"
        accepted = self.mutate(name, "POST", path,
                               {"expectedRevision": source["projectRevision"], "framesPerOutput": frames}, status=202)
        terminal = self.wait_job(name + "-terminal", accepted, expected=expected_state)
        self.check(terminal["input"]["sourceProfile"] == PACKAGE_PROFILE,
                   name + ": diagnostic fixture remains package profile")
        return terminal

    def assert_report_diagnostic(self, name, terminal, *, code, origin=None, passed=False,
                                 expected_frames=None, warning_union=None):
        self.check(terminal.get("validationResult") is not None,
                   name + ": validation report is persisted")
        reference = terminal["validationResult"]["report"]
        _, raw = self.binary(name + "-report", "/api/v1/jobs/" + terminal["jobId"] + "/validation-result")
        self.check(hashlib.sha256(raw).hexdigest() == reference["sha256"], name + ": report SHA is immutable")
        report = json.loads(raw)
        self.check(report["succeeded"] is passed, name + ": report passed flag matches expected outcome")
        values = report["issues"] + report["diagnostics"]
        encoded = json.dumps(values, ensure_ascii=False)
        self.check(code in encoded, name + ": report preserves expected diagnostic code")
        if origin is not None:
            self.check(origin in encoded, name + ": report preserves diagnostic union origin")
        if expected_frames is not None:
            result = terminal["validationResult"]
            self.check(result["opaqueFrames"] == expected_frames
                       and result["transparentFrames"] == expected_frames,
                       name + ": warning coexists with the fixed opaque/transparent frame plan")
        if warning_union is not None:
            issues = report.get("issues") or []
            self.check(bool(issues), name + ": warning report retains at least one issue")
            union_fields = ("source", "imageEffect", "material")
            for index, issue in enumerate(issues):
                label = name + ": issue " + str(index)
                self.check(issue.get("code") == code and issue.get("isError") is False,
                           label + " is a non-error warning with the expected code")
                diagnostic = issue.get("diagnostic")
                self.check(isinstance(diagnostic, dict)
                           and diagnostic.get("code") == code
                           and diagnostic.get("severity") == "warning"
                           and diagnostic.get("originKind") == origin,
                           label + " preserves the warning diagnostic envelope")
                self.check(sum(diagnostic.get(field) is not None for field in union_fields) == 1
                           and diagnostic.get(warning_union) is not None
                           and all(diagnostic.get(field) is None for field in union_fields
                                   if field != warning_union),
                           label + " preserves an exclusive diagnostic union")
                payload = diagnostic[warning_union]
                self.check(isinstance(payload, dict)
                           and payload.get("inputId") == "background"
                           and payload.get("code") == code,
                           label + " preserves the background input closure")
            self.check(not any(item.get("isError") is True for item in issues),
                       name + ": warning report contains no error issue")
        return report

    def package_lifecycle_controls(self, source):
        """Exercise the existing validation job controls with a real package Worker.

        The Worker is stopped after the job has entered ``running``. This
        proves owned job/Worker control, not that a runaway script executed.
        The pause keeps the cases deterministic while still exercising the production timeout,
        cancellation and project-close paths; no package-specific control API is
        introduced here.
        """
        # Explicit cancellation must drain the actual package Worker before the
        # terminal job is exposed, and must not publish a partial report.
        accepted = self.start_validation("package-active-cancel", source["projectSessionId"],
                                         source["projectRevision"], 22)
        identity = self.controlled_worker("package-active-cancel", accepted)
        pid = identity["pid"]
        self.cancel("package-active-cancel-request", accepted)
        cancelled = self.wait_job("package-active-cancel", accepted, expected="cancelled")
        self.check(cancelled.get("validationResult") is None and cancelled.get("failureReportRef") is None,
                   "package active cancellation: no partial validation report")
        self.check(pid not in self.live_workers(),
                   "package active cancellation: actual Worker drained before terminal")

        self.assert_no_report("package-cancel", cancelled)

        # A stopped Worker crosses the configured validation deadline.  The
        # timeout is a failure distinct from explicit cancellation.
        accepted = self.start_validation("package-deadline", source["projectSessionId"],
                                         source["projectRevision"], 22)
        identity = self.controlled_worker("package-deadline", accepted)
        pid = identity["pid"]
        timed_out = self.wait_job("package-deadline", accepted, expected="failed")
        self.check(timed_out.get("failureCode") == "WorkerTaskTimedOut"
                   and not timed_out.get("cancellationRequested"),
                   "package deadline: exact WorkerTaskTimedOut differs from explicit cancellation")
        self.check(timed_out.get("validationResult") is None and pid not in self.live_workers(),
                   "package deadline: no report and actual Worker drained")

        self.assert_no_report("package-deadline", timed_out)

        # Closing a project rejects active jobs by default, then cancelAndWait
        # drains the stopped package Worker through the normal close workflow.
        accepted = self.start_validation("package-close-active", source["projectSessionId"],
                                         source["projectRevision"], 22)
        identity = self.controlled_worker("package-close-active", accepted)
        pid = identity["pid"]
        close_path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/close"
        self.mutate("package-close-reject-active", "POST", close_path,
                    {"expectedRevision": source["projectRevision"], "policy": "discard",
                     "activeJobs": "reject"}, status=409, code="activeJobs")
        closed = self.mutate("package-close-cancel-wait", "POST", close_path,
                             {"expectedRevision": source["projectRevision"], "policy": "discard",
                              "activeJobs": "cancelAndWait"}, status=202)
        self.control_workers[closed["jobId"]] = identity
        self.wait_job("package-close-job", closed)
        closed_job = self.wait_job("package-close-validation", accepted, expected="cancelled")
        self.check(closed_job.get("validationResult") is None and pid not in self.live_workers(),
                   "package close: cancelled validation drained actual Worker without report")
        self.assert_no_report("package-close", closed_job)

    def crash_server(self, name, controlled_identity):
        """Crash this Server process, then observe the controlled Worker drain."""
        self.capture_workers()
        old_pid = self.process.pid
        os.kill(old_pid, signal.SIGKILL)
        code = self.process.wait(timeout=10)
        self.server_runs.append({"name": name, "pid": old_pid, "exitCode": code,
                                 "crash": True, "forcedKill": False})
        self.check(code == -signal.SIGKILL, name + ": Server terminated by SIGKILL")
        # A crash skips the normal owner shutdown. Resume the deliberately
        # stopped Worker so its pipe observes EOF and exits; this is cleanup of
        # the observed identity, not a claim that the crashed host drained it.
        now = self.process_identity(controlled_identity["pid"])
        if now and now["startTicks"] == controlled_identity["startTicks"]:
            os.kill(controlled_identity["pid"], signal.SIGCONT)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            self.capture_workers()
            if not self.live_observed([controlled_identity]):
                break
            time.sleep(.05)
        survivors = self.live_observed([controlled_identity])
        self.check(not survivors, name + ": crashed host Worker identity drained after restart boundary")
        self.crash_drain_checks.append({"serverPid": old_pid, "serverExitCode": code,
                                        "worker": controlled_identity, "survivorsAfterResume": survivors})

    def exercise_crash_recovery(self, successful, successful_raw, source):
        """Exercise abrupt host loss: prior report survives, active job becomes unknown."""
        old_host, old_owner = self.host, self.client
        running = self.start_validation("package-crash-active", source["projectSessionId"],
                                        source["projectRevision"], 22)
        identity = self.controlled_worker("package-crash-active", running)
        self.crash_server("package-crash-server", identity)
        self.start_server(self.environment, "package-crash-server-restarted")
        health = self.request("package-crash-health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.check(self.host != old_host, "package crash: new authoritative host identity")
        old_recovery_path = "/api/v1/recovery-jobs/" + successful["jobId"] + "/validation-result"
        self.request("package-crash-old-host-credential-rejected", "GET", old_recovery_path,
                     client=old_owner, host=old_host, status=401, code="unauthorizedClient")
        self.register("package-crash-recovery-client")
        recovered = self.request("package-crash-recovered-jobs", "GET", "/api/v1/recovery-jobs")["jobs"]
        prior = next(value for value in recovered if value["job"]["jobId"] == successful["jobId"])
        self.check(prior["previousHostInstanceId"] == old_host and prior["job"]["state"] == "succeeded",
                   "package crash: completed report retains previous host identity")
        self.check(prior["job"]["input"]["sourceProfile"] == PACKAGE_PROFILE
                   and prior["job"]["validationResult"] == successful["validationResult"],
                   "package crash: successful package report reference survives host crash")
        unknown_entry = next(value for value in recovered if value["job"]["jobId"] == running["jobId"])
        unknown = unknown_entry["job"]
        self.check(unknown["state"] == "unknown" and unknown_entry.get("previousHostInstanceId") == old_host
                   and not unknown["canCancel"]
                   and unknown.get("failureCode") is None
                   and unknown.get("error", {}).get("code") == "internalError"
                   and unknown.get("validationResult") is None,
                   "package crash: active task becomes unknown with internalError and no report")
        self.request("package-crash-live-job-hidden", "GET", "/api/v1/jobs/" + successful["jobId"],
                     status=404, code="jobNotFound")
        self.binary("package-crash-recovery-report", old_recovery_path, expected=successful_raw)
        outsider = self.register("package-crash-second-recovery-client", select=False)
        self.binary("package-crash-recovery-report-second-client", old_recovery_path,
                    client=outsider["clientId"], expected=successful_raw)
        self.check(not self.live_workers(),
                   "package crash: no Worker automatically replays recovered tasks")

    def exercise_shutdown_recovery(self, successful, successful_raw):
        """Exercise previous-host read-only recovery for a package report.

        The active package task is deliberately stopped during an orderly
        Server shutdown.  The restarted Server must retain the completed
        report as a recovery-only fact, persist the active task as cancelled,
        and must not replay either task automatically.
        """
        old_host, old_owner = self.host, self.client
        source = self.open_fixture("scale", request_prefix="package-shutdown-scale")
        running = self.start_validation("package-shutdown-active", source["projectSessionId"],
                                        source["projectRevision"], 22)
        self.controlled_worker("package-shutdown-active", running)
        self.stop_server("package-shutdown-with-active-validation")

        self.start_server(self.environment, "package-server-restarted")
        health = self.request("package-restart-health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.check(self.host != old_host, "package restart: new authoritative host identity")
        self.register("package-restart-recovery-client")
        self.check(self.client != old_owner, "package restart: new bootstrap client identity")

        recovered = self.request("package-restart-recovered-jobs", "GET", "/api/v1/recovery-jobs")["jobs"]
        prior = next(value for value in recovered if value["job"]["jobId"] == successful["jobId"])
        self.check(prior["previousHostInstanceId"] == old_host and prior["job"]["state"] == "succeeded",
                   "package restart: completed report remains an old-host terminal fact")
        self.check(prior["job"]["input"]["sourceProfile"] == PACKAGE_PROFILE
                   and prior["job"]["validationResult"] == successful["validationResult"],
                   "package restart: package source profile and immutable report reference are retained")

        cancelled = next(value["job"] for value in recovered if value["job"]["jobId"] == running["jobId"])
        self.check(cancelled["state"] == "cancelled" and cancelled.get("validationResult") is None,
                   "package shutdown: active package task is persisted as cancelled without a report")

        recovery_path = "/api/v1/recovery-jobs/" + successful["jobId"] + "/validation-result"
        self.binary("package-restart-authenticated-report", recovery_path, expected=successful_raw)
        outsider = self.register("package-restart-outsider", select=False)
        self.binary("package-restart-second-authorized-report", recovery_path,
                    client=outsider["clientId"], expected=successful_raw)
        self.request("package-restart-live-job-hidden", "GET", "/api/v1/jobs/" + successful["jobId"],
                     status=404, code="jobNotFound")
        self.request("package-restart-unauthenticated-report-rejected", "GET", recovery_path,
                     headers={"Authorization": "Bearer " + "0" * 64}, status=401,
                     code="unauthorizedClient")
        self.check(not self.live_workers(),
                   "package restart: no recovered package task is automatically replayed")

    def exercise(self):
        health = self.request("health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.check(health["status"] == "live", "health: server live")
        self.oracle_generate()
        self.register("package-validation-owner")
        capability = self.request("validation-capabilities", "GET", "/api/v1/validation-capabilities")
        advertised = PACKAGE_PROFILE in (capability.get("sourceProfiles") or [])
        self.check(advertised == self.expect_package,
                   "validation capability package profile matches Worker expectation")

        # The package fixture contains the normal and transparent render paths;
        # validation is one task with both outputs, so this is the actual public
        # admission boundary rather than a synthetic payload.
        source = self.open_fixture("scale")
        self.check(source.get("isReadOnly") is False, "package fixture is writable")
        completed = None
        if self.expect_package:
            completed = self.package_validation("package-validation-scale", source, frames=4)
            # The fixture oracle deliberately includes independent package
            # diagnostic closures. Exercise each wire union without asserting
            # pixels or inventing author mutations through the public API.
            runtime = self.package_validation_outcome("package-validation-runtime-error", "runtime-error",
                                                      expected_state="failed", frames=1)
            self.assert_report_diagnostic("package-runtime-error-report", runtime,
                                          code="ActualPackageRuntimeError", origin="runtime", passed=False)
            compile_failure = self.package_validation_outcome("package-validation-compile", "diagnostic-compile",
                                                              expected_state="failed")
            self.check(bool(compile_failure.get("failureCode")),
                       "package compile failure has stable terminal failure code")
            image = self.package_validation_outcome("package-validation-image-warning", "diagnostic-image",
                                                    expected_state="succeeded", frames=4)
            self.assert_report_diagnostic("package-image-warning-report", image,
                                          code="TransparentBackgroundDependency", origin="imageEffect", passed=True,
                                          expected_frames=4, warning_union="imageEffect")
            material = self.package_validation_outcome("package-validation-material-warning", "diagnostic-material",
                                                      expected_state="succeeded", frames=4)
            self.assert_report_diagnostic("package-material-warning-report", material,
                                          code="TransparentBackgroundDependency", origin="material", passed=True,
                                          expected_frames=4, warning_union="material")
            budget = self.package_validation_outcome("package-validation-budget", "over-budget",
                                                     expected_state="failed")
            self.check(budget.get("failureCode") == "WorkerValidationReportBudgetExceeded",
                       "package diagnostic budget has explicit stable failure code")
        else:
            self.mutate("package-validation-rejected-old-worker", "POST",
                        "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs",
                        {"expectedRevision": source["projectRevision"], "framesPerOutput": 1},
                        status=422, code="commandNotSupported")

        # Exercise deterministic admission boundaries separately from the
        # successful report above. Rejected requests must not be treated as
        # package results. These checks also cover the current revision
        # and cross-client ownership rules shared by static validation.
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs"
        self.mutate("package-invalid-frame-plan", "POST", path,
                    {"expectedRevision": source["projectRevision"], "framesPerOutput": 0},
                    status=400, code="invalidRequest")
        self.mutate("package-stale-revision", "POST", path,
                    {"expectedRevision": "999", "framesPerOutput": 1},
                    status=409, code="revisionConflict")
        outsider = self.register("package-validation-outsider", select=False)
        if completed is not None:
            self.request("package-cross-client-job-rejected", "GET", "/api/v1/jobs/" + completed["jobId"],
                         client=outsider["clientId"], status=404, code="jobNotFound")

        # A static fixture is a regression sentinel: package support must not
        # remove the previously advertised static profile.
        self.check("static-lyrics-scene-v1" in (capability.get("sourceProfiles") or []),
                   "static validation profile remains advertised")
        if self.expect_package:
            self.package_lifecycle_controls(source)
            # Re-open the fixture after close/cancel lifecycle checks, then
            # verify the previous-host read-only recovery path separately.
            _, successful_raw = self.binary("package-recovery-source-report",
                                            "/api/v1/jobs/" + completed["jobId"] + "/validation-result")
            self.exercise_shutdown_recovery(completed, successful_raw)
            # The orderly restart above created a new host generation. Capture
            # a fresh successful report on that host before testing G1 crash -> G2.
            crash_source = self.open_fixture("scale", request_prefix="package-crash-success")
            crash_success = self.package_validation("package-crash-success", crash_source)
            _, crash_raw = self.binary("package-crash-success-report",
                                      "/api/v1/jobs/" + crash_success["jobId"] + "/validation-result")
            self.exercise_crash_recovery(crash_success, crash_raw, crash_source)

    def run(self):
        environment = os.environ.copy()
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "MAKO_BACKEND_FAKE_MEDIA", "MAKO_BACKEND_CREDENTIAL_FILE"):
            environment.pop(key, None)
        self.bootstrap = secrets.token_hex(32)
        self.check(self.worker.is_file() and os.access(self.worker, os.X_OK),
                   "deployment: configured Worker apphost exists and is executable")
        environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap,
                           MAKO_BACKEND_RESOURCE_ROOT=str(self.output / "resources"),
                           MAKO_BACKEND_STATE_ROOT=str(self.output / "state"),
                           MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({"validation-probe-root": str(self.output / "projects")}),
                           MAKO_BACKEND_CLIENT_TOKEN_SECONDS="3600", MAKO_BACKEND_CLIENT_LEASE_SECONDS="30",
                           MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS="8",
                           # Pass the matched self-contained apphost directly.  A
                           # shell wrapper has no sibling script-refs directory,
                           # so the Server cannot compute the Worker identity and
                           # would silently withhold package capabilities.
                           MAKO_BACKEND_WORKER_PATH=str(self.worker))
        self.environment = environment
        (self.output / "projects").mkdir(parents=True, exist_ok=True)
        source_state = {
            "commit": subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                check=True, capture_output=True, text=True).stdout.strip(),
            "dirty": bool(subprocess.run(["git", "status", "--porcelain"], cwd=ROOT,
                check=True, capture_output=True, text=True).stdout),
        }
        file_hash = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
        provenance = {"source": source_state, "toolSha256": file_hash(__file__),
            "validationHelperSha256": file_hash(ROOT / "tools/backend-validation-http-probe/probe.py"),
            "httpHelperSha256": file_hash(ROOT / "tools/backend-http-probe/probe.py"),
            "serverSha256": file_hash(self.server_command[0]), "workerSha256": file_hash(self.worker),
            "oracleSha256": file_hash(self.oracle)}
        deployment_manifest = self.worker.parent.parent / "deployment.json"
        if deployment_manifest.is_file():
            provenance["deploymentManifestSha256"] = file_hash(deployment_manifest)
            deployment = json.loads(deployment_manifest.read_text())
            provenance["deploymentSourceCommit"] = deployment.get("sourceCommit")
            provenance["deploymentSourceDirty"] = deployment.get("sourceDirty")
        failure = None; code = None
        survivors_before_cleanup = []
        try:
            self.start_server(environment, "package-validation-server")
            server_identity = self.process_identity(self.process.pid)
            provenance["serverIdentity"] = server_identity
            self.exercise()
        except Exception as error:
            failure = str(error)
            (self.output / "failure.txt").write_text(traceback.format_exc())
        finally:
            self.capture_workers()
            if self.process is not None and self.process.poll() is None:
                self.process.terminate()
            if self.process is not None:
                try:
                    code = self.process.wait(timeout=25)
                except subprocess.TimeoutExpired:
                    self.forced_cleanup.append({"kind": "server", "pid": self.process.pid})
                    self.process.kill()
                    code = self.process.wait(timeout=5)
                    failure = failure or "Server required forced cleanup"
            survivors_before_cleanup = self.live_observed()
            if code != 0 or survivors_before_cleanup:
                failure = failure or "Server did not exit0 with all observed descendants drained"
            # Cleanup is recorded separately and can never turn a failed drain
            # assertion into success. Recheck identity immediately before kill.
            for identity in survivors_before_cleanup:
                now = self.process_identity(identity["pid"])
                if now and now["startTicks"] == identity["startTicks"]:
                    self.forced_cleanup.append(identity)
                    try: os.kill(identity["pid"], signal.SIGKILL)
                    except ProcessLookupError: pass
            if self.process is not None:
                self.server_runs.append({"name": "final-shutdown", "pid": self.process.pid,
                                         "exitCode": code, "forcedKill": bool(self.forced_cleanup)})
            self.launcher.unlink(missing_ok=True)
        scan_count = 0
        for path in self.output.rglob("*"):
            if path.is_file():
                raw = path.read_bytes()
                if any(value.encode() in raw for value in [self.bootstrap, *self.credentials]):
                    failure = failure or "Credential artifact scan failed: " + str(path.relative_to(self.output))
                scan_count += 1
        MOD.HELPER.save_json(self.output / "manifest.json", {
            "succeeded": failure is None, "failure": failure, "requestCount": self.request_count,
            "assertionCount": len(self.assertions), "assertions": self.assertions,
            "packageJobs": self.package_jobs, "results": self.results, "serverExitCode": code,
            "provenance": provenance, "observedProcesses": list(self.observed_processes.values()),
            "serverRuns": self.server_runs,
            "terminalDrainChecks": self.terminal_drain_checks, "crashDrainChecks": self.crash_drain_checks,
            "observedWorkerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in self.workers.items()],
            "survivorsBeforeForcedCleanup": survivors_before_cleanup, "forcedCleanup": self.forced_cleanup,
            "survivingObservedProcesses": self.live_observed(), "credentialScanFileCount": scan_count,
            "scope": "Package validation capability, successful immutable report and diagnostics, explicit Worker failures, controlled owned Worker SIGSTOP cancel/deadline/close, one orderly and one abrupt crash/restart/recovery cycle; observed processes only, no claim of runaway script execution or complete B4-P1."
        })
        if failure:
            raise SystemExit(failure)
        print(json.dumps({"succeeded": True, "requests": self.request_count,
                          "assertions": len(self.assertions), "serverExitCode": code}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--server", nargs="+", required=True)
    parser.add_argument("--worker", required=True)
    parser.add_argument("--oracle", type=Path, required=True)
    parser.add_argument("--expect-package", choices=("yes", "no"), default="yes",
                        help="whether the configured Worker must advertise package validation")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    PackageValidationProbe(args.output.resolve(), args.server, args.worker, args.oracle,
                           expect_package=args.expect_package == "yes").run()
