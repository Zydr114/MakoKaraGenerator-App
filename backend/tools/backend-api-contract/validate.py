#!/usr/bin/env python3
"""Validate the current frozen OpenAPI spec, schema refs, examples, manifest hashes, and optional HTTP logs."""
import argparse
import hashlib
import json
import re
from pathlib import Path
from urllib.parse import urlsplit

from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate_spec

ROOT = Path(__file__).resolve().parents[2]
REQUIRED_MANIFEST_ARTIFACTS = {
    "web/openapi.json",
    "web/examples.json",
    "web/api-index.md",
    "web/api-contract.md",
    "web/frontend-workflows.md",
}
DECOMMISSIONED_PATH_PREFIXES = ("/api/v1/recovery-jobs",)
DECOMMISSIONED_PATHS = {
    "/api/v1/project-sessions/{id}/edit-previews",
    "/api/v1/project-sessions/{id}/resources/package-asset",
}
RETIRED_PROFILE_PREFIX = "project-image-"


def read_product_version() -> str:
    match = re.search(
        r"<VersionPrefix>([^<]+)</VersionPrefix>",
        (ROOT / "Directory.Build.props").read_text(encoding="utf-8"),
    )
    assert match is not None, "Missing <VersionPrefix> in backend/Directory.Build.props"
    return match.group(1)


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_refs(node, spec):
    if isinstance(node, dict):
        if "$ref" in node:
            pointer = node["$ref"]
            assert isinstance(pointer, str) and pointer.startswith("#/"), f"Unexpected external reference: {pointer}"
            target = spec
            for part in pointer[2:].split("/"):
                key = part.replace("~1", "/").replace("~0", "~")
                assert isinstance(target, dict) and key in target, f"Broken $ref: {pointer}"
                target = target[key]
        for value in node.values():
            check_refs(value, spec)
    elif isinstance(node, list):
        for value in node:
            check_refs(value, spec)


def check_no_retired_project_image_extensions(spec):
    def walk(node, path="$"):
        if isinstance(node, dict):
            for key, value in node.items():
                assert not key.startswith("x-project-image-"), f"Retired extension present at {path}.{key}"
                walk(value, f"{path}.{key}")
        elif isinstance(node, list):
            for idx, item in enumerate(node):
                walk(item, f"{path}[{idx}]")
        elif isinstance(node, str):
            if path.endswith(".enum") or ".enum[" in path:
                assert not node.startswith(RETIRED_PROFILE_PREFIX), f"Retired profile enum at {path}: {node}"

    walk(spec)


def validate_manifest(manifest_path: Path, actual_version: str, product_version: str):
    assert manifest_path.is_file(), f"Missing contract manifest: {manifest_path}"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert manifest.get("contractVersion") == actual_version, (
        f"Manifest contractVersion {manifest.get('contractVersion')!r} != OpenAPI {actual_version!r}"
    )
    assert manifest.get("productVersion") == product_version, (
        f"Manifest productVersion {manifest.get('productVersion')!r} != Directory.Build.props {product_version!r}"
    )
    artifacts = manifest.get("artifacts")
    assert isinstance(artifacts, dict) and artifacts, "Manifest artifacts must be a non-empty object"
    missing_required = REQUIRED_MANIFEST_ARTIFACTS - set(artifacts)
    assert not missing_required, f"Manifest missing required artifacts: {sorted(missing_required)}"
    for artifact_rel, expected_sha in artifacts.items():
        artifact_path = ROOT / artifact_rel
        assert artifact_path.is_file(), f"Missing frozen artifact listed in manifest: {artifact_rel}"
        actual_sha = sha256_file(artifact_path)
        assert actual_sha == expected_sha, (
            f"SHA-256 mismatch for {artifact_rel}: expected {expected_sha}, got {actual_sha}"
        )
    return len(artifacts)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--http-log", type=Path, action="append", default=[])
    parser.add_argument("--report", type=Path)
    parser.add_argument("--spec", type=Path, default=ROOT / "web/openapi.json")
    parser.add_argument("--examples", type=Path, default=ROOT / "web/examples.json")
    parser.add_argument("--manifest", type=Path, default=None, help="Defaults to web/contract-manifest.json for the canonical spec")
    parser.add_argument("--extra-examples", type=Path, action="append", default=[], help="Additional fixture JSON files")
    args = parser.parse_args()

    spec = json.loads(args.spec.read_text(encoding="utf-8"))
    actual_version = spec["info"]["version"]
    product_version = read_product_version()
    is_canonical_spec = args.spec.resolve() == (ROOT / "web/openapi.json").resolve()
    if is_canonical_spec:
        assert spec.get("x-product-version") == product_version, (
            f"OpenAPI x-product-version {spec.get('x-product-version')!r} != Directory.Build.props {product_version!r}"
        )

    # (a) OpenAPI 3.1 structural validation
    validate_spec(spec)
    components = spec["components"]

    # (b) Complete internal $ref integrity
    check_refs(spec, spec)

    # (c) Draft 2020-12 schema validity for every component schema
    for name, schema in components["schemas"].items():
        Draft202012Validator.check_schema(schema)
        assert isinstance(name, str) and name

    # Current-contract route & surface invariants
    check_no_retired_project_image_extensions(spec)
    routes = []
    seen_operation_ids = set()
    for path, methods in spec["paths"].items():
        assert path.startswith("/api/v1/"), f"Unversioned alias route must not exist: {path}"
        assert path not in DECOMMISSIONED_PATHS, f"Decommissioned route must not exist: {path}"
        assert not any(path.startswith(prefix) for prefix in DECOMMISSIONED_PATH_PREFIXES), (
            f"Decommissioned route prefix must not exist: {path}"
        )
        for method, operation in methods.items():
            op_id = operation["operationId"]
            assert op_id not in seen_operation_ids, f"Duplicate operationId: {op_id}"
            seen_operation_ids.add(op_id)
            if method == "head":
                assert all("content" not in response for response in operation["responses"].values()), path
            if method == "get":
                assert "requestBody" not in operation, path
            routes.append((
                method.upper(),
                re.compile("^" + re.sub(r"\\\{[^{}]+\\\}", "[^/]+", re.escape(path)) + "$"),
                path,
                operation,
            ))

    command_union = components["schemas"]["ProjectCommandDtoInput"]
    assert len(command_union["oneOf"]) == len(command_union["discriminator"]["mapping"]) > 0

    def errors(schema, instance):
        root_schema = {"allOf": [schema], "components": components}
        return list(Draft202012Validator(root_schema, format_checker=FormatChecker()).iter_errors(instance))

    # (d) Current examples corpus validation (positive == 0 errors, negative >= 1 error)
    example_document = json.loads(args.examples.read_text(encoding="utf-8"))
    assert example_document.get("contractVersion") == actual_version, (
        f"Example corpus contractVersion {example_document.get('contractVersion')!r} != OpenAPI {actual_version!r}"
    )
    cases = list(example_document["cases"])
    for extra in args.extra_examples:
        cases.extend(json.loads(extra.read_text(encoding="utf-8"))["cases"])
    assert cases, "Example corpus must not be empty"
    seen_case_names = set()
    for case in cases:
        name = case["name"]
        assert name not in seen_case_names, f"Duplicate example name: {name}"
        seen_case_names.add(name)
        schema_name = case["schema"]
        assert schema_name in components["schemas"], f"Example {name} references missing schema {schema_name}"
        found = errors({"$ref": "#/components/schemas/" + schema_name}, case["value"])
        assert (not found) == case["valid"], f"Example {name}: expected valid={case['valid']}; {found[:1]}"

    # (e) Frozen manifest version & artifact SHA-256 validation
    manifest_path = args.manifest if args.manifest is not None else (ROOT / "web/contract-manifest.json" if is_canonical_spec else None)
    verified_artifacts = 0
    if manifest_path is not None:
        verified_artifacts = validate_manifest(manifest_path, actual_version, product_version)

    report = {
        "contractVersion": actual_version,
        "productVersion": spec.get("x-product-version"),
        "paths": len(spec["paths"]),
        "operations": len(routes),
        "schemas": len(components["schemas"]),
        "examples": len(cases),
        "negativeExamples": sum(not c["valid"] for c in cases),
        "manifestArtifactsVerified": verified_artifacts,
        "http": [],
    }

    # (f) Optional offline HTTP log validation
    for directory in args.http_log:
        checked, checked_requests, skipped, unpaired, covered = 0, 0, 0, 0, set()
        files = sorted(directory.glob("*.response.json"))
        assert files, f"No saved HTTP responses in {directory}"
        for file in files:
            request_file = file.with_name(file.name.replace(".response.json", ".request.json"))
            if not request_file.exists():
                unpaired += 1
                continue
            request = json.loads(request_file.read_text(encoding="utf-8"))
            response = json.loads(file.read_text(encoding="utf-8"))
            candidates = [r for r in routes if r[0] == request["method"] and r[1].match(urlsplit(request["path"]).path)]
            if not candidates or response.get("json") is None:
                skipped += 1
                continue
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
                skipped += 1
                continue
            found = errors(schema, response["json"])
            assert not found, f"HTTP schema mismatch {file}: {found[0] if found else ''}"
            checked += 1
            covered.add(request["method"] + " " + path)
        report["http"].append({
            "directory": str(directory),
            "checkedJsonResponses": checked,
            "checkedSuccessfulJsonRequests": checked_requests,
            "skippedNonJsonOrUnmapped": skipped,
            "skippedUnpairedArtifacts": unpaired,
            "coveredOperations": sorted(covered),
        })

    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
