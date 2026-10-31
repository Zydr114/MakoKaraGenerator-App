"""Reviewed 0.95 basic-business transport and provenance overlays, after live CLR reflection."""
import copy

from orchestration_properties_schema import int64_pattern
from template_library_schema import PROPERTIES, REVISION


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def obj(properties, required=None):
    return {"type": "object", "properties": properties,
            "required": list(properties) if required is None else required, "additionalProperties": False}


STABLE = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
IDENTITY = {"type": "string", "minLength": 1, "pattern": r"\S"}
PROJECT_REVISION = {"type": "string", "pattern": int64_pattern(), "x-int64-string": True}
CANONICAL_REVISION = {**PROJECT_REVISION, "pattern": "^(?=0$|[1-9])" + int64_pattern()[1:]}
HASH = {"type": "string", "pattern": "^[0-9a-f]{64}$"}
TRANSFER_LIMITS = dict(maxRequestBytes=16384, maxArchiveBytes=268435456, maxExpandedBytes=268435456,
    maxFileBytes=67108864, maxZipEntries=1024, maxTemplates=100, maxReportBytes=65536,
    maxRetainedPerClient=2, maxRetainedTotal=8, maxRetainedBytes=1073741824, lifetimeSeconds=300)
LYRICS_LIMITS = dict(maxRequestBytes=16384, maxSourceLines=10000, maxSourceTokensAndRuby=100000,
    maxSourceBytes=4194304, maxContentBytes=4194304, maxReportBytes=65536,
    maxRetainedPerClient=8, maxRetainedTotal=32, maxConcurrent=2, maxConcurrentPerClient=1, lifetimeSeconds=300)


def extend_basic_command_schemas(schemas, metadata, schema_for, variants, base_command):
    array = {"type": "array", "items": copy.deepcopy(STABLE), "minItems": 1, "maxItems": 1000, "uniqueItems": True}
    definitions = (
        ("orchestration.setClipMembers", "SetOrchestrationClipMembersCommandDto", {"branchId": "String", "clipId": "String", "lineIds": "String[]"},
         {"branchId": STABLE, "clipId": STABLE, "lineIds": array}),
        ("orchestration.splitClip", "SplitOrchestrationClipCommandDto", {"branchId": "String", "clipId": "String", "splitBeforeLineId": "String", "newClipId": "String"},
         {"branchId": STABLE, "clipId": STABLE, "splitBeforeLineId": STABLE, "newClipId": STABLE}),
        ("orchestration.mergeClips", "MergeOrchestrationClipsCommandDto", {"branchId": "String", "clipIds": "String[]", "targetClipId": "String"},
         {"branchId": STABLE, "clipIds": {**array, "minItems": 2}, "targetClipId": STABLE}),
        ("audio.replaceAsset", "ReplaceProjectAudioAssetCommandDto", {"assetId": "String"}, {"assetId": STABLE}),
        ("audio.clear", "ClearProjectAudioCommandDto", {}, {}),
        ("project.rename", "RenameProjectCommandDto", {"name": "String"},
         {"name": {**IDENTITY, "maxLength": 256, "x-max-utf16-code-units": 256}}),
    )
    for typ, clr, fields, values in definitions:
        actual = metadata[clr]
        if actual.get("baseType") != "ProjectCommandDto" or {p["name"] for p in actual["properties"]} != set(base_command["properties"]) | set(fields):
            raise ValueError("Unreviewed basic command shape: " + clr)
        if any(p["type"] != fields[p["name"]] or not p["nullable"] or p["optional"] or p["defaultValue"] is not None
               for p in actual["properties"] if p["name"] in fields):
            raise ValueError("Unreviewed basic command fields/nullability/defaults: " + clr)
        component = schema_for(clr, "request")["$ref"].split("/")[-1]
        schemas[component] = {**obj({"type": {"type": "string", "const": typ}, **copy.deepcopy(values)}),
            "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr,
            "x-capability": "project-sessions.commands." + typ}
        if typ.startswith("orchestration."):
            schemas[component]["x-clip-members-limits"] = dict(maxIntentBytes=65536, maxMembers=1000, maxReadBytes=1048576,
                maxReadItems=20000, maxNodes=2000, maxDepth=64, maxInputCharacters=1048576,
                description="提交前完整编排/预设分配投影及完整回执预算。连续顺序、独立Karaoke来源、上下文和引用合法性由业务验证。")
        variants.append(ref(component))


def configure_basic_operation(op, item):
    operation = item["operationId"].removesuffix("Head")
    queries = None
    if operation == "projectResolvedStyle":
        queries = {"expectedRevision": PROJECT_REVISION, "branchId": STABLE, "lineId": STABLE,
                   "tokenId": STABLE, "clipId": STABLE, "nodeId": STABLE}
        required = ["expectedRevision", "branchId", "lineId"]
        op["x-capability"] = "project-sessions.resolved-style.read"
        op["x-query-schema"] = ref("ProjectResolvedStyleQuery")
        op["x-read-model-limits"] = dict(maxResponseBytes=1048576, maxItems=20000, maxNodes=2000, maxDepth=64,
            description="实际branch/line推导clip/container及Core调度slot，nodePath包含真实text叶节点；可选clipId/nodeId只能断言相同上下文。无装载计划时给container上下文，不猜槽位。静态预设/局部覆盖，不执行动画、脚本或像素采样。")
    elif operation == "projectResources":
        queries = {"expectedRevision": PROJECT_REVISION, "offset": {"type": "integer", "minimum": 0, "maximum": 2147483647, "default": 0},
                   "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}}
        required = ["expectedRevision"]
        op["x-capability"] = "project-sessions.resources.read"
        op["x-query-schema"] = ref("ProjectResourcesQuery")
        op["x-read-model-limits"] = dict(maxResponseBytes=1048576, maxItems=20000,
            description="提交分页前验证当前工程实际资源完整闭包；不含客户端未引用上传。无主机路径或凭证；没有公开内容路由的内嵌资源不提供contentUrl。")
    elif operation == "projectDirectoryEntries":
        queries = {"relativePath": {"type": "string", "maxLength": 2048, "default": ""},
                   "offset": {"type": "integer", "minimum": 0, "maximum": 2147483647, "default": 0},
                   "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}}
        required = []
        op["x-capability"] = "file-roots.entries"
        op["x-query-schema"] = ref("ProjectDirectoryEntriesQuery")
        op["x-directory-limits"] = dict(maxScannedEntries=4096, maxPageSize=100, maxRelativePathUtf16Units=2048,
            description="受信rootGrantId下单层目录，按名称Ordinal排序；包含文件在内扫描超过4096项整次拒绝，不截断，不递归，不装载工程。链接及越界拒绝。不同请求不共享文件系统快照。")
    capabilities = {"planLyricsExport": "project-sessions.lyrics-export-plans", "exportLyrics": "project-sessions.lyrics-exports",
        "lyricsExportContent": "lyrics-exports.content.range", "preflightTemplateImport": "template-libraries.imports.preflight",
        "importTemplates": "template-libraries.imports", "exportTemplateArchive": "template-libraries.exports",
        "templateTransferArchive": "template-transfers.archive.range"}
    if operation in capabilities:
        op["x-capability"] = capabilities[operation]
        op["x-query-policy"] = dict(allowUnknown=False, allowed=[])
    if operation in ("planLyricsExport", "exportLyrics", "lyricsExportContent"):
        op["x-lyrics-export-limits"] = copy.deepcopy(LYRICS_LIMITS)
        op["description"] += " 固定歌词快照/显式损失确认/UTF-8内容；支持 lrc.plain、lrc.enhanced、ttml.project、ttml.amll。scope省略或branch须branchId；document仅ttml.project且禁止branchId（包括null）。document捕获完整分支与主来源，响应branchId显式null；branch捕获选定来源并报告其它来源省略。不写工程、历史、dirty或修订；样式与profile不能承载的数据须计划报告。"
    if operation in ("preflightTemplateImport", "importTemplates", "exportTemplateArchive", "templateTransferArchive"):
        op["x-template-transfer-limits"] = copy.deepcopy(TRANSFER_LIMITS)
        op["description"] += " 原.mktpl完整闭包；预检/导入只允许personal，导出可builtin/personal。高级包/配方/材质惰性保真，不编译或执行。成功预检单次消费，metadata重放保原结果。"
    if operation == "exportTemplateArchive":
        op["description"] += " builtin模板仅在捕获归档内映射为exported:<原builtinID>；响应templateIds保持原选择身份，personal身份原样。导入仍生成全部新身份。"
    if queries is not None:
        op["parameters"] += [dict(name=name, **{"in": "query"}, required=name in required, schema=copy.deepcopy(schema)) for name, schema in queries.items()]
        op["x-query-policy"] = dict(allowUnknown=False, allPresentExactlyOnce=True, caseSensitiveNames=True, required=required, allowed=list(queries))


def extend_basic_response_schemas(schemas, metadata, schema_for):
    # Explicit new public record fields and CLR types; shared existing records keep their prior reviewed schemas.
    shapes = {
        "ResolvedStyleContextDto": dict(branchId="String", lineId="String", tokenId="String", clipId="String", containerId="String", nodeId="String", textNodeId="String", nodePath="String[]", contextKind="String"),
        "ResolvedStylePropertySourceDto": dict(property="String", sourcePath="String"),
        "ProjectResolvedStyleResponse": dict(projectSessionId="String", projectRevision="String", context="ResolvedStyleContextDto", style="CanonicalStyleDto", sources="ResolvedStylePropertySourceDto[]", resolution="String", finalPixelAppearance="Boolean", protocolVersion="String"),
        "OrchestrationClipMembersCommandResultDto": dict(operation="String", branchId="String", clips="ProjectOrchestrationClipDto[]", removedClipIds="String[]", changed="Boolean", createdApplicationIds="String[]", removedApplicationIds="String[]"),
        "ProjectPackageClosureSummaryDto": dict(packageId="String", packageRevision="String", manifestSizeBytes="String", manifestSha256="String", totalSizeBytes="String", fileCount="Int32", validatedFileCount="Int32", unsupportedFileCount="Int32", status="String"),
        "ProjectResourceDto": dict(resourceId="String", kind="String", roles="String[]", status="String", sizeBytes="String", sha256="String", assetId="String", contentUrl="String", preparationStatus="String", waveformStatus="String", packageId="String", packageRevision="String", packagePath="String", packageRole="String", packageClosure="ProjectPackageClosureSummaryDto"),
        "ProjectResourcesResponse": dict(projectSessionId="String", projectRevision="String", offset="Int32", totalCount="Int32", nextOffset="Int32", resources="ProjectResourceDto[]", protocolVersion="String"),
        "ProjectDirectoryEntryDto": dict(name="String", relativePath="String", isProject="Boolean"),
        "ProjectDirectoryEntriesResponse": dict(rootGrantId="String", relativePath="String", offset="Int32", totalCount="Int32", nextOffset="Int32", entries="ProjectDirectoryEntryDto[]", protocolVersion="String"),
        "LyricsExportOptionsDto": dict(title="String", artist="String", album="String", newLine="String", writeByteOrderMark="Boolean", millisecondPrecision="Boolean"),
        "LyricsExportPlanRequest": dict(expectedRevision="String", branchId="String", format="String", options="LyricsExportOptionsDto", metadata="RequestMetadataDto", scope="String"),
        "LyricsExportRequest": dict(expectedRevision="String", planId="String", acceptLosses="Boolean", metadata="RequestMetadataDto"),
        "TemplateImportPreflightRequest": dict(assetId="String", metadata="RequestMetadataDto"),
        "TemplateImportRequest": dict(expectedRevision="String", preflightId="String", metadata="RequestMetadataDto"),
        "TemplateArchiveExportRequest": dict(expectedRevision="String", templateIds="String[]", metadata="RequestMetadataDto"),
        "TemplateTransferSummaryDto": dict(summary="TemplateSummaryDto", definitionAvailability="String"),
        "TemplateImportIdentityDto": dict(sourceTemplateId="String", templateId="String"),
        "TemplateImportPreflightResponse": dict(preflightId="String", assetSha256="String", archiveBytes="Int64", expandedBytes="Int64", templates="TemplateTransferSummaryDto[]", expiresAtUtc="DateTimeOffset", identityPolicy="String", protocolVersion="String"),
        "TemplateImportResponse": dict(libraryId="String", previousRevision="String", revision="String", changed="Boolean", importedTemplates="TemplateImportIdentityDto[]", protocolVersion="String"),
        "TemplateArchiveExportResponse": dict(exportId="String", libraryId="String", revision="String", templateIds="String[]", sizeBytes="Int64", sha256="String", contentUrl="String", expiresAtUtc="DateTimeOffset", protocolVersion="String"),
        "LyricsExportLossDto": dict(feature="String", affectedCount="Int32", message="String"),
        "LyricsExportIssueDto": dict(kind="String", lineId="String", message="String"),
        "LyricsExportAssessmentDto": dict(losses="LyricsExportLossDto[]", exportableLineCount="Int32", skippedLineCount="Int32", issues="LyricsExportIssueDto[]", hasLosses="Boolean"),
        "LyricsExportPlanResponse": dict(planId="String", projectSessionId="String", projectRevision="String", branchId="String", format="String", options="LyricsExportOptionsDto", encoding="String", expiresAtUtc="DateTimeOffset", plan="LyricsExportAssessmentDto", additionalLosses="LyricsExportLossDto[]", requiresLossAcceptance="Boolean", omittedBranchCount="Int32", scope="String", protocolVersion="String"),
        "LyricsExportReportDto": dict(exporterId="String", exporterName="String", sourceLineCount="Int32", exportedLineCount="Int32", skippedLineCount="Int32", droppedTokenTimeCount="Int32", roundedTimeCount="Int32", writtenByteCount="Int64", metadataCount="Int32", plan="LyricsExportAssessmentDto", writtenRubySegmentCount="Int32", skippedRubySegmentCount="Int32", droppedRubySegmentTimeCount="Int32"),
        "LyricsExportResponse": dict(exportId="String", planId="String", projectSessionId="String", projectRevision="String", branchId="String", format="String", encoding="String", sizeBytes="Int64", sha256="String", contentUrl="String", expiresAtUtc="DateTimeOffset", report="LyricsExportReportDto", additionalLosses="LyricsExportLossDto[]", protocolVersion="String", scope="String"),
    }
    for name, expected in shapes.items():
        if {p["name"]: p["type"] for p in metadata[name]["properties"]} != expected:
            raise ValueError("Unreviewed basic business DTO fields/types: " + name)
        schema_for(name, "request" if name.endswith("Request") else "response")

    for name, query, required in (
        ("ProjectResolvedStyleQuery", {"expectedRevision": PROJECT_REVISION, "branchId": STABLE, "lineId": STABLE, "tokenId": STABLE, "clipId": STABLE, "nodeId": STABLE}, ["expectedRevision", "branchId", "lineId"]),
        ("ProjectResourcesQuery", {"expectedRevision": PROJECT_REVISION, "offset": {"type": "integer", "minimum": 0, "maximum": 2147483647}, "limit": {"type": "integer", "minimum": 1, "maximum": 100}}, ["expectedRevision"]),
        ("ProjectDirectoryEntriesQuery", {"relativePath": {"type": "string", "maxLength": 2048}, "offset": {"type": "integer", "minimum": 0, "maximum": 2147483647}, "limit": {"type": "integer", "minimum": 1, "maximum": 100}}, [])):
        schemas[name] = obj(copy.deepcopy(query), required)
    context = schemas["ResolvedStyleContextDto"]
    for field in ("tokenId", "clipId"):
        context["properties"][field] = {"anyOf": [copy.deepcopy(IDENTITY), {"type": "null"}]}
        if field not in context["required"]:
            context["required"].append(field)
    context["properties"]["contextKind"]["enum"] = ["container", "scheduledSlot"]
    context["properties"]["nodePath"].update(minItems=2, maxItems=65, uniqueItems=True)
    resolved = schemas["ProjectResolvedStyleResponse"]
    resolved["properties"]["resolution"]["const"] = "staticPresetHierarchy"
    resolved["properties"]["finalPixelAppearance"]["const"] = False
    source = schemas["ResolvedStylePropertySourceDto"]
    source["properties"]["property"]["enum"] = list(PROPERTIES)
    resolved["properties"]["sources"].update(minItems=21, maxItems=21,
        prefixItems=[{"allOf": [ref("ResolvedStylePropertySourceDto"), {"properties": {"property": {"const": prop}}}]} for prop in PROPERTIES])
    result = schemas["OrchestrationClipMembersCommandResultDto"]["properties"]
    result["operation"]["enum"] = ["setClipMembers", "splitClip", "mergeClips"]
    for name in ("removedClipIds", "createdApplicationIds", "removedApplicationIds"):
        result[name]["uniqueItems"] = True
    result["clips"].update(minItems=1, maxItems=2)
    for name in ("ProjectResourcesResponse", "ProjectDirectoryEntriesResponse"):
        for field in ("offset", "totalCount", "nextOffset"):
            schemas[name]["properties"][field]["minimum"] = 0
        schemas[name]["properties"]["resources" if name == "ProjectResourcesResponse" else "entries"]["maxItems"] = 100
    resource = schemas["ProjectResourceDto"]["properties"]
    resource["kind"]["enum"] = ["media", "audioAnalysis", "effectPackageManifest", "effectPackageFile", "templateSnapshot"]
    resource["status"]["enum"] = ["available", "missing", "identityUnavailable", "unsupportedContent", "embedded", "noStream", "failed", "validated", "unsupported"]
    resource["sizeBytes"].update(pattern=int64_pattern(), **{"x-int64-string": True})
    resource["roles"].update(minItems=1, uniqueItems=True)
    resource["preparationStatus"]["enum"] = ["prepared", "unavailable"]
    resource["waveformStatus"]["enum"] = ["available", "noAudio", "cacheBudgetExceeded", "audioIdentityChanged"]
    closure = schemas["ProjectPackageClosureSummaryDto"]["properties"]
    for field in ("manifestSizeBytes", "totalSizeBytes"):
        closure[field].update(pattern=int64_pattern(), **{"x-int64-string": True})
    closure["manifestSha256"]["pattern"] = "^[0-9a-f]{64}$"
    closure["fileCount"].update(minimum=0, maximum=200000)
    closure["validatedFileCount"].update(minimum=0, maximum=200000)
    closure["unsupportedFileCount"].update(minimum=0, maximum=200000)
    closure["status"]["enum"] = ["validated", "unsupported"]
    for field in ("packageId", "packageRevision", "packagePath", "packageRole"):
        resource[field]["description"] = "包内逻辑身份字段；无包资源时省略。"
    resource["packageClosure"]["description"] = "包manifest资源的闭包摘要；仅manifest项提供。"

    for component in ("LyricsExportOptionsDto", "LyricsExportOptionsDtoInput"):
        fields = schemas[component]["properties"]
        for field in ("title", "artist", "album"):
            target = fields[field]["anyOf"][0] if "anyOf" in fields[field] else fields[field]
            target.update(maxLength=1024, pattern=r"^[^\x00-\x1f\x7f-\x9f\]]*$")
        fields["newLine"]["enum"] = ["\n", "\r\n"]
    for component in ("LyricsExportPlanRequest", "LyricsExportRequest"):
        schemas[component]["properties"]["expectedRevision"] = copy.deepcopy(CANONICAL_REVISION)
    schemas["LyricsExportPlanRequest"]["properties"]["branchId"] = copy.deepcopy(IDENTITY)
    schemas["LyricsExportRequest"]["properties"]["planId"] = copy.deepcopy(IDENTITY)
    for component in ("LyricsExportPlanRequest", "LyricsExportPlanResponse", "LyricsExportResponse"):
        schemas[component]["properties"]["format"]["enum"] = ["lrc.plain", "lrc.enhanced", "ttml.project", "ttml.amll"]
    request = schemas["LyricsExportPlanRequest"]
    request["additionalProperties"] = False
    request["properties"]["scope"] = {"type": "string", "enum": ["branch", "document"], "default": "branch"}
    request["required"] = ["expectedRevision", "format", "options", "metadata"]
    request["oneOf"] = [
        {"properties": {"scope": {"const": "branch"}}, "required": ["branchId"]},
        {"properties": {"scope": {"const": "document"}, "format": {"const": "ttml.project"}}, "required": ["scope"], "not": {"required": ["branchId"]}},
    ]
    for component in ("LyricsExportPlanResponse", "LyricsExportResponse"):
        response = schemas[component]
        response["properties"]["scope"] = {"type": "string", "enum": ["branch", "document"]}
        response["properties"]["branchId"] = {"anyOf": [copy.deepcopy(IDENTITY), {"type": "null"}]}
        for field in ("scope", "branchId"):
            if field not in response["required"]:
                response["required"].append(field)
        response["oneOf"] = [
            {"properties": {"scope": {"const": "branch"}, "branchId": copy.deepcopy(IDENTITY)}},
            {"properties": {"scope": {"const": "document"}, "branchId": {"type": "null"}, "format": {"const": "ttml.project"}}},
        ]
    for field in ("writtenRubySegmentCount", "skippedRubySegmentCount", "droppedRubySegmentTimeCount"):
        schemas["LyricsExportReportDto"]["properties"][field]["minimum"] = 0
    for component in ("LyricsExportPlanResponse", "LyricsExportResponse"):
        schemas[component]["properties"]["encoding"]["enum"] = ["utf-8", "utf-8-bom"]
    # Library revisions are missing/index-SHA identities, not the project Int64 scalar overlay.
    for component, fields in (("TemplateImportRequest", ("expectedRevision",)), ("TemplateArchiveExportRequest", ("expectedRevision",)),
                              ("TemplateImportResponse", ("previousRevision", "revision")), ("TemplateArchiveExportResponse", ("revision",))):
        for field in fields:
            schemas[component]["properties"][field] = copy.deepcopy(REVISION)
    schemas["TemplateImportPreflightRequest"]["properties"]["assetId"] = copy.deepcopy(IDENTITY)
    schemas["TemplateImportRequest"]["properties"]["preflightId"] = copy.deepcopy(IDENTITY)
    schemas["TemplateArchiveExportRequest"]["properties"]["templateIds"] = {"type": "array", "items": copy.deepcopy(IDENTITY), "minItems": 1, "maxItems": 100, "uniqueItems": True}
    schemas["TemplateImportPreflightResponse"]["properties"]["identityPolicy"]["const"] = "generateNewIds"
    schemas["TemplateImportPreflightResponse"]["properties"]["assetSha256"] = copy.deepcopy(HASH)
    schemas["TemplateImportPreflightResponse"]["properties"]["templates"].update(minItems=1, maxItems=100)
    schemas["TemplateTransferSummaryDto"]["properties"]["definitionAvailability"]["enum"] = ["staticAvailable", "advancedUnsupported"]
    for component, fields in (("LyricsExportLossDto", ("affectedCount",)),
                              ("LyricsExportAssessmentDto", ("exportableLineCount", "skippedLineCount")),
                              ("LyricsExportPlanResponse", ("omittedBranchCount",)),
                              ("LyricsExportReportDto", ("sourceLineCount", "exportedLineCount", "skippedLineCount", "droppedTokenTimeCount", "roundedTimeCount", "writtenByteCount", "metadataCount")),
                              ("LyricsExportResponse", ("sizeBytes",)),
                              ("TemplateImportPreflightResponse", ("archiveBytes", "expandedBytes")),
                              ("TemplateArchiveExportResponse", ("sizeBytes",))):
        for field in fields:
            schemas[component]["properties"][field]["minimum"] = 0
    for component, field, maximum in (("LyricsExportResponse", "sizeBytes", 4194304),
                                    ("TemplateImportPreflightResponse", "archiveBytes", 268435456),
                                    ("TemplateImportPreflightResponse", "expandedBytes", 268435456),
                                    ("TemplateArchiveExportResponse", "sizeBytes", 268435456)):
        schemas[component]["properties"][field]["maximum"] = maximum
