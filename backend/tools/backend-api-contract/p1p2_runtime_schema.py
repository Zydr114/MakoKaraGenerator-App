"""Reviewed P1/P2 shared runtime input overlays."""
import copy

RUNTIME = "runtime-project-input-v1"
PACKAGE_PRESETS = "package-lyrics-scene-presets-v1"
STATIC = ["static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1", "static-lyrics-scene-presets-v1"]
ORDINARY = STATIC + ["package-lyrics-scene-v1", PACKAGE_PRESETS]
PREVIEW = ORDINARY + [RUNTIME]
VALIDATION = ORDINARY + [RUNTIME]
EXPORT = ORDINARY + [RUNTIME]
JOB = ORDINARY + [RUNTIME]
VIDEO_MIMES = ["video/mp4", "video/quicktime", "video/webm", "video/x-matroska"]


def plain(value):
    return value["anyOf"][0] if "anyOf" in value else value


def extend(schemas, metadata):
    caps = schemas["BackendCapabilitiesResponse"]
    for name in ("maxAssetBytes", "maxAssetQuotaBytes"):
        field = next(p for p in metadata["BackendCapabilitiesResponse"]["properties"] if p["name"] == name)
        if field["type"] != "Int64" or not field["nullable"] or field["optional"] or field["omitNull"]:
            raise ValueError("Unreviewed required-null upload capability: " + name)
        caps["properties"][name] = {"anyOf": [{"type": "integer", "format": "int64", "minimum": 1, "maximum": 9223372036854775807}, {"type": "null"}],
            "description": "必填的部署字节限额；null表示该维度未配置限额。数字为JSON Int64，客户端须保真读取。"}
        if name not in caps["required"]: caps["required"].append(name)
    create = schemas["CreateProjectSessionRequest"]
    for name in ("audioAssetId", "lyricsAssetId"):
        field = next(p for p in metadata["CreateProjectSessionRequest"]["properties"] if p["name"] == name)
        if field["type"] != "String" or not field["nullable"] or not field["optional"] or field["defaultValue"] is not None:
            raise ValueError("Unreviewed optional create material: " + name)
        plain(create["properties"][name]).update(minLength=1, pattern=r"\S")
    create["description"] = "非空name和metadata必填；音频/歌词ID各自可省略或null，从真实空工程或单素材工程开始；空白ID拒绝。四组合保持revision0、dirty和原收据。"
    schemas["PreviewRuntimeInfoResponse"]["properties"]["sourceProfile"].update(
        const="static-lyrics-scene-v1",
        description="兼容单值，保持static-lyrics-scene-v1；当前实际输入能力读取sourceProfiles。")
    for component, profiles in (("PreviewRuntimeInfoResponse", PREVIEW), ("ValidationTaskCapabilitiesDto", VALIDATION),
                                ("ExportTaskCapabilitiesDto", EXPORT)):
        schemas[component]["properties"]["sourceProfiles"].update(items={"type": "string", "enum": profiles}, uniqueItems=True,
            description="只公布对应实际Worker能力通过的固定来源；runtime覆盖需准备的媒体/包字体闭包，不从旧能力推断新来源。")
    for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        schemas[component]["description"] = "固定validation/export任务的实际输入；sourceProfile区分静态、包与统一Runtime来源，不能只按媒体类型推断。"
        profile = plain(schemas[component]["properties"]["sourceProfile"])
        profile["enum"] = copy.deepcopy(JOB)
        profile["description"] = "实际协商并固定的来源profile；统一Runtime任务使用runtime-project-input-v1。"
    availability = ["staticAvailable", "advancedRuntimeAvailable", "advancedUnsupported"]
    detail = schemas["TemplateDetailResponse"]
    detail["properties"]["definitionAvailability"].update(enum=availability,
        description="staticAvailable提供静态作者定义；advancedRuntimeAvailable表示现有Line/Token高级闭包可执行，definition仍为null；advancedUnsupported保持明确不可执行。")
    detail["allOf"][1]["if"]["properties"]["definitionAvailability"] = {"enum": availability[1:]}
    schemas["TemplateTransferSummaryDto"]["properties"]["definitionAvailability"]["enum"] = copy.deepcopy(availability)
    schemas["ValidationJobRequest"]["description"] = "精确expectedRevision、framesPerOutput与metadata；Host固定当前授权工程完整Runtime输入，不接受媒体bytes/path/profile/grant。"
    schemas["SceneMeasurementDataDto"]["properties"]["sourceProfile"]["enum"] = copy.deepcopy(EXPORT)
    for component in ("ProjectEditCandidateDto",):
        field = schemas[component]["properties"]["candidateRevision"]
        field.update(type="string", minLength=1, description="固定编辑候选的不透明candidate-{guidN}身份；不能解析为工程修订。")
    source = schemas["PreviewSourceDto"]["properties"]["sourceRevision"]
    source["description"] = "不透明固定来源修订：普通工程为十进制修订，编辑候选为candidate-{guidN}；保持原字符串。"
    revision = schemas["SceneMeasurementResponse"]["properties"]["projectRevision"]
    revision.clear()
    revision.update(oneOf=[copy.deepcopy(schemas["SceneMeasurementRequest"]["properties"]["expectedRevision"]),
        {"type": "string", "pattern": r"^candidate-\S+$"}],
        description="固定测量来源修订：普通工程为canonical十进制工程修订，编辑候选为不透明candidate身份，与候选sourceRevision一致；不能统一按Int64解析。")
    schemas["SceneMeasurementRequest"]["description"] += " 候选expectedRevision必须等于baseProjectRevision；sourceRevision/candidateRevision均保持不透明身份。"


def configure(operation, item):
    name = item["operationId"]
    if name == "createProject":
        operation["description"] += " 音频和歌词各自可省略/null；两者省略直接创建空工程，仅有音频时才准备音轨/波形并占用Worker名额。"
    if name == "uploadAsset":
        operation["description"] += " 原始字节或单文件multipart按流写入，不要求Content-Length；部署maxAssetBytes/maxAssetQuotaBytes为required nullable Int64，null不转换成固定限额。"
    if name in ("runtimeInfo", "createPreview", "updatePreviewInput", "createPresetApplicationPreview", "createTemplatePreview", "measureOrchestration", "validationCapabilities", "validateProject", "exportCapabilities", "exportProject"):
        operation["x-runtime-input-profile"] = RUNTIME
        operation["description"] += " P2共享固定Runtime输入按实际独立Worker能力消费媒体、包和预设闭包；HTTP不接主机路径或私有grant；完整当前能力读取对应sourceProfiles。"
