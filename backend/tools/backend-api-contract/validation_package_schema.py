"""Reviewed contract overlay for the B4-P1 package validation profile.

The public validation route is unchanged.  This module only constrains the
existing sourceProfile projections and documents the four profiles that a
validation deployment may advertise.  Actual Worker capability negotiation,
package codec acceptance, and diagnostics are runtime gates and are not proved
by this schema overlay.
"""

import copy


VALIDATION_SOURCE_PROFILES = [
    "static-lyrics-scene-v1",
    "static-lyrics-scene-analysis-v1",
    "static-lyrics-scene-presets-v1",
    "package-lyrics-scene-v1",
]


def extend_validation_package_schemas(schemas):
    """Apply the reviewed package-validation source profile constraints."""
    for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        if component not in schemas:
            raise ValueError(f"Missing validation job input schema: {component}")
        prop = schemas[component]["properties"].get("sourceProfile")
        if prop is None:
            raise ValueError(f"Missing sourceProfile in {component}")
        target = prop["anyOf"][0] if "anyOf" in prop else prop
        target["enum"] = copy.deepcopy(VALIDATION_SOURCE_PROFILES)
        target["description"] = (
            "验证任务固定输入来源；package-lyrics-scene-v1 仅在实际 Worker "
            "能力握手成功时可用。"
        )

    capabilities = schemas.get("ValidationTaskCapabilitiesDto")
    if capabilities is None:
        raise ValueError("Missing validation capability schema")
    singular = capabilities["properties"].get("sourceProfile")
    if singular is None:
        raise ValueError("Missing validation capability sourceProfile")
    singular["enum"] = copy.deepcopy(VALIDATION_SOURCE_PROFILES)
    singular["description"] = "兼容的首选验证来源；实际可用来源见 sourceProfiles。"
    profiles = capabilities["properties"].get("sourceProfiles")
    if profiles is None or profiles.get("type") != "array":
        raise ValueError("Missing validation capability sourceProfiles array")
    profiles["items"] = {"type": "string", "enum": copy.deepcopy(VALIDATION_SOURCE_PROFILES)}
    profiles["description"] = (
        "本次部署通过实际 Worker 能力握手后可接受的验证来源；"
        "configured-unverified 不得加入该数组。"
    )
    return schemas
