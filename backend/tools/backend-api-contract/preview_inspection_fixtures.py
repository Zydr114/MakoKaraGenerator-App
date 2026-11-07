"""Finite wire examples for per-frame preview inspection (contract 1.48.0)."""
import copy

PREFIX = "preview-inspection-"
METADATA = dict(requestId="preview-inspection-example", hostInstanceId="host-example", validUntilUtc="2026-11-07T12:01:00Z")
SCRIPT = "ABCDEF0123456789"


def response():
    quad = [0, 0, 10, 0, 10, 10, 0, 10]
    inspection = dict(
        canvas=dict(width=1920, height=1080), prepared=True,
        tokens=[dict(tokenId="token-1", bindingId="binding-1", lineId="line-1", independent=False,
            diagnosticCode="ClusterSpansElements", ink=quad,
            clusters=[dict(unitIndex=0, ownerTokenIds=["token-1", "token-2"], independent=False,
                diagnosticCode="ClusterSpansElements", ink=quad)],
            ruby=[dict(rubyId="ruby-1", ink=quad)], omittedClusters=0)],
        lines=[dict(lineId="line-1", bindingId="binding-1", bounds=quad)],
        cursors=[dict(lineId="line-1", bindingId="binding-1", tokenId="token-1", progress=0.5, progressSource="fromToken",
            point=[5, 5], start=[0, 5], end=[10, 5])],
        particles=[dict(instanceId="cursor", births=2, alive=1, trailSamples=3, birthPoints=[1, 2, 3, 4], omittedBirthPoints=0)],
        effects=[dict(instanceId="cursor", packageId="demo.cursor", packageRevision="sha256:" + "a" * 64, exportId="main",
            sdkMajor=2, bindingKind="lines", lineIds=["line-1"], scriptId=SCRIPT, omittedLines=0)],
        cost=dict(wallMs=12.5, prepareMs=3, scriptMs=1.5, graphMs=0, materialMs=0, particleMs=0.25, rasterMs=4,
            validationMs=0, readbackMs=0.5, scriptCalls=3, readbackBytes=2048, allocatedBytes=4096, gc0=0, gc1=0, gc2=0,
            scripts=[dict(scriptId=SCRIPT, stage="Advance", calls=2, wallMs=1.5, maxMs=1)], scriptOverflow=0,
            anchors=dict(uniqueTimes=1, textCalls=2, futureEndpoints=0, memoHits=3, memoMisses=1), sessionOpenMs=40.125),
        truncated=dict(tokens=0, lines=0, cursors=0, particles=0, effects=0))
    return dict(frameId="frame-1", previewId="preview-1", previewGeneration="7", sourceRevision="3", timeUs="1250000",
        width=640, height=360, renderBackend="gpu", inspection=inspection,
        host=dict(renderRoundTripMs=15.75, encodeMs=2.5, pngBytes=48213), protocolVersion="1")


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, valid=valid, value=copy.deepcopy(value)))

    request = dict(expectedSourceRevision="3", previewGeneration="7", requestSeq="1", timeUs="1250000", intent="seek", metadata=METADATA)
    add("request-omitted", "PreviewFrameCommandRequest", request)
    add("request-true", "PreviewFrameCommandRequest", {**request, "inspect": True})
    add("request-false", "PreviewFrameCommandRequest", {**request, "inspect": False})
    add("request-null", "PreviewFrameCommandRequest", {**request, "inspect": None})
    add("request-string", "PreviewFrameCommandRequest", {**request, "inspect": "true"}, False)
    add("request-number", "PreviewFrameCommandRequest", {**request, "inspect": 1}, False)

    body = response()
    add("response", "PreviewFrameInspectionResponse", body)
    unprepared = copy.deepcopy(body)
    unprepared["inspection"].update(prepared=False, tokens=[], lines=[], cursors=[], particles=[])
    del unprepared["inspection"]["cost"]["sessionOpenMs"]
    add("response-unprepared", "PreviewFrameInspectionResponse", unprepared)
    warped = copy.deepcopy(body)
    warped["inspection"]["tokens"][0]["ink"] = list(range(32))
    del warped["inspection"]["tokens"][0]["ruby"][0]["ink"]
    add("response-warped-outline", "PreviewFrameInspectionResponse", warped)
    for field in body:
        if field != "protocolVersion":
            add("response-missing-" + field, "PreviewFrameInspectionResponse", {k: v for k, v in body.items() if k != field}, False)
    for name, change in (("time-number", dict(timeUs=1250000)), ("backend", dict(renderBackend="vulkan")),
                         ("protocol", dict(protocolVersion="2"))):
        add("response-" + name, "PreviewFrameInspectionResponse", {**body, **change}, False)
    for name, path, value in (
            ("polygon-too-long", ("tokens", 0, "ink"), list(range(34))),
            ("point-arity", ("cursors", 0, "point"), [1, 2, 3]),
            ("progress-range", ("cursors", 0, "progress"), 1.5),
            ("progress-source", ("cursors", 0, "progressSource"), "token"),
            ("sdk-major", ("effects", 0, "sdkMajor"), 3),
            ("binding-kind", ("effects", 0, "bindingKind"), "group"),
            ("script-id", ("effects", 0, "scriptId"), "abc"),
            ("script-stage", ("cost", "scripts", 0, "stage"), "Declare")):
        damaged = copy.deepcopy(body)
        target = damaged["inspection"]
        for key in path[:-1]:
            target = target[key]
        target[path[-1]] = value
        add("response-" + name, "PreviewFrameInspectionResponse", damaged, False)

    frame = dict(frameId="frame-1", previewId="preview-1", previewGeneration="7", sourceRevision="3", requestSeq="1",
        timeUs="1250000", width=640, height=360, alphaMode="opaque",
        content=dict(handleId="handle-1", contentType="image/png", sizeBytes=48213, sha256="b" * 64,
            expiresAtUtc="2026-11-07T12:00:30Z", contentUrl="/api/v1/preview-frames/frame-1/content"), renderBackend="gpu")
    add("frame-without-url", "PreviewFrameMetadataDto", frame)
    add("frame-with-url", "PreviewFrameMetadataDto", {**frame, "inspectionUrl": "/api/v1/preview-frames/frame-1/inspection"})
    add("error-unavailable", "BackendErrorResponse", dict(code="previewInspectionUnavailable",
        message="这一帧请求时没有开启检查，或检查投影没有生成。", field="frameId", protocolVersion="1"))
    return cases


def append_to(document):
    result = copy.deepcopy(document)
    result["cases"] = [case for case in result.get("cases", []) if not case["name"].startswith(PREFIX)] + fixtures()
    return result
