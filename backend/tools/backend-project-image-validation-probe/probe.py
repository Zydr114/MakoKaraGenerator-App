#!/usr/bin/env python3
"""Finite fresh HTTP acceptance for project-image-validation-v1.

The probe submits only public project-session validation requests. Expected
reports are generated independently by FixtureOracle from the saved
ProjectStore closure and the original RuntimeFrameValidator. The HTTP path
never supplies report expectations or pixels to the oracle.
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
import uuid
from urllib.parse import quote
from provenance import digest, inventory, production_inputs, tool_inputs, validate_dist, validate_oracle

ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "tools/backend-package-validation-http-probe/probe.py"
spec = importlib.util.spec_from_file_location("package_validation_probe", PACKAGE)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

PROFILE = "project-image-validation-v1"
PACKAGE_PROFILE = "package-lyrics-scene-v1"
LOCK = "/tmp/mkg-backend-b4-dotnet.lock"


class ImageValidationProbe(mod.PackageValidationProbe):
    def __init__(self, output, server, worker, oracle, unsupported_worker=None):
        super().__init__(output, server, worker, oracle, expect_package=True)
        self.oracle = Path(oracle).resolve()
        self.unsupported_worker = Path(unsupported_worker).resolve() if unsupported_worker else None
        self.dist = self.worker.parent.parent
        self.image_jobs = []
        self.image_fixtures = {}
        self.oracle_reports = {}
        self.open_projects = {}
        self.mutations = []
        self.pagination = []
        self.runaway_results = []
        self.frozen_inputs = None
        self.fixture_inputs = None
        self.server_identities = []
        self.unsupported_result = None

    def start_server(self, environment, name):
        super().start_server(environment, name)
        identity = self.process_identity(self.process.pid)
        self.check(identity is not None, name + ": observed exact Server identity")
        self.server_identities.append({"name": name, **identity})

    def stop_server(self, name):
        super().stop_server(name)
        self.capture_workers()
        self.check(not self.live_observed(), name + ": all observed descendants drained")

    def open_fixture(self, name, *, request_prefix=None):
        source = super().open_fixture(name, request_prefix=request_prefix)
        self.open_projects[source["projectSessionId"]] = source
        return source

    def verify_oracle_inputs(self):
        deployment_path = self.worker.parent.parent / "deployment.json"
        deployment = validate_dist(self.dist)
        oracle_manifest = validate_oracle(self.oracle, self.dist)
        self.check(Path(self.server_command[0]).resolve() == self.dist / "server/MakoKaraGenerator.Backend.Server",
                   "deployment: Server and Worker apphosts are from the same immutable dist")
        self.check(not deployment.get("sourceDirty"), "deployment: source-clean matched deployment")
        self.check(deployment.get("selfContained") and deployment.get("rid") == "linux-x64",
                   "deployment: self-contained linux-x64 deployment")
        required = {
            "MakoKaraGenerator.Backend.Contracts.dll", "MakoKaraGenerator.Backend.Rendering.dll",
            "MakoKaraGenerator.Core.dll", "MakoKaraGenerator.Media.dll", "MakoKaraGenerator.Processes.dll",
            "MakoKaraGenerator.Rendering.Abstractions.dll", "MakoKaraGenerator.Rendering.Skia.dll",
            "MakoKaraGenerator.Runtime.Hosting.dll", "MakoKaraGenerator.Scripting.Abstractions.dll",
            "MakoKaraGenerator.Scripting.dll", "MakoKaraGenerator.Text.dll",
            "MakoKaraGenerator.Scripting.Compiler.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
        }
        oracle_root, worker_root = self.oracle.parent, self.worker.parent
        oracle_names = {p.name for p in oracle_root.glob("*.dll") if p.name != self.oracle.name}
        oracle_refs = {p.name for p in (oracle_root / "script-refs/net10.0").glob("*.dll")}
        worker_refs = {p.name for p in (worker_root / "script-refs/net10.0").glob("*.dll")}
        names = sorted(oracle_names | required)
        names += ["script-refs/net10.0/" + name for name in sorted(oracle_refs | worker_refs)]
        compiler_names = {"MakoKaraGenerator.Scripting.Compiler.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"}
        identity = []
        for name in names:
            component = "server" if name in compiler_names else "worker"
            left, right = oracle_root / name, self.dist / component / name
            if name in compiler_names:
                self.check(not (worker_root / name).exists(), "render Worker excludes compiler: " + name)
            oracle_sha = hashlib.sha256(left.read_bytes()).hexdigest() if left.is_file() else None
            worker_sha = hashlib.sha256(right.read_bytes()).hexdigest() if right.is_file() else None
            deployed_sha = deployment.get("files", {}).get(component + "/" + name)
            identity.append({"path": name, "deploymentComponent": component, "oracleSha256": oracle_sha, "componentSha256": worker_sha,
                             "deploymentSha256": deployed_sha,
                             "matched": oracle_sha is not None and oracle_sha == worker_sha == deployed_sha})
        mod.MOD.HELPER.save_json(self.output / "oracle-input-identity.json", {
            "deploymentSourceCommit": deployment.get("sourceCommit"),
            "deploymentManifestSha256": hashlib.sha256(deployment_path.read_bytes()).hexdigest(),
            "oracleBuildManifestSha256": digest(self.oracle.parent.parent / "input-manifest.json"),
            "oracleBuildInputs": oracle_manifest,
            "oracleSha256": hashlib.sha256(self.oracle.read_bytes()).hexdigest(),
            "requiredAssemblies": sorted(required), "inputs": identity,
            "scope": "Every adjacent Oracle dependency, original Runtime/render/compiler chain and complete script reference set must equal the immutable matched deployment before HTTP: Runtime against Worker, compiler/Roslyn against Server. Worker excludes compiler dependencies."
        })
        self.check(required <= oracle_names, "Oracle: complete Runtime/compiler dependency set")
        self.check(oracle_refs == worker_refs and "System.Runtime.dll" in oracle_refs,
                   "Oracle: complete framework reference set matches Worker")
        for item in identity:
            self.check(item["matched"], "Oracle/component/deployment byte identity: " + item["path"])
        self.frozen_inputs = {"deploymentManifestSha256": digest(deployment_path),
                              "deploymentFiles": inventory(self.dist),
                              "productionInputs": production_inputs(), "toolInputs": tool_inputs(),
                              "oracleFiles": inventory(self.oracle.parent),
                              "oracleBuildManifestSha256": digest(self.oracle.parent.parent / "input-manifest.json")}
        mod.MOD.HELPER.save_json(self.output / "input-manifest-initial.json", self.frozen_inputs)

    def oracle_generate(self):
        projects = self.output / "projects"
        projects.mkdir(parents=True, exist_ok=True)
        result = subprocess.run(["flock", "-o", LOCK, "dotnet", str(self.oracle),
                                 "generate", str(projects)], capture_output=True, text=True)
        (self.output / "fixture-generation.log").write_text(result.stdout + result.stderr)
        self.check(result.returncode == 0, "fixture generation: independent FixtureOracle exit0")
        manifest = json.loads((projects / "fixtures.json").read_text())
        self.image_fixtures = {item["kind"]: item for item in manifest["fixtures"]}
        for kind in ("image-static", "image-analysis", "image-package", "image-corrupt", "image-dimension", "image-decoded"):
            self.check((projects / kind / "project.json").is_file(), kind + ": saved ProjectStore fixture exists")
        for kind, fixture in self.image_fixtures.items():
            fixed = projects / kind / fixture["fixedPath"]
            self.check(not Path(fixture["fixedPath"]).is_absolute() and ".." not in Path(fixture["fixedPath"]).parts,
                       kind + ": fixture media path is relative to its saved project root")
            self.check(fixed.is_file() and fixed.stat().st_size == fixture["sizeBytes"] and digest(fixed) == fixture["sha256"],
                       kind + ": complete saved image identity matches generated fixture")
            if kind in ("image-large", "image-envelope-budget"):
                self.check(fixture.get("expectedInputFailure") == "SnapshotSizeExceeded",
                           kind + ": independent fixture oracle proves complete validation input budget overflow")
        self.fixture_inputs = inventory(projects)
        mod.MOD.HELPER.save_json(self.output / "fixture-inputs-initial.json", self.fixture_inputs)
        mod.MOD.HELPER.save_json(self.output / "fixture-source-manifest.json", {
            "fixtures": self.image_fixtures,
            "scope": "Probe-owned ProjectStore.Save closures. Input mutations are recorded separately and are not public author operations.",
        })

    def controlled_worker(self, name, accepted, *, stop=True):
        state, pid = self.wait_active(name, accepted)
        self.check(state.get("input", {}).get("sourceProfile") == PROFILE,
                   name + ": running control job retains image validation profile")
        self.capture_workers()
        identity = self.process_identity(pid)
        self.check(identity is not None and identity["startTicks"] == self.workers[pid],
                   name + ": exact owned Worker identity captured before control")
        self.control_workers[accepted["jobId"]] = identity
        if stop:
            self.worker_signal(pid, signal.SIGSTOP)
        return identity

    def oracle_validate(self, kind, frames=4, source=None):
        saved = self.output / "state/sessions" / source["projectSessionId"] / "loaded-input" if source else self.output / "projects" / kind
        prefix = self.output / "oracle" / (kind + "-" + str(frames))
        prefix.parent.mkdir(parents=True, exist_ok=True)
        base = self.image_fixtures[kind].get("baseProfile", "static-lyrics-scene-v1")
        result = subprocess.run(["flock", "-o", LOCK, "dotnet", str(self.oracle), "validate",
                                 str(saved), str(prefix), str(frames), base], capture_output=True, text=True, timeout=35)
        (prefix.with_suffix(".stdout.log")).write_text(result.stdout + result.stderr)
        self.check(result.returncode == 0, kind + ": independent RuntimeFrameValidator exit0")
        report = json.loads((prefix.parent / (prefix.name + "-report.json")).read_bytes())
        source = json.loads((prefix.parent / (prefix.name + "-source.json")).read_text())
        self.check(source["baseProfile"] == source["actualBaseProfile"] == base,
                   kind + ": independently loaded complete base profile matches fixture declaration")
        self.check(source["imageSha256"] == self.image_fixtures[kind]["sha256"],
                   kind + ": independent Oracle reads exact saved image bytes")
        self.oracle_reports[(kind, frames)] = {"report": report, "source": source,
                                                "prefix": str(prefix), "baseProfile": base}
        return self.oracle_reports[(kind, frames)]

    def read_report(self, name, job):
        reference = job["validationResult"]["report"]
        headers, raw = self.binary(name, "/api/v1/jobs/" + job["jobId"] + "/validation-result")
        self.check(headers["Content-Type"].split(";")[0] == "application/json", name + ": JSON report media type")
        self.check(len(raw) == reference["sizeBytes"] and hashlib.sha256(raw).hexdigest() == reference["sha256"],
                   name + ": report bytes, size and SHA match immutable reference")
        report = json.loads(raw)
        values, cursor, seen_cursors, page_sizes = [], None, set(), []
        while True:
            path = "/api/v1/jobs/" + job["jobId"] + "/validation-diagnostics?limit=128"
            if cursor:
                path += "&cursor=" + quote(cursor, safe="")
            page = self.request(name + "-diagnostics-" + str(len(values)), "GET", path)
            self.check(page["jobId"] == job["jobId"] and page["reportSha256"] == reference["sha256"],
                       name + ": diagnostics page binds immutable report")
            values.extend(page["diagnostics"])
            page_sizes.append(len(page["diagnostics"]))
            self.check(len(page["diagnostics"]) <= 128, name + ": diagnostics page stays in its requested budget")
            cursor = page.get("nextCursor")
            if not cursor:
                break
            self.check(cursor not in seen_cursors, name + ": diagnostics cursor advances without a cycle")
            seen_cursors.add(cursor)
        self.check(values == self.page_projection(report.get("diagnostics", [])),
                   name + ": diagnostics pages are complete and untruncated")
        self.pagination.append({"jobId": job["jobId"], "pageSizes": page_sizes, "count": len(values),
                                "reportSha256": reference["sha256"]})
        self.check(report["succeeded"] == job["validationResult"]["passed"]
                   and all(report[key] == job["validationResult"][key] for key in ("opaqueFrames", "transparentFrames")),
                   name + ": persisted result and complete report agree")
        return report, raw

    @staticmethod
    def normalize(value):
        # Map random Runtime epochs to stable first-seen session slots. Keep
        # repeated epochs, ordering, output grouping and sequence relationships.
        epochs = {}
        def walk(item):
            if isinstance(item, dict):
                result = {}
                for key, child in item.items():
                    if key == "epoch":
                        uuid.UUID(str(child))
                        result[key] = epochs.setdefault(child, "session-" + str(len(epochs)))
                    else:
                        result[key] = walk(child)
                return result
            if isinstance(item, list):
                return [walk(child) for child in item]
            return item
        return walk(value)

    @staticmethod
    def page_projection(values):
        # Only HTTP DTO nullable members may disappear. Never normalize the
        # page itself: an unexpected null, missing required value or foreign
        # union payload must remain a real mismatch.
        nullable = {"diagnostic": {"location", "occurrence", "originKind", "source", "imageEffect", "material"},
                    "occurrence": {"location", "package"}, "source": {"instanceId", "exportId", "path"},
                    "owner": {"script", "source"}, "script": {"packageIdentity"}}
        def project(item, context=None):
            if isinstance(item, dict):
                return {key: project(child, key) for key, child in item.items()
                        if not (child is None and key in nullable.get(context, set()))}
            if isinstance(item, list):
                return [project(child, context) for child in item]
            return item
        return project(values)

    def assert_occurrences(self, name, report):
        epochs, current_epoch = {}, None
        for index, item in enumerate(report.get("diagnostics", [])):
            diagnostic, transparent = item["diagnostic"], item["transparent"]
            self.check(diagnostic["originKind"] == "runtime" and diagnostic.get("occurrence") is not None
                       and all(diagnostic.get(key) is None for key in ("source", "imageEffect", "material")),
                       f"{name}: diagnostic {index} has exclusive runtime union")
            occurrence = diagnostic["occurrence"]
            epoch = uuid.UUID(occurrence["epoch"])
            self.check(epoch.int != 0, f"{name}: occurrence {index} has a nonempty Runtime epoch")
            if epoch not in epochs:
                epochs[epoch] = {"transparent": transparent, "sequence": 0}
            group = epochs[epoch]
            self.check(group["transparent"] is transparent,
                       f"{name}: epoch {epoch} belongs to exactly one output session")
            self.check(current_epoch is None or current_epoch == epoch or group["sequence"] == 0,
                       f"{name}: epoch {epoch} occurs in one contiguous report group")
            sequence = occurrence["sequence"]
            self.check(isinstance(sequence, str) and sequence == str(group["sequence"] + 1),
                       f"{name}: occurrence {index} sequence is canonical and contiguous from one")
            group["sequence"] += 1; current_epoch = epoch

    def close_project(self, name, source):
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/close"
        accepted = self.mutate(name + "-project-close", "POST", path,
                              {"expectedRevision": source["projectRevision"], "policy": "discard", "activeJobs": "wait"}, status=202)
        self.wait_job(name + "-project-close", accepted)
        fixed = self.output / "state/sessions" / source["projectSessionId"] / "loaded-input"
        self.check(not fixed.exists(), name + ": loaded-input closure drains after project close")
        self.open_projects.pop(source["projectSessionId"], None)

    def compare_report(self, name, terminal, oracle):
        self.check(terminal["input"]["sourceProfile"] == PROFILE,
                   name + ": immutable task input advertises project image profile")
        self.check(terminal["input"]["framesPerOutput"] == oracle["source"]["framesPerOutput"]
                   and terminal["input"]["canvasWidth"] == oracle["source"]["canvasWidth"]
                   and terminal["input"]["canvasHeight"] == oracle["source"]["canvasHeight"],
                   name + ": accepted geometry and frame plan match saved closure oracle")
        self.check(terminal["input"]["sourceSha256"] == oracle["source"]["snapshotSha256"],
                   name + ": accepted source SHA matches independent encoded image envelope")
        report, raw = self.read_report(name + "-report", terminal)
        self.assert_occurrences(name + "-HTTP", report)
        self.assert_occurrences(name + "-Oracle", oracle["report"])
        self.check(all(terminal["input"][key] == oracle["source"][key]
                       for key in ("frameRateNumerator", "frameRateDenominator", "outputDurationUs")),
                   name + ": complete source frame timing and duration match Oracle")
        self.check(self.normalize(report) == self.normalize(oracle["report"]),
                   name + ": complete HTTP report equals independent Runtime report (epoch normalized)")
        return report, raw

    def assert_warning_union(self, name, terminal, code, origin, union):
        report, _ = self.read_report(name + "-warning-report", terminal)
        issues = report.get("issues") or []
        self.check(issues, name + ": warning report contains issue")
        for index, issue in enumerate(issues):
            self.check(issue.get("code") == code and issue.get("isError") is False,
                       f"{name}: issue {index} is the expected non-error warning")
            diagnostic = issue.get("diagnostic") or {}
            self.check(diagnostic.get("originKind") == origin and diagnostic.get("severity") == "warning",
                       f"{name}: issue {index} has the expected diagnostic origin")
            self.check(sum(diagnostic.get(key) is not None for key in ("source", "imageEffect", "material")) == 1
                       and diagnostic.get(union) is not None,
                       f"{name}: issue {index} has an exclusive diagnostic union")
            self.check(diagnostic[union].get("inputId") == "background",
                       f"{name}: issue {index} binds the background input")

    def image_validation(self, name, fixture, frames=4, *, expect="succeeded", oracle=True, issue_code=None):
        source = self.open_fixture(fixture, request_prefix=name + "-open")
        expected = self.oracle_validate(fixture, frames, source) if oracle else None
        cursor = self.request(name + "-cursor", "GET", "/api/v1/clients/" + self.client + "/state")["eventCursor"]
        metadata = self.metadata(name + "-once")
        accepted = self.start_validation(name, source["projectSessionId"], source["projectRevision"], frames, metadata=metadata)
        replay = self.start_validation(name + "-receipt-replay", source["projectSessionId"], source["projectRevision"], frames, metadata=metadata)
        self.check(replay == accepted, name + ": exact accepted receipt replay")
        self.image_jobs.append(accepted)
        terminal = self.wait_job(name + "-terminal", accepted, expected=expect)
        self.check(terminal["input"]["sourceProfile"] == PROFILE, name + ": immutable image validation profile")
        self.check(terminal.get("validationResult") is not None, name + ": complete immutable report is required")
        if expected is not None:
            report, raw = self.compare_report(name, terminal, expected)
        else:
            report, raw = self.read_report(name + "-report", terminal)
        self.check(report["succeeded"] == (expect == "succeeded"), name + ": exact report outcome")
        if expect == "succeeded":
            self.check(report["opaqueFrames"] == frames and report["transparentFrames"] == frames,
                       name + ": complete planned ordinary and transparent frames")
        else:
            self.check(terminal.get("failureCode") == "ValidationFailed", name + ": exact failed-report terminal code")
        if issue_code:
            self.check(any(item["code"] == issue_code and item["isError"] for item in report["issues"]),
                       name + ": complete report contains exact error issue " + issue_code)
        events = self.terminal_events(name, cursor, terminal)
        if expected is not None and expect == "succeeded":
            self.compare_progress(name, terminal, expected, events)
        self.report_boundaries(terminal, raw)
        self.comparisons.append({"name": name, "fixture": fixture, "framesPerOutput": frames,
                                 "baseProfile": self.image_fixtures[fixture]["baseProfile"],
                                 "reportSha256": terminal["validationResult"]["report"]["sha256"],
                                 "oracleMatched": expected is not None, "epochNormalization": "first-seen session mapping"})
        self.close_project(name, source)
        return source, terminal, report

    def compare_progress(self, name, terminal, oracle, events):
        last = oracle["source"]["progress"][-1]
        rendering = [event["job"]["progress"] for event in events
                     if (event.get("job") or {}).get("jobId") == terminal["jobId"]
                     and (event["job"].get("progress") or {}).get("stage") == "rendering"]
        self.check(rendering and all(rendering[-1][key] == value for key, value in last.items()),
                   name + ": final rendering SSE progress matches independent Runtime frame plan")
        self.check(all(terminal["progress"][key] == last[key] for key in ("completedFrames", "totalFrames")),
                   name + ": terminal progress retains complete Runtime frame counts")

    def exercise_fixed_input(self):
        kind = "image-capture-stable"
        source = self.open_fixture(kind)
        expected = self.oracle_validate(kind, 4, source)
        metadata = self.metadata(kind + "-once")
        accepted = self.start_validation(kind, source["projectSessionId"], source["projectRevision"], 4, metadata=metadata)
        identity = self.controlled_worker(kind, accepted)
        fixed = self.output / "state/sessions" / source["projectSessionId"] / "loaded-input" / self.image_fixtures[kind]["fixedPath"]
        original = fixed.read_bytes()
        self.check(digest(fixed) == self.image_fixtures[kind]["sha256"],
                   kind + ": exact accepted image identity before owned mutation")
        changed = bytes([original[0] ^ 1]) + original[1:]
        try:
            fixed.write_bytes(changed)
            self.mutations.append({"path": str(fixed.relative_to(self.output)), "jobId": accepted["jobId"],
                                   "beforeSha256": hashlib.sha256(original).hexdigest(),
                                   "afterSha256": digest(fixed), "scope": "Internal mutation after HTTP202 fixed-byte capture"})
            replay = self.start_validation(kind + "-receipt-after-mutation", source["projectSessionId"], source["projectRevision"], 4, metadata=metadata)
            self.check(replay == accepted, kind + ": receipt replay preserves exact accepted immutable input after mutation")
            self.worker_signal(identity["pid"], signal.SIGCONT)
            terminal = self.wait_job(kind, accepted)
            self.check(terminal["input"]["sourceRevision"] == source["projectRevision"],
                       kind + ": task retains exact accepted revision")
            self.compare_report(kind, terminal, expected)
            self.image_jobs.append(terminal)
        finally:
            fixed.write_bytes(original)
            self.check(digest(fixed) == self.image_fixtures[kind]["sha256"],
                       kind + ": owned loaded image restored byte-exactly")
        self.close_project(kind, source)

    def exercise_runaway(self, stage, action):
        kind = "runaway-" + stage + "-" + action
        source = self.open_fixture(kind)
        accepted = self.start_validation(kind, source["projectSessionId"], source["projectRevision"], 22)
        marker = self.output / "projects" / "markers" / kind
        deadline = time.monotonic() + 15
        while not marker.exists() and time.monotonic() < deadline:
            self.capture_workers(); time.sleep(.05)
        self.check(marker.exists(), kind + ": actual author entered marker")
        values = marker.read_text().split("|")
        self.check(len(values) == 4 and values[2] == stage and values[3] == kind,
                   kind + ": marker records exact stage and nonce")
        worker_identity = self.controlled_worker(kind, accepted, stop=False)
        self.check(int(values[0]) == worker_identity["pid"]
                   and (marker.with_suffix(".ready")).read_text() == str(worker_identity["pid"]),
                   kind + ": author marker and Initialize-ready marker bind exact controlled Worker")
        child_pid = int(values[1])
        child = self.process_identity(child_pid)
        self.check(child is not None, kind + ": observed actual author child identity")
        controlled = self.controlled_identities(worker_identity)
        self.check(any(item["pid"] == child_pid and item["startTicks"] == child["startTicks"] for item in controlled),
                   kind + ": child marker identity belongs to the observed Worker tree")
        if action == "cancel":
            self.cancel(kind + "-cancel", accepted)
            terminal = self.wait_job(kind + "-terminal", accepted, expected="cancelled")
        elif action == "deadline":
            terminal = self.wait_job(kind + "-terminal", accepted, expected="failed")
            self.check(terminal.get("failureCode") == "WorkerTaskTimedOut",
                       kind + ": exact WorkerTaskTimedOut")
        else:
            path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/close"
            self.request(kind + "-close-reject", "POST", path, value={"expectedRevision": source["projectRevision"], "policy": "discard", "activeJobs": "reject"}, status=409, code="activeJobs")
            close = self.request(kind + "-close-cancel-wait", "POST", path, value={"expectedRevision": source["projectRevision"], "policy": "discard", "activeJobs": "cancelAndWait"}, status=202)
            self.wait_job(kind + "-close-job", close)
            terminal = self.wait_job(kind + "-terminal", accepted, expected="cancelled")
        self.check(not self.live_observed(self.controlled_identities(worker_identity)),
                   kind + ": actual Worker and observed child tree drains before terminal")
        self.assert_no_report(kind, terminal)
        self.runaway_results.append({"stage": stage, "action": action, "nonce": kind,
                                     "worker": worker_identity, "child": child,
                                     "observedWorkerTree": self.controlled_identities(worker_identity),
                                     "jobId": terminal["jobId"], "state": terminal["state"],
                                     "failureCode": terminal.get("failureCode"), "liveAtTerminal": []})
        if action == "close":
            self.check(not (self.output / "state/sessions" / source["projectSessionId"] / "loaded-input").exists(),
                       kind + ": cancelAndWait project close drains loaded image closure")
            self.open_projects.pop(source["projectSessionId"], None)
        else:
            self.close_project(kind, source)

    def exercise_controls(self, source):
        active = self.start_validation("active-cancel", source["projectSessionId"], source["projectRevision"], 22)
        self.controlled_worker("active-cancel", active)
        queued_jobs = [self.start_validation("queued-" + str(index), source["projectSessionId"], source["projectRevision"], 22) for index in range(4)]
        self.check(all(item["state"] == "queued" for item in queued_jobs), "queue: four jobs remain behind blocked active Worker")
        time.sleep(0.25); self.capture_workers()
        queued_states = [self.request("queued-still-blocked-" + str(index), "GET", "/api/v1/jobs/" + item["jobId"])
                         for index, item in enumerate(queued_jobs)]
        self.check(all(item["state"] == "queued" and item.get("progress") is None and item.get("validationResult") is None
                       for item in queued_states), "queue: blocked jobs remain queued without execution, progress or report")
        self.check(len(self.live_workers()) == 1 and self.live_workers()[0] == self.control_workers[active["jobId"]]["pid"],
                   "queue: only the blocked active Worker is running; queued jobs did not execute")
        self.mutate("queue-quota-rejected", "POST", "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs",
                    {"expectedRevision": source["projectRevision"], "framesPerOutput": 22}, status=429, code="requestQuotaExceeded")
        for index, queued in enumerate(queued_jobs):
            self.cancel("queued-cancel-" + str(index), queued)
            terminal = self.wait_job("queued-cancel-" + str(index), queued, expected="cancelled")
            self.check(terminal.get("progress") is None and terminal.get("validationResult") is None,
                       "queued cancellation: no frames and no partial report")
            self.assert_no_report("queued-" + str(index), terminal)
        metadata = self.metadata("cancel-active-once")
        cancelled = self.cancel("active-cancel-request", active, metadata)
        replay = self.cancel("active-cancel-repeat", active, metadata)
        self.check(cancelled == replay, "active cancel: exact cancellation receipt replay")
        cancelled = self.wait_job("active-cancel", active, expected="cancelled")
        self.assert_no_report("active-cancel", cancelled)
        timed = self.start_validation("active-deadline", source["projectSessionId"], source["projectRevision"], 22)
        self.controlled_worker("active-deadline", timed)
        timeout = self.wait_job("active-deadline", timed, expected="failed")
        self.check(timeout.get("failureCode") == "WorkerTaskTimedOut" and not timeout.get("cancellationRequested"),
                   "active-deadline: exact timeout failure differs from cancellation")
        self.assert_no_report("active-deadline", timeout)
        close = self.start_validation("active-close", source["projectSessionId"], source["projectRevision"], 22)
        self.controlled_worker("active-close", close)
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/close"
        self.mutate("active-close-reject", "POST", path, {"expectedRevision": source["projectRevision"], "policy": "discard", "activeJobs": "reject"}, status=409, code="activeJobs")
        accepted = self.mutate("active-close-cancel-wait", "POST", path, {"expectedRevision": source["projectRevision"], "policy": "discard", "activeJobs": "cancelAndWait"}, status=202)
        self.wait_job("active-close-project", accepted)
        terminal = self.wait_job("active-close-validation", close, expected="cancelled")
        self.assert_no_report("active-close", terminal)
        self.check(not (self.output / "state/sessions" / source["projectSessionId"] / "loaded-input").exists(),
                   "active-close: loaded image closure drained before close completion")
        self.open_projects.pop(source["projectSessionId"], None)

    def exercise_shutdown_recovery_image(self, successful, raw):
        old_host, old_client = self.host, self.client
        source = self.open_fixture("image-shutdown")
        running = self.start_validation("shutdown-active", source["projectSessionId"], source["projectRevision"], 22)
        self.controlled_worker("shutdown-active", running)
        self.stop_server("shutdown-with-active-image-validation")
        self.start_server(self.environment, "image-server-restarted")
        health = self.request("restart-health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.check(self.host != old_host, "restart: new authoritative host identity")
        path = "/api/v1/recovery-jobs/" + successful["jobId"] + "/validation-result"
        self.request("restart-image-old-host-credential-rejected", "GET", path,
                     client=old_client, host=old_host, status=401, code="unauthorizedClient")
        self.register("restart-image-recovery-client")
        self.check(self.client != old_client, "restart: new bootstrap-authorized client identity")
        recovered = self.request("restart-image-recovered-jobs", "GET", "/api/v1/recovery-jobs")["jobs"]
        prior = next(item for item in recovered if item["job"]["jobId"] == successful["jobId"])
        self.check(prior["previousHostInstanceId"] == old_host
                   and prior["job"]["state"] == "succeeded"
                   and prior["job"]["input"] == successful["input"]
                   and prior["job"]["validationResult"] == successful["validationResult"],
                   "restart: exact old-host image input and successful report retained")
        cancelled = next(item["job"] for item in recovered if item["job"]["jobId"] == running["jobId"])
        self.check(cancelled["state"] == "cancelled" and cancelled.get("validationResult") is None, "restart: active image task persisted cancelled")
        self.binary("restart-image-report", path, expected=raw)
        outsider = self.register("restart-image-second-authorized-client", select=False)
        self.binary("restart-image-report-second-authorized-client", path, client=outsider["clientId"], expected=raw)
        self.request("restart-image-unauthenticated-rejected", "GET", path,
                     headers={"Authorization": "Bearer " + "0" * 64}, status=401, code="unauthorizedClient")
        self.request("restart-image-live-hidden", "GET", "/api/v1/jobs/" + successful["jobId"], status=404, code="jobNotFound")
        self.check(not self.live_workers(), "restart: no image task automatically replays")
        shutdown_loaded = self.output / "state/sessions" / source["projectSessionId"] / "loaded-input"
        self.check(not shutdown_loaded.exists(), "restart: shutdown image session closure drained its loaded-input")
        self.open_projects.pop(source["projectSessionId"], None)

    def exercise(self):
        health = self.request("health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.oracle_generate()
        self.register("image-validation-owner")
        capability = self.request("validation-capabilities", "GET", "/api/v1/validation-capabilities")
        self.check(PROFILE in (capability.get("sourceProfiles") or []), "capabilities: project image validation profile advertised")
        self.check({PACKAGE_PROFILE, "static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1"} <= set(capability.get("sourceProfiles") or []), "capabilities: existing static/analysis/package profiles remain advertised")
        self.check(capability["operationTimeoutSeconds"] == 8, "capability: configured validation deadline is 8 seconds")
        successful = []
        for kind in ("image-static", "image-analysis", "image-package"):
            _, terminal, _ = self.image_validation(kind, kind, frames=4)
            successful.append(terminal)
        for kind, code in (("image-corrupt", "PackageImageDecodeFailed"), ("image-dimension", "PackageImageBudgetExceeded"), ("image-decoded", "PackageImageBudgetExceeded")):
            self.image_validation(kind, kind, frames=1, expect="failed", oracle=False, issue_code=code)
        warning_cases = (("image-warning", "image-diagnostic-image", "imageEffect", "imageEffect"),
                         ("material-warning", "image-diagnostic-material", "material", "material"),
                         ("script-material-warning", "image-diagnostic-script-material", "material", "material"))
        for name, fixture, origin, union in warning_cases:
            _, terminal, _ = self.image_validation(name, fixture, frames=4)
            self.assert_warning_union(name, terminal, "TransparentBackgroundDependency", origin, union)
        for fixture in ("image-diagnostic-compile", "image-diagnostic-compile-source"):
            _, terminal, report = self.image_validation(fixture, fixture, frames=1, expect="failed")
            diagnostics = [item.get("diagnostic") for item in report["issues"] if item.get("diagnostic")]
            self.check(diagnostics and all(item["originKind"] == "compileSource" and item.get("source") is not None and item.get("imageEffect") is None and item.get("material") is None for item in diagnostics), fixture + ": exclusive full compileSource union")
        self.image_validation("runtime-error", "image-runtime-error", frames=1, expect="failed", issue_code="ActualPackageRuntimeError")
        self.image_validation("image-pages", "image-pages", frames=1)
        self.check(len(self.pagination[-1]["pageSizes"]) >= 2 and self.pagination[-1]["count"] >= 65,
                   "image-pages: complete report diagnostics are actually paginated")
        for fixture in ("image-large", "image-envelope-budget"):
            source = self.open_fixture(fixture, request_prefix=fixture + "-open")
            path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs"
            rejected = self.mutate(fixture + "-input-budget", "POST", path,
                                   {"expectedRevision": source["projectRevision"], "framesPerOutput": 1},
                                   status=413, code="resourceTooLarge")
            self.check(rejected.get("failureCode") == "SnapshotSizeExceeded" and rejected.get("field") == "background",
                       fixture + ": exact complete image input budget failure")
            self.close_project(fixture, source)
        budget_source = self.open_fixture("image-over-budget")
        accepted = self.start_validation("report-budget", budget_source["projectSessionId"], budget_source["projectRevision"], 1)
        budget = self.wait_job("report-budget", accepted, expected="failed")
        self.check(budget.get("failureCode") == "WorkerValidationReportBudgetExceeded" and budget.get("validationResult") is None,
                   "report-budget: exact explicit failure with no truncated report")
        self.assert_no_report("report-budget", budget)
        self.close_project("report-budget", budget_source)

        source = self.open_fixture("image-static", request_prefix="controls-source")
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs"
        self.mutate("stale-revision", "POST", path, {"expectedRevision": "999", "framesPerOutput": 1}, status=409, code="revisionConflict")
        self.mutate("invalid-frame-plan", "POST", path, {"expectedRevision": source["projectRevision"], "framesPerOutput": 0}, status=400, code="invalidRequest")
        self.mutate("unknown-validation-member", "POST", path, {"expectedRevision": source["projectRevision"], "framesPerOutput": 1, "timeoutSeconds": 1}, status=400, code="invalidRequest")
        outsider = self.register("image-validation-outsider", select=False)["clientId"]
        for suffix in ("", "/validation-result", "/validation-diagnostics?limit=1"):
            self.request("foreign-image-job-" + str(len(suffix)), "GET", "/api/v1/jobs/" + successful[0]["jobId"] + suffix, client=outsider, status=404, code="jobNotFound")
        self.mutate("foreign-image-start", "POST", path, {"expectedRevision": source["projectRevision"], "framesPerOutput": 1}, client=outsider, status=404, code="sessionNotFound")
        self.exercise_controls(source)
        self.exercise_source_boundaries()
        self.exercise_fixed_input()
        for stage in ("Initialize", "Evaluate"):
            for action in ("cancel", "deadline", "close"):
                self.exercise_runaway(stage, action)
        completed_raw = self.binary("recovery-source-report", "/api/v1/jobs/" + successful[0]["jobId"] + "/validation-result")[1]
        self.exercise_shutdown_recovery_image(successful[0], completed_raw)

    def exercise_source_boundaries(self):
        source = self.open_fixture("image-source-change", request_prefix="tamper-source")
        path = "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs"
        fixture = self.image_fixtures["image-source-change"]
        relative = Path(fixture["fixedPath"])
        self.check(not relative.is_absolute() and ".." not in relative.parts,
                   "tamper: fixed media path is relative to saved project root")
        fixed = self.output / "state/sessions" / source["projectSessionId"] / "loaded-input" / relative
        self.check(fixed.is_file() and fixed.stat().st_size == fixture["sizeBytes"] and hashlib.sha256(fixed.read_bytes()).hexdigest() == fixture["sha256"], "tamper: exact pinned image exists before internal fault injection")
        original = fixed.read_bytes()
        fixed.write_bytes(bytes([original[0] ^ 1]) + original[1:])
        changes = [{"path": str(fixed.relative_to(self.output)), "beforeSha256": fixture["sha256"], "afterSha256": hashlib.sha256(fixed.read_bytes()).hexdigest(), "scope": "Internal fault injection only"}]
        rejected = self.mutate("tampered-image-rejected", "POST", path, {"expectedRevision": source["projectRevision"], "framesPerOutput": 1}, status=422, code="commandNotSupported")
        self.check(rejected.get("failureCode") == "ProjectImageSourceChanged" and rejected.get("field") == "background", "tamper: exact failure code and field")
        fixed.unlink()
        changes.append({"path": str(fixed.relative_to(self.output)), "beforeSha256": fixture["sha256"],
                        "afterSha256": None, "scope": "Internal fault injection only"})
        rejected = self.mutate("missing-image-rejected", "POST", path, {"expectedRevision": source["projectRevision"], "framesPerOutput": 1}, status=422, code="commandNotSupported")
        self.check(rejected.get("failureCode") == "ProjectImageSourceUnavailable", "missing: exact failure code")
        target = self.output / "owned-link-target.media"; target.write_bytes(original); fixed.symlink_to(target)
        changes.append({"path": str(fixed.relative_to(self.output)), "beforeSha256": None,
                        "afterSha256": hashlib.sha256(original).hexdigest(), "kind": "symbolic-link",
                        "scope": "Internal fault injection only"})
        rejected = self.mutate("linked-image-rejected", "POST", path, {"expectedRevision": source["projectRevision"], "framesPerOutput": 1}, status=422, code="commandNotSupported")
        self.check(rejected.get("failureCode") == "ProjectImageSourceUnavailable", "linked: exact failure code")
        fixed.unlink(); fixed.write_bytes(original)
        self.mutations.extend(changes)
        mod.MOD.HELPER.save_json(self.output / "owned-fixture-mutations.json", changes)
        self.close_project("tamper", source)

    def exercise_unsupported_worker(self):
        """Run a separate negative evidence tree against the immutable preview-only Worker."""
        if self.unsupported_worker is None:
            return
        old_dist = self.unsupported_worker.parent.parent
        manifest = json.loads((old_dist / "deployment.json").read_text())
        actual = inventory(old_dist); actual.pop("deployment.json", None)
        expected_commit = subprocess.run(["git", "rev-parse", "d961895"], cwd=ROOT,
                                         check=True, capture_output=True, text=True).stdout.strip()
        self.check(manifest.get("sourceCommit") == expected_commit and manifest.get("sourceDirty") is False,
                   "unsupported deployment: exact preview-only source baseline")
        self.check(actual == manifest.get("files"), "unsupported deployment: complete immutable file inventory")
        old_output = self.output / "unsupported-worker"
        old_output.mkdir(parents=True, exist_ok=False)
        old = ImageValidationProbe(old_output, self.server_command, self.unsupported_worker, self.oracle)
        old.oracle_generate()
        environment = os.environ.copy()
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "MAKO_BACKEND_FAKE_MEDIA", "MAKO_BACKEND_CREDENTIAL_FILE"):
            environment.pop(key, None)
        old.bootstrap = secrets.token_hex(32)
        environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=old.bootstrap,
                           MAKO_BACKEND_RESOURCE_ROOT=str(old_output / "resources"),
                           MAKO_BACKEND_STATE_ROOT=str(old_output / "state"),
                           MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({"validation-probe-root": str(old_output / "projects")}),
                           MAKO_BACKEND_CLIENT_TOKEN_SECONDS="3600", MAKO_BACKEND_CLIENT_LEASE_SECONDS="30",
                           MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS="8", MAKO_BACKEND_WORKER_PATH=str(self.unsupported_worker))
        old.environment = environment
        failure = None
        try:
            old.start_server(environment, "unsupported-worker-server")
            health = old.request("unsupported-health", "GET", "/health/live", client=None, host=None)
            old.host = health["hostInstanceId"]
            old.register("unsupported-owner")
            capability = old.request("unsupported-capability", "GET", "/api/v1/validation-capabilities")
            old.check(PROFILE not in (capability.get("sourceProfiles") or []),
                      "unsupported Worker withholds project image validation profile")
            source = old.open_fixture("image-static")
            rejected = old.mutate("unsupported-image-validation", "POST",
                                  "/api/v1/project-sessions/" + source["projectSessionId"] + "/validation-jobs",
                                  {"expectedRevision": source["projectRevision"], "framesPerOutput": 1},
                                  status=503, code="taskUnavailable")
            old.check(rejected.get("field") == "worker" and rejected.get("failureCode") == "WorkerImageValidationUnavailable",
                      "unsupported Worker rejects image validation with exact taskUnavailable annotation")
            old.close_project("unsupported-close", source)
        except BaseException as error:
            failure = error
        finally:
            code, old_survivors, shutdown_failure = old.finish_server()
            failure = failure or shutdown_failure
            try:
                old.verify_fixture_inputs()
                scanned = old.scan_credentials()
            except BaseException as error:
                failure = failure or error
                scanned = None
            self.credentials.extend([old.bootstrap, *old.credentials])
            self.unsupported_result = {
                "succeeded": failure is None, "failure": str(failure) if failure else None,
                "sourceCommit": manifest["sourceCommit"], "deploymentManifestSha256": digest(old_dist / "deployment.json"),
                "requestCount": old.request_count, "assertionCount": len(old.assertions), "assertions": old.assertions,
                "serverExitCode": code, "serverIdentities": old.server_identities, "serverRuns": old.server_runs,
                "observedProcesses": list(old.observed_processes.values()), "survivorsBeforeForcedCleanup": old_survivors,
                "forcedCleanup": old.forced_cleanup, "survivingObservedProcesses": old.live_observed(),
                "credentialScanFileCount": scanned,
                "scope": "Preview-only Worker negative capability boundary; no image validation execution claim."}
            mod.MOD.HELPER.save_json(old_output / "manifest.json", self.unsupported_result)
        if failure:
            raise RuntimeError(str(failure)) from (failure if isinstance(failure, BaseException) else None)

    def verify_final_inputs(self):
        if self.frozen_inputs is None:
            return
        current = {"deploymentManifestSha256": digest(self.dist / "deployment.json"),
                    "deploymentFiles": inventory(self.dist), "productionInputs": production_inputs(),
                    "toolInputs": tool_inputs(), "oracleFiles": inventory(self.oracle.parent),
                    "oracleBuildManifestSha256": digest(self.oracle.parent.parent / "input-manifest.json")}
        self.check(current == self.frozen_inputs,
                   "all deployment, Oracle, production and tool inputs remain byte-stable through HTTP")
        mod.MOD.HELPER.save_json(self.output / "input-manifest-final.json", current)
        self.verify_fixture_inputs()
        self.check(not self.open_projects, "all opened image validation projects were explicitly closed")

    def verify_fixture_inputs(self):
        if self.fixture_inputs is None:
            return
        current = inventory(self.output / "projects")
        for name, expected in self.fixture_inputs.items():
            self.check(current.get(name) == expected, "fixture input remains byte-stable: " + name)
        additions = set(current) - set(self.fixture_inputs)
        expected_markers = {"markers/runaway-" + stage + "-" + action + suffix
                            for stage in ("Initialize", "Evaluate")
                            for action in ("cancel", "deadline", "close") for suffix in ("", ".ready")}
        self.check(additions <= expected_markers, "fixture input additions are only explicit actual-author markers")
        mod.MOD.HELPER.save_json(self.output / "fixture-inputs-final.json", current)

    def finish_server(self):
        """Drain our Server and record failed drain before any emergency cleanup."""
        failure, code = None, None
        self.capture_workers()
        if self.process is not None:
            if self.process.poll() is None:
                self.process.terminate()
            try:
                code = self.process.wait(timeout=25)
            except subprocess.TimeoutExpired:
                self.forced_cleanup.append({"kind": "server", "pid": self.process.pid})
                self.process.kill()
                code = self.process.wait(timeout=5)
                failure = "Server required forced cleanup"
        survivors = self.live_observed()
        if code != 0 or survivors:
            failure = failure or "Server did not exit0 with every observed descendant drained"
        for identity in survivors:
            now = self.process_identity(identity["pid"])
            if now and now["startTicks"] == identity["startTicks"]:
                self.forced_cleanup.append(identity)
                try:
                    os.kill(identity["pid"], signal.SIGKILL)
                except ProcessLookupError:
                    pass
        if self.process is not None:
            self.server_runs.append({"name": "final-shutdown", "pid": self.process.pid,
                                     "exitCode": code, "forcedKill": bool(self.forced_cleanup)})
        self.launcher.unlink(missing_ok=True)
        return code, survivors, failure

    def scan_credentials(self):
        scanned = 0
        for path in self.output.rglob("*"):
            if path.is_file():
                raw = path.read_bytes()
                self.check(not any(value.encode() in raw for value in [self.bootstrap, *self.credentials] if value),
                           "credential scan: " + str(path.relative_to(self.output)))
                scanned += 1
        return scanned

    def run(self):
        environment = os.environ.copy()
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "MAKO_BACKEND_FAKE_MEDIA", "MAKO_BACKEND_CREDENTIAL_FILE"):
            environment.pop(key, None)
        self.bootstrap = secrets.token_hex(32)
        environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap,
                           MAKO_BACKEND_RESOURCE_ROOT=str(self.output / "resources"),
                           MAKO_BACKEND_STATE_ROOT=str(self.output / "state"),
                           MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({"validation-probe-root": str(self.output / "projects")}),
                           MAKO_BACKEND_CLIENT_TOKEN_SECONDS="3600", MAKO_BACKEND_CLIENT_LEASE_SECONDS="30",
                           MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS="8", MAKO_BACKEND_WORKER_PATH=str(self.worker))
        self.environment = environment
        failure, code, survivors, scanned = None, None, [], None
        try:
            self.check(self.worker.is_file() and os.access(self.worker, os.X_OK),
                       "deployment: configured Worker apphost exists and is executable")
            self.verify_oracle_inputs()
            self.start_server(environment, "image-validation-server")
            self.exercise()
        except BaseException as error:
            failure = str(error)
            (self.output / "failure.txt").write_text(traceback.format_exc())
        finally:
            code, survivors, shutdown_failure = self.finish_server()
            failure = failure or shutdown_failure
        try:
            if failure is None:
                self.exercise_unsupported_worker()
            self.verify_final_inputs()
            scanned = self.scan_credentials()
        except BaseException as error:
            failure = failure or str(error)
            (self.output / "finalization-failure.txt").write_text(traceback.format_exc())
        manifest = {"succeeded": failure is None, "failure": failure,
                    "requestCount": self.request_count, "assertionCount": len(self.assertions), "assertions": self.assertions,
                    "serverExitCode": code, "serverIdentities": self.server_identities, "serverRuns": self.server_runs,
                    "observedProcesses": list(self.observed_processes.values()),
                    "terminalDrainChecks": self.terminal_drain_checks,
                    "observedWorkerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in self.workers.items()],
                    "survivorsBeforeForcedCleanup": survivors, "forcedCleanup": self.forced_cleanup,
                    "survivingObservedProcesses": self.live_observed(), "credentialScanFileCount": scanned,
                    "imageJobs": self.image_jobs, "results": self.results, "comparisons": self.comparisons,
                    "pagination": self.pagination, "imageFixtures": self.image_fixtures,
                    "ownedMutations": self.mutations, "runawayResults": self.runaway_results,
                    "unsupportedWorker": self.unsupported_result,
                    "scope": "Finite saved ProjectStore Image validation: static, static-analysis and direct-package ordinary/transparent reports; report bytes/SHA/diagnostics pagination/SSE/ACL/revision; input/report budgets; corrupt/dimension/decoded failures; fixed accepted bytes after internal input mutation; queued/running cancel/deadline/close; actual Initialize/Evaluate x controls, one orderly recovery, observed process drain and credential scan. No PNG/pixel/browser/full-project/B3/B4/B5/B6 claim."}
        mod.MOD.HELPER.save_json(self.output / "manifest.json", manifest)
        if failure:
            raise SystemExit(failure)
        print(json.dumps({"succeeded": True, "requests": self.request_count,
                          "assertions": len(self.assertions), "serverExitCode": code,
                          "unsupportedWorkerRequests": (self.unsupported_result or {}).get("requestCount", 0)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--server", nargs="+", required=True)
    parser.add_argument("--worker", required=True)
    parser.add_argument("--oracle", type=Path, required=True)
    parser.add_argument("--unsupported-worker", type=Path,
                        help="immutable preview-only Worker used for a separate capability-negative evidence tree")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    ImageValidationProbe(args.output.resolve(), args.server, args.worker, args.oracle,
                         args.unsupported_worker).run()
