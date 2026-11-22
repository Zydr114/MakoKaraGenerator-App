"""Contract 1.53.0+ example migration and negative fixtures for SDK v1 removal."""
import copy

PREFIX = "sdk-v1-removal-"
LEGACY_V1_CASE_NAME = "author-metadata-inspection-response-v1"


def migrate_cases(cases):
    """Mark the historical v1 inspection fixture invalid under narrowed SDK v2 enums."""
    migrated = []
    matched = 0
    for case in cases:
        updated = copy.deepcopy(case)
        if updated["name"] == LEGACY_V1_CASE_NAME:
            updated["valid"] = False
            matched += 1
        migrated.append(updated)
    assert matched == 1, f"Expected 1 {LEGACY_V1_CASE_NAME} case, found {matched}"
    return migrated


def fixtures():
    """Tail negative examples verifying packageVersion=2 and v1 shaderProfile rejection."""
    from author_metadata_fixtures import fixtures as author_metadata_fixtures
    base = copy.deepcopy(next(
        case["value"] for case in author_metadata_fixtures()
        if case["name"] == "author-metadata-inspection-response-exact-integer"
    ))
    return [
        {
            "name": PREFIX + "inspection-response-package-version-2",
            "schema": "AuthorSourceInspectionResponse",
            "valid": False,
            "value": {**base, "packageVersion": 2},
        },
        {
            "name": PREFIX + "inspection-response-raster-shader-profile",
            "schema": "AuthorSourceInspectionResponse",
            "valid": False,
            "value": {**base, "shaderProfile": "skia-raster-3.119.4-v1"},
        },
    ]
