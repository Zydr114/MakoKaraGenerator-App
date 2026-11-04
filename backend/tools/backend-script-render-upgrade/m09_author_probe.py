#!/usr/bin/env python3
"""Finite M09 author/package HTTP acceptance against a matched deployment."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.parse import urlencode
import zipfile

sys.dont_write_bytecode = True
# Works from .local/m09-http-probe or after root integrates this beside M07/M08.
REPO = next(path for path in Path(__file__).resolve().parents
    if (path / "backend/tools/backend-script-render-upgrade/m07_author_probe.py").is_file())
BACKEND = REPO / "backend"
TOOLS = BACKEND / "tools/backend-script-render-upgrade"
SPEC = importlib.util.spec_from_file_location("m09_m08_author", TOOLS / "m08_author_probe.py")
m08 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m08)
m07 = m08.m07
selection = m07.selection
save_json = m07.save_json
sha = lambda value: hashlib.sha256(value).hexdigest()
SVG_PATH = "assets/main.svg"
SVG_DESCRIPTOR = "resources/drawing.json"
ERROR_CODE = "PackageSvgSyntaxInvalid"


def profile():
    """Use the versioned repository fixture profile, never infer it from a URL."""
    value = json.loads((BACKEND / "samples/ScriptEffectsV2/WordEmphasis/manifest.json").read_text())
    return {key: value[key] for key in ("packageVersion", "sdkVersion", "shaderProfile")}


def svg_source(atlas):
    # Actual SvgRuntimePackageFixture Declare/VisualOutputs/Draw contract, reduced
    # to one original large solid rectangle outside the lyric region.
    resource = "SpriteHandle" if atlas else "VectorHandle"
    lookup = 'source.Resources.GetSprite("drawing", "fixed")' if atlas else 'source.Resources.GetVector("drawing")'
    primitive = "Sprites" if atlas else "Paths"
    resource_kind = "SvgAtlas" if atlas else "SvgVector"
    draw = "DrawAtlasSprites" if atlas else "DrawPaths"
    return f'''using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
public sealed record SvgData({resource} Resource) : PreparedEffect;
public sealed class SvgEffect : IPureKaraokeEffect, IPureVisualEffect
{{
    public PreparedEffect Declare(DeclareContext source) => new SvgData({lookup})
    {{ VisualOutputs = [new(new("drawing"), VisualPrimitiveKind.{primitive}, OverlayLayer.AboveText, VisualResourceKind.{resource_kind})] }};
    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) {{ }}
    public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
    {{
        var d = (SvgData)data;
        var matrix = new Affine2(frame.CanvasSize.X / 4, 0, 0, frame.CanvasSize.Y / 4,
            frame.CanvasSize.X / 16 + frame.TimeUs % 1000000 / 100000.0, frame.CanvasSize.Y / 18);
        output.{draw}(new("drawing"), [new(d.Resource, matrix, new ColorRgba(1, 1, 1, 1))], OverlayLayer.AboveText);
    }}
}}
'''


def svg_fixture(atlas):
    kind = "atlas" if atlas else "vector"
    capabilities = ["visuals.sprites", "resources.svg.atlas"] if atlas else ["visuals.paths", "resources.svg.vector"]
    descriptor = {"svgResourceVersion": 1, "id": "drawing", "source": SVG_PATH,
        "profile": "mako-svg-static-1", "mode": kind,
        "viewport": {"width": 100, "height": 100}, "fonts": [], "images": []}
    if atlas:
        descriptor["atlas"] = {"variants": [{"id": "fixed", "width": 100, "height": 100}],
            "paddingPx": 4, "mipmaps": True}
    files = {"src/SvgEffect.cs": svg_source(atlas).encode(),
        SVG_PATH: b"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' fill='#e04020'/></svg>",
        SVG_DESCRIPTOR: json.dumps(descriptor, separators=(",", ":")).encode()}
    manifest = {**profile(), "packageId": "mako.m09.http.svg-" + kind, "name": "M09 HTTP SVG " + kind,
        "version": "2.0.0", "requires": capabilities, "lineExit": {"durationUs": 0}, "emitters": [],
        "parameters": [], "exports": [{"id": "svg", "kind": "controller", "name": "M09 SVG " + kind,
            "entryType": "SvgEffect", "parameters": [], "bindingKinds": ["project"],
            "outputs": [{"id": "drawing", "kind": "Overlay", "target": "canvas", "requires": capabilities, "dependsOn": []}]}]}
    return manifest, files


def warp_fixture():
    """Use the exact production-admitted test controller rather than invent SetWarp."""
    path = BACKEND / "tests/MakoKaraGenerator.Rendering.Tests/V2WarpPackageTests.cs"
    text = path.read_text()
    match = re.search(r'private const string ControllerSource = """\n(.*?)\n        """;', text, re.S)
    if not match:
        raise AssertionError("V2WarpPackageTests.ControllerSource raw string changed; refresh the fixture deliberately")
    # Strip the C# raw-string indentation exactly.
    source = "\n".join(line[8:] if line.startswith("        ") else line for line in match.group(1).splitlines()) + "\n"
    parameters = [("target", 1, 0, 4), ("dx", 13, -100, 100), ("dy", 7, -100, 100),
        ("writes", 1, 0, 257), ("clear", 0, 0, 1), ("badDomain", 0, 0, 1),
        ("cells", 1, 1, 32), ("vary", 1, 0, 1)]
    manifest = {**profile(), "packageId": "mako.m09.http.warp", "name": "M09 HTTP Warp",
        "version": "2.0.0", "requires": ["token.warp", "cluster.warp", "mark.warp"],
        "lineExit": {"durationUs": 0}, "emitters": [],
        "parameters": [{"id": name, "type": "number", "label": name, "default": value, "min": low, "max": high}
            for name, value, low, high in parameters],
        "exports": [{"id": "warp", "kind": "controller", "name": "M09 Warp", "entryType": "WarpEffect",
            "parameters": [item[0] for item in parameters], "bindingKinds": ["project"],
            "outputs": [{"id": name, "kind": "VisualTransform", "target": target, "requires": [], "dependsOn": []}
                for name, target in (("family", "tokenFamily"), ("base", "baseToken"), ("cluster", "cluster"), ("mark", "textMark"))]}]}
    return manifest, {"src/WarpEffect.cs": source.encode()}


def archive(manifest, files):
    value = copy.deepcopy(manifest)
    value["files"] = [{"path": path, "role": "csharp" if path.endswith(".cs") else "svg" if path.endswith(".svg") else "svgResource",
        "sha256": sha(data)} for path, data in sorted(files.items())]
    raw_manifest = json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as package:
        # Fixed metadata makes the fixture archive itself reproducible.
        for path, data in [("manifest.json", raw_manifest), *sorted(files.items())]:
            entry = zipfile.ZipInfo(path, (2026, 11, 5, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(entry, data)
    return value, output.getvalue()


class M09AuthorProbe(m08.M08AuthorProbe):
    def __init__(self, output, dist, icd):
        super().__init__(output, dist, icd)
        self.m09_runs = []
        self.projects = {}
        self.pending_jobs = {}

    def request(self, name, method, path, **kwargs):
        response = super().request(name, method, path, **kwargs)
        if method == "POST" and isinstance(response, dict) and response.get("jobId"):
            self.pending_jobs[response["jobId"]] = name
        return response

    def long_job(self, name, accepted, expected="succeeded"):
        result = super().long_job(name, accepted, expected)
        self.pending_jobs.pop(accepted["jobId"], None)
        return result

    def send(self, name, command):
        result = super().send(name, command)
        # Keep the finally-close CAS current even if a later mutation fails.
        if self.project:
            self.projects[self.project] = self.revision
        return result

    def import_fixture(self, name, manifest, files):
        manifest, data = archive(manifest, files)
        (self.output / (name + "-fixture.mkfx")).write_bytes(data)
        asset = self.upload(name + "-upload", name + ".mkfx", "application/octet-stream", data)
        inspected = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], name + ": uploaded fixed archive structure admitted")
        draft = self.request(name + "-create", "POST", "/api/v1/drafts", status=201,
            value={"packageAssetId": asset["assetId"], "exportId": manifest["exports"][0]["id"], "name": "M09 " + name})
        self.check(draft["canCompile"] and draft["packageVersion"] == 3 and draft["sdkVersion"].split(".")[0] == "2",
            name + ": actual v3 SDK2 controller admitted")
        self.check(draft["currentSourceRevision"] == inspected["packageRevision"] == draft["configuration"]["sourceRevision"],
            name + ": import pins exact inspected package revision")
        self.fixtures.append({"name": name, "packageId": manifest["packageId"], "archiveSha256": sha(data),
            "manifest": manifest, "files": {path: sha(value) for path, value in files.items()},
            "sourceRevision": draft["currentSourceRevision"]})
        save_json(self.output / "fixtures.json", self.fixtures)
        return self.record(name + "-imported", draft)

    def package_read(self, name, draft):
        result = self.files(name, draft)
        self.check(result["draftId"] == draft["draftId"] and result["draftRevision"] == draft["draftRevision"]
            and result["sourceRevision"] == result["packageRevision"] == draft["currentSourceRevision"],
            name + ": complete package GET binds exact draft and source revisions")
        manifest = json.loads(result["manifestJson"])
        self.check({item["path"] for item in result["files"]} == {item["path"] for item in manifest["files"]},
            name + ": all manifest files described")
        for item in result["files"]:
            if item["editable"]:
                value = item["text"].encode()
                self.check(len(value) == item["sizeBytes"] and sha(value) == item["sha256"], name + ": editable bytes/hash " + item["path"])
        return result

    def author_svg(self, name, draft):
        initial = draft
        original = self.package_read(name + "-full-get", draft)
        by_path = {item["path"]: item for item in original["files"]}
        self.check(all(by_path[path]["editable"] and isinstance(by_path[path].get("text"), str)
            for path in (SVG_PATH, SVG_DESCRIPTOR)), name + ": deployed author role patch exposes svg and svgResource as editable text")
        texts = [{"path": item["path"], "text": item["text"]} for item in original["files"] if item["editable"]]
        broken = copy.deepcopy(texts)
        next(item for item in broken if item["path"] == SVG_PATH)["text"] = (
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><path d='M0 0 Lbad'/></svg>")
        # package-files accepts the structural immutable package; real SVG grammar
        # validation belongs to both author compilation modes.
        draft = self.record(name + "-bad-svg-put", self.save_files(name + "-bad-svg-put", draft, original, text_files=broken))
        self.check(draft["currentSourceRevision"] != initial["currentSourceRevision"]
            and draft["draftRevision"] != initial["draftRevision"], name + ": bad SVG save creates immutable new source and CAS revision")
        self.assert_in_use(name + "-bad-save", initial, draft)
        self.request(name + "-stale-put", "PUT", self.path(draft) + "/package-files", status=409, code="revisionConflict",
            value={"expectedDraftRevision": initial["draftRevision"], "manifestJson": original["manifestJson"],
                "textFiles": texts, "metadata": self.metadata(name + "-stale-put")})
        self.assert_unchanged(name + "-after-stale-put", draft)
        bad_source = draft["currentSourceRevision"]
        failures = []
        for asynchronous in (False, True):
            label = name + ("-bad-async" if asynchronous else "-bad-sync")
            before = draft
            result = self.compile_source(label, draft, bad_source, succeeded=False, asynchronous=asynchronous)
            self.check(any(item["isError"] and item["code"] == ERROR_CODE and item.get("path") == SVG_PATH
                for item in result["diagnostics"]), label + ": exact SVG syntax diagnostic and source path")
            self.check(result["artifactHash"] == "none", label + ": no C# artifact published after static SVG failure")
            failures.append(result)
            draft = self.refreshed(label + "-draft", draft)
            self.assert_in_use(label, before, draft)
        draft = self.adopt(name + "-bad-adopt", draft, bad_source, status=422, failure="AuthorSourceCompilationRequired")
        current = self.package_read(name + "-bad-full-get", draft)
        repaired = copy.deepcopy(texts)
        # A new valid revision, rather than returning to the previously imported hash.
        for item in repaired:
            if item["path"] == SVG_PATH:
                item["text"] = item["text"].replace("</svg>", "<!-- M09 repaired immutable revision --></svg>")
            elif item["path"] == SVG_DESCRIPTOR:
                item["text"] += "\n"
        before = draft
        draft = self.record(name + "-repair-put", self.save_files(name + "-repair-put", draft, current, text_files=repaired))
        self.assert_in_use(name + "-repair-save", before, draft)
        source = draft["currentSourceRevision"]
        self.check(source not in (bad_source, initial["currentSourceRevision"]), name + ": repair is a third exact source revision")
        result = self.compile_source(name + "-repair-compile", draft, source, asynchronous=True)
        before = draft
        draft = self.refreshed(name + "-repaired", draft)
        self.assert_in_use(name + "-repair-compile", before, draft)
        draft = self.adopt(name + "-repair-adopt", draft, source)
        self.package_read(name + "-adopted-full-get", draft)
        return draft, {"failures": failures, "compilation": result}

    def author_warp(self, name, draft):
        original = self.package_read(name + "-full-get", draft)
        texts = [{"path": item["path"], "text": item["text"] + "\n// M09 authored immutable Warp revision\n"}
            for item in original["files"] if item["editable"]]
        before = draft
        draft = self.record(name + "-put", self.save_files(name + "-put", draft, original, text_files=texts))
        self.assert_in_use(name + "-save", before, draft)
        source = draft["currentSourceRevision"]
        self.check(source != before["currentSourceRevision"], name + ": full package PUT changes actual Warp revision")
        result = self.compile_source(name + "-compile", draft, source, asynchronous=True)
        draft = self.refreshed(name + "-compiled", draft)
        draft = self.adopt(name + "-adopt", draft, source)
        return draft, {"compilation": result}

    def preview_request(self, draft, backend="gpu"):
        result = super().preview_request(draft, backend)
        result["alphaMode"] = "straight"
        return result

    def transparent_frame(self, name, preview, time_us, sequence):
        # SelectionProbe.frame hardcodes opaque alpha. Reuse its exact frame
        # request/poll/PNG/SHA decoder from CompletionProbe, then verify GPU here.
        rgba = selection.completion.CompletionProbe.frame(self, name, preview, str(time_us), sequence, intent="seek")
        state = self.state(name + "-identity", preview)
        frame = state["latestFrame"]
        self.check(state["source"] == preview["source"] and state["renderBackend"] == frame["renderBackend"] == "gpu"
            and frame["requestSeq"] == str(sequence) and frame["timeUs"] == str(time_us), name + ": source/time/sequence/backend fixed")
        self.check(min(rgba[3::4]) == 0 and max(rgba[3::4]) > 0, name + ": transparent and visible full PNG pixels")
        live = {(item["pid"], item["startTicks"]) for item in self.worker_identities_now()}
        self.require_gpu(name, identities=live)
        diagnostics = self.diagnostic(name + "-diagnostics", preview)
        self.check(diagnostics["previewGeneration"] == preview["previewGeneration"]
            and diagnostics["sourceRevision"] == preview["source"]["sourceRevision"]
            and not diagnostics["hasMore"] and not any(item["severity"] == "error" for item in diagnostics["diagnostics"]),
            name + ": exact complete diagnostics page without runtime failure")
        self.author_frames.append({"name": name, "source": state["source"], "previewId": state["previewId"],
            "previewGeneration": state["previewGeneration"], "frame": frame, "rgbaSha256": sha(rgba)})
        save_json(self.output / "author-frames.json", self.author_frames)
        return rgba

    def block(self, name, rgba, width, expected=(224, 64, 32, 255), tolerance=2):
        # Native canvas-to-viewport scaling puts the rectangle at 20..110 x
        # 10..55 for a 320x180 output. This block stays far from every edge.
        samples = [rgba[(y * width + x) * 4:(y * width + x) * 4 + 4]
            for y in range(22, 38) for x in range(42, 58)]
        self.check(all(abs(pixel[channel] - expected[channel]) <= tolerance for pixel in samples for channel in range(4)),
            name + ": complete 16x16 interior block matches authored SVG RGBA within " + str(tolerance))

    def exercise_preview(self, name, draft, svg):
        preview = self.create_preview(name + "-gpu-transparent", draft)
        first = self.transparent_frame(name + "-at-1250000", preview, 1_250_000, 1)
        repeated = self.transparent_frame(name + "-repeat-1250000", preview, 1_250_000, 2)
        self.check(first == repeated, name + ": repeated same-time seek produces identical complete RGBA")
        later = self.transparent_frame(name + "-seek-1750000", preview, 1_750_000, 3)
        returned = self.transparent_frame(name + "-return-1250000", preview, 1_250_000, 4)
        self.check(first == returned, name + ": seek away and back preserves complete deterministic RGBA")
        self.check(first != later, name + ": authored time variation produces actual different complete pixels")
        if svg:
            for label, value in (("first", first), ("repeated", repeated), ("later", later), ("returned", returned)):
                self.block(name + "-" + label, value, 320)
        self.close(name + "-preview-close", preview)
        self.require_workers_drained(name + "-preview-drain")
        if not svg:
            params = json.loads(draft["configuration"]["parametersJson"])
            params.update(dx=0, dy=0, vary=0)
            control = self.configure(name + "-zero-warp", draft, params)
            preview = self.create_preview(name + "-zero-preview", control)
            baseline = self.transparent_frame(name + "-zero-at-1250000", preview, 1_250_000, 1)
            self.check(sum(first[index:index+4] != baseline[index:index+4] for index in range(0, len(first), 4)) > 20,
                name + ": same-time zero-displacement control differs at more than twenty actual pixels")
            self.close(name + "-zero-close", preview)
            self.require_workers_drained(name + "-zero-drain")
            # Retain authored parameters for later project export.
            draft = self.configure(name + "-restore-warp", control, json.loads(draft["configuration"]["parametersJson"]))
        return draft

    def create_template(self, name, draft):
        data = self.download(name + "-adopted-package", draft, draft["configuration"]["sourceRevision"])
        with zipfile.ZipFile(io.BytesIO(data)) as package:
            manifest = json.loads(package.read("manifest.json"))
            self.check(all(sha(package.read(item["path"])) == item["sha256"] for item in manifest["files"]),
                name + ": adopted complete archive matches every manifest hash")
        asset = self.upload(name + "-adopted-upload", name + "-adopted.mkfx", "application/octet-stream", data)
        inspected = self.request(name + "-adopted-inspect", "POST", "/api/v1/effect-packages/inspect",
            value={"packageAssetId": asset["assetId"], "expectedPackageRevision": draft["configuration"]["sourceRevision"]})
        self.check(inspected["structureValid"] and inspected["packageRevision"] == draft["configuration"]["sourceRevision"],
            name + ": project template references exact adopted package")
        builtin = self.request(name + "-builtin", "GET", "/api/v1/template-libraries/builtin/templates")
        base = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request(name + "-builtin-detail", "GET", "/api/v1/template-libraries/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": base["templateId"]}))
        definition = copy.deepcopy(detail["definition"])
        config = draft["configuration"]
        definition.update(name="M09 " + name, kind="composite", properties=[], packages=[inspected["reference"]])
        definition["effectRecipes"] = [{"recipeId": "m09-" + name, "name": "M09 " + name,
            "target": "application", "packageId": config["packageId"], "revision": config["sourceRevision"],
            "exportId": config["exportId"], "parametersJson": config["parametersJson"], "enabled": True,
            "overlayLayer": config["overlayLayer"], "readScope": config["readScope"],
            "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"), "configurationId": None}]
        library = self.request(name + "-personal", "GET", "/api/v1/template-libraries/personal/templates")
        return self.request(name + "-template-create", "POST", "/api/v1/template-libraries/personal/commands",
            value={"expectedRevision": library["revision"], "metadata": self.metadata(name + "-template-create"),
                "command": {"type": "create", "definitionFormat": "full", "definition": definition}})

    def project_for_export(self, name, draft):
        template = self.create_template(name, draft)
        lyric = self.upload(name + "-lyrics", name + ".lrc", "text/plain", b"[00:00.00]M09 SVG Warp\n")
        created = self.request(name + "-project", "POST", "/api/v1/project-sessions", status=201,
            value={"name": "M09 image export " + name, "lyricsAssetId": lyric["assetId"],
                "composition": {"canvasWidth": 320, "canvasHeight": 180, "frameRateNumerator": 24,
                    "frameRateDenominator": 1, "outputDurationUs": 2_000_000}})
        self.project, self.revision = created["projectSessionId"], created["projectRevision"]
        self.projects[self.project] = self.revision
        lines = self.lyrics(name + "-project-lines")["lines"]
        self.check(len(lines) == 1 and bool(lines[0]["tokens"]), name + ": finite two-second actual lyric project")
        line = lines[0]
        self.send(name + "-line-time", {"type": "lyrics.setTimeRange", "target": {"branchId": "primary", "lineId": line["id"]},
            "startUs": "0", "endUs": "2000000"})
        self.send(name + "-token-times", {"type": "lyrics.setTokenTimes", "changes": [{"target": {
            "branchId": "primary", "lineId": line["id"], "tokenId": item["id"]}, "startUs": str(2_000_000 * index // len(line["tokens"])),
            "endUs": str(2_000_000 * (index + 1) // len(line["tokens"]))} for index, item in enumerate(line["tokens"])]})
        image = self.upload(name + "-image", name + "-background.png", "image/png", self.image_bytes())
        self.send(name + "-image-background", {"type": "composition.setBackgroundAsset", "assetId": image["assetId"],
            "kind": "image", "fit": "cover", "playback": "loop"})
        self.send(name + "-apply-project", {"type": "presetApplications.apply", "templateLibraryId": "personal",
            "templateId": template["templateId"], "templateRevision": template["revision"], "targets": [{"kind": "project"}]})
        self.projects[self.project] = self.revision
        settings = self.settings(name + "-image-settings")
        self.check(settings["background"]["kind"] == "image", name + ": project really retains image background")
        handle = self.request(name + "-save-handle", "POST", "/api/v1/file-handles", status=201,
            value={"rootGrantId": "projects", "relativePath": "m09-" + name})["handleId"]
        saved = self.long_job(name + "-save", self.request(name + "-save", "POST", self.project_path + "/save-jobs", status=202,
            value={"expectedRevision": self.revision, "targetHandleId": handle}))
        self.check(saved["savedProjectRevision"] == self.revision, name + ": managed image and adopted package saved at fixed project revision")
        self.close_project(name + "-before-reopen")
        opened = self.long_job(name + "-reopen", self.request(name + "-reopen", "POST", "/api/v1/project-sessions/open-jobs", status=202,
            value={"projectDirectoryHandleId": handle}))["result"]
        self.project, self.revision = opened["projectSessionId"], opened["projectRevision"]
        self.projects[self.project] = self.revision
        self.check(self.settings(name + "-reopened-image")["background"]["kind"] == "image", name + ": reopened managed Image closure retained")
        return image

    def close_project(self, name):
        project = self.project
        result = self.long_job(name, self.request(name, "POST", self.project_path + "/close", status=202,
            value={"expectedRevision": self.revision, "policy": "discard", "activeJobs": "wait"}))
        self.projects.pop(project, None)
        self.project = None
        return result

    def image_export(self, name, draft, svg):
        self.project_for_export(name, draft)
        source = {"kind": "project", "sourceId": self.project, "sourceRevision": self.revision}
        accepted = self.request(name + "-image-export", "POST", self.project_path + "/export-jobs", status=202,
            value={"expectedRevision": self.revision, "format": "transparentProRes4444", "includeAudio": False,
                "quality": "standard", "renderBackend": "gpu"})
        self.check(accepted["input"]["sourceId"] == source["sourceId"] and accepted["input"]["sourceRevision"] == source["sourceRevision"]
            and accepted["input"]["renderBackend"] == accepted["exportInput"]["renderBackend"] == "gpu",
            name + ": accepted Image-background export pins exact project and GPU execution input")
        # Use the existing render-task poller to preserve real Vulkan Worker
        # attribution; author compilation uses M07's separate poller above.
        terminal = selection.SelectionProbe.long_job(self, name + "-image-export", accepted)
        self.pending_jobs.pop(accepted["jobId"], None)
        self.check(terminal["input"] == accepted["input"] and terminal["exportInput"] == accepted["exportInput"],
            name + ": terminal retains exact accepted render and export inputs")
        report = self.report(name + "-image-export", terminal, "export")
        self.check(report["format"] == "transparentProRes4444" and report["hasAlpha"]
            and report["width"] == 320 and report["height"] == 180 and report["expectedFrames"] == 48,
            name + ": verified two-second 24fps transparent export plan")
        reference = terminal["exportResult"]["artifact"]
        _, media, _ = self.fetch(name + "-image-media", "/api/v1/jobs/" + terminal["jobId"] + "/export-content")
        self.check(len(media) == reference["sizeBytes"] == report["mediaSize"]
            and sha(media) == reference["sha256"] == report["sha256"], name + ": full downloadable media identity")
        path = self.output / (name + "-image-transparent.mov")
        path.write_bytes(media)
        decoded = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"],
            capture_output=True, timeout=90)
        frame_bytes = 320 * 180 * 4
        self.check(decoded.returncode == 0 and len(decoded.stdout) == 48 * frame_bytes,
            name + ": every exported frame independently decodes to complete RGBA")
        self.check(min(decoded.stdout[3::4]) == 0 and max(decoded.stdout[3::4]) > 0,
            name + ": decoded video retains actual transparent and visible pixels")
        sampled = decoded.stdout[30 * frame_bytes:31 * frame_bytes]  # Exactly 1.25s at 24fps.
        if svg:
            # ProRes4444 is a separate encoding claim: allow a documented 12/255
            # channel tolerance only in a large interior block, never PNG edges.
            self.block(name + "-encoded-block", sampled, 320, tolerance=12)
        save_json(self.output / (name + "-export-pixels.json"), {"frameCount": 48, "rgbaSha256": sha(decoded.stdout),
            "sampleFrame": 30, "sampleTimeUs": "1250000", "sampleRgbaSha256": sha(sampled),
            "encodedInteriorChannelTolerance": 12 if svg else None, "report": report, "source": source})
        self.close_project(name + "-project-close")
        self.require_workers_drained(name + "-image-export-drain")
        return {"terminal": terminal, "report": report, "mediaSha256": sha(media), "sampleRgbaSha256": sha(sampled)}

    def drain(self):
        """Best-effort owned HTTP close/cancel, then launcher identity drain."""
        errors = []
        if self.current_ready and self.launcher is not None and self.launcher.poll() is None:
            for client, preview in list(self.open_previews):
                try:
                    self.close("m09-final-preview-" + preview["previewId"], preview)
                except BaseException as error:
                    errors.append({"kind": "preview", "detail": self.safe_text(error)})
            for job_id in list(self.pending_jobs):
                try:
                    current = self.request("m09-final-job-" + job_id, "GET", "/api/v1/jobs/" + job_id)
                    if current["state"] not in ("succeeded", "failed", "cancelled", "unknown"):
                        self.cancel_job("m09-final-cancel-" + job_id, job_id)
                        deadline = time.monotonic() + 45
                        while current["state"] not in ("succeeded", "failed", "cancelled", "unknown") and time.monotonic() < deadline:
                            time.sleep(.15)
                            current = self.request("m09-final-drain-" + job_id, "GET", "/api/v1/jobs/" + job_id)
                    self.check(current["state"] in ("succeeded", "failed", "cancelled", "unknown"),
                        "owned pending job reaches terminal before shutdown: " + job_id)
                    self.pending_jobs.pop(job_id, None)
                except BaseException as error:
                    errors.append({"kind": "job", "detail": self.safe_text(error)})
            for project_id, revision in list(self.projects.items()):
                try:
                    self.project, self.revision = project_id, revision
                    self.close_project("m09-final-project-" + project_id)
                except BaseException as error:
                    errors.append({"kind": "project", "detail": self.safe_text(error)})
        save_json(self.output / "final-http-drain.json", {"errors": errors, "pendingJobsBeforeHostStop": sorted(self.pending_jobs)})
        self.stop("m09-final")
        self.check(not errors, "all owned HTTP previews/jobs/projects drained before launcher shutdown")

    def run(self, include_warp=True, include_export=True):
        deployment = selection.host.validate_dist(self.dist)
        self.deployment = {key: deployment.get(key) for key in ("version", "sourceCommit", "sourceDirty", "rid", "selfContained")}
        save_json(self.output / "deployment-reference.json", {**self.deployment, "dist": str(self.dist),
            "manifestSha256": sha((self.dist / "deployment.json").read_bytes())})
        self.start("m09-start")
        self.connect("m09")
        self.check(self.current_ready["version"] == deployment["version"], "ready host matches validated deployment")
        availability = self.request("m09-backends", "GET", "/api/v1/render-backends")
        gpu = next(item for item in availability["backends"] if item["renderBackend"] == "gpu")
        self.check(gpu["available"] and gpu["api"] == "vulkan" and bool(gpu.get("device")), "actual deployment offers Vulkan hardware backend")
        fixtures = [("svg-vector", *svg_fixture(False)), ("svg-atlas", *svg_fixture(True))]
        if include_warp:
            fixtures.append(("warp-base", *warp_fixture()))
        for name, manifest, files in fixtures:
            self.stage = name + ":author"
            draft = self.import_fixture(name, manifest, files)
            is_svg = name.startswith("svg-")
            draft, author = self.author_svg(name, draft) if is_svg else self.author_warp(name, draft)
            self.stage = name + ":transparent-preview"
            draft = self.exercise_preview(name, draft, is_svg)
            result = {"name": name, "draft": draft, "author": author, "export": None}
            if include_export:
                self.stage = name + ":image-background-export"
                result["export"] = self.image_export(name, draft, is_svg)
            self.m09_runs.append(result)
            save_json(self.output / "m09-runs.json", self.redact(self.m09_runs))
        self.stage = "complete"
        self.require_workers_drained("m09-all-workers")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    parser.add_argument("--skip-warp", action="store_true", help="Explicitly exclude Warp from this finite run")
    parser.add_argument("--skip-export", action="store_true", help="Explicitly exclude project Image-background export from this finite run")
    args = parser.parse_args()
    icd = selection.gpu.hardware_icd(args.icd)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    os.environ["MAKO_RENDER_BACKEND"] = "cpu"
    os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = icd["path"]
    probe = M09AuthorProbe(output, args.dist.resolve(), icd)
    save_json(output / "environment.json", {"defaultBackend": "cpu", "icd": icd, "scenarioBackend": "gpu",
        "includeWarp": not args.skip_warp, "includeImageBackgroundExport": not args.skip_export})
    failure = None
    try:
        probe.run(include_warp=not args.skip_warp, include_export=not args.skip_export)
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
    save_json(output / "gpu-workers.json", {"observations": hardware, "assignments": probe.gpu_assignments,
        "exportHardware": probe.task_hardware})
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        scanned = None
        save_json(output / "scan-failure.json", {"errorType": type(error).__name__, "detail": probe.safe_text(error)})
    summary = {"status": "failed" if failure else "passed", "errorType": type(failure).__name__ if failure else None,
        "httpRequests": probe.request_count, "assertions": len(probe.assertions), "assertionLabels": probe.assertions,
        "deployment": probe.deployment, "jobTerminals": probe.terminals, "runs": probe.runs, "fixtures": probe.fixtures,
        "m09Runs": probe.redact(probe.m09_runs), "authorFrames": probe.author_frames, "credentialScannedFiles": scanned,
        "includeWarp": not args.skip_warp, "includeImageBackgroundExport": not args.skip_export,
        "workerIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.worker_identities)],
        "scope": "Finite Linux SVG vector/atlas full author GET/PUT CAS, named sync/async static SVG failure, new repaired source, "
            "compile/adopt, exact-source hardware Vulkan transparent PNGs with repeated/seek RGBA, optional real Base Warp control, "
            "optional saved/reopened project Image-background transparent ProRes4444 export terminal and complete decode. "
            "No browser/display/shared texture, cross-platform, arbitrary SVG profile, exhaustive Warp target or performance acceptance."}
    save_json(output / "summary.json", summary)
    save_json(output / "evidence-manifest.json", {"files": {str(path.relative_to(output)): sha(path.read_bytes())
        for path in sorted(output.rglob("*")) if path.is_file()}})
    # Only aggregate counters reach stdout. Credential-bearing data stays in memory.
    print(json.dumps({key: summary[key] for key in ("status", "httpRequests", "assertions", "errorType")}))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
