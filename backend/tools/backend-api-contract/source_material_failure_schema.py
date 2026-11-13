"""Contract 1.52.0: real source-material export failures without script occurrences."""
from package_preview_schema import absent
from static_style_schema import ref

ORIGIN = "sourceMaterialFailure"
MAXIMUM_JSON_BYTES = 64 * 1024
NEW_SCHEMAS = ("BackendSourceMaterialFailureDto", "BackendSourceMaterialFailureDtoRaw",
               "BackendImageSurfaceBudgetDto", "BackendImageSurfaceBudgetDtoRaw")


def audit(metadata, name, expected, *, optional=(), nullable=()):
    actual = {p["name"]: p for p in metadata[name]["properties"]}
    if set(actual) != set(expected):
        raise ValueError("Unreviewed source material failure CLR fields: " + name)
    for field, typ in expected.items():
        shape = dict(type=typ, nullable=field in nullable, optional=field in optional,
                     defaultValue=None, omitNull=False)
        if any(actual[field][key] != value for key, value in shape.items()):
            raise ValueError(f"Unreviewed source material failure CLR shape: {name}.{field}")


def number(*, positive=False):
    return {"type": "string", "pattern": r"^[1-9][0-9]*$" if positive else r"^(?:0|[1-9][0-9]*)$",
            "maxLength": 19, "x-int64-string": True,
            "description": "规范非负十进制Int64字符串；不接受符号、前导零或指数。精确数值范围由服务校验。"}


def extend(schemas, metadata):
    field = next(p for p in metadata["BackendDiagnosticDto"]["properties"] if p["name"] == ORIGIN)
    if any(field[key] != value for key, value in dict(type="BackendSourceMaterialFailureDto", nullable=True,
            optional=True, defaultValue=None, omitNull=False).items()):
        raise ValueError("Unreviewed optional sourceMaterialFailure diagnostic slot")
    audit(metadata, "BackendSourceMaterialFailureDto", dict(owner="BackendDiagnosticSourceOwnerDto", stage="String",
          timeUs="String", code="String", budget="BackendImageSurfaceBudgetDto"), optional=("budget",), nullable=("budget",))
    audit(metadata, "BackendImageSurfaceBudgetDto", dict(currentSurfaces="Int32", additionalSurfaces="Int32",
          maximumSurfaces="Int32", currentBytes="String", additionalBytes="String", maximumBytes="String"))
    for name in ("BackendSourceMaterialFailureDto", "BackendSourceMaterialFailureDtoRaw"):
        value = schemas[name]
        props = value["properties"]
        # The retained source identity is the existing complete owner shape, not a runtime instance.
        props["owner"] = ref("BackendDiagnosticSourceOwnerDto")
        for field in ("stage", "code"):
            props[field].update(minLength=1, pattern=r"\S")
        props["timeUs"] = number()
        budget = ref("BackendImageSurfaceBudgetDto")
        props["budget"] = {"anyOf": [budget, {"type": "null"}]} if name.endswith("Raw") else budget
        value["allOf"] = [{"if": {"properties": {"budget": {"type": "object"}}, "required": ["budget"]},
                           "then": {"properties": {"code": {"const": "ImageSurfaceBudgetExceeded"}}}}]
        value["description"] = (f"实际源材质失败的完整安全结构，最多{MAXIMUM_JSON_BYTES}字节；"
            "包含真实持久源部位、包、参数、阶段、微秒时刻和稳定码；没有脚本实例、编译代次或运行时发生序号。")
    for name in ("BackendImageSurfaceBudgetDto", "BackendImageSurfaceBudgetDtoRaw"):
        props = schemas[name]["properties"]
        for field in ("currentSurfaces", "additionalSurfaces", "maximumSurfaces"):
            props[field]["minimum"] = 1 if field == "maximumSurfaces" else 0
        for field in ("currentBytes", "additionalBytes", "maximumBytes"):
            props[field] = number(positive=field == "maximumBytes")
        schemas[name]["description"] = "失败申请之前的实际活动量、申请量与固定上限；仅ImageSurfaceBudgetExceeded附带，不含异常文本或堆栈。"

    diagnostic = schemas["BackendDiagnosticDto"]
    diagnostic["oneOf"][-1]["allOf"][1]["not"]["properties"]["originKind"]["enum"].append(ORIGIN)
    for branch in diagnostic["oneOf"]:
        branch.setdefault("allOf", []).append(absent(ORIGIN))
    diagnostic["oneOf"].append({"properties": {"originKind": {"const": ORIGIN}, "severity": {"const": "error"}},
        "required": ["originKind", ORIGIN], **absent("location", "occurrence", "source", "imageEffect", "material")})
    diagnostic["description"] += " sourceMaterialFailure是独立源材质执行错误，不能附加脚本occurrence或其他来源payload。"

    raw = schemas["BackendDiagnosticDtoRaw"]
    raw["required"] = [field for field in raw["required"] if field != ORIGIN]
    raw.setdefault("allOf", []).append({
        "if": {"properties": {"originKind": {"const": ORIGIN}}, "required": ["originKind"]},
        "then": {"properties": {ORIGIN: ref("BackendSourceMaterialFailureDtoRaw"), "severity": {"const": "error"},
            **{field: {"type": "null"} for field in ("location", "occurrence", "source", "imageEffect", "material")}},
            "required": [ORIGIN]},
        "else": {"properties": {ORIGIN: {"type": "null"}}}})


def configure(operation, item):
    if item["operationId"] not in ("exportProject", "exportResult", "recoveredExportResult", "exportDiagnostics", "jobState"):
        return
    operation["x-source-material-failure"] = {"originKind": ORIGIN, "maximumJsonBytes": MAXIMUM_JSON_BYTES,
        "runtimeOccurrence": False, "budgetCode": "ImageSurfaceBudgetExceeded"}
    operation["description"] += " 实际源材质失败停止导出并登记完整安全诊断；来源为sourceMaterialFailure，不制造脚本发生身份；表面超限附实际预算数值。"
