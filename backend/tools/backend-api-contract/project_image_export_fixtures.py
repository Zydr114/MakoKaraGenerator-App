"""Synthetic JSON shape cases for the saved-project Image export overlay."""
import copy
import json

from project_image_export_schema import BASE_PROFILES, PROFILE


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name="project-image-export-" + name, schema=schema,
                          value=copy.deepcopy(value), valid=valid))

    metadata = dict(requestId="image-export-example", hostInstanceId="host-example",
                    validUntilUtc="2026-10-24T12:01:00Z")
    request = dict(expectedRevision="1", format="opaqueMp4", includeAudio=False, metadata=metadata)
    add("request-existing-fields", "ExportJobRequest", request)
    for field in ("taskProfile", "baseSourceProfile", "imageBytes", "path", "grantId"):
        add("request-reject-" + field, "ExportJobRequest", {**request, field: PROFILE}, False)

    source = dict(sourceKind="project", sourceId="image-project", sourceRevision="1",
                  sourceProfile=PROFILE, sourceSha256="a" * 64, framesPerOutput=4,
                  canvasWidth=320, canvasHeight=180, frameRateNumerator=30,
                  frameRateDenominator=1, outputDurationUs="1000000")
    image_input = dict(format="opaqueMp4", includeAudio=False,
                       taskProfile=PROFILE, baseSourceProfile=BASE_PROFILES[0])
    add("input-and-export-pair", "BackendJobInputDto", source)
    add("export-input-pair", "BackendExportInputDto", image_input)
    add("export-input-missing-base", "BackendExportInputDto",
        {k: v for k, v in image_input.items() if k != "baseSourceProfile"}, False)
    add("export-input-missing-task", "BackendExportInputDto",
        {k: v for k, v in image_input.items() if k != "taskProfile"}, False)
    add("export-input-unknown-base", "BackendExportInputDto", {**image_input, "baseSourceProfile": "unknown"}, False)
    add("export-input-preview-profile", "BackendExportInputDto", {**image_input, "taskProfile": "project-image-preview-v1"}, False)

    ordinary = dict(format="opaqueMp4", includeAudio=False)
    add("ordinary-export-input", "BackendExportInputDto", ordinary)
    add("ordinary-export-input-with-image-fields", "BackendExportInputDto",
        {**ordinary, "taskProfile": PROFILE, "baseSourceProfile": BASE_PROFILES[0]}, True)
    capabilities = dict(enabled=True, sourceProfile="static-lyrics-scene-v1",
                        formats=["opaqueMp4", "transparentProRes4444"],
                        audioProfiles=["pcm16-wave-self-contained", "mp3-layer3-self-contained", "native-flac-self-contained"],
                        quality="standard", renderQuality="final", maxFrames=600,
                        maxDurationUs="10000000", maxFrameRate=60, maxCanvasWidth=1920,
                        maxCanvasHeight=1080, maxRawFrameBytes=8388608, operationTimeoutSeconds=120,
                        cancellationGraceSeconds=2, maxMediaBytes=67108864, maxReportBytes=262144,
                        maxOccurrences=512, maxArtifactStorageBytes=536870912, maxRunning=2,
                        maxRunningPerClient=1, maxRunningPerProject=1, maxQueued=16,
                        maxQueuedPerClient=4, outputFileGuard="linux-x64-rlimit-fsize",
                        maxDiagnosticsPageSize=128, executionProfile="static-export-pcmwav-v1",
                        maxCompressedAudioDurationUs="300000000",
                        compressedAudioDurationBasis="strict-source-rate-decoded-sample-frames",
                        imageTaskProfile=PROFILE, imageBaseProfiles=BASE_PROFILES)
    add("capability-image", "ExportTaskCapabilitiesDto", capabilities)
    add("capability-image-duplicate-base", "ExportTaskCapabilitiesDto",
        {**capabilities, "imageBaseProfiles": BASE_PROFILES + [BASE_PROFILES[0]]}, False)
    add("capability-image-unknown-base", "ExportTaskCapabilitiesDto",
        {**capabilities, "imageBaseProfiles": ["unknown"]}, False)
    return {"cases": cases}


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))

