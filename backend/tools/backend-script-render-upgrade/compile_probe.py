#!/usr/bin/env python3
"""Measure synchronous author compilation over authenticated loopback HTTP."""
import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import statistics
import sys
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("w8_compile_probe", ROOT / "tools/backend-w8-authoring-http-probe/probe.py")
w8 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(w8)
save_json = w8.save_json


class TimedResponse:
    def __init__(self, response, timing):
        self.response, self.timing = response, timing

    def __getattr__(self, name):
        return getattr(self.response, name)

    def __enter__(self):
        self.response.__enter__()
        return self

    def __exit__(self, *args):
        return self.response.__exit__(*args)

    def read(self, *args):
        data = self.response.read(*args)
        self.timing["endNs"] = time.monotonic_ns()
        return data


class TimedOpener:
    def __init__(self, opener, probe):
        self.opener, self.probe = opener, probe

    def open(self, request, *args, **kwargs):
        timing = self.probe.active_timing
        if timing is None:
            return self.opener.open(request, *args, **kwargs)
        timing["startNs"] = time.monotonic_ns()
        return TimedResponse(self.opener.open(request, *args, **kwargs), timing)


class CompileProbe(w8.W8Probe):
    def __init__(self, output, dist, count):
        super().__init__(output, dist)
        self.count = count
        self.active_timing = None
        self.http = TimedOpener(self.http, self)
        self.samples = []
        self.stage = "initialize"

    def run(self):
        manifest_bytes = (self.dist / "deployment.json").read_bytes()
        deployment = json.loads(manifest_bytes)
        self.deployment = {key: deployment.get(key) for key in ("version", "sourceCommit", "sourceDirty", "rid", "selfContained")}
        self.deployment["manifestSha256"] = hashlib.sha256(manifest_bytes).hexdigest()
        self.stage = "start"
        self.start("compile")
        self.connect("compile")
        self.check(self.current_ready["version"] == deployment["version"], "matched deployment version")
        self.stage = "create-draft"
        draft = self.sample("WordEmphasis")
        self.check(draft["canCompile"], "WordEmphasis draft can compile")
        self.check(draft.get("lastCompilation") is None, "new draft has no prior compilation")
        before = self.files("source-before", draft)
        identity = None
        last_renewal = time.monotonic()
        for index in range(self.count + 1):
            name = "cold" if index == 0 else f"warm-{index:02d}"
            self.stage = name
            if time.monotonic() - last_renewal >= 30:
                self.request(name + "-heartbeat", "POST", "/api/v1/clients/" + self.client + "/heartbeat")
                self.register(name + "-resume", resume=self.registration)
                last_renewal = time.monotonic()
            timing = {}
            self.active_timing = timing
            try:
                response = self.compilation(name, draft)
            finally:
                self.active_timing = None
            self.check("startNs" in timing and "endNs" in timing and timing["endNs"] >= timing["startNs"], name + ": full HTTP timing")
            self.check(response.get("draftId") == draft["draftId"] and response.get("draftRevision") == draft["draftRevision"]
                and response.get("sourceRevision") == draft["currentSourceRevision"] and response.get("stale") is False,
                name + ": fixed draft and source identity")
            self.check(isinstance(response.get("artifactHash"), str) and response["artifactHash"] not in ("", "none")
                and isinstance(response.get("diagnostics"), list), name + ": artifact and complete diagnostics")
            current = (response["sourceRevision"], response["artifactHash"], response["diagnostics"])
            if identity is None:
                identity = current
            self.check(current == identity, name + ": same source, artifact and diagnostics")
            self.samples.append({"name": name, "elapsedMs": (timing["endNs"] - timing["startNs"]) / 1_000_000,
                "sourceRevision": current[0], "artifactHash": current[1], "diagnostics": current[2]})
        self.stage = "verify-source"
        after = self.files("source-after", draft)
        self.check(before == after, "complete source snapshot unchanged")
        latest = self.read("draft-after", draft)
        self.check(all(latest.get(key) == draft.get(key) for key in
            ("draftId", "draftRevision", "baseSourceRevision", "currentSourceRevision", "packageId", "exportId", "configuration")),
            "draft identity and configuration unchanged")
        self.stage = "completed"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--warm-count", type=int, default=12)
    args = parser.parse_args()
    if not 12 <= args.warm_count <= 100:
        parser.error("--warm-count must be between 12 and 100")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    probe = CompileProbe(output, args.dist.resolve(), args.warm_count)
    failures = []
    scanned = None
    try:
        probe.run()
    except BaseException as error:
        failures.append({"stage": probe.stage, "errorType": type(error).__name__})
    finally:
        try:
            probe.stop("compile")
        except BaseException as error:
            failures.append({"stage": "owned-process-drain", "errorType": type(error).__name__})
    warm = [sample["elapsedMs"] for sample in probe.samples[1:]]
    summary = {"status": "failed" if failures or len(warm) != args.warm_count else "passed", "failures": failures,
        "deployment": probe.deployment, "toolSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "cold": probe.samples[0] if probe.samples else None, "warmSamples": probe.samples[1:],
        "warmMedianMs": statistics.median(warm) if warm else None,
        "warmP95Ms": sorted(warm)[math.ceil(0.95 * len(warm)) - 1] if warm else None,
        "warmP95Method": "nearest-rank", "httpRequests": probe.request_count,
        "scope": "Full synchronous author-compilation HTTP response; artifact identity does not establish Emit count."}
    save_json(output / "summary.json", summary)
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        summary["status"] = "failed"
        failures.append({"stage": "credential-scan", "errorType": type(error).__name__})
    summary["credentialScannedFiles"] = scanned
    save_json(output / "summary.json", summary)
    try:
        probe.scan_credentials()
    except BaseException:
        summary["status"] = "failed"
        summary["credentialScannedFiles"] = None
        save_json(output / "summary.json", summary)
    print(json.dumps({"status": summary["status"], "warmSamples": len(warm)}))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
