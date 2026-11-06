#!/usr/bin/env python3
"""M11 author validation over real HTTP + GPU Worker: every SDK2 sample, imported as a draft, runs the formal 720-frame job."""
import argparse
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import time
import zipfile

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("m11v_m10_probe", HERE / "m10_media_probe.py")
m10 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m10)
save_json = m10.save_json
SAMPLES_ROOT = m10.ROOT / "samples" / "ScriptEffectsV2"


class M11ValidationProbe(m10.M10MediaProbe):
    def import_draft(self, name):
        directory = SAMPLES_ROOT / name
        raw = (directory / "manifest.json").read_bytes()
        manifest = json.loads(raw)
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("manifest.json", raw)
            for item in manifest["files"]:
                package.writestr(item["path"], (directory / item["path"]).read_bytes())
        asset = self.upload(name + "-package", "m11v-" + name + ".mkfx", "application/octet-stream", archive.getvalue())
        inspected = self.request(name + "-inspect", "POST", "/api/v1/effect-packages/inspect", value={"packageAssetId": asset["assetId"]})
        self.check(inspected["structureValid"], name + ": package admitted")
        export = manifest["exports"][0]["id"]
        draft = self.request(name + "-draft", "POST", "/api/v1/drafts", status=201, value={
            "packageAssetId": asset["assetId"], "exportId": export, "name": "M11V " + name})
        save_json(self.output / (name + "-draft.json"), self.redact(draft))
        return draft, manifest["exports"][0]["kind"]

    def validate(self, name, draft, kind):
        # 与 M07 作者探针同一选择：默认样本、首个目标、首个材质槽。
        options = draft["sampleOptions"]
        sample = next(item for item in options["samples"] if item["sampleId"] == options["defaultSampleId"])
        value = {"expectedDraftRevision": draft["draftRevision"], "sampleId": sample["sampleId"],
            "targetKey": sample["targets"][0]["key"], "renderBackend": "gpu", "metadata": self.metadata(name + "-validation")}
        if options["materialSlots"]:
            value["materialSlot"] = options["materialSlots"][0]
        started = time.monotonic()
        accepted = self.request(name + "-validation", "POST", "/api/v1/drafts/" + draft["draftId"] + "/validation-jobs",
            status=202, value=value)
        job = accepted.get("job", accepted)
        deadline = started + self.args.timeout_seconds
        count = 0
        while time.monotonic() < deadline:
            count += 1
            current = self.request(name + "-job-" + str(count), "GET", "/api/v1/jobs/" + job["jobId"])
            if current["state"] in ("succeeded", "failed", "cancelled"):
                break
            time.sleep(0.5)
        else:
            raise TimeoutError(name + ": validation job did not finish")
        result = {"sample": name, "kind": kind, "sampleId": value["sampleId"], "targetKey": value["targetKey"],
            "state": current["state"], "elapsedSeconds": round(time.monotonic() - started, 2)}
        if current["state"] in ("succeeded", "failed") and current.get("validationResult"):
            # 结果接口返回完整报告；失败任务同样保留报告和问题列表。
            report = self.request(name + "-result", "GET", "/api/v1/jobs/" + job["jobId"] + "/validation-result")
            result.update(passed=report["succeeded"] and current["state"] == "succeeded",
                opaqueFrames=report["opaqueFrames"], transparentFrames=report["transparentFrames"])
            issues = [{"transparent": item["transparent"], "timeUs": item["timeUs"], "code": item["code"],
                "message": self.safe_text(item.get("message") or "")[:300]} for item in report["issues"][:8]]
            issues += [{"transparent": item["transparent"], "code": item["diagnostic"].get("code"),
                "message": self.safe_text(item["diagnostic"].get("message") or "")[:300]} for item in report["diagnostics"][:8]]
            if issues:
                result["diagnostics"] = issues
        if current["state"] != "succeeded":
            result["failure"] = self.redact({key: current.get(key) for key in ("failureCode", "error", "diagnostics")})
        save_json(self.output / (name + "-validation.json"), result)
        return result

    def run(self):
        self.stage = "start"
        self.start("m11v")
        self.stage = "connect"
        self.connect("m11v")
        names = self.args.samples or sorted(path.name for path in SAMPLES_ROOT.iterdir() if (path / "manifest.json").is_file())
        for name in names:
            self.stage = name
            try:
                draft, kind = self.import_draft(name)
                result = self.validate(name, draft, kind)
            except Exception as error:
                result = {"sample": name, "state": "probe-error", "failure": self.failure(error)}
            status = "passed" if result.get("passed") and result["opaqueFrames"] + result["transparentFrames"] == 720 else "failed"
            self.scenarios.append({"name": name, "status": status, **{key: result.get(key) for key in
                ("state", "passed", "opaqueFrames", "transparentFrames", "elapsedSeconds", "diagnostics", "failure")}})
            print(json.dumps({"sample": name, "status": status, "frames": (result.get("opaqueFrames"), result.get("transparentFrames")),
                "seconds": result.get("elapsedSeconds")}, ensure_ascii=False), flush=True)


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timeout-seconds", type=float, default=900)
    parser.add_argument("--icd", type=Path, default=Path("/usr/share/vulkan/icd.d/radeon_icd.json"))
    parser.add_argument("--sample", dest="samples", action="append")
    args = parser.parse_args()
    args.width, args.height, args.poll_ms, args.backend, args.ffmpeg = 640, 360, 50, "gpu", "/usr/bin/ffmpeg"
    args.count, args.require_reuse, args.scenario = 2, False, "m11v"
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    return args


def main():
    args = arguments()
    args.output.mkdir(parents=True, exist_ok=False)
    if args.icd.is_file():
        os.environ["VK_DRIVER_FILES"] = os.environ["VK_ICD_FILENAMES"] = str(args.icd)
    probe = M11ValidationProbe(args.output, args.dist, args)
    failures = []
    try:
        probe.run()
    except Exception as error:
        failures.append(probe.failure(error))
    finally:
        try:
            probe.stop("m11v")
        except Exception as error:
            failures.append(probe.failure(error, "owned-process-drain"))
        passed = not failures and probe.scenarios and all(item["status"] == "passed" for item in probe.scenarios)
        summary = {"status": "passed" if passed else "failed", "failures": failures, "scenarios": probe.scenarios,
            "totalFrames": sum((item.get("opaqueFrames") or 0) + (item.get("transparentFrames") or 0) for item in probe.scenarios)}
        save_json(args.output / "summary.json", summary)
        try:
            probe.scan_credentials()
        except Exception:
            summary["status"] = "failed"
            save_json(args.output / "summary.json", summary)
        print(json.dumps({"status": summary["status"], "totalFrames": summary["totalFrames"],
            "failed": [item["name"] for item in probe.scenarios if item["status"] != "passed"],
            "failures": [{key: item.get(key) for key in ("stage", "errorType", "message")} for item in failures]}, ensure_ascii=False))
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
