"""Contract 1.51.0: deduplicated fallback diagnostics returned with exports and scene measurements.

An export or measurement whose image effects/materials need a background that is
missing (opaque output without usable media) or absent (transparent output) now
continues with the declared fallback, like preview. The worker reports each
distinct fallback once; the export report also counts affected frames and marks
truncation when the item or byte budget was reached.
"""
import copy

from static_style_schema import ref

MAXIMUM_ITEMS = 128
MAXIMUM_JSON_BYTES = 96 * 1024
ORIGINS = ["imageEffect", "material"]
NEW_SCHEMAS = ("BackendFallbackDiagnostic", "BackendFallbackDiagnosticRaw")
FIELDS = {
    "ExportReportDto": {"fallbackDiagnostics": ("BackendDiagnosticDto[]", None), "fallbackFrameCount": ("Int32", 0),
                        "fallbackDiagnosticsTruncated": ("Boolean", False)},
    "ExportJobResultDto": {"fallbackDiagnosticCount": ("Int32", 0)},
    "SceneMeasurementDataDto": {"fallbackDiagnostics": ("BackendDiagnosticDto[]", None)},
}
NULL = {"type": "null"}
REPORT_FIELDS = ("fallbackDiagnostics", "fallbackFrameCount", "fallbackDiagnosticsTruncated")


def audit(metadata):
    for name, fields in FIELDS.items():
        actual = {p["name"]: p for p in metadata[name]["properties"] if p["name"].startswith("fallback")}
        if set(actual) != set(fields):
            raise ValueError("Unreviewed fallback CLR fields: " + name)
        for field, (typ, default) in fields.items():
            expected = dict(type=typ, nullable=False, optional=True, defaultValue=default, omitNull=False)
            if any(actual[field][key] != value for key, value in expected.items()):
                raise ValueError(f"Unreviewed fallback CLR type/nullability/default: {name}.{field}")


def items(component):
    return {"type": "array", "maxItems": MAXIMUM_ITEMS, "uniqueItems": True, "items": ref(component),
            "description": f"去重后的兜底诊断，按首次出现排序；最多{MAXIMUM_ITEMS}条，省略null的公共JSON不超过{MAXIMUM_JSON_BYTES}字节。"}


def require(schema, *fields):
    for field in fields:
        if field not in schema["required"]:
            schema["required"].append(field)


def extend(schemas, metadata):
    audit(metadata)
    # Public measurement wire omits null: reuse the reviewed diagnostic union and narrow the origin.
    schemas["BackendFallbackDiagnostic"] = {
        "allOf": [ref("BackendDiagnosticDto"), {"properties": {"originKind": {"enum": copy.deepcopy(ORIGINS)},
                                                                "severity": {"const": "warning"}},
                                                 "required": ["originKind"]}],
        "description": "兜底诊断：图效果或材质缺背景时已按声明降级继续；来源只为imageEffect或material，级别为warning，不含实际发生与位置。"}
    # Registered raw reports keep explicit null for absent slots.
    branches = []
    for origin in ORIGINS:
        other = next(item for item in ORIGINS if item != origin)
        branches.append({"properties": {"originKind": {"const": origin}, origin: ref(
            "BackendImageEffectDiagnosticDtoRaw" if origin == "imageEffect" else "BackendMaterialDiagnosticDtoRaw"),
            other: copy.deepcopy(NULL), "source": copy.deepcopy(NULL)}, "required": [origin]})
    schemas["BackendFallbackDiagnosticRaw"] = {
        "allOf": [ref("BackendDiagnosticDtoRaw"), {"properties": {"severity": {"const": "warning"}, "location": copy.deepcopy(NULL),
                                                                   "occurrence": copy.deepcopy(NULL)},
                                                    "oneOf": branches}],
        "description": "登记导出报告里的兜底诊断原样形状：空槽位显式为null；来源只为imageEffect或material，级别为warning。"}
    report = schemas["ExportReportDtoRaw"]
    report["properties"]["fallbackDiagnostics"] = items("BackendFallbackDiagnosticRaw")
    report["properties"]["fallbackFrameCount"] = {"type": "integer", "format": "int32", "minimum": 0, "maximum": 2147483647,
        "description": "含至少一条兜底诊断的帧数；不超过expectedFrames。为0时fallbackDiagnostics为空且未截断；大于0时至少一条诊断或已截断。"}
    report["properties"]["fallbackDiagnosticsTruncated"] = {"type": "boolean",
        "description": "是否因条数、字节或完整报告预算丢掉了部分去重兜底诊断。兜底不会让导出失败。"}
    # Registered reports are served byte for byte: those from before 1.51.0 lack all
    # three fields (read as empty/0/false); new registrations always carry all three.
    report["required"] = [field for field in report["required"] if field not in REPORT_FIELDS]
    report["dependentRequired"] = {field: [other for other in REPORT_FIELDS if other != field] for field in REPORT_FIELDS}
    result = schemas["ExportJobResultDto"]
    result["properties"]["fallbackDiagnosticCount"] = {"type": "integer", "format": "int32", "minimum": 0, "maximum": MAXIMUM_ITEMS,
        "description": "报告中fallbackDiagnostics的条数；完整列表、兜底帧数与截断标记见报告。"}
    require(result, "fallbackDiagnosticCount")
    measurement = schemas["SceneMeasurementDataDto"]
    measurement["properties"]["fallbackDiagnostics"] = items("BackendFallbackDiagnostic")
    measurement["properties"]["fallbackDiagnostics"]["description"] = (
        f"本帧去重后的兜底诊断，最多{MAXIMUM_ITEMS}条；超过预算时测量以SceneMeasurementBudgetExceeded失败，不截断。")
    require(measurement, "fallbackDiagnostics")
