#!/usr/bin/env python3
"""Finite W8 acceptance against matched self-contained Server/Worker, with restart."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
from urllib.parse import urlencode
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("w8_completion", ROOT / "tools/backend-api-completion-probe/probe.py")
completion = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(completion)
save_json = completion.save_json


class W8Probe(completion.CompletionProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist, 180)

    def register(self, *args, **kwargs):
        result = super().register(*args, **kwargs)
        self.secrets.extend((result["accessToken"], result["resumeToken"]))
        return result

    def sample(self, name):
        directory = ROOT / "samples/ScriptEffectsV2" / name
        manifest = json.loads((directory / "manifest.json").read_text())
        files = {entry["path"]: (directory / entry["path"]).read_bytes() for entry in manifest["files"]}
        for entry in manifest["files"]:
            entry["sha256"] = hashlib.sha256(files[entry["path"]]).hexdigest()
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False))
            for path, data in files.items():
                archive.writestr(path, data)
        asset = self.upload(name + "-upload", name + ".mkfx", "application/octet-stream", output.getvalue())
        draft = self.request(name + "-create", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": manifest["exports"][0]["id"], "name": name})
        return draft

    @staticmethod
    def path(draft):
        return "/api/v1/drafts/" + draft["draftId"]

    def read(self, name, draft):
        return self.request(name, "GET", self.path(draft))

    def files(self, name, draft, source=None):
        return self.request(name, "GET", self.path(draft) + "/package-files?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source or draft["currentSourceRevision"]}))

    def history(self, name, draft, **extra):
        return self.request(name, "GET", self.path(draft) + "/source-revisions?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], **extra}))

    def save_files(self, name, draft, snapshot, *, text_files=None, manifest=None, **extra):
        return self.request(name, "PUT", self.path(draft) + "/package-files", value={
            "expectedDraftRevision": draft["draftRevision"], "manifestJson": manifest or snapshot["manifestJson"],
            "textFiles": text_files if text_files is not None else [
                {"path": file["path"], "text": file["text"]} for file in snapshot["files"] if file["editable"]],
            "metadata": self.metadata(name), **extra})

    def compilation(self, name, draft, asynchronous=False):
        response = self.request(name, "POST", self.path(draft) + "/compilation-jobs",
            status=202 if asynchronous else 200, value={"expectedDraftRevision": draft["draftRevision"],
                "executionMode": "async" if asynchronous else "sync"})
        if asynchronous:
            self.long_job(name, response)
            response = self.request(name + "-result", "GET", "/api/v1/jobs/" + response["jobId"] + "/compilation-result")
        self.check(response["succeeded"] and response["sourceRevision"] == draft["currentSourceRevision"], name + ": fixed source compiled")
        return response

    def adopt(self, name, draft, source):
        response = self.request(name, "POST", self.path(draft) + "/adopt-source-revision", value={
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source,
            "metadata": self.metadata(name)})
        self.check(response["adoptedSourceRevision"] == source, name + ": adopted exact source")
        return self.read(name + "-draft", draft)

    def download(self, name, draft, source=None):
        headers, data, _ = self.fetch(name, self.path(draft) + "/package?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source or draft["configuration"]["sourceRevision"]}))
        self.check(headers.get("Content-Type", "").startswith("application/octet-stream"), name + ": package MIME")
        self.check(".mkfx" in headers.get("Content-Disposition", ""), name + ": download filename")
        return data

    def inspect_download(self, name, data, source):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            self.check(all(hashlib.sha256(archive.read(entry["path"])).hexdigest() == entry["sha256"]
                for entry in manifest["files"]), name + ": full archive hashes match manifest")
        asset = self.upload(name + "-upload", name + ".mkfx", "application/octet-stream", data)
        inspection = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect", value={
            "packageAssetId": asset["assetId"], "expectedPackageRevision": source})
        self.check(inspection["structureValid"] and inspection["packageRevision"] == source, name + ": reimport fixed revision")
        return inspection

    def preview_author(self, name, draft):
        options = draft["sampleOptions"]
        sample = next(sample for sample in options["samples"] if sample["sampleId"] == options["defaultSampleId"])
        selection = {"sampleId": sample["sampleId"], "targetKey": sample["targets"][0]["key"]}
        if options["materialSlots"]:
            selection["materialSlot"] = options["materialSlots"][0]
        preview = self.request(name + "-create", "POST", "/api/v1/preview-sessions", status=201, value={
            "source": {"kind": "authorDraft", "sourceId": draft["draftId"], "sourceRevision": draft["draftRevision"],
                "authorSourceRevision": draft["configuration"]["sourceRevision"], "templateSample": selection},
            "previewKind": "authorDraft", "committable": False,
            "size": {"width": 320, "height": 180}, "alphaMode": "opaque", "purpose": "preview", "renderBackend": "gpu"})
        try:
            self.frame(name + "-frame", preview, "3000000")
        finally:
            self.close_preview(name, preview)

    def template_error(self, configuration):
        templates = "/api/v1/template-libraries"
        builtin = self.request("template-builtin", "GET", templates + "/builtin/templates")
        base = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request("template-builtin-detail", "GET", templates + "/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": base["templateId"]}))
        definition = copy.deepcopy(detail["definition"])
        definition.update(name="W8 duplicate package", kind="line", properties=["opacity"])
        reference = configuration["package"]["reference"]
        config = configuration["configuration"]
        definition["packages"] = [reference, copy.deepcopy(reference)]
        definition["effectRecipes"] = [{"recipeId": "w8-effect", "name": "W8 effect", "target": config["relativeTarget"],
            "packageId": config["packageId"], "revision": config["sourceRevision"], "exportId": config["exportId"],
            "parametersJson": config["parametersJson"], "overlayLayer": config["overlayLayer"], "enabled": True,
            "readScope": config["readScope"], "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"),
            "configurationId": None}]
        personal = self.request("template-personal-before", "GET", templates + "/personal/templates")
        error = self.request("template-duplicate-rejected", "POST", templates + "/personal/commands", status=422, value={
            "expectedRevision": personal["revision"], "metadata": self.metadata("template-duplicate"),
            "command": {"type": "create", "definitionFormat": "full", "definition": definition}})
        self.check(error.get("field", "").startswith("definition.packages") and bool(error.get("failureCode")),
            "duplicate template reference has specific field and failureCode")
        unchanged = self.request("template-personal-after", "GET", templates + "/personal/templates")
        self.check(unchanged["revision"] == personal["revision"], "template rejection preserves library revision")

    def run(self):
        deployment = json.loads((self.dist / "deployment.json").read_text())
        self.deployment = {key: deployment.get(key) for key in ("version", "sourceCommit", "sourceDirty", "rid", "selfContained")}
        self.start("first")
        self.connect("first")
        self.check(self.current_ready["version"] == deployment["version"], "matched deployment version")
        original = self.sample("WordEmphasis")
        self.check(original["canCompile"], "C# draft advertises compilation")
        self.compilation("initial-compile", original)
        sources = self.request("initial-sources", "GET", self.path(original) + "/sources?" + urlencode({
            "expectedDraftRevision": original["draftRevision"], "sourceRevision": original["currentSourceRevision"]}))
        noop = self.request("old-source-noop", "POST", self.path(original) + "/source-revision-jobs", value={
            "expectedDraftRevision": original["draftRevision"], "files": sources["files"]})
        self.check(noop["draftRevision"] == original["draftRevision"], "old source no-op keeps exact draft")
        self.check(self.read("after-source-noop", original)["lastCompilation"]["succeeded"], "source no-op retains compilation")
        initial_history = self.history("initial-history", original)
        self.check(len(initial_history["revisions"]) == 1 and initial_history["revisions"][0].get("savedAtUtc")
            and initial_history["revisions"][0].get("compilation", {}).get("succeeded"), "initial history has saved time and compilation")

        snapshot = self.files("initial-package-files", original)
        manifest = json.loads(snapshot["manifestJson"])
        manifest["name"] = "W8 edited package"
        manifest["version"] = "1.0.2"
        helper = {"path": "src/W8Helper.cs", "text": "internal static class W8Helper { public const int Value = 1; }"}
        manifest["files"].append({"path": helper["path"], "role": "csharp", "sha256": "0" * 64})
        texts = [{"path": f["path"], "text": f["text"]} for f in snapshot["files"] if f["editable"]] + [helper]
        edited = self.save_files("add-csharp-and-manifest", original, snapshot, manifest=json.dumps(manifest), text_files=texts)
        self.check(edited["currentSourceRevision"] != original["currentSourceRevision"], "package edit creates new revision")
        self.compilation("edited-async-compile", edited, True)
        after = self.files("edited-package-files", edited)
        no_change = self.save_files("package-noop", edited, after)
        self.check(no_change["draftRevision"] == edited["draftRevision"] and no_change["lastCompilation"]["succeeded"],
            "package no-op keeps revision and compilation")
        page = self.history("history-page-one", edited, limit=1)
        page2 = self.history("history-page-two", edited, limit=1, afterSourceRevision=page["nextAfterSourceRevision"])
        self.check(len(page["revisions"]) == len(page2["revisions"]) == 1 and "nextAfterSourceRevision" not in page2,
            "history stable bounded paging")
        self.check({page["revisions"][0]["sourceRevision"], page2["revisions"][0]["sourceRevision"]}
            == {original["currentSourceRevision"], edited["currentSourceRevision"]}, "history preserves both immutable revisions")
        self.request("stale-history", "GET", self.path(edited) + "/source-revisions?expectedDraftRevision=" + original["draftRevision"],
            status=409, code="revisionConflict")
        self.request("unknown-query", "GET", self.path(edited) + "/source-revisions?" + urlencode({
            "expectedDraftRevision": edited["draftRevision"], "path": "../"}), status=400, code="invalidRequest")
        self.request("duplicate-query", "GET", self.path(edited) + "/package-files?" + urlencode({
            "expectedDraftRevision": edited["draftRevision"], "sourceRevision": edited["currentSourceRevision"]})
            + "&sourceRevision=" + original["currentSourceRevision"], status=400, code="invalidRequest")

        self.request("unknown-history-cursor", "GET", self.path(edited) + "/source-revisions?" + urlencode({
            "expectedDraftRevision": edited["draftRevision"], "afterSourceRevision": "sha256:" + "0" * 64}),
            status=404, code="resourceNotFound")
        self.request("package-edit-missing-text", "PUT", self.path(edited) + "/package-files", status=400, code="invalidRequest", value={
            "expectedDraftRevision": edited["draftRevision"], "manifestJson": after["manifestJson"], "textFiles": [],
            "metadata": self.metadata("package-edit-missing-text")})
        self.check(self.read("package-rejection-unchanged", edited)["draftRevision"] == edited["draftRevision"],
            "rejected package edit keeps draft revision")
        manifest = json.loads(after["manifestJson"])
        manifest["files"] = [entry for entry in manifest["files"] if entry["path"] != helper["path"]]
        old_path = sources["files"][0]["path"]
        new_path = "src/RenamedEffect.cs"
        for entry in manifest["files"]:
            if entry["path"] == old_path:
                entry["path"] = new_path
        texts = [{"path": new_path if f["path"] == old_path else f["path"], "text": f["text"]}
            for f in after["files"] if f["editable"] and f["path"] != helper["path"]]
        edited = self.save_files("remove-and-rename-csharp", edited, after, manifest=json.dumps(manifest), text_files=texts)
        self.compilation("renamed-compile", edited)
        edited = self.adopt("renamed-adopt", edited, edited["currentSourceRevision"])
        self.preview_author("edited-controller-preview", edited)
        data = self.download("edited-download", edited)
        self.inspect_download("edited-roundtrip", data, edited["currentSourceRevision"])
        original_bytes = self.download("original-download", edited, original["currentSourceRevision"])
        self.inspect_download("original-roundtrip", original_bytes, original["currentSourceRevision"])

        for name in ("CustomInk", "LineGlitch"):
            material = self.sample(name)
            self.check(not material["canCompile"], name + ": non-C# draft advertises no compilation")
            package = self.files(name + "-files", material)
            text_files = [{"path": f["path"], "text": f["text"] + "\n"} for f in package["files"] if f["editable"]]
            material = self.save_files(name + "-text-edit", material, package, text_files=text_files)
            material = self.adopt(name + "-adopt", material, material["currentSourceRevision"])
            self.check(not material["canCompile"], name + ": edited graph/shader remains non-C#")
            self.preview_author(name + "-preview", material)

        configuration = {key: edited["configuration"].get(key) for key in (
            "name", "exportId", "parametersJson", "relativeTarget", "readScope", "branchInput", "fixedSeed", "overlayLayer")}
        configuration["source"] = {"draftId": edited["draftId"], "expectedDraftRevision": edited["draftRevision"],
            "sourceRevision": edited["configuration"]["sourceRevision"]}
        library = self.request("configuration-library", "GET", "/api/v1/script-configurations")
        created = self.request("configuration-create", "POST", "/api/v1/script-configurations/commands", value={
            "expectedRevision": library["revision"], "metadata": self.metadata("configuration-create"),
            "command": {"type": "create", "configuration": configuration}})
        configuration_path = "/api/v1/script-configurations/" + created["configurationId"]
        detail = self.request("configuration-detail", "GET", configuration_path + "?expectedRevision=" + created["revision"])
        _, saved_bytes, _ = self.fetch("configuration-download", configuration_path + "/package?expectedRevision=" + created["revision"])
        self.inspect_download("configuration-roundtrip", saved_bytes, detail["configuration"]["sourceRevision"])
        self.template_error(detail)

        owner = self.client
        other = self.register("second-tab", select=False)
        self.request("ordinary-owner-denied", "GET", self.path(edited), client=other["clientId"], status=401, code="resourceUnauthorized")
        profile = self.request("profile-active", "GET", "/api/v1/profile/author-drafts", client=other["clientId"])
        candidate = next(item for item in profile["drafts"] if item["draftId"] == edited["draftId"])
        self.check(candidate["ownershipState"] == "active" and not candidate["canClaim"], "active owner cannot be stolen")
        self.request("active-claim-rejected", "POST", "/api/v1/profile/author-drafts/" + edited["draftId"] + "/claim",
            client=other["clientId"], status=409, value={"expectedDraftRevision": candidate["draftRevision"],
                "expectedOwnershipRevision": candidate["ownershipRevision"]})
        before_restart = self.history("before-restart-history", edited)
        self.stop("first")
        self.start("restart")
        self.connect("restart")
        self.check(self.client != owner, "restart creates new authenticated client")
        profile = self.request("profile-after-restart", "GET", "/api/v1/profile/author-drafts")
        candidate = next(item for item in profile["drafts"] if item["draftId"] == edited["draftId"])
        self.check(candidate["ownershipState"] == "orphaned" and candidate["canClaim"], "restart discovers persistent orphan")
        claim_path = "/api/v1/profile/author-drafts/" + edited["draftId"] + "/claim"
        claim_input = {"expectedDraftRevision": candidate["draftRevision"], "expectedOwnershipRevision": candidate["ownershipRevision"],
            "metadata": self.metadata("claim-after-restart")}
        self.request("claim-unknown-query", "POST", claim_path + "?unknown=1", status=400, code="invalidRequest", value=claim_input)
        self.request("claim-duplicate-query", "POST", claim_path + "?unknown=1&unknown=2", status=400, code="invalidRequest", value=claim_input)
        claimed = self.request("claim-after-restart", "POST", claim_path, value=claim_input)
        replay = self.request("claim-receipt-replay", "POST", claim_path, value=claim_input)
        self.check(claimed == replay, "claim receipt replays exact result")
        self.check(claimed["ownershipRevision"] != candidate["ownershipRevision"], "claim changes independent ownership revision")
        recovered = claimed["draft"]
        self.check(recovered["draftRevision"] == edited["draftRevision"] and recovered["configuration"] == edited["configuration"],
            "claim preserves content CAS and configuration")
        self.request("stale-ownership", "POST", claim_path, status=409, value={**claim_input, "metadata": self.metadata("stale-ownership")})
        history = self.history("after-restart-history", recovered)
        self.check(history == before_restart, "restart retains all revision times and compilation summaries")
        self.inspect_download("recovered-roundtrip", self.download("recovered-download", recovered), recovered["configuration"]["sourceRevision"])
        self.preview_author("recovered-preview", recovered)
        deleted = self.request("recovered-delete", "DELETE", self.path(recovered), value={
            "expectedDraftRevision": recovered["draftRevision"], "metadata": self.metadata("recovered-delete")})
        self.check(deleted["draftId"] == recovered["draftId"], "claimed draft can be explicitly deleted")
        self.request("deleted-draft", "GET", self.path(recovered), status=404, code="resourceNotFound")
        _, retained, _ = self.fetch("configuration-after-delete", configuration_path + "/package?expectedRevision=" + created["revision"])
        self.inspect_download("configuration-after-restart-roundtrip", retained, detail["configuration"]["sourceRevision"])
        self.flows.append({"name": "w8-authoring", "scope": "C# and graph/shader editing, compilation/no-op, history, downloads/reimport, detailed template error, owner isolation, restart claim and explicit deletion; GPU Worker frames."})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    probe = W8Probe(output, args.dist.resolve())
    failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
        save_json(output / "failure.json", {"errorType": type(error).__name__, "detail": str(error)})
    finally:
        try:
            probe.stop("final")
        except BaseException as error:
            failure = failure or error
            save_json(output / "shutdown-failure.json", {"errorType": type(error).__name__, "detail": str(error)})
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        scanned = None
    summary = {"status": "failed" if failure else "passed", "errorType": type(failure).__name__ if failure else None,
        "httpRequests": probe.request_count, "assertions": len(probe.assertions), "credentialScannedFiles": scanned,
        "deployment": probe.deployment, "deploymentManifestSha256": hashlib.sha256((probe.dist / "deployment.json").read_bytes()).hexdigest(),
        "flows": probe.flows, "jobTerminals": probe.terminals, "runs": probe.runs,
        "workerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.worker_identities)],
        "assertionLabels": probe.assertions, "scope": "Finite Linux GPU backend HTTP acceptance. Browser, other platforms and performance were not tested."}
    save_json(output / "summary.json", summary)
    save_json(output / "evidence-manifest.json", {"files": {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sorted(output.rglob("*")) if p.is_file()}})
    print(json.dumps({key: summary[key] for key in ("status", "httpRequests", "assertions", "errorType")}))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
