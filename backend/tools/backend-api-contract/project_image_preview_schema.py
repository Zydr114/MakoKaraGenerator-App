"""Reviewed saved-project image preview boundary; no image bytes enter public DTOs."""
import copy

PROFILE = "project-image-preview-v1"
SOURCE_PROFILES = ["static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1",
                   "static-lyrics-scene-presets-v1", "package-lyrics-scene-v1", PROFILE]
LIMITS = dict(maxImageBytes=2097152, maxImageDimension=4096, maxDecodedImageBytes=16777216,
              maxSnapshotBytes=4194280, maxBaseSnapshotBytes=4194304, maxSourceValues=200000)


def extend_project_image_preview_schemas(schemas, metadata):
    runtime = schemas["PreviewRuntimeInfoResponse"]
    expected = {"sourceProfile": "String", "sourceProfiles": "String[]"}
    actual = {p["name"]: p["type"] for p in metadata["PreviewRuntimeInfoResponse"]["properties"]}
    if any(actual.get(name) != kind for name, kind in expected.items()):
        raise ValueError("Unreviewed preview source capability fields")
    runtime["properties"]["sourceProfile"].update(
        const="static-lyrics-scene-v1",
        description="兼容单值，保持static-lyrics-scene-v1；当前输入能力读取sourceProfiles，不由此值推断图片支持。")
    runtime["properties"]["sourceProfiles"].update(
        items={"type": "string", "enum": SOURCE_PROFILES}, uniqueItems=True,
        description="原双静态baseline，加实际Worker独立探测成功的静态预设、直接包和project-image-preview-v1。图片探测失败仅省略图片profile；创建/open仍复核实际Worker。字段省略保留旧响应兼容。")
    runtime["description"] = "Worker配置及启动时各独立来源探测的当前事实；enabled/status并非图片能力证明。GET不重新握手，创建及实际open再次核对能力。"
    for name in ("PreviewSessionRequest", "PreviewProjectInputRequest"):
        schemas[name]["description"] = "固定工程ID和精确修订；Host根据完整源自动选择预览profile。图片仅从ProjectStore已加载的受管闭包固定，客户端不能提交profile、图片bytes、路径或grant。"
    schemas["PreviewSessionResponse"]["description"] = "沿既有preview/generation/sourceRevision身份规则；不新增图片profile或原始图片/主机路径字段。packageClosure只表示包内闭包摘要。"
    schemas["PreviewSessionStateResponse"]["properties"]["renderFailureCode"]["description"] = (
        "异步失败稳定码。图片完整解码失败为PackageImageDecodeFailed，尺寸/解码预算为PackageImageBudgetExceeded；"
        "复用WorkerPackageTimedOut/WorkerPackageCompilationFailed/WorkerRuntimeDiagnosticError等既有会话码。失败无latestFrame。")


def configure_project_image_preview_operation(operation, item):
    if item["operationId"] not in ("runtimeInfo", "createPreview", "updatePreviewInput", "previewState", "previewDiagnostics"):
        return
    operation["x-project-image-preview-profile"] = PROFILE
    operation["x-project-image-preview-limits"] = copy.deepcopy(LIMITS)
    operation["description"] += (
        " 0.99已保存工程Image背景有限预览见行为§22；该历史HTTP范围只接project预览。"
        "当前图片正式验证须独立project-image-validation-v1能力，见行为§10；预览能力不能代替。"
        "export/measurement与模板候选继续拒绝图片。")
    if item["operationId"] in ("createPreview", "updatePreviewInput"):
        operation["x-project-image-preview-failures"] = [
            dict(status=503, code="previewUnavailable", field="worker", failureCode="WorkerImagePreviewProfileMismatch"),
            dict(status=503, code="previewUnavailable", field="worker", failureCode="WorkerImagePreviewUnavailable"),
            dict(status=422, code="commandNotSupported", field="background", failureCode="ProjectImageSourceUnavailable"),
            dict(status=422, code="commandNotSupported", field="background", failureCode="ProjectImageSourceChanged"),
            dict(status=413, code="resourceTooLarge", field="background", failureCode="SnapshotSizeExceeded"),
        ]
