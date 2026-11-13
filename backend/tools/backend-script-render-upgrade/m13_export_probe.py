#!/usr/bin/env python3
"""M13 A3 c／f：fresh HTTP + GPU Worker 导出全部 SDK2 样例与 T01 组合，并核对成品画面。

每组单独建模板与工程（浅色图片背景、每 1.25 s 一行，同 m12 export），GPU 不透明 MP4 必做；
T01 组合与含粒子、图、材质的几组再做透明 ProRes4444。成品完整下载后核对大小／SHA 与工件引用，
ffprobe 核对编码、尺寸与帧数，ffmpeg 全量解码，再取 1.5 s（第二行在唱、第一行退场中）的导出帧
与同一时刻的预览帧比较：不透明比 RGB，透明比预乘 RGBA。比较只在预览有内容的像素上算均值，
内容像素过少直接失败，避免整屏背景稀释差异。

透明预览由不透明会话经 PUT input 切到 straight（--straight-session new 改为关闭后新建，用于区分两条路径）。"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("m13_m12_probe", HERE / "m12_http_probe.py")
m12 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m12)
m11, m10 = m12.m11, m12.m10
save_json = m10.save_json
SAMPLES = ("WordEmphasis", "RepeatEmphasis", "CursorTrail", "EventBurst", "SpriteTrail", "VectorTrail", "CustomInk",
    "LineGlitch", "GlassInk", "ClusterExit", "AdaptiveStroke", "StarSweep", "WaveWarp")
# 透明 ProRes：组合、精灵与矢量粒子、整行图、两个材质包。
TRANSPARENT = ("t01", "SpriteTrail", "VectorTrail", "LineGlitch", "CustomInk", "GlassInk")
SECONDS, FPS, CHECK_FRAME = 3, 30, 45
CHECK_US = CHECK_FRAME * 1_000_000 // FPS
BACKGROUND = (235, 235, 240)
# 内容像素：预览与背景任一通道差超过 24（不透明），或预览 alpha 非零（透明）。
CONTENT_THRESHOLD, MINIMUM_CONTENT = 24, 400
# GlassInk 是折射下层的玻璃材质：叠在浅色纯色背景上，字形与背景最多只差约 15，按 8 取内容像素。
CONTENT_THRESHOLDS = {"GlassInk": 8}
# 有损编码容差（内容像素上的通道均值）：H.264 yuv420p 色度下采样主要落在字形边缘；ProRes4444 近无损。
# 0.112.87 实测最大：不透明 8.48（CustomInk），透明预乘 0.43、alpha 0.90（GlassInk）。
OPAQUE_MEAN_LIMIT, TRANSPARENT_MEAN_LIMIT, ALPHA_MEAN_LIMIT = 12.0, 2.0, 2.0


def decode(ffmpeg, data_or_path):
    command = [ffmpeg, "-v", "error", "-i", "pipe:0" if isinstance(data_or_path, bytes) else str(data_or_path),
        "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"]
    return subprocess.run(command, input=data_or_path if isinstance(data_or_path, bytes) else None,
        capture_output=True, timeout=120, check=True).stdout


def compare_opaque(preview, exported, threshold=CONTENT_THRESHOLD):
    content = diff = 0
    for index in range(0, len(preview), 4):
        p = preview[index:index + 3]
        if max(abs(p[0] - BACKGROUND[0]), abs(p[1] - BACKGROUND[1]), abs(p[2] - BACKGROUND[2])) <= threshold:
            continue
        content += 1
        diff += abs(p[0] - exported[index]) + abs(p[1] - exported[index + 1]) + abs(p[2] - exported[index + 2])
    return {"contentThreshold": threshold, "contentPixels": content, "rgbMeanAbs": round(diff / max(1, content * 3), 3)}


def compare_transparent(preview, exported):
    content = diff = alpha = 0
    for index in range(0, len(preview), 4):
        a, b = preview[index + 3], exported[index + 3]
        if a == 0 and b == 0:
            continue
        content += 1
        alpha += abs(a - b)
        diff += sum(abs(preview[index + c] * a - exported[index + c] * b) for c in range(3)) / 255
    return {"contentPixels": content, "premultipliedMeanAbs": round(diff / max(1, content * 3), 3),
        "alphaMeanAbs": round(alpha / max(1, content), 3),
        "exportAlphaMin": min(exported[3::4]), "exportAlphaMax": max(exported[3::4])}


class M13ExportProbe(m12.M12Probe):
    label = "m13"

    def metadata(self, name, *args, **kwargs):
        # m12 的模板与工程命令用固定 requestId；多组共用一个 Server 时按组加前缀。
        return super().metadata(self.label + "-" + name, *args, **kwargs)

    def upload(self, name, filename, content_type, data):
        # 上传直接以 name 作 requestId；同一样例出现在组合与单独两组时按组加前缀。
        return super().upload(self.label + "-" + name, filename, content_type, data)

    def wait_media_event(self, cursor, deadline):
        """同 m10，但只认本组工程的 project.mediaFeaturesChanged：前一组工程的背景分析事件可能晚到。"""
        if self.sse is None:
            request = m10.urllib.request.Request(self.base_url + "/api/v1/events", headers={"X-Mako-Client-Id": self.client,
                "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": cursor})
            self.sse = self.http.open(request, timeout=20)
            self.check(self.sse.status == 200, "events: SSE HTTP200")
        project, fields = self.active_project["path"].rsplit("/", 1)[1], {}
        while time.monotonic() < deadline:
            line = self.sse.readline().decode("utf-8").rstrip("\r\n")
            if line:
                if ": " in line and not line.startswith(":"):
                    key, value = line.split(": ", 1)
                    fields[key] = value
                continue
            if fields.get("event") == "project.mediaFeaturesChanged" and project in fields.get("data", ""):
                self.media_events.append({"eventSeq": fields.get("id"), "observedMonotonicNs": time.monotonic_ns()})
                return fields.get("id")
            if fields.get("event") == "client.expired":
                self.sse.close()
                self.sse = None
                return self.wait_media_event(cursor, deadline)
            if fields.get("event") == "resync.required":
                raise AssertionError("event stream requires resync")
            if "id" in fields:
                cursor = fields["id"]
            fields = {}
        raise TimeoutError("project.mediaFeaturesChanged not observed for " + project)

    def preview_rgba(self, name, time_us):
        try:
            self.sample(name, self.next_sequence(), time_us, {})
        except AssertionError:
            # 失败的预览会话只给 state=failed，诊断另取一页留证。
            diagnostics = self.request(name + "-diagnostics", "GET", "/api/v1/preview-sessions/"
                + self.active_preview["previewId"] + "/diagnostics")
            save_json(self.output / (name + "-diagnostics.json"), self.redact(diagnostics))
            raise
        return decode(self.args.ffmpeg, self.pngs[name])

    def set_alpha(self, name, alpha):
        preview = self.active_preview
        if self.args.straight_session == "new":
            closed, source = dict(preview), preview["source"]
            self.close_preview(name + "-close-opaque")
            created = self.request(name, "POST", "/api/v1/preview-sessions", status=201, value={"source": source,
                "size": closed["size"], "alphaMode": alpha, "purpose": "preview", "renderBackend": "gpu"})
            self.active_preview = created.get("session", created)
            self.check(self.active_preview["alphaMode"] == alpha, name + ": new preview uses alpha mode")
            return
        updated = self.request(name, "PUT", "/api/v1/preview-sessions/" + preview["previewId"] + "/input", value={
            "expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": preview["source"]["sourceRevision"],
            "source": preview["source"], "size": preview["size"], "alphaMode": alpha, "renderBackend": "gpu",
            "metadata": self.metadata(name)})
        self.active_preview = updated.get("session", updated)
        self.check(self.active_preview["alphaMode"] == alpha, name + ": preview switched alpha mode")

    def close_preview(self, name):
        preview = self.active_preview
        closed = self.request(name, "POST", "/api/v1/preview-sessions/" + preview["previewId"] + "/close", value={
            "expectedPreviewGeneration": preview["previewGeneration"], "expectedSourceRevision": preview["source"]["sourceRevision"]})
        self.check(closed.get("session", closed).get("state") == "closed", name + ": preview closed")
        self.active_preview = None

    def export_one(self, label, fmt):
        name = label + "-" + fmt
        accepted = self.request(name, "POST", self.active_project["path"] + "/export-jobs", status=202, value={
            "expectedRevision": self.active_project["revision"], "format": fmt, "includeAudio": False, "quality": "standard",
            "renderBackend": "gpu", "metadata": self.metadata(name)})
        self.check(accepted["exportInput"]["renderBackend"] == "gpu", name + ": export pins GPU execution")
        started = time.monotonic()
        terminal = self.wait_job(name, accepted["jobId"], started + self.args.timeout_seconds)
        wall = time.monotonic() - started
        save_json(self.output / (name + "-terminal.json"), self.redact(terminal))
        self.check(terminal["state"] == "succeeded", name + ": export succeeded")
        artifact = terminal["exportResult"]["artifact"]
        _, media = self.binary(name + "-content", "/api/v1/jobs/" + terminal["jobId"] + "/export-content")
        sha = hashlib.sha256(media).hexdigest()
        self.check(len(media) == artifact["sizeBytes"] and sha == artifact["sha256"], name + ": complete media matches artifact")
        path = self.output / (name + (".mp4" if fmt == "opaqueMp4" else ".mov"))
        path.write_bytes(media)
        stream = json.loads(subprocess.run([self.args.ffprobe, "-v", "error", "-select_streams", "v:0", "-count_frames",
            "-show_entries", "stream=codec_name,pix_fmt,width,height,nb_read_frames", "-of", "json", str(path)],
            capture_output=True, timeout=120, check=True).stdout)["streams"][0]
        width, height, frames = self.args.width, self.args.height, SECONDS * FPS
        self.check(stream["width"] == width and stream["height"] == height and int(stream["nb_read_frames"]) == frames,
            name + ": encoded size and frame count")
        self.check(stream["codec_name"] == ("h264" if fmt == "opaqueMp4" else "prores")
            and (fmt == "opaqueMp4" or stream["pix_fmt"].startswith("yuva")), name + ": codec and alpha plane")
        decoded = decode(self.args.ffmpeg, path)
        size = width * height * 4
        self.check(len(decoded) == frames * size, name + ": every frame decodes to complete RGBA")
        return decoded[CHECK_FRAME * size:(CHECK_FRAME + 1) * size], {"format": fmt, "wallMs": round(wall * 1000, 1),
            "mediaBytes": len(media), "mediaSha256": sha, "stream": stream, "checkFrame": CHECK_FRAME, "checkTimeUs": str(CHECK_US)}

    def export_set(self, label, names):
        self.begin("export-" + label)
        result = {"name": "export-" + label, "status": "running", "samples": list(names), "exports": []}
        try:
            self.label, self.names = label, names
            self.prepare_template()
            result["lines"] = self.create_project(label, SECONDS * 1_000_000)
            # 先导出：导出等待完整背景分析制品；之后再重新固定预览输入，两边读同一份媒体结果。
            # project.mediaFeaturesChanged 会因任意分析完成（含作者草稿试样）广播给全部工程，不能据此认定本工程已分析完。
            exported, info = self.export_one(label, "opaqueMp4")
            self.refresh_input(label + "-preview-opaque-input")
            metrics = compare_opaque(self.preview_rgba(label + "-preview-opaque", CHECK_US), exported,
                CONTENT_THRESHOLDS.get(label, CONTENT_THRESHOLD))
            result["exports"].append({**info, **metrics})
            self.check(metrics["contentPixels"] >= MINIMUM_CONTENT, label + "-opaqueMp4: enough content pixels to compare")
            self.check(metrics["rgbMeanAbs"] <= OPAQUE_MEAN_LIMIT, label + "-opaqueMp4: exported frame matches preview (RGB)")
            if label in self.args.transparent:
                exported, info = self.export_one(label, "transparentProRes4444")
                self.set_alpha(label + "-preview-straight-input", "straight")
                metrics = compare_transparent(self.preview_rgba(label + "-preview-straight", CHECK_US), exported)
                result["exports"].append({**info, **metrics})
                self.check(metrics["contentPixels"] >= MINIMUM_CONTENT, label + "-transparent: enough content pixels to compare")
                self.check(metrics["exportAlphaMin"] == 0 and metrics["exportAlphaMax"] > 0, label + "-transparent: transparent and visible pixels")
                self.check(metrics["premultipliedMeanAbs"] <= TRANSPARENT_MEAN_LIMIT and metrics["alphaMeanAbs"] <= ALPHA_MEAN_LIMIT,
                    label + "-transparent: exported frame matches preview (premultiplied RGBA)")
            result["status"] = "passed"
        except Exception as error:
            result.update(status="failed", failure=self.failure(error))
        finally:
            try:
                self.close_resources(label)
            except Exception as error:
                result.update(status="failed", cleanupFailure=self.failure(error))
            self.end(result)

    def run(self):
        deployment = json.loads((self.dist / "deployment.json").read_text())
        save_json(self.output / "provenance.json", {"deployment": {key: deployment.get(key) for key in (
            "version", "sourceCommit", "sourceDirty")}, "probeSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "sets": [label for label, _ in self.sets]})
        self.stage = "start"
        self.start("m13")
        self.stage = "connect"
        self.connect("m13")
        self.check(self.current_ready["version"] == deployment["version"], "running version matches deployment manifest")
        self.background = self.upload("m13-background", "m13-light.png", "image/png",
            m10.solid_png(self.args.width, self.args.height, BACKGROUND))
        for label, names in self.sets:
            self.stage = "export-" + label
            self.export_set(label, names)


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--sets", default="t01," + ",".join(SAMPLES), help="t01 and/or ScriptEffectsV2 sample names")
    parser.add_argument("--transparent", default=",".join(TRANSPARENT), help="sets that also export transparent ProRes4444")
    parser.add_argument("--straight-session", choices=("put", "new"), default="put",
        help="switch the opaque preview to straight via PUT input, or close it and create a new straight session")
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=360)
    parser.add_argument("--timeout-seconds", type=float, default=180)
    parser.add_argument("--poll-ms", type=int, default=50)
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    parser.add_argument("--ffmpeg", default="/usr/bin/ffmpeg")
    parser.add_argument("--ffprobe", default="/usr/bin/ffprobe")
    args = parser.parse_args()
    labels = args.sets.split(",")
    if not (64 <= args.width <= 1280 and 64 <= args.height <= 720) or any(label != "t01" and label not in SAMPLES for label in labels):
        parser.error("size outside preview bounds or unknown sample set")
    args.samples, args.count, args.require_reuse, args.backend, args.background, args.scenario = "t01", 2, False, "gpu", None, "m13"
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    args.labels, args.transparent = labels, set(args.transparent.split(","))
    return args


def main():
    args = arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    if args.icd.is_file():
        os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = str(args.icd)
    for key in [key for key in os.environ if key.startswith("MAKO_PERFORMANCE_")]:
        del os.environ[key]
    probe = M13ExportProbe(args.output, args.dist, args)
    probe.sets = [(label, m11.SAMPLES if label == "t01" else (label,)) for label in args.labels]
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
            probe.stop("m13")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        summary = {"status": "passed" if not failures and len(probe.results) == len(probe.sets)
            and all(result["status"] == "passed" for result in probe.results) else "failed",
            "size": [args.width, args.height], "seconds": SECONDS, "checkFrame": CHECK_FRAME,
            "limits": {"opaqueContentThreshold": CONTENT_THRESHOLD, "opaqueContentThresholds": CONTENT_THRESHOLDS,
                "opaqueRgbMeanAbs": OPAQUE_MEAN_LIMIT, "transparentPremultipliedMeanAbs": TRANSPARENT_MEAN_LIMIT,
                "transparentAlphaMeanAbs": ALPHA_MEAN_LIMIT, "minimumContentPixels": MINIMUM_CONTENT},
            "failures": failures, "results": probe.results,
            "scope": "Real HTTP + GPU Worker export on this machine; one sampled frame per export compared with the preview frame."}
        save_json(args.output / "summary.json", summary)
        try:
            probe.scan_credentials()
        except Exception as error:
            summary["status"] = "failed"
            summary["credentialFailure"] = probe.failure(error, "credential-scan")
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "results": [{"name": result["name"], "status": result["status"],
            "exports": [{key: item.get(key) for key in ("format", "contentPixels", "rgbMeanAbs", "premultipliedMeanAbs", "alphaMeanAbs")}
                for item in result.get("exports", [])]} for result in probe.results],
            "failures": [{key: item.get(key) for key in ("stage", "errorType", "message")} for item in failures]}, ensure_ascii=False))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
