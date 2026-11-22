"""Contract 1.51.0+ fixtures: fallback diagnostics on export reports, export job results and scene measurements."""
import copy
import json

PREFIX = "export-fallback-"
REPORT_FIELDS = {"fallbackDiagnostics": [], "fallbackFrameCount": 0, "fallbackDiagnosticsTruncated": False}
MEASUREMENT_FIELDS = {"fallbackDiagnostics": []}


def _targets(case):
    """Objects in the case value that gained fields in 1.51.0, with the fields they gained."""
    value = case["value"]
    if not isinstance(value, dict):
        return []
    if case["schema"] in ("ExportReportDtoRaw", "ExportResultReport") and value.get("kind") != "exportFailure":
        return [(value, REPORT_FIELDS)]
    if case["schema"] == "SceneMeasurementDataDto":
        return [(value, MEASUREMENT_FIELDS)]
    if case["schema"] == "SceneMeasurementResponse" and isinstance(value.get("measurement"), dict):
        return [(value["measurement"], MEASUREMENT_FIELDS)]
    if case["schema"] == "ExportJobResultDto":
        return [(value, {"fallbackDiagnosticCount": 0})]
    return []


def migrate_cases(cases):
    result = []
    for case in cases:
        case = copy.deepcopy(case)
        for target, fields in _targets(case):
            for field, value in fields.items():
                target.setdefault(field, copy.deepcopy(value))
        result.append(case)
    return result


IDENTITY = {"packageId": "package", "revision": "sha256:" + "a" * 64, "exportId": "ink"}
GRAPH = {"packageId": "package", "revision": "sha256:" + "a" * 64, "exportId": "glass"}


def _material(code="TransparentBackgroundDependency", input_id="background", raw=True):
    owner_source = {"path": "lyricStyle", "region": "fill", "slot": 0, "sourceMaterial": IDENTITY, "parametersJson": "{}"}
    owner = {"kind": "source", "script": None, "source": owner_source} if raw else {"kind": "source", "source": owner_source}
    item = {"code": code, "severity": "warning",
            "message": "透明输出没有背景，材质已退回包内兜底外观。" if code == "TransparentBackgroundDependency"
            else "背景媒体不可用（未设置、文件丢失或该时刻无帧），材质已退回包内兜底外观。",
            "originKind": "material", "material": {"owner": owner, "material": IDENTITY, "inputId": input_id, "code": code}}
    if raw:
        item.update(location=None, occurrence=None, source=None, imageEffect=None)
    return item


def _image(code="BackgroundMediaUnavailable", raw=True):
    item = {"code": code, "severity": "warning", "message": "背景媒体不可用（未设置、文件丢失或该时刻无帧），图效果已旁路为原样。",
            "originKind": "imageEffect", "imageEffect": {"instanceId": "instance", "artifactHash": "sha256:" + "b" * 64,
                                                         "graph": GRAPH, "inputId": "background", "code": code}}
    if raw:
        item.update(location=None, occurrence=None, source=None, material=None)
    return item


def _script_material():
    item = _material("BackgroundMediaUnavailable")
    item["material"]["owner"] = {"kind": "script", "source": None, "script": {
        "instanceId": "instance", "artifactHash": "sha256:" + "b" * 64, "packageIdentity": None}}
    return item


REPORT = {"format": "opaqueMp4", "audioIncluded": False, "expectedFrames": 3, "decodedFrames": 3, "framesWritten": 3,
          "droppedFrames": 0, "failedFrames": 0, "width": 320, "height": 180, "frameRateNumerator": 30, "frameRateDenominator": 1,
          "derivedVideoDurationUs": "100000", "actualVideoDurationUs": "100000", "audioDurationUs": None, "videoCodec": "h264",
          "audioCodec": None, "pixelFormat": "yuv420p", "hasAlpha": False, "mediaSize": 1000, "sha256": "0" * 64, "warnings": [],
          "diagnostics": [], "isVerified": True, "actualAudioDurationUs": None, "strictlyVerified": True,
          "requestedQuality": "standard", "actualQuality": "standard", "renderBackend": "gpu", **REPORT_FIELDS}
RESULT = {"artifact": {"artifactId": "artifact-job", "contentType": "video/mp4", "sizeBytes": 1000, "sha256": "0" * 64},
          "report": {"reportId": "report-job", "sizeBytes": 2048, "sha256": "1" * 64}, "fallbackDiagnosticCount": 0}
MEASUREMENT = {"sourceSha256": "a" * 64, "sampleInputFingerprint": "b" * 64, "sourceProfile": "runtime-project-input-v1",
               "samplingProfile": "time-v1", "mode": "time", "timeUs": "250000", "canvasWidth": 1920, "canvasHeight": 1080,
               "width": 320, "height": 180, "alphaMode": "transparent", "elements": [], "pngSha256": "c" * 64,
               "png": "iVBORw0KGgo=", **MEASUREMENT_FIELDS}


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append({"name": PREFIX + name, "schema": schema, "value": copy.deepcopy(value), "valid": valid})

    def without(value, field):
        return {key: item for key, item in value.items() if key != field}

    # Export reports: all three fields or none (registered before 1.51.0), the two origins, truncation and bounds.
    add("report-none", "ExportReportDtoRaw", REPORT)
    add("report-none-result-union", "ExportResultReport", REPORT)
    legacy = {key: value for key, value in REPORT.items() if key not in REPORT_FIELDS}
    add("report-legacy-registered", "ExportReportDtoRaw", legacy)
    add("report-legacy-registered-result-union", "ExportResultReport", legacy)
    two = {**REPORT, "fallbackDiagnostics": [_image(), _material("BackgroundMediaUnavailable"), _script_material()], "fallbackFrameCount": 3}
    add("report-image-and-material", "ExportReportDtoRaw", two)
    add("report-transparent", "ExportReportDtoRaw", {**REPORT, "format": "transparentProRes4444", "videoCodec": "prores",
        "pixelFormat": "yuva444p12le", "hasAlpha": True, "fallbackDiagnostics": [_material()], "fallbackFrameCount": 2})
    add("report-truncated", "ExportReportDtoRaw", {**two, "fallbackDiagnosticsTruncated": True})
    add("report-truncated-empty", "ExportReportDtoRaw", {**REPORT, "fallbackFrameCount": 1, "fallbackDiagnosticsTruncated": True})
    full = [_material(input_id="background-" + str(index)) for index in range(128)]
    add("report-128", "ExportReportDtoRaw", {**REPORT, "fallbackDiagnostics": full, "fallbackFrameCount": 3})
    add("report-129", "ExportReportDtoRaw", {**REPORT, "fallbackDiagnostics": full + [_material(input_id="background-128")],
        "fallbackFrameCount": 3}, False)
    for field in REPORT_FIELDS:
        add("report-missing-" + field, "ExportReportDtoRaw", without(two, field), False)
        add("report-null-" + field, "ExportReportDtoRaw", {**two, field: None}, False)
    add("report-negative-frames", "ExportReportDtoRaw", {**two, "fallbackFrameCount": -1}, False)
    add("report-duplicate", "ExportReportDtoRaw", {**two, "fallbackDiagnostics": [_material(), _material()]}, False)
    invalid_items = {
        "runtime-origin": {**_material(), "originKind": "runtime"},
        "compile-origin": {**_material(), "originKind": "compileSource"},
        "error-severity": {**_material(), "severity": "error"},
        "occurrence": {**_material(), "occurrence": {"epoch": "00000000-0000-0000-0000-000000000001", "sequence": "1",
            "instanceId": "instance", "artifactHash": "", "stage": "render", "timeUs": "0", "location": None, "package": None}},
        "location": {**_material(), "location": "server/path"},
        "foreign-image-payload": {**_material(), "imageEffect": _image()["imageEffect"]},
        "foreign-material-payload": {**_image(), "material": _material()["material"]},
        "source-payload": {**_material(), "source": {"packageId": "package", "revision": IDENTITY["revision"], "instanceId": None,
            "exportId": None, "code": "CS1002", "message": "Expected", "isError": False, "path": None, "line": 0, "column": 0}},
        "missing-material-payload": without(_material(), "material"),
        "null-material-payload": {**_material(), "material": None},
        "missing-location": without(_material(), "location"),
    }
    for name, item in invalid_items.items():
        add("report-item-" + name, "ExportReportDtoRaw", {**two, "fallbackDiagnostics": [item]}, False)

    # Export job results carry only the count.
    add("result-none", "ExportJobResultDto", RESULT)
    add("result-three", "ExportJobResultDto", {**RESULT, "fallbackDiagnosticCount": 3})
    add("result-128", "ExportJobResultDto", {**RESULT, "fallbackDiagnosticCount": 128})
    for name, value in (("missing", without(RESULT, "fallbackDiagnosticCount")), ("null", {**RESULT, "fallbackDiagnosticCount": None}),
                        ("negative", {**RESULT, "fallbackDiagnosticCount": -1}), ("129", {**RESULT, "fallbackDiagnosticCount": 129}),
                        ("string", {**RESULT, "fallbackDiagnosticCount": "1"})):
        add("result-" + name, "ExportJobResultDto", value, False)

    # Scene measurements: public wire omits null; overflow fails the measurement instead of truncating.
    add("measurement-none", "SceneMeasurementDataDto", MEASUREMENT)
    measured = {**MEASUREMENT, "fallbackDiagnostics": [_material(raw=False), _image(raw=False)]}
    add("measurement-material-and-image", "SceneMeasurementDataDto", measured)
    add("measurement-response", "SceneMeasurementResponse", {"projectSessionId": "session", "projectRevision": "3",
        "measurement": measured, "protocolVersion": "1"})
    add("measurement-128", "SceneMeasurementDataDto", {**MEASUREMENT, "fallbackDiagnostics":
        [_material(input_id="background-" + str(index), raw=False) for index in range(128)]})
    add("measurement-129", "SceneMeasurementDataDto", {**MEASUREMENT, "fallbackDiagnostics":
        [_material(input_id="background-" + str(index), raw=False) for index in range(129)]}, False)
    add("measurement-missing", "SceneMeasurementDataDto", without(MEASUREMENT, "fallbackDiagnostics"), False)
    add("measurement-null", "SceneMeasurementDataDto", {**MEASUREMENT, "fallbackDiagnostics": None}, False)
    add("measurement-duplicate", "SceneMeasurementDataDto", {**MEASUREMENT, "fallbackDiagnostics":
        [_material(raw=False), _material(raw=False)]}, False)
    for name, item in (("runtime-origin", {"code": "Runtime", "severity": "warning", "message": "runtime", "originKind": "runtime",
                            "occurrence": {"epoch": "00000000-0000-0000-0000-000000000001", "sequence": "1", "instanceId": "instance",
                                           "artifactHash": "", "stage": "render", "timeUs": "0"}}),
                       ("legacy-origin", {"code": "Legacy", "severity": "warning", "message": "legacy"}),
                       ("error-severity", {**_material(raw=False), "severity": "error"}),
                       ("explicit-null-slot", {**_material(raw=False), "imageEffect": None})):
        add("measurement-item-" + name, "SceneMeasurementDataDto", {**MEASUREMENT, "fallbackDiagnostics": [item]}, False)
    return cases


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))
