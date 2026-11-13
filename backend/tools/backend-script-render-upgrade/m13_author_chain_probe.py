#!/usr/bin/env python3
"""M13 A3 e：按样例走完整作者链（fresh HTTP + GPU Worker）。

每个样例：仓库包导入建草稿 → 草稿预览出帧（源码 A）→ 给 C#／SkSL 源文件追加注释存为新修订 B
→ 含 C# 的包同步编译 B → 采用 B → 同一预览会话换输入出帧（只改注释，像素须与 A 逐字节相同）
→ 下载采用的 B 包重新导入，建个人模板（controller／graph 作效果配方，material 写入两态填充）
→ 建工程（浅色图片背景、每 1.25 秒一行）并逐行应用 → 工程预览出帧 → 保存到新目录 → 关闭 → 重开
→ 重开后的工程预览同一时刻出帧，与保存前逐字节相同，且磁盘上的工程引用 B。"""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlencode
import zipfile

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("m13a_export_probe", HERE / "m13_export_probe.py")
m13 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m13)
m12, m11, m10 = m13.m12, m13.m11, m13.m10
save_json = m10.save_json
# S01 补保存重开；S02、S05、S07、S08、S09 与附加三例此前只有 m11 作者验证。
CHAIN = ("WordEmphasis", "RepeatEmphasis", "SpriteTrail", "CustomInk", "LineGlitch", "GlassInk", "ClusterExit", "StarSweep", "WaveWarp")
EDIT_SUFFIXES = (".cs", ".sksl")
EDIT_MARK = "\n// M13 author chain: comment-only revision\n"
AUTHOR_TIME_US = 3_000_000  # 与 m07／m12 作者帧同一时刻
PROJECT_TIME_US = 1_500_000  # 第二行在唱、第一行退场中
SECONDS = 3


def sha(data):
    return hashlib.sha256(data).hexdigest()


class M13AuthorChainProbe(m13.M13ExportProbe):
    label = "m13a"

    def import_draft(self, name):
        directory = m10.ROOT / "samples" / "ScriptEffectsV2" / name
        raw = (directory / "manifest.json").read_bytes()
        manifest = json.loads(raw)
        files = {item["path"]: (directory / item["path"]).read_bytes() for item in manifest["files"]}
        self.check(all(sha(files[item["path"]]) == item["sha256"] for item in manifest["files"]), name + ": repository bytes match manifest")
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("manifest.json", raw)
            for path, data in files.items():
                package.writestr(path, data)
        asset = self.upload(name + "-package", "m13a-" + name + ".mkfx", "application/octet-stream", archive.getvalue())
        inspected = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], name + ": package admitted")
        export = manifest["exports"][0]
        draft = self.request(name + "-draft", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": export["id"], "name": "M13 " + name})
        self.check(draft["currentSourceRevision"] == inspected["packageRevision"] == draft["configuration"]["sourceRevision"],
            name + ": draft starts from the imported immutable source")
        return draft, export["kind"]

    def author_frame(self, name, draft):
        if self.active_preview is None:
            preview = self.request(name + "-preview", "POST", "/api/v1/preview-sessions", status=201, value={
                "source": self.author_source(draft), "size": {"width": self.args.width, "height": self.args.height},
                "alphaMode": "opaque", "purpose": "preview", "previewKind": "authorDraft", "committable": False, "renderBackend": "gpu"})
            self.active_preview = preview.get("session", preview)
            self.sequence = 0
        else:
            self.author_input(name + "-input", draft)
        self.check(self.active_preview["renderBackend"] == "gpu"
            and self.active_preview["source"]["authorSourceRevision"] == draft["configuration"]["sourceRevision"],
            name + ": GPU author preview pins the in-use source")
        return self.checked_frame(name, AUTHOR_TIME_US)

    def checked_frame(self, name, time_us):
        rgba = self.preview_rgba(name, time_us)
        diagnostics = self.request(name + "-diagnostics", "GET", "/api/v1/preview-sessions/" + self.active_preview["previewId"] + "/diagnostics")
        self.check(not diagnostics["hasMore"] and not any(item["severity"] == "error" for item in diagnostics["diagnostics"]),
            name + ": no runtime error diagnostics")
        return rgba

    def edit_source(self, name, draft):
        files = self.request(name + "-files", "GET", self.draft_path(draft) + "/package-files?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": draft["currentSourceRevision"]}))
        editable = [item for item in files["files"] if item["editable"] and item["path"] != "manifest.json"]
        targets = [item["path"] for item in editable if item["path"].endswith(EDIT_SUFFIXES)]
        self.check(bool(targets), name + ": package exposes an editable C# or SkSL source")
        texts = [{"path": item["path"], "text": item["text"] + (EDIT_MARK if item["path"] in targets else "")} for item in editable]
        saved = self.request(name + "-save", "PUT", self.draft_path(draft) + "/package-files", value={
            "expectedDraftRevision": draft["draftRevision"], "manifestJson": files["manifestJson"], "textFiles": texts,
            "metadata": self.metadata(name + "-save")})
        self.check(saved["currentSourceRevision"] != draft["currentSourceRevision"]
            and saved["configuration"]["sourceRevision"] == draft["configuration"]["sourceRevision"],
            name + ": saving sources creates a new latest revision and keeps the in-use one")
        return saved, targets

    def compile_and_adopt(self, name, draft):
        source = draft["currentSourceRevision"]
        compiled = bool(draft["canCompile"])
        if compiled:
            report = self.request(name + "-compile", "POST", self.draft_path(draft) + "/compilation-jobs", value={
                "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source, "executionMode": "sync"})
            self.check(report["succeeded"] and report["sourceRevision"] == source
                and not any(item["isError"] for item in report["diagnostics"]), name + ": edited source compiles")
            draft = self.read_draft(name + "-after-compile", draft)
        draft = self.adopt(name + "-adopt", draft, source)
        self.check(draft["configuration"]["sourceRevision"] == draft["adoptedSourceRevision"] == source, name + ": adopted revision is in use")
        return draft, compiled

    def template_from(self, name, draft, kind):
        config = draft["configuration"]
        source = config["sourceRevision"]
        _, data = self.binary(name + "-adopted-package", self.draft_path(draft) + "/package?" + urlencode({
            "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source}))
        with zipfile.ZipFile(io.BytesIO(data)) as package:
            manifest = json.loads(package.read("manifest.json"))
            self.check(all(sha(package.read(item["path"])) == item["sha256"] for item in manifest["files"]),
                name + ": downloaded adopted archive matches every manifest hash")
            edited = [item["path"] for item in manifest["files"] if package.read(item["path"]).decode("utf-8", "replace").endswith(EDIT_MARK)]
        self.check(bool(edited), name + ": downloaded archive carries the authored edit")
        asset = self.upload(name + "-adopted-upload", "m13a-" + name + "-adopted.mkfx", "application/octet-stream", data)
        inspected = self.request(name + "-adopted-inspect", "POST", "/api/v1/effect-packages/inspect",
            value={"packageAssetId": asset["assetId"], "expectedPackageRevision": source})
        self.check(inspected["structureValid"] and inspected["packageRevision"] == source, name + ": re-imported archive is the adopted revision")
        builtin = self.request(name + "-builtin", "GET", "/api/v1/template-libraries/builtin/templates")
        line = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request(name + "-builtin-detail", "GET", "/api/v1/template-libraries/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": line["templateId"]}))
        definition, properties, recipes = dict(detail["definition"]), ["opacity"], []
        if kind == "material":
            material = {"kind": "package", "packageId": config["packageId"], "revision": source, "exportId": config["exportId"],
                "parametersJson": config["parametersJson"]}
            style = definition["style"]
            definition["style"] = dict(style, unsung=dict(style["unsung"], fill=material), sung=dict(style["sung"], fill=material))
            properties += ["unsung.fill", "sung.fill"]
        else:
            recipes = [{"recipeId": "m13a-" + name.lower(), "name": name, "target": config["relativeTarget"],
                "packageId": config["packageId"], "revision": source, "exportId": config["exportId"],
                "parametersJson": config["parametersJson"], "enabled": True, "overlayLayer": config["overlayLayer"],
                "readScope": config["readScope"], "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"),
                "configurationId": None}]
        definition.update(name="M13 作者链 " + name, kind="line", properties=properties, packages=[inspected["reference"]], effectRecipes=recipes)
        library = self.request(name + "-personal", "GET", "/api/v1/template-libraries/personal/templates")
        self.template = self.request(name + "-template-create", "POST", "/api/v1/template-libraries/personal/commands", value={
            "expectedRevision": library["revision"], "metadata": self.metadata(name + "-template-create"),
            "command": {"type": "create", "definitionFormat": "full", "definition": definition}})
        return source

    def save_reopen(self, name, source):
        relative = "m13-author-" + name
        handle = self.request(name + "-save-handle", "POST", "/api/v1/file-handles", status=201,
            value={"rootGrantId": "projects", "relativePath": relative})["handleId"]
        revision = self.active_project["revision"]
        saved = self.job(name + "-save", self.request(name + "-save", "POST", self.active_project["path"] + "/save-jobs", status=202,
            value={"expectedRevision": revision, "targetHandleId": handle}))
        self.check(saved["state"] == "succeeded" and saved["savedProjectRevision"] == revision, name + ": project saved at the fixed revision")
        directories = [path for path in self.output.rglob(relative) if path.is_dir()]
        self.check(len(directories) == 1, name + ": saved project directory located in the isolated profile")
        texts = [path.read_text("utf-8", "replace") for path in directories[0].rglob("*.json")]
        self.check(any(source in text for text in texts), name + ": saved project references the adopted package revision")
        self.close_resources(name + "-before-reopen")
        opened = self.job(name + "-reopen", self.request(name + "-reopen", "POST", "/api/v1/project-sessions/open-jobs", status=202,
            value={"projectDirectoryHandleId": handle}))
        self.check(opened["state"] == "succeeded", name + ": saved project reopens")
        result = opened["result"]
        self.active_project = {"path": "/api/v1/project-sessions/" + result["projectSessionId"], "revision": result["projectRevision"]}
        self.check(not result["isDirty"] and result["hasPath"], name + ": reopened project is clean and bound to its directory")
        preview = self.request(name + "-reopened-preview", "POST", "/api/v1/preview-sessions", status=201, value={
            "source": {"kind": "project", "sourceId": result["projectSessionId"], "sourceRevision": result["projectRevision"]},
            "size": self.saved_size, "alphaMode": "opaque", "purpose": "preview", "renderBackend": "gpu"})
        self.active_preview = preview.get("session", preview)
        self.sequence = 0

    def chain(self, name):
        self.begin("author-" + name)
        result = {"name": "author-" + name, "status": "running"}
        try:
            self.label, self.names = "a-" + name, (name,)
            draft, kind = self.import_draft(name)
            result.update(kind=kind, sourceA=draft["currentSourceRevision"])
            before = self.author_frame(name + "-author-A", draft)
            draft, targets = self.edit_source(name + "-edit", draft)
            draft, compiled = self.compile_and_adopt(name, draft)
            after = self.author_frame(name + "-author-B", draft)
            self.check(sha(after) == sha(before), name + ": comment-only adopted revision renders byte-identical author frame")
            self.close_preview(name + "-author-close")
            result.update(sourceB=draft["configuration"]["sourceRevision"], editedFiles=targets, compiled=compiled,
                authorFrameSha256=sha(after))
            source = self.template_from(name, draft, kind)
            result["lines"] = self.create_project(name, SECONDS * 1_000_000)
            self.saved_size = self.active_preview["size"]
            saved = self.checked_frame(name + "-project-saved", PROJECT_TIME_US)
            self.save_reopen(name, source)
            reopened = self.checked_frame(name + "-project-reopened", PROJECT_TIME_US)
            self.check(sha(reopened) == sha(saved), name + ": reopened project frame is byte-identical to the saved one")
            changed = sum(1 for index in range(0, len(saved), 4) if saved[index:index + 3] != bytes(m13.BACKGROUND))
            self.check(changed >= m13.MINIMUM_CONTENT, name + ": project frame has visible effect content")
            result.update(status="passed", projectFrameSha256=sha(reopened), nonBackgroundPixels=changed)
        except Exception as error:
            result.update(status="failed", failure=self.failure(error))
        finally:
            try:
                self.close_resources("author-" + name)
            except Exception as error:
                result.update(status="failed", cleanupFailure=self.failure(error))
            self.end(result)

    def run(self):
        deployment = json.loads((self.dist / "deployment.json").read_text())
        save_json(self.output / "provenance.json", {"deployment": {key: deployment.get(key) for key in (
            "version", "sourceCommit", "sourceDirty")}, "probeSha256": sha(Path(__file__).read_bytes()), "samples": list(self.args.labels)})
        self.stage = "start"
        self.start("m13a")
        self.stage = "connect"
        self.connect("m13a")
        self.check(self.current_ready["version"] == deployment["version"], "running version matches deployment manifest")
        self.background = self.upload("m13a-background", "m13a-light.png", "image/png",
            m10.solid_png(self.args.width, self.args.height, m13.BACKGROUND))
        for name in self.args.labels:
            self.stage = "author-" + name
            self.chain(name)


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--samples", default=",".join(CHAIN), help="ScriptEffectsV2 sample names")
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=360)
    parser.add_argument("--timeout-seconds", type=float, default=180)
    parser.add_argument("--poll-ms", type=int, default=50)
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    parser.add_argument("--ffmpeg", default="/usr/bin/ffmpeg")
    args = parser.parse_args()
    labels = args.samples.split(",")
    if not (64 <= args.width <= 1280 and 64 <= args.height <= 720) or any(label not in m13.SAMPLES for label in labels):
        parser.error("size outside preview bounds or unknown sample")
    args.samples, args.count, args.require_reuse, args.backend, args.background, args.scenario = "t01", 2, False, "gpu", None, "m13a"
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    args.labels, args.transparent, args.straight_session = labels, set(), "put"
    return args


def main():
    args = arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    if args.icd.is_file():
        os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = str(args.icd)
    for key in [key for key in os.environ if key.startswith("MAKO_PERFORMANCE_")]:
        del os.environ[key]
    probe = M13AuthorChainProbe(args.output, args.dist, args)
    failures = []
    try:
        probe.run()
    except Exception as error:
        failures.append(probe.failure(error))
    finally:
        if probe.sse is not None:
            probe.sse.close()
        if probe.active_preview or probe.active_project:
            try:
                probe.close_resources("failure")
            except Exception as error:
                failures.append(probe.failure(error, "resource-cleanup"))
        try:
            probe.stop("m13a")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        summary = {"status": "passed" if not failures and len(probe.results) == len(args.labels)
            and all(result["status"] == "passed" for result in probe.results) else "failed",
            "size": [args.width, args.height], "authorTimeUs": str(AUTHOR_TIME_US), "projectTimeUs": str(PROJECT_TIME_US),
            "failures": failures, "results": probe.results,
            "scope": "Real HTTP + GPU Worker author chain per sample on this machine: draft, comment-only source revision, "
                "compile (C#), adopt, author frame, re-imported template, project save/close/reopen and frame identity."}
        save_json(args.output / "summary.json", summary)
        try:
            probe.scan_credentials()
        except Exception as error:
            summary["status"] = "failed"
            summary["credentialFailure"] = probe.failure(error, "credential-scan")
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "results": [{key: result.get(key) for key in
            ("name", "status", "kind", "compiled", "nonBackgroundPixels")} | ({"failure": result["failure"].get("message")}
            if result.get("failure") else {}) for result in probe.results],
            "failures": [{key: item.get(key) for key in ("stage", "errorType", "message")} for item in failures]}, ensure_ascii=False))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
