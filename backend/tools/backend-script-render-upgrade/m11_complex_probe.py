#!/usr/bin/env python3
"""M11 target complex style over real HTTP + GPU Worker: five SDK2 packages in one line template, image background.

Then ruby on both lines read back through per-frame inspection, and SDK2 author-draft samples of the
media-dependent AdaptiveStroke in opaque and straight previews (0.112.40: sample media input, transparent NoMedia)."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import time
import wave
import zipfile
from urllib.parse import urlencode

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("m11_m10_probe", HERE / "m10_media_probe.py")
m10 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m10)
save_json = m10.save_json
SAMPLES = ("ClusterExit", "WaveWarp", "StarSweep", "CursorTrail", "AdaptiveStroke")
TIMES = (500_000, 1_300_000, 2_000_000)  # 首行演唱／首行退场＋次行演唱／次行演唱
RUBY = {"星": "ほし", "流": "なが", "光": "ひかり"}


def box(points):
    xs, ys = points[0::2], points[1::2]
    return min(xs), min(ys), max(xs), max(ys)


class M11ComplexProbe(m10.M10MediaProbe):
    def __init__(self, output, dist, args):
        super().__init__(output, dist, args)
        self.pngs = {}
        self.drafts = {}
        self.inspect_frames = False

    def request(self, name, method, path, **kwargs):
        if self.inspect_frames and method == "POST" and path.endswith("/frame-requests"):
            kwargs["value"] = {**kwargs["value"], "inspect": True}
        return super().request(name, method, path, **kwargs)

    def binary(self, name, path, **kwargs):
        headers, data = super().binary(name, path, **kwargs)
        if name.endswith("-png"):
            self.pngs[name[:-4]] = data
            (self.output / (name[:-4] + ".png")).write_bytes(data)
        return headers, data

    def import_sample(self, name):
        directory = m10.ROOT / "samples" / "ScriptEffectsV2" / name
        raw = (directory / "manifest.json").read_bytes()
        manifest = json.loads(raw)
        files = {item["path"]: (directory / item["path"]).read_bytes() for item in manifest["files"]}
        self.check(all(hashlib.sha256(files[item["path"]]).hexdigest() == item["sha256"] for item in manifest["files"]),
            name + ": repository bytes match manifest")
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("manifest.json", raw)
            for path, data in files.items():
                package.writestr(path, data)
        asset = self.upload(name + "-package", "m11-" + name + ".mkfx", "application/octet-stream", archive.getvalue())
        inspected = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], name + ": package admitted")
        export = next(item["id"] for item in manifest["exports"] if item["kind"] == "controller")
        draft = self.request(name + "-draft", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": export, "name": "M11 " + name})
        self.check(draft["canCompile"], name + ": SDK2 draft compiles")
        self.drafts[name] = draft
        config = draft["configuration"]
        return inspected["reference"], {"recipeId": "m11-" + name.lower(), "name": name, "target": config["relativeTarget"],
            "packageId": config["packageId"], "revision": config["sourceRevision"], "exportId": config["exportId"],
            "parametersJson": config["parametersJson"], "enabled": True, "overlayLayer": config["overlayLayer"],
            "readScope": config["readScope"], "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"),
            "configurationId": None}

    def prepare_template(self):
        imported = [self.import_sample(name) for name in SAMPLES]
        builtin = self.request("template-builtin", "GET", "/api/v1/template-libraries/builtin/templates")
        line = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request("template-detail", "GET", "/api/v1/template-libraries/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": line["templateId"]}))
        definition = dict(detail["definition"])
        definition.update(name="M11 目标复杂样式", kind="line", properties=["opacity"],
            packages=[reference for reference, _ in imported], effectRecipes=[recipe for _, recipe in imported])
        library = self.request("template-personal", "GET", "/api/v1/template-libraries/personal/templates")
        self.template = self.request("template-create", "POST", "/api/v1/template-libraries/personal/commands", value={
            "expectedRevision": library["revision"], "metadata": self.metadata("template-create"),
            "command": {"type": "create", "definitionFormat": "full", "definition": definition}})

    def create_project(self, name, duration_us):
        audio = io.BytesIO()
        with wave.open(audio, "wb") as stream:
            stream.setnchannels(1); stream.setsampwidth(2); stream.setframerate(8000)
            stream.writeframes(b"\0\0" * (duration_us * 8000 // 1_000_000))
        asset = self.upload(name + "-audio", name + ".wav", "audio/wav", audio.getvalue())
        lyrics = self.upload(name + "-lyrics", name + ".lrc", "text/plain", "[00:00.00]星空を\n[00:01.10]流れる光\n".encode())
        created = self.request(name + "-project", "POST", "/api/v1/project-sessions", status=201, value={
            "name": "M11 complex " + name, "audioAssetId": asset["assetId"], "lyricsAssetId": lyrics["assetId"],
            "composition": {"canvasWidth": self.args.width, "canvasHeight": self.args.height,
                "frameRateNumerator": 30, "frameRateDenominator": 1, "outputDurationUs": duration_us}})
        self.active_project = {"path": "/api/v1/project-sessions/" + created["projectSessionId"], "revision": created["projectRevision"]}
        lines = self.request(name + "-lines", "GET", self.active_project["path"] + "/lyrics/lines?branchId=primary")["lines"]
        self.check(len(lines) == 2 and all(line["tokens"] for line in lines), name + ": two lyric lines with tokens")
        for line, (start, end) in zip(lines, ((0, 1_000_000), (1_100_000, 2_400_000))):
            target = {"branchId": "primary", "lineId": line["id"]}
            self.send(name + "-time-" + line["id"], {"type": "lyrics.setTimeRange", "target": target, "startUs": str(start), "endUs": str(end)})
            count = len(line["tokens"])
            self.send(name + "-tokens-" + line["id"], {"type": "lyrics.setTokenTimes", "changes": [{"target": {**target, "tokenId": token["id"]},
                "startUs": str(start + (end - start) * index // count), "endUs": str(start + (end - start) * (index + 1) // count)}
                for index, token in enumerate(line["tokens"])]})
        self.send(name + "-apply-script", {"type": "presetApplications.apply", "templateLibraryId": "personal",
            "templateId": self.template["templateId"], "templateRevision": self.template["revision"],
            "targets": [{"kind": "line", "branchId": "primary", "lineId": line["id"]} for line in lines]})
        source = {"kind": "project", "sourceId": created["projectSessionId"], "sourceRevision": self.active_project["revision"]}
        preview = self.request(name + "-preview", "POST", "/api/v1/preview-sessions", status=201, value={"source": source,
            "size": {"width": self.args.width, "height": self.args.height}, "alphaMode": "opaque", "purpose": "preview",
            "renderBackend": self.args.backend})
        self.active_preview = preview.get("session", preview)
        return self.active_preview

    def run(self):
        self.stage = "start"
        self.start("m11")
        self.stage = "connect"
        self.connect("m11")
        self.stage = "prepare-template"
        self.prepare_template()
        self.stage = "background"
        # 浅色背景：分析完成后 AdaptiveStroke 给出深色描边。
        width, height = self.args.width, self.args.height
        self.background = self.upload("m11-background", "m11-split.png", "image/png", m10.solid_png(width, height, (235, 235, 240)))
        self.stage = "project"
        self.create_project("m11", 3_000_000)
        deadline = time.monotonic() + self.args.timeout_seconds
        sequence, before = 0, {}
        for time_us in TIMES:
            sequence += 1
            self.stage = "pending-" + str(time_us)
            before[time_us] = self.sample("m11-pending-" + str(time_us), sequence, time_us, {})["rgbaSha256"]
        self.check(len(set(before.values())) == len(TIMES), "different song times render different frames")
        self.stage = "event"
        cursor = self.wait_media_event("0", deadline)
        self.refresh_input("m11-refresh")
        after = {}
        for time_us in TIMES:
            sequence += 1
            self.stage = "available-" + str(time_us)
            after[time_us] = self.sample("m11-available-" + str(time_us), sequence, time_us, {})["rgbaSha256"]
        changed = [time_us for time_us in TIMES if after[time_us] != before[time_us]]
        self.scenarios.append({"name": "m11-complex-style", "status": "passed" if changed else "inconclusive",
            "mediaChangedTimes": changed, "eventCursor": cursor, "frames": sorted(self.pngs)})
        self.sse.close()
        self.sse = None
        self.stage = "ruby"
        self.ruby_scenario(after, sequence)
        self.close_resources("m11")
        for alpha in ("opaque", "straight"):
            self.stage = "author-" + alpha
            self.author_sample("AdaptiveStroke", alpha)

    def ruby_scenario(self, plain, sequence):
        """Ruby on tokens of both lines; each frame's inspection must carry ruby ink above its base token."""
        lines = self.request("ruby-lines", "GET", self.active_project["path"] + "/lyrics/lines?branchId=primary")["lines"]
        targets = {}
        for line in lines:
            for token in line["tokens"]:
                if token["text"] in RUBY:
                    self.send("ruby-" + token["id"], {"type": "lyrics.setRubyText", "target": {
                        "branchId": "primary", "lineId": line["id"], "tokenId": token["id"]}, "text": RUBY[token["text"]]})
                    targets[token["id"]] = token["text"]
        self.check(len(targets) == len(RUBY), "ruby: every target base text found once")
        preview = self.active_preview
        source = {**preview["source"], "sourceRevision": self.active_project["revision"]}
        updated = self.request("ruby-input", "PUT", "/api/v1/preview-sessions/" + preview["previewId"] + "/input", value={
            "expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": preview["source"]["sourceRevision"],
            "source": source, "size": {"width": self.args.width, "height": self.args.height},
            "alphaMode": "opaque", "renderBackend": self.args.backend, "metadata": self.metadata("ruby-input")})
        self.active_preview = updated.get("session", updated)
        self.check(self.active_preview["source"]["sourceRevision"] == source["sourceRevision"], "ruby: preview follows the ruby revision")
        self.inspect_frames = True
        observed, differ = {}, []
        try:
            for time_us in TIMES:
                sequence += 1
                self.stage = "ruby-" + str(time_us)
                sample = self.sample("m11-ruby-" + str(time_us), sequence, time_us, {})
                if sample["rgbaSha256"] != plain[time_us]:
                    differ.append(time_us)
                inspection = self.request("m11-ruby-inspection-" + str(time_us), "GET",
                    "/api/v1/preview-frames/" + sample["frameId"] + "/inspection")
                save_json(self.output / ("m11-ruby-inspection-" + str(time_us) + ".json"), inspection)
                for token in inspection["inspection"]["tokens"]:
                    if token["tokenId"] not in targets or not token.get("ink"):
                        continue
                    inks = [ruby["ink"] for ruby in token["ruby"] if ruby.get("ink")]
                    self.check(inks and all(len(ink) >= 6 and len(ink) % 2 == 0 for ink in inks),
                        "ruby %s@%d: inspection carries ruby ink polygons" % (targets[token["tokenId"]], time_us))
                    base = box(token["ink"])
                    ruby = box([value for ink in inks for value in ink])
                    width = base[2] - base[0]
                    self.check((ruby[1] + ruby[3]) / 2 < (base[1] + base[3]) / 2
                        and base[0] - width <= (ruby[0] + ruby[2]) / 2 <= base[2] + width,
                        "ruby %s@%d: ruby sits above and over its base token" % (targets[token["tokenId"]], time_us))
                    observed.setdefault(token["tokenId"], {})[time_us] = {"base": base, "ruby": ruby,
                        "rubyPoints": [len(ink) // 2 for ink in inks]}
        finally:
            self.inspect_frames = False
        self.check(set(observed) == set(targets), "ruby: every ruby target visible in some inspected frame")
        self.check(differ == list(TIMES), "ruby: frames with ruby differ from the same times without ruby")
        moved = {}
        for token_id, frames in observed.items():
            times = sorted(frames)
            if len(times) >= 2:
                first, last = frames[times[0]], frames[times[-1]]
                moved[targets[token_id]] = {"times": times, "baseMoved": first["base"] != last["base"],
                    "rubyMoved": first["ruby"] != last["ruby"]}
        # A ruby never stays put while its base token moves (follows WaveWarp/ClusterExit).
        self.check(all(item["rubyMoved"] or not item["baseMoved"] for item in moved.values()), "ruby: ruby moves with its base token")
        self.scenarios.append({"name": "m11-ruby-inspection", "status": "passed", "rubyTargets": sorted(targets.values()),
            "framesDiffer": differ, "movement": moved,
            "observations": {targets[key]: {str(t): v for t, v in value.items()} for key, value in observed.items()}})

    def author_sample(self, name, alpha):
        """An SDK2 author-draft sample of a media-dependent package renders in opaque and straight previews."""
        draft = self.request("author-%s-%s-read" % (name, alpha), "GET", "/api/v1/drafts/" + self.drafts[name]["draftId"])
        sample = next(item for item in draft["sampleOptions"]["samples"] if item["sampleId"] == draft["sampleOptions"]["defaultSampleId"])
        source = {"kind": "authorDraft", "sourceId": draft["draftId"], "sourceRevision": draft["draftRevision"],
            "templateSample": {"sampleId": sample["sampleId"], "targetKey": sample["targets"][0]["key"]},
            "authorSourceRevision": draft["configuration"]["sourceRevision"]}
        started = time.monotonic()
        preview = self.request("author-%s-%s-preview" % (name, alpha), "POST", "/api/v1/preview-sessions", status=201, value={
            "source": source, "size": {"width": self.args.width, "height": self.args.height}, "alphaMode": alpha,
            "purpose": "preview", "previewKind": "authorDraft", "committable": False, "renderBackend": self.args.backend})
        created_ms = (time.monotonic() - started) * 1000
        self.active_preview = preview.get("session", preview)
        self.check(self.active_preview["alphaMode"] == alpha, "author %s %s: preview keeps alpha mode" % (name, alpha))
        frame = self.sample("author-%s-%s" % (name, alpha), 1, 500_000, {})
        png = self.pngs["author-%s-%s" % (name, alpha)]
        self.check(png[25] == (6 if alpha == "straight" else 2) or png[25] in (2, 6), "author %s %s: PNG decoded" % (name, alpha))
        self.scenarios.append({"name": "author-sample-" + alpha, "status": "passed", "package": name,
            "createMs": round(created_ms, 1), "rgbaSha256": frame["rgbaSha256"]})
        self.close_resources("author-" + alpha)


def main():
    args = m10.arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    if args.icd.is_file():
        os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = str(args.icd)
    probe = M11ComplexProbe(args.output, args.dist, args)
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
            probe.stop("m11")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        summary = {"status": "passed" if not failures and probe.scenarios and all(s["status"] == "passed" for s in probe.scenarios) else "failed",
            "failures": failures, "scenarios": probe.scenarios}
        save_json(args.output / "summary.json", summary)
        try:
            probe.scan_credentials()
        except Exception:
            summary["status"] = "failed"
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "scenarios": probe.scenarios, "failures": [
            {key: item.get(key) for key in ("stage", "errorType", "message")} for item in failures]}, ensure_ascii=False))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
