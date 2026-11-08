"""Finite wire examples for author draft image/font replacement (contract 1.49.0)."""
import copy

PREFIX = "author-replacement-"
REVISION = "sha256:" + "a" * 64
SOURCE = "sha256:" + "b" * 64
DRAFT = "draft-" + "d" * 32
ASSET = "asset-" + "e" * 32
METADATA = dict(requestId="author-replacement-example", hostInstanceId="host-example", validUntilUtc="2026-11-08T12:01:00Z")


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, valid=valid, value=copy.deepcopy(value)))

    save = dict(expectedDraftRevision=REVISION, manifestJson='{"files":[]}',
        textFiles=[dict(path="src/PackEffect.cs", text="// effect")], metadata=METADATA)
    replaced = [dict(path="assets/spark.png", assetId=ASSET)]
    add("save-legacy", "AuthorPackageFilesRequest", save)
    add("save-replaced", "AuthorPackageFilesRequest", {**save, "replacedFiles": replaced, "baseSourceRevision": SOURCE})
    add("save-replaced-latest-base", "AuthorPackageFilesRequest", {**save, "replacedFiles": replaced})
    add("save-base-only", "AuthorPackageFilesRequest", {**save, "baseSourceRevision": SOURCE})
    add("save-null-fields", "AuthorPackageFilesRequest", {**save, "replacedFiles": None, "baseSourceRevision": None})
    add("save-empty-replacements", "AuthorPackageFilesRequest", {**save, "replacedFiles": []})
    for name, change in (("base-latest", dict(baseSourceRevision="latest")), ("base-number", dict(baseSourceRevision=1)),
            ("base-uppercase", dict(baseSourceRevision="sha256:" + "B" * 64)),
            ("replacements-object", dict(replacedFiles=replaced[0])),
            ("replacement-missing-path", dict(replacedFiles=[dict(assetId=ASSET)])),
            ("replacement-missing-asset", dict(replacedFiles=[dict(path="assets/spark.png")])),
            ("replacement-empty-path", dict(replacedFiles=[dict(path="", assetId=ASSET)])),
            ("replacement-whitespace-asset", dict(replacedFiles=[dict(path="assets/spark.png", assetId=" ")])),
            ("replacement-unknown-field", dict(replacedFiles=[{**replaced[0], "sha256": "c" * 64}])),
            ("replacement-null-item", dict(replacedFiles=[None]))):
        add("save-" + name, "AuthorPackageFilesRequest", {**save, **change}, False)

    request = dict(expectedDraftRevision=REVISION, sourceRevision=SOURCE, path="assets/spark.png", assetId=ASSET)
    add("inspect-request", "AuthorPackageFileReplacementInspectionRequest", request)
    for field in request:
        add("inspect-request-missing-" + field, "AuthorPackageFileReplacementInspectionRequest",
            {k: v for k, v in request.items() if k != field}, False)
    for name, change in (("revision-latest", dict(expectedDraftRevision="latest")), ("source-number", dict(sourceRevision=1)),
            ("path-empty", dict(path="")), ("asset-whitespace", dict(assetId=" ")), ("unknown", dict(metadata=METADATA))):
        add("inspect-request-" + name, "AuthorPackageFileReplacementInspectionRequest", {**request, **change}, False)

    image = dict(draftId=DRAFT, draftRevision=REVISION, sourceRevision=SOURCE, path="assets/spark.png", role="image",
        current=dict(format="png", sizeBytes=120, sha256="c" * 64, width=8, height=8),
        replacement=dict(format="png", sizeBytes=9007199254740993, sha256="d" * 64, width=16, height=12),
        warnings=[dict(code="imageSizeChanged", message="尺寸从 8×8 变为 16×12；按像素裁切或假定尺寸的效果可能需要调整。")],
        protocolVersion="1")
    add("inspect-image", "AuthorPackageFileReplacementInspectionResponse", image)
    add("inspect-image-same-size", "AuthorPackageFileReplacementInspectionResponse",
        {**image, "replacement": {**image["replacement"], "width": 8, "height": 8}, "warnings": []})
    font = {**image, "path": "fonts/fixed-cjk.ttc", "role": "font",
        "current": dict(format="collection", sizeBytes=4096, sha256="c" * 64, familyNames=["Mako Sans CJK SC", "Mako Sans CJK JP"]),
        "replacement": dict(format="collection", sizeBytes=4096, sha256="d" * 64, familyNames=["Noto Sans CJK SC"]),
        "warnings": [dict(code="fontFamilyChanged", message="字体家族从「Mako Sans CJK SC」变为「Noto Sans CJK SC」。")]}
    add("inspect-font", "AuthorPackageFileReplacementInspectionResponse", font)
    add("inspect-future-field", "AuthorPackageFileReplacementInspectionResponse", {**image, "futureField": True})
    for field in image:
        if field != "protocolVersion":
            add("inspect-response-missing-" + field, "AuthorPackageFileReplacementInspectionResponse",
                {k: v for k, v in image.items() if k != field}, False)
    for name, change in (("role-svg", dict(role="svg")), ("protocol", dict(protocolVersion="2")),
            ("warning-code", dict(warnings=[dict(code="formatChanged", message="x")])),
            ("warnings-too-many", dict(warnings=image["warnings"] * 3)),
            ("size-string", dict(current={**image["current"], "sizeBytes": "120"})),
            ("size-zero", dict(current={**image["current"], "sizeBytes": 0})),
            ("sha-prefixed", dict(current={**image["current"], "sha256": "sha256:" + "c" * 64})),
            ("format-uppercase", dict(current={**image["current"], "format": "PNG"})),
            ("width-zero", dict(current={**image["current"], "width": 0})),
            ("family-empty", dict(current={**font["current"], "familyNames": []}))):
        add("inspect-response-" + name, "AuthorPackageFileReplacementInspectionResponse", {**image, **change}, False)

    for code, field in (("AuthorReplacementTargetInvalid", "replacedFiles[0].path"),
                        ("AuthorReplacementFormatMismatch", "replacedFiles[0].assetId"),
                        ("AuthorReplacementFontInvalid", "assetId")):
        add("error-" + code, "BackendErrorResponse", dict(code="invalidRequest", message="替换被拒绝。", field=field,
            failureCode=code, protocolVersion="1"))
    return cases


def append_to(document):
    result = copy.deepcopy(document)
    result["cases"] = [case for case in result.get("cases", []) if not case["name"].startswith(PREFIX)] + fixtures()
    return result
