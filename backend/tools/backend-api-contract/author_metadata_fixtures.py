"""Finite wire examples for exact source inspection and optional complete adoption."""
import copy

PREFIX = "author-metadata-"
REVISION = "sha256:" + "a" * 64
SOURCE = "sha256:" + "b" * 64
DRAFT = "draft-" + "d" * 32
METADATA = dict(requestId="author-metadata-example", hostInstanceId="host-example", validUntilUtc="2026-11-05T12:01:00Z")


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, valid=valid, value=copy.deepcopy(value)))

    query = dict(expectedDraftRevision=REVISION, sourceRevision=SOURCE)
    add("inspection-query", "AuthorPackageQuery", query)
    for field in query:
        add("inspection-query-missing-" + field, "AuthorPackageQuery", {k: v for k, v in query.items() if k != field}, False)
        for name, value in (("empty", ""), ("whitespace", " "), ("number", 1), ("latest", "latest"), ("uppercase", "sha256:" + "A" * 64)):
            add("inspection-query-" + field + "-" + name, "AuthorPackageQuery", {**query, field: value}, False)
    add("inspection-query-unknown", "AuthorPackageQuery", {**query, "packageId": "demo.closed"}, False)

    response = dict(draftId=DRAFT, draftRevision=REVISION, sourceRevision=SOURCE, packageId="demo.closed",
        packageRevision=SOURCE, name="Demo", version="1.0.0", packageVersion=3, sdkVersion="2.0.0",
        shaderProfile="skia-ganesh-3.119.4-v2", structureValid=True,
        parameters=[dict(id="counter", type="integer", label="Counter", defaultValueJson="9007199254740993",
            minimumJson="-9223372036854775808", maximumJson="9223372036854775807", options=[])],
        exports=[dict(id="migrated", kind="controller", name="Migrated", parameterIds=["counter"], bindingKinds=["tokens"],
            declaration=dict(id="migrated", kind="controller", parameters=["counter"], bindingKinds=["tokens"], outputs=[]))],
        bindingKinds=["tokens"], inputs=[dict(path="graphs/tint.json", inputs=[dict(id="color", source="TargetColor")])],
        files=[dict(path="src/PackEffect.cs", role="csharp", size=250, sha256="c" * 64)], diagnostics=[], protocolVersion="1")
    add("inspection-response-exact-integer", "AuthorSourceInspectionResponse", response)
    add("inspection-response-structure-diagnostic", "AuthorSourceInspectionResponse", {**response, "structureValid": False,
        "parameters": [], "exports": [], "bindingKinds": [], "inputs": [],
        "diagnostics": [dict(code="PackageParameterMissing", path="exports.controller.parameters", message="缺少参数声明。")]})
    add("inspection-response-v1", "AuthorSourceInspectionResponse", {**response, "packageVersion": 2, "sdkVersion": "1.0.0", "shaderProfile": "skia-raster-3.119.4-v1"})
    add("inspection-response-sdk2-patch", "AuthorSourceInspectionResponse", {**response, "sdkVersion": "2.0.1"})
    add("inspection-response-future-field", "AuthorSourceInspectionResponse", {**response, "futureField": True})
    for field in response:
        add("inspection-response-missing-" + field, "AuthorSourceInspectionResponse", {k: v for k, v in response.items() if k != field}, False)
    for field in ("draftRevision", "sourceRevision", "packageRevision"):
        add("inspection-response-invalid-" + field, "AuthorSourceInspectionResponse", {**response, field: "latest"}, False)
    for name, value in (("default-number", {"defaultValueJson": 9007199254740993}),
                        ("minimum-number", {"minimumJson": -9223372036854775808}),
                        ("maximum-number", {"maximumJson": 9223372036854775807})):
        add("inspection-response-" + name, "AuthorSourceInspectionResponse", {**response, "parameters": [{**response["parameters"][0], **value}]}, False)
    wrong_file = {k: v for k, v in response["files"][0].items() if k != "size"}
    add("inspection-response-sizeBytes-is-not-size", "AuthorSourceInspectionResponse", {**response, "files": [{**wrong_file, "sizeBytes": 250}]}, False)
    add("inspection-response-size-int64", "AuthorSourceInspectionResponse", {**response, "files": [{**response["files"][0], "size": 9007199254740993}]})
    add("inspection-response-size-string", "AuthorSourceInspectionResponse", {**response, "files": [{**response["files"][0], "size": "250"}]}, False)

    adopt = dict(expectedDraftRevision=REVISION, sourceRevision=SOURCE, metadata=METADATA)
    configuration = dict(name="Migrated config", exportId="migrated", parametersJson='{"counter":9007199254740993}',
        relativeTarget="tokenFamily", readScope="project", branchInput=None, fixedSeed="18446744073709551615", overlayLayer="package")
    add("adopt-legacy-three-fields", "AuthorAdoptSourceRevisionRequest", adopt)
    add("adopt-null-configuration", "AuthorAdoptSourceRevisionRequest", {**adopt, "configuration": None})
    add("adopt-complete-configuration", "AuthorAdoptSourceRevisionRequest", {**adopt, "configuration": configuration})
    for field in configuration:
        add("adopt-configuration-missing-" + field, "AuthorAdoptSourceRevisionRequest", {**adopt, "configuration": {k: v for k, v in configuration.items() if k != field}}, False)
    for field in ("packageId", "sourceRevision", "kind", "bindingKinds"):
        add("adopt-configuration-server-field-" + field, "AuthorAdoptSourceRevisionRequest", {**adopt, "configuration": {**configuration, field: "forbidden"}}, False)
    for name, change in (("parameters-object", dict(parametersJson={})), ("name-empty", dict(name="")),
            ("name-whitespace", dict(name=" ")), ("name-too-long", dict(name="n" * 257)), ("export-empty", dict(exportId="")),
            ("target-invalid", dict(relativeTarget="group")), ("scope-invalid", dict(readScope="unknown")),
            ("layer-invalid", dict(overlayLayer="unknown")), ("unused-branch", dict(branchInput="translation")),
            ("branch-null", dict(readScope="branchInput", branchInput=None)), ("branch-empty", dict(readScope="branchInput", branchInput="")),
            ("seed-number", dict(fixedSeed=18446744073709551615)), ("seed-empty", dict(fixedSeed="")),
            ("seed-negative", dict(fixedSeed="-1")), ("seed-plus", dict(fixedSeed="+1")),
            ("seed-space", dict(fixedSeed=" 1")), ("seed-final-newline", dict(fixedSeed="1\n")),
            ("seed-decimal", dict(fixedSeed="1.0")), ("seed-exponent", dict(fixedSeed="1e1")),
            ("seed-overflow", dict(fixedSeed="18446744073709551616")), ("seed-large-overflow", dict(fixedSeed="999999999999999999999"))):
        add("adopt-configuration-" + name, "AuthorAdoptSourceRevisionRequest", {**adopt, "configuration": {**configuration, **change}}, False)
    for name, change in (("seed-null", dict(fixedSeed=None)), ("seed-zero", dict(fixedSeed="0")),
            ("seed-leading-zero", dict(fixedSeed="00018446744073709551615")),
            ("branch", dict(readScope="branchInput", branchInput="translation"))):
        add("adopt-configuration-" + name, "AuthorAdoptSourceRevisionRequest", {**adopt, "configuration": {**configuration, **change}})
    add("adopt-no-metadata", "AuthorAdoptSourceRevisionRequest", {k: v for k, v in adopt.items() if k != "metadata"}, False)
    add("adopt-unknown-envelope", "AuthorAdoptSourceRevisionRequest", {**adopt, "packageId": "forbidden"}, False)
    # Source inspection grants no new distributable package reference kind.
    add("public-reference-authorDraft-rejected", "EffectPackageReferenceDtoInput",
        dict(kind="authorDraft", packageId="demo.closed", packageRevision=SOURCE, draftId=DRAFT), False)
    for name, code, failure in (("inspection-budget", "resourceTooLarge", "AuthorSourceInspectionTooLarge"),
            ("inspection-read", "projectLoadFailed", "AuthorSourceInspectionReadFailed"),
            ("inspection-identity", "projectLoadFailed", "AuthorSourceIdentityMismatch"),
            ("adopt-uncompiled", "commandRejected", "AuthorSourceCompilationRequired"),
            ("adopt-configuration", "invalidRequest", "AuthorConfigurationInvalid")):
        add("error-" + name, "BackendErrorResponse", dict(code=code, message="具名拒绝；完整操作未发布。", field="configuration.fixedSeed" if name == "adopt-configuration" else "sourceRevision", failureCode=failure, protocolVersion="1"))
    return cases


def append_to(document):
    result = copy.deepcopy(document)
    result["cases"] = [case for case in result.get("cases", []) if not case["name"].startswith(PREFIX)] + fixtures()
    return result
