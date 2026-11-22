"""P1 strict public input and P2 export policy examples; static wire checks only."""
import copy
import itertools
import json


def fixtures():
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name="p1p2-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    create = dict(name="empty", metadata=dict(requestId="p1p2-create", hostInstanceId="host", validUntilUtc="2030-01-01T00:00:00Z"))
    for name, assets in (
        ("empty", {}), ("null-assets", dict(audioAssetId=None, lyricsAssetId=None)),
        ("audio-only", dict(audioAssetId="asset-A")), ("lyrics-only", dict(lyricsAssetId="asset-L")),
        ("both-assets", dict(audioAssetId="asset-A", lyricsAssetId="asset-L")),
    ):
        add("create-" + name, "CreateProjectSessionRequest", {**create, **assets})
    add("create-audio-wrong-type", "CreateProjectSessionRequest", {**create, "audioAssetId": 1}, False)
    add("create-lyrics-wrong-type", "CreateProjectSessionRequest", {**create, "lyricsAssetId": []}, False)
    commands = [
        dict(type="composition.setBackgroundAsset", assetId="asset-A", kind="image"),
        dict(type="composition.clearBackground"),
        dict(type="composition.setBackgroundFit", fit="cover"),
        dict(type="composition.setBackgroundPlayback", playback="freezeLastFrame"),
        dict(type="project.applyEdits", edits=[]),
    ]
    for command in commands:
        name = command["type"]
        add(name, "ProjectCommandDtoInput", command)
        add(name + "-extra", "ProjectCommandDtoInput", {**command, "extra": True}, False)
        add(name + "-inherited-slot", "ProjectCommandDtoInput", {**command, "target": None}, False)
    add("background-fit-null", "ProjectCommandDtoInput", {**commands[0], "fit": None}, False)
    add("background-playback-invalid", "ProjectCommandDtoInput", dict(type="composition.setBackgroundPlayback", playback="Loop"), False)
    add("batch-data-edit", "ProjectCommandDtoInput", dict(type="project.applyEdits", edits=[commands[2]]))
    add("batch-nested", "ProjectCommandDtoInput", dict(type="project.applyEdits", edits=[commands[-1]]), False)
    add("batch-history", "ProjectCommandDtoInput", dict(type="project.applyEdits", edits=[dict(type="history.undo")]), False)
    add("import-plan", "LyricsImportPlanRequest", dict(assetId="asset-A"))
    add("import-plan-options", "LyricsImportPlanRequest", dict(assetId="asset-A", options={}))
    add("import-plan-options-null", "LyricsImportPlanRequest", dict(assetId="asset-A", options=None), False)
    add("import-plan-metadata", "LyricsImportPlanRequest", dict(assetId="asset-A", metadata={}), False)
    font = dict(text="Mako 歌詞", family=None, fontSizePx=72, fontWeight=400, italic=False,
                fallbackFamilies=None, sessionId=None, expectedRevision=None)
    add("font-inspect", "FontInspectRequest", font)
    for field in font:
        add("font-missing-" + field, "FontInspectRequest", {k:v for k,v in font.items() if k != field}, False)
    add("font-extra", "FontInspectRequest", {**font, "extra": True}, False)
    request = dict(expectedRevision="0", mode="time", timeUs="0", width=64, height=36, alphaMode="transparent")
    candidate = dict(previewId="preview-A", expectedPreviewGeneration="0", candidateRevision="candidate-A")
    add("measurement-project", "SceneMeasurementRequest", request)
    add("measurement-candidate", "SceneMeasurementRequest", {**request, **candidate})
    for size in (1, 2):
        for fields in itertools.combinations(candidate, size):
            add("measurement-partial-" + "-".join(fields), "SceneMeasurementRequest", {**request, **{k:candidate[k] for k in fields}}, False)
    for field in candidate:
        add("measurement-null-" + field, "SceneMeasurementRequest", {**request, **candidate, field: None}, False)
    export = dict(expectedRevision="0", includeAudio=False,
                  metadata=dict(requestId="p1p2-export", hostInstanceId="host", validUntilUtc="2030-01-01T00:00:00Z"))
    for quality in ("draft", "standard", "high"):
        add("export-quality-" + quality, "ExportJobRequest", {**export, "quality": quality})
    add("export-quality-invalid", "ExportJobRequest", {**export, "quality": "ultra"}, False)
    from p1p2_runtime_schema import RUNTIME, PACKAGE_PRESETS, PREVIEW, VALIDATION, EXPORT
    from editing_completion_fixtures import package_measurement_cases
    preview_runtime = dict(enabled=True, status="configured-unverified", sourceProfile="static-lyrics-scene-v1",
        sourceProfiles=PREVIEW, executionMode="worker-process", maxPixelWidth=1280, maxPixelHeight=720,
        maxRawFrameBytes=4194304, maxSnapshotBytes=4194280, frameLeaseLifetimeSeconds=30,
        maxRetainedFramesPerSession=2, maxRetainedFrameBytesPerSession=8388608, maxReadLeases=8,
        maxReadLeaseBytes=536870912, clientLeaseExpiryPolicy="close-previews-preserve-projects",
        eventDelivery="bounded-advisory-read-state-on-gaps")
    validation_caps = dict(enabled=True, sourceProfile="static-lyrics-scene-v1", sourceProfiles=VALIDATION,
        maxFramesPerOutput=600, maxCanvasWidth=1920, maxCanvasHeight=1080, maxRawFrameBytes=8388608,
        operationTimeoutSeconds=60, maxReportBytes=262144, maxIssues=256, maxOccurrences=512,
        maxRunning=2, maxRunningPerClient=1, maxRunningPerProject=1, maxQueued=8, maxQueuedPerClient=4,
        maxReportStorageBytes=16777216, maxDiagnosticsPageSize=128)
    export_caps = dict(enabled=True, sourceProfile="static-lyrics-scene-v1",
        formats=["opaqueMp4", "transparentProRes4444"],
        audioProfiles=["pcm16-wave-self-contained", "mp3-layer3-self-contained", "native-flac-self-contained"],
        quality="standard", renderQuality="final", maxFrames=600, maxDurationUs="10000000", maxFrameRate=60,
        maxCanvasWidth=1920, maxCanvasHeight=1080, maxRawFrameBytes=8388608, operationTimeoutSeconds=120,
        cancellationGraceSeconds=2, maxMediaBytes=67108864, maxReportBytes=262144, maxOccurrences=512,
        maxArtifactStorageBytes=536870912, maxRunning=2, maxRunningPerClient=1, maxRunningPerProject=1,
        maxQueued=16, maxQueuedPerClient=4, outputFileGuard="linux-x64-rlimit-fsize",
        maxDiagnosticsPageSize=128, executionProfile="static-export-pcmwav-v1",
        maxCompressedAudioDurationUs="300000000",
        compressedAudioDurationBasis="strict-source-rate-decoded-sample-frames")
    for schema, profiles, base in (
        ("PreviewRuntimeInfoResponse", PREVIEW, preview_runtime),
        ("ValidationTaskCapabilitiesDto", VALIDATION, validation_caps),
        ("ExportTaskCapabilitiesDto", EXPORT, export_caps),
    ):
        add(schema + "-runtime-profiles", schema, {**base, "sourceProfiles": profiles})
        add(schema + "-runtime-duplicate", schema, {**base, "sourceProfiles": profiles + [RUNTIME]}, False)
    ordinary_export_input = dict(format="opaqueMp4", includeAudio=False, quality="standard")
    add("BackendExportInputDto-ordinary", "BackendExportInputDto", ordinary_export_input)
    add("BackendExportInputDtoRaw-ordinary", "BackendExportInputDtoRaw", {**ordinary_export_input, "audio": None})
    validation_request = dict(expectedRevision="0", framesPerOutput=4,
        metadata=dict(requestId="p1p2-validation", hostInstanceId="host", validUntilUtc="2030-01-01T00:00:00Z"))
    add("ValidationJobRequest-basic", "ValidationJobRequest", validation_request)
    add("ValidationJobRequest-extra", "ValidationJobRequest", {**validation_request, "profile": "unknown"}, False)
    measurement = package_measurement_cases()[0]["value"]
    add("measurement-runtime", "SceneMeasurementDataDto", {**measurement, "sourceProfile": RUNTIME})
    add("measurement-candidate-response", "SceneMeasurementResponse", dict(projectSessionId="project-A", projectRevision="candidate-" + "a" * 32, measurement={**measurement, "sourceProfile": RUNTIME}, protocolVersion="1"))
    add("measurement-invalid-source-revision", "SceneMeasurementResponse", dict(projectSessionId="project-A", projectRevision="unrelated", measurement={**measurement, "sourceProfile": RUNTIME}, protocolVersion="1"), False)
    source = dict(sourceKind="project", sourceId="project-A", sourceRevision="0", sourceProfile=RUNTIME,
        sourceSha256="a" * 64, framesPerOutput=4, canvasWidth=320, canvasHeight=180,
        frameRateNumerator=30, frameRateDenominator=1, outputDurationUs="1000000")
    for profile in (RUNTIME, PACKAGE_PRESETS):
        for schema in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
            add(schema + "-" + profile, schema, {**source, "sourceProfile": profile})
    caps = dict(protocolVersion="1", hostInstanceId="host", productVersion="0.102.0", maxAssetBytes=None,
        maxAssetQuotaBytes=None, assetMimeTypes=["video/mp4"], features=[], serverTimeUtc="2030-01-01T00:00:00Z",
        maxCommandRetryWindowSeconds=600, maxCommandReceiptsPerSession=256, clientTokenLifetimeSeconds=3600,
        clientLeaseLifetimeSeconds=600, maxClients=64, maxSessionsPerClient=16, unreferencedAssetLifetimeSeconds=600, maxEventsPerClient=256,
        maxCommandReceiptsPerClient=1024, maxReceiptBytes=65536, maxReceiptBytesPerClient=67108864,
        maxUploadPreparations=4, maxUploadPreparationsPerClient=2, maxEventConnections=4,
        maxEventConnectionsPerClient=2, maxAssets=4096)
    add("capabilities-upload-unlimited", "BackendCapabilitiesResponse", caps)
    add("capabilities-upload-int64", "BackendCapabilitiesResponse", {**caps, "maxAssetBytes": 2**40, "maxAssetQuotaBytes": 2**50})
    for field in ("maxAssetBytes", "maxAssetQuotaBytes"):
        add("capabilities-missing-" + field, "BackendCapabilitiesResponse", {k:v for k,v in caps.items() if k != field}, False)
        add("capabilities-zero-" + field, "BackendCapabilitiesResponse", {**caps, field: 0}, False)
    candidate_source = dict(kind="project", sourceId="project-A", sourceRevision="candidate-" + "a" * 32)
    add("preview-candidate-opaque-source", "PreviewSourceDto", candidate_source)
    from template_library_fixtures import fixtures as template_fixtures
    advanced = next(case["value"] for case in template_fixtures() if case["name"] == "template-library-detail-advanced-required-null")
    advanced = {**advanced, "definitionAvailability": "advancedRuntimeAvailable"}
    add("template-advanced-runtime-null", "TemplateDetailResponse", advanced)
    add("template-advanced-runtime-missing-definition", "TemplateDetailResponse", {k:v for k,v in advanced.items() if k != "definition"}, False)
    add("template-advanced-runtime-not-static", "TemplateDetailResponse", {**advanced, "definition": next(case["value"]["definition"] for case in template_fixtures() if case["name"] == "template-library-detail-static")}, False)
    add("template-transfer-runtime", "TemplateTransferSummaryDto", dict(summary=advanced["summary"], definitionAvailability="advancedRuntimeAvailable"))
    from basic_business_fixtures import fixtures as business_fixtures
    historical = business_fixtures()
    transfer = next(case["value"] for case in historical if case["schema"] == "TemplateImportPreflightResponse" and case["valid"])
    transfer = {**transfer, "archiveBytes": 19_484_784, "expandedBytes": 19_484_784}
    add("template-transfer-large-font", "TemplateImportPreflightResponse", transfer)
    add("template-transfer-budget-edge", "TemplateImportPreflightResponse", {**transfer, "archiveBytes": 268_435_456, "expandedBytes": 268_435_456})
    for field in ("archiveBytes", "expandedBytes"):
        add("template-transfer-budget-over-" + field, "TemplateImportPreflightResponse", {**transfer, field: 268_435_457}, False)
    return {"cases": cases}


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))
