#!/usr/bin/env python3
"""Finite HTTP acceptance for exact author inspection and atomic adoption.

This module is intentionally separate from M09's SVG/Warp/export run. Importing
it does not start a host; only main() invokes the explicit fresh HTTP workflow.
"""
import argparse
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlencode

sys.dont_write_bytecode = True
REPO = next(path for path in Path(__file__).resolve().parents
    if (path / "backend/tools/backend-script-render-upgrade/m09_author_probe.py").is_file())
M09_PATH = REPO / "backend/tools/backend-script-render-upgrade/m09_author_probe.py"
SPEC = importlib.util.spec_from_file_location("author_metadata_m09", M09_PATH)
m09 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m09)
selection = m09.selection
save_json = m09.save_json
sha = lambda value: hashlib.sha256(value).hexdigest()
CONFIG_FIELDS = ("name", "exportId", "parametersJson", "relativeTarget", "readScope",
    "branchInput", "fixedSeed", "overlayLayer")
INSPECTION_FIELDS = {"bindingKinds", "diagnostics", "draftId", "draftRevision", "exports", "files", "inputs",
    "name", "packageId", "packageRevision", "packageVersion", "parameters", "protocolVersion", "sdkVersion",
    "shaderProfile", "sourceRevision", "structureValid", "version"}
SOURCE_PATH = "src/MetadataEffect.cs"
COUNTER = 9007199254740993
ABSENT = object()


def compact(value):
    # Python integers retain the exact Int64 JSON text; never route via float.
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def source(migrated=False):
    marker_type = "long" if migrated else "double"
    lookup = 'GetInteger("counter")' if migrated else 'GetNumber("peak")'
    rgb = "32 / 255d, 96 / 255d, 224 / 255d" if migrated else "224 / 255d, 64 / 255d, 32 / 255d"
    # DrawDots and its closed declaration are the production VisualOutput API.
    # A large interior disk is independent of fonts, timing, particles, SVG and
    # material exports; the red/blue difference is literally in retained source.
    return f'''using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
public sealed record MetadataData({marker_type} Marker) : PreparedEffect;
public sealed class MetadataEffect : IPureKaraokeEffect, IPureVisualEffect
{{
    public PreparedEffect Declare(DeclareContext input) => new MetadataData(input.Parameters.{lookup})
    {{ VisualOutputs = [new(new("marker"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] }};
    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) {{ }}
    public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
    {{
        output.DrawDots(new("marker"), [new(new(frame.CanvasSize.X / 4, frame.CanvasSize.Y / 4),
            frame.CanvasSize.Y / 10, new({rgb}, 1))], OverlayLayer.AboveText);
    }}
}}
'''


def fixture(migrated=False):
    parameter = {"id": "counter", "type": "integer", "label": "Counter", "default": COUNTER,
        "min": -9223372036854775808, "max": 9223372036854775807} if migrated else {
        "id": "peak", "type": "number", "label": "Peak", "default": 1, "min": 0, "max": 4}
    manifest = {**m09.profile(), "packageId": "mako.author-metadata.http", "name": "Author metadata source fixture",
        "version": "2.0.1" if migrated else "2.0.0", "requires": ["visuals.dots"],
        "lineExit": {"durationUs": 0}, "emitters": [], "parameters": [parameter],
        "exports": [{"id": "migrated" if migrated else "original", "kind": "controller",
            "name": "Migrated" if migrated else "Original", "entryType": "MetadataEffect",
            "parameters": [parameter["id"]], "bindingKinds": ["project", "tokens"],
            "outputs": [{"id": "marker", "kind": "Overlay", "target": "canvas", "requires": ["visuals.dots"],
                "dependsOn": []}]}]}
    return manifest, {SOURCE_PATH: source(migrated).encode()}


class MetadataProbe(m09.M09AuthorProbe):
    """Reuse matched launcher, isolation, redaction, PNG and owned process drain."""

    def __init__(self, output, dist, icd):
        super().__init__(output, dist, icd)
        self.metadata_run = {}
        self.inspections = []
        self.pixel_controls = []

    def request(self, name, method, path, **kwargs):
        # Keep every credential, including peer credentials, only in memory for
        # inherited safe_text/redaction and the final evidence secret scan.
        result = super().request(name, method, path, **kwargs)
        if isinstance(result, dict) and result.get("accessToken"):
            self.secrets.extend(filter(None, (result.get("accessToken"), result.get("resumeToken"))))
        return result

    def response_headers(self, name):
        record = json.loads((self.output / (f"{self.request_count:03d}-{name}.response.json")).read_text())
        return {key.lower(): value for key, value in record["headers"].items()}

    def snapshot(self, label, draft):
        current = self.read(label + "-draft", draft)
        self.check(current == draft, label + ": exact complete starting draft")
        return {"draft": copy.deepcopy(current), "history": self.history(label + "-history", current)}

    def unchanged(self, label, before):
        current = self.read(label + "-draft", before["draft"])
        self.check(current == before["draft"], label + ": complete draft, configuration, sample and latest report unchanged")
        history = self.history(label + "-history", current)
        self.check(history == before["history"], label + ": complete retained source history unchanged")
        return current

    def inspect(self, label, draft, revision, *, status=200, code=None, client="default", no_store=True,
            query=None, value=None):
        query = urlencode({"expectedDraftRevision": draft["draftRevision"], "sourceRevision": revision}) if query is None else query
        path = self.path(draft) + "/source-inspection" + ("?" + query if query else "")
        result = self.request(label, "GET", path, status=status, code=code, client=client, value=value)
        if no_store:
            self.check("no-store" in self.response_headers(label).get("cache-control", "").lower(),
                label + ": exact source inspection is no-store")
        if status != 200:
            return result
        self.check(set(result) == INSPECTION_FIELDS, label + ": exact narrow declaration response shape")
        self.check(result["draftId"] == draft["draftId"] and result["draftRevision"] == draft["draftRevision"]
            and result["packageId"] == draft["packageId"] and result["sourceRevision"] == result["packageRevision"] == revision,
            label + ": complete owner draft CAS and retained source/package identity")
        self.check(result["structureValid"] and result["packageVersion"] == 3 and result["sdkVersion"].split(".")[0] == "2",
            label + ": exact retained SDK2 declarations can be inspected without compiling")
        files = self.files(label + "-package-files", draft, revision)
        self.check(files["draftId"] == result["draftId"] and files["draftRevision"] == result["draftRevision"]
            and files["sourceRevision"] == files["packageRevision"] == result["sourceRevision"],
            label + ": separate declaration and package-files reads have the same full identity")
        expected_files = sorted((item["path"], item["role"], item["sizeBytes"], item["sha256"]) for item in files["files"])
        actual_files = sorted((item["path"], item["role"], item["size"], item["sha256"]) for item in result["files"])
        self.check(all(set(item) == {"path", "role", "size", "sha256"} for item in result["files"])
            and actual_files == expected_files, label + ": file summary uses size and exact retained hashes")
        encoded = compact(result)
        self.check(all(text not in encoded for text in ("public sealed", "using MakoKaraGenerator", str(REPO), str(self.output))),
            label + ": declaration response contains no source text or local workspace/profile path")
        self.inspections.append({"label": label, "response": result})
        save_json(self.output / "source-inspections.json", self.inspections)
        return result

    @staticmethod
    def full_configuration(draft):
        return {key: copy.deepcopy(draft["configuration"].get(key)) for key in CONFIG_FIELDS}

    def adopt_request(self, label, draft, revision, configuration=ABSENT):
        request = {"expectedDraftRevision": draft["draftRevision"], "sourceRevision": revision,
            "metadata": self.metadata(label, seconds=600)}
        if configuration is not ABSENT:
            request["configuration"] = copy.deepcopy(configuration)
        return request

    def submit_adopt(self, label, draft, request, *, status=200, code=None, raw=None):
        kwargs = {"value": request} if raw is None else {"raw": raw.encode(), "content_type": "application/json"}
        return self.request(label, "POST", self.path(draft) + "/adopt-source-revision", status=status, code=code, **kwargs)

    def accepted_adopt(self, label, draft, revision, configuration=ABSENT):
        request = self.adopt_request(label, draft, revision, configuration)
        response = self.submit_adopt(label, draft, request)
        result = self.refreshed(label + "-result", draft)
        self.check(response["draftId"] == draft["draftId"] and response["packageId"] == draft["packageId"]
            and response["draftRevision"] == result["draftRevision"] and response["exportId"] == result["exportId"]
            and response["previousSourceRevision"] == draft["configuration"]["sourceRevision"]
            and response["adoptedSourceRevision"] == result["adoptedSourceRevision"] == result["configuration"]["sourceRevision"] == revision,
            label + ": one accepted response publishes exact adopted source and resulting CAS/export")
        expected = self.full_configuration(draft) if configuration in (ABSENT, None) else configuration
        self.check(self.full_configuration(result) == expected and result["name"] == expected["name"]
            and result["exportId"] == expected["exportId"], label + ": all eight fields, draft name and export published together")
        self.check(result["currentSourceRevision"] == draft["currentSourceRevision"]
            and result.get("lastCompilation") == draft.get("lastCompilation"),
            label + ": latest saved source and latest compilation keep their actual identity")
        return result, request, response

    def reject_adopt(self, label, draft, revision, configuration, *, status, code, field=None, failure=None, raw=None):
        before = self.snapshot(label + "-before", draft)
        request = self.adopt_request(label, draft, revision, configuration)
        error = self.submit_adopt(label, draft, request, status=status, code=code, raw=raw)
        if field is not None:
            self.check(error.get("field") == field, label + ": exact invalid configuration field")
        if failure is not None:
            self.check(error.get("failureCode") == failure, label + ": exact rejection failureCode")
        self.unchanged(label + "-unchanged", before)
        return error

    def save_revision(self, label, draft, manifest, files):
        current = self.files(label + "-before-files", draft)
        manifest, _ = m09.archive(manifest, files)
        result = self.record(label, self.save_files(label, draft, current, manifest=compact(manifest),
            text_files=[{"path": path, "text": data.decode()} for path, data in sorted(files.items())]))
        self.check(result["currentSourceRevision"] != draft["currentSourceRevision"], label + ": actual new immutable source")
        self.assert_in_use(label, draft, result)
        return result

    def inspection_boundaries(self, draft, stale, source_a, source_b):
        self.stage = "source-inspection-boundaries"
        foreign_manifest, foreign_files = fixture()
        foreign_files[SOURCE_PATH] += b"\n// belongs only to the other draft\n"
        foreign = self.import_fixture("metadata-foreign", foreign_manifest, foreign_files)
        before = self.snapshot("inspection-read-only-before", draft)
        original = self.inspect("inspection-retained-A", draft, source_a)
        migrated = self.inspect("inspection-uncompiled-B", draft, source_b)
        self.check([item["id"] for item in original["parameters"]] == ["peak"]
            and [item["id"] for item in original["exports"]] == ["original"]
            and [item["id"] for item in migrated["parameters"]] == ["counter"]
            and [item["id"] for item in migrated["exports"]] == ["migrated"],
            "same draft exact retained A/B inspections expose different original and migrated schemas")
        parameter = migrated["parameters"][0]
        self.check((parameter["defaultValueJson"], parameter["minimumJson"], parameter["maximumJson"])
            == (str(COUNTER), "-9223372036854775808", "9223372036854775807"),
            "Int64 default and complete signed bounds remain original JSON text strings")
        self.check(next(item for item in before["history"]["revisions"] if item["sourceRevision"] == source_b)
            .get("compilation") is None, "new-schema B inspected while no B compile report exists")
        exact = urlencode({"expectedDraftRevision": draft["draftRevision"], "sourceRevision": source_b})
        invalid_queries = ["", urlencode({"sourceRevision": source_b}),
            urlencode({"expectedDraftRevision": draft["draftRevision"]}),
            exact + "&sourceRevision=" + source_b, exact + "&expectedDraftRevision=" + draft["draftRevision"],
            exact + "&unexpected=true", urlencode({"expectedDraftRevision": " ", "sourceRevision": source_b}),
            urlencode({"expectedDraftRevision": draft["draftRevision"], "sourceRevision": " "}),
            urlencode({"expectedDraftRevision": "latest", "sourceRevision": source_b}),
            urlencode({"expectedDraftRevision": draft["draftRevision"], "sourceRevision": "latest"})]
        for index, query in enumerate(invalid_queries):
            self.inspect("inspection-query-" + str(index), draft, source_b, query=query, status=400, code="invalidRequest")
        body = self.inspect("inspection-get-body", draft, source_b, value={}, status=400, code="invalidRequest")
        self.check(body.get("field") == "body", "source inspection rejects even an empty GET JSON body")
        self.inspect("inspection-anonymous", draft, source_b, client=None, status=401,
            code="unauthorizedClient")
        peer = self.register("inspection-peer-register", select=False)
        self.inspect("inspection-peer-owner", draft, source_b, client=peer["clientId"], status=401, code="resourceUnauthorized")
        self.inspect("inspection-stale-CAS", stale, source_a, status=409, code="revisionConflict")
        for label, revision in (("unknown", "sha256:" + "e" * 64), ("foreign", foreign["currentSourceRevision"])):
            error = self.inspect("inspection-" + label, draft, revision, status=404, code="resourceNotFound")
            self.check(error.get("field") == "sourceRevision", "inspection " + label + ": retained membership field")
        self.unchanged("inspection-read-only-after", before)
        return foreign

    def invalid_configurations(self, draft, source_b, valid):
        self.stage = "complete-configuration-rejections"
        invalid = [
            ("blank-name", {"name": " "}, 400, "invalidRequest", "name"),
            ("long-name", {"name": "n" * 257}, 400, "invalidRequest", "name"),
            ("blank-export", {"exportId": " "}, 400, "invalidRequest", "exportId"),
            ("missing-export", {"exportId": "missing"}, 422, "commandRejected", "exportId"),
            ("broken-json", {"parametersJson": "{broken"}, 400, "invalidRequest", "parametersJson"),
            ("array-json", {"parametersJson": "[]"}, 422, "commandRejected", "parametersJson"),
            ("null-parameters", {"parametersJson": None}, 400, "invalidRequest", "parametersJson"),
            ("wrong-integer", {"parametersJson": '{"counter":"bad"}'}, 422, "commandRejected", "parametersJson"),
            ("overflow-integer", {"parametersJson": '{"counter":9223372036854775808}'}, 422, "commandRejected", "parametersJson"),
            ("obsolete-parameter", {"parametersJson": '{"peak":2}'}, 422, "commandRejected", "parametersJson"),
            ("duplicate-parameter", {"parametersJson": '{"counter":1,"counter":2}'}, 422, "commandRejected", "parametersJson"),
            ("invalid-target", {"relativeTarget": 99}, 400, "invalidRequest", "relativeTarget"),
            ("invalid-scope", {"readScope": 99}, 400, "invalidRequest", "readScope"),
            ("missing-branch", {"readScope": "branchInput", "branchInput": None}, 400, "invalidRequest", "branchInput"),
            ("unexpected-branch", {"branchInput": "unexpected"}, 400, "invalidRequest", "branchInput"),
            ("seed-overflow", {"fixedSeed": "18446744073709551616"}, 400, "invalidRequest", "fixedSeed"),
            ("seed-empty", {"fixedSeed": ""}, 400, "invalidRequest", "fixedSeed"),
            ("seed-sign", {"fixedSeed": "+1"}, 400, "invalidRequest", "fixedSeed"),
            ("invalid-layer", {"overlayLayer": 99}, 400, "invalidRequest", "overlayLayer"),
        ]
        for label, changes, status, code, field in invalid:
            self.reject_adopt("config-" + label, draft, source_b, {**valid, **changes}, status=status, code=code,
                field="configuration." + field)
        # Serializer-level shape failures do not promise a field from the
        # framework JsonException path; transport must still reject atomically.
        for label, changes in (("seed-number", {"fixedSeed": 1}),
                ("parameters-object", {"parametersJson": {"counter": COUNTER}}),
                ("target-name", {"relativeTarget": "missing"}), ("scope-name", {"readScope": "missing"}),
                ("layer-name", {"overlayLayer": "missing"})):
            self.reject_adopt("config-type-" + label, draft, source_b, {**valid, **changes}, status=400, code="invalidRequest")
        for field in CONFIG_FIELDS:
            config = {key: value for key, value in valid.items() if key != field}
            self.reject_adopt("config-missing-" + field, draft, source_b, config, status=400, code="invalidRequest",
                field="configuration." + field)
        for field in ("packageId", "sourceRevision", "kind", "bindingKinds"):
            self.reject_adopt("config-forbidden-" + field, draft, source_b, {**valid, field: "must-not-be-accepted"},
                status=400, code="invalidRequest")
        for label, config in (("array", []), ("string", "invalid"), ("number", 1)):
            self.reject_adopt("config-shape-" + label, draft, source_b, config, status=400, code="invalidRequest", field="configuration")
        # Raw bytes are required: a normal dict serializer would erase duplicate
        # keys and turn these into different tests. Raw bodies are hash-only in
        # the inherited evidence writer and never contain credentials.
        for field in CONFIG_FIELDS:
            request = self.adopt_request("config-duplicate-" + field, draft, source_b, valid)
            config_json = compact(valid)
            duplicate = config_json[:-1] + "," + compact(field) + ":" + compact(valid[field]) + "}"
            raw = compact({key: value for key, value in request.items() if key != "configuration"})[:-1] + ',"configuration":' + duplicate + "}"
            self.reject_adopt("config-duplicate-" + field, draft, source_b, valid, raw=raw,
                status=400, code="invalidRequest", field="configuration." + field)
        request = self.adopt_request("config-duplicate-root", draft, source_b, valid)
        raw = compact(request)[:-1] + ',"configuration":null}'
        self.reject_adopt("config-duplicate-root", draft, source_b, valid, raw=raw,
            status=400, code="invalidRequest", field="configuration")

    def pixel(self, label, preview, sequence, expected):
        rgba = self.transparent_frame(label, preview, 3000000, sequence)
        # Dot center is 80,45 and radius18 at320x180. This 12x12 block is
        # strictly inside the authored disk and away from the antialias edge.
        block = [rgba[(y * 320 + x) * 4:(y * 320 + x) * 4 + 4]
            for y in range(39, 51) for x in range(74, 86)]
        self.check(all(abs(pixel[channel] - expected[channel]) <= 2 for pixel in block for channel in range(4)),
            label + ": every interior pixel matches literal adopted-source RGBA within2")
        self.pixel_controls.append({"label": label, "rgbaSha256": sha(rgba), "expectedRgba": list(expected),
            "timeUs": "3000000", "block": {"x": 74, "y": 39, "width": 12, "height": 12},
            "source": copy.deepcopy(preview["source"]), "previewGeneration": preview["previewGeneration"]})
        save_json(self.output / "pixel-controls.json", self.pixel_controls)
        return rgba

    def run(self):
        deployment = selection.host.validate_dist(self.dist)
        inputs = deployment.get("sourceInputs")
        required_inputs = {"src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.AuthorInspection.cs",
            "src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.AuthorAdoptionConfiguration.cs",
            "src/MakoKaraGenerator.Backend.Server/ServerEndpoints.AuthorAdoption.cs",
            "src/MakoKaraGenerator.Backend.Contracts/BackendAuthorInspectionContracts.cs"}
        self.check(isinstance(inputs, dict) and required_inputs <= set(inputs),
            "matched deployment records integrated inspection and atomic-adoption production inputs")
        backend_root = (REPO / "backend").resolve()
        for name, expected_hash in inputs.items():
            path = (backend_root / name).resolve()
            self.check(path.is_relative_to(backend_root) and path.is_file() and sha(path.read_bytes()) == expected_hash,
                "matched deployment input equals current App backend source: " + name)
        self.deployment = {key: deployment.get(key) for key in ("version", "sourceCommit", "sourceDirty", "rid", "selfContained")}
        save_json(self.output / "deployment-reference.json", {**self.deployment, "dist": str(self.dist),
            "manifestSha256": sha((self.dist / "deployment.json").read_bytes()), "probeSha256": sha(Path(__file__).read_bytes()),
            "inheritedProbeSha256": sha(M09_PATH.read_bytes()), "matchedProductionInputCount": len(inputs)})
        self.start("author-metadata-start")
        self.connect("author-metadata")
        self.check(self.current_ready["version"] == deployment["version"], "ready version matches validated matched Server/Worker distribution")
        availability = self.request("author-metadata-backends", "GET", "/api/v1/render-backends")
        gpu = next(item for item in availability["backends"] if item["renderBackend"] == "gpu")
        self.check(gpu["available"] and gpu["api"] == "vulkan" and bool(gpu.get("device")), "fresh host offers actual Vulkan backend")

        self.stage = "original-source-and-legacy-adopt"
        draft = self.import_fixture("metadata-main", *fixture())
        source_a = draft["currentSourceRevision"]
        self.compile_source("compile-A", draft, source_a)
        draft = self.refreshed("compiled-A", draft)
        draft, _, _ = self.accepted_adopt("legacy-omitted-configuration", draft, source_a)
        legacy = copy.deepcopy(draft)
        draft, _, _ = self.accepted_adopt("legacy-null-configuration", draft, source_a, None)
        self.check(draft == legacy, "explicit configuration null preserves the complete legacy snapshot and no-op CAS")
        config_a = {"name": "HTTP original complete config", "exportId": "original", "parametersJson": '{"peak":2}',
            "relativeTarget": "application", "readScope": "boundSources", "branchInput": None,
            "fixedSeed": "7", "overlayLayer": "package"}
        draft, _, _ = self.accepted_adopt("complete-A-baseline", draft, source_a, config_a)
        old = copy.deepcopy(draft)
        draft = self.save_revision("save-B-new-schema", draft, *fixture(True))
        source_b = draft["currentSourceRevision"]
        foreign = self.inspection_boundaries(draft, old, source_a, source_b)
        config_b = {"name": "HTTP migrated complete config", "exportId": "migrated", "parametersJson": '{"counter":9007199254740993}',
            "relativeTarget": "tokenFamily", "readScope": "project", "branchInput": None,
            "fixedSeed": "18446744073709551615", "overlayLayer": "aboveText"}
        self.reject_adopt("A-success-does-not-authorize-B", draft, source_b, config_b, status=422,
            code="commandRejected", field="sourceRevision", failure="AuthorSourceCompilationRequired")
        self.stage = "retained-B-success-latest-C-failure"
        self.compile_source("compile-B-async", draft, source_b, asynchronous=True)
        draft = self.refreshed("compiled-B", draft)
        manifest_c, files_c = fixture(True)
        files_c[SOURCE_PATH] = b"public class BrokenMetadataEffect { this is invalid CSharp !!! }\n"
        draft = self.save_revision("save-C-broken", draft, manifest_c, files_c)
        source_c = draft["currentSourceRevision"]
        self.compile_source("compile-C-failed", draft, source_c, succeeded=False)
        draft = self.refreshed("compiled-C-failed", draft)
        latest = copy.deepcopy(draft.get("lastCompilation"))
        self.check(latest and latest["sourceRevision"] == source_c and latest["succeeded"] is False,
            "latest compilation is actual failed C, independent of retained successful B")
        before_inspect = self.snapshot("C-inspection-before", draft)
        self.inspect("inspection-failed-C-declarations", draft, source_c)
        self.inspect("inspection-compiled-retained-B", draft, source_b)
        self.unchanged("C-inspection-after", before_inspect)
        self.reject_adopt("failed-C-not-authorized", draft, source_c, config_b, status=422,
            code="commandRejected", field="sourceRevision", failure="AuthorSourceCompilationRequired")
        self.reject_adopt("legacy-new-schema-cannot-keep-old-export", draft, source_b, ABSENT, status=422,
            code="commandRejected", field="configuration.exportId", failure="PackageExportMissing")
        self.reject_adopt("null-new-schema-cannot-keep-old-export", draft, source_b, None, status=422,
            code="commandRejected", field="configuration.exportId", failure="PackageExportMissing")
        self.invalid_configurations(draft, source_b, config_b)
        for label, revision in (("unknown", "sha256:" + "e" * 64), ("foreign", foreign["currentSourceRevision"])):
            self.reject_adopt("adopt-" + label + "-not-retained", draft, revision, config_b, status=404,
                code="resourceNotFound", field="sourceRevision")

        self.stage = "atomic-adopt-actual-GPU-pixels"
        preview = self.create_preview("preview-A-with-latest-C-failure", draft)
        rgba_a = self.pixel("pixels-in-use-A-before-adopt", preview, 1, (224, 64, 32, 255))
        before_adopt = copy.deepcopy(draft)
        draft, receipt, original_response = self.accepted_adopt("atomic-complete-B", draft, source_b, config_b)
        self.check(draft["draftRevision"] != before_adopt["draftRevision"] and draft["currentSourceRevision"] == source_c
            and draft.get("lastCompilation") == latest, "full B adoption changes one CAS while latest failed C remains explicit")
        adopted_b = copy.deepcopy(draft)
        # Existing preview remains pinned even after the draft mutation.
        pinned = self.pixel("pixels-old-preview-still-A", preview, 2, (224, 64, 32, 255))
        self.check(pinned == rgba_a, "adopting B does not silently replace already pinned A preview pixels")
        self.refresh_input("preview-explicitly-replace-with-B", preview, draft)
        rgba_b = self.pixel("pixels-adopted-B", preview, 3, (32, 96, 224, 255))
        changed = sum(rgba_a[index:index + 4] != rgba_b[index:index + 4] for index in range(0, len(rgba_a), 4))
        self.check(changed >= 144 and sha(rgba_a) != sha(rgba_b), "same-time decoded full PNG has actual source-authored visible change after adopt")
        self.stage = "exact-source-plus-configuration-undo-redo"
        draft, _, _ = self.accepted_adopt("undo-source-and-all-eight-fields-A", draft, source_a, config_a)
        self.check(draft["configuration"] == {**old["configuration"]}, "undo restores exact complete original source/configuration DTO")
        undone = self.snapshot("undo-before-receipt-replay", draft)
        replay = self.submit_adopt("original-B-receipt-replay-after-undo", draft, receipt)
        self.check(replay == original_response, "receipt replay returns original exact B response despite stale request CAS")
        self.unchanged("receipt-replay-does-not-redo", undone)
        changed_request = copy.deepcopy(receipt)
        changed_request["configuration"]["name"] += " changed semantic payload"
        self.submit_adopt("changed-config-reuses-receipt", draft, changed_request, status=409, code="requestIdReuse")
        self.unchanged("receipt-reuse-rejection-unchanged", undone)
        stale_request = self.adopt_request("stale-B-after-undo", adopted_b, source_b, config_b)
        self.submit_adopt("stale-adopt-409", draft, stale_request, status=409, code="revisionConflict")
        self.inspect("stale-inspection-after-undo", adopted_b, source_b, status=409, code="revisionConflict")
        peer = self.register("adopt-peer-register", select=False)
        owner_request = self.adopt_request("peer-owner-rejection", draft, source_b, config_b)
        self.request("peer-adopt-owner-rejection", "POST", self.path(draft) + "/adopt-source-revision",
            value=owner_request, client=peer["clientId"], status=401, code="resourceUnauthorized")
        self.unchanged("stale-and-peer-unchanged", undone)
        self.refresh_input("preview-undo-A", preview, draft)
        self.check(self.pixel("pixels-undone-A", preview, 4, (224, 64, 32, 255)) == rgba_a,
            "undo source and exact config restore original complete same-time RGBA")
        draft, _, _ = self.accepted_adopt("redo-source-and-all-eight-fields-B", draft, source_b, config_b)
        self.check(draft["configuration"] == adopted_b["configuration"], "redo restores exact complete migrated source/configuration DTO")
        self.refresh_input("preview-redo-B", preview, draft)
        self.check(self.pixel("pixels-redone-B", preview, 5, (32, 96, 224, 255)) == rgba_b,
            "redo source and exact config restore complete B RGBA")
        before_noop = self.snapshot("full-B-noop-before", draft)
        draft, _, _ = self.accepted_adopt("full-B-same-value-noop", draft, source_b, config_b)
        self.unchanged("full-B-noop-after", before_noop)
        history = self.history("final-exact-compilation-history", draft)
        by_source = {item["sourceRevision"]: item for item in history["revisions"]}
        self.check(set(by_source) == {source_a, source_b, source_c}
            and by_source[source_a]["compilation"]["succeeded"] is True
            and by_source[source_b]["compilation"]["succeeded"] is True
            and by_source[source_c]["compilation"]["succeeded"] is False,
            "history retains exact successful A/B and failed C compilation evidence")
        self.close("author-metadata-preview", preview)
        self.require_workers_drained("author-metadata-all-workers")
        self.metadata_run = {"draft": draft, "foreignDraftId": foreign["draftId"],
            "sourceA": source_a, "sourceB": source_b, "sourceC": source_c, "configurationA": config_a,
            "configurationB": config_b, "changedPixels": changed, "lastCompilation": latest, "history": history}
        save_json(self.output / "metadata-run.json", self.redact(self.metadata_run))
        self.stage = "complete"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", type=Path, required=True, help="Matched self-contained App-local Server/Worker distribution")
    parser.add_argument("--output", type=Path, required=True, help="New private evidence directory; never a personal profile")
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    args = parser.parse_args()
    icd = selection.gpu.hardware_icd(args.icd)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    output.chmod(0o700)
    os.environ["MAKO_RENDER_BACKEND"] = "cpu"
    os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = icd["path"]
    probe = MetadataProbe(output, args.dist.resolve(), icd)
    save_json(output / "environment.json", {"defaultBackend": "cpu", "scenarioBackend": "gpu", "icd": icd,
        "scope": "exact source inspection and complete configuration adoption only"})
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
            probe.drain()
        except BaseException as error:
            failure = failure or error
            save_json(output / "shutdown-failure.json", {"errorType": type(error).__name__, "detail": probe.safe_text(error)})
    with probe.gpu_lock:
        hardware = list(probe.gpu_samples.values())
    save_json(output / "gpu-workers.json", {"observations": hardware, "assignments": probe.gpu_assignments})
    scanned = None
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        save_json(output / "scan-failure.json", {"errorType": type(error).__name__, "detail": probe.safe_text(error)})
    summary = {"status": "failed" if failure else "passed", "errorType": type(failure).__name__ if failure else None,
        "stage": probe.stage, "httpRequests": probe.request_count, "assertions": len(probe.assertions),
        "assertionLabels": probe.assertions, "deployment": probe.deployment, "jobTerminals": probe.terminals,
        "runs": probe.runs, "fixtures": probe.fixtures, "metadataRun": probe.redact(probe.metadata_run),
        "authorFrames": probe.author_frames, "pixelControls": probe.pixel_controls, "credentialScannedFiles": scanned,
        "workerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.worker_identities)],
        "scope": "Finite fresh Linux loopback owner/CAS/retained-source declaration inspection, precise Int64 text, "
            "SDK2 exact successful compilation gate, legacy/null and complete eight-field atomic adoption, rejection invariance, "
            "receipt replay/reuse, explicit source+configuration undo/redo, actual same-time transparent hardware Vulkan PNG pixels. "
            "No graph/group/new-material workflow, image export, browser/display/shared texture, cross-platform or performance acceptance. "
            "HTTP unchanged checks compare complete public draft/sample/history; they do not claim byte-exact private storage checks."}
    save_json(output / "summary.json", summary)
    save_json(output / "evidence-manifest.json", {"files": {str(path.relative_to(output)): sha(path.read_bytes())
        for path in sorted(output.rglob("*")) if path.is_file()}})
    print(json.dumps({key: summary[key] for key in ("status", "httpRequests", "assertions", "errorType")}))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
