"""Synthetic image-preview HTTP cases; authorization/decoding require runtime evidence."""
import copy
import json

from project_image_preview_schema import SOURCE_PROFILES


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name="project-image-preview-" + name, schema=schema,
                          value=copy.deepcopy(value), valid=valid))

    runtime = dict(enabled=True, status="configured-unverified", sourceProfile="static-lyrics-scene-v1",
                   sourceProfiles=SOURCE_PROFILES, executionMode="worker-process", maxPixelWidth=1280,
                   maxPixelHeight=720, maxRawFrameBytes=4194304, maxSnapshotBytes=4194280,
                   frameLeaseLifetimeSeconds=30, maxRetainedFramesPerSession=2,
                   maxRetainedFrameBytesPerSession=8388608, maxReadLeases=8, maxReadLeaseBytes=536870912,
                   clientLeaseExpiryPolicy="close-previews-preserve-projects",
                   eventDelivery="bounded-advisory-read-state-on-gaps")
    add("runtime-all-profiles", "PreviewRuntimeInfoResponse", runtime)
    add("runtime-image-probe-failed", "PreviewRuntimeInfoResponse", {**runtime, "sourceProfiles": SOURCE_PROFILES[:-1]})
    add("runtime-disabled", "PreviewRuntimeInfoResponse", {**runtime, "enabled": False, "status": "disabled",
        "sourceProfiles": SOURCE_PROFILES[:2], "disabledReason": dict(code="previewUnavailable",
        message="预览Worker尚未部署。", field="worker", protocolVersion="1")})
    add("runtime-legacy-omitted-profiles", "PreviewRuntimeInfoResponse", {k: v for k, v in runtime.items() if k != "sourceProfiles"})
    add("runtime-wrong-legacy-single-value", "PreviewRuntimeInfoResponse", {**runtime, "sourceProfile": SOURCE_PROFILES[-1]}, False)
    for suffix, profiles in (("null", None), ("unknown", ["unknown"]), ("duplicate", SOURCE_PROFILES + [SOURCE_PROFILES[-1]])):
        add("runtime-profiles-" + suffix, "PreviewRuntimeInfoResponse", {**runtime, "sourceProfiles": profiles}, False)

    metadata = dict(requestId="image-preview-example", hostInstanceId="host-example", validUntilUtc="2026-10-23T12:01:00Z")
    source = dict(kind="project", sourceId="project-image-example", sourceRevision="1")
    create = dict(source=source, size=dict(width=320, height=180), alphaMode="opaque", metadata=metadata)
    add("create-project", "PreviewSessionRequest", create)
    replace = dict(expectedPreviewGeneration="1", expectedSourceRevision="1", source={**source, "sourceRevision": "2"},
                   size=create["size"], alphaMode="straight", metadata={**metadata, "requestId": "image-input-example"})
    add("replace-project", "PreviewProjectInputRequest", replace)
    for field, value in (("profile", SOURCE_PROFILES[-1]), ("imageBytes", "AA=="), ("path", "image.png"), ("grantId", "grant-example")):
        add("create-reject-" + field, "PreviewSessionRequest", {**create, field: value}, False)
        add("source-reject-" + field, "PreviewSessionRequest", {**create, "source": {**source, field: value}}, False)
    add("source-image-kind-rejected", "PreviewSessionRequest", {**create, "source": {**source, "kind": "image"}}, False)

    for code, field, failure in (("previewUnavailable", "worker", "WorkerImagePreviewProfileMismatch"),
                                 ("previewUnavailable", "worker", "WorkerImagePreviewUnavailable"),
                                 ("commandNotSupported", "background", "ProjectImageSourceUnavailable"),
                                 ("commandNotSupported", "background", "ProjectImageSourceChanged"),
                                 ("resourceTooLarge", "background", "SnapshotSizeExceeded")):
        add("error-" + failure, "BackendErrorResponse", dict(code=code, message="固定图片预览未被接受。",
            field=field, failureCode=failure, protocolVersion="1"))
    limits = dict(maxWidth=1280, maxHeight=720, maxInFlightRequests=1, maxRetainedFrames=2,
                  maxRetainedFrameBytes=8388608, frameLeaseLifetimeSeconds=30,
                  maxDiagnosticsPageSize=128, maxRawFrameBytes=4194304)
    session = dict(previewId="preview-image-example", source=source, previewGeneration="1", size=create["size"],
                   alphaMode="opaque", state="failed", limits=limits, protocolVersion="1", previewKind="project", committable=True)
    for failure in ("PackageImageDecodeFailed", "PackageImageBudgetExceeded", "WorkerPackageTimedOut"):
        add("async-failure-" + failure, "PreviewSessionStateResponse", dict(session=session,
            failure=dict(code="internalError", message="预览渲染失败。", protocolVersion="1"), renderFailureCode=failure))
    return {"cases": cases}


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))
