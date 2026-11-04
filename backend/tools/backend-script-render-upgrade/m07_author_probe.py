#!/usr/bin/env python3
"""Finite fresh HTTP acceptance for SDK2 author revisions and hardware GPU preview."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import time
from urllib.parse import urlencode
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


w8 = module("m07_w8", ROOT / "tools/backend-w8-authoring-http-probe/probe.py")
selection = module("m07_selection", ROOT / "tools/backend-render-backend-probe/probe.py")
save_json = w8.save_json


class M07AuthorProbe(selection.SelectionProbe):
    """Reuse the matched launcher/device observer and W8 immutable package reads."""
    path = staticmethod(w8.W8Probe.path)
    read = w8.W8Probe.read
    files = w8.W8Probe.files
    history = w8.W8Probe.history
    save_files = w8.W8Probe.save_files
    download = w8.W8Probe.download

    def __init__(self, output, dist, icd):
        super().__init__(output, dist, None, icd, False)
        self.deployment = {}
        self.stage = "initialize"
        self.last_request = None
        self.last_renewal = time.monotonic()
        self.renewing = False
        self.author_identities = []
        self.author_frames = []
        self.active_draft = None

    def safe_text(self, value):
        text = str(value)
        secrets = set(self.secrets) | set(self.tokens.values())
        if self.bootstrap:
            secrets.add(self.bootstrap)
        if self.registration:
            secrets.update(self.registration.get(key, "") for key in ("accessToken", "resumeToken"))
        for secret in sorted(filter(None, secrets), key=len, reverse=True):
            text = text.replace(secret, "<redacted>")
        return text

    def redact(self, value):
        if isinstance(value, dict):
            return {key: "<redacted>" if key.lower() in ("accesstoken", "resumetoken", "bootstraptoken")
                else self.redact(item) for key, item in value.items()}
        if isinstance(value, list):
            return [self.redact(item) for item in value]
        return self.safe_text(value) if isinstance(value, str) else value

    def register(self, *args, **kwargs):
        result = super().register(*args, **kwargs)
        self.secrets.extend((result["accessToken"], result["resumeToken"]))
        self.last_renewal = time.monotonic()
        return result

    def request(self, name, method, path, **kwargs):
        if self.client and not self.renewing and path != "/api/v1/clients" \
                and time.monotonic() - self.last_renewal >= 45:
            self.renewing = True
            try:
                self.register("m07-resume-" + str(self.request_count + 1), resume=self.registration)
            finally:
                self.renewing = False
        self.last_request = {"name": name, "method": method, "path": path}
        return super().request(name, method, path, **kwargs)

    def long_job(self, name, accepted, expected="succeeded"):
        # SelectionProbe.long_job expects render jobs; compilation has its own
        # authorCompilationInput and uses CompletionProbe's bounded terminal wait.
        return selection.completion.CompletionProbe.long_job(self, name, accepted, expected)

    def record(self, label, draft):
        self.active_draft = draft
        self.author_identities.append({"name": label, **{key: draft.get(key) for key in (
            "draftId", "draftRevision", "currentSourceRevision", "adoptedSourceRevision", "packageVersion", "sdkVersion")},
            "inUseSourceRevision": draft["configuration"]["sourceRevision"],
            "lastCompilation": draft.get("lastCompilation")})
        save_json(self.output / "author-identities.json", self.author_identities)
        return draft

    def refreshed(self, label, draft):
        return self.record(label, self.read(label, draft))

    @staticmethod
    def configuration(draft, **changes):
        value = {key: copy.deepcopy(draft["configuration"].get(key)) for key in (
            "name", "packageId", "sourceRevision", "exportId", "parametersJson", "relativeTarget",
            "readScope", "branchInput", "fixedSeed", "overlayLayer")}
        return {**value, **changes}

    def assert_unchanged(self, label, before):
        after = self.read(label, before)
        self.check(after == before, label + ": complete author state unchanged")
        return after

    def assert_in_use(self, label, before, after):
        self.check(after["configuration"] == before["configuration"]
            and after.get("adoptedSourceRevision") == before.get("adoptedSourceRevision"),
            label + ": latest save/compile preserves configuration and adopted source")

    def compile_source(self, label, draft, source, *, succeeded=True, asynchronous=False):
        response = self.request(label, "POST", self.path(draft) + "/compilation-jobs",
            status=202 if asynchronous else 200, value={"expectedDraftRevision": draft["draftRevision"],
                "sourceRevision": source, "executionMode": "async" if asynchronous else "sync"})
        if asynchronous:
            fixed = {"draftId": draft["draftId"], "draftRevision": draft["draftRevision"], "sourceRevision": source}
            self.check(response["authorCompilationInput"] == fixed, label + ": accepted compilation fixes exact source")
            terminal = self.long_job(label, response)
            self.check(terminal["authorCompilationInput"] == fixed, label + ": terminal retains fixed compilation input")
            response = self.request(label + "-result", "GET", "/api/v1/jobs/" + response["jobId"] + "/compilation-result")
        self.check(response["draftId"] == draft["draftId"] and response["draftRevision"] == draft["draftRevision"]
            and response["sourceRevision"] == source and response["succeeded"] is succeeded,
            label + ": result binds captured draft/source and expected compiler outcome")
        self.check(response["stale"] is (source != draft["currentSourceRevision"]), label + ": stale is relative to latest source")
        self.check(not any(item["isError"] for item in response["diagnostics"]) if succeeded
            else any(item["isError"] for item in response["diagnostics"]), label + ": diagnostics match compiler outcome")
        return response

    def adopt(self, label, draft, source, *, status=200, failure=None):
        response = self.request(label, "POST", self.path(draft) + "/adopt-source-revision", status=status,
            code="commandRejected" if failure else None,
            value={"expectedDraftRevision": draft["draftRevision"], "sourceRevision": source})
        if failure:
            self.check(response.get("failureCode") == failure, label + ": specific adoption gate")
            self.assert_unchanged(label + "-unchanged", draft)
            return draft
        self.check(response["previousSourceRevision"] == draft["configuration"]["sourceRevision"]
            and response["adoptedSourceRevision"] == source, label + ": adoption records exact before/after source")
        result = self.refreshed(label + "-draft", draft)
        self.check(result["configuration"]["sourceRevision"] == result["adoptedSourceRevision"] == source,
            label + ": only explicit adoption changes in-use source")
        return result

    def save_sources(self, label, draft, sources):
        result = self.request(label, "POST", self.path(draft) + "/source-revision-jobs",
            value={"expectedDraftRevision": draft["draftRevision"], "files": sources})
        current = self.refreshed(label + "-draft", draft)
        self.check(current["currentSourceRevision"] == result["sourceRevision"]
            and current["currentSourceRevision"] != draft["currentSourceRevision"], label + ": new immutable latest source")
        self.assert_in_use(label, draft, current)
        return current

    def sample(self):
        directory = ROOT / "samples/ScriptEffectsV2/WordEmphasis"
        raw_manifest = (directory / "manifest.json").read_bytes()
        manifest = json.loads(raw_manifest)
        files = {entry["path"]: (directory / entry["path"]).read_bytes() for entry in manifest["files"]}
        self.check(manifest["packageVersion"] == 3 and manifest["sdkVersion"].split(".")[0] == "2",
            "repository fixture uses actual package3/SDK2 profile")
        self.check(all(hashlib.sha256(files[item["path"]]).hexdigest() == item["sha256"] for item in manifest["files"]),
            "repository fixture bytes match every manifest hash")
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("manifest.json", raw_manifest)
            for path, data in files.items():
                archive.writestr(path, data)
        data = output.getvalue()
        asset = self.upload("m07-package-upload", "m07-word-emphasis.mkfx", "application/octet-stream", data)
        inspection = self.request("m07-package-inspect", "POST", "/api/v1/effect-packages/inspect",
            value={"packageAssetId": asset["assetId"]})
        self.check(inspection["structureValid"], "SDK2 archive structure admitted through HTTP")
        draft = self.request("m07-draft-create", "POST", "/api/v1/drafts", status=201,
            value={"packageAssetId": asset["assetId"], "exportId": "word-emphasis", "name": "M07 SDK2 author acceptance"})
        self.check(draft["packageVersion"] == 3 and draft["sdkVersion"].split(".")[0] == "2" and draft["canCompile"],
            "created draft directly advertises SDK2 profile and C# compilation")
        self.check(draft["currentSourceRevision"] == inspection["packageRevision"] == draft["configuration"]["sourceRevision"],
            "import/inspection/draft configuration agree on initial immutable source")
        save_json(self.output / "fixture.json", {"sample": "ScriptEffectsV2/WordEmphasis",
            "manifestSha256": hashlib.sha256(raw_manifest).hexdigest(), "archiveSha256": hashlib.sha256(data).hexdigest(),
            "files": {path: hashlib.sha256(value).hexdigest() for path, value in files.items()}})
        return self.record("imported-A", draft)

    @staticmethod
    def selection(draft):
        options = draft["sampleOptions"]
        sample = next(item for item in options["samples"] if item["sampleId"] == options["defaultSampleId"])
        value = {"sampleId": sample["sampleId"], "targetKey": sample["targets"][0]["key"]}
        if options["materialSlots"]:
            value["materialSlot"] = options["materialSlots"][0]
        return value

    def preview_request(self, draft, backend="gpu"):
        return {"source": {"kind": "authorDraft", "sourceId": draft["draftId"],
            "sourceRevision": draft["draftRevision"], "authorSourceRevision": draft["configuration"]["sourceRevision"],
            "templateSample": self.selection(draft)}, "size": {"width": 320, "height": 180},
            "alphaMode": "opaque", "purpose": "preview", "previewKind": "authorDraft", "committable": False,
            "renderBackend": backend}

    def author_frame(self, label, preview, sequence):
        pixels = self.frame(label, preview, "3000000", sequence)
        state = self.request(label + "-identity", "GET", "/api/v1/preview-sessions/" + preview["previewId"])
        session = state["session"]
        self.check(session["source"] == preview["source"], label + ": complete author draft and source identities remain pinned")
        diagnostics = self.diagnostic(label + "-diagnostics", preview)
        self.check(diagnostics["sourceRevision"] == preview["source"]["sourceRevision"]
            and diagnostics["previewGeneration"] == preview["previewGeneration"], label + ": diagnostics bind same source/generation")
        self.check(not diagnostics["hasMore"] and not any(item["severity"] == "error" for item in diagnostics["diagnostics"]),
            label + ": complete first diagnostics page contains no runtime error")
        self.author_frames.append({"name": label, "source": session["source"], "previewId": session["previewId"],
            "previewGeneration": session["previewGeneration"], "frame": session["latestFrame"],
            "rgbaSha256": hashlib.sha256(pixels).hexdigest(), "renderBackend": session["renderBackend"]})
        save_json(self.output / "author-frames.json", self.author_frames)

    def reject_cpu(self, draft, preview):
        self.stage = "cpu-admission"
        error = self.request("m07-cpu-create", "POST", "/api/v1/preview-sessions", status=422,
            code="commandNotSupported", value=self.preview_request(draft, "cpu"))
        self.check(error.get("field") == "renderBackend" and error.get("failureCode") == "PackageBackendUnsupported",
            "CPU author create has specific backend rejection")
        error = self.request("m07-cpu-validation", "POST", self.path(draft) + "/validation-jobs", status=422,
            code="commandNotSupported", value={"expectedDraftRevision": draft["draftRevision"],
                **self.selection(draft), "renderBackend": "cpu"})
        self.check(error.get("field") == "renderBackend" and error.get("failureCode") == "PackageBackendUnsupported",
            "CPU author validation has specific backend rejection before acceptance")
        before = self.state("m07-before-cpu-switch", preview)
        error = self.switch("m07-cpu-switch", preview, "cpu", status=422, code="commandNotSupported")
        self.check(error.get("field") == "renderBackend" and error.get("failureCode") == "PackageBackendUnsupported",
            "CPU author switch has specific backend rejection")
        self.check(self.state("m07-after-cpu-switch", preview) == before,
            "CPU rejection preserves GPU preview source/generation/frame")
        self.assert_unchanged("m07-after-cpu-draft", draft)

    def package_snapshot(self, label, draft, source):
        data = self.download(label, draft, source)
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = {name: archive.read(name) for name in archive.namelist()}
        manifest = json.loads(entries["manifest.json"])
        self.check(all(hashlib.sha256(entries[item["path"]]).hexdigest() == item["sha256"] for item in manifest["files"]),
            label + ": immutable archive entries match complete manifest hashes")
        self.check(manifest["packageVersion"] == 3 and manifest["sdkVersion"].split(".")[0] == "2",
            label + ": archive retains actual SDK2 profile")
        return entries

    def run(self):
        deployment = selection.host.validate_dist(self.dist)
        self.deployment = {key: deployment.get(key) for key in ("version", "sourceCommit", "sourceDirty", "rid", "selfContained")}
        save_json(self.output / "deployment-reference.json", {**self.deployment, "dist": str(self.dist),
            "manifestSha256": hashlib.sha256((self.dist / "deployment.json").read_bytes()).hexdigest()})
        self.start("m07-start")
        self.connect("m07")
        self.check(self.current_ready["version"] == deployment["version"], "ready host matches validated deployment version")
        availability = self.request("m07-render-backends", "GET", "/api/v1/render-backends")
        gpu = next(item for item in availability["backends"] if item["renderBackend"] == "gpu")
        self.check(gpu["available"] and gpu["api"] == "vulkan" and bool(gpu.get("device")),
            "actual isolated deployment offers Vulkan GPU")

        self.stage = "import-and-parameters"
        draft = self.sample()
        source_a = draft["currentSourceRevision"]
        original = self.request("m07-original-sources", "GET", self.path(draft) + "/sources?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source_a}))["files"]
        archive_a = self.package_snapshot("m07-initial-A", draft, source_a)
        initial = draft
        params = json.loads(draft["configuration"]["parametersJson"])
        params["peakScale"] = 1.5
        config = self.configuration(draft, parametersJson=json.dumps(params, separators=(",", ":")))
        draft = self.record("parameter-saved", self.request("m07-parameters-save", "PUT", self.path(draft) + "/configuration",
            value={"expectedDraftRevision": draft["draftRevision"], "configuration": config,
                "metadata": self.metadata("m07-parameters-save")}))
        self.check(draft["draftRevision"] != initial["draftRevision"] and draft["configuration"]["sourceRevision"] == source_a
            and json.loads(draft["configuration"]["parametersJson"])["peakScale"] == 1.5,
            "parameter save changes content CAS while preserving in-use source")
        self.request("m07-parameters-stale", "PUT", self.path(draft) + "/configuration", status=409, code="revisionConflict",
            value={"expectedDraftRevision": initial["draftRevision"], "configuration": config,
                "metadata": self.metadata("m07-parameters-stale")})
        self.assert_unchanged("m07-parameters-stale-retained", draft)

        self.stage = "failed-compilation"
        broken = [{**item, "text": item["text"] + "\n#error M07 deliberate compilation rejection\n"} for item in original]
        draft = self.save_sources("m07-save-broken-X", draft, broken)
        source_x = draft["currentSourceRevision"]
        before_compile = draft
        self.compile_source("m07-compile-X", draft, source_x, succeeded=False)
        draft = self.refreshed("m07-after-failed-X", draft)
        self.assert_in_use("failed compilation", before_compile, draft)
        draft = self.adopt("m07-adopt-X-rejected", draft, source_x, status=422, failure="AuthorSourceCompilationRequired")
        error = self.request("m07-config-bypass-X", "PUT", self.path(draft) + "/configuration", status=422, code="commandRejected",
            value={"expectedDraftRevision": draft["draftRevision"], "configuration": self.configuration(draft, sourceRevision=source_x),
                "metadata": self.metadata("m07-config-bypass-X")})
        self.check(error.get("failureCode") == "AuthorSourceCompilationRequired", "configuration source bypass has exact compilation gate")
        self.assert_unchanged("m07-config-bypass-X-retained", draft)

        self.stage = "compile-adopt-undo-redo"
        valid_b = [{**item, "text": item["text"] + "\n// M07 immutable revision B\n"} for item in original]
        draft = self.save_sources("m07-save-B", draft, valid_b)
        source_b = draft["currentSourceRevision"]
        before_compile = draft
        self.compile_source("m07-compile-B", draft, source_b, asynchronous=True)
        draft = self.refreshed("m07-after-compiled-B", draft)
        self.assert_in_use("successful compilation", before_compile, draft)
        draft = self.adopt("m07-adopt-B", draft, source_b)
        draft = self.adopt("m07-uncompiled-undo-A", draft, source_a, status=422, failure="AuthorSourceCompilationRequired")
        self.compile_source("m07-compile-history-A", draft, source_a)
        draft = self.refreshed("m07-after-history-compile", draft)
        self.check(draft["currentSourceRevision"] == draft["configuration"]["sourceRevision"] == source_b,
            "successful historical compile never changes latest or in-use source")
        draft = self.adopt("m07-undo-A", draft, source_a)
        stale_adopt = draft
        draft = self.adopt("m07-redo-B", draft, source_b)
        self.request("m07-adopt-stale", "POST", self.path(draft) + "/adopt-source-revision", status=409, code="revisionConflict",
            value={"expectedDraftRevision": stale_adopt["draftRevision"], "sourceRevision": source_a})
        self.assert_unchanged("m07-adopt-stale-retained", draft)
        archive_b = self.package_snapshot("m07-compiled-B", draft, source_b)

        self.stage = "gpu-preview"
        preview = self.request("m07-gpu-preview", "POST", "/api/v1/preview-sessions", status=201, value=self.preview_request(draft))
        self.open_previews.append((self.client, preview))
        self.check(preview["renderBackend"] == "gpu" and preview["source"]["authorSourceRevision"] == source_b,
            "GPU preview captures explicitly adopted B")
        self.author_frame("m07-gpu-B", preview, 1)
        self.reject_cpu(draft, preview)

        self.stage = "pinned-preview-and-parameter-cas"
        old_source = copy.deepcopy(preview["source"])
        params["peakScale"] = 2.0
        draft = self.record("preview-parameter-saved", self.request("m07-preview-parameters", "PUT", self.path(draft) + "/configuration",
            value={"expectedDraftRevision": draft["draftRevision"],
                "configuration": self.configuration(draft, parametersJson=json.dumps(params, separators=(",", ":"))),
                "metadata": self.metadata("m07-preview-parameters")}))
        self.author_frame("m07-old-input-remains-pinned", preview, 2)
        self.check(preview["source"] == old_source, "draft parameter save does not mutate existing preview input")
        update = self.request("m07-preview-refresh", "PUT", "/api/v1/preview-sessions/" + preview["previewId"] + "/input",
            value={"expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": old_source["sourceRevision"],
                "source": self.preview_request(draft)["source"], "size": preview["size"], "alphaMode": preview["alphaMode"],
                "metadata": self.metadata("m07-preview-refresh")})
        self.check(update["renderBackend"] == "gpu" and int(update["previewGeneration"]) > int(preview["previewGeneration"])
            and update["source"]["sourceRevision"] == draft["draftRevision"]
            and update["source"]["authorSourceRevision"] == source_b, "explicit input refresh pins new draft and preserves omitted GPU choice")
        preview.clear()
        preview.update(update)
        self.author_frame("m07-refreshed-input-B", preview, 3)
        self.close("m07-gpu", preview)

        self.stage = "package-text-cas-and-history"
        package = self.files("m07-B-files", draft, source_b)
        before_package = draft
        texts = [{"path": item["path"], "text": item["text"] + "\n// M07 package text C\n"}
            for item in package["files"] if item["editable"]]
        draft = self.record("package-C-saved", self.save_files("m07-save-package-C", draft, package, text_files=texts))
        source_c = draft["currentSourceRevision"]
        self.check(source_c != source_b and draft["configuration"]["sourceRevision"] == source_b,
            "package text save advances latest C and preserves adopted/in-use B")
        self.assert_in_use("package text save", before_package, draft)
        self.check((draft.get("lastCompilation") or {}).get("sourceRevision")
            == (before_package.get("lastCompilation") or {}).get("sourceRevision"),
            "package text save retains actual previous compilation identity")
        self.request("m07-package-stale", "PUT", self.path(draft) + "/package-files", status=409, code="revisionConflict",
            value={"expectedDraftRevision": before_package["draftRevision"], "manifestJson": package["manifestJson"],
                "textFiles": texts, "metadata": self.metadata("m07-package-stale")})
        self.assert_unchanged("m07-package-stale-retained", draft)
        history = self.history("m07-history", draft)
        revisions = {item["sourceRevision"]: item for item in history["revisions"]}
        self.check(set(revisions) == {source_a, source_x, source_b, source_c}, "history retains all and only four authored source revisions")
        self.check(revisions[source_a]["compilation"]["succeeded"] and revisions[source_b]["compilation"]["succeeded"]
            and revisions[source_x]["compilation"]["succeeded"] is False and not revisions[source_c].get("compilation"),
            "history preserves exact successful/failed/uncompiled identities")
        page = self.history("m07-history-page1", draft, limit=1)
        page2 = self.history("m07-history-page2", draft, limit=1, afterSourceRevision=page["nextAfterSourceRevision"])
        self.check(len(page["revisions"]) == len(page2["revisions"]) == 1
            and page["revisions"][0]["sourceRevision"] != page2["revisions"][0]["sourceRevision"],
            "history cursor visits distinct immutable revisions")
        self.check(self.package_snapshot("m07-retained-A", draft, source_a) == archive_a
            and self.package_snapshot("m07-retained-B", draft, source_b) == archive_b,
            "historical A/B complete archive entries stay byteexact after all author mutations")
        draft = self.adopt("m07-uncompiled-package-C", draft, source_c, status=422, failure="AuthorSourceCompilationRequired")
        self.stage = "cleanup"
        self.request("m07-draft-delete", "DELETE", self.path(draft), value={"expectedDraftRevision": draft["draftRevision"],
            "metadata": self.metadata("m07-draft-delete")})
        self.request("m07-draft-deleted", "GET", self.path(draft), status=404, code="resourceNotFound")
        self.active_draft = None
        self.require_workers_drained("m07-all-workers")


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
    probe = M07AuthorProbe(output, args.dist.resolve(), icd)
    save_json(output / "environment.json", {"defaultBackend": "cpu", "icd": icd, "scenarioBackend": "gpu"})
    failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
        save_json(output / "failure.json", {"stage": probe.stage, "errorType": type(error).__name__,
            "detail": probe.safe_text(error), "lastRequest": probe.last_request,
            "retainedDraftId": probe.active_draft["draftId"] if probe.active_draft else None})
    finally:
        try:
            probe.stop("m07-final")
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
        "authorFrames": probe.author_frames, "credentialScannedFiles": scanned,
        "workerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.worker_identities)],
        "scope": "Finite Linux SDK2 author HTTP/CAS/history and actual hardware-Vulkan PNG acceptance. "
            "No browser/display, full author validation, library reopen, shared-texture, platform or performance acceptance."}
    save_json(output / "summary.json", summary)
    save_json(output / "evidence-manifest.json", {"files": {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(output.rglob("*")) if path.is_file()}})
    print(json.dumps({key: summary[key] for key in ("status", "httpRequests", "assertions", "errorType")}))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
