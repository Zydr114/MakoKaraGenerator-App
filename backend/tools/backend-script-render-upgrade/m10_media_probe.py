#!/usr/bin/env python3
"""M10 real HTTP media-feature probe: image or video background -> Host analysis -> event -> same-revision input refresh.

Without --background a generated white PNG is used; --background takes a real still or video file.
"""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import struct
import sys
import time
import zipfile
import zlib
from urllib.parse import urlencode
import urllib.request

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SPEC = importlib.util.spec_from_file_location("m10_seek_probe", HERE / "probe.py")
base = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(base)
save_json = base.save_json
SAMPLE = ROOT / "samples" / "ScriptEffectsV2" / "AdaptiveStroke"
VIDEO_TYPES = {".mp4": "video/mp4", ".mov": "video/quicktime", ".mkv": "video/x-matroska", ".webm": "video/webm"}
IMAGE_TYPES = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}


def solid_png(width, height, rgb):
    """Minimal opaque RGB PNG without external imaging dependencies."""
    row = b"\0" + bytes(rgb) * width
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(row * height)) + chunk(b"IEND", b""))


class M10MediaProbe(base.SeekProbe):
    def __init__(self, output, dist, args):
        super().__init__(output, dist, args)
        self.frames = []
        self.media_events = []
        self.background = None
        self.background_kind = "image"
        self.sse = None

    def prepare_template(self):
        raw_manifest = (SAMPLE / "manifest.json").read_bytes()
        manifest = json.loads(raw_manifest)
        files = {item["path"]: (SAMPLE / item["path"]).read_bytes() for item in manifest["files"]}
        self.check(all(hashlib.sha256(files[item["path"]]).hexdigest() == item["sha256"] for item in manifest["files"]),
            "AdaptiveStroke: repository bytes match manifest hashes")
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("manifest.json", raw_manifest)
            for path, data in files.items():
                package.writestr(path, data)
        asset = self.upload("media-package", "m10-adaptive-stroke.mkfx", "application/octet-stream", archive.getvalue())
        inspected = self.request("media-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], "AdaptiveStroke: package admitted (media.features open)")
        draft = self.request("media-draft", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": "adaptive-stroke", "name": "M10 AdaptiveStroke"})
        self.check(draft["canCompile"] and draft["sdkVersion"].split(".")[0] == "2", "AdaptiveStroke: SDK2 draft compiles")
        builtin = self.request("template-builtin", "GET", "/api/v1/template-libraries/builtin/templates")
        line = next(item for item in builtin["templates"] if item["kind"] == "line")
        detail = self.request("template-detail", "GET", "/api/v1/template-libraries/builtin/templates/detail?" + urlencode({
            "expectedRevision": builtin["revision"], "templateId": line["templateId"]}))
        definition = dict(detail["definition"])
        config = draft["configuration"]
        definition.update(name="M10 AdaptiveStroke", kind="line", properties=["opacity"], packages=[inspected["reference"]])
        definition["effectRecipes"] = [{"recipeId": "m10-adaptive-stroke", "name": "AdaptiveStroke",
            "target": config["relativeTarget"], "packageId": config["packageId"], "revision": config["sourceRevision"],
            "exportId": config["exportId"], "parametersJson": config["parametersJson"], "enabled": True,
            "overlayLayer": config["overlayLayer"], "readScope": config["readScope"],
            "branchInput": config.get("branchInput"), "fixedSeed": config.get("fixedSeed"), "configurationId": None}]
        library = self.request("template-personal", "GET", "/api/v1/template-libraries/personal/templates")
        self.template = self.request("template-create", "POST", "/api/v1/template-libraries/personal/commands", value={
            "expectedRevision": library["revision"], "metadata": self.metadata("template-create"),
            "command": {"type": "create", "definitionFormat": "full", "definition": definition}})

    def send(self, name, command):
        # Insert the background before the template is applied and the preview is captured.
        if name.endswith("-apply-script") and self.background is not None:
            super().send(name + "-background", {"type": "composition.setBackgroundAsset",
                "assetId": self.background["assetId"], "kind": self.background_kind})
        return super().send(name, command)

    def binary(self, name, path, **kwargs):
        headers, data = super().binary(name, path, **kwargs)
        if name.endswith("-png"):
            (self.output / (name[:-4] + ".png")).write_bytes(data)
        return headers, data

    def wait_media_event(self, cursor, deadline):
        """Read one persistent client SSE stream until project.mediaFeaturesChanged arrives."""
        if self.sse is None:
            request = urllib.request.Request(self.base_url + "/api/v1/events", headers={"X-Mako-Client-Id": self.client,
                "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": cursor})
            # The server writes a heartbeat every 15 s, so a 20 s read timeout only trips on a stalled stream.
            self.sse = self.http.open(request, timeout=20)
            self.check(self.sse.status == 200, "events: SSE HTTP200")
        fields = {}
        while time.monotonic() < deadline:
            line = self.sse.readline().decode("utf-8").rstrip("\r\n")
            if line:
                if ": " in line and not line.startswith(":"):
                    key, value = line.split(": ", 1)
                    fields[key] = value
                continue
            if fields.get("event") == "project.mediaFeaturesChanged":
                self.media_events.append({"eventSeq": fields.get("id"), "observedMonotonicNs": time.monotonic_ns()})
                return fields.get("id")
            if fields.get("event") == "client.expired":
                # Access renewal between requests retires the stream's bearer; reopen from the last cursor.
                self.sse.close()
                self.sse = None
                return self.wait_media_event(cursor, deadline)
            if fields.get("event") == "resync.required":
                raise AssertionError("event stream requires resync")
            if "id" in fields:
                cursor = fields["id"]
            fields = {}
        raise TimeoutError("project.mediaFeaturesChanged not observed")

    def refresh_input(self, name):
        preview = self.active_preview
        updated = self.request(name, "PUT", "/api/v1/preview-sessions/" + preview["previewId"] + "/input", value={
            "expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": preview["source"]["sourceRevision"],
            "source": preview["source"], "size": {"width": self.args.width, "height": self.args.height},
            "alphaMode": "opaque", "renderBackend": self.args.backend, "metadata": self.metadata(name)})
        session = updated.get("session", updated)
        self.check(session["source"] == preview["source"], name + ": same fixed project revision recaptured")
        self.check(int(session["previewGeneration"]) > int(preview["previewGeneration"]), name + ": recapture advances generation")
        self.active_preview = session

    def run(self):
        self.stage = "start"
        self.start("m10")
        self.stage = "connect"
        self.connect("m10")
        self.stage = "prepare-template"
        self.prepare_template()
        self.stage = "background"
        if self.args.background is None:
            self.background = self.upload("m10-background", "m10-white.png", "image/png",
                solid_png(self.args.width, self.args.height, (255, 255, 255)))
        else:
            suffix = self.args.background.suffix.lower()
            self.background_kind = "video" if suffix in VIDEO_TYPES else "image"
            self.background = self.upload("m10-background", "m10-background" + suffix,
                VIDEO_TYPES.get(suffix) or IMAGE_TYPES[suffix], self.args.background.read_bytes())
        self.stage = "project"
        self.create_project("m10", 2_000_000)
        deadline = time.monotonic() + self.args.timeout_seconds
        self.stage = "frame-initial"
        started = time.monotonic_ns()
        first = self.sample("m10-initial", 1, 500_000, {})
        self.frames = [first["rgbaSha256"]]
        cursor, sequence = "0", 1
        # Inspection and completion each advance the Host generation; refresh on every hint until the
        # final input changes the pixels. A still analysed before the first capture never differs.
        while self.frames[-1] == first["rgbaSha256"] and time.monotonic() < deadline:
            self.stage = "event-" + str(sequence)
            try:
                # A long video can analyse for minutes between inspection and completion; wait to the overall deadline.
                cursor = self.wait_media_event(cursor, deadline)
            except TimeoutError:
                break
            sequence += 1
            self.stage = "refresh-" + str(sequence)
            self.refresh_input("m10-refresh-" + str(sequence))
            self.frames.append(self.sample("m10-refreshed-" + str(sequence), sequence, 500_000, {})["rgbaSha256"])
        differ = self.frames[-1] != first["rgbaSha256"]
        self.scenarios.append({"name": "m10-media-refresh", "status": "passed" if differ else "inconclusive",
            "background": self.background_kind, "framesDiffer": differ, "mediaEvents": self.media_events,
            "changedAfterMs": (time.monotonic_ns() - started) / 1e6 if differ else None})
        self.sse.close()
        self.sse = None
        self.close_resources("m10")


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=180)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--poll-ms", type=int, default=50)
    parser.add_argument("--backend", choices=("cpu", "gpu"), default="gpu")
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    parser.add_argument("--ffmpeg", default="/usr/bin/ffmpeg")
    parser.add_argument("--background", type=Path, help="real still (.png/.jpg) or video (.mp4/.mov/.mkv/.webm) background")
    args = parser.parse_args()
    args.count, args.require_reuse, args.scenario = 2, False, "m10"
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    return args


def main():
    args = arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    if args.icd.is_file():
        os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = str(args.icd)
    probe = M10MediaProbe(args.output, args.dist, args)
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
            probe.stop("m10")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        summary = {"status": "passed" if not failures and probe.scenarios and all(s["status"] == "passed" for s in probe.scenarios) else "failed", "failures": failures,
            "scenarios": probe.scenarios, "frames": probe.frames}
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
