#!/usr/bin/env python3
"""Finite real-Server compatibility gate; deliberately no rendering or FPS test."""
import argparse
import hashlib
import importlib.util
import io
import json
import platform
import subprocess
import wave
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "backend_http_probe", ROOT / "backend/tools/backend-http-probe/probe.py")
probe_module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe_module)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--credential-file", required=True, type=Path,
                        help="Private launcher bootstrap.json; never copied to evidence")
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    save = lambda name, value: probe_module.save_json(args.output / name, value)
    now = lambda: datetime.now(timezone.utc).isoformat()
    probe = probe_module.Probe(args.output, [])
    boot = json.loads(args.credential_file.read_text())
    probe.bootstrap, probe.host = boot["bootstrapToken"], boot["hostInstanceId"]
    probe.base_url = boot["addresses"][0]
    if not probe.base_url.startswith("http://127.0.0.1:"):
        raise ValueError("Gate only accepts the launcher's IPv4 loopback address")
    secrets = [probe.bootstrap]
    deployment_bytes = (args.dist / "deployment.json").read_bytes()
    result = {
        "startedUtc": now(), "scope": "real HTTP compatibility gate; no frame requests",
        "machine": {"platform": platform.platform(), "architecture": platform.machine(),
                    "logicalCpuCount": __import__("os").cpu_count()},
        "sourceSnapshots": json.loads((ROOT / "source-snapshots.json").read_text()),
        "appCommitAtStart": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "deploymentSha256": hashlib.sha256(deployment_bytes).hexdigest(),
        "deployment": json.loads(deployment_bytes),
        "requestedRgbaTarget": {"width": 2560, "height": 1440, "bytesPerPixel": 4,
                                "bytesPerFrame": 2560 * 1440 * 4, "targetFps": 30},
        "previewFrameRequestsIssued": 0, "performanceMatrix": "notRun",
    }
    project = preview = None

    def metadata(name):
        return probe.metadata("protocol-gate-" + name, seconds=60)

    try:
        registration = probe.register("register")
        secrets += [registration["accessToken"], registration["resumeToken"]]
        runtime = probe.request("runtime-info", "GET", "/api/v1/runtime-info")
        probe.request("capabilities", "GET", "/api/v1/capabilities")
        result["advertisedPreviewLimits"] = {key: runtime[key] for key in
            ("maxPixelWidth", "maxPixelHeight", "maxRawFrameBytes")}
        probe.check(runtime["enabled"], "real preview Worker configured")
        audio_buffer = io.BytesIO()
        with wave.open(audio_buffer, "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(8000)
            audio.writeframes(b"\0\0" * 16000)
        assets = []
        for name, mime, data in (
            ("gate.wav", "audio/wav", audio_buffer.getvalue()),
            ("gate.lrc", "text/plain", b"[00:00.00]Protocol gate\n"),
        ):
            meta = metadata(name)
            assets.append(probe.request("upload-" + name, "POST", "/api/v1/assets?fileName=" + name,
                raw=data, content_type=mime, status=201,
                headers={"X-Mako-Request-Id": meta["requestId"],
                         "X-Mako-Valid-Until-Utc": meta["validUntilUtc"]}))
        project = probe.request("create-project", "POST", "/api/v1/project-sessions", status=201,
            value={"name": "Preview protocol gate", "audioAssetId": assets[0]["assetId"],
                "lyricsAssetId": assets[1]["assetId"], "metadata": metadata("project"),
                "composition": {"canvasWidth": 1280, "canvasHeight": 720,
                    "frameRateNumerator": 24, "frameRateDenominator": 1,
                    "backgroundRed": 0, "backgroundGreen": 0, "backgroundBlue": 0,
                    "backgroundAlpha": 255, "outputDurationUs": 2000000}})
        request = {"source": {"kind": "project", "sourceId": project["projectSessionId"],
                               "sourceRevision": project["projectRevision"]},
                   "size": {"width": 1280, "height": 720}, "alphaMode": "opaque",
                   "purpose": "preview", "metadata": metadata("valid-preview")}
        preview = probe.request("create-valid-720p-preview", "POST", "/api/v1/preview-sessions",
                                value=request, status=201)
        request = {**request, "size": {"width": 2560, "height": 1440},
                   "metadata": metadata("oversize-preview")}
        rejection = probe.request("reject-1440p-preview", "POST", "/api/v1/preview-sessions",
                                  value=request, status=400, code="invalidRequest")
        probe.check(rejection.get("field") == "size", "1440p rejected specifically by size gate")
        result.update(outcome="blockedByExistingProtocol", rejection=rejection,
                      stopReason="Valid source accepted at 720p; 1440p rejected before frame rendering")
    except Exception as error:
        result.update(outcome="gateFailed", failureType=type(error).__name__, failure=str(error))
        raise
    finally:
        cleanup_errors = []
        try:
            if preview:
                probe.request("close-preview", "POST", "/api/v1/preview-sessions/" + preview["previewId"] + "/close",
                    value={"expectedPreviewGeneration": preview["previewGeneration"],
                           "expectedSourceRevision": preview["source"]["sourceRevision"],
                           "metadata": metadata("close-preview")})
        except Exception as error:
            cleanup_errors.append({"operation": "close-preview", "error": str(error)})
        try:
            if project:
                job = probe.request("close-project", "POST", "/api/v1/project-sessions/" + project["projectSessionId"] + "/close",
                    status=202, value={"expectedRevision": project["projectRevision"], "policy": "discard",
                                       "metadata": metadata("close-project")})
                probe.job("close-project", job)
        except Exception as error:
            cleanup_errors.append({"operation": "close-project", "error": str(error)})
        result.update(finishedUtc=now(), assertions=probe.assertions,
                      httpRequestCount=probe.request_count,
                      cleanup={"status": "failed" if cleanup_errors else "completed", "errors": cleanup_errors})
        if cleanup_errors:
            result.update(gateOutcome=result.get("outcome"), outcome="gateCleanupFailed")
        save("result.json", result)
        for path in args.output.rglob("*"):
            if path.is_file() and any(secret.encode() in path.read_bytes() for secret in secrets):
                raise RuntimeError("Credential scan failed; do not publish this evidence")
        if cleanup_errors:
            raise RuntimeError("Gate fixture cleanup failed; see result.json")
    print(json.dumps({"outcome": result["outcome"], "output": str(args.output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
