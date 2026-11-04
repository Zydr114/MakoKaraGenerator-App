#!/usr/bin/env python3
"""Finite SDK2 particle author HTTP acceptance with real hardware Vulkan PNGs."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import random
import sys
import time
from urllib.parse import urlencode
import zipfile

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SPEC = importlib.util.spec_from_file_location("m08_m07_author", HERE / "m07_author_probe.py")
m07 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m07)
selection = m07.selection
save_json = m07.save_json
SAMPLES = ("CursorTrail", "VectorTrail", "EventBurst")


class M08AuthorProbe(m07.M07AuthorProbe):
    """Use M07 lifecycle/redaction/device observation, with an independent M08 run."""

    def __init__(self, output, dist, icd):
        super().__init__(output, dist, icd)
        self.fixtures = []
        self.particle_runs = []
        self.budget_failure = None
        self.retained_drafts = {}

    def record(self, label, draft):
        result = super().record(label, draft)
        self.retained_drafts[draft["draftId"]] = draft
        return result

    def import_sample(self, name):
        directory = ROOT / "samples" / "ScriptEffectsV2" / name
        raw_manifest = (directory / "manifest.json").read_bytes()
        manifest = json.loads(raw_manifest)
        files = {item["path"]: (directory / item["path"]).read_bytes() for item in manifest["files"]}
        self.check(manifest["packageVersion"] == 3 and manifest["sdkVersion"].split(".")[0] == "2",
            name + ": actual package3/SDK2 fixture")
        self.check(all(hashlib.sha256(files[item["path"]]).hexdigest() == item["sha256"] for item in manifest["files"]),
            name + ": exact repository bytes match every manifest hash")
        controllers = [item for item in manifest["exports"] if item["kind"] == "controller"]
        self.check(len(controllers) == 1, name + ": one real controller export")
        archive_bytes = io.BytesIO()
        with zipfile.ZipFile(archive_bytes, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("manifest.json", raw_manifest)
            for path, data in files.items():
                archive.writestr(path, data)
        data = archive_bytes.getvalue()
        asset = self.upload(name + "-upload", "m08-" + name + ".mkfx", "application/octet-stream", data)
        inspected = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect",
            value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], name + ": real package admitted through HTTP")
        draft = self.request(name + "-create", "POST", "/api/v1/drafts", status=201,
            value={"packageAssetId": asset["assetId"], "exportId": controllers[0]["id"], "name": "M08 " + name})
        self.check(draft["packageId"] == manifest["packageId"] and draft["exportId"] == controllers[0]["id"]
            and draft["packageVersion"] == 3 and draft["sdkVersion"].split(".")[0] == "2" and draft["canCompile"],
            name + ": imported draft advertises exact real controller and SDK2 profile")
        self.check(draft["currentSourceRevision"] == inspected["packageRevision"] == draft["configuration"]["sourceRevision"],
            name + ": imported source and in-use source agree")
        self.fixtures.append({"sample": name, "packageId": manifest["packageId"], "exportId": controllers[0]["id"],
            "manifestSha256": hashlib.sha256(raw_manifest).hexdigest(), "archiveSha256": hashlib.sha256(data).hexdigest(),
            "sourceRevision": draft["currentSourceRevision"],
            "files": {path: hashlib.sha256(value).hexdigest() for path, value in files.items()}})
        save_json(self.output / "fixtures.json", self.fixtures)
        return self.record(name + "-imported", draft)

    @staticmethod
    def selection(draft):
        # The actual author sample is finite (12s). Project binding includes its
        # later 6-11s lyrics; selecting the first line would hide that source.
        sample = next(item for item in draft["sampleOptions"]["samples"]
            if item["sampleId"] == "workshop-multi-line-v1")
        target = next(item for item in sample["targets"] if item["key"] == "project")
        return {"sampleId": sample["sampleId"], "targetKey": target["key"]}

    def configure(self, label, draft, parameters):
        config = self.configuration(draft, parametersJson=json.dumps(parameters, separators=(",", ":")),
            relativeTarget="application", readScope="boundSources", branchInput=None, fixedSeed="17")
        saved = self.request(label, "PUT", self.path(draft) + "/configuration",
            value={"expectedDraftRevision": draft["draftRevision"], "configuration": config,
                "metadata": self.metadata(label)})
        # The response DTO omits nullable branchInput/fixedSeed when null.
        # Project every writable field instead of requiring those null keys to
        # exist on the wire; every non-null value must still match exactly.
        retained = {key: saved["configuration"].get(key) for key in config}
        self.check(saved["draftRevision"] != draft["draftRevision"] and retained == config,
            label + ": content CAS changes and complete writable configuration retained")
        self.check(saved["configuration"]["sourceRevision"] == draft["configuration"]["sourceRevision"],
            label + ": parameter configuration preserves exact in-use source")
        return self.record(label, saved)

    def sources(self, label, draft):
        return self.request(label, "GET", self.path(draft) + "/sources?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": draft["currentSourceRevision"]}))["files"]

    def compile_and_adopt(self, label, draft, sources):
        before = draft
        draft = self.save_sources(label + "-save", draft, sources)
        source = draft["currentSourceRevision"]
        self.check(source != before["currentSourceRevision"], label + ": real authored source revision changed")
        result = self.compile_source(label + "-compile", draft, source, asynchronous=True)
        draft = self.refreshed(label + "-compiled", draft)
        self.assert_in_use(label + "-compile", before, draft)
        draft = self.adopt(label + "-adopt", draft, source)
        return draft, result

    def create_preview(self, label, draft):
        preview = self.request(label, "POST", "/api/v1/preview-sessions", status=201, value=self.preview_request(draft))
        self.open_previews.append((self.client, preview))
        self.check(preview["renderBackend"] == "gpu" and preview["source"] == self.preview_request(draft)["source"],
            label + ": explicit GPU input captures full exact draft/source/sample identity")
        return preview

    def particle_frame(self, label, preview, time_us, sequence):
        pixels = self.frame(label, preview, str(time_us), sequence, intent="seek")
        state = self.state(label + "-identity", preview)
        frame = state["latestFrame"]
        expected = {"previewId": preview["previewId"], "previewGeneration": preview["previewGeneration"],
            "sourceRevision": preview["source"]["sourceRevision"], "requestSeq": str(sequence), "timeUs": str(time_us)}
        self.check(state["source"] == preview["source"] and all(frame.get(key) == value for key, value in expected.items()),
            label + ": ready frame binds exact full author input, generation, request and microsecond")
        diagnostics = self.diagnostic(label + "-diagnostics", preview)
        self.check(diagnostics["sourceRevision"] == expected["sourceRevision"]
            and diagnostics["previewGeneration"] == expected["previewGeneration"] and not diagnostics["hasMore"]
            and not any(item["severity"] == "error" for item in diagnostics["diagnostics"]),
            label + ": complete same-generation diagnostics contain no runtime error")
        evidence = {"name": label, "source": copy.deepcopy(state["source"]), "frame": copy.deepcopy(frame),
            "rgbaSha256": hashlib.sha256(pixels).hexdigest(), "renderBackend": state["renderBackend"]}
        self.author_frames.append(evidence)
        save_json(self.output / "author-frames.json", self.author_frames)
        return evidence

    def retained_png(self, label, evidence):
        frame = evidence["frame"]
        _, png, _ = self.fetch(label, frame["content"]["contentUrl"])
        self.check(len(png) == frame["content"]["sizeBytes"]
            and hashlib.sha256(png).hexdigest() == frame["content"]["sha256"], label + ": fixed old binary handle remains byteexact")
        pixels = self.decode_png(label, png)
        self.check(hashlib.sha256(pixels).hexdigest() == evidence["rgbaSha256"], label + ": fixed old decoded frame remains unchanged")

    def refresh_input(self, label, preview, draft):
        previous = copy.deepcopy(preview)
        updated = self.request(label, "PUT", "/api/v1/preview-sessions/" + preview["previewId"] + "/input",
            value={"expectedPreviewGeneration": preview["previewGeneration"],
                "expectedSourceRevision": preview["source"]["sourceRevision"], "source": self.preview_request(draft)["source"],
                "size": preview["size"], "alphaMode": preview["alphaMode"], "metadata": self.metadata(label)})
        self.check(int(updated["previewGeneration"]) > int(previous["previewGeneration"])
            and updated["source"] == self.preview_request(draft)["source"] and updated["renderBackend"] == "gpu",
            label + ": explicit input replacement advances generation and pins new content while preserving GPU")
        preview.clear()
        preview.update(updated)

    def delete_draft(self, label, draft):
        self.request(label, "DELETE", self.path(draft), value={"expectedDraftRevision": draft["draftRevision"],
            "metadata": self.metadata(label)})
        self.request(label + "-gone", "GET", self.path(draft), status=404, code="resourceNotFound")
        self.retained_drafts.pop(draft["draftId"], None)
        self.active_draft = None

    def exercise_sample(self, name):
        self.stage = name + ":import-configure-compile-adopt"
        draft = self.import_sample(name)
        parameters = json.loads(draft["configuration"]["parametersJson"])
        parameters.update(sizePx=6, spreadPx=0)
        draft = self.configure(name + "-configure", draft, parameters)
        original = self.sources(name + "-sources", draft)
        authored = [{**item, "text": item["text"] + "\n// M08 exact particle author revision\n"} for item in original]
        draft, compiled = self.compile_and_adopt(name, draft, authored)
        self.stage = name + ":gpu-random-repeat"
        preview = self.create_preview(name + "-gpu", draft)
        times = [1_250_000, 10_250_000, 6_250_000]
        random.Random(8).shuffle(times)
        times.extend([1_250_000, 10_250_000, 1_250_000])
        hashes = {}
        near = None
        for sequence, at in enumerate(times, 1):
            result = self.particle_frame(name + "-seek-" + str(sequence), preview, at, sequence)
            self.check(at not in hashes or hashes[at] == result["rgbaSha256"], name + ": repeated microsecond has identical complete decoded pixels")
            hashes[at] = result["rgbaSha256"]
            if at == 1_250_000:
                near = result
        self.retained_png(name + "-retained-before-edit", near)
        self.stage = name + ":parameter-cas-and-input-replacement"
        before = draft
        parameters["sizePx"] = 14
        draft = self.configure(name + "-parameter-edit", draft, parameters)
        self.request(name + "-parameter-stale", "PUT", self.path(draft) + "/configuration", status=409, code="revisionConflict",
            value={"expectedDraftRevision": before["draftRevision"], "configuration": self.configuration(draft),
                "metadata": self.metadata(name + "-parameter-stale")})
        self.assert_unchanged(name + "-stale-retained", draft)
        # A seek creates a new generation, so exercise the old binary lease
        # after the draft edit and before submitting another frame request.
        self.retained_png(name + "-retained-after-edit", near)
        old = self.particle_frame(name + "-old-input", preview, 1_250_000, len(times) + 1)
        self.check(old["source"] == near["source"] and old["rgbaSha256"] == near["rgbaSha256"],
            name + ": real parameter save leaves existing fixed input and pixels unchanged")
        self.refresh_input(name + "-input-refresh", preview, draft)
        changed = self.particle_frame(name + "-new-input", preview, 1_250_000, len(times) + 2)
        self.check(changed["source"]["sourceRevision"] == draft["draftRevision"]
            and changed["source"]["authorSourceRevision"] == compiled["sourceRevision"]
            and changed["rgbaSha256"] != near["rgbaSha256"], name + ": new fixed parameters actually change rendered particle pixels")
        self.particle_runs.append({"sample": name, "compilation": compiled, "timesUs": times,
            "oldInputFrame": near, "newInputFrame": changed})
        save_json(self.output / "particle-runs.json", self.particle_runs)
        self.close(name + "-close", preview)
        self.require_workers_drained(name + "-workers")
        if name == "CursorTrail":
            self.exercise_budget(draft)
        self.delete_draft(name + "-delete", self.retained_drafts[draft["draftId"]])

    def exercise_budget(self, draft):
        self.stage = "CursorTrail:real-particle-budget-failure"
        sources = self.sources("budget-original-source", draft)
        original = 'double rate = p.GetNumber("rate"), finalRate = p.GetNumber("finalRate");'
        replacement = 'double rate = 60_000, finalRate = 60_000; // M08 real birth-budget negative fixture'
        self.check(sum(item["text"].count(original) for item in sources) == 1,
            "budget: actual CursorTrail source has one explicit rate declaration")
        negative = [{**item, "text": item["text"].replace(original, replacement)} for item in sources]
        draft, compiled = self.compile_and_adopt("budget", draft, negative)
        preview = self.create_preview("budget-gpu", draft)
        self.particle_frame("budget-empty-time-zero", preview, 0, 1)
        path = "/api/v1/preview-sessions/" + preview["previewId"]
        accepted = self.request("budget-frame-request", "POST", path + "/frame-requests", status=202,
            value={"expectedSourceRevision": preview["source"]["sourceRevision"],
                "previewGeneration": preview["previewGeneration"], "requestSeq": "2", "timeUs": "1000000", "intent": "seek"})
        self.check(accepted["previewId"] == preview["previewId"] and accepted["requestSeq"] == "2"
            and accepted["timeUs"] == "1000000" and accepted["state"] == "accepted"
            and int(accepted["previewGeneration"]) > int(preview["previewGeneration"]),
            "budget: accepted is only admission with exact request and next generation")
        preview["previewGeneration"] = accepted["previewGeneration"]
        deadline = time.monotonic() + 60
        failed = None
        poll = 0
        while time.monotonic() < deadline:
            poll += 1
            state = self.request("budget-state-" + str(poll), "GET", path)
            current = state["session"]
            self.check(current["source"] == preview["source"]
                and current["previewGeneration"] == preview["previewGeneration"], "budget: fixed failure input retained")
            self.check(not current.get("latestFrame"), "budget: failing generation publishes no successful PNG or prior frame")
            if current["state"] == "failed":
                failed = state
                break
            time.sleep(.15)
        self.check(failed is not None and failed["session"]["latestRequestSeq"] == "2" and bool(failed.get("failure")),
            "budget: actual request reaches failed terminal with service failure")
        self.check(failed.get("renderFailureCode") == "EmissionRecordBudgetExceeded", "budget: exact particle birth-record budget code")
        page = self.diagnostic("budget-diagnostics", preview)
        self.check(page["previewGeneration"] == preview["previewGeneration"]
            and page["sourceRevision"] == preview["source"]["sourceRevision"] and not page["hasMore"],
            "budget: complete failure diagnostics bind exact input generation")
        matches = [item for item in page["diagnostics"] if item["code"] == "EmissionRecordBudgetExceeded"]
        self.check(len(matches) == 1, "budget: one actual birth-budget diagnostic")
        diagnostic = matches[0]
        occurrence = diagnostic.get("occurrence") or {}
        package = occurrence.get("package") or {}
        self.check(diagnostic["severity"] == "error" and diagnostic.get("originKind") == "runtime"
            and bool(occurrence.get("epoch")) and int(occurrence.get("sequence", "0")) > 0
            and occurrence.get("instanceId", "").startswith("fx:") and occurrence.get("stage") == "ReadParticleBirths"
            and occurrence.get("timeUs") == "1000000" and occurrence.get("artifactHash") == compiled["artifactHash"]
            and package == {"packageId": draft["packageId"], "revision": draft["configuration"]["sourceRevision"],
                "exportId": draft["exportId"]}, "budget: actual runtime occurrence identifies exact instance, package/source/export and compiled artifact")
        self.budget_failure = {"state": failed, "diagnostics": page, "compilation": compiled,
            "fixture": "Actual CursorTrail authored revision, fixed 60000/s; first one-second query exceeds 50000 birth records before anchor allocation."}
        save_json(self.output / "particle-budget-failure.json", self.budget_failure)
        self.close("budget-close", preview)
        self.require_workers_drained("budget-workers")

    def run(self):
        deployment = selection.host.validate_dist(self.dist)
        self.deployment = {key: deployment.get(key) for key in ("version", "sourceCommit", "sourceDirty", "rid", "selfContained")}
        save_json(self.output / "deployment-reference.json", {**self.deployment, "dist": str(self.dist),
            "manifestSha256": hashlib.sha256((self.dist / "deployment.json").read_bytes()).hexdigest()})
        self.start("m08-start")
        self.connect("m08")
        self.check(self.current_ready["version"] == deployment["version"], "ready host matches actual matched deployment version")
        availability = self.request("m08-render-backends", "GET", "/api/v1/render-backends")
        gpu = next(item for item in availability["backends"] if item["renderBackend"] == "gpu")
        self.check(gpu["available"] and gpu["api"] == "vulkan" and bool(gpu.get("device")), "actual deployment offers Vulkan GPU")
        for sample in SAMPLES:
            self.exercise_sample(sample)
        self.stage = "cleanup"
        self.require_workers_drained("m08-all-workers")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    args = parser.parse_args()
    icd = selection.gpu.hardware_icd(args.icd)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    os.environ["MAKO_RENDER_BACKEND"] = "cpu"
    os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = icd["path"]
    probe = M08AuthorProbe(output, args.dist.resolve(), icd)
    save_json(output / "environment.json", {"defaultBackend": "cpu", "icd": icd, "scenarioBackend": "gpu"})
    failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
        save_json(output / "failure.json", {"stage": probe.stage, "errorType": type(error).__name__,
            "detail": probe.safe_text(error), "lastRequest": probe.last_request,
            "retainedDraftIds": list(probe.retained_drafts)})
    finally:
        try:
            probe.stop("m08-final")
        except BaseException as error:
            failure = failure or error
            save_json(output / "shutdown-failure.json", {"errorType": type(error).__name__, "detail": probe.safe_text(error)})
    with probe.gpu_lock:
        hardware = list(probe.gpu_samples.values())
    save_json(output / "gpu-workers.json", {"observations": hardware, "assignments": probe.gpu_assignments})
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        scanned = None
        save_json(output / "scan-failure.json", {"errorType": type(error).__name__, "detail": probe.safe_text(error)})
    summary = {"status": "failed" if failure else "passed", "errorType": type(failure).__name__ if failure else None,
        "httpRequests": probe.request_count, "assertions": len(probe.assertions), "assertionLabels": probe.assertions,
        "deployment": probe.deployment, "jobTerminals": probe.terminals, "runs": probe.runs,
        "fixtures": probe.fixtures, "authorFrames": probe.author_frames, "particleRuns": probe.particle_runs,
        "particleBudgetFailure": probe.budget_failure, "credentialScannedFiles": scanned,
        "workerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.worker_identities)],
        "scope": "Finite Linux HTTP acceptance of three actual SDK2 particle packages, exact compile/adopt, author CAS, "
            "pinned and replaced inputs, full PNG decode, random/repeated finite sample times, actual birth-budget failure "
            "and hardware Vulkan Worker observation. No browser/display, full author validation, library reopen, shared texture, "
            "cross-platform or performance acceptance."}
    save_json(output / "summary.json", summary)
    save_json(output / "evidence-manifest.json", {"files": {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(output.rglob("*")) if path.is_file()}})
    print(json.dumps({key: summary[key] for key in ("status", "httpRequests", "assertions", "errorType")}))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
