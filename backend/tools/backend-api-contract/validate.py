#!/usr/bin/env python3
"""Validate OpenAPI, all schema references, examples and optional saved HTTP responses."""
import argparse
import hashlib
import json
import re
from pathlib import Path
from urllib.parse import urlsplit

from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate_spec

ROOT = Path(__file__).resolve().parents[2]


def expected_product_versions(contract_version, spec_path):
    historical = {"1.37.0": "0.103.0", "1.38.0": "0.104.0", "1.38.1": "0.104.1", "1.39.0": "0.106.0", "1.40.0": "0.108.0", "1.41.0": "0.109.0", "1.42.0": "0.110.0", "1.42.1": "0.110.1", "1.42.2": "0.110.2", "1.42.3": "0.110.3", "1.42.4": "0.110.6", "1.43.0": "0.111.3", "1.44.0": "0.112.1", "1.44.1": "0.112.2", "1.44.2": "0.112.3", "1.45.0": "0.112.8", "1.46.0": "0.112.14", "1.46.1": "0.112.15", "1.47.0": "0.112.37", "1.48.0": "0.112.38", "1.49.0": "0.112.39", "1.49.1": "0.112.40", "1.49.2": "0.112.72", "1.50.0": "0.112.85", "1.51.0": "0.112.86", "1.51.1": "0.112.88", "1.52.0": "0.112.89", "1.52.1": "0.112.90", "1.53.0": "0.113.0", "1.54.0": "0.113.0", "1.55.0": "0.114.0", "1.55.1": "0.114.1", "1.56.0": "0.115.0", "1.57.0": "0.116.0", "1.58.0": "0.116.0", "1.59.0": "0.116.1"}
    if contract_version not in ("1.38.1", "1.39.0", "1.40.0", "1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        return {historical[contract_version]}
    # The public API can keep its contract version across an internal renderer
    # release. The canonical frozen document must match the current product.
    product = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>",
                        (ROOT / "Directory.Build.props").read_text()).group(1)
    if spec_path.resolve() == (ROOT / "web/openapi.json").resolve():
        return {product}
    # Explicit offline --spec snapshots retain the original reviewed product;
    # do not weaken validation to accept an arbitrary historical version.
    return {historical[contract_version], product}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--http-log", type=Path, action="append", default=[])
    parser.add_argument("--report", type=Path)
    parser.add_argument("--spec", type=Path, default=ROOT / "web/openapi.json", help="Read an offline author preview without changing frozen web files")
    parser.add_argument("--examples", type=Path, default=ROOT / "web/examples.json")
    parser.add_argument("--extra-examples", type=Path, action="append", default=[], help="Additional incremental fixture JSON files")
    args = parser.parse_args()
    from p1p2_runtime_schema import PREVIEW as P1_PREVIEW, VALIDATION as P1_VALIDATION, JOB as P1_JOB, RUNTIME
    spec = json.loads(args.spec.read_text())
    actual_version = spec["info"]["version"]
    # P3/P4 and their documentation patch retain the historical 1.36 gates.
    # Keep previous hash and shape assertions active for the current contract.
    p4_versions = ("1.38.0", "1.38.1", "1.39.0", "1.40.0", "1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0")
    sdk_v2_author = actual_version in ("1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0")
    p3_versions = ("1.37.0", *p4_versions)
    legacy_version = "1.36.0" if actual_version in p3_versions else actual_version
    validate_spec(spec)
    components = spec["components"]

    def check_refs(node):
        if isinstance(node, dict):
            if "$ref" in node:
                pointer = node["$ref"]
                assert pointer.startswith("#/"), f"Unexpected external reference: {pointer}"
                target = spec
                for part in pointer[2:].split("/"):
                    target = target[part.replace("~1", "/").replace("~0", "~")]
            for value in node.values():
                check_refs(value)
        elif isinstance(node, list):
            for value in node:
                check_refs(value)

    check_refs(spec)
    for schema in components["schemas"].values():
        Draft202012Validator.check_schema(schema)

    def errors(schema, instance):
        root_schema = {"allOf": [schema], "components": components}
        return list(Draft202012Validator(root_schema, format_checker=FormatChecker()).iter_errors(instance))

    example_document = json.loads(args.examples.read_text())
    assert example_document.get("contractVersion") == actual_version, (
        "Example corpus contractVersion must match OpenAPI info.version")
    frozen_cases = example_document["cases"]
    # 1.50.0 replaced the single-state style surface. The reviewed 1.49.2 corpus
    # is restored exactly from the migrated cases plus the archived originals and
    # audited by the historical gates below; the current corpus (migrated cases and
    # the new style-v2 fixtures) is validated against the current spec instead.
    # 1.51.0 added always-present fallback diagnostics to export reports, export job
    # results and scene measurements. The whole current corpus is validated against
    # the current spec; the reviewed 1.50.0 corpus is restored for the gates below.
    validation_cases = None
    render_base_cases = None
    lyrics_refactor_additions = []
    if actual_version == "1.59.0":
        from lyrics_refactor_fixtures import fixtures as lyrics_refactor_fixtures, PREFIX as LYRICS_REFACTOR_PREFIX
        from lyrics_refactor_schema import NEW_SCHEMAS as LYRICS_REFACTOR_SCHEMAS
        lyrics_refactor_additions = lyrics_refactor_fixtures()
        assert frozen_cases[-len(lyrics_refactor_additions):] == lyrics_refactor_additions
        validation_cases = frozen_cases
        frozen_cases = frozen_cases[:-len(lyrics_refactor_additions)]
        assert not any(case["name"].startswith(LYRICS_REFACTOR_PREFIX) for case in frozen_cases)
        assert all(name in components["schemas"] for name in LYRICS_REFACTOR_SCHEMAS)
        assert components["schemas"]["ProjectLyricTokenDto"]["properties"]["phraseEndHint"] == {"type": "boolean"}
        assert "phraseEndHint" not in components["schemas"]["ProjectLyricTokenDto"].get("required", [])
        assert components["schemas"]["LyricTimingCommandResultDto"]["properties"]["closeGuardApplied"] == {"type": "boolean"}
        assert "closeGuardApplied" not in components["schemas"]["LyricTimingCommandResultDto"].get("required", [])
    gpu_stream_recovery_additions = []
    if actual_version in ("1.58.0", "1.59.0"):
        from gpu_stream_recovery_fixtures import fixtures as gpu_stream_recovery_fixtures, PREFIX as GPU_STREAM_RECOVERY_PREFIX
        gpu_stream_recovery_additions = gpu_stream_recovery_fixtures()
        assert frozen_cases[-len(gpu_stream_recovery_additions):] == gpu_stream_recovery_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = frozen_cases[:-len(gpu_stream_recovery_additions)]
        assert not any(case["name"].startswith(GPU_STREAM_RECOVERY_PREFIX) for case in frozen_cases)
        list_op = spec["paths"]["/api/v1/gpu-preview-streams"]["get"]
        assert list_op["operationId"] == "listGpuPreviewStreams"
        assert list_op["responses"]["200"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/GpuPreviewStreamListResponse"}
        assert components["schemas"]["GpuPreviewStreamState"]["properties"]["quarantined"] == {"type": "boolean"}
        assert "quarantined" not in components["schemas"]["GpuPreviewStreamState"]["required"]
        assert components["schemas"]["GpuPreviewStreamListResponse"]["required"] == ["streams", "protocolVersion"]
    preview_transport_additions = []
    if actual_version in ("1.57.0", "1.58.0", "1.59.0"):
        from preview_transport_fixtures import fixtures as preview_transport_fixtures, restore_cases as restore_preview_transport, PREFIX as PREVIEW_TRANSPORT_PREFIX
        preview_transport_additions = preview_transport_fixtures()
        assert frozen_cases[-len(preview_transport_additions):] == preview_transport_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = restore_preview_transport(frozen_cases[:-len(preview_transport_additions)])
        assert not any(case["name"].startswith(PREVIEW_TRANSPORT_PREFIX) for case in frozen_cases)
        assert components["schemas"]["GpuPreviewStreamCreateRequest"]["properties"]["api"]["enum"] == ["vulkan", "cpu"]
        assert components["schemas"]["GpuPreviewStreamState"]["properties"]["api"]["enum"] == ["vulkan", "cpu"]
        assert components["schemas"]["GpuPreviewStreamState"]["properties"]["transport"]["enum"] == ["dma-buf", "shm"]
        assert "transport" in components["schemas"]["GpuPreviewStreamState"]["required"]
    ruby_style_additions = []
    if actual_version in ("1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from ruby_style_fixtures import fixtures as ruby_style_fixtures, restore_cases as restore_ruby, PREFIX as RUBY_STYLE_PREFIX
        ruby_style_additions = ruby_style_fixtures()
        assert frozen_cases[-len(ruby_style_additions):] == ruby_style_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = restore_ruby(frozen_cases[:-len(ruby_style_additions)])
        assert not any(case["name"].startswith(RUBY_STYLE_PREFIX) for case in frozen_cases)
    ttml_import_additions = []
    if actual_version in ("1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from ttml_import_fixtures import fixtures as ttml_import_fixtures, PREFIX as TTML_IMPORT_PREFIX, NEW_SCHEMAS as TTML_IMPORT_SCHEMAS
        ttml_import_additions = ttml_import_fixtures()
        assert frozen_cases[-len(ttml_import_additions):] == ttml_import_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = frozen_cases[:-len(ttml_import_additions)]
        assert not any(case["name"].startswith(TTML_IMPORT_PREFIX) for case in frozen_cases)
        assert all(name in components["schemas"] for name in TTML_IMPORT_SCHEMAS)
        assert components["schemas"]["ImportLyricsDestinationInput"]["oneOf"][-1] == {"$ref": "#/components/schemas/AllSourcesLyricsDestinationDtoInput"}
    lead_in_presentation_additions = []
    if actual_version in ("1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from lead_in_presentation_fixtures import fixtures as lead_in_presentation_fixtures, PREFIX as LEAD_IN_PRESENTATION_PREFIX
        from editing_completion_schema import LEAD_IN_PRESENTATION_SCHEMAS
        lead_in_presentation_additions = lead_in_presentation_fixtures()
        assert frozen_cases[-len(lead_in_presentation_additions):] == lead_in_presentation_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = frozen_cases[:-len(lead_in_presentation_additions)]
        assert not any(case["name"].startswith(LEAD_IN_PRESENTATION_PREFIX) for case in frozen_cases)
        assert all(name in components["schemas"] for name in LEAD_IN_PRESENTATION_SCHEMAS)
        assert components["schemas"]["SetLeadInAttachmentCommandDtoInput"]["properties"]["mode"]["enum"] == [
            "FixedParent", "CueTargetText", "CueTargetAboveStart"]
        assert "setCountdownPresentation" in components["schemas"]["OrchestrationLeadInCommandResultDto"]["properties"]["operation"]["enum"]
    sdk_v1_removal_additions = []
    if actual_version in ("1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from sdk_v1_removal_fixtures import fixtures as sdk_v1_removal_fixtures, restore_cases as restore_sdk_v1_removal, PREFIX as SDK_V1_REMOVAL_PREFIX
        sdk_v1_removal_additions = sdk_v1_removal_fixtures()
        assert frozen_cases[-len(sdk_v1_removal_additions):] == sdk_v1_removal_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = restore_sdk_v1_removal(frozen_cases[:-len(sdk_v1_removal_additions)])
        assert not any(case["name"].startswith(SDK_V1_REMOVAL_PREFIX) for case in frozen_cases)
        inspection_response = components["schemas"]["AuthorSourceInspectionResponse"]
        assert inspection_response["properties"]["packageVersion"]["enum"] == [3]
        assert inspection_response["properties"]["shaderProfile"]["enum"] == ["skia-ganesh-3.119.4-v2"]
    source_material_failure_additions = []
    if actual_version in ("1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from source_material_failure_fixtures import fixtures as source_material_failure_fixtures, PREFIX as SOURCE_MATERIAL_FAILURE_PREFIX
        from source_material_failure_schema import NEW_SCHEMAS as SOURCE_MATERIAL_FAILURE_SCHEMAS
        source_material_failure_additions = source_material_failure_fixtures()
        assert frozen_cases[-len(source_material_failure_additions):] == source_material_failure_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = frozen_cases[:-len(source_material_failure_additions)]
        assert not any(case["name"].startswith(SOURCE_MATERIAL_FAILURE_PREFIX) for case in frozen_cases)
        assert all(name in components["schemas"] for name in SOURCE_MATERIAL_FAILURE_SCHEMAS)
        assert "sourceMaterialFailure" not in components["schemas"]["BackendDiagnosticDto"]["required"]
        assert "sourceMaterialFailure" not in components["schemas"]["BackendDiagnosticDtoRaw"]["required"]
        for operation_id in ("exportProject", "exportResult", "recoveredExportResult", "exportDiagnostics", "jobState"):
            operation = next(operation for methods in spec["paths"].values() for operation in methods.values()
                             if operation["operationId"] == operation_id)
            assert operation["x-source-material-failure"]["runtimeOccurrence"] is False
    fallback_additions = []
    if actual_version in ("1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from export_fallback_fixtures import fixtures as fallback_fixtures, restore_cases as restore_fallback, PREFIX as FALLBACK_PREFIX
        from export_fallback_schema import NEW_SCHEMAS as FALLBACK_SCHEMAS, MAXIMUM_ITEMS as FALLBACK_MAXIMUM_ITEMS
        fallback_additions = fallback_fixtures()
        assert frozen_cases[-len(fallback_additions):] == fallback_additions
        if validation_cases is None:
            validation_cases = frozen_cases
        frozen_cases = restore_fallback(frozen_cases[:-len(fallback_additions)])
        render_base_cases = frozen_cases
        assert not any(case["name"].startswith(FALLBACK_PREFIX) for case in frozen_cases)
        assert all(name in components["schemas"] for name in FALLBACK_SCHEMAS)
        raw_report = components["schemas"]["ExportReportDtoRaw"]
        fallback_fields = {"fallbackDiagnostics", "fallbackFrameCount", "fallbackDiagnosticsTruncated"}
        # Reports registered before 1.51.0 are served byte for byte without the three fields.
        assert not fallback_fields & set(raw_report["required"])
        assert {field: set(others) | {field} for field, others in raw_report["dependentRequired"].items()} == {field: fallback_fields for field in fallback_fields}
        assert raw_report["properties"]["fallbackDiagnostics"]["items"] == {"$ref": "#/components/schemas/BackendFallbackDiagnosticRaw"}
        assert raw_report["properties"]["fallbackDiagnostics"]["maxItems"] == FALLBACK_MAXIMUM_ITEMS
        assert components["schemas"]["ExportJobResultDto"]["properties"]["fallbackDiagnosticCount"]["maximum"] == FALLBACK_MAXIMUM_ITEMS
        assert "fallbackDiagnosticCount" in components["schemas"]["ExportJobResultDto"]["required"]
        measurement = components["schemas"]["SceneMeasurementDataDto"]
        assert "fallbackDiagnostics" in measurement["required"]
        assert measurement["properties"]["fallbackDiagnostics"]["items"] == {"$ref": "#/components/schemas/BackendFallbackDiagnostic"}
    style_v2_additions = []
    if actual_version in ("1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from style_v2_fixtures import fixtures as style_v2_fixtures, restore_legacy, PREFIX as STYLE_V2_PREFIX
        from static_style_schema import PROPERTIES as STYLE_V2_PROPERTIES
        style_v2_additions = style_v2_fixtures()
        assert frozen_cases[-len(style_v2_additions):] == style_v2_additions
        migrated_cases = frozen_cases[:-len(style_v2_additions)]
        assert not any(case["name"].startswith(STYLE_V2_PREFIX) for case in migrated_cases)
        if validation_cases is None:
            validation_cases = migrated_cases + style_v2_additions
        frozen_cases = restore_legacy(migrated_cases)
        assert components["schemas"]["StylePropertyPath"]["enum"] == list(STYLE_V2_PROPERTIES)
        assert "TemplateProperty" not in components["schemas"]
        assert components["schemas"]["BackendDiagnosticSourceOwnerDto"]["properties"]["slot"]["maximum"] == 3
    author_replacement_additions = []
    if actual_version in ("1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from author_replacement_fixtures import fixtures as author_replacement_fixtures
        from author_replacement_schema import NEW_SCHEMAS as AUTHOR_REPLACEMENT_SCHEMAS, LIMITS as AUTHOR_REPLACEMENT_LIMITS, CAPABILITY as AUTHOR_REPLACEMENT_CAPABILITY
        author_replacement_additions = author_replacement_fixtures()
        assert frozen_cases[-len(author_replacement_additions):] == author_replacement_additions
        frozen_cases = frozen_cases[:-len(author_replacement_additions)]
        replacement = spec["paths"]["/api/v1/drafts/{id}/package-file-replacement-inspection"]
        assert set(replacement) == {"post"}
        replacement = replacement["post"]
        assert replacement["operationId"] == "inspectAuthorPackageFileReplacement"
        assert replacement["x-capability"] == AUTHOR_REPLACEMENT_CAPABILITY
        assert replacement["x-author-replacement-limits"] == AUTHOR_REPLACEMENT_LIMITS
        assert replacement["x-receipt-location"] == "none"
        assert replacement["x-behavior-section"] == "21"
        assert replacement["requestBody"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/AuthorPackageFileReplacementInspectionRequest"}
        assert replacement["responses"]["200"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/AuthorPackageFileReplacementInspectionResponse"}
        assert all(name in components["schemas"] for name in AUTHOR_REPLACEMENT_SCHEMAS)
        save = components["schemas"]["AuthorPackageFilesRequest"]
        assert save["additionalProperties"] is False
        assert {"replacedFiles", "baseSourceRevision"} <= set(save["properties"])
        assert not {"replacedFiles", "baseSourceRevision"} & set(save["required"])
        assert components["schemas"]["AuthorPackageFileReplacementInspectionRequest"]["additionalProperties"] is False
        assert components["schemas"]["AuthorPackageFileReplacementDtoInput"]["additionalProperties"] is False
        assert spec["paths"]["/api/v1/drafts/{id}/package-files"]["put"]["x-receipt-location"] == "metadata"
    preview_inspection_additions = []
    if actual_version in ("1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from preview_inspection_fixtures import fixtures as preview_inspection_fixtures
        from preview_inspection_schema import NEW_SCHEMAS as PREVIEW_INSPECTION_SCHEMAS, LIMITS as PREVIEW_INSPECTION_LIMITS
        preview_inspection_additions = preview_inspection_fixtures()
        assert frozen_cases[-len(preview_inspection_additions):] == preview_inspection_additions
        frozen_cases = frozen_cases[:-len(preview_inspection_additions)]
        inspection = spec["paths"]["/api/v1/preview-frames/{id}/inspection"]
        assert set(inspection) == {"get"}
        inspection = inspection["get"]
        assert inspection["operationId"] == "previewFrameInspection"
        assert inspection["x-capability"] == "preview-frames.inspection"
        assert inspection["x-preview-inspection-limits"] == PREVIEW_INSPECTION_LIMITS
        assert inspection["responses"]["200"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/PreviewFrameInspectionResponse"}
        assert "requestBody" not in inspection
        assert all(name in components["schemas"] for name in PREVIEW_INSPECTION_SCHEMAS)
        assert "previewInspectionUnavailable" in components["schemas"]["BackendErrorCode"]["enum"]
        command = components["schemas"]["PreviewFrameCommandRequest"]
        assert command["properties"]["inspect"] == {"anyOf": [{"type": "boolean"}, {"type": "null"}]}
        assert "inspect" not in command["required"]
        assert "inspectionUrl" not in components["schemas"]["PreviewFrameMetadataDto"]["required"]
    author_metadata_additions = []
    if actual_version in ("1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from author_metadata_fixtures import fixtures as author_metadata_fixtures
        author_metadata_additions = author_metadata_fixtures()
        assert frozen_cases[-len(author_metadata_additions):] == author_metadata_additions
        frozen_cases = frozen_cases[:-len(author_metadata_additions)]
        assert len(spec["paths"]) == 113 + (1 if preview_inspection_additions else 0) + (1 if author_replacement_additions else 0)
        assert len(components["schemas"]) == 598 + (15 if preview_inspection_additions else 0) + (5 if author_replacement_additions else 0) + (43 if style_v2_additions else 0) + (2 if fallback_additions else 0) + (4 if source_material_failure_additions else 0) + (len(LEAD_IN_PRESENTATION_SCHEMAS) if lead_in_presentation_additions else 0) + (len(TTML_IMPORT_SCHEMAS) if ttml_import_additions else 0) + (6 if ruby_style_additions else 0) + (1 if gpu_stream_recovery_additions else 0) + (len(LYRICS_REFACTOR_SCHEMAS) if lyrics_refactor_additions else 0)
        assert len(frozen_cases) == 3434
        assert sum(not case["valid"] for case in frozen_cases) == 2548
        inspection = spec["paths"]["/api/v1/drafts/{id}/source-inspection"]["get"]
        assert inspection["operationId"] == "inspectAuthorSource"
        assert inspection["x-capability"] == "author.drafts.source-inspection"
        assert inspection["x-query-schema"] == {"$ref": "#/components/schemas/AuthorPackageQuery"}
        assert inspection["x-query-policy"] == {"allowUnknown": False, "allPresentExactlyOnce": True,
            "caseSensitiveNames": True, "required": ["expectedDraftRevision", "sourceRevision"],
            "allowed": ["expectedDraftRevision", "sourceRevision"]}
        assert inspection["x-receipt-location"] == "none" and "requestBody" not in inspection
        assert inspection["x-request-body-policy"] == "forbidden"
        assert inspection["x-author-inspection-limits"] == {"maxResponseBytes": 8388608, "encoding": "web-json-utf8", "truncate": False}
        assert inspection["responses"]["200"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/AuthorSourceInspectionResponse"}
        assert inspection["responses"]["200"]["headers"]["Cache-Control"]["schema"]["const"] == "no-store"
        response = components["schemas"]["AuthorSourceInspectionResponse"]
        from author_metadata_schema import INSPECTION_FIELDS, CONFIGURATION_FIELDS, uint64_pattern
        assert set(response["properties"]) == set(response["required"]) == set(INSPECTION_FIELDS)
        assert not {"reference", "manifest", "manifestJson", "sourceText", "path"}.intersection(response["properties"])
        assert response.get("additionalProperties") is not False
        request = components["schemas"]["AuthorAdoptSourceRevisionRequest"]
        assert "configuration" not in request["required"]
        assert set(request["required"]) == {"expectedDraftRevision", "sourceRevision", "metadata"}
        configuration, null_shape = request["properties"]["configuration"]["anyOf"]
        assert null_shape == {"type": "null"}
        assert configuration["additionalProperties"] is False
        assert set(configuration["properties"]) == set(configuration["required"]) == set(CONFIGURATION_FIELDS)
        assert configuration["properties"]["name"]["maxLength"] == 256
        assert configuration["properties"]["fixedSeed"]["anyOf"][0]["pattern"] == uint64_pattern()
        for component in ("EffectPackageReferenceDto", "EffectPackageReferenceDtoInput"):
            assert [branch["properties"]["kind"]["const"] for branch in components["schemas"][component]["oneOf"]] == ["asset", "library", "configuration"]
    if actual_version in ("1.44.0", "1.44.1", "1.44.2", "1.45.0"):
        # M08 changes real package/runtime behavior, not the HTTP DTO surface.
        # Preserve the reviewed 1.43 corpus and shape counts, including SDK2 author cases.
        assert len(spec["paths"]) == 112
        assert len(components["schemas"]) == 597
        assert len(frozen_cases) == 3434
        assert sum(not case["valid"] for case in frozen_cases) == 2548
    glow_additions = []
    if actual_version in ("1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from glow_style_fixtures import fixtures as glow_fixtures, migrate_legacy_cases as migrate_glow
        glow_additions = glow_fixtures()
        assert frozen_cases[-len(glow_additions):] == glow_additions
        frozen_cases = migrate_glow(frozen_cases[:-len(glow_additions)], upgrade=False)
    gpu_additions = []
    if actual_version in ("1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from gpu_stream_fixtures import append_to as append_gpu
        gpu_additions = append_gpu({"cases": []})["cases"]
        assert frozen_cases[-len(gpu_additions):] == gpu_additions
        frozen_cases = frozen_cases[:-len(gpu_additions)]
    w8_additions = []
    if actual_version in ("1.40.0", "1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from w8_authoring_fixtures import fixtures as w8_fixtures
        w8_additions = w8_fixtures()["cases"]
        assert frozen_cases[-len(w8_additions):] == w8_additions
        frozen_cases = frozen_cases[:-len(w8_additions)]
    render_additions = []
    render_wire_cases = None
    if actual_version in ("1.39.0", "1.40.0", "1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        from render_backend_fixtures import fixtures as render_fixtures
        from render_backend_schema import migrate_legacy_cases
        # The fixtures sample the reviewed corpus; 1.51.0 reads it from the restored 1.50.0 cases.
        render_additions = render_fixtures(render_base_cases)["cases"]
        assert len(render_additions) == 175
        assert sum(not case["valid"] for case in render_additions) == 109
        assert frozen_cases[-len(render_additions):] == render_additions
        render_wire_cases = frozen_cases[:-len(render_additions)]
        frozen_cases = migrate_legacy_cases(render_wire_cases, historical=True)
    p4_additions = []
    if actual_version in p4_versions:
        from p4_author_fixtures import fixtures as p4_fixtures
        p4_additions = p4_fixtures(include_sdk_v2=sdk_v2_author)["cases"]
        assert frozen_cases[-len(p4_additions):] == p4_additions
        frozen_cases = frozen_cases[:-len(p4_additions)]
    p3_additions = []
    if actual_version in p3_versions:
        from p3_workshop_fixtures import fixtures as p3_fixtures
        p3_additions = p3_fixtures()["cases"]
        assert frozen_cases[-len(p3_additions):] == p3_additions
        frozen_cases = frozen_cases[:-len(p3_additions)]
    p1_additions = []
    if legacy_version == "1.36.0":
        from p1p2_fixtures import fixtures as p1_fixtures
        p1_additions = p1_fixtures()["cases"]
        assert frozen_cases[-len(p1_additions):] == p1_additions
        frozen_cases = frozen_cases[:-len(p1_additions)]
    extra_cases = p1_additions + p3_additions + p4_additions
    external_cases = []
    for extra in args.extra_examples:
        external_cases += json.loads(extra.read_text())["cases"]
    cases = (render_wire_cases + render_additions if render_additions else frozen_cases + extra_cases) + w8_additions + gpu_additions + external_cases
    if glow_additions:
        cases = migrate_glow(cases) + glow_additions
    cases += author_metadata_additions
    cases += preview_inspection_additions
    cases += author_replacement_additions
    if legacy_version == "1.36.0":
        from p1p2_fixtures import migrate_legacy_cases
        # Validate migrated wire values above; audit immutable historical hashes
        # using only the explicitly reversible additive default-field migration.
        frozen_cases = migrate_legacy_cases(frozen_cases, historical=True)
    assert len({case["name"] for case in cases}) == len(cases), "Duplicate example name"
    image_additions = []
    validation_image_additions = []
    export_image_additions = []
    if legacy_version in ("1.35.0", "1.36.0"):
        from project_image_export_fixtures import fixtures as export_image_fixtures
        export_image_additions = export_image_fixtures()["cases"]
    preview_cases = frozen_cases
    if legacy_version in ("1.34.0", "1.35.0", "1.36.0"):
        from project_image_validation_fixtures import fixtures as validation_image_fixtures
        validation_image_additions = validation_image_fixtures()["cases"]
        assert len(validation_image_additions) == 25
        assert sum(not case["valid"] for case in validation_image_additions) == 13
        validation_end = -len(export_image_additions) if legacy_version in ("1.35.0", "1.36.0") else None
        validation_start = -(len(validation_image_additions) + len(export_image_additions)) if legacy_version in ("1.35.0", "1.36.0") else -len(validation_image_additions)
        validation_slice = frozen_cases[validation_start:validation_end]
        assert validation_slice == validation_image_additions
        tail = len(validation_image_additions) + (len(export_image_additions) if legacy_version in ("1.35.0", "1.36.0") else 0)
        preview_cases = frozen_cases[:-tail]
        assert len(preview_cases) == 2848
        assert hashlib.sha256(json.dumps(preview_cases, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest() == "d6c4f6d24e2d4dbd951f1f4a5961c0d1406ec26d090bfb7612f64241c8b751b0"
    if legacy_version in ("1.35.0", "1.36.0"):
        assert frozen_cases[-len(export_image_additions):] == export_image_additions
    if legacy_version in ("1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        from project_image_preview_fixtures import fixtures as image_fixtures
        from project_image_preview_schema import SOURCE_PROFILES, LIMITS
        image_additions = image_fixtures()["cases"]
        assert preview_cases[-len(image_additions):] == image_additions
        # Preserve all 2821 examples of 1.32.1, including measurement corrections.
        assert hashlib.sha256(json.dumps(frozen_cases[:2821], sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest() == "411e5e93d622043505e37a37de1bf4fc4bdfac25a38038a2a46ccbe3fb966429"
        props = components["schemas"]["PreviewRuntimeInfoResponse"]["properties"]
        assert props["sourceProfile"]["const"] == "static-lyrics-scene-v1"
        assert props["sourceProfiles"]["items"]["enum"] == (P1_PREVIEW if legacy_version == "1.36.0" else SOURCE_PROFILES)
        assert props["sourceProfiles"]["uniqueItems"] is True
        for path, method in (("/api/v1/runtime-info", "get"), ("/api/v1/preview-sessions", "post"),
                             ("/api/v1/preview-sessions/{id}/input", "put")):
            operation = spec["paths"][path][method]
            assert operation["x-project-image-preview-profile"] == SOURCE_PROFILES[-1]
            assert operation["x-project-image-preview-limits"] == LIMITS
        assert SOURCE_PROFILES[-1] not in components["schemas"]["SceneMeasurementDataDto"]["properties"]["sourceProfile"]["enum"]
    if legacy_version in ("1.34.0", "1.35.0", "1.36.0"):
        from project_image_validation_schema import SOURCE_PROFILES as VALIDATION_SOURCE_PROFILES, LIMITS as VALIDATION_LIMITS
        assert VALIDATION_SOURCE_PROFILES == [
            "static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1", "static-lyrics-scene-presets-v1",
            "package-lyrics-scene-v1", "project-image-validation-v1"]
        assert VALIDATION_LIMITS == dict(maxInputBytes=4194272, maxImageBytes=2097152, maxImageDimension=4096,
            maxDecodedImageBytes=16777216, maxBaseSnapshotBytes=4194304, maxSourceValues=200000, maxReportBytes=262144)
        assert LIMITS["maxSnapshotBytes"] - VALIDATION_LIMITS["maxInputBytes"] == 8
        props = components["schemas"]["ValidationTaskCapabilitiesDto"]["properties"]
        assert props["sourceProfile"]["enum"] == VALIDATION_SOURCE_PROFILES[:-1]
        assert "sourceProfiles" not in components["schemas"]["ValidationTaskCapabilitiesDto"]["required"]
        assert props["sourceProfiles"]["items"]["enum"] == (P1_VALIDATION if legacy_version == "1.36.0" else VALIDATION_SOURCE_PROFILES)
        assert props["sourceProfiles"]["uniqueItems"] is True
        for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
            source = components["schemas"][component]["properties"]["sourceProfile"]
            source = source["anyOf"][0] if "anyOf" in source else source
            expected_source_enum = VALIDATION_SOURCE_PROFILES + (["project-image-export-v1"] if legacy_version in ("1.35.0", "1.36.0") else [])
            assert source["enum"] == ((P1_JOB + (["author-script-sample-v1"] if p4_additions else []))
                if legacy_version == "1.36.0" else expected_source_enum)
        request = components["schemas"]["ValidationJobRequest"]
        assert request["additionalProperties"] is False
        assert set(request["properties"]) == {"expectedRevision", "framesPerOutput", "metadata"} | ({"renderBackend"} if render_additions else set())
        for path, method in (("/api/v1/validation-capabilities", "get"), ("/api/v1/project-sessions/{id}/validation-jobs", "post"),
                             ("/api/v1/jobs/{id}/validation-result", "get"), ("/api/v1/jobs/{id}/validation-diagnostics", "get"),
                             ("/api/v1/recovery-jobs/{id}/validation-result", "get")):
            operation = spec["paths"][path][method]
            assert operation["x-project-image-validation-profile"] == "project-image-validation-v1"
            assert operation["x-project-image-validation-limits"] == VALIDATION_LIMITS
        assert VALIDATION_SOURCE_PROFILES[-1] not in components["schemas"]["SceneMeasurementDataDto"]["properties"]["sourceProfile"]["enum"]
        assert VALIDATION_SOURCE_PROFILES[-1] not in SOURCE_PROFILES
        for path, method in (("/api/v1/export-capabilities", "get"), ("/api/v1/project-sessions/{id}/export-jobs", "post"),
                             ("/api/v1/project-sessions/{id}/orchestration/measurements", "post")):
            assert "x-project-image-validation-profile" not in spec["paths"][path][method]
    baseline_cases = preview_cases[:-len(image_additions)] if image_additions else preview_cases
    profile_additions = []
    if legacy_version in ("1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        from editing_completion_fixtures import package_measurement_cases
        profile_additions = package_measurement_cases()
        # The frozen examples must end with this contract's additions. Extra
        # fixture files are appended only for validation and must not change
        # the frozen ordering or the historical count assertions below.
        assert baseline_cases[-len(profile_additions):] == profile_additions
        assert components["schemas"]["SceneMeasurementDataDto"]["properties"]["sourceProfile"]["enum"] == [
            "static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1", "static-lyrics-scene-presets-v1",
            "package-lyrics-scene-v1", "package-lyrics-scene-presets-v1"] + ([RUNTIME] if legacy_version == "1.36.0" else [])
    previous_cases = baseline_cases[:-len(profile_additions)] if profile_additions else baseline_cases
    for case in (cases if validation_cases is None else validation_cases):
        found = errors({"$ref": "#/components/schemas/" + case["schema"]}, case["value"])
        assert (not found) == case["valid"], f"Example {case['name']}: expected valid={case['valid']}; {found[:1]}"
    routes = []
    for path, methods in spec["paths"].items():
        for method, operation in methods.items():
            assert operation["operationId"] not in [r[3]["operationId"] for r in routes]
            if method == "head":
                assert all("content" not in response for response in operation["responses"].values()), path
            if method == "get":
                assert "requestBody" not in operation, path
            routes.append((method.upper(), re.compile("^" + re.sub(r"\\\{[^{}]+\\\}", "[^/]+", re.escape(path)) + "$"), path, operation))
    assert (len(routes) - (1 if gpu_stream_recovery_additions else 0) - (1 if author_replacement_additions else 0) - (1 if preview_inspection_additions else 0) - (1 if author_metadata_additions else 0) - (5 if actual_version in ("1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0") else 0) - (7 if w8_additions else 0) - (2 if p3_additions else 0) - (8 if p4_additions else 0) - (2 if render_additions else 0), len(spec["paths"]) - (1 if author_replacement_additions else 0) - (1 if preview_inspection_additions else 0) - (1 if author_metadata_additions else 0) - (4 if actual_version in ("1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0") else 0) - (6 if w8_additions else 0) - (2 if p3_additions else 0) - (7 if p4_additions else 0) - (2 if render_additions else 0)) == ({"1.8.0": (65, 58), "1.9.0": (69, 62), "1.10.0": (69, 62), "1.11.0": (69, 62), "1.12.0": (70, 63), "1.13.0": (71, 64), "1.14.0": (71, 64), "1.15.0": (72, 65), "1.16.0": (73, 66), "1.17.0": (73, 66), "1.18.0": (73, 66), "1.19.0": (73, 66), "1.20.0": (73, 66), "1.21.0": (73, 66), "1.22.0": (75, 68), "1.23.0": (75, 68), "1.24.0": (77, 70), "1.24.1": (77, 70), "1.25.0": (89, 80), "1.26.0": (89, 80), "1.27.0": (89, 80), "1.28.0": (89, 80), "1.29.0": (89, 80), "1.30.0": (89, 80), "1.30.1": (89, 80), "1.30.2": (89, 80), "1.31.0": (90, 81), "1.32.0": (97, 87), "1.32.1": (97, 87), "1.33.0": (97, 87), "1.34.0": (97, 87), "1.35.0": (97, 87), "1.36.0": (101, 91), "1.38.0": (101, 91)}[legacy_version])
    if legacy_version in ("1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0", "1.38.0"):
        author_routes = {
            ("post", "/api/v1/drafts"): ("AuthorDraftCreateRequest", "AuthorDraftResponse", "201"),
            ("get", "/api/v1/drafts/{id}"): (None, "AuthorDraftResponse", "200"),
            ("get", "/api/v1/drafts/{id}/compilation"): (None, "AuthorCompilationResponse", "200"),
            ("post", "/api/v1/drafts/{id}/source-revision-jobs"): ("AuthorSourceRevisionRequest", "AuthorSourceRevisionResponse", "200"),
            ("post", "/api/v1/drafts/{id}/compilation-jobs"): ("AuthorCompilationRequest", "AuthorCompilationResponse", "200"),
            ("post", "/api/v1/drafts/{id}/adopt-source-revision"): ("AuthorAdoptSourceRevisionRequest", "AuthorAdoptSourceRevisionResponse", "200"),
            ("delete", "/api/v1/drafts/{id}"): ("AuthorDraftDeleteRequest", "AuthorDraftDeleteResponse", "200"),
        }
        for (method, path), (request, response, status) in author_routes.items():
            operation = spec["paths"][path][method]
            assert operation["responses"][status]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/" + response}
            if request is not None:
                body = operation["requestBody"]["content"]["application/json"]["schema"]
                assert body == {"$ref": "#/components/schemas/" + request}
                assert components["schemas"][request]["additionalProperties"] is False
                assert "metadata" in components["schemas"][request]["required"]
        assert components["schemas"]["AuthorSourceFileDtoInput"]["required"] == ["path", "text"]
        assert components["schemas"]["AuthorSourceRevisionRequest"]["properties"]["files"]["type"] == "array"
        assert components["schemas"]["AuthorSourceRevisionRequest"]["properties"]["files"]["minItems"] == 1
        assert components["schemas"]["AuthorSourceRevisionRequest"]["properties"]["files"]["maxItems"] == 256
        assert components["schemas"]["AuthorSourceFileDtoInput"]["properties"]["path"]["pattern"] == r"\S"
        assert components["schemas"]["AuthorDraftResponse"]["properties"]["draftId"]["pattern"] == r"^draft-[0-9a-f]{32}$"
        assert components["schemas"]["AuthorCompilationRequest"]["properties"]["expectedDraftRevision"]["pattern"] == r"^sha256:[0-9a-f]{64}$"
    if p4_additions:
        compilation_request = components["schemas"]["AuthorCompilationRequest"]
        compilation_fields = {"expectedDraftRevision", "metadata", "executionMode"}
        assert compilation_request["required"] == ["expectedDraftRevision", "metadata"]
        draft_response = components["schemas"]["AuthorDraftResponse"]
        if sdk_v2_author:
            compilation_fields.add("sourceRevision")
            assert compilation_request["properties"]["sourceRevision"] == {
                "anyOf": [{"type": "string", "pattern": r"^sha256:[0-9a-f]{64}$"}, {"type": "null"}]}
            assert set(draft_response["properties"]) == {
                "draftId", "draftRevision", "name", "packageId", "baseSourceRevision", "currentSourceRevision",
                "exportId", "adoptedSourceRevision", "canCompile", "canAdopt", "lastCompilation", "protocolVersion",
                "configuration", "sampleOptions", "packageVersion", "sdkVersion"}
            assert draft_response["required"] == [
                "draftId", "draftRevision", "name", "packageId", "baseSourceRevision", "currentSourceRevision",
                "exportId", "canCompile", "canAdopt", "protocolVersion"]
            # Ordinary responses omit CLR nulls; the additive fields remain
            # optional and carry concrete primitive values when present.
            for field, expected in (
                ("packageVersion", {"type": "integer", "format": "int32", "minimum": -2147483648, "maximum": 2147483647}),
                ("sdkVersion", {"type": "string"}),
            ):
                assert {key: value for key, value in draft_response["properties"][field].items()
                        if key != "description"} == expected
                assert field not in draft_response["required"]
        else:
            assert "sourceRevision" not in compilation_request["properties"]
            assert not {"packageVersion", "sdkVersion"}.intersection(draft_response["properties"])
        assert set(compilation_request["properties"]) == compilation_fields
    if legacy_version in ("1.22.0", "1.23.0", "1.24.0", "1.24.1", "1.25.0", "1.26.0", "1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        command_union = components["schemas"]["ProjectCommandDtoInput"]
        expected_command_count = 106 if lyrics_refactor_additions else 97 if lead_in_presentation_additions else 96
        assert len(command_union["oneOf"]) == len(command_union["discriminator"]["mapping"]) == ({"1.22.0": 62, "1.23.0": 64, "1.24.0": 78, "1.24.1": 78, "1.25.0": 84, "1.26.0": 84, "1.27.0": 91, "1.28.0": 91, "1.29.0": 91, "1.30.0": 91, "1.30.1": 91, "1.30.2": 91, "1.31.0": 91, "1.32.0": 91, "1.32.1": 91, "1.33.0": 91, "1.34.0": 91, "1.35.0": 91, "1.36.0": expected_command_count}[legacy_version])
        for suffix, required, schema, capability in (
            ("geometry", ["expectedRevision", "nodeId"], "ProjectGeometryReferenceResponse", "geometry-reference"),
            ("line-local-transform", ["expectedRevision", "branchId", "lineId"], "ProjectLineLocalTransformResponse", "line-local-transform"),
        ):
            operation = spec["paths"]["/api/v1/project-sessions/{id}/orchestration/" + suffix]["get"]
            query = [p for p in operation["parameters"] if p.get("in") == "query"]
            assert [p["name"] for p in query] == required
            assert all(p["required"] for p in query)
            assert operation["x-query-policy"] == dict(allowUnknown=False, allRequiredExactlyOnce=True,
                caseSensitiveNames=True, required=required)
            assert operation["x-capability"] == "project-sessions.orchestration." + capability + ".read"
            assert operation["responses"]["200"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/" + schema}
        for component, field in (("ProjectLineLocalTransformResponse", "transform"), ("LineLocalTransformCommandResultDto", "transform"),
                                 ("ProjectOrchestrationLayoutDto", "slotAnimation")):
            definition = components["schemas"][component]
            assert field in definition["required"] and {"type": "null"} in definition["properties"][field]["anyOf"]
        reference = components["schemas"]["ProjectGeometryReferenceResponse"]["properties"]
        assert reference["referenceKind"]["const"] == "fixedContainerReferenceV1" and reference["finalPixelGeometry"]["const"] is False
    if legacy_version in ("1.23.0", "1.24.0", "1.24.1", "1.25.0", "1.26.0", "1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        for operation, component, fields in (
            ("createNode", "CreateOrchestrationNodeCommandDtoInput", ["type", "kind", "parentId", "name", "slotCount"]),
            ("copySubtree", "CopyOrchestrationSubtreeCommandDtoInput", ["type", "nodeId", "name"]),
        ):
            definition = components["schemas"][component]
            assert list(definition["properties"]) == definition["required"] == fields
            assert definition["additionalProperties"] is False
            assert definition["properties"]["type"]["const"] == "orchestration." + operation
            assert definition["x-capability"] == "project-sessions.commands.orchestration." + operation
            assert components["schemas"]["ProjectCommandDtoInput"]["discriminator"]["mapping"]["orchestration." + operation] == "#/components/schemas/" + component
        creation = components["schemas"]["CreateOrchestrationNodeCommandDtoInput"]
        assert [branch["properties"]["kind"]["const"] for branch in creation["oneOf"]] == ["group", "auto", "slotSequence"]
        assert [branch["properties"]["slotCount"] for branch in creation["oneOf"]] == [
            {"type": "null"}, {"type": "null"}, {"type": "integer", "minimum": 1, "maximum": 64}]
        structure = components["schemas"]["OrchestrationStructureCommandResultDto"]
        assert "createdNodeIds" not in structure["required"]
        assert structure["properties"]["createdNodeIds"]["uniqueItems"] is True
        assert structure["properties"]["createdNodeIds"]["maxItems"] == 128
    if legacy_version in ("1.24.0", "1.24.1", "1.25.0", "1.26.0", "1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        schemas = components["schemas"]
        mapping = schemas["ProjectCommandDtoInput"]["discriminator"]["mapping"]
        assert len(mapping) == ((106 if lyrics_refactor_additions else 97 if lead_in_presentation_additions else 96) if legacy_version == "1.36.0" else 91 if legacy_version in ("1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0") else 84 if legacy_version in ("1.25.0", "1.26.0", "1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0") else 78)
        strict_completion_commands = (
            "setLayoutKind", "setLineEnterAnimation", "setProjectEnterAnimation", "setLeadInGeneration",
            "previewLeadInGeneration", "applyLeadInGeneration", "deleteLeadInAndExclude", "setLeadInAttachment",
            "addMark", "setMarkText", "reorderMarks", "spaceMarks", "setCountdownAppearance", "setMarkAppearance",
        ) + (("setCountdownPresentation",) if lead_in_presentation_additions else ())
        for command in strict_completion_commands:
            definition = schemas[mapping["orchestration." + command].split("/")[-1]]
            assert definition["additionalProperties"] is False
            assert list(definition["properties"]) == definition["required"]
        operation = spec["paths"]["/api/v1/project-sessions/{id}/orchestration/measurements"]["post"]
        assert operation["x-query-policy"] == {"allowUnknown": False, "allowed": []}
        assert operation["x-receipt-location"] == "none"
        assert operation["x-capability"] == "orchestration.measurements.scene-measurement-v1"
        assert operation["requestBody"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/SceneMeasurementRequest"}
        assert schemas["SceneMeasurementDataDto"]["properties"]["png"]["contentEncoding"] == "base64"
        for component, fields in (("ProjectOrchestrationResponse", ("projectEnterAnimation",)),
                                 ("ProjectOrchestrationProgramDto", ("leadInGeneration",)),
                                 ("ProjectLinePresentationResponse", ("localTransform", "localEnterAnimation", "effectiveEnterAnimation")),
                                 ("SceneMeasurementElementDto", ("worldMatrix", "pivot", "fixedReference", "editReference")),
                                 ("SceneMeasurementDataDto", ("timeUs",))):
            for field in fields:
                assert field in schemas[component]["required"]
                assert {"type": "null"} in schemas[component]["properties"][field]["anyOf"]
        node = schemas["PresetApplicationTargetDtoInput"]["oneOf"][-1]
        assert node["properties"]["kind"]["const"] == "node"
        assert node["required"] == ["kind", "nodeId", "includeDescendants"]
    if legacy_version in ("1.24.1", "1.25.0", "1.26.0", "1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        assert components["schemas"]["ExportJobRequest"]["properties"]["format"]["enum"] == ["opaqueMp4", "transparentProRes4444"]
        export_examples = {case["name"]: case for case in cases if case["schema"] == "ExportJobRequest"}
        for format_name, valid in (("opaqueMp4", True), ("transparentProRes4444", True), ("transparentMov", False)):
            case = export_examples["export-request-format-" + format_name]
            assert case["value"]["format"] == format_name and case["valid"] is valid
    if legacy_version in ("1.25.0", "1.26.0", "1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        schemas = components["schemas"]
        mapping = schemas["ProjectCommandDtoInput"]["discriminator"]["mapping"]
        for command, fields in (
            ("orchestration.setClipMembers", ["type", "branchId", "clipId", "lineIds"]),
            ("orchestration.splitClip", ["type", "branchId", "clipId", "splitBeforeLineId", "newClipId"]),
            ("orchestration.mergeClips", ["type", "branchId", "clipIds", "targetClipId"]),
            ("audio.replaceAsset", ["type", "assetId"]), ("audio.clear", ["type"]), ("project.rename", ["type", "name"])):
            schema = schemas[mapping[command].split("/")[-1]]
            assert list(schema["properties"]) == schema["required"] == fields
            assert schema["additionalProperties"] is False
            assert schema["x-capability"] == "project-sessions.commands." + command
        for path, method, request, response, status in (
            ("/api/v1/project-sessions/{id}/resolved-style", "get", None, "ProjectResolvedStyleResponse", "200"),
            ("/api/v1/project-sessions/{id}/resources", "get", None, "ProjectResourcesResponse", "200"),
            ("/api/v1/file-roots/{rootGrantId}/entries", "get", None, "ProjectDirectoryEntriesResponse", "200"),
            ("/api/v1/project-sessions/{id}/lyrics-export-plans", "post", "LyricsExportPlanRequest", "LyricsExportPlanResponse", "201"),
            ("/api/v1/project-sessions/{id}/lyrics-exports", "post", "LyricsExportRequest", "LyricsExportResponse", "201"),
            ("/api/v1/template-libraries/{id}/imports/preflight", "post", "TemplateImportPreflightRequest", "TemplateImportPreflightResponse", "201"),
            ("/api/v1/template-libraries/{id}/imports", "post", "TemplateImportRequest", "TemplateImportResponse", "200"),
            ("/api/v1/template-libraries/{id}/exports", "post", "TemplateArchiveExportRequest", "TemplateArchiveExportResponse", "201")):
            operation = spec["paths"][path][method]
            assert operation["x-capability"]
            assert operation["responses"][status]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/" + response}
            assert operation["x-query-policy"]["allowUnknown"] is False
            if request:
                assert operation["requestBody"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/" + request}
                assert schemas[request]["additionalProperties"] is False
                assert "metadata" in schemas[request]["required"]
        for path in ("/api/v1/lyrics-exports/{id}/content", "/api/v1/template-transfers/{id}/archive"):
            for method in ("get", "head"):
                operation = spec["paths"][path][method]
                assert all(status in operation["responses"] for status in ("200", "206", "304", "416"))
                assert operation["x-query-policy"] == {"allowUnknown": False, "allowed": []}
        context = schemas["ResolvedStyleContextDto"]
        for field in ("tokenId", "clipId"):
            assert field in context["required"] and {"type": "null"} in context["properties"][field]["anyOf"]
        resolved = schemas["ProjectResolvedStyleResponse"]["properties"]
        assert resolved["resolution"]["const"] == "staticPresetHierarchy" and resolved["finalPixelAppearance"]["const"] is False
        assert resolved["sources"]["minItems"] == resolved["sources"]["maxItems"] == (45 if ruby_style_additions else 43 if style_v2_additions else 26 if actual_version in ("1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0") else 21)
        assert schemas["ProjectResourceDto"]["properties"]["sizeBytes"]["x-int64-string"]
        for component, fields in (("TemplateImportRequest", ("expectedRevision",)),
                                  ("TemplateArchiveExportRequest", ("expectedRevision",)),
                                  ("TemplateImportResponse", ("previousRevision", "revision")),
                                  ("TemplateArchiveExportResponse", ("revision",))):
            for field in fields:
                assert schemas[component]["properties"][field]["pattern"] == "^(missing|[0-9a-f]{64})$"
                assert "x-int64-string" not in schemas[component]["properties"][field]
        # A rootGrantId parameter must match saved HTTP just like an id parameter.
        route = next(route for route in routes if route[2] == "/api/v1/file-roots/{rootGrantId}/entries")
        assert route[1].fullmatch("/api/v1/file-roots/root-example/entries")
    if legacy_version in ("1.27.0", "1.28.0", "1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
        assert ("0.102.0" if p3_additions else spec["x-product-version"]) == {"1.27.0": "0.96.0", "1.28.0": "0.97.0", "1.29.0": "0.98.0", "1.30.0": "0.98.0", "1.30.1": "0.98.1", "1.30.2": "0.98.1", "1.31.0": "0.98.1", "1.32.0": "0.98.1", "1.32.1": "0.98.1", "1.33.0": "0.99.0", "1.34.0": "0.100.0", "1.35.0": "0.101.0", "1.36.0": "0.102.0"}[legacy_version]
        if legacy_version not in ("1.29.0", "1.30.0", "1.30.1", "1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0"):
            assert len(components["schemas"]) == 449
            assert len(frozen_cases) == 2648 and sum(not case["valid"] for case in frozen_cases) == 2040
        else:
            from package_preview_fixtures import fixtures
            additions = fixtures()["cases"]
            assert previous_cases[-len(additions):] == additions
            assert hashlib.sha256(json.dumps(frozen_cases[:2648], sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest() == "0cc466900b362b2817fe66b1a989450ba5d888953f19d08c0219e013f45b7a6a"
            closure_additions = [case for case in previous_cases[2648:-len(additions)] if case["name"].startswith("basic-business-package-")]
            assert len(closure_additions) == (4 if legacy_version in ("1.30.2", "1.31.0", "1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0") else 0)
            frozen_count = 2648 + 24 + len(closure_additions) + len(additions) + len(profile_additions) + len(image_additions) + len(validation_image_additions) + len(export_image_additions)
            frozen_negative_count = (2040 + (11 if legacy_version in ("1.32.0", "1.32.1", "1.33.0", "1.34.0", "1.35.0", "1.36.0") else 9)
                                    + sum(not case["valid"] for case in closure_additions)
                                    + sum(not case["valid"] for case in additions)
                                    + sum(not case["valid"] for case in profile_additions)
                                    + sum(not case["valid"] for case in image_additions)
                                    + sum(not case["valid"] for case in validation_image_additions)
                                    + sum(not case["valid"] for case in export_image_additions))
            assert len(frozen_cases) == frozen_count
            assert len(cases) == frozen_count + len(extra_cases) + len(render_additions) + len(w8_additions) + len(gpu_additions) + len(glow_additions) + len(author_metadata_additions) + len(preview_inspection_additions) + len(author_replacement_additions) + len(external_cases)
            assert sum(not case["valid"] for case in frozen_cases) == frozen_negative_count
            assert sum(not case["valid"] for case in cases) == (frozen_negative_count
                + sum(not case["valid"] for case in extra_cases)
                + sum(not case["valid"] for case in render_additions)
                + sum(not case["valid"] for case in w8_additions)
                + sum(not case["valid"] for case in gpu_additions)
                + sum(not case["valid"] for case in glow_additions)
                + sum(not case["valid"] for case in author_metadata_additions)
                + sum(not case["valid"] for case in preview_inspection_additions)
                + sum(not case["valid"] for case in author_replacement_additions)
                + sum(not case["valid"] for case in external_cases))
            # SDK2 author additions are inline fields; keep the component-count
            # gate strict while metadata confirms the generated contract.
            expected_schema_count = 556 if p4_additions else (530 if p3_additions else {"1.30.2": 464, "1.31.0": 468, "1.32.0": 481, "1.32.1": 481, "1.33.0": 481, "1.34.0": 481, "1.35.0": 481, "1.36.0": 500}.get(legacy_version, 463))
            expected_schema_count += 4 if render_additions else 0
            if w8_additions:
                from w8_authoring_schema import NEW_SCHEMAS
                assert set(NEW_SCHEMAS).issubset(components["schemas"])
                expected_schema_count += len(NEW_SCHEMAS)
            expected_schema_count += 11 if gpu_additions else 0
            expected_schema_count += 12 if glow_additions else 0
            expected_schema_count += 1 if author_metadata_additions else 0
            expected_schema_count += 15 if preview_inspection_additions else 0
            expected_schema_count += 5 if author_replacement_additions else 0
            expected_schema_count += 43 if style_v2_additions else 0
            expected_schema_count += 6 if ruby_style_additions else 0
            expected_schema_count += 2 if fallback_additions else 0
            expected_schema_count += 4 if source_material_failure_additions else 0
            expected_schema_count += len(LEAD_IN_PRESENTATION_SCHEMAS) if lead_in_presentation_additions else 0
            expected_schema_count += len(TTML_IMPORT_SCHEMAS) if ttml_import_additions else 0
            expected_schema_count += 1 if gpu_stream_recovery_additions else 0
            expected_schema_count += len(LYRICS_REFACTOR_SCHEMAS) if lyrics_refactor_additions else 0
            assert len(components["schemas"]) == expected_schema_count
        schemas = components["schemas"]
        mapping = schemas["ProjectCommandDtoInput"]["discriminator"]["mapping"]
        assert len(mapping) == ((106 if lyrics_refactor_additions else 97 if lead_in_presentation_additions else 96) if legacy_version == "1.36.0" else 91)
        for command, fields in (
            ("setRubySegmentTime", ["type", "target", "startUs", "endUs"]),
            ("clearRubySegmentTime", ["type", "target"]),
            ("setRubySegmentText", ["type", "target", "text"]),
            ("addRubySegment", ["type", "target", "text", "afterSegmentId"]),
            ("removeRubySegment", ["type", "target"]),
            ("reorderRubySegments", ["type", "target", "orderedSegmentIds"]),
            ("setRubySegmentTimes", ["type", "changes"])):
            schema = schemas[mapping["lyrics." + command].split("/")[-1]]
            assert list(schema["properties"]) == schema["required"] == fields
            assert schema["additionalProperties"] is False
            assert schema["x-capability"] == "project-sessions.commands.lyrics." + command
        assert schemas["SetRubySegmentTimesCommandDtoInput"]["properties"]["changes"]["maxItems"] == 1000
        assert "segments" in schemas["ProjectRubyAnnotationDto"]["required"]
        assert schemas["LyricsImportOptionsDtoInput"]["properties"]["sourceBranchId"]["type"] == "string"
        for name in ("LyricsExportPlanResponse", "LyricsExportResponse"):
            assert "branchId" in schemas[name]["required"] and "scope" in schemas[name]["required"]
            assert {"type": "null"} in schemas[name]["properties"]["branchId"]["anyOf"]
        for name in ("writtenRubySegmentCount", "skippedRubySegmentCount", "droppedRubySegmentTimeCount"):
            assert name in schemas["LyricsExportReportDto"]["required"]
    if legacy_version == "1.36.0":
        schemas = components["schemas"]
        for path, method, status in (("/api/v1/lyrics-import-plans", "post", "200"),
                                     ("/api/v1/fonts", "get", "200"), ("/api/v1/fonts/inspect", "post", "200"),
                                     ("/api/v1/project-sessions/{id}/edit-previews", "post", "201")):
            operation = spec["paths"][path][method]
            assert status in operation["responses"]
            assert operation["x-receipt-location"] == ("metadata" if "edit-previews" in path else "none")
        font = schemas["FontInspectRequest"]
        assert font["additionalProperties"] is False
        assert set(font["required"]) == set(font["properties"])
        assert len(font["required"]) == 8
        for field in ("family", "fallbackFamilies", "sessionId", "expectedRevision"):
            assert {"type": "null"} in font["properties"][field]["anyOf"]
        assert schemas["LyricsImportPlanRequest"]["required"] == ["assetId"]
        assert "metadata" not in schemas["LyricsImportPlanRequest"]["properties"]
        for field in ("candidateRevision",):
            assert "x-int64-string" not in schemas["ProjectEditCandidateDto"]["properties"][field]
        assert "x-int64-string" not in schemas["PreviewSourceDto"]["properties"]["sourceRevision"]
        assert "x-int64-string" not in schemas["SceneMeasurementResponse"]["properties"]["projectRevision"]
        assert "pattern" not in schemas["SceneMeasurementResponse"]["properties"]["projectRevision"]
        assert "editResults" in schemas["ProjectCommandResponse"]["properties"]
        assert "editCandidate" in schemas["PreviewSessionResponse"]["properties"]
        for field in ("maxAssetBytes", "maxAssetQuotaBytes"):
            assert field in schemas["BackendCapabilitiesResponse"]["required"]
            shape = schemas["BackendCapabilitiesResponse"]["properties"][field]
            assert {"type": "null"} in shape["anyOf"]
            assert shape["anyOf"][0]["format"] == "int64"
        assert "audioAssetId" not in schemas["CreateProjectSessionRequest"]["required"]
        assert "lyricsAssetId" not in schemas["CreateProjectSessionRequest"]["required"]
        for component in ("TemplateDetailResponse", "TemplateTransferSummaryDto"):
            assert schemas[component]["properties"]["definitionAvailability"]["enum"] == (["staticAvailable", "advancedRuntimeAvailable", "advancedUnsupported"] + (["fullAvailable"] if p3_additions and component == "TemplateDetailResponse" else []))
        assert schemas["ExportJobRequest"]["properties"]["quality"]["enum"] == ["draft", "standard", "high"]
        assert "quality" not in schemas["ExportJobRequest"]["required"]
        for field in ("maxFrames", "maxDurationUs", "maxMediaBytes", "maxAudioBytes", "maxArtifactStorageBytes"):
            assert field not in schemas["ExportTaskCapabilitiesDto"]["required"]
    if p3_additions:
        assert spec["x-product-version"] in expected_product_versions(actual_version, args.spec), (
            "Frozen contract product must match Directory.Build.props; explicit offline specs may retain the reviewed historical product")
        assert (len(spec["paths"]), len(routes)) == (((115, 129) if gpu_stream_recovery_additions else (115, 128) if author_replacement_additions else (114, 127) if preview_inspection_additions else (113, 126)) if author_metadata_additions else (112, 125) if actual_version in ("1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0") else (108, 120) if w8_additions else (102, 113) if render_additions else (100, 111) if p4_additions else (93, 103))
        for path in ("/api/v1/effect-packages/inspect", "/api/v1/template-preview-options"):
            operation = spec["paths"][path]["post"]
            assert operation["x-receipt-location"] == "none"
            assert "200" in operation["responses"]
        assert len(components["schemas"]["ProjectCommandDtoInput"]["oneOf"]) == (106 if lyrics_refactor_additions else 97 if lead_in_presentation_additions else 96)
        assert components["schemas"]["EffectPackageParameterSchemaDto"]["properties"]["defaultValueJson"]["type"] == "string"
    if actual_version in ("1.38.1", "1.39.0", "1.40.0", "1.41.0", "1.42.0", "1.42.1", "1.42.2", "1.42.3", "1.42.4", "1.43.0", "1.44.0", "1.44.1", "1.44.2", "1.45.0", "1.46.0", "1.46.1", "1.47.0", "1.48.0", "1.49.0", "1.49.1", "1.49.2", "1.50.0", "1.51.0", "1.51.1", "1.52.0", "1.52.1", "1.53.0", "1.54.0", "1.55.0", "1.55.1", "1.56.0", "1.57.0", "1.58.0", "1.59.0"):
        for path, methods in spec["paths"].items():
            if path.startswith("/api/v1/drafts") or path.startswith("/api/v1/script-configurations") or path == "/api/v1/jobs/{id}/compilation-result":
                for operation in methods.values():
                    assert operation["x-behavior-section"] == "21", (path, operation["operationId"])
        compilation = spec["paths"]["/api/v1/drafts/{id}/compilation-jobs"]["post"]
        assert compilation["responses"]["200"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/AuthorCompilationResponse"}
        assert compilation["responses"]["202"]["content"]["application/json"]["schema"] == {"$ref": "#/components/schemas/JobResponse"}
        assert "GET/POST/PUT/DELETE/HEAD" in spec["x-options"]["description"]
    if render_additions:
        from render_backend_schema import OPTIONAL_REQUESTS, REQUIRED_ECHOES, HISTORICAL_ECHOES, backend
        schemas = components["schemas"]
        for name in OPTIONAL_REQUESTS:
            if name not in schemas:
                continue
            assert schemas[name]["properties"]["renderBackend"]["anyOf"] == backend(nullable=True)["anyOf"]
            assert "renderBackend" not in schemas[name]["required"]
        for name in REQUIRED_ECHOES:
            if name in schemas:
                assert schemas[name]["properties"]["renderBackend"] == backend()
                assert "renderBackend" in schemas[name]["required"]
        for name in HISTORICAL_ECHOES:
            for component in (name, name + "Raw"):
                if component in schemas:
                    assert schemas[component]["properties"]["renderBackend"]["anyOf"] == backend(nullable=True)["anyOf"]
                    assert "renderBackend" not in schemas[component]["required"]
        assert schemas["PreviewRenderBackendRequest"]["required"] == ["expectedPreviewGeneration", "renderBackend", "metadata"]
        for method, path in (("get", "/api/v1/render-backends"), ("put", "/api/v1/preview-sessions/{id}/render-backend")):
            operation = spec["paths"][path][method]
            assert operation["x-behavior-section"] == "25"
            assert operation.get("security") != []
            assert {"$ref": "#/components/parameters/HostInstance"} in operation["parameters"]
        assert spec["paths"]["/api/v1/preview-sessions/{id}/render-backend"]["put"]["x-receipt-location"] == "metadata"
    report = {"contractVersion": actual_version, "paths": len(spec["paths"]), "operations": len(routes), "schemas": len(components["schemas"]), "examples": len(cases if validation_cases is None else validation_cases), "negativeExamples": sum(not c["valid"] for c in (cases if validation_cases is None else validation_cases)), "http": []}
    for directory in args.http_log:
        checked, checked_requests, skipped, unpaired, covered = 0, 0, 0, 0, set()
        files = sorted(directory.glob("*.response.json"))
        assert files, f"No saved HTTP responses in {directory}"
        for file in files:
            request_file = file.with_name(file.name.replace(".response.json", ".request.json"))
            if not request_file.exists():
                unpaired += 1
                continue
            request = json.loads(request_file.read_text())
            response = json.loads(file.read_text())
            candidates = [r for r in routes if r[0] == request["method"] and r[1].match(urlsplit(request["path"]).path)]
            if not candidates or response.get("json") is None:
                skipped += 1
                continue
            # Fixed routes win over parameterized paths, just as endpoint routing does.
            _, _, path, operation = sorted(candidates, key=lambda r: "{" in r[2])[0]
            status = str(response["status"])
            if status.startswith("2") and request.get("json") is not None:
                request_schema = operation.get("requestBody", {}).get("content", {}).get("application/json", {}).get("schema")
                if request_schema is not None:
                    found = errors(request_schema, request["json"])
                    assert not found, f"HTTP request schema mismatch {request_file}: {found[0] if found else ''}"
                    checked_requests += 1
            definition = operation["responses"].get(status, operation["responses"].get("default"))
            mime = next((v for k, v in response["headers"].items() if k.lower() == "content-type"), "").split(";")[0]
            schema = definition.get("content", {}).get(mime, {}).get("schema")
            assert schema is not None, f"Unspecified JSON response {file}: {status} {mime}"
            if schema.get("format") == "binary":
                # A Range fragment may happen to parse as JSON; it is still a byte slice.
                skipped += 1
                continue
            found = errors(schema, response["json"])
            assert not found, f"HTTP schema mismatch {file}: {found[0] if found else ''}"
            checked += 1
            covered.add(request["method"] + " " + path)
        report["http"].append({"directory": str(directory), "checkedJsonResponses": checked, "checkedSuccessfulJsonRequests": checked_requests, "skippedNonJsonOrUnmapped": skipped, "skippedUnpairedArtifacts": unpaired, "coveredOperations": sorted(covered)})
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
