"""Saved-project image validation uses existing HTTP DTOs and independent Worker proof."""
import copy

from validation_package_schema import VALIDATION_SOURCE_PROFILES

PROFILE = "project-image-validation-v1"
SOURCE_PROFILES = [*VALIDATION_SOURCE_PROFILES, PROFILE]
LIMITS = dict(maxInputBytes=4194272, maxImageBytes=2097152, maxImageDimension=4096,
              maxDecodedImageBytes=16777216, maxBaseSnapshotBytes=4194304,
              maxSourceValues=200000, maxReportBytes=262144)


def extend_project_image_validation_schemas(schemas, metadata):
    expected = {"sourceProfile": "String", "sourceProfiles": "String[]"}
    actual = {p["name"]: p["type"] for p in metadata["ValidationTaskCapabilitiesDto"]["properties"]}
    if any(actual.get(name) != kind for name, kind in expected.items()):
        raise ValueError("Unreviewed image validation capability fields")
    props = schemas["ValidationTaskCapabilitiesDto"]["properties"]
    props["sourceProfile"]["description"] = "兼容首选来源保持static-lyrics-scene-v1；图片能力只由sourceProfiles的独立Worker探测证明。"
    props["sourceProfiles"].update(items={"type": "string", "enum": SOURCE_PROFILES}, uniqueItems=True,
        description="三种静态来源，加实际Worker各自独立协商通过的直接包与project-image-validation-v1。图片预览或package能力不能代替图片正式验证能力。")
    for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        target = schemas[component]["properties"]["sourceProfile"]
        target = target["anyOf"][0] if "anyOf" in target else target
        target.update(enum=copy.deepcopy(SOURCE_PROFILES), description="正式验证固定输入来源；图片为project-image-validation-v1，sourceSha256覆盖完整内存image envelope。")
    schemas["ValidationJobRequest"]["description"] = (
        "既有精确expectedRevision、framesPerOutput与metadata；Host从ProjectStore已加载的受管Image闭包固定输入，"
        "不接受图片bytes/path/profile/grant。图片只支持无preset的static/static-analysis/直接package基础源。")


def configure_project_image_validation_operation(operation, item):
    if item["operationId"] not in ("validationCapabilities", "validateProject", "validationResult", "validationDiagnostics", "recoveredValidationResult"):
        return
    operation["x-project-image-validation-profile"] = PROFILE
    operation["x-project-image-validation-limits"] = copy.deepcopy(LIMITS)
    operation["description"] += (
        " 0.100已保存工程Image背景正式验证有限来源见行为§10；独立Worker能力，复用既有job/report/diagnostics与取消、deadline、close排水。"
        "图片export/measurement、video/font/preset组合仍不开放。")
    if item["operationId"] == "validateProject":
        operation["x-project-image-validation-failures"] = [
            dict(status=503, code="taskUnavailable", field="worker", failureCode="WorkerImageValidationUnavailable"),
            dict(status=422, code="commandNotSupported", field="background", failureCode="ProjectImageSourceUnavailable"),
            dict(status=422, code="commandNotSupported", field="background", failureCode="ProjectImageSourceChanged"),
            dict(status=413, code="resourceTooLarge", field="background", failureCode="SnapshotSizeExceeded"),
        ]
