"""Reviewed P2-A public diagnostic unions; private Worker DTOs are not public schemas."""
import copy

TEXT = {"type": "string", "minLength": 1, "pattern": r"\S"}
REVISION = {"type": "string", "pattern": r"^sha256:[0-9a-fA-F]{64}$"}
ARTIFACT = {"type": "string", "pattern": r"^(?:sha256:)?[0-9a-fA-F]{64}$"}
LIMITS = dict(maxItemsPerKind=256, maxPackageItems=512, maxItems=768, maxJsonBytes=524288,
              maxPageItems=128, maxPages=32)


def absent(*names):
    return {"not": {"anyOf": [{"required": [name]} for name in names]}}


def extend_package_preview_schemas(schemas, metadata):
    shapes = {
        "BackendSourceDiagnosticDto": dict(packageId="String", revision="String", instanceId="String", exportId="String", code="String", message="String", isError="Boolean", path="String", line="Int32", column="Int32"),
        "BackendDiagnosticResourceDto": dict(packageId="String", revision="String", exportId="String"),
        "BackendImageEffectDiagnosticDto": dict(instanceId="String", artifactHash="String", graph="BackendDiagnosticResourceDto", inputId="String", code="String"),
        "BackendMaterialDiagnosticDto": dict(owner="BackendDiagnosticMaterialOwnerDto", material="BackendDiagnosticResourceDto", inputId="String", code="String"),
        "BackendDiagnosticMaterialOwnerDto": dict(kind="String", script="BackendDiagnosticScriptOwnerDto", source="BackendDiagnosticSourceOwnerDto"),
        "BackendDiagnosticScriptOwnerDto": dict(instanceId="String", artifactHash="String", packageIdentity="BackendDiagnosticResourceDto"),
        "BackendDiagnosticSourceOwnerDto": dict(path="String", region="String", slot="Int32", sourceMaterial="BackendDiagnosticResourceDto", parametersJson="String"),
    }
    for name, expected in shapes.items():
        if {p["name"]: p["type"] for p in metadata[name]["properties"]} != expected:
            raise ValueError("Unreviewed public package diagnostic: " + name)
        props = schemas[name]["properties"]
        for field in props:
            if field in ("packageId", "instanceId", "exportId", "inputId", "code", "region") or name == "BackendDiagnosticSourceOwnerDto" and field == "path":
                props[field].update(copy.deepcopy(TEXT))
            if field == "revision": props[field].update(copy.deepcopy(REVISION))
            if field == "artifactHash": props[field].update(copy.deepcopy(ARTIFACT))
    # Registered raw legacy reports may retain explicit null; new optional slots
    # must not force migration of existing saved payloads/examples.
    raw = schemas.get("BackendDiagnosticDtoRaw")
    if raw:
        raw["required"] = [field for field in raw["required"] if field not in ("source", "imageEffect", "material")]
    source = schemas["BackendSourceDiagnosticDto"]
    for field in ("line", "column"): source["properties"][field]["minimum"] = 0
    source["allOf"] = [{"if": {"required": ["instanceId"]}, "then": {"required": ["exportId"]}, "else": absent("exportId")}]
    owner = schemas["BackendDiagnosticMaterialOwnerDto"]
    owner["oneOf"] = [
        {"properties": {"kind": {"const": "script"}}, "required": ["script"], **absent("source")},
        {"properties": {"kind": {"const": "source"}}, "required": ["source"], **absent("script")},
    ]
    schemas["BackendDiagnosticSourceOwnerDto"]["properties"]["slot"].update(minimum=0, maximum=2)
    schemas["BackendDiagnosticSourceOwnerDto"]["properties"]["parametersJson"]["description"] = "JSON object encoded as string; object syntax/validity checked by service."
    diagnostic = schemas["BackendDiagnosticDto"]
    branches = []
    for origin, payload in (("compileSource", "source"), ("imageEffect", "imageEffect"), ("material", "material")):
        branch = {"properties": {"originKind": {"const": origin}, "severity": {"enum": ["error", "warning"] if origin == "compileSource" else ["warning"]}},
                  "required": ["originKind", payload], **absent("occurrence", "location", *[p for p in ("source", "imageEffect", "material") if p != payload])}
        if origin == "compileSource":
            branch["allOf"] = [{"if": {"properties": {"source": {"properties": {"isError": {"const": True}}, "required": ["isError"]}}},
                                "then": {"properties": {"severity": {"const": "error"}}}, "else": {"properties": {"severity": {"const": "warning"}}}}]
        branches.append(branch)
    # Legacy B2/runtime reports retain their previous response rules and values.
    branches.append({"allOf": [absent("source", "imageEffect", "material"),
        {"not": {"properties": {"originKind": {"enum": ["compileSource", "imageEffect", "material"]}}, "required": ["originKind"]}}]})
    diagnostic["oneOf"] = branches
    diagnostic["description"] = "Legacy report/runtime occurrence or exact compileSource/imageEffect/material payload; unrelated nullable slots omitted. Full public array max768/512KiB; byte/cross-field identity checked by service."
    schemas["PreviewDiagnosticsPageDto"]["properties"]["diagnostics"]["maxItems"] = 128
