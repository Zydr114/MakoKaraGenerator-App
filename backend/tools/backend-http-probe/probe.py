#!/usr/bin/env python3
"""Exercise the real loopback server, retaining requests and terminal evidence."""

import argparse
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import secrets
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import time
import traceback
from datetime import datetime, timedelta, timezone
import urllib.error
import urllib.parse
import urllib.request
import wave


def utc_now():
    return datetime.now(timezone.utc)


def iso(value):
    return value.isoformat().replace("+00:00", "Z")


def save_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


class Probe:
    def __init__(self, output, server_command):
        self.output = output
        self.server_command = server_command
        self.assertions = []
        self.request_count = 0
        self.host = None
        self.base_url = None
        self.client = None
        self.tokens = {}
        self.registration = None
        self.bootstrap = None
        self.metadata_counter = 0
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.final_state = None
        self.quota_server = None
        self.owned_servers = []

    def check(self, condition, label):
        if not condition:
            raise AssertionError(label)
        self.assertions.append(label)

    def request(self, name, method, path, *, value=None, raw=None, client="default",
                content_type=None, host="default", headers=None, status=200, code=None):
        self.request_count += 1
        prefix = f"{self.request_count:03d}-{name}"
        request_headers = dict(headers or {})
        selected_client = self.client if client == "default" else client
        selected_host = self.host if host == "default" else host
        if selected_client is not None:
            request_headers["X-Mako-Client-Id"] = selected_client
        if selected_host is not None:
            request_headers["X-Mako-Host-Instance-Id"] = selected_host
        if selected_client is not None and selected_client in self.tokens:
            request_headers.setdefault("Authorization", "Bearer " + self.tokens[selected_client])
        data = raw
        if method == "POST" and path != "/api/v1/clients":
            self.metadata_counter += 1
            metadata = {"requestId": f"http-request-{self.metadata_counter}", "hostInstanceId": selected_host,
                        "validUntilUtc": iso(utc_now() + timedelta(seconds=90))}
            if path in ("/api/v1/assets", "/assets"):
                request_headers.setdefault("X-Mako-Request-Id", metadata["requestId"])
                request_headers.setdefault("X-Mako-Valid-Until-Utc", metadata["validUntilUtc"])
            elif isinstance(value, dict) and path.endswith(("project-sessions", "file-handles", "save-jobs", "open-jobs", "recover-jobs", "/close")):
                value = {**value, "metadata": value.get("metadata", metadata)}
        if value is not None:
            data = json.dumps(value, ensure_ascii=False).encode("utf-8")
            content_type = content_type or "application/json"
        if content_type is not None:
            request_headers["Content-Type"] = content_type
        save_json(self.output / (prefix + ".request.json"), {
            "method": method, "path": path, "headers": {key: ("<redacted>" if key.lower() == "authorization" else val) for key, val in request_headers.items()},
            "json": self.redact(value), "bodyBytes": len(data or b""),
            "bodySha256": hashlib.sha256(data or b"").hexdigest(),
        })
        request = urllib.request.Request(self.base_url + path, data=data,
                                         headers=request_headers, method=method)
        try:
            response = self.http.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            response_bytes = response.read()
            response_headers = dict(response.headers.items())
            actual_status = response.status
        response_text = response_bytes.decode("utf-8")
        try:
            body = json.loads(response_text)
        except json.JSONDecodeError:
            body = None
        safe_body = self.redact(body)
        save_json(self.output / (prefix + ".response.json"), {
            "status": actual_status, "headers": response_headers,
            "json": safe_body,
            # A malformed/non-JSON response is evidence by size/hash only. Keeping
            # its text would turn a future diagnostic detail into a credential log.
            "body": json.dumps(safe_body, ensure_ascii=False) if body is not None else
                    f"<non-json body: {len(response_bytes)} bytes, sha256={hashlib.sha256(response_bytes).hexdigest()}>"
        })
        detail = json.dumps(safe_body, ensure_ascii=False, separators=(",", ":")) if body is not None \
            else f"non-json body ({len(response_bytes)} bytes)"
        self.check(actual_status == status, f"{name}: HTTP {status}, got {actual_status}: {detail}" if actual_status != status
                   else f"{name}: HTTP {status}")
        self.check(isinstance(body, dict), f"{name}: structured JSON object")
        media_type = response_headers.get("Content-Type", "").split(";")[0].lower()
        self.check(media_type == ("application/problem+json" if status >= 400 else "application/json"),
                   f"{name}: correct response media type")
        if "protocolVersion" in body:
            self.check(body.get("protocolVersion") == "1", f"{name}: protocol version")
        if code is not None:
            permitted = [code] if isinstance(code, str) else code
            self.check(body.get("code") in permitted, f"{name}: stable error code {permitted}")
        return body

    @staticmethod
    def redact(value):
        if isinstance(value, dict):
            return {key: ("<redacted>" if key.lower() in ("accesstoken", "resumetoken", "bootstraptoken") else Probe.redact(item))
                    for key, item in value.items()}
        if isinstance(value, list):
            return [Probe.redact(item) for item in value]
        return value

    def register(self, name, *, select=True, resume=None):
        value = {"protocolVersion": "1"}
        if resume is not None:
            value.update(resumeClientId=resume["clientId"], resumeToken=resume["resumeToken"])
        registration = self.request(name, "POST", "/api/v1/clients", value=value, client=None,
                                    headers={"Authorization": "Bearer " + self.bootstrap}, status=201)
        self.tokens[registration["clientId"]] = registration["accessToken"]
        self.check(len(registration["accessToken"]) >= 32 and len(registration["resumeToken"]) >= 32, name + ": independent random credentials")
        self.check(registration["accessToken"] != registration["resumeToken"], name + ": access differs from recovery credential")
        if select:
            self.client, self.registration = registration["clientId"], registration
        return registration

    def metadata(self, name, seconds=90):
        return {"requestId": name, "hostInstanceId": self.host, "validUntilUtc": iso(utc_now() + timedelta(seconds=seconds))}

    def binary(self, name, path, *, headers=None, status=200, expected=None, client=None):
        self.request_count += 1
        client = client or self.client
        request = urllib.request.Request(self.base_url + path, headers={"X-Mako-Client-Id": client,
            "Authorization": "Bearer " + self.tokens[client], "X-Mako-Host-Instance-Id": self.host, **(headers or {})})
        try:
            response = self.http.open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            data, actual, result_headers = response.read(), response.status, dict(response.headers.items())
        prefix = f"{self.request_count:03d}-{name}"
        (self.output / (prefix + ".bin")).write_bytes(data)
        save_json(self.output / (prefix + ".response.json"), {"status": actual, "headers": result_headers,
                  "bodyBytes": len(data), "bodySha256": hashlib.sha256(data).hexdigest()})
        self.check(actual == status, name + f": HTTP{status}, got{actual}")
        if expected is not None:
            self.check(data == expected, name + ": exact immutable bytes")
        return result_headers, data

    def events(self, name, cursor):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + "/api/v1/events", headers={"X-Mako-Client-Id": self.client,
            "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": cursor})
        messages, current = [], []
        with self.http.open(request, timeout=5) as response:
            self.check(response.status == 200, name + ": SSE HTTP200")
            self.check(response.headers["Content-Type"].startswith("text/event-stream"), name + ": SSE media type")
            while True:
                line = response.readline().decode("utf-8").rstrip("\r\n")
                if not line:
                    if current:
                        messages.append("\n".join(current))
                        break
                    continue
                current.append(line)
        save_json(self.output / f"{self.request_count:03d}-{name}.sse.json", messages)
        return messages[0]

    def job(self, name, accepted, *, state="succeeded"):
        self.check(bool(accepted.get("jobId")), name + ": accepted job identity")
        deadline = time.monotonic() + 30
        count = 0
        while time.monotonic() < deadline:
            count += 1
            current = self.request(name + f"-poll-{count}", "GET", "/api/v1/jobs/" + accepted["jobId"])
            if current["state"] in ("succeeded", "failed", "cancelled"):
                self.check(current["state"] == state, name + ": terminal " + state + ", got " + current["state"])
                return current
            time.sleep(0.02)
        raise AssertionError(name + ": job failed to converge")

    def run_extended_http(self, audio, audio_bytes, session, second_id, target, timing, old_command, create_input):
        path = "/api/v1/project-sessions/" + session
        self.request("auth-no-bearer", "GET", path, headers={"Authorization": "Bearer " + "0" * 64}, status=401, code="unauthorizedClient")
        self.request("bad-host-authority", "GET", "/health/live", headers={"Host": "attacker.invalid"}, status=403, code="unauthorizedClient")
        self.request("bad-origin", "GET", "/health/live", headers={"Origin": "https://attacker.invalid"}, status=403, code="unauthorizedClient")
        self.request("same-origin", "GET", path, headers={"Origin": self.base_url})
        self.request("wrong-http-method", "PUT", path, status=405, code="invalidRequest")
        self.request("bearer-query-not-auth", "GET", path + "?access_token=" + "0" * 64, client=None, status=400, code="invalidRequest")
        old_access = self.registration["accessToken"]
        resumed = self.register("resume-owner", resume=self.registration)
        self.check(resumed["clientId"] == self.client and resumed["accessToken"] != old_access, "resume: stable owner with rotated access credential")
        self.request("rotated-token-rejected", "GET", path, headers={"Authorization": "Bearer " + old_access}, status=401, code="unauthorizedClient")
        self.request("heartbeat", "POST", f"/api/v1/clients/{self.client}/heartbeat", value={})
        state = self.request("client-atomic-state", "GET", f"/api/v1/clients/{self.client}/state")
        self.check(any(item["projectSessionId"] == session for item in state["sessions"]), "client-state: project retained after auth reconnect")
        self.check(all(item["eventCursor"] == state["eventCursor"] for item in state["sessions"]), "client-state: common captured cursor")
        gap = self.events("sse-gap", "0")
        self.check("event: resync.required" in gap, "SSE: bounded old cursor requires resync")
        before_cursor = state["eventCursor"]
        edit = self.request("event-time-edit", "POST", path + "/commands", value=self.command(6, "event-time-edit", {**timing, "startUs": "11001"}))
        replay = self.events("sse-replay", before_cursor)
        self.check("event: project.changed" in replay and '"projectRevision":"7"' in replay, "SSE: replay exact committed summary")
        self.check("id: " in replay, "SSE: decimal event id")
        content = "/api/v1/assets/" + audio["assetId"] + "/content"
        full_headers, _ = self.binary("asset-full", content, expected=audio_bytes)
        self.check(full_headers.get("Content-Type", "").startswith("audio/wav"), "binary: actual MIME")
        self.check(full_headers.get("ETag") == '"' + audio["sha256"] + '"', "binary: immutable SHA ETag")
        self.binary("asset-prefix-range", content, headers={"Range": "bytes=0-43"}, status=206, expected=audio_bytes[:44])
        self.binary("asset-suffix-range", content, headers={"Range": "bytes=-9"}, status=206, expected=audio_bytes[-9:])
        self.binary("asset-open-range", content, headers={"Range": f"bytes={len(audio_bytes)-8}-"}, status=206, expected=audio_bytes[-8:])
        invalid_headers, _ = self.binary("asset-unsatisfiable", content, headers={"Range": f"bytes={len(audio_bytes)}-"}, status=416, expected=b"")
        self.check(invalid_headers.get("Content-Range") == f"bytes */{len(audio_bytes)}", "binary: 416 complete length")
        self.binary("asset-multi-range-rejected", content, headers={"Range": "bytes=0-1,4-5"}, status=416, expected=b"")
        self.binary("asset-not-modified", content, headers={"If-None-Match": full_headers["ETag"]}, status=304, expected=b"")
        self.binary("asset-weak-cache-validator", content, headers={"If-None-Match": "W/" + full_headers["ETag"]}, status=304, expected=b"")
        self.binary("asset-if-range-mismatch", content, headers={"If-Range": '"old"', "Range": "bytes=0-43"}, status=200, expected=audio_bytes)
        self.request("root-grants", "GET", "/api/v1/file-roots")
        self.request("path-root-not-authorized", "POST", "/api/v1/file-handles", value={"rootGrantId": "unknown-root", "relativePath": "p"}, status=400, code="pathNotAllowed")
        for name, bad in (("traversal", "../escape"), ("absolute", "/tmp/escape"), ("url", "https://example.org/p")):
            self.request("handle-" + name, "POST", "/api/v1/file-handles", value={"rootGrantId": "probe-root", "relativePath": bad}, status=400, code="pathNotAllowed")
        outside = self.output / "outside-root"
        outside.mkdir()
        (self.output / "projects" / "outside-link").symlink_to(outside, target_is_directory=True)
        self.request("handle-symlink-escape", "POST", "/api/v1/file-handles", value={"rootGrantId": "probe-root", "relativePath": "outside-link/p"}, status=400, code="pathNotAllowed")
        handle_input = {"rootGrantId": "probe-root", "relativePath": "saved-project", "metadata": self.metadata("save-handle")}
        handle = self.request("create-save-handle", "POST", "/api/v1/file-handles", value=handle_input, status=201)
        same_handle = self.request("retry-save-handle", "POST", "/api/v1/file-handles", value=handle_input, status=201)
        self.check(same_handle == handle, "handle receipt: stable identity")
        self.request("cross-endpoint-request-id", "POST", "/api/v1/project-sessions", value={**create_input, "metadata": handle_input["metadata"]}, status=409, code="requestIdReuse")
        save_input = {"expectedRevision": "7", "targetHandleId": handle["handleId"], "metadata": self.metadata("save-snapshot")}
        save = self.request("save-snapshot", "POST", path + "/save-jobs", value=save_input, status=202)
        before_edit_job = self.request("save-job-before-later-edit", "GET", "/api/v1/jobs/" + save["jobId"])
        concurrent = self.request("edit-after-save-capture", "POST", path + "/commands", value=self.command(7, "edit-after-save-capture", {**timing, "startUs": "12001"}))
        self.check(concurrent["projectRevision"] == "8", "save: editing continues after capture")
        terminal = self.job("save", save)
        save_json(self.output / "save-edit-order.json", {"acceptedState": save["state"], "beforeEditState": before_edit_job["state"],
            "savedProjectRevision": terminal["savedProjectRevision"], "currentProjectRevision": concurrent["projectRevision"],
            "httpIoOverlapObserved": before_edit_job["state"] not in ("succeeded", "failed", "cancelled"),
            "scope": "HTTP proves snapshot isolation and later dirty state; Hosting controlled storage tests prove IO overlap"})
        self.check(terminal["savedProjectRevision"] == "7", "save: captured revision saved")
        retry_save = self.request("retry-save", "POST", path + "/save-jobs", value=save_input, status=202)
        self.check(retry_save["jobId"] == save["jobId"], "save receipt: same job after later edits")
        saved_summary = self.request("after-save-summary", "GET", path)
        self.check(saved_summary["hasPath"] and saved_summary["isDirty"] and saved_summary["projectRevision"] == "8", "save: newer live edit remains dirty")
        self.request("dirty-close-rejected", "POST", path + "/close", value={"expectedRevision": "8"}, status=409, code="dirtySession")
        close_input = {"expectedRevision": "8", "policy": "discard", "activeJobs": "wait", "metadata": self.metadata("close-owned")}
        close = self.request("close", "POST", path + "/close", value=close_input, status=202)
        self.job("close", close)
        retry_close = self.request("retry-close", "POST", path + "/close", value=close_input, status=202)
        self.check(retry_close["jobId"] == close["jobId"], "close receipt: stable closed job")
        self.request("closed-session-query", "GET", path, status=404, code="sessionNotFound")
        open_input = {"projectDirectoryHandleId": handle["handleId"], "metadata": self.metadata("open-saved")}
        opened_job = self.request("reopen", "POST", "/api/v1/project-sessions/open-jobs", value=open_input, status=202)
        opened = self.job("reopen", opened_job)
        reopened_id = opened["result"]["projectSessionId"]
        reopened = self.request("reopened-lyrics", "GET", f"/api/v1/project-sessions/{reopened_id}/lyrics/lines?branchId=primary")
        self.check(reopened["lines"][0]["time"]["startUs"] == "11001", "reopen: exact saved snapshot excludes newer edit")
        self.check(opened["result"]["hasPath"] and not opened["result"]["isDirty"], "reopen: clean disk-backed session")
        copied_audio = opened["result"]["audioAssetId"]
        self.binary("reopened-audio-closure", "/api/v1/assets/" + copied_audio + "/content", expected=audio_bytes)
        conflict_job = self.request("open-write-lease-conflict", "POST", "/api/v1/project-sessions/open-jobs", value={"projectDirectoryHandleId": handle["handleId"]}, status=202)
        conflict = self.job("write-lease-conflict", conflict_job, state="failed")
        self.check(conflict["error"]["code"] == "writeLeaseConflict", "write lease: accepted open failed before publish")
        readonly = self.request("open-readonly", "POST", "/api/v1/project-sessions/open-jobs", value={"projectDirectoryHandleId": handle["handleId"], "access": "read"}, status=202)
        ro = self.job("readonly", readonly)
        self.request("readonly-command-rejected", "POST", "/api/v1/project-sessions/" + ro["result"]["projectSessionId"] + "/commands",
                     value=self.command(0, "readonly-undo", {"type": "history.undo"}), status=403, code="readOnlySession")
        recovery = self.request("recovery-candidates", "GET", "/api/v1/recovery-candidates")
        self.check(all("recoveryId" in item for item in recovery["candidates"]), "recovery: only actual retained candidates exposed")
        self.reopened_id = reopened_id
        self.binary("referenced-audio-after-TTL", content, expected=audio_bytes)

    def command(self, revision, name, command, *, deadline=None):
        return {"requestId": name, "hostInstanceId": self.host,
                "validUntilUtc": iso(deadline or (utc_now() + timedelta(seconds=90))),
                "expectedRevision": str(revision), "command": command}

    def read_state(self, name, session, revision, undo_count, redo_count):
        path = f"/api/v1/project-sessions/{session}"
        summary = self.request(name + "-summary", "GET", path)
        history = self.request(name + "-history", "GET", path + "/history")
        lyrics = self.request(name + "-lyrics", "GET", path + "/lyrics/lines?branchId=primary")
        for model in (summary, history, lyrics):
            self.check(model["projectRevision"] == str(revision), name + ": matching exact revision")
        self.check(summary["canUndo"] == history["canUndo"] == (undo_count > 0), name + ": undo availability")
        self.check(summary["canRedo"] == history["canRedo"] == (redo_count > 0), name + ": redo availability")
        self.check(history["undoCount"] == undo_count and history["redoCount"] == redo_count,
                   name + ": history counts")
        self.check(summary["lyricsLineCount"] == len(lyrics["lines"]), name + ": lyrics count")
        self.check(summary["isDirty"] and summary["hasPath"] is False, name + ": unsaved new project remains dirty")
        self.check(isinstance(summary.get("eventCursor"), str), name + ": implemented event cursor")
        return summary, history, lyrics

    def check_command(self, name, response, previous, revision, changed):
        self.check(response["previousRevision"] == str(previous), name + ": previous revision")
        self.check(response["projectRevision"] == str(revision), name + ": resulting revision")
        self.check(response["changed"] is changed, name + ": changed flag")
        self.check(isinstance(response.get("eventCursor"), str), name + ": implemented event cursor")
        self.check(response["isDirty"], name + ": dirty new project")

    def run_http(self):
        health = self.request("health", "GET", "/health/live", client=None, host=None)
        self.host = health["hostInstanceId"]
        self.check(health["status"] == "live", "health: server live")
        self.register("register-owner")
        capabilities = self.request("capabilities", "GET", "/api/v1/capabilities")
        self.check(capabilities["hostInstanceId"] == self.host, "capabilities: same host identity")
        self.check(0 < capabilities["maxCommandRetryWindowSeconds"] <= 600, "capabilities: bounded retry window")
        self.check(capabilities["maxCommandReceiptsPerSession"] > 0, "capabilities: bounded receipt count")
        datetime.fromisoformat(capabilities["serverTimeUtc"].replace("Z", "+00:00"))
        self.check(set(capabilities["features"]) >= {
            "assets.upload", "project-sessions.create", "project-sessions.summary", "project-sessions.lyrics.read",
            "project-sessions.history.read", "project-sessions.commands.lyrics.setTimeRange",
            "project-sessions.commands.history"}, "capabilities: retained implemented B2-B features")

        audio_path = self.output / "fixture.wav"
        with wave.open(str(audio_path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(44100)
            audio.writeframes(b"".join(struct.pack("<h", round(4000 * math.sin(2 * math.pi * 440 * sample / 44100)))
                                       for sample in range(88200)))
        lyrics_bytes = "[00:00.00]HTTP probe\n[00:00.80]Second line\n".encode("utf-8")
        (self.output / "fixture.lrc").write_bytes(lyrics_bytes)
        media = subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(audio_path)],
                               capture_output=True, text=True, check=True)
        (self.output / "ffprobe-fixture.json").write_text(media.stdout, encoding="utf-8")
        media_json = json.loads(media.stdout)
        self.check(media_json["streams"][0]["sample_rate"] == "44100", "fixture: real 44.1kHz WAV")
        self.check(media_json["streams"][0]["channels"] == 1, "fixture: mono WAV")
        self.check(float(media_json["format"]["duration"]) == 2, "fixture: real two-second WAV")
        for executable in ("ffmpeg", "ffprobe"):
            version = subprocess.run([executable, "-version"], capture_output=True, text=True, check=True)
            (self.output / (executable + "-version.txt")).write_text(version.stdout, encoding="utf-8")
        audio = self.request("upload-audio", "POST", "/api/v1/assets", raw=audio_path.read_bytes(),
                             content_type="audio/wav", headers={"X-Mako-Asset-Name": "fixture.wav"}, status=201)
        lyrics = self.request("upload-lyrics", "POST", "/api/v1/assets", raw=lyrics_bytes,
                              content_type="text/plain", headers={"X-Mako-Asset-Name": "fixture.lrc"}, status=201)
        self.check(audio["sha256"] == hashlib.sha256(audio_path.read_bytes()).hexdigest(), "upload: audio fixed hash")
        self.check(lyrics["sha256"] == hashlib.sha256(lyrics_bytes).hexdigest(), "upload: lyrics fixed hash")
        create_input = {"name": "Real HTTP command probe", "audioAssetId": audio["assetId"],
                        "lyricsAssetId": lyrics["assetId"]}
        create = self.request("create", "POST", "/api/v1/project-sessions", value=create_input, status=201)
        session = create["projectSessionId"]
        path = f"/api/v1/project-sessions/{session}"
        commands = path + "/commands"
        self.check(create["audioDurationUs"] == 2_000_000, "create: FFmpeg-decoded audio duration")
        self.check(create["projectRevision"] == "0", "create: initial revision")
        self.check(create["isDirty"] and create["hasPath"] is False, "create: unsaved without disk path")
        _, _, initial = self.read_state("initial", session, 0, 0, 0)
        self.check(len(initial["lines"]) == 2, "query: imported lines")
        line = initial["lines"][0]
        self.check(bool(line["id"]) and bool(line["tokens"]), "query: stable line and token IDs")
        target = {"branchId": initial["branchId"], "lineId": line["id"]}
        timing = {"type": "lyrics.setTimeRange", "target": target, "startUs": "10001", "endUs": "700003"}
        first_request = self.command(0, "first-line-time", timing)
        first = self.request("line-time", "POST", commands, value=first_request)
        self.check_command("line-time", first, 0, 1, True)
        self.check(first["changeSet"]["branchIds"] == ["primary"]
                   and first["changeSet"]["lineIds"] == [line["id"]], "line-time: stable change targets")
        self.check(first["changeSet"]["areas"] == ["lyricsTiming", "timeline", "renderInputs"], "line-time: actual invalidation areas")
        _, _, edited = self.read_state("edited", session, 1, 1, 0)
        self.check(edited["lines"][0]["time"] == {"startUs": "10001", "endUs": "700003"}, "query: exact integer microseconds")
        self.check(edited["lines"][1] == initial["lines"][1], "line-time: unrelated line unchanged")
        self.check(edited["lines"][0]["id"] == line["id"] and
                   [t["id"] for t in edited["lines"][0]["tokens"]] == [t["id"] for t in line["tokens"]],
                   "line-time: all stable identities preserved")
        normalized_retry = copy.deepcopy(first_request)
        normalized_retry["validUntilUtc"] = normalized_retry["validUntilUtc"].replace("Z", "+00:00")
        retry = self.request("line-time-retry", "POST", commands, value=normalized_retry)
        self.check(retry == first, "retry: identical receipt despite old expectedRevision and equivalent UTC offset")
        self.read_state("after-retry", session, 1, 1, 0)
        self.request("stale-revision", "POST", commands,
                     value=self.command(0, "stale-revision", {"type": "history.undo"}), status=409, code="revisionConflict")
        reused = copy.deepcopy(first_request)
        reused["command"]["startUs"] = "10002"
        self.request("request-id-reuse", "POST", commands, value=reused, status=409, code="requestIdReuse")

        undo = self.request("undo", "POST", commands, value=self.command(1, "undo-line", {"type": "history.undo"}))
        self.check_command("undo", undo, 1, 2, True)
        _, history_after_undo, undone = self.read_state("undone", session, 2, 0, 1)
        self.check(undone["lines"] == initial["lines"], "undo: exact initial lyrics restored")
        original_timing = {"type": "lyrics.setTimeRange", "target": target, **line["time"]}
        no_op = self.request("no-op-preserves-redo", "POST", commands, value=self.command(2, "same-original-time", original_timing))
        self.check_command("no-op-original", no_op, 2, 2, False)
        _, same_history, _ = self.read_state("no-op-redo", session, 2, 0, 1)
        self.check(same_history == history_after_undo, "no-op: redo branch and history preserved")
        redo = self.request("redo", "POST", commands, value=self.command(2, "redo-line", {"type": "history.redo"}))
        self.check_command("redo", redo, 2, 3, True)
        _, history_after_redo, redone = self.read_state("redone", session, 3, 1, 0)
        self.check(redone["lines"] == edited["lines"], "redo: exact edited lyrics restored")
        no_op = self.request("no-op-preserves-undo", "POST", commands, value=self.command(3, "same-edited-time", timing))
        self.check_command("no-op-edited", no_op, 3, 3, False)
        self.check(no_op["changeSet"]["areas"] == [], "no-op: no view invalidation")
        _, same_history, _ = self.read_state("no-op-undo", session, 3, 1, 0)
        self.check(same_history == history_after_redo, "no-op: existing undo history preserved")

        token_target = {**target, "tokenId": line["tokens"][0]["id"]}
        token_timing = {"type": "lyrics.setTimeRange", "target": token_target,
                        "startUs": "20003", "endUs": "400007"}
        token_response = self.request("token-time", "POST", commands, value=self.command(3, "token-time", token_timing))
        self.check_command("token-time", token_response, 3, 4, True)
        self.check(token_response["changeSet"]["tokenIds"] == [token_target["tokenId"]], "token-time: stable token change target")
        _, _, token_edited = self.read_state("token-edited", session, 4, 2, 0)
        self.check(token_edited["lines"][0]["tokens"][0]["time"] == {"startUs": "20003", "endUs": "400007"}, "token query: exact microseconds")
        self.request("undo-token", "POST", commands, value=self.command(4, "undo-token", {"type": "history.undo"}))
        _, _, token_undone = self.read_state("token-undone", session, 5, 1, 1)
        self.check(token_undone["lines"] == redone["lines"], "token undo: previous lyrics restored")
        self.request("redo-token", "POST", commands, value=self.command(5, "redo-token", {"type": "history.redo"}))
        _, _, token_redone = self.read_state("token-redone", session, 6, 2, 0)
        self.check(token_redone["lines"] == token_edited["lines"], "token redo: edited lyrics restored")

        # Protocol/ownership rejections must leave the same revision and history.
        for name, replacement in (("missing-audio-id", {"lyricsAssetId": lyrics["assetId"]}),
                                  ("null-audio-id", {"audioAssetId": None, "lyricsAssetId": lyrics["assetId"]}),
                                  ("missing-lyrics-id", {"audioAssetId": audio["assetId"]}),
                                  ("null-lyrics-id", {"audioAssetId": audio["assetId"], "lyricsAssetId": None})):
            self.request(name, "POST", "/api/v1/project-sessions", value={"name": "invalid", **replacement}, status=400, code="invalidRequest")
        for suffix in ("/api/v1/project-sessions", commands):
            name = "create" if suffix == "/api/v1/project-sessions" else "command"
            self.request(name + "-bad-json", "POST", suffix, raw=b"{", content_type="application/json", status=400, code="invalidRequest")
            self.request(name + "-null-json", "POST", suffix, raw=b"null", content_type="application/json", status=400, code="invalidRequest")
            self.request(name + "-wrong-content-type", "POST", suffix, raw=b"{}", content_type="text/plain", status=415, code="unsupportedMediaType")
        self.request("old-host-header", "GET", path, host="host-old", status=409, code="hostRestarted")
        old_host = self.command(6, "old-host-body", timing)
        old_host["hostInstanceId"] = "host-old"
        self.request("old-host-body", "POST", commands, value=old_host, status=409, code="hostRestarted")
        self.request("old-host-missing-session", "POST", "/api/v1/project-sessions/old-missing-session/commands",
                     value=old_host, host=None, status=409, code="hostRestarted")
        self.request("expired-request", "POST", commands,
                     value=self.command(6, "expired", timing, deadline=utc_now() - timedelta(seconds=1)), status=409, code="requestExpired")
        too_far = self.command(6, "window-too-long", timing,
                               deadline=utc_now() + timedelta(seconds=capabilities["maxCommandRetryWindowSeconds"] + 10))
        self.request("deadline-outside-window", "POST", commands, value=too_far, status=400, code="invalidRequest")
        for name, removed, expected_code, status in (("missing-request-id", "requestId", "invalidRequest", 400),
                                                     ("missing-host-id", "hostInstanceId", "hostRestarted", 409),
                                                     ("missing-deadline", "validUntilUtc", "requestExpired", 409)):
            invalid = self.command(6, name, timing)
            del invalid[removed]
            self.request(name, "POST", commands, value=invalid, status=status, code=expected_code)
        for name, invalid_command, status, code in (
            ("unknown-command", {"type": "project.replace"}, 422, "commandNotSupported"),
            ("missing-target", {"type": "lyrics.setTimeRange", "startUs": "1", "endUs": "2"}, 400, "invalidRequest"),
            ("missing-endpoint", {"type": "lyrics.setTimeRange", "target": target, "startUs": "1"}, 400, "invalidRequest"),
            ("floating-time", {**timing, "startUs": "1.5"}, 400, "invalidRequest"),
            ("overflow-time", {**timing, "endUs": "9223372036854775808"}, 400, "invalidRequest"),
            ("negative-time", {**timing, "startUs": "-1"}, 422, "commandRejected"),
            ("reversed-time", {**timing, "startUs": "700004"}, 422, "commandRejected"),
            ("time-beyond-media", {**timing, "endUs": "2000001"}, 422, "commandRejected"),
            ("history-with-time-fields", {"type": "history.undo", "target": target}, 400, "invalidRequest"),
            ("missing-stable-target", {**timing, "target": {"branchId": "primary", "lineId": "no-such-line"}}, 422, "commandRejected")):
            self.request(name, "POST", commands, value=self.command(6, name, invalid_command), status=status, code=code)
        self.request("numeric-time-json", "POST", commands, value=self.command(6, "numeric-time", {**timing, "startUs": 1}), status=400, code="invalidRequest")
        self.request("unknown-command-json-field", "POST", commands,
                     value=self.command(6, "unknown-json-field", {**timing, "unrecognized": True}), status=400, code="invalidRequest")
        self.request("missing-client", "GET", path, client=None, status=401, code="unauthorizedClient")
        other = self.register("register-other", select=False)["clientId"]
        self.request("cross-client-summary", "GET", path, client=other, status=404, code="sessionNotFound")
        self.request("cross-client-command", "POST", commands, client=other, value=first_request, status=404, code="sessionNotFound")
        self.request("cross-client-assets", "POST", "/api/v1/project-sessions", client=other, value=create_input, status=401, code="resourceUnauthorized")
        self.request("missing-session", "POST", "/api/v1/project-sessions/no-such-session/commands", value=self.command(6, "missing-session-fresh", timing), status=404, code="sessionNotFound")
        self.request("unknown-route", "GET", "/api/v1/no-such-route", status=404, code="invalidRequest")
        self.request("no-event-stream", "GET", path + "/events", status=404, code="invalidRequest")
        self.request("save-invalid-input", "POST", path + "/save-jobs", value={}, status=400, code="invalidRequest")
        self.request("missing-branch", "GET", path + "/lyrics/lines", status=400, code="invalidRequest")
        self.request("unknown-branch", "GET", path + "/lyrics/lines?branchId=no-such-branch", status=404, code="commandRejected")
        self.request("path-traversal-upload", "POST", "/api/v1/assets", raw=b"bad", content_type="text/plain",
                     headers={"X-Mako-Asset-Name": "../escape.lrc"}, status=400, code="resourcePathNotAllowed")
        self.request("unsupported-upload-mime", "POST", "/api/v1/assets", raw=b"bad", content_type="application/x-unsafe",
                     headers={"X-Mako-Asset-Name": "unsafe.bin"}, status=415, code="unsupportedMediaType")
        self.request("empty-upload", "POST", "/api/v1/assets", raw=b"", content_type="text/plain",
                     headers={"X-Mako-Asset-Name": "empty.lrc"}, status=400, code="resourceEmpty")
        invalid_audio = self.request("invalid-audio-upload", "POST", "/api/v1/assets", raw=b"not-real-audio",
                                     content_type="audio/wav", headers={"X-Mako-Asset-Name": "invalid.wav"}, status=201)
        self.request("invalid-audio-create", "POST", "/api/v1/project-sessions",
                     value={**create_input, "audioAssetId": invalid_audio["assetId"]}, status=422,
                     code=("noAudioStream", "audioPreparationFailed"))
        _, _, rejected = self.read_state("after-rejections", session, 6, 2, 0)
        self.check(rejected["lines"] == token_redone["lines"], "rejections: all original data preserved")

        second = self.request("create-second-session", "POST", "/api/v1/project-sessions", value=create_input, status=201)
        second_id = second["projectSessionId"]
        self.check(second_id != session, "session: independent identity")
        _, _, second_lyrics = self.read_state("second-initial", second_id, 0, 0, 0)
        cross_session = copy.deepcopy(first_request)
        cross_session["command"]["target"]["lineId"] = second_lyrics["lines"][0]["id"]
        self.request("request-id-other-session", "POST", f"/api/v1/project-sessions/{second_id}/commands", value=cross_session,
                     status=409, code="requestIdReuse")

        # An expired previously accepted receipt must never become a fresh command.
        deadline = utc_now() + timedelta(seconds=1.5)
        expires = self.command(6, "short-lived-receipt", token_timing, deadline=deadline)
        expiring_result = self.request("short-lived-receipt", "POST", commands, value=expires)
        self.check_command("short-lived-receipt", expiring_result, 6, 6, False)
        time.sleep(max(0, (deadline - utc_now()).total_seconds()) + 0.05)
        self.request("expired-receipt-replay", "POST", commands, value=expires, status=409, code="requestExpired")
        summary, history, final_lyrics = self.read_state("final", session, 6, 2, 0)
        self.check(final_lyrics["lines"] == token_redone["lines"], "final: state unchanged after expired replay")
        self.run_extended_http(audio, audio_path.read_bytes(), session, second_id, target, timing, first_request, create_input)
        self.final_state = {"summary": summary, "history": history, "lyrics": final_lyrics}
        save_json(self.output / "final-state.json", self.final_state)

    def owned_phase(self, name, environment, action, *, expected_exit=0):
        original = (self.base_url, self.host, self.client, self.registration, self.bootstrap)
        resources = Path(tempfile.mkdtemp(prefix=name + "-resources-", dir=self.output))
        environment = {**environment, "MAKO_BACKEND_RESOURCE_ROOT": str(resources)}
        process, forced = None, False
        log_path = self.output / (name + "-server.log")
        try:
            with log_path.open("w") as log:
                process = subprocess.Popen(self.server_command + ["--urls", "http://127.0.0.1:0"], env=environment, stdout=log, stderr=subprocess.STDOUT)
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    ready = [line for line in log_path.read_text().splitlines() if line.startswith("BACKEND_READY ")]
                    if ready:
                        self.base_url, self.host = ready[-1].split()[1:3]
                        break
                    if process.poll() is not None:
                        raise RuntimeError(name + ": exited before ready")
                    time.sleep(0.02)
                else:
                    raise AssertionError(name + ": readiness timeout")
                self.bootstrap = environment.get("MAKO_BACKEND_BOOTSTRAP_TOKEN") or json.loads((resources / "bootstrap.json").read_text())["bootstrapToken"]
                self.register(name + "-register")
                action(process, environment)
        finally:
            if process is not None:
                if process.poll() is None:
                    process.terminate()
                try:
                    code = process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    forced = True
                    process.kill()
                    code = process.wait(timeout=5)
            else:
                code = None
            self.owned_servers.append({"name": name, "pid": process.pid if process else None, "exitCode": code,
                                       "forcedKill": forced, "expectedExitCode": expected_exit})
            if resources.exists():
                shutil.rmtree(resources)
            self.base_url, self.host, self.client, self.registration, self.bootstrap = original
        self.check(code == expected_exit and not forced, name + ": expected terminal process exit")
        self.check(not resources.exists(), name + ": owned resource cleanup")

    def run_peer_lease_http(self, environment):
        temporary = self.output / "peer-tmp"
        temporary.mkdir()
        environment = {**environment, "TMPDIR": str(temporary), "MAKO_BACKEND_STATE_ROOT": str(self.output / "peer-backend-state")}
        def peer(process, child_environment):
            handle = self.request("peer-shared-path-handle", "POST", "/api/v1/file-handles",
                                  value={"rootGrantId": "probe-root", "relativePath": "saved-project"}, status=201)
            accepted = self.request("peer-open-write-lease", "POST", "/api/v1/project-sessions/open-jobs",
                                    value={"projectDirectoryHandleId": handle["handleId"]}, status=202)
            terminal = self.job("peer-write-lease", accepted, state="failed")
            self.check(terminal["error"]["code"] == "writeLeaseConflict", "cross-process lease: different TMPDIR cannot bypass active shared writer")
            state = self.request("peer-state-after-conflict", "GET", f"/api/v1/clients/{self.client}/state")
            self.check(state["sessions"] == [], "cross-process lease: failed open never publishes partial session")
        self.owned_phase("lease-peer", environment, peer)

    def run_restart_http(self, environment):
        def restart(process, child_environment):
            candidates = self.request("restart-recovery-candidates", "GET", "/api/v1/recovery-candidates")["candidates"]
            self.check(len(candidates) > 0, "restart: durable autosave survives host resource root removal")
            candidate = next((item for item in candidates if not item["hasOriginalPath"]), candidates[0])
            recovered_job = self.request("recover-after-restart", "POST", "/api/v1/project-sessions/recover-jobs",
                 value={"recoveryId": candidate["recoveryId"]}, status=202)
            recovered = self.job("recover-after-restart", recovered_job)
            self.check(recovered["result"]["isDirty"], "recovery: explicit dirty recovered session")
            self.binary("recovery-fixed-audio", "/api/v1/assets/" + recovered["result"]["audioAssetId"] + "/content", expected=(self.output / "fixture.wav").read_bytes())
            diagnostics = self.request("restart-job-ledger", "GET", "/api/v1/recovery-jobs")["jobs"]
            self.check(any(item["job"]["kind"] == "save" and item["job"]["state"] == "succeeded" for item in diagnostics), "restart ledger: terminal save retained")
            self.check(all("/" not in (item.get("outputRootGrantId") or "") for item in diagnostics), "restart ledger: logical output grants only")
            save_record = next(item for item in diagnostics if item["job"]["kind"] == "save" and item["job"]["state"] == "succeeded")
            self.check(bool(save_record.get("projectFileSha256")), "restart ledger: actual saved project file hash")
        self.owned_phase("restart", environment, restart)

    def run_crash_ledger_http(self, environment):
        state_root = self.output / "crash-backend-state"
        environment = {**environment, "MAKO_BACKEND_STATE_ROOT": str(state_root)}
        def crash(process, child_environment):
            handle = self.request("crash-open-handle", "POST", "/api/v1/file-handles",
                                  value={"rootGrantId": "probe-root", "relativePath": "saved-project"}, status=201)
            for index in range(8):
                accepted = self.request(f"crash-open-accept-{index}", "POST", "/api/v1/project-sessions/open-jobs",
                                       value={"projectDirectoryHandleId": handle["handleId"], "access": "read"}, status=202)
                process.send_signal(signal.SIGSTOP)
                entry_path = state_root / "jobs" / (accepted["jobId"] + ".json")
                entry = json.loads(entry_path.read_text())
                save_json(self.output / f"crash-job-observation-{index}.json", entry)
                if entry["Job"]["State"] not in ("succeeded", "failed", "cancelled"):
                    self.crash_job, self.crash_host = accepted["jobId"], self.host
                    self.check(entry["HostInstanceId"] == self.host, "crash ledger: durable accepted job belongs to crashed host")
                    self.check(entry["Job"]["State"] in ("queued", "committing", "running"), "crash ledger: observed incomplete accepted operation")
                    process.kill()
                    process.wait(timeout=5)
                    return
                process.send_signal(signal.SIGCONT)
            raise AssertionError("could not observe an unfinished accepted job before owned-process crash")
        self.owned_phase("crash-ledger", environment, crash, expected_exit=-9)
        def inspect(process, child_environment):
            jobs = self.request("crashed-job-recovery-diagnostics", "GET", "/api/v1/recovery-jobs")["jobs"]
            unknown = next(item for item in jobs if item["job"]["jobId"] == self.crash_job)
            self.check(unknown["job"]["state"] == "unknown" and not unknown["job"]["canCancel"], "crash recovery: incomplete ledger becomes non-cancellable unknown")
            self.check(unknown["previousHostInstanceId"] == self.crash_host, "crash recovery: original host identity retained for diagnosis")
            state = self.request("crash-restart-no-replay-state", "GET", f"/api/v1/clients/{self.client}/state")
            self.check(state["sessions"] == [], "crash recovery: unknown job is not automatically replayed")
            self.request("crash-host-command-rejected", "POST", "/api/v1/project-sessions/no-such-session/commands",
                         value={"requestId": "old-crash-command", "hostInstanceId": self.crash_host,
                                "validUntilUtc": iso(utc_now() + timedelta(seconds=60)), "expectedRevision": "0", "command": {"type": "history.undo"}},
                         status=409, code="hostRestarted")
            self.request("old-crash-job-no-live-access", "GET", "/api/v1/jobs/" + self.crash_job, status=404, code="jobNotFound")
        self.owned_phase("crash-restart", environment, inspect)

    def accepted_then_abort_response(self, name, path, value):
        data = json.dumps(value).encode()
        request = urllib.request.Request(self.base_url + path, data=data, method="POST", headers={
            "Authorization": "Bearer " + self.tokens[self.client], "X-Mako-Client-Id": self.client,
            "X-Mako-Host-Instance-Id": self.host, "Content-Type": "application/json"})
        self.request_count += 1
        response = self.http.open(request, timeout=10)
        self.check(response.status == 202, name + ": domain job accepted before response read abort")
        save_json(self.output / f"{self.request_count:03d}-{name}.abort.json", {"status": response.status,
            "headers": dict(response.headers.items()), "request": self.redact(value), "responseBodyConsumed": False})
        response.close()

    def run_abort_shutdown_http(self, environment):
        state_root = self.output / "abort-shutdown-state"
        environment = {**environment, "MAKO_BACKEND_STATE_ROOT": str(state_root)}
        def abort_and_stop(process, child_environment):
            handle = self.request("abort-open-handle", "POST", "/api/v1/file-handles",
                                  value={"rootGrantId": "probe-root", "relativePath": "saved-project"}, status=201)
            opened_input = {"projectDirectoryHandleId": handle["handleId"], "metadata": self.metadata("abort-open-job")}
            self.accepted_then_abort_response("abort-open-response", "/api/v1/project-sessions/open-jobs", opened_input)
            accepted = self.request("retry-aborted-open", "POST", "/api/v1/project-sessions/open-jobs", value=opened_input, status=202)
            terminal = self.job("accepted-open-survives-abort", accepted)
            session = terminal["result"]["projectSessionId"]
            save_input = {"expectedRevision": "0", "metadata": self.metadata("shutdown-save-job")}
            self.accepted_then_abort_response("abort-save-response", "/api/v1/project-sessions/" + session + "/save-jobs", save_input)
            saved_acceptance = self.request("retry-aborted-save", "POST", "/api/v1/project-sessions/" + session + "/save-jobs", value=save_input, status=202)
            self.shutdown_save_job = saved_acceptance["jobId"]
            entry_path = state_root / "jobs" / (self.shutdown_save_job + ".json")
            before = json.loads(entry_path.read_text())
            save_json(self.output / "shutdown-save-before-sigterm.json", before)
            process.terminate()
            code = process.wait(timeout=20)
            self.check(code == 0, "shutdown: accepted save host exits after drain")
            after = json.loads(entry_path.read_text())
            save_json(self.output / "shutdown-save-after-sigterm.json", after)
            self.check(after["Job"]["State"] == "succeeded", "shutdown: accepted save has durable terminal success")
            self.check(after["ProjectFileSha256"], "shutdown: actual published project hash recorded")
            self.check(not (Path(child_environment["MAKO_BACKEND_RESOURCE_ROOT"]) / "bootstrap.json").exists(), "shutdown: launcher credential removed")
            self.check(before["Job"]["JobId"] == after["Job"]["JobId"], "shutdown: accepted operation identity retained")
        self.owned_phase("abort-shutdown", environment, abort_and_stop)

    def run_inherited_launcher_http(self, environment):
        secret = secrets.token_hex(32)
        allowed_origin = "http://127.0.0.1:5173"
        environment = {**environment, "MAKO_BACKEND_BOOTSTRAP_TOKEN": secret,
            "MAKO_BACKEND_ALLOWED_ORIGINS": allowed_origin, "MAKO_BACKEND_STATE_ROOT": str(self.output / "inherited-launcher-state")}
        def inherited(process, child_environment):
            self.check(not (Path(child_environment["MAKO_BACKEND_RESOURCE_ROOT"]) / "bootstrap.json").exists(), "inherited launcher: no credential file needed")
            self.check(secret not in (self.output / "inherited-launcher-server.log").read_text(), "inherited launcher: secret absent from process logs")
            self.request("allowed-dev-origin", "GET", "/api/v1/capabilities", headers={"Origin": allowed_origin})
            response_record = json.loads((self.output / f"{self.request_count:03d}-allowed-dev-origin.response.json").read_text())
            self.check(response_record["headers"].get("Access-Control-Allow-Origin") == allowed_origin, "CORS: exact explicitly configured development origin")
            self.request("unregistered-dev-origin", "GET", "/api/v1/capabilities", headers={"Origin": "http://127.0.0.1:5174"}, status=403, code="unauthorizedClient")
            self.request_count += 1
            request = urllib.request.Request(self.base_url + "/api/v1/assets", method="OPTIONS", headers={"Origin": allowed_origin,
                "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "Authorization, X-Mako-Client-Id"})
            with self.http.open(request, timeout=5) as response:
                self.check(response.status == 204, "CORS: allowed preflight")
                self.check(response.headers.get("Access-Control-Allow-Origin") == allowed_origin, "CORS preflight: exact origin")
                save_json(self.output / "development-preflight.json", {"status": response.status, "headers": dict(response.headers.items())})
        self.owned_phase("inherited-launcher", environment, inherited)

    def run_stream_auth_http(self, environment):
        environment = {**environment, "MAKO_BACKEND_CLIENT_TOKEN_SECONDS": "1", "MAKO_BACKEND_CLIENT_LEASE_SECONDS": "2",
                       "MAKO_BACKEND_STATE_ROOT": str(self.output / "stream-backend-state")}
        def stream(process, child_environment):
            for index in range(3):
                time.sleep(0.4)
                self.request(f"stream-heartbeat-{index}", "POST", f"/api/v1/clients/{self.client}/heartbeat", value={})
            self.request("stream-heartbeat-live", "GET", f"/api/v1/clients/{self.client}/state")
            time.sleep(1.1)
            self.request("expired-access-token", "GET", f"/api/v1/clients/{self.client}/state", status=401, code="clientTokenExpired")
            registration = self.registration
            self.register("expired-owner-resume", resume=registration)
            time.sleep(2.1)
            self.register("dormant-owner-resume", resume=self.registration)
            state = self.request("dormant-resume-state", "GET", f"/api/v1/clients/{self.client}/state")
            self.check(not state["dormant"], "resume: dormant lease does not discard owner")
            request = urllib.request.Request(self.base_url + "/api/v1/events", headers={"X-Mako-Client-Id": self.client,
                "Authorization": "Bearer " + self.tokens[self.client], "Last-Event-ID": state["eventCursor"]})
            self.request_count += 1
            started = time.monotonic()
            with self.http.open(request, timeout=20) as response:
                self.register("stream-rotate-owner", resume=self.registration)
                text = response.read().decode("utf-8")
            self.check("event: client.expired" in text, "SSE: rotated access token ends existing stream")
            self.check(time.monotonic() - started <= 17, "SSE: token validity rechecked within bounded heartbeat interval")
            (self.output / "rotated-stream.sse.txt").write_text(text)
        self.owned_phase("stream-auth", environment, stream)

    def run_asset_lease_http(self, environment):
        environment = {**environment, "MAKO_BACKEND_STATE_ROOT": str(self.output / "asset-lease-state"), "MAKO_BACKEND_ASSET_TTL_SECONDS": "2"}
        def asset_lease(process, child_environment):
            content = b"x" * (12 * 1024 * 1024)
            upload = self.request("lease-upload-large", "POST", "/api/v1/assets", raw=content, content_type="text/plain",
                                  headers={"X-Mako-Asset-Name": "lease.txt"}, status=201)
            path = "/api/v1/assets/" + upload["assetId"] + "/content"
            request = urllib.request.Request(self.base_url + path, headers={"X-Mako-Client-Id": self.client,
                "Authorization": "Bearer " + self.tokens[self.client]})
            self.request_count += 1
            with self.http.open(request, timeout=20) as response:
                time.sleep(2.2)
                self.binary("leased-resource-after-original-TTL", path, headers={"Range": "bytes=0-3"}, status=206, expected=b"xxxx")
                received = response.read()
            self.check(received == content, "resource lease: slow reader receives entire immutable body across TTL")
            time.sleep(2.2)
            self.request("resource-expired-after-read-release", "GET", path, status=410, code="resourceExpired")
            idle = self.request("idle-expiry-upload", "POST", "/api/v1/assets", raw=b"idle", content_type="text/plain",
                                headers={"X-Mako-Asset-Name": "idle.txt"}, status=201)
            resource_directory = Path(child_environment["MAKO_BACKEND_RESOURCE_ROOT"]) / self.client
            time.sleep(3.2)
            self.check(not list(resource_directory.glob(idle["assetId"] + ".*")), "resource cleanup: idle TTL deletes disk bytes without access")
        self.owned_phase("asset-lease", environment, asset_lease)

    def run_quota_http(self, environment):
        """Check multipart lengths and actual quota errors in a separate owned host."""
        original_url, original_host, original_client, original_registration = self.base_url, self.host, self.client, self.registration
        resources = Path(tempfile.mkdtemp(prefix="quota-resources-", dir=self.output))
        environment = {**environment, "MAKO_BACKEND_RESOURCE_ROOT": str(resources),
                       "MAKO_BACKEND_MAX_ASSET_BYTES": "1024", "MAKO_BACKEND_MAX_ASSET_QUOTA_BYTES": "2048",
                       "MAKO_BACKEND_STATE_ROOT": str(self.output / "quota-backend-state")}
        process = None
        forced_kill = False
        exit_code = None
        try:
            with (self.output / "quota-server.log").open("w") as log:
                process = subprocess.Popen(self.server_command + ["--urls", "http://127.0.0.1:0"],
                                           env=environment, stdout=log, stderr=subprocess.STDOUT)
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    ready = [line for line in (self.output / "quota-server.log").read_text().splitlines()
                             if line.startswith("BACKEND_READY ")]
                    if ready:
                        self.base_url, self.host = ready[-1].split()[1:3]
                        break
                    if process.poll() is not None:
                        raise RuntimeError(f"quota server exited before ready: {process.returncode}")
                    time.sleep(0.05)
                self.check(self.base_url != original_url, "quota startup: separate real server ready")
                self.bootstrap = json.loads((resources / "bootstrap.json").read_text())["bootstrapToken"]
                self.register("quota-register")
                previous_session = self.final_state["summary"]["projectSessionId"]
                stale_host_request = {"requestId": "actual-old-instance", "hostInstanceId": original_host,
                                      "validUntilUtc": iso(utc_now() + timedelta(seconds=60)),
                                      "expectedRevision": "6", "command": {"type": "history.undo"}}
                self.request("old-real-instance-missing-session", "POST",
                             f"/api/v1/project-sessions/{previous_session}/commands", value=stale_host_request,
                             status=409, code="hostRestarted")
                boundary = "mako-http-probe-boundary"

                def multipart(file_name, data):
                    return (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
                            f"filename=\"{file_name}\"\r\nContent-Type: text/plain\r\n\r\n").encode() + data + f"\r\n--{boundary}--\r\n".encode()

                asset = self.request("multipart-exact-file-limit", "POST", "/api/v1/assets",
                                     raw=multipart("max.lrc", b"a" * 1024),
                                     content_type=f"multipart/form-data; boundary={boundary}", status=201)
                self.check(asset["size"] == 1024, "multipart: overhead excluded from file size limit")
                self.request("multipart-over-file-limit", "POST", "/api/v1/assets",
                             raw=multipart("over.lrc", b"b" * 1025),
                             content_type=f"multipart/form-data; boundary={boundary}", status=413, code="resourceTooLarge")
                self.request("raw-over-file-limit", "POST", "/api/v1/assets", raw=b"c" * 1025,
                             content_type="text/plain", headers={"X-Mako-Asset-Name": "raw-over.lrc"}, status=413, code="resourceTooLarge")
                retry_deadline = iso(utc_now() + timedelta(seconds=90))
                second = self.request("raw-fills-total-quota", "POST", "/api/v1/assets", raw=b"d" * 1024,
                                      content_type="text/plain", headers={"X-Mako-Asset-Name": "second.lrc", "X-Mako-Request-Id": "quota-second", "X-Mako-Valid-Until-Utc": retry_deadline}, status=201)
                self.check(second["size"] == 1024, "quota: rejected oversized uploads consumed no budget")
                retry = self.request("quota-full-retained-upload-retry", "POST", "/api/v1/assets", raw=b"d" * 1024,
                    content_type="text/plain", headers={"X-Mako-Asset-Name": "second.lrc", "X-Mako-Request-Id": "quota-second", "X-Mako-Valid-Until-Utc": retry_deadline}, status=201)
                self.check(retry == second, "quota: valid retry succeeds after quota fills")
                self.request("total-quota-exceeded", "POST", "/api/v1/assets", raw=b"e",
                             content_type="text/plain", headers={"X-Mako-Asset-Name": "quota.lrc"}, status=429, code="resourceQuotaExceeded")
        finally:
            if process is not None:
                if process.poll() is None:
                    process.terminate()
                try:
                    exit_code = process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    forced_kill = True
                    process.kill()
                    exit_code = process.wait(timeout=5)
            if resources.exists():
                shutil.rmtree(resources)
            self.base_url, self.host, self.client, self.registration = original_url, original_host, original_client, original_registration
            self.quota_server = {"pid": process.pid if process else None, "exitCode": exit_code,
                                 "forcedKill": forced_kill, "resourceRootRemoved": not resources.exists()}
        self.check(exit_code == 0 and not forced_kill, "quota shutdown: real server exited cleanly")
        self.check(not resources.exists(), "quota cleanup: owned resources removed")

    def run(self):
        started = iso(utc_now())
        resources = Path(tempfile.mkdtemp(prefix="resources-", dir=self.output))
        environment = os.environ.copy()
        for name in ("DISPLAY", "WAYLAND_DISPLAY", "MAKO_BACKEND_FAKE_MEDIA"):
            environment.pop(name, None)
        environment["MAKO_BACKEND_RESOURCE_ROOT"] = str(resources)
        environment.pop("MAKO_BACKEND_BOOTSTRAP_TOKEN", None)
        environment.pop("MAKO_BACKEND_CREDENTIAL_FILE", None)
        roots = self.output / "projects"
        roots.mkdir()
        environment["MAKO_BACKEND_ALLOWED_ROOTS"] = json.dumps({"probe-root": str(roots)})
        environment["MAKO_BACKEND_STATE_ROOT"] = str(self.output / "backend-state")
        environment["MAKO_BACKEND_CLIENT_TOKEN_SECONDS"] = "3600"
        environment["MAKO_BACKEND_MAX_EVENTS"] = "4"
        environment["MAKO_BACKEND_AUTOSAVE_SECONDS"] = "1"
        environment["MAKO_BACKEND_ASSET_TTL_SECONDS"] = "2"
        process = None
        exit_code = None
        forced_kill = False
        failure = None
        rejected_exit = None
        try:
            with (self.output / "non-loopback.log").open("w") as log:
                rejected = subprocess.run(self.server_command + ["--urls", "http://0.0.0.0:0"],
                                          env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=15, check=False)
                rejected_exit = rejected.returncode
            self.check(rejected_exit != 0, "startup: non-loopback listener rejected")
            self.check("BACKEND_READY" not in (self.output / "non-loopback.log").read_text(), "startup: no accidental public listener")
            with (self.output / "config-override-listener.log").open("w") as log:
                overridden = subprocess.run(self.server_command + ["--urls", "http://127.0.0.1:0"],
                    env={**environment, "Kestrel__Endpoints__External__Url": "http://0.0.0.0:0"}, stdout=log, stderr=subprocess.STDOUT, timeout=15)
            self.check(overridden.returncode != 0, "startup: actual external Kestrel configuration rejected")
            self.check("BACKEND_READY" not in (self.output / "config-override-listener.log").read_text(), "startup: no readiness published for external override")
            with (self.output / "server.log").open("w") as log:
                process = subprocess.Popen(self.server_command + ["--urls", "http://127.0.0.1:0"],
                                           env=environment, stdout=log, stderr=subprocess.STDOUT)
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    ready = [line for line in (self.output / "server.log").read_text().splitlines()
                             if line.startswith("BACKEND_READY ")]
                    if ready:
                        self.base_url = ready[-1].split()[1]
                        break
                    if process.poll() is not None:
                        raise RuntimeError(f"server exited before ready: {process.returncode}")
                    time.sleep(0.05)
                self.check(self.base_url is not None, "startup: real server reached ready")
                self.check(urllib.parse.urlparse(self.base_url).hostname == "127.0.0.1", "startup: loopback ephemeral listener")
                credentials = resources / "bootstrap.json"
                self.check(credentials.exists(), "bootstrap: private launcher credential file exists")
                self.check(credentials.stat().st_mode & 0o777 == 0o600, "bootstrap: mode0600")
                self.bootstrap = json.loads(credentials.read_text())["bootstrapToken"]
                self.check(self.bootstrap not in (self.output / "server.log").read_text(), "bootstrap: secret absent from logs")
                self.run_http()
                self.run_peer_lease_http(environment)
                self.run_quota_http(environment)
                process.terminate()
                exit_code = process.wait(timeout=20)
                self.check(exit_code == 0, "main shutdown: accepted jobs drained before restart")
                if resources.exists():
                    shutil.rmtree(resources)
                self.run_restart_http(environment)
                self.run_crash_ledger_http(environment)
                self.run_abort_shutdown_http(environment)
                self.run_inherited_launcher_http(environment)
                self.run_stream_auth_http(environment)
                self.run_asset_lease_http(environment)
        except Exception:
            failure = traceback.format_exc()
            (self.output / "failure.txt").write_text(failure, encoding="utf-8")
        finally:
            if process is not None:
                if process.poll() is None:
                    process.terminate()
                try:
                    exit_code = process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    forced_kill = True
                    process.kill()
                    exit_code = process.wait(timeout=5)
            if resources.exists():
                shutil.rmtree(resources)
        if failure is None:
            try:
                self.check(exit_code == 0 and not forced_kill, "shutdown: real server exited cleanly")
                self.check(not resources.exists(), "cleanup: only owned temporary resources removed")
            except Exception:
                failure = traceback.format_exc()
                (self.output / "failure.txt").write_text(failure, encoding="utf-8")
        if failure is None:
            for log_path in self.output.glob("*server.log"):
                self.check("access_token=" not in log_path.read_text(), "logging: query credentials absent from " + log_path.name)
        save_json(self.output / "assertions.json", self.assertions)
        evidence = {}
        for path in sorted(self.output.iterdir()):
            if path.is_file() and path.name != "manifest.json":
                evidence[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
        source_root = Path(__file__).resolve().parents[2]
        source_paths = ["Directory.Build.props", "src/MakoKaraGenerator.Backend.Server/Program.cs",
                        "src/MakoKaraGenerator.Backend.Contracts/BackendContracts.cs",
                        "src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.cs",
                        "src/MakoKaraGenerator.Backend.Hosting/ProjectSession.cs",
                        "src/MakoKaraGenerator.Backend.Hosting/ProjectCommandRetentionOptions.cs",
                        "src/MakoKaraGenerator.Backend.Hosting/ResourceStore.cs",
                        "tools/backend-http-probe/run.sh", "tools/backend-http-probe/probe.py"]
        source_paths += sorted(str(path.relative_to(source_root)) for folder in ("MakoKaraGenerator.Backend.Server", "MakoKaraGenerator.Backend.Hosting", "MakoKaraGenerator.Backend.Contracts") for path in (source_root / "src" / folder).glob("*.cs") if str(path.relative_to(source_root)) not in source_paths)
        save_json(self.output / "manifest.json", {
            "status": "passed" if failure is None else "failed", "startedUtc": started, "finishedUtc": iso(utc_now()),
            "assertionCount": len(self.assertions), "httpRequestCount": self.request_count,
            "serverCommand": self.server_command, "serverPid": process.pid if process else None,
            "serverExitCode": exit_code, "forcedKill": forced_kill, "nonLoopbackExitCode": rejected_exit,
            "quotaServer": self.quota_server, "ownedServers": self.owned_servers,
            "baseUrl": self.base_url, "resourceRootRemoved": not resources.exists(), "guiOrSoundDeviceUsed": False,
            "sourceSha256": {path: hashlib.sha256((source_root / path).read_bytes()).hexdigest() for path in source_paths},
            "evidenceSha256": evidence, "failure": failure,
        })
        print(f"backend HTTP probe {'passed' if failure is None else 'failed'}: {len(self.assertions)} assertions, "
              f"{self.request_count} requests, server exit {exit_code}; {self.output}")
        if failure is not None:
            print(failure, file=sys.stderr)
        return 0 if failure is None else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--server", required=True, nargs=argparse.REMAINDER)
    arguments = parser.parse_args()
    return Probe(arguments.output, arguments.server).run()


if __name__ == "__main__":
    raise SystemExit(main())
