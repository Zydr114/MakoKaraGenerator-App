"""Synthetic HTTP shapes only; decoder, Worker and resource lifetime require runtime evidence."""
import copy
import json

from project_image_validation_schema import PROFILE, SOURCE_PROFILES


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name="project-image-validation-" + name, schema=schema,
                          value=copy.deepcopy(value), valid=valid))

    capabilities = dict(enabled=True, sourceProfile="static-lyrics-scene-v1", sourceProfiles=SOURCE_PROFILES,
        maxFramesPerOutput=600, maxCanvasWidth=1920, maxCanvasHeight=1080, maxRawFrameBytes=8388608,
        operationTimeoutSeconds=60, maxReportBytes=262144, maxIssues=256, maxOccurrences=512,
        maxRunning=2, maxRunningPerClient=1, maxRunningPerProject=1, maxQueued=8, maxQueuedPerClient=4,
        maxReportStorageBytes=16777216, maxDiagnosticsPageSize=128)
    add("capability-all", "ValidationTaskCapabilitiesDto", capabilities)
    add("capability-image-unavailable", "ValidationTaskCapabilitiesDto", {**capabilities, "sourceProfiles": SOURCE_PROFILES[:-1]})
    add("capability-legacy-omitted", "ValidationTaskCapabilitiesDto", {k: v for k, v in capabilities.items() if k != "sourceProfiles"})
    for suffix, profiles in (("null", None), ("preview-profile", ["project-image-preview-v1"]),
                             ("unknown", ["unknown"]), ("duplicate", SOURCE_PROFILES + [PROFILE])):
        add("capability-" + suffix, "ValidationTaskCapabilitiesDto", {**capabilities, "sourceProfiles": profiles}, False)
    metadata = dict(requestId="image-validation-example", hostInstanceId="host-example", validUntilUtc="2026-10-23T12:01:00Z")
    request = dict(expectedRevision="1", framesPerOutput=4, metadata=metadata)
    add("request-existing-fields", "ValidationJobRequest", request)
    for field, value in (("profile", PROFILE), ("sourceProfile", PROFILE), ("imageBytes", "AA=="),
                         ("path", "image.png"), ("grantId", "grant-example")):
        add("request-reject-" + field, "ValidationJobRequest", {**request, field: value}, False)
    for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        job_input = dict(sourceKind="project", sourceId="image-project", sourceRevision="1", sourceProfile=PROFILE,
            sourceSha256="a" * 64, framesPerOutput=4, canvasWidth=320, canvasHeight=180,
            frameRateNumerator=30, frameRateDenominator=1, outputDurationUs="1000000")
        add("input-" + component, component, job_input)
        add("input-reject-preview-" + component, component, {**job_input, "sourceProfile": "project-image-preview-v1"}, False)
        add("input-reject-unknown-" + component, component, {**job_input, "sourceProfile": "unknown"}, False)
    for code, field, failure in (("taskUnavailable", "worker", "WorkerImageValidationUnavailable"),
                               ("commandNotSupported", "background", "ProjectImageSourceUnavailable"),
                               ("commandNotSupported", "background", "ProjectImageSourceChanged"),
                               ("resourceTooLarge", "background", "SnapshotSizeExceeded")):
        add("error-" + failure, "BackendErrorResponse", dict(code=code, message="固定图片验证未被接受。",
            field=field, failureCode=failure, protocolVersion="1"))
    for failure in ("PackageImageDecodeFailed", "PackageImageBudgetExceeded"):
        add("report-" + failure, "ValidationReportDtoRaw", dict(succeeded=False, opaqueFrames=0, transparentFrames=0,
            issues=[dict(transparent=False, timeUs="0", code=failure, message="固定图片无法解码。", isError=True,
                         location=None, sampleFailureCode=None)], diagnostics=[]))
    return {"cases": cases}


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))
