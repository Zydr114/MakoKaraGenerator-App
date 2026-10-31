"""Saved-project Image export contract overlay.

The public export route keeps the existing request DTO.  Image source identity
is carried only in the registered job input/exportInput projection; the
operation extension documents the independent Worker capability and budgets.
"""
import copy

PROFILE = "project-image-export-v1"
BASE_PROFILES = ["static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1", "package-lyrics-scene-v1"]
LIMITS = dict(
    maxInputBytes=4194272,
    maxMetadataBytes=32768,
    maxImageBytes=2097152,
    maxImageDimension=4096,
    maxDecodedImageBytes=16777216,
    maxBaseSnapshotBytes=4194304,
    maxSourceValues=200000,
    maxMediaBytes=67108864,
    maxAudioBytes=16777216,
    maxReportBytes=262144,
)


def _plain(schema):
    return schema["anyOf"][0] if "anyOf" in schema else schema


def extend_project_image_export_schemas(schemas, metadata):
    expected_capability = {
        "imageTaskProfile": "String",
        "imageBaseProfiles": "String[]",
    }
    actual = {p["name"]: p["type"] for p in metadata["ExportTaskCapabilitiesDto"]["properties"]}
    if any(actual.get(name) != kind for name, kind in expected_capability.items()):
        raise ValueError("Unreviewed image export capability fields")
    capability = schemas["ExportTaskCapabilitiesDto"]
    capability["properties"]["imageTaskProfile"].update(
        enum=[PROFILE], description="独立已保存Image导出任务profile；不改变sourceProfile兼容首选值。")
    capability["properties"]["imageBaseProfiles"].update(
        items={"type": "string", "enum": BASE_PROFILES}, uniqueItems=True,
        description="Image导出可用的基础snapshot来源；只公布实际Worker协商成功的三种基础profile。")
    # Nullable response fields are omitted when absent.  If the image capability
    # is unavailable both fields remain omitted; enabled=false still carries the
    # ordinary disabledReason.
    for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        source = _plain(schemas[component]["properties"]["sourceProfile"])
        source.setdefault("enum", []).append(PROFILE)
        source["description"] = "导出Image任务为project-image-export-v1；其基础来源与任务profile在exportInput配对。"
    export_input = schemas["BackendExportInputDto"]
    export_input_raw = schemas["BackendExportInputDtoRaw"]
    for component in (export_input, export_input_raw):
        props = component["properties"]
        for field in ("taskProfile", "baseSourceProfile"):
            if field not in props:
                raise ValueError("Unreviewed image export input field: " + field)
        _plain(props["taskProfile"]).update(enum=[PROFILE])
        _plain(props["baseSourceProfile"]).update(enum=BASE_PROFILES)
        component["description"] = "普通导出省略taskProfile/baseSourceProfile；Image导出必须同时提供二者，并与job.input.sourceProfile配对。"
        component.setdefault("allOf", []).extend([
            {"if": {"required": ["taskProfile"]}, "then": {"required": ["baseSourceProfile"]}},
            {"if": {"required": ["baseSourceProfile"]}, "then": {"required": ["taskProfile"]}},
        ])
    # ExportTaskCapabilitiesDto fields are nullable in the CLR and therefore
    # omitted on ordinary deployments.  Keep the old sourceProfiles untouched.
    schemas["BackendJobInputDto"]["description"] = "导出job输入；Image任务仅在export操作中使用project-image-export-v1。"
    schemas["BackendJobInputDtoRaw"]["description"] = "登记导出job输入；sourceProfile与exportInput的Image任务字段必须配对。"


def configure_project_image_export_operation(operation, item):
    if item["operationId"] not in (
        "exportCapabilities", "exportProject", "exportResult", "recoveredExportResult",
        "exportContent", "recoveredExportContent", "exportDiagnostics",
    ):
        return
    operation["x-project-image-export-profile"] = PROFILE
    operation["x-project-image-export-base-profiles"] = copy.deepcopy(BASE_PROFILES)
    operation["x-project-image-export-limits"] = copy.deepcopy(LIMITS)
    operation["description"] += (
        " 0.101已保存工程Image导出使用独立project-image-export-v1 Worker能力；"
        "客户端仍只提交既有expectedRevision/format/includeAudio/metadata，Image任务的taskProfile/baseSourceProfile"
        "仅出现在登记job input/exportInput并由服务配对。图片export只支持无预设static/static-analysis/直接package基础源，"
        "package仅无音轨；预览/validation能力不能代替。"
    )
    if item["operationId"] == "exportProject":
        operation["x-project-image-export-failures"] = [
            dict(status=503, code="taskUnavailable", field="worker", failureCode="WorkerImageExportUnavailable"),
            dict(status=422, code="exportInputRejected", field="background", failureCode="ProjectImageSourceUnavailable"),
            dict(status=422, code="exportInputRejected", field="background", failureCode="ProjectImageSourceChanged"),
            dict(status=413, code="resourceTooLarge", field="background", failureCode="WorkerSnapshotBudgetExceeded"),
        ]

