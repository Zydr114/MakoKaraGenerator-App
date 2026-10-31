#!/usr/bin/env python3
"""Fresh public HTTP acceptance for package no-audio export.

The probe uses FixtureOracle's real ProjectStore package fixtures and a matched
self-contained Server/Worker. It deliberately keeps the scope bounded to the
package-lyrics-scene-v1 no-audio export contract.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/backend-public-package-preview-probe"))
spec = importlib.util.spec_from_file_location("package_preview", ROOT / "tools/backend-public-package-preview-probe/probe.py")
preview = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preview)


class PackageExportProbe(preview.PackagePreviewProbe):
    def __init__(self, output, dist, oracle):
        super().__init__(output, dist, oracle)
        self.export_results = []
        self.export_workers = {}
        self.sse_events = []

    def worker_identities_now(self):
        self.observe(self.current_ready["pid"])
        values = []
        for (pid, ticks) in self.observed:
            try:
                exe = Path(f"/proc/{pid}/exe").resolve().name
                stat = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
                if exe == "MakoKaraGenerator.Backend.Worker" and stat[19] == ticks:
                    values.append({"pid": pid, "startTicks": ticks})
            except (OSError, IndexError):
                pass
        return values

    def wait_export_active(self, name, accepted):
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            self.observe(self.current_ready["pid"])
            state = self.request(name + "-active", "GET", "/api/v1/jobs/" + accepted["jobId"])
            workers = self.worker_identities_now()
            if state["state"] == "running" and workers:
                # Capability handshakes can leave a short-lived Worker identity
                # in the process ledger. The active task is the newest live
                # Worker under this host generation.
                workers.sort(key=lambda item: item["pid"])
                selected = workers[-1]
                self.export_workers[accepted["jobId"]] = selected
                self.check(len(workers) >= 1, name + ": actual package export Worker observed")
                return state, selected
            self.check(state["state"] not in ("succeeded", "failed", "cancelled"), name + ": active before control")
            time.sleep(.02)
        raise AssertionError(name + ": package export did not become active")

    def signal_worker(self, name, identity, signum):
        try:
            stat = Path(f"/proc/{identity['pid']}/stat").read_text().rsplit(")", 1)[1].split()
            self.check(stat[19] == identity["startTicks"], name + ": exact Worker start ticks before signal")
            os.kill(identity["pid"], signum)
        except OSError as error:
            raise AssertionError(name + ": Worker disappeared before controlled signal") from error

    def export_job(self, name, source, fmt, include_audio=False, **kwargs):
        path = "/api/v1/project-sessions/" + source["sourceId"] + "/export-jobs"
        value = {"expectedRevision": source["sourceRevision"], "format": fmt,
                 "includeAudio": include_audio, "metadata": self.metadata(name)}
        return self.request(name, "POST", path, status=202, value=value, **kwargs)

    def cancel_job(self, name, accepted):
        # Job cancellation consumes RequestMetadataDto directly.  B1's
        # BasicBusinessProbe wrapper is intentionally bypassed here so the
        # request is not turned into {requestId,...,metadata:{...}}.
        return preview.basic.g20.smoke.existing.Probe.request(
            self, name, "POST", "/api/v1/jobs/" + accepted["jobId"] + "/cancel",
            value=self.metadata(name + "-metadata"))

    def wait_terminal(self, name, accepted, expected="succeeded", timeout=45):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.observe(self.current_ready["pid"])
            value = self.request(name + "-poll", "GET", "/api/v1/jobs/" + accepted["jobId"])
            if value["state"] in ("succeeded", "failed", "cancelled", "unknown"):
                self.check(value["state"] == expected, name + ": terminal " + expected)
                self.check(not value["canCancel"], name + ": terminal cannot be cancelled")
                self.export_results.append(value)
                return value
            time.sleep(.05)
        raise AssertionError(name + ": package export did not reach terminal state")

    def export_report_and_media(self, name, terminal):
        job_path = "/api/v1/jobs/" + terminal["jobId"]
        ref = terminal["exportResult"]
        report_headers, report_bytes, _ = self.fetch(name + "-report", job_path + "/export-result")
        self.check(len(report_bytes) == ref["report"]["sizeBytes"], name + ": report size matches ledger")
        self.check(hashlib.sha256(report_bytes).hexdigest() == ref["report"]["sha256"], name + ": report SHA matches ledger")
        self.check(report_headers.get("Content-Type", "").split(";")[0] == "application/json", name + ": report JSON media type")
        report = json.loads(report_bytes)
        self.check(report["audioIncluded"] is False and report["strictlyVerified"] is True, name + ": no-audio report is strict")
        self.check(report["expectedFrames"] == report["decodedFrames"] == report["framesWritten"] and report["failedFrames"] == report["droppedFrames"] == 0,
                   name + ": report frame accounting is complete")
        self.check(report.get("audioCodec") is None and report.get("actualAudioDurationUs") is None, name + ": report has no audio stream")
        page = self.request(name + "-diagnostics", "GET", job_path + "/export-diagnostics?limit=128")
        self.check(page["reportSha256"] == ref["report"]["sha256"] and preview.compact(page["diagnostics"]) == preview.compact(report["diagnostics"]) and page.get("nextCursor") is None,
                   name + ": diagnostics page binds complete immutable report")
        media_headers, media, _ = self.fetch(name + "-content", job_path + "/export-content")
        self.check(len(media) == ref["artifact"]["sizeBytes"] == report["mediaSize"], name + ": media size matches ledger/report")
        media_sha = hashlib.sha256(media).hexdigest()
        self.check(media_sha == ref["artifact"]["sha256"] == report["sha256"], name + ": media SHA matches ledger/report")
        self.check(media_headers.get("Content-Type", "").split(";")[0] in ("video/mp4", "video/quicktime"), name + ": media content type")
        media_path = self.output / (name + (".mov" if media_headers.get("Content-Type", "").split(";")[0] == "video/quicktime" else ".mp4"))
        media_path.write_bytes(media)
        ffprobe = subprocess.run([shutil.which("ffprobe") or "/usr/bin/ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(media_path)], capture_output=True, check=True)
        streams = json.loads(ffprobe.stdout)["streams"]
        video = next(stream for stream in streams if stream["codec_type"] == "video")
        self.check(int(video["nb_read_frames"]) == report["expectedFrames"], name + ": independent ffprobe frame count")
        self.check(all(stream["codec_type"] != "audio" for stream in streams), name + ": independent ffprobe confirms no audio stream")
        self.decode_bgra_frames(name, media_path, report)
        self.check(self.has_bmff_ftyp(media), name + ": media contains a valid BMFF ftyp atom")
        self.export_download_boundaries(name, job_path, media, ref)
        return report_bytes, media

    def has_bmff_ftyp(self, media):
        """Find a top-level ISO-BMFF ftyp atom without assuming its offset."""
        offset = 0
        while offset + 8 <= len(media):
            size = int.from_bytes(media[offset:offset + 4], "big")
            kind = media[offset + 4:offset + 8]
            header = 8
            if size == 1:
                if offset + 16 > len(media):
                    return False
                size = int.from_bytes(media[offset + 8:offset + 16], "big")
                header = 16
            elif size == 0:
                size = len(media) - offset
            if size < header or offset + size > len(media):
                return False
            if kind == b"ftyp":
                return True
            offset += size
        return False

    def decode_bgra_frames(self, name, media_path, report):
        """Decode every exported frame independently and validate byte/alpha coverage."""
        width, height = int(report["width"]), int(report["height"])
        frame_bytes = width * height * 4
        decoded = subprocess.run(
            [shutil.which("ffmpeg") or "/usr/bin/ffmpeg", "-v", "error", "-i", str(media_path),
             "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "bgra", "-"],
            capture_output=True, check=True)
        raw = decoded.stdout
        self.check(len(raw) % frame_bytes == 0, name + ": BGRA output has complete frame lengths")
        frame_count = len(raw) // frame_bytes
        self.check(frame_count == report["expectedFrames"], name + ": BGRA decode frame count")
        alpha = raw[3::4]
        if report["hasAlpha"]:
            self.check(any(value < 255 for value in alpha), name + ": transparent export retains non-opaque alpha")
        else:
            self.check(all(value == 255 for value in alpha), name + ": opaque export has opaque alpha")

    def export_download_boundaries(self, name, job_path, media, ref):
        headers, _, _ = self.fetch(name + "-etag", job_path + "/export-content", expected=media)
        etag = headers.get("ETag")
        self.check(etag == '"' + ref["artifact"]["sha256"] + '"', name + ": ETag is artifact SHA")
        self.fetch(name + "-head", job_path + "/export-content", method="HEAD", expected=b"")
        self.fetch(name + "-range", job_path + "/export-content", headers={"Range": "bytes=0-31"}, status=206, expected=media[:32])
        self.fetch(name + "-suffix-range", job_path + "/export-content", headers={"Range": "bytes=-16"}, status=206, expected=media[-16:])
        self.fetch(name + "-cached", job_path + "/export-content", headers={"If-None-Match": etag}, status=304, expected=b"")
        self.fetch(name + "-invalid-range", job_path + "/export-content", headers={"Range": "bytes=999999999-"}, status=416, expected=b"", require_error_body=False)
        outsider = self.register(name + "-foreign-client", select=False)["clientId"]
        self.fetch(name + "-foreign-report", job_path + "/export-result", status=404, client=outsider)
        self.fetch(name + "-foreign-content", job_path + "/export-content", status=404, client=outsider)
        self.fetch(name + "-foreign-diagnostics", job_path + "/export-diagnostics", status=404, client=outsider)
        self.fetch(name + "-anonymous-content", job_path + "/export-content", status=401, anonymous=True)
        self.request(name + "-bad-auth-report", "GET", job_path + "/export-result", headers={"Authorization": "Bearer " + "0" * 64}, status=401, code="unauthorizedClient")
        self.request(name + "-diagnostic-limit-low", "GET", job_path + "/export-diagnostics?limit=0", status=400, code="diagnosticLimitExceeded")
        self.request(name + "-diagnostic-limit-high", "GET", job_path + "/export-diagnostics?limit=129", status=400, code="diagnosticLimitExceeded")
        self.request(name + "-foreign-cursor", "GET", job_path + "/export-diagnostics?cursor=foreign&limit=1", status=409, code="diagnosticCursorExpired")

    def export_sse(self, name, cursor, terminal):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + "/api/v1/events", headers={
            "X-Mako-Client-Id": self.client, "X-Mako-Host-Instance-Id": self.host,
            "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": cursor})
        events, current = [], []
        with self.http.open(request, timeout=15) as response:
            self.check(response.status == 200, name + ": SSE HTTP200")
            while len(events) < 256:
                line = response.readline().decode("utf-8").rstrip("\r\n")
                if line:
                    current.append(line); continue
                if not current: continue
                data = [value[6:] for value in current if value.startswith("data: ")]
                current = []
                if not data: continue
                event = json.loads("\n".join(data)); events.append(event)
                if event.get("type") == "job.terminal" and (event.get("job") or {}).get("jobId") == terminal["jobId"]:
                    self.check(event["job"]["state"] == terminal["state"], name + ": terminal SSE agrees with job ledger")
                    self.sse_events.extend(events)
                    preview.save_json(self.output / (name + ".json"), events)
                    return
        raise AssertionError(name + ": terminal SSE event missing")

    def prepare_long_fixture(self):
        source_dir = self.output / "profile/projects/scale"
        long_dir = self.output / "profile/projects/scale-long"
        import shutil
        shutil.copytree(source_dir, long_dir)
        project = long_dir / "project.json"
        model = json.loads(project.read_text())
        model["composition"]["outputDurationUs"] = 10_000_000
        project.write_text(json.dumps(model, ensure_ascii=False, indent=2) + "\n")

    def open_directory(self, label, relative):
        handle = self.request(label + "-directory", "POST", "/api/v1/file-handles", status=201,
                              value={"rootGrantId": "projects", "relativePath": relative})["handleId"]
        accepted = self.request(label + "-open", "POST", "/api/v1/project-sessions/open-jobs", status=202,
                                value={"projectDirectoryHandleId": handle})
        opened = self.job(label + "-open", accepted)["result"]
        source = {"kind": "project", "sourceId": opened["projectSessionId"], "sourceRevision": opened["projectRevision"]}
        self.check(opened.get("isReadOnly") is False, label + ": writable package project opened")
        return source

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False)
        self.prepare()
        self.prepare_long_fixture()
        self.start("package-export")
        self.connect("package-export")
        capabilities = self.request("export-capabilities", "GET", "/api/v1/export-capabilities")
        self.check(capabilities["enabled"] is True, "export capability enabled")
        self.check("package-lyrics-scene-v1" in (capabilities.get("sourceProfiles") or []), "package export source advertised")
        source = self.open_directory("scale", "scale")
        cursor = self.request("before-opaque", "GET", "/api/v1/clients/" + self.client + "/state")["eventCursor"]
        opaque = self.export_job("package-opaque", source, "opaqueMp4", False)
        opaque_terminal = self.wait_terminal("package-opaque", opaque)
        opaque_raw, opaque_media = self.export_report_and_media("package-opaque", opaque_terminal)
        self.export_sse("package-opaque", cursor, opaque_terminal)
        self.check(opaque_terminal["input"]["sourceProfile"] == "package-lyrics-scene-v1", "opaque source profile persisted")
        self.request("package-audio-rejected", "POST", "/api/v1/project-sessions/" + source["sourceId"] + "/export-jobs",
                     value={"expectedRevision": source["sourceRevision"], "format": "opaqueMp4", "includeAudio": True, "metadata": self.metadata("package-audio-rejected")}, status=422, code="exportInputRejected")

        transparent = self.export_job("package-transparent", source, "transparentProRes4444", False)
        transparent_terminal = self.wait_terminal("package-transparent", transparent)
        transparent_raw, transparent_media = self.export_report_and_media("package-transparent", transparent_terminal)
        self.check(json.loads(transparent_raw)["hasAlpha"] is True, "transparent package export reports alpha")

        long_source = self.open_directory("scale-long", "scale-long")
        cancel = self.export_job("package-cancel", long_source, "opaqueMp4", False)
        _, cancel_worker = self.wait_export_active("package-cancel", cancel)
        self.signal_worker("package-cancel", cancel_worker, signal.SIGSTOP)
        self.cancel_job("package-cancel-request", cancel)
        cancelled = self.wait_terminal("package-cancel", cancel, "cancelled")
        self.check(cancelled.get("exportResult") is None and cancelled.get("failureReportRef") is None, "cancelled package export publishes no artifact/report")
        self.request("package-cancel-no-report", "GET", "/api/v1/jobs/" + cancel["jobId"] + "/export-result", status=404, code="exportArtifactNotFound")

        deadline = self.export_job("package-deadline", long_source, "opaqueMp4", False)
        _, deadline_worker = self.wait_export_active("package-deadline", deadline)
        self.signal_worker("package-deadline", deadline_worker, signal.SIGSTOP)
        timed = self.wait_terminal("package-deadline", deadline, "failed", timeout=30)
        self.check(timed.get("failureCode") == "WorkerExportTimedOut" and not timed.get("cancellationRequested"), "deadline has stable WorkerExportTimedOut")
        self.request("package-deadline-no-report", "GET", "/api/v1/jobs/" + deadline["jobId"] + "/export-result", status=404, code="exportArtifactNotFound")

        close_job = self.export_job("package-close", long_source, "opaqueMp4", False)
        _, close_worker = self.wait_export_active("package-close", close_job)
        self.signal_worker("package-close", close_worker, signal.SIGSTOP)
        close_path = "/api/v1/project-sessions/" + long_source["sourceId"] + "/close"
        self.request("package-close-reject", "POST", close_path, value={"expectedRevision": long_source["sourceRevision"], "policy": "discard", "activeJobs": "reject", "metadata": self.metadata("package-close-reject")}, status=409, code="activeJobs")
        close_accepted = self.request("package-close-cancel-wait", "POST", close_path, value={"expectedRevision": long_source["sourceRevision"], "policy": "discard", "activeJobs": "cancelAndWait", "metadata": self.metadata("package-close-cancel-wait")}, status=202)
        self.job("package-close-job", close_accepted)
        closed = self.wait_terminal("package-close", close_job, "cancelled")
        self.check(closed.get("exportResult") is None, "close cancellation publishes no artifact")

        # Previous-host recovery: keep the successful report/media, then shut
        # down with a fresh active package export and verify both recovery facts.
        recovery_source = self.open_directory("recovery-long", "scale-long")
        active = self.export_job("package-recovery-active", recovery_source, "opaqueMp4", False)
        _, active_worker = self.wait_export_active("package-recovery-active", active)
        self.signal_worker("package-recovery-active", active_worker, signal.SIGSTOP)
        old_host = self.host
        self.stop("package-export-restart")
        self.start("package-export-restarted")
        self.connect("package-export-restarted")
        self.check(self.host != old_host, "restart creates a new host identity")
        recovered = self.request("package-recovery-jobs", "GET", "/api/v1/recovery-jobs")["jobs"]
        previous = next(item for item in recovered if item["job"]["jobId"] == opaque_terminal["jobId"])
        self.check(previous["previousHostInstanceId"] == old_host and previous["job"]["state"] == "succeeded", "successful package report survives host restart")
        recovery_active = next(item["job"] for item in recovered if item["job"]["jobId"] == active["jobId"])
        self.check(recovery_active["state"] == "cancelled" and recovery_active.get("exportResult") is None, "active package export persists cancelled after orderly restart")
        recovery_path = "/api/v1/recovery-jobs/" + opaque_terminal["jobId"]
        self.fetch("package-recovery-report", recovery_path + "/export-result", expected=opaque_raw)
        self.fetch("package-recovery-media", recovery_path + "/export-content", expected=opaque_media)
        self.check(not self.worker_identities_now(), "no package Worker replayed after restart")
        return {"opaque": opaque_terminal, "transparent": transparent_terminal, "cancelled": cancelled, "timedOut": timed, "closed": closed}

    def run_old_worker_rejection(self):
        """Exercise an older Worker that predates package export admission."""
        self.output.mkdir(parents=True, exist_ok=False)
        self.prepare()
        self.start("package-old-worker")
        self.connect("package-old-worker")
        capabilities = self.request("old-worker-export-capabilities", "GET", "/api/v1/export-capabilities")
        self.check("package-lyrics-scene-v1" not in (capabilities.get("sourceProfiles") or []),
                   "old Worker withholds package export source profile")
        source = self.open_directory("old-worker-scale", "scale")
        self.request("old-worker-package-export-rejected", "POST",
                     "/api/v1/project-sessions/" + source["sourceId"] + "/export-jobs",
                     value={"expectedRevision": source["sourceRevision"], "format": "opaqueMp4",
                            "includeAudio": False, "metadata": self.metadata("old-worker-package-export-rejected")},
                     status=422, code="taskUnavailable")
        self.check(not self.worker_identities_now(), "old Worker rejection leaves no export Worker running")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dist", type=Path, required=True)
    parser.add_argument("--old-dist", type=Path,
                        help="matched historical Server/Worker deployment for --old-worker")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--oracle", type=Path, required=True)
    parser.add_argument("--old-worker", action="store_true",
                        help="run only the historical Worker package-export rejection matrix")
    args = parser.parse_args()
    if args.old_worker and args.old_dist is None:
        parser.error("--old-worker requires --old-dist")
    # The normal slice uses a bounded deadline so the stopped Worker reaches
    # the timeout path promptly. Keep that override local to this probe rather
    # than changing the shared preview launcher default.
    os.environ.setdefault("MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS", "8")
    dist = (args.old_dist if args.old_worker else args.dist).resolve()
    probe = PackageExportProbe(args.output.resolve(), dist, args.oracle.resolve())
    failure = None
    result = None
    try:
        result = probe.run_old_worker_rejection() if args.old_worker else probe.run()
    except BaseException as error:
        failure = error
        (probe.output / "failure.txt").write_text(str(error) + "\n", encoding="utf-8")
    finally:
        try:
            probe.stop("package-export-final")
        except BaseException as error:
            failure = failure or error
        try:
            scanned = probe.scan_credentials()
        except BaseException as error:
            failure = failure or error
            scanned = None
    summary = {"status": "failed" if failure else "passed", "error": str(failure) if failure else None,
               "httpRequests": probe.request_count, "assertions": len(probe.assertions),
               "credentialScannedFiles": scanned, "runs": probe.runs,
               "observedIdentities": [{"pid": pid, "startTicks": ticks} for pid, ticks in sorted(probe.observed)],
               "exportResults": result, "sseEvents": len(probe.sse_events),
               "scope": "Fresh package-lyrics-scene-v1 no-audio opaque/transparent export, report/media identity, diagnostics, SSE, ACL/range/HEAD, includeAudio rejection, cancel/deadline/close and orderly previous-host recovery; no package audio, external media/font closure, analysis or measurement."}
    preview.save_json(probe.output / "summary.json", summary)
    preview.save_json(probe.output / "evidence-manifest.json", {"files": {
        str(path.relative_to(probe.output)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(probe.output.rglob("*")) if path.is_file() and path.name != "evidence-manifest.json"}})
    print(json.dumps(summary, ensure_ascii=False))
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
