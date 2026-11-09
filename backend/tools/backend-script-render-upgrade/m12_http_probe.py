#!/usr/bin/env python3
"""M12 performance matrix over real HTTP + GPU Worker; one mode per process and fresh profile.

seek    SDK2 sample set on a project; explicit seeks (near/180s/360s) -> exact ready frame -> full PNG.
export  SDK2 sample set on a project; opaque MP4 export job dispatch -> terminal; frames per second.
author  WordEmphasis author draft preview: parameter save, source save+compile+adopt and adoption of an
        already compiled revision, each -> matching frame.
Every mode samples owned Server/Worker RSS, FD and DRM memory every 500 ms. Worker telemetry is opt-in
(--telemetry) because detailed mode adds measurable cost; run it separately from the timing rounds."""
import argparse
import datetime
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import statistics
import sys
import threading
import time
from urllib.parse import urlencode
import wave
import zipfile

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("m12_m11_probe", HERE / "m11_complex_probe.py")
m11 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m11)
m10 = m11.m10
save_json = m10.save_json
SETS = {"t01": m11.SAMPLES}
TEXTS = ("星空を", "流れる光")
TELEMETRY_PREFIX = "MAKO_PERFORMANCE_V1 "


def stats(values):
    ordered = sorted(values)
    if not ordered:
        return {"n": 0}
    return {"n": len(ordered), "medianMs": round(statistics.median(ordered), 3),
            "p95Ms": round(ordered[math.ceil(len(ordered) * .95) - 1], 3),
            "minMs": round(ordered[0], 3), "maxMs": round(ordered[-1], 3)}


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc)


def lrc_time(us):
    centis = us // 10_000
    return f"{centis // 6000:02d}:{centis // 100 % 60:02d}.{centis % 100:02d}"


def process_snapshot(pid):
    """Owned process memory/FD/DRM observation; never reads arguments or environment."""
    directory = Path(f"/proc/{pid}")
    try:
        ticks = (directory / "stat").read_text().rsplit(")", 1)[1].split()[19]
        name = (directory / "exe").resolve(strict=True).name
        status = (directory / "status").read_text()
        rss = next(int(line.split()[1]) * 1024 for line in status.splitlines() if line.startswith("VmRSS:"))
        fds = [entry.name for entry in (directory / "fd").iterdir()]
    except (OSError, StopIteration, IndexError):
        return None
    clients = {}
    for fd in fds:
        try:
            info = (directory / "fdinfo" / fd).read_text()
        except OSError:
            continue
        client, memory = None, {"vram": 0, "gtt": 0}
        for line in info.splitlines():
            if line.startswith("drm-client-id:"):
                client = line.split()[1]
            for kind in memory:
                if line.startswith(f"drm-memory-{kind}:"):
                    value, unit = line.split()[1:3]
                    memory[kind] = int(value) * (1024 if unit in ("KiB", "kB") else 1)
        if client is not None:
            clients[client] = memory
    role = {"MakoKaraGenerator.Backend.Server": "server", "MakoKaraGenerator.Backend.Worker": "worker"}.get(name, "other")
    return {"pid": pid, "startTicks": ticks, "role": role, "rssBytes": rss, "fdCount": len(fds),
            "gpuVramBytes": sum(item["vram"] for item in clients.values()) if clients else None,
            "gpuGttBytes": sum(item["gtt"] for item in clients.values()) if clients else None}


class ResourceSampler(threading.Thread):
    def __init__(self, probe, path):
        super().__init__(daemon=True)
        self.probe, self.path, self.halt, self.records = probe, path, threading.Event(), []

    def run(self):
        with self.path.open("x") as stream:
            while not self.halt.wait(.5):
                for _ in range(3):
                    try:
                        identities = list(self.probe.observed)
                        break
                    except RuntimeError:
                        identities = []
                now = time.monotonic_ns()
                for pid, ticks in identities:
                    snapshot = process_snapshot(pid)
                    if snapshot and snapshot["startTicks"] == ticks and snapshot["role"] != "other":
                        record = {"monotonicNs": now, "scenario": self.probe.current_scenario, **snapshot}
                        self.records.append(record)
                        stream.write(json.dumps(record) + "\n")
                stream.flush()

    def summary(self):
        result = {}
        for scenario in dict.fromkeys(record["scenario"] for record in self.records):
            rows = [record for record in self.records if record["scenario"] == scenario]
            roles = {}
            for role in ("server", "worker"):
                selected = [record for record in rows if record["role"] == role]
                if not selected:
                    continue
                by_tick = {}
                for record in selected:
                    by_tick.setdefault(record["monotonicNs"], []).append(record)
                totals = [sum(item["rssBytes"] for item in group) for group in by_tick.values()]
                vram = [sum(item["gpuVramBytes"] or 0 for item in group) for group in by_tick.values()]
                gtt = [sum(item["gpuGttBytes"] or 0 for item in group) for group in by_tick.values()]
                roles[role] = {"identities": len({(item["pid"], item["startTicks"]) for item in selected}),
                    "maxConcurrent": max(len(group) for group in by_tick.values()),
                    "rssFirstMiB": round(totals[0] / 2**20, 1), "rssLastMiB": round(totals[-1] / 2**20, 1),
                    "rssPeakMiB": round(max(totals) / 2**20, 1), "fdPeak": max(item["fdCount"] for item in selected),
                    "gpuVramPeakMiB": round(max(vram) / 2**20, 1), "gpuGttPeakMiB": round(max(gtt) / 2**20, 1)}
            result[scenario] = roles
        return result


class M12Probe(m11.M11ComplexProbe):
    def __init__(self, output, dist, args):
        super().__init__(output, dist, args)
        self.names = SETS.get(args.samples) or tuple(args.samples.split(","))
        self.results = []
        self.current_scenario = "setup"
        self.windows = []
        self.sequence = 0
        # 背景分析按内容缓存：同一背景只在首个工程完成时发一次 mediaFeaturesChanged，之后的工程直接取已完成结果。
        self.media_ready = False

    # ---- shared fixture ----
    def import_sample(self, name):
        """Controller samples keep the M11 path; graph packages become recipes through a resource draft and
        material packages fill the template's unsung/sung slots (first/last material export)."""
        directory = m10.ROOT / "samples" / "ScriptEffectsV2" / name
        raw = (directory / "manifest.json").read_bytes()
        manifest = json.loads(raw)
        if any(item["kind"] == "controller" for item in manifest["exports"]):
            return super().import_sample(name)
        files = {item["path"]: (directory / item["path"]).read_bytes() for item in manifest["files"]}
        self.check(all(hashlib.sha256(files[item["path"]]).hexdigest() == item["sha256"] for item in manifest["files"]),
            name + ": repository bytes match manifest")
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("manifest.json", raw)
            for path, data in files.items():
                package.writestr(path, data)
        asset = self.upload(name + "-package", "m12-" + name + ".mkfx", "application/octet-stream", archive.getvalue())
        inspected = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], name + ": package admitted")
        materials = [item["id"] for item in manifest["exports"] if item["kind"] == "material"]
        if materials:
            self.materials.extend({"kind": "package", "packageId": inspected["packageId"], "revision": inspected["packageRevision"],
                "exportId": export, "parametersJson": "{}"} for export in materials)
            return inspected["reference"], None
        export = next(item["id"] for item in manifest["exports"] if item["kind"] == "graph")
        draft = self.request(name + "-draft", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": export, "name": "M12 " + name})
        config = draft["configuration"]
        return inspected["reference"], {"recipeId": "m12-" + name.lower(), "name": name, "target": config["relativeTarget"],
            "packageId": config["packageId"], "revision": config["sourceRevision"], "exportId": config["exportId"],
            "parametersJson": config["parametersJson"], "enabled": True, "overlayLayer": config["overlayLayer"],
            "readScope": config["readScope"], "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"),
            "configurationId": None}

    def prepare_template(self):
        self.materials = []
        imported = [self.import_sample(name) for name in self.names]
        builtin = self.request("template-builtin", "GET", "/api/v1/template-libraries/builtin/templates")
        line = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request("template-detail", "GET", "/api/v1/template-libraries/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": line["templateId"]}))
        definition = dict(detail["definition"])
        properties = ["opacity"]
        if self.materials:
            definition["style"] = dict(definition["style"], unsungFill={"material": self.materials[0]},
                sungFill={"material": self.materials[-1]})
            properties += ["unsungColor", "sungColor"]
        definition.update(name="M12 " + "+".join(self.names), kind="line", properties=properties,
            packages=[reference for reference, _ in imported],
            effectRecipes=[recipe for _, recipe in imported if recipe is not None])
        library = self.request("template-personal", "GET", "/api/v1/template-libraries/personal/templates")
        self.template = self.request("template-create", "POST", "/api/v1/template-libraries/personal/commands", value={
            "expectedRevision": library["revision"], "metadata": self.metadata("template-create"),
            "command": {"type": "create", "definitionFormat": "full", "definition": definition}})

    def create_project(self, name, duration_us, period_us=1_250_000, length_us=1_150_000):
        """Lines every period across the whole duration, so every sought time has a live SDK2 line."""
        count = duration_us // period_us
        wait_media = "AdaptiveStroke" in self.names and not self.media_ready
        if wait_media:
            # 从当前游标等待本工程的媒体完成事件，避免早期事件滑出重放窗口时 resync。
            state = self.request(name + "-event-cursor", "GET", "/api/v1/clients/" + self.client + "/state")
            cursor = state["eventCursor"].rsplit(":", 1)[1]
        stream = io.BytesIO()
        with wave.open(stream, "wb") as audio:
            audio.setnchannels(1); audio.setsampwidth(2); audio.setframerate(8000)
            audio.writeframes(b"\0\0" * (duration_us * 8000 // 1_000_000))
        asset = self.upload(name + "-audio", name + ".wav", "audio/wav", stream.getvalue())
        text = "".join(f"[{lrc_time(index * period_us)}]{TEXTS[index % 2]}\n" for index in range(count))
        lyrics = self.upload(name + "-lyrics", name + ".lrc", "text/plain", text.encode())
        created = self.request(name + "-project", "POST", "/api/v1/project-sessions", status=201, value={
            "name": "M12 " + name, "audioAssetId": asset["assetId"], "lyricsAssetId": lyrics["assetId"],
            "composition": {"canvasWidth": self.args.width, "canvasHeight": self.args.height,
                "frameRateNumerator": 30, "frameRateDenominator": 1, "outputDurationUs": duration_us}})
        self.active_project = {"path": "/api/v1/project-sessions/" + created["projectSessionId"], "revision": created["projectRevision"]}
        lines = self.request(name + "-lines", "GET", self.active_project["path"] + "/lyrics/lines?branchId=primary")["lines"]
        self.check(len(lines) == count and all(line["tokens"] for line in lines), name + ": every generated line has tokens")
        changes = []
        for index, line in enumerate(lines):
            start, end = index * period_us, index * period_us + length_us
            target = {"branchId": "primary", "lineId": line["id"]}
            self.send(name + "-time-" + str(index), {"type": "lyrics.setTimeRange", "target": target, "startUs": str(start), "endUs": str(end)})
            tokens = len(line["tokens"])
            changes += [{"target": {**target, "tokenId": token["id"]}, "startUs": str(start + length_us * at // tokens),
                "endUs": str(start + length_us * (at + 1) // tokens)} for at, token in enumerate(line["tokens"])]
        self.send(name + "-tokens", {"type": "lyrics.setTokenTimes", "changes": changes})
        self.send(name + "-apply-script", {"type": "presetApplications.apply", "templateLibraryId": "personal",
            "templateId": self.template["templateId"], "templateRevision": self.template["revision"],
            "targets": [{"kind": "line", "branchId": "primary", "lineId": line["id"]} for line in lines]})
        source = {"kind": "project", "sourceId": created["projectSessionId"], "sourceRevision": self.active_project["revision"]}
        # 预览尺寸受 1280x720 限制；导出可测更大画布，预览只用于等待媒体输入，按比例缩入上限。
        scale = min(1.0, 1280 / self.args.width, 720 / self.args.height)
        preview = self.request(name + "-preview", "POST", "/api/v1/preview-sessions", status=201, value={"source": source,
            "size": {"width": max(64, int(self.args.width * scale)), "height": max(64, int(self.args.height * scale))},
            "alphaMode": "opaque", "purpose": "preview",
            "renderBackend": "gpu"})
        self.active_preview = preview.get("session", preview)
        self.sequence = 0
        if wait_media:
            # Measure against the completed media input, not the transient analysing state.
            try:
                self.wait_media_event(cursor, time.monotonic() + 90)
                self.refresh_input(name + "-media-refresh")
                self.media_ready = True
            finally:
                if self.sse is not None:
                    self.sse.close()
                    self.sse = None
        return len(lines)

    def refresh_input(self, name):
        # 导出画布可能大于预览上限，预览已按比例缩小；重捕获沿用会话实际尺寸。
        size = (self.args.width, self.args.height)
        self.args.width, self.args.height = self.active_preview["size"]["width"], self.active_preview["size"]["height"]
        try:
            super().refresh_input(name)
        finally:
            self.args.width, self.args.height = size

    def next_sequence(self):
        self.sequence += 1
        return self.sequence

    def begin(self, scenario):
        self.current_scenario = scenario
        self.windows.append({"scenario": scenario, "startUtc": utc_now().isoformat()})

    def end(self, result):
        self.windows[-1]["endUtc"] = utc_now().isoformat()
        self.results.append(result)
        save_json(self.output / (result["name"] + "-result.json"), result)
        self.current_scenario = "between"

    # ---- seek ----
    def seek(self, kind):
        self.begin("seek-" + kind)
        result = {"name": "seek-" + kind, "status": "running"}
        try:
            far = {"near": None, "180s": 180_000_000, "360s": 360_000_000}[kind]
            duration = 8_000_000 if far is None else far + 1_000_000
            lines = self.create_project(kind, duration, *((1_250_000, 1_150_000) if far is None else (3_000_000, 2_600_000)))
            pattern = [500_000, 1_000_000, 2_000_000, 3_000_000] if far is None else [500_000, far, 1_000_000, far]
            seen, hashes, samples = set(), {}, []
            for index in range(self.args.count):
                time_us = pattern[index % len(pattern)]
                sample = self.sample(f"seek-{kind}-{index + 1:02d}", self.next_sequence(), time_us, hashes)
                samples.append({"timeUs": time_us, "repeat": time_us in seen, "acceptedMs": sample["acceptedMs"],
                    "readyMs": sample["readyMs"], "downloadCompleteMs": sample["downloadCompleteMs"]})
                seen.add(time_us)
            result.update(status="passed", lines=lines, durationUs=duration, samples=samples,
                readyMs=stats([item["readyMs"] for item in samples]),
                readyFirstMs=stats([item["readyMs"] for item in samples if not item["repeat"]]),
                readyRepeatMs=stats([item["readyMs"] for item in samples if item["repeat"]]),
                downloadCompleteMs=stats([item["downloadCompleteMs"] for item in samples]))
        except Exception as error:
            result.update(status="failed", failure=self.failure(error))
        finally:
            try:
                self.close_resources("seek-" + kind)
            except Exception as error:
                result.update(status="failed", cleanupFailure=self.failure(error))
            self.end(result)

    # ---- export ----
    def wait_job(self, name, job_id, deadline):
        count = 0
        while time.monotonic() < deadline:
            count += 1
            current = self.request(f"{name}-poll-{count}", "GET", "/api/v1/jobs/" + job_id)
            if current["state"] in ("succeeded", "failed", "cancelled"):
                return current
            time.sleep(.1)
        raise TimeoutError(name + ": job not terminal within deadline")

    def export(self):
        self.begin("export")
        result = {"name": "export", "status": "running", "rounds": []}
        try:
            duration = self.args.export_seconds * 1_000_000
            result["lines"] = self.create_project("export", duration)
            frames = self.args.export_seconds * 30
            for round_index in range(1, self.args.export_rounds + 1):
                name = f"export-r{round_index}"
                started = time.monotonic_ns()
                accepted = self.request(name, "POST", self.active_project["path"] + "/export-jobs", status=202, value={
                    "expectedRevision": self.active_project["revision"], "format": "opaqueMp4", "includeAudio": False,
                    "renderBackend": "gpu"})
                terminal = self.wait_job(name, accepted["jobId"], time.monotonic() + self.args.timeout_seconds)
                wall = (time.monotonic_ns() - started) / 1e6
                save_json(self.output / (name + "-terminal.json"), self.redact(terminal))
                self.check(terminal["state"] == "succeeded", name + ": export succeeded")
                result["rounds"].append({"round": round_index, "wallMs": round(wall, 1), "frames": frames,
                    "framesPerSecond": round(frames / (wall / 1000), 2), "msPerFrame": round(wall / frames, 2)})
            result.update(status="passed", width=self.args.width, height=self.args.height, seconds=self.args.export_seconds,
                wallMs=stats([item["wallMs"] for item in result["rounds"]]))
        except Exception as error:
            result.update(status="failed", failure=self.failure(error))
        finally:
            try:
                self.close_resources("export")
            except Exception as error:
                result.update(status="failed", cleanupFailure=self.failure(error))
            self.end(result)

    # ---- author ----
    @staticmethod
    def draft_path(draft):
        return "/api/v1/drafts/" + draft["draftId"]

    def read_draft(self, name, draft):
        return self.request(name, "GET", self.draft_path(draft))

    def author_source(self, draft):
        options = draft["sampleOptions"]
        sample = next(item for item in options["samples"] if item["sampleId"] == options["defaultSampleId"])
        selection = {"sampleId": sample["sampleId"], "targetKey": sample["targets"][0]["key"]}
        if options["materialSlots"]:
            selection["materialSlot"] = options["materialSlots"][0]
        return {"kind": "authorDraft", "sourceId": draft["draftId"], "sourceRevision": draft["draftRevision"],
            "authorSourceRevision": draft["configuration"]["sourceRevision"], "templateSample": selection}

    def author_input(self, name, draft):
        preview = self.active_preview
        updated = self.request(name, "PUT", "/api/v1/preview-sessions/" + preview["previewId"] + "/input", value={
            "expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": preview["source"]["sourceRevision"],
            "source": self.author_source(draft), "size": {"width": self.args.width, "height": self.args.height},
            "alphaMode": "opaque", "metadata": self.metadata(name)})
        session = updated.get("session", updated)
        self.check(session["source"]["sourceRevision"] == draft["draftRevision"]
            and session["source"]["authorSourceRevision"] == draft["configuration"]["sourceRevision"], name + ": preview pins new draft input")
        self.active_preview = session

    def author_frame(self, name, row, started):
        sample = self.sample(name, self.next_sequence(), 3_000_000, {})
        row.update(frameReadyMs=round(sample["readyMs"], 3),
            totalMs=round((sample["requestDispatchedMonotonicNs"] + sample["readyMs"] * 1e6 - started) / 1e6, 3),
            rgbaSha256=sample["rgbaSha256"])
        return row

    def adopt(self, name, draft, source):
        response = self.request(name, "POST", self.draft_path(draft) + "/adopt-source-revision",
            value={"expectedDraftRevision": draft["draftRevision"], "sourceRevision": source})
        self.check(response["adoptedSourceRevision"] == source, name + ": adopted exact source")
        return self.read_draft(name + "-draft", draft)

    def author(self):
        self.begin("author-setup")
        result = {"name": "author", "status": "running", "parameter": [], "source": [], "adoptCompiled": []}
        try:
            self.import_sample("WordEmphasis")
            draft = self.drafts["WordEmphasis"]
            preview = self.request("author-preview", "POST", "/api/v1/preview-sessions", status=201, value={
                "source": self.author_source(draft), "size": {"width": self.args.width, "height": self.args.height},
                "alphaMode": "opaque", "purpose": "preview", "previewKind": "authorDraft", "committable": False, "renderBackend": "gpu"})
            self.active_preview = preview.get("session", preview)
            self.sequence = 0
            self.author_frame("author-initial", {}, time.monotonic_ns())
            self.windows[-1]["endUtc"] = utc_now().isoformat()

            self.begin("author-parameter")
            parameters = json.loads(draft["configuration"]["parametersJson"])
            for index in range(self.args.count):
                parameters["peakScale"] = round(1.3 + 0.1 * index, 2)
                name = f"author-parameter-{index + 1:02d}"
                started = time.monotonic_ns()
                configuration = {key: draft["configuration"].get(key) for key in ("name", "packageId", "sourceRevision", "exportId",
                    "parametersJson", "relativeTarget", "readScope", "branchInput", "fixedSeed", "overlayLayer")}
                configuration["parametersJson"] = json.dumps(parameters, separators=(",", ":"))
                draft = self.request(name + "-save", "PUT", self.draft_path(draft) + "/configuration", value={
                    "expectedDraftRevision": draft["draftRevision"], "configuration": configuration, "metadata": self.metadata(name)})
                saved = time.monotonic_ns()
                self.author_input(name + "-input", draft)
                refreshed = time.monotonic_ns()
                result["parameter"].append(self.author_frame(name, {"index": index + 1, "peakScale": parameters["peakScale"],
                    "saveMs": round((saved - started) / 1e6, 3), "inputMs": round((refreshed - saved) / 1e6, 3)}, started))
            self.windows[-1]["endUtc"] = utc_now().isoformat()

            self.begin("author-source")
            files = self.request("author-files", "GET", self.draft_path(draft) + "/package-files?" + urlencode({
                "expectedDraftRevision": draft["draftRevision"], "sourceRevision": draft["currentSourceRevision"]}))
            editable = [item for item in files["files"] if item["editable"] and item["path"] != "manifest.json"]
            self.check(any(item["role"] == "csharp" for item in editable), "author: package exposes editable C# source")
            compiled = []
            for index in range(self.args.source_count):
                name = f"author-source-{index + 1:02d}"
                started = time.monotonic_ns()
                texts = [{"path": item["path"], "text": item["text"] + (f"\n// M12 source edit {index + 1}\n" if item["role"] == "csharp" else "")}
                    for item in editable]
                draft = self.request(name + "-save", "PUT", self.draft_path(draft) + "/package-files", value={
                    "expectedDraftRevision": draft["draftRevision"], "manifestJson": files["manifestJson"], "textFiles": texts,
                    "metadata": self.metadata(name)})
                source = draft["currentSourceRevision"]
                saved = time.monotonic_ns()
                report = self.request(name + "-compile", "POST", self.draft_path(draft) + "/compilation-jobs", value={
                    "expectedDraftRevision": draft["draftRevision"], "sourceRevision": source, "executionMode": "sync"})
                self.check(report["succeeded"] and report["sourceRevision"] == source, name + ": compiled edited source")
                compiled_at = time.monotonic_ns()
                draft = self.adopt(name + "-adopt", self.read_draft(name + "-after-compile", draft), source)
                adopted = time.monotonic_ns()
                self.author_input(name + "-input", draft)
                refreshed = time.monotonic_ns()
                compiled.append(source)
                result["source"].append(self.author_frame(name, {"index": index + 1, "saveMs": round((saved - started) / 1e6, 3),
                    "compileMs": round((compiled_at - saved) / 1e6, 3), "adoptMs": round((adopted - compiled_at) / 1e6, 3),
                    "inputMs": round((refreshed - adopted) / 1e6, 3)}, started))
            self.windows[-1]["endUtc"] = utc_now().isoformat()

            self.begin("author-adopt-compiled")
            # Alternate between two revisions this process already compiled: no compiler, artifact reuse only.
            pair = compiled[-2:]
            for index in range(self.args.count):
                name = f"author-adopt-{index + 1:02d}"
                target = pair[index % 2]
                started = time.monotonic_ns()
                draft = self.adopt(name, draft, target)
                adopted = time.monotonic_ns()
                self.author_input(name + "-input", draft)
                refreshed = time.monotonic_ns()
                result["adoptCompiled"].append(self.author_frame(name, {"index": index + 1, "source": target,
                    "adoptMs": round((adopted - started) / 1e6, 3), "inputMs": round((refreshed - adopted) / 1e6, 3)}, started))
            for key in ("parameter", "source", "adoptCompiled"):
                result[key + "Summary"] = {field: stats([row[field] for row in result[key]]) for field in
                    ("totalMs", "frameReadyMs", "inputMs", *(("saveMs",) if key != "adoptCompiled" else ()),
                     *(("compileMs", "adoptMs") if key == "source" else ()), *(("adoptMs",) if key == "adoptCompiled" else ()))}
            result["status"] = "passed"
        except Exception as error:
            result.update(status="failed", failure=self.failure(error))
        finally:
            try:
                self.close_resources("author")
            except Exception as error:
                result.update(status="failed", cleanupFailure=self.failure(error))
            self.end(result)

    def run(self):
        deployment = json.loads((self.dist / "deployment.json").read_text())
        save_json(self.output / "provenance.json", {"deployment": {key: deployment.get(key) for key in (
            "version", "sourceCommit", "sourceDirty")}, "probeSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "parameters": {key: str(value) for key, value in vars(self.args).items()},
            "telemetryEnvironment": {key: value for key, value in os.environ.items() if key.startswith("MAKO_PERFORMANCE_")}})
        self.stage = "start"
        self.start("m12")
        self.stage = "connect"
        self.connect("m12")
        self.check(self.current_ready["version"] == deployment["version"], "running version matches deployment manifest")
        if self.args.mode != "author":
            self.stage = "prepare-template"
            self.prepare_template()
            width, height = self.args.width, self.args.height
            # Light still background: AdaptiveStroke analyses it; every sample composes over a real image.
            self.background = self.upload("m12-background", "m12-light.png", "image/png", m10.solid_png(width, height, (235, 235, 240)))
        if self.args.mode == "seek":
            for kind in self.args.scenarios.split(","):
                self.stage = "seek-" + kind
                self.seek(kind)
        elif self.args.mode == "export":
            self.stage = "export"
            self.export()
        else:
            self.stage = "author"
            self.author()


def telemetry(output, windows):
    """Parse forwarded Worker telemetry from this run's own server logs; fixed numeric fields only."""
    events = []
    for log in sorted((output / "profile" / "runs").glob("run-*/server.log")):
        for line in log.read_text(errors="replace").splitlines():
            at = line.find(TELEMETRY_PREFIX)
            if at >= 0:
                try:
                    events.append(json.loads(line[at + len(TELEMETRY_PREFIX):]))
                except json.JSONDecodeError:
                    pass
            elif line.startswith("worker_telemetry_drain"):
                events.append({"Kind": "drain", "Line": line.strip()[:300]})
    with (output / "telemetry.jsonl").open("x") as stream:
        for event in events:
            stream.write(json.dumps(event) + "\n")
    frames = [event for event in events if event.get("Kind") == "frame"]
    result = {}
    for window in windows:
        start = datetime.datetime.fromisoformat(window["startUtc"])
        end = datetime.datetime.fromisoformat(window.get("endUtc") or utc_now().isoformat())
        selected = [event for event in frames if start <= datetime.datetime.fromisoformat(event["TimestampUtc"]) <= end]
        groups = {}
        for event in selected:
            groups.setdefault(f"{event['Width']}x{event['Height']}", []).append(event)
        result[window["scenario"]] = {size: {"frameEvents": len(rows), "wallMs": stats([row["WallMs"] for row in rows]),
            **{key: stats([row[key] for row in rows]) for key in ("PrepareMs", "ScriptMs", "RasterMs", "ReadbackMs", "MaterialMs", "GraphMs", "ParticleMs")},
            "errors": sum(row["Errors"] for row in rows), "cancelled": sum(row["Cancelled"] for row in rows)}
            for size, rows in groups.items()}
    result["drain"] = [event["Line"] for event in events if event.get("Kind") == "drain"]
    return result


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--mode", choices=("seek", "export", "author"), required=True)
    parser.add_argument("--samples", default="t01", help="t01 or comma-separated ScriptEffectsV2 sample names")
    parser.add_argument("--scenarios", default="near,180s,360s")
    parser.add_argument("--width", type=int, default=1280)
    parser.add_argument("--height", type=int, default=720)
    parser.add_argument("--count", type=int, default=20)
    parser.add_argument("--source-count", type=int, default=10)
    parser.add_argument("--export-seconds", type=int, default=10)
    parser.add_argument("--export-rounds", type=int, default=3)
    parser.add_argument("--timeout-seconds", type=float, default=300)
    parser.add_argument("--poll-ms", type=int, default=10)
    parser.add_argument("--telemetry", choices=("off", "summary", "detailed"), default="off")
    # Host 每 Worker 每秒只转发前 10 行；逐帧事件应低于该速率，否则保留的样本偏向慢帧。
    parser.add_argument("--detail-every", type=int, default=1)
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    parser.add_argument("--ffmpeg", default="/usr/bin/ffmpeg")
    args = parser.parse_args()
    if not (64 <= args.width <= 3840 and 64 <= args.height <= 2160 and 2 <= args.count <= 100 and 2 <= args.source_count <= 50
            and 1 <= args.export_seconds <= 600 and 1 <= args.export_rounds <= 10 and 10 <= args.poll_ms <= 1000
            and 1 <= args.detail_every <= 1000):
        parser.error("arguments outside finite probe bounds")
    if any(kind not in ("near", "180s", "360s") for kind in args.scenarios.split(",")):
        parser.error("scenarios must be near, 180s or 360s")
    args.require_reuse, args.backend, args.background, args.scenario = False, "gpu", None, args.mode
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    return args


def main():
    args = arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    if args.icd.is_file():
        os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = str(args.icd)
    for key in [key for key in os.environ if key.startswith("MAKO_PERFORMANCE_")]:
        del os.environ[key]
    os.environ["MAKO_PERFORMANCE_MODE"] = args.telemetry
    if args.telemetry != "off":
        os.environ.update(MAKO_PERFORMANCE_INTERVAL_SECONDS="1", MAKO_PERFORMANCE_DETAIL_EVERY=str(args.detail_every),
                          MAKO_PERFORMANCE_MAX_EVENTS_PER_SECOND="100")
    probe = M12Probe(args.output, args.dist, args)
    sampler = ResourceSampler(probe, args.output / "resources.jsonl")
    sampler.start()
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
            probe.stop("m12")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        sampler.halt.set()
        sampler.join()
        summary = {"status": "passed" if not failures and probe.results and all(r["status"] == "passed" for r in probe.results) else "failed",
            "mode": args.mode, "samples": list(probe.names), "size": [args.width, args.height], "telemetryMode": args.telemetry,
            "detailEvery": args.detail_every if args.telemetry == "detailed" else None,
            "failures": failures, "results": probe.results, "windows": probe.windows, "resources": sampler.summary(),
            "scope": "Real HTTP + GPU Worker on this machine; poll resolution and JSON evidence I/O included in ready times; "
                     "no UI-visible, desktop shared-texture or display timing."}
        try:
            summary["telemetry"] = telemetry(args.output, probe.windows) if args.telemetry != "off" else None
        except Exception as error:
            summary["telemetryFailure"] = probe.failure(error, "telemetry")
        save_json(args.output / "summary.json", summary)
        try:
            probe.scan_credentials()
        except Exception as error:
            summary["status"] = "failed"
            summary["credentialFailure"] = probe.failure(error, "credential-scan")
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "mode": args.mode,
            "results": [{key: result.get(key) for key in ("name", "status")} for result in probe.results]}, ensure_ascii=False))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
