"""Reviewed explicit CPU/GPU selection; never infer a historical backend."""
import copy

from static_style_schema import audit, ref

VALUES = ["cpu", "gpu"]
OPTIONAL_REQUESTS = (
    "PreviewSessionRequest", "TemplatePreviewSessionRequest", "PresetApplicationPreviewRequest",
    "ProjectEditPreviewRequest", "PreviewProjectInputRequest", "PreviewInputRequest",
    "ValidationJobRequest", "AuthorValidationJobRequest", "ExportJobRequest", "SceneMeasurementRequest",
)
REQUIRED_ECHOES = ("PreviewSessionResponse", "PreviewFrameMetadataDto", "PreviewEventDto")
HISTORICAL_ECHOES = (
    "BackendJobInputDto", "BackendExportInputDto", "AuthorValidationJobInputDto",
    "ValidationReportDto", "ExportReportDto", "SceneMeasurementDataDto",
)
SELECTION_OPERATIONS = {
    "createPreview", "createTemplatePreview", "createPresetApplicationPreview", "createEditPreview",
    "updatePreviewInput", "updatePreviewRenderBackend", "validateProject", "validateAuthorDraft",
    "exportProject", "measureOrchestration",
}


def backend(nullable=False):
    value = {"type": "string", "enum": VALUES.copy()}
    return {"anyOf": [value, {"type": "null"}]} if nullable else value


def field(metadata, name, *, nullable, optional, default=None):
    properties = [p for p in metadata[name]["properties"] if p["name"] == "renderBackend"]
    if len(properties) != 1:
        raise ValueError("Missing reviewed renderBackend CLR field: " + name)
    actual = properties[0]
    expected = dict(type="String", nullable=nullable, optional=optional, defaultValue=default)
    if any(actual[key] != value for key, value in expected.items()):
        raise ValueError("Unreviewed renderBackend CLR type/nullability/default: " + name)
    return actual


def extend(schemas, metadata):
    for name in OPTIONAL_REQUESTS:
        field(metadata, name, nullable=True, optional=True)
        if name not in schemas:  # PreviewInputRequest is internal, not a new HTTP route.
            continue
        value = backend(nullable=True)
        value["description"] = (
            "严格cpu/gpu；新候选省略/null使用启动默认，指定previewId替换现有候选时保留当前选择。" if name == "ProjectEditPreviewRequest" else
            "严格cpu/gpu；省略/null保留该预览当前选择。" if name in ("PreviewProjectInputRequest", "PreviewInputRequest") else
            "严格cpu/gpu；省略/null使用启动时固定默认。候选测量省略/null使用候选固定选择。"
            if name == "SceneMeasurementRequest" else
            "严格cpu/gpu；省略/null使用启动时固定默认。GPU失败明确失败，不回退CPU。")
        schemas[name]["properties"]["renderBackend"] = value
        schemas[name]["required"] = [p for p in schemas[name].get("required", []) if p != "renderBackend"]
    for name in REQUIRED_ECHOES:
        field(metadata, name, nullable=False, optional=True, default="cpu")
        if name in schemas:
            schemas[name]["properties"]["renderBackend"] = backend()
            if "renderBackend" not in schemas[name]["required"]:
                schemas[name]["required"].append("renderBackend")
    for name in HISTORICAL_ECHOES:
        actual = field(metadata, name, nullable=True, optional=True)
        if not actual["omitNull"]:
            raise ValueError("Historical renderBackend must omit unknown/null: " + name)
        for component in (name, name + "Raw"):
            if component not in schemas:
                continue
            value = backend(nullable=True)
            value["description"] = "固定执行选择；历史记录缺失/null为未知，禁止推定CPU或GPU；读取旧记录不重跑。"
            schemas[component]["properties"]["renderBackend"] = value
            schemas[component]["required"] = [p for p in schemas[component].get("required", []) if p != "renderBackend"]

    audit(metadata, "PreviewRenderBackendRequest", dict(expectedPreviewGeneration="String", renderBackend="String",
        metadata="RequestMetadataDto"), nullable=("metadata",), optional=("metadata",))
    request = schemas["PreviewRenderBackendRequest"]
    request["properties"]["renderBackend"] = backend()
    request["properties"]["expectedPreviewGeneration"] = {
        "type": "string", "pattern": r"^(?:0|[1-9][0-9]*)$", "x-int64-string": True,
        "description": "精确canonical非负Int64预览代次；执行时复核CAS与对象身份。"}
    request["additionalProperties"] = False
    request["required"] = ["expectedPreviewGeneration", "renderBackend", "metadata"]
    request["properties"]["metadata"] = ref("RequestMetadataDtoInput")
    request["description"] = "全部预览类型的固定输入设备切换；相同后端CAS成功为no-op，失败不换代、不丢旧帧。"

    audit(metadata, "RenderBackendsResponse", dict(defaultBackend="String", backends="RenderBackendAvailabilityDto[]"))
    audit(metadata, "RenderBackendAvailabilityDto", dict(renderBackend="String", available="Boolean", api="String",
        device="RenderBackendDeviceInfoDto", unavailableReason="BackendErrorResponse"),
        nullable=("device", "unavailableReason"), optional=("device", "unavailableReason"))
    audit(metadata, "RenderBackendDeviceInfoDto", dict(vendor="String", device="String", driver="String", apiVersion="String"),
        nullable=("vendor", "device", "driver", "apiVersion"))
    response = schemas["RenderBackendsResponse"]
    response["properties"]["defaultBackend"] = backend()
    response["properties"]["backends"].update(minItems=2, maxItems=2,
        allOf=[{"contains": {"properties": {"renderBackend": {"const": selected}}, "required": ["renderBackend"]},
                "minContains": 1, "maxContains": 1} for selected in VALUES])
    response["description"] = "当前部署真实隔离Worker设备检查快照；检查执行设备创建/绘制/提交/读回，不运行用户脚本。"
    availability = schemas["RenderBackendAvailabilityDto"]
    availability["properties"]["renderBackend"] = backend()
    availability.setdefault("allOf", []).extend([
        {"if": {"properties": {"renderBackend": {"const": "gpu"}}, "required": ["renderBackend"]},
         "then": {"properties": {"api": {"const": "vulkan"}}},
         "else": {"properties": {"api": {"const": "raster"}}}},
        {"if": {"properties": {"available": {"const": False}}, "required": ["available"]},
         "then": {"required": ["unavailableReason"], "not": {"required": ["device"]}},
         "else": {"required": ["device"], "not": {"required": ["unavailableReason"]}}},
    ])
    device = schemas["RenderBackendDeviceInfoDto"]
    device["properties"]["device"].update(minLength=1, pattern=r"\S")
    device["required"] = ["device"]


def configure(operation, item):
    name = item["operationId"]
    if name not in SELECTION_OPERATIONS | {"renderBackends"}:
        return
    operation["x-render-backend-values"] = VALUES.copy()
    operation["description"] += " 显式设备选择见统一API§25；公共gpu实际使用Vulkan；不修改Host环境或其它客户端偏好。"
    if name == "renderBackends":
        operation["x-receipt-location"] = "none"
    elif name == "updatePreviewRenderBackend":
        operation["x-render-backend-switch"] = dict(fixedInput=True, exactGeneration=True,
            sameBackendNoOp=True, failureAtomic=True, oldWorkerDrain=True)
    elif name == "updatePreviewInput":
        operation["x-render-backend-default"] = "current-preview"
    elif name == "measureOrchestration":
        operation["x-render-backend-default"] = "candidate-or-startup"
    elif name == "createEditPreview":
        operation["x-render-backend-default"] = "existing-preview-or-startup"
    else:
        operation["x-render-backend-default"] = "startup"


def migrate_legacy_cases(cases, *, historical=False):
    """Add only new required live-preview echoes; reverse before old hash gates."""
    result = copy.deepcopy(cases)
    def visit(value):
        if isinstance(value, dict):
            if historical:
                value.pop("renderBackend", None)
            elif ({"previewId", "previewGeneration", "source", "limits"} <= value.keys()
                  or {"frameId", "previewId", "previewGeneration", "content"} <= value.keys()):
                value.setdefault("renderBackend", "cpu")
            for child in value.values():
                visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)
    for case in result:
        if not historical and case["schema"] in REQUIRED_ECHOES:
            case["value"].setdefault("renderBackend", "cpu")
        visit(case["value"])
    return result
