#!/usr/bin/env python3
"""Explicit long-timeline HTTP correctness probe with a glyph-safe fixture."""
import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import wave
import math

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
BASE_PROBE = HERE / "probe.py"
SPEC = importlib.util.spec_from_file_location("seek_probe", BASE_PROBE)
seek = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(seek)
save_json = seek.save_json

# Digits preserve the original 24-token shape while avoiding spaces and common
# Latin ligature sequences. The value is deliberately explicit in evidence.
SAFE_LYRICS_TEXT = "012345678901234567890123"
BASE_PROBE_SHA256 = hashlib.sha256(BASE_PROBE.read_bytes()).hexdigest()


class LongFixtureProbe(seek.SeekProbe):
    def create_project(self, name, duration_us):
        stream = io.BytesIO()
        with wave.open(stream, "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(8000)
            audio.writeframes(b"\0\0" * math.ceil(duration_us * 8000 / 1_000_000))
        asset = self.upload(name + "-audio", name + ".wav", "audio/wav", stream.getvalue())
        lyrics = self.upload(name + "-lyrics", name + ".lrc", "text/plain",
            ("[00:00.00]" + SAFE_LYRICS_TEXT + "\n").encode("ascii"))
        created = self.request(name + "-project", "POST", "/api/v1/project-sessions", status=201, value={
            "name": "HTTP long glyph-safe " + name, "audioAssetId": asset["assetId"], "lyricsAssetId": lyrics["assetId"],
            "composition": {"canvasWidth": self.args.width, "canvasHeight": self.args.height,
                "frameRateNumerator": 30, "frameRateDenominator": 1, "outputDurationUs": duration_us}})
        self.active_project = {"path": "/api/v1/project-sessions/" + created["projectSessionId"],
            "revision": created["projectRevision"]}
        lines = self.request(name + "-lines", "GET", self.active_project["path"] + "/lyrics/lines?branchId=primary")["lines"]
        self.check(len(lines) == 1 and len(lines[0]["tokens"]) == len(SAFE_LYRICS_TEXT),
            name + ": one 24-token glyph-safe lyric line")
        line = lines[0]
        self.send(name + "-line-time", {"type": "lyrics.setTimeRange", "target": {"branchId": "primary", "lineId": line["id"]},
            "startUs": "0", "endUs": str(duration_us)})
        self.send(name + "-token-time", {"type": "lyrics.setTokenTimes", "changes": [{"target": {
            "branchId": "primary", "lineId": line["id"], "tokenId": token["id"]},
            "startUs": str(duration_us * index // len(line["tokens"])),
            "endUs": str(duration_us * (index + 1) // len(line["tokens"]))}
            for index, token in enumerate(line["tokens"])]})
        self.send(name + "-apply-script", {"type": "presetApplications.apply", "templateLibraryId": "personal",
            "templateId": self.template["templateId"], "templateRevision": self.template["revision"],
            "targets": [{"kind": "line", "branchId": "primary", "lineId": line["id"]}]})
        source = {"kind": "project", "sourceId": created["projectSessionId"],
            "sourceRevision": self.active_project["revision"]}
        preview = self.request(name + "-preview", "POST", "/api/v1/preview-sessions", status=201, value={
            "source": source, "size": {"width": self.args.width, "height": self.args.height},
            "alphaMode": "opaque", "purpose": "preview", "renderBackend": self.args.backend})
        self.active_preview = preview.get("session", preview)
        return self.active_preview

    def run(self):
        super().run()
        provenance = self.output / "provenance.json"
        if provenance.exists():
            value = json.loads(provenance.read_text())
            value.update({"baseProbeSha256": BASE_PROBE_SHA256,
                "fixtureText": SAFE_LYRICS_TEXT, "fixtureTextSha256": hashlib.sha256(SAFE_LYRICS_TEXT.encode("ascii")).hexdigest(),
                "fixtureScope": "24 ASCII digits; no spaces or common Latin ligature sequences; original WordEmphasis source unchanged"})
            save_json(provenance, value)
        save_json(self.output / "long-fixture.json", {"baseProbeSha256": BASE_PROBE_SHA256,
            "fixtureText": SAFE_LYRICS_TEXT, "fixtureTextSha256": hashlib.sha256(SAFE_LYRICS_TEXT.encode("ascii")).hexdigest(),
            "tokenCount": len(SAFE_LYRICS_TEXT), "source": "WordEmphasis sample copied by base probe; no source mutation"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--scenario", choices=("180s", "360s", "all"), default="all")
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=180)
    parser.add_argument("--count", type=int, default=4)
    parser.add_argument("--timeout-seconds", type=float, default=180)
    parser.add_argument("--poll-ms", type=int, default=50)
    parser.add_argument("--backend", choices=("cpu", "gpu"), default="cpu")
    parser.add_argument("--ffmpeg", default="/usr/bin/ffmpeg")
    parser.add_argument("--require-reuse", action="store_true")
    args = parser.parse_args()
    if not (64 <= args.width <= 2560 and 64 <= args.height <= 1440 and 2 <= args.count <= 100
            and 1 <= args.timeout_seconds <= 600 and 10 <= args.poll_ms <= 1000):
        parser.error("size/count/deadline/poll outside finite probe bounds")
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    probe = LongFixtureProbe(args.output, args.dist, args)
    failures = []
    try:
        probe.run()
    except Exception as error:
        failures.append(probe.failure(error))
    finally:
        if probe.active_preview or probe.active_project:
            try:
                probe.close_resources("failure")
            except Exception as error:
                failures.append(probe.failure(error, "resource-cleanup"))
        try:
            probe.stop("long-seek")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        try:
            scanned = probe.scan_credentials()
        except Exception as error:
            failures.append(probe.failure(error, "credential-scan"))
            scanned = None
        summary = {"status": "passed" if not failures and probe.scenarios and all(s["status"] == "passed" for s in probe.scenarios) else "failed",
            "failures": failures, "scenarios": probe.scenarios, "samples": probe.samples,
            "credentialScannedFiles": scanned, "httpRequests": probe.request_count, "assertions": len(probe.assertions),
            "scope": "Long glyph-safe fixture correctness only; no UI or performance claim; v1 replay remains in scope."}
        save_json(args.output / "summary.json", summary)
        try:
            probe.scan_credentials()
        except Exception:
            summary["status"] = "failed"
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "scenarios": len(probe.scenarios), "samples": len(probe.samples)}))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
