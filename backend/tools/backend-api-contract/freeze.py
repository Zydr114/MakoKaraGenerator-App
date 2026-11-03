#!/usr/bin/env python3
"""Build/check the reviewed HTTP contract. No Server mutation or new API routes."""
import argparse
import copy
import hashlib
import json
import re
from pathlib import Path

from p3_workshop_schema import extend as extend_p3_workshop, configure as configure_p3_workshop
from p4_author_schema import extend as extend_p4_author, configure as configure_p4_author
from render_backend_schema import extend as extend_render_backends, configure as configure_render_backends
from w8_authoring_schema import extend as extend_w8_authoring, configure as configure_w8_authoring
from p1p2_runtime_schema import extend as extend_p1p2_runtime, configure as configure_p1p2_runtime, VIDEO_MIMES
from package_preview_schema import extend_package_preview_schemas, LIMITS as PACKAGE_PREVIEW_LIMITS
from project_image_preview_schema import extend_project_image_preview_schemas, configure_project_image_preview_operation
from project_image_validation_schema import extend_project_image_validation_schemas, configure_project_image_validation_operation
from project_image_export_schema import extend_project_image_export_schemas, configure_project_image_export_operation
from validation_package_schema import extend_validation_package_schemas
from ruby_timeline_schema import extend_ruby_commands, extend_ruby_responses
from basic_business_schema import extend_basic_command_schemas, extend_basic_response_schemas, configure_basic_operation
from editing_completion_schema import extend_editing_completion_schemas, preset_target_union
from node_creation_schema import extend_node_creation_schemas
from orchestration_properties_schema import extend_orchestration_schemas
from static_style_schema import extend_static_style_schemas, style_query_parameters
from template_library_schema import extend_template_library_schemas, query_parameters as template_query_parameters, LIMITS as TEMPLATE_LIMITS

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = "1.42.4"
BASELINE = "9dada95921162c55fa3a73f07581f1615b2910bf"
PRODUCT = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", (ROOT / "Directory.Build.props").read_text()).group(1)
FREEZE_DATE = "2026-11-04"
BEHAVIOR = "docs/architecture/backend-api.md"
TOOL = Path(__file__).resolve().parent

# Explicit public boundary. Handler-to-DTO and behavioral rules require review;
# the route comparison below prevents omitted/imaginary endpoints.
# method | path | stable operationId | request | response | success | section | summary
OPERATIONS = """
GET|/api/v1/health/live|healthLive|-|HealthResponse|200|2|读取存活与主机身份
GET|/api/v1/capabilities|capabilities|-|BackendCapabilitiesResponse|200|2|读取当前部署能力与限额
POST|/api/v1/clients|registerClient|ClientRegistrationRequest|ClientRegistrationResponse|201|3|首次登记或恢复客户端
POST|/api/v1/clients/{id}/heartbeat|heartbeatClient|-|ClientHeartbeatResponse|200|3|延长当前访问令牌与租约
GET|/api/v1/clients/{id}/state|clientState|-|ClientStateResponse|200|3|读取工程任务摘要与事件游标
GET|/api/v1/events|events|-|sse|200|4|订阅有界事件流
POST|/api/v1/assets|uploadAsset|upload|AssetResponse|201|5|上传恰好一个固定资源
GET|/api/v1/fonts|fonts|-|FontListResponse|200|6.12|读取实际Worker字体环境目录
POST|/api/v1/fonts/inspect|inspectFonts|FontInspectRequest|FontInspectResponse|200|6.12|检查实际Worker整形与回退缺字
POST|/api/v1/lyrics-import-plans|planLyricsImport|LyricsImportPlanRequest|LyricsImportPlanResponse|200|6.12|读取授权歌词资源的导入检测与来源预览
GET,HEAD|/api/v1/assets/{id}/content|assetContent|-|asset|200|5|读取授权资源字节
POST|/api/v1/project-sessions|createProject|CreateProjectSessionRequest|ProjectSessionSummaryResponse|201|6|同步创建工程并返回摘要
GET|/api/v1/project-sessions/{id}|projectSummary|-|ProjectSessionSummaryResponse|200|6|读取工程摘要
GET|/api/v1/project-sessions/{id}/history|projectHistory|-|ProjectHistoryResponse|200|6|读取历史状态
GET|/api/v1/project-sessions/{id}/lyrics/lines|lyricLines|-|LyricsLinesResponse|200|6|读取指定歌词来源的最小行投影
GET|/api/v1/project-sessions/{id}/lyrics/branches|lyricBranches|-|ProjectLyricBranchesResponse|200|6|读取精确修订的有界歌词来源列表
GET|/api/v1/project-sessions/{id}/lyrics/window|lyricsWindow|-|ProjectLyricsWindowResponse|200|6|读取精确修订的完整歌词编辑窗口
GET|/api/v1/project-sessions/{id}/settings|projectSettings|-|ProjectSettingsResponse|200|6|读取精确修订的工程标量设置
GET|/api/v1/project-sessions/{id}/resolved-style|projectResolvedStyle|-|ProjectResolvedStyleResponse|200|17|读取真实上下文静态预设样式与逐属性来源
GET|/api/v1/project-sessions/{id}/resources|projectResources|-|ProjectResourcesResponse|200|17|读取精确修订的当前工程资源引用页
POST|/api/v1/project-sessions/{id}/resources/package-asset|materializePackageResource|ProjectPackageResourceAssetRequest|ProjectPackageResourceAssetResponse|201|17|将已验证包内资源投影为当前客户端受控asset
GET|/api/v1/project-sessions/{id}/style|projectStyle|-|ProjectStyleResponse|200|6|读取精确修订的canonical静态样式继承投影
GET|/api/v1/project-sessions/{id}/orchestration|projectOrchestration|-|ProjectOrchestrationResponse|200|13|读取精确修订的工程编排结构投影
GET|/api/v1/project-sessions/{id}/orchestration/geometry|projectGeometryReference|-|ProjectGeometryReferenceResponse|200|13|读取精确修订的单节点固定容器参考
GET|/api/v1/project-sessions/{id}/orchestration/line-local-transform|projectLineLocalTransform|-|ProjectLineLocalTransformResponse|200|13|读取精确修订的单句局部微调
GET|/api/v1/project-sessions/{id}/orchestration/line-presentation|projectLinePresentation|-|ProjectLinePresentationResponse|200|13|读取精确修订的单句展示与进入动画继承
POST|/api/v1/project-sessions/{id}/orchestration/measurements|measureOrchestration|SceneMeasurementRequest|SceneMeasurementResponse|200|13|读取真实Worker同帧几何与PNG
GET|/api/v1/project-sessions/{id}/preset-applications|presetApplications|-|ProjectPresetApplicationsResponse|200|6|读取精确修订的工程预设应用投影
GET|/api/v1/template-libraries|templateLibraries|-|TemplateLibrariesResponse|200|13|读取固定模板库描述而不访问个人入口
GET|/api/v1/template-libraries/{id}/templates|templateList|-|TemplateListResponse|200|13|读取同次索引修订的有界模板摘要
GET|/api/v1/template-libraries/{id}/templates/detail|templateDetail|-|TemplateDetailResponse|200|13|读取精确修订的静态或完整高级模板定义
POST|/api/v1/template-libraries/{id}/commands|templateCommand|TemplateLibraryCommandRequest|TemplateLibraryCommandResponse|200|13|提交独立个人模板库CAS修改意图
GET|/api/v1/project-sessions/{id}/waveform|projectWaveform|-|ProjectWaveformResponse|200|6|读取同次音频准备的波形缓存元数据
GET,HEAD|/api/v1/project-sessions/{id}/waveform/peaks|projectWaveformPeaks|-|waveformPeaks|200|6|读取有界小端float32峰值窗口
POST|/api/v1/project-sessions/{id}/commands|executeCommand|ProjectCommandRequest|ProjectCommandResponse|200|6|提交九十六种已开放具名命令之一
GET|/api/v1/file-roots/{rootGrantId}/entries|projectDirectoryEntries|-|ProjectDirectoryEntriesResponse|200|17|读取授权根下单层有界目录
POST|/api/v1/project-sessions/{id}/lyrics-export-plans|planLyricsExport|LyricsExportPlanRequest|LyricsExportPlanResponse|201|17|预检来源或TTML全文档歌词导出与完整损失报告
POST|/api/v1/project-sessions/{id}/lyrics-exports|exportLyrics|LyricsExportRequest|LyricsExportResponse|201|17|按固定计划及显式损失确认生成歌词内容
GET,HEAD|/api/v1/lyrics-exports/{id}/content|lyricsExportContent|-|lyricsContent|200|17|读取短期授权歌词内容
POST|/api/v1/template-libraries/{id}/imports/preflight|preflightTemplateImport|TemplateImportPreflightRequest|TemplateImportPreflightResponse|201|17|完整预检授权模板归档闭包
POST|/api/v1/template-libraries/{id}/imports|importTemplates|TemplateImportRequest|TemplateImportResponse|200|17|按预检与精确个人库修订原子导入
POST|/api/v1/template-libraries/{id}/exports|exportTemplateArchive|TemplateArchiveExportRequest|TemplateArchiveExportResponse|201|17|导出固定库修订的完整模板归档
GET,HEAD|/api/v1/template-transfers/{id}/archive|templateTransferArchive|-|templateArchive|200|17|读取短期授权模板归档
GET|/api/v1/file-roots|fileRoots|-|FileRootsResponse|200|7|读取受信启动器授权目录根ID
POST|/api/v1/file-handles|createFileHandle|FileHandleRequest|FileHandleResponse|201|7|申请相对目录句柄
POST|/api/v1/project-sessions/open-jobs|openProject|OpenProjectSessionRequest|JobResponse|202|7|接受打开工程任务
POST|/api/v1/project-sessions/recover-jobs|recoverProject|RecoverProjectSessionRequest|JobResponse|202|7|接受恢复工程任务
POST|/api/v1/project-sessions/{id}/save-jobs|saveProject|SaveProjectRequest|JobResponse|202|7|接受保存任务
POST|/api/v1/project-sessions/{id}/close|closeProject|CloseProjectRequest|JobResponse|202|7|接受关闭任务
GET|/api/v1/jobs/{id}|jobState|-|JobResponse|200|8|读取live任务权威状态
POST|/api/v1/jobs/{id}/cancel|cancelJob|RequestMetadataDto|JobResponse|200|8|请求取消任务并返回当前状态
GET|/api/v1/recovery-candidates|recoveryCandidates|-|RecoveryCandidatesResponse|200|8|列举工程恢复候选
GET|/api/v1/recovery-jobs|recoveryJobs|-|RecoveryJobsResponse|200|8|读取旧主机持久任务记录
GET|/api/v1/runtime-info|runtimeInfo|-|PreviewRuntimeInfoResponse|200|9|读取预览运行能力
GET|/api/v1/render-backends|renderBackends|-|RenderBackendsResponse|200|25|读取当前部署真实CPU/GPU设备可用性
POST|/api/v1/gpu-preview-streams|createGpuPreviewStream|GpuPreviewStreamCreateRequest|GpuPreviewStreamState|201|25|创建固定来源GPU流
GET|/api/v1/gpu-preview-streams/{id}|readGpuPreviewStream|-|GpuPreviewStreamState|200|25|读取GPU流登记和Worker状态
PUT|/api/v1/gpu-preview-streams/{id}/source|replaceGpuPreviewStream|GpuPreviewStreamReplaceRequest|GpuPreviewStreamState|200|25|按修订更换GPU流来源
PUT|/api/v1/gpu-preview-streams/{id}/size|resizeGpuPreviewStream|GpuPreviewStreamResizeRequest|GpuPreviewStreamState|200|25|按修订更换GPU流尺寸
DELETE|/api/v1/gpu-preview-streams/{id}|closeGpuPreviewStream|GpuPreviewStreamCloseRequest|GpuPreviewStreamState|200|25|排空并关闭GPU流
POST|/api/v1/preview-sessions|createPreview|PreviewSessionRequest|PreviewSessionResponse|201|9|创建固定修订预览
POST|/api/v1/project-sessions/{id}/edit-previews|createEditPreview|ProjectEditPreviewRequest|PreviewSessionResponse|201|6.12|创建或替换固定修订的只读编辑候选
POST|/api/v1/project-sessions/{id}/preset-application-previews|createPresetApplicationPreview|PresetApplicationPreviewRequest|PreviewSessionResponse|201|13|创建只读预设候选试看
POST|/api/v1/effect-packages/inspect|inspectEffectPackage|EffectPackageInspectRequest|EffectPackageInspectResponse|200|24|检查授权固定包的完整声明而不执行脚本
POST|/api/v1/template-preview-options|templatePreviewOptions|TemplatePreviewOptionsRequest|TemplatePreviewOptionsResponse|200|24|读取固定模板的单多行试样目标与材质编辑焦点
POST|/api/v1/template-preview-sessions|createTemplatePreview|TemplatePreviewSessionRequest|PreviewSessionResponse|201|13|创建模板固定试样
PUT|/api/v1/preview-sessions/{id}/input|updatePreviewInput|PreviewProjectInputRequest|PreviewSessionResponse|200|9|显式替换固定预览输入
PUT|/api/v1/preview-sessions/{id}/render-backend|updatePreviewRenderBackend|PreviewRenderBackendRequest|PreviewSessionResponse|200|25|按精确代次原子切换固定预览执行设备
POST|/api/v1/preview-sessions/{id}/frame-requests|requestPreviewFrame|PreviewFrameCommandRequest|PreviewFrameAcceptedResponse|202|9|接受playback或seek帧请求
GET|/api/v1/preview-sessions/{id}|previewState|-|PreviewSessionStateResponse|200|9|读取预览权威状态与最新帧身份
POST|/api/v1/preview-sessions/{id}/close|closePreview|ClosePreviewSessionRequest|PreviewSessionStateResponse|200|9|关闭预览并等待排水
GET,HEAD|/api/v1/preview-frames/{id}/content|previewFrameContent|-|png|200|9|读取短租约PNG
GET|/api/v1/preview-sessions/{id}/diagnostics|previewDiagnostics|-|PreviewDiagnosticsPageDto|200|9|读取当前预览诊断页
GET|/api/v1/validation-capabilities|validationCapabilities|-|ValidationTaskCapabilitiesDto|200|10|读取正式验证能力
POST|/api/v1/project-sessions/{id}/validation-jobs|validateProject|ValidationJobRequest|JobResponse|202|10|接受固定输入正式验证
GET|/api/v1/jobs/{id}/validation-result|validationResult|-|ValidationReportDtoRaw|200|10|读取登记完整验证报告
GET|/api/v1/jobs/{id}/validation-diagnostics|validationDiagnostics|-|ValidationDiagnosticsPageDto|200|10|分页读取验证实际诊断
GET|/api/v1/recovery-jobs/{id}/validation-result|recoveredValidationResult|-|ValidationReportDtoRaw|200|10|按profile权限读取旧主机验证报告
GET|/api/v1/export-capabilities|exportCapabilities|-|ExportTaskCapabilitiesDto|200|11|读取短片导出能力
POST|/api/v1/project-sessions/{id}/export-jobs|exportProject|ExportJobRequest|JobResponse|202|11|接受静态短片导出
GET|/api/v1/jobs/{id}/export-result|exportResult|-|ExportResultReport|200|11|读取导出成功或分型失败报告
GET|/api/v1/recovery-jobs/{id}/export-result|recoveredExportResult|-|ExportResultReport|200|11|按profile权限读取旧主机导出报告
GET,HEAD|/api/v1/jobs/{id}/export-content|exportContent|-|video|200|11|读取已发布成片
GET,HEAD|/api/v1/recovery-jobs/{id}/export-content|recoveredExportContent|-|video|200|11|按profile权限读取旧主机成片
GET|/api/v1/jobs/{id}/export-diagnostics|exportDiagnostics|-|ExportDiagnosticsPageDto|200|11|分页读取导出实际诊断
GET|/api/v1/analysis-capabilities|analysisCapabilities|-|AnalysisTaskCapabilitiesDto|200|12|读取离线音频分析能力
POST|/api/v1/project-sessions/{id}/analysis-jobs|analyzeProject|AnalysisJobRequest|JobResponse|202|12|接受独立分析而不自动采用
GET,HEAD|/api/v1/jobs/{id}/analysis-result|analysisResult|-|analysis|200|12|读取原Core规范完整分析JSON
GET,HEAD|/api/v1/recovery-jobs/{id}/analysis-result|recoveredAnalysisResult|-|analysis|200|12|按profile权限读取旧主机完整分析
POST|/api/v1/drafts|createAuthorDraft|AuthorDraftCreateRequest|AuthorDraftResponse|201|21|从授权效果包或个人配置建立作者草稿
GET|/api/v1/drafts|readAuthorDrafts|-|AuthorDraftListResponse|200|21|读取当前客户端拥有的作者草稿
GET|/api/v1/drafts/{id}|readAuthorDraft|-|AuthorDraftResponse|200|21|读取当前客户端拥有的作者草稿
GET|/api/v1/drafts/{id}/sources|readAuthorSources|-|AuthorDraftSourcesResponse|200|21|读取精确草稿修订下的完整固定源码
GET|/api/v1/profile/author-drafts|readProfileAuthorDrafts|-|ProfileAuthorDraftListResponse|200|21|发现本机profile作者草稿及可认领状态
POST|/api/v1/profile/author-drafts/{id}/claim|claimAuthorDraft|AuthorDraftClaimRequest|AuthorDraftClaimResponse|200|21|按内容及所有权修订显式认领可恢复草稿
GET|/api/v1/drafts/{id}/package-files|readAuthorPackageFiles|-|AuthorPackageFilesResponse|200|21|读取固定包清单与可编辑文本文件
PUT|/api/v1/drafts/{id}/package-files|saveAuthorPackageFiles|AuthorPackageFilesRequest|AuthorDraftResponse|200|21|原子保存完整包文本及可选配置
GET|/api/v1/drafts/{id}/source-revisions|readAuthorSourceRevisions|-|AuthorSourceRevisionsResponse|200|21|分页读取保留修订与保存时间编译摘要
GET|/api/v1/drafts/{id}/package|downloadAuthorPackage|-|packageArchive|200|21|按精确草稿与源码修订下载效果包
GET|/api/v1/script-configurations/{id}/package|downloadScriptConfigurationPackage|-|packageArchive|200|21|按精确个人配置库修订下载效果包
PUT|/api/v1/drafts/{id}/configuration|saveAuthorConfiguration|AuthorDraftConfigurationRequest|AuthorDraftResponse|200|21|更新固定源码的完整作者配置
GET|/api/v1/drafts/{id}/compilation|readAuthorCompilation|-|AuthorCompilationResponse|200|21|读取最近一次作者编译诊断
GET|/api/v1/jobs/{id}/compilation-result|readAuthorCompilationResult|-|AuthorCompilationResponse|200|21|读取异步作者编译的固定结果
POST|/api/v1/drafts/{id}/validation-jobs|validateAuthorDraft|AuthorValidationJobRequest|JobResponse|202|21|接受固定作者配置的正式试样验证
POST|/api/v1/drafts/{id}/source-revision-jobs|saveAuthorSourceRevision|AuthorSourceRevisionRequest|AuthorSourceRevisionResponse|200|21|固定作者源码修订
POST|/api/v1/drafts/{id}/compilation-jobs|compileAuthorDraft|AuthorCompilationRequest|AuthorCompilationResponse|200|21|同步编译返回诊断或接受固定源码异步编译任务
POST|/api/v1/drafts/{id}/adopt-source-revision|adoptAuthorSourceRevision|AuthorAdoptSourceRevisionRequest|AuthorAdoptSourceRevisionResponse|200|21|显式采用作者源码修订
DELETE|/api/v1/drafts/{id}|deleteAuthorDraft|AuthorDraftDeleteRequest|AuthorDraftDeleteResponse|200|21|删除作者草稿并释放固定包
GET|/api/v1/script-configurations|scriptConfigurationList|-|ScriptConfigurationListResponse|200|21|读取个人配置库及固定修订
GET|/api/v1/script-configurations/{id}|scriptConfigurationDetail|-|ScriptConfigurationDetailResponse|200|21|读取精确库修订的完整配置与包声明
POST|/api/v1/script-configurations/commands|scriptConfigurationCommand|ScriptConfigurationCommandRequest|ScriptConfigurationCommandResponse|200|21|提交独立个人配置库的严格CAS命令
""".strip()
ALIASES = {
    ("GET", "/health/live"): "/api/v1/health/live",
    ("GET", "/capabilities"): "/api/v1/capabilities",
    ("POST", "/assets"): "/api/v1/assets",
    ("POST", "/project-sessions"): "/api/v1/project-sessions",
    ("GET", "/project-sessions/{id}"): "/api/v1/project-sessions/{id}",
}
MIMES = VIDEO_MIMES + ["application/json", "application/octet-stream", "text/plain", "text/lrc", "text/lyrics", "application/ttml+xml", "audio/aac", "audio/flac", "audio/m4a", "audio/mp4", "audio/mpeg", "audio/ogg", "audio/wav", "audio/x-flac", "audio/x-wav"]


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def object_schema(properties, required=None, strict=False):
    result = {"type": "object", "properties": properties, "required": list(properties) if required is None else required}
    if strict:
        result["additionalProperties"] = False
    return result


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()


def sha(data):
    return hashlib.sha256(data).hexdigest()


def generate(metadata):
    schemas = {}
    integers = {"Int32": (-2147483648, 2147483647, "int32"), "Int64": (-9223372036854775808, 9223372036854775807, "int64"), "Byte": (0, 255, "int32")}

    def schema_for(name, mode="response"):
        if name.endswith("[]"):
            return {"type": "array", "items": schema_for(name[:-2], mode)}
        if name == "Dictionary`2":
            return {"type": "object", "additionalProperties": {"type": "string"}}
        if name in integers:
            lo, hi, fmt = integers[name]
            return {"type": "integer", "format": fmt, "minimum": lo, "maximum": hi}
        if name in ("String", "Boolean", "DateTimeOffset", "Guid", "Double", "Single"):
            return {"String": {"type": "string"}, "Boolean": {"type": "boolean"}, "DateTimeOffset": {"type": "string", "format": "date-time"}, "Guid": {"type": "string", "format": "uuid"}, "Double": {"type": "number", "format": "double"}, "Single": {"type": "number", "format": "float"}}[name]
        if name == "JsonElement":
            return {}  # Exact JSON declaration; only reviewed public package projections use this type.
        info = metadata[name]  # Fail on new/unreviewed CLR shapes.
        if info["kind"] == "enum":
            schemas[name] = {"type": "string", "enum": info["values"]}
            return ref(name)
        component = name + ("Raw" if mode == "raw" else "Input" if mode == "request" and not name.endswith("Request") else "")
        if component in schemas:
            return ref(component)
        schemas[component] = {}
        properties, required = {}, []
        for prop in info["properties"]:
            value = schema_for(prop["type"], mode)
            nullable = prop["nullable"]
            # Ordinary wire omits null. Raw registered reports retain it unless explicitly annotated.
            if nullable and (mode == "request" or mode == "raw" and not prop["omitNull"]):
                value = {"anyOf": [value, {"type": "null"}]}
            if mode == "request" and prop["optional"] and prop["defaultValue"] is not None:
                value["default"] = prop["defaultValue"]
            properties[prop["name"]] = value
            if (mode == "request" and not prop["optional"] and not nullable
                    or mode == "response" and not nullable
                    or mode == "raw" and not prop["omitNull"]):
                required.append(prop["name"])
        schemas[component] = object_schema(properties, required, mode == "request" and info["strict"])
        schemas[component]["x-clr-type"] = "MakoKaraGenerator.Backend.Contracts." + name
        return ref(component)

    operations = []
    for row in OPERATIONS.splitlines():
        methods, path, opid, request, response, status, section, summary = row.split("|")
        for method in methods.split(","):
            operations.append(dict(method=method, path=path, operationId=opid + ("Head" if method == "HEAD" else ""), request=request, response=response, status=status, section=section, summary=summary))
    for (method, alias), canonical in ALIASES.items():
        item = copy.deepcopy(next(o for o in operations if o["method"] == method and o["path"] == canonical))
        item.update(path=alias, alias=canonical, operationId=item["operationId"] + "Alias")
        operations.append(item)

    # Anonymous endpoint envelopes and raw Core codec are not Contracts records.
    schemas["FileRootsResponse"] = object_schema({"rootGrantIds": {"type": "array", "items": {"type": "string"}}, "protocolVersion": {"type": "string", "const": "1"}})
    schemas["RecoveryCandidatesResponse"] = object_schema({"candidates": {"type": "array", "items": schema_for("RecoveryCandidateResponse")}, "protocolVersion": {"type": "string", "const": "1"}})
    schemas["RecoveryJobsResponse"] = object_schema({"jobs": {"type": "array", "items": schema_for("RecoveredJobResponse")}, "protocolVersion": {"type": "string", "const": "1"}})
    for name in ("ValidationReportDto", "ExportReportDto", "ExportFailureReportDto"):
        schema_for(name, "raw")
    schemas["ExportFailureReportDtoRaw"]["properties"]["kind"] = {"type": "string", "const": "exportFailure"}
    schemas["ExportResultReport"] = {"oneOf": [ref("ExportReportDtoRaw"), ref("ExportFailureReportDtoRaw")], "description": "成功报告或kind=exportFailure的失败报告；HTTP 200不代表导出成功。"}
    schemas["AudioAnalysisPoint"] = object_schema({"timeUs": {"type": "integer", "minimum": 0}, "bassLevel": {"type": "number"}, "pulse": {"type": "number"}, "beatPhase": {"type": ["number", "null"]}, "beatConfidence": {"type": "number"}}, strict=True)
    schemas["AudioAnalysisCanonical"] = object_schema({
        "version": {"type": "integer", "const": 1}, "algorithm": {"type": "string", "const": "bass-pulse-rbj-v1"},
        "sampleRate": {"type": "integer", "const": 48000}, "hopSamples": {"type": "integer", "const": 960},
        "originUs": {"type": "integer", "const": 0}, "sourceHash": {"type": "string"}, "pcmHash": {"type": "string"},
        "sampleCount": {"type": "integer", "minimum": 1, "maximum": 14400000}, "durationUs": {"type": "integer", "minimum": 1, "maximum": 300000000},
        "revision": {"type": "string"}, "points": {"type": "array", "maxItems": 15000, "items": ref("AudioAnalysisPoint")},
    }, strict=True)
    schemas["AudioAnalysisCanonical"]["description"] = "原Core AudioAnalysisJson字节；整数是number，beatPhase可显式null。网格、浮点、revision与canonical字节约束另见行为§12.3，不能仅凭schema判定完整性。"
    for name in ("BackendErrorResponse", "BackendEventResponse", "ClientEventsResponse"):
        schema_for(name)

    parameters = {
        "HostInstance": {"name": "X-Mako-Host-Instance-Id", "in": "header", "required": False, "schema": {"type": "string"}, "description": "读取时可选，推荐始终发送；写入身份另由必填metadata校验。旧access可能先401，不能只等待409。"},
        "Cursor": {"name": "cursor", "in": "query", "schema": {"type": "string"}, "description": "服务返回的不透明诊断游标；不可跨报告/代次。"},
        "Limit": {"name": "limit", "in": "query", "schema": {"type": "integer", "minimum": 1, "maximum": 128, "default": 128}},
    }
    for header in ("Range", "If-None-Match", "If-Range"):
        parameters[header] = {"name": header, "in": "header", "schema": {"type": "string"}, "description": "单段Range、条件读取及优先级见行为§1.4/§5。"}
    response_headers = {h: {"schema": {"type": t}} for h, t in [("ETag", "string"), ("Accept-Ranges", "string"), ("Content-Length", "integer"), ("Content-Range", "string")]}
    error_response = {"description": "统一BackendErrorResponse。HTTP状态/code映射及端点主要拒绝见行为§1.4与对应章节；不是RFC Problem Details。已开始的SSE/字节流若中断不保证还能返回JSON。", "content": {"application/problem+json": {"schema": ref("BackendErrorResponse")}}}
    paths = {}
    for item in operations:
        method, path, request, response = (item[k] for k in ("method", "path", "request", "response"))
        op = {"operationId": item["operationId"], "summary": item["summary"], "tags": ["section-" + item["section"]],
              "description": "执行行为见 api-contract.md 及 ../docs/architecture/backend-api.md §" + item["section"] + "。",
              "x-behavior-section": item["section"], "parameters": [], "responses": {"default": copy.deepcopy(error_response)}}
        if item.get("alias"):
            op["x-canonical-path"] = item["alias"]
            op["description"] += " 兼容别名；新客户端使用对应/api/v1路径，认证/回执相同。"
        for path_parameter in re.findall(r"\{([^{}]+)\}", path):
            parameter_schema = {"type": "string", "minLength": 1}
            if path.startswith("/api/v1/drafts/") and path_parameter == "id":
                parameter_schema["pattern"] = r"^draft-[0-9a-f]{32}$"
            op["parameters"].append({"name": path_parameter, "in": "path", "required": True, "schema": parameter_schema, "description": "采用服务返回的实际身份；不可由数组下标或本机路径推导。"})
        if path.endswith("health/live"):
            op["security"] = []
        elif path == "/api/v1/clients":
            op["security"] = [{"BootstrapBearer": []}]
        else:
            op["parameters"].append({"$ref": "#/components/parameters/HostInstance"})
        if path.endswith("lyrics/lines"):
            op["parameters"].append({"name": "branchId", "in": "query", "required": True, "schema": {"type": "string", "minLength": 1}})
        if item["operationId"] in ("templateList", "templateDetail"):
            op["parameters"] += template_query_parameters(detail=item["operationId"] == "templateDetail")
            op["x-template-library-limits"] = copy.deepcopy(TEMPLATE_LIMITS)
        if item["operationId"] in ("lyricBranches", "lyricsWindow", "projectSettings", "projectOrchestration", "presetApplications"):
            op["parameters"].append({"name": "expectedRevision", "in": "query", "required": True,
                                     "schema": {"type": "string", "pattern": r"^[0-9]+$", "x-int64-string": True},
                                     "description": "精确工程修订；不匹配返回409 revisionConflict。重复查询参数返回400。"})
            if item["operationId"] in ("lyricBranches", "lyricsWindow"):
                op["parameters"] += [
                    {"name": "offset", "in": "query", "schema": {"type": "integer", "minimum": 0, "maximum": 2147483647, "default": 0},
                     "description": "稳定保存顺序中的起点，允许等于totalCount；大于totalCount拒绝。"},
                    {"name": "limit", "in": "query", "schema": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}},
                ]
            if item["operationId"] == "lyricsWindow":
                op["parameters"].append({"name": "branchId", "in": "query", "required": True, "schema": {"type": "string", "minLength": 1}})
            op["x-read-model-limits"] = {"maxResponseBytes": 1048576, "maxItems": 20000,
                                         "description": "完整UTF-8 JSON envelope最多1MiB，来源/行/元素/注音组/注音片段合计最多20000条。超限413 resourceTooLarge，不截断单行；预算内也不混合修订。"}
            if item["operationId"] == "projectOrchestration":
                op["x-read-model-limits"]["description"] = "完整UTF-8 JSON envelope最多1MiB，编排nodes/programs合计最多20000个JSON值。超限413 resourceTooLarge，不截断。"
            if item["operationId"] == "presetApplications":
                op["x-read-model-limits"]["description"] = "完整UTF-8 JSON envelope最多1MiB，预设应用与branchInputs合计最多20000项。超限413 resourceTooLarge，不截断。"
        if item["operationId"] in ("projectGeometryReference", "projectLineLocalTransform", "projectLinePresentation"):
            ids = ["nodeId"] if item["operationId"] == "projectGeometryReference" else ["branchId", "lineId"]
            op["parameters"] += [{"name": "expectedRevision", "in": "query", "required": True,
                "schema": {"type": "string", "pattern": r"^[0-9]+$", "x-int64-string": True}}]
            op["parameters"] += [{"name": name, "in": "query", "required": True,
                "schema": {"type": "string", "minLength": 1, "pattern": r"\S"}} for name in ids]
            op["x-query-policy"] = {"allowUnknown": False, "allRequiredExactlyOnce": True,
                "caseSensitiveNames": True, "required": ["expectedRevision", *ids]}
            op["x-read-model-limits"] = {"maxResponseBytes": 1048576, "maxItems": 20000}
            op["description"] += " 所有query唯一必填，未知字段拒绝；精确修订不匹配409 revisionConflict。"
            if item["operationId"] == "projectGeometryReference":
                op["x-capability"] = "project-sessions.orchestration.geometry-reference.read"
                op["x-query-schema"] = ref("ProjectGeometryReferenceQuery")
                op["x-read-model-limits"].update(maxNodes=1024, maxDepth=64, maxInputItems=20000,
                    maxInputIdBytes=65536, maxTargetIdBytes=1024)
                op["description"] += " fixedContainerReferenceV1，不含文字测量、自动布局、动画、效果或最终像素；finalPixelGeometry固定false。"
            elif item["operationId"] == "projectLineLocalTransform":
                op["x-capability"] = "project-sessions.orchestration.line-local-transform.read"
                op["x-query-schema"] = ref("ProjectLineLocalTransformQuery")
                op["description"] += " transform始终出现，对象或显式null；目标不存在422 TargetStale。"
            else:
                op["x-capability"] = "project-sessions.orchestration.line-presentation.read"
                op["x-query-schema"] = ref("ProjectLinePresentationQuery")
                op["description"] += " localTransform/localEnterAnimation/effectiveEnterAnimation始终出现，可显式null；animationSource为line/project/none。"
        if item["operationId"] == "measureOrchestration":
            op["x-capability"] = "orchestration.measurements.scene-measurement-v1"
            op["x-query-policy"] = {"allowUnknown": False, "allowed": []}
            op["x-measurement-limits"] = {"maxResponseBytes": 2097152, "maxWorkerInputBytes": 4194304,
                "maxSourceLines": 2048, "maxDefinitionItems": 4096, "maxTextCharacters": 200000,
                "maxLayoutSamples": 512, "maxElements": 4096, "maxCorners": 20000,
                "maxNodes": 1024, "maxDepth": 64, "globalConcurrency": 2, "clientConcurrency": 1, "deadlineSeconds": 20}
            op["description"] += " 只读，无metadata/回执/job，不创建preview资源；同一次真实Worker采样返回geometry与base64 PNG。layout代表试样，time为指定时间。候选测量须同时提供非null的previewId/expectedPreviewGeneration/candidateRevision并匹配同一owner、工程、generation与固定候选；基础expectedRevision仍核对当前工程。完整超限413，不截断。"

        if item["operationId"] == "fonts":
            op["parameters"].append({"name": "family", "in": "query", "required": False,
                "schema": {"type": "string"}, "description": "可选且最多出现一次，仅作提示；返回完整实际Worker环境目录，不按family过滤。"})
        if item["operationId"] in ("planLyricsImport", "inspectFonts"):
            op["x-query-policy"] = {"allowUnknown": False, "allowed": []}
            op["description"] += " 只读，不创建回执或修改工程；不接受query。"
        if item["operationId"] == "createEditPreview":
            op["description"] += " 固定基础修订后复用单命令解释器创建只读候选，不改变工程/history；替换须匹配previewId与expectedPreviewGeneration，逐编辑反馈及candidateRevision沿现有preview会话返回。"

        if item["operationId"] == "projectWaveform":
            op["description"] += " 不接受任何查询参数，包括expectedRevision；不可用返回available=false和明确reason，不以空peaks冒充静音。"
            op["x-query-policy"] = {"allowUnknown": False, "allowed": []}
        if item["operationId"] == "projectStyle":
            op["parameters"] += style_query_parameters()
            op["x-query-policy"] = {"allowUnknown": False, "allPresentExactlyOnce": True,
                "caseSensitiveNames": True, "required": ["expectedRevision", "kind"],
                "idsByKind": {"project": [], "branch": ["branchId"], "line": ["branchId", "lineId"], "token": ["branchId", "lineId", "tokenId"]}}
            op["x-read-model-limits"] = {"maxResponseBytes": 1048576, "maxItems": 20000,
                "description": "完整UTF-8 JSON及原始局部值预算；不截断旧渐变或字体。新写入长度限额不套旧数据读取。"}
            op["description"] += " 必须提供精确expectedRevision及kind，ID组合严格随kind；不混合修订。staticHierarchy仅解析工程→来源→行→元素，hasPresetApplications提示其他预设层，不代表实际帧最终外观。"
        if item["operationId"] in ("projectWaveformPeaks", "projectWaveformPeaksHead"):
            op["parameters"] += [
                {"name": "waveformId", "in": "query", "required": True, "schema": {"type": "string", "minLength": 1, "pattern": r"\S"}},
                *[{"name": name, "in": "query", "required": True, "schema": {"type": "integer", "format": "int32", "minimum": minimum, "maximum": maximum}}
                  for name, minimum, maximum in (("level", 0, 2147483647), ("start", 0, 2147483647), ("count", 1, 8192))],
                {"$ref": "#/components/parameters/If-None-Match"},
            ]
            op["x-query-policy"] = {"allowUnknown": False, "allRequiredExactlyOnce": True,
                "caseSensitiveNames": True, "integerSyntax": "ASCII decimal digits only; no sign/whitespace; Int32 range", "maxWindowBuckets": 8192}
            op["description"] += " 四查询字段均唯一必填；未知/重复/缺失字段和窗口越过层末返回400 invalidRequest。Range一律400 invalidRequest，field=Range；不支持206/416。身份/归属检查先于304，旧waveformId为404。HEAD所有状态无body。"
        if item["operationId"] in ("runtimeInfo", "createPreview", "updatePreviewInput", "previewDiagnostics"):
            op["x-package-preview-profile"] = "package-lyrics-scene-v1"
            op["x-package-preview-limits"] = copy.deepcopy(PACKAGE_PREVIEW_LIMITS)
            op["description"] += " 0.98直接效果包有限项目预览与完整诊断见行为§20；实际Worker能力门禁，不扩大B4/预设/媒体字体范围。"
        configure_project_image_preview_operation(op, item)
        configure_project_image_validation_operation(op, item)
        configure_project_image_export_operation(op, item)
        configure_p1p2_runtime(op, item)
        configure_p3_workshop(op, item)
        configure_basic_operation(op, item)
        if path.endswith("diagnostics"):
            op["parameters"] += [{"$ref": "#/components/parameters/" + x} for x in ("Cursor", "Limit")]
        if response == "sse":
            op["parameters"] += [{"name": "after", "in": "query", "schema": {"type": "string"}}, {"name": "Last-Event-ID", "in": "header", "schema": {"type": "string"}, "description": "优先于after；保留eventCursor或eventSeq。"}]
            success = {"description": "SSE流。正常data=BackendEventResponse，resync.required=ClientEventsResponse，client.expired=BackendErrorResponse。心跳注释不续租。", "content": {"text/event-stream": {"schema": {"type": "string"}}}}
            op["x-sse-data-schemas"] = {"event": ref("BackendEventResponse"), "resync.required": ref("ClientEventsResponse"), "client.expired": ref("BackendErrorResponse")}
        elif response == "packageArchive":
            success = {"description": "认证读取精确修订的完整.mkfx包；下载结束或断开释放临时流与并发名额。",
                "headers": {"Content-Disposition": {"schema": {"type": "string"}}, "Content-Length": {"schema": {"type": "integer", "minimum": 1}}},
                "content": {"application/octet-stream": {"schema": {"type": "string", "format": "binary"}}}}
        elif response == "waveformPeaks":
            headers = {h: copy.deepcopy(response_headers[h]) for h in ("ETag", "Content-Length")}
            headers["Content-Length"]["schema"].update(minimum=8, maximum=65536, multipleOf=8)
            binary = {"type": "string", "format": "binary", "x-waveform-format": "minmax-f32le-v1", "x-bytes-per-bucket": 8,
                      "description": "恰好8*count字节；逐桶little-endian float32 Min后Max，保留实际IEEE754位模式。不是JSON、PCM或浏览器播放音频。"}
            success = {"description": "完整请求窗口；HEAD保持对应GET Content-Length且无body。", "headers": headers}
            if method != "HEAD":
                success["content"] = {"application/octet-stream": {"schema": binary}}
            op["responses"]["304"] = {"description": "已认证且当前身份/窗口有效，If-None-Match命中，无body。", "headers": {"ETag": copy.deepcopy(headers["ETag"])}}
            for status in ("400", "404"):
                op["responses"][status] = copy.deepcopy(error_response) if method != "HEAD" else {"description": "错误状态沿GET；HEAD没有响应body。"}
            if method == "HEAD":
                op["responses"]["default"] = {"description": "所有错误状态沿GET，HEAD均无响应body。"}
        elif response in ("asset", "png", "video", "analysis", "lyricsContent", "templateArchive"):
            op["parameters"] += [{"$ref": "#/components/parameters/" + h} for h in ("Range", "If-None-Match", "If-Range")]
            headers = copy.deepcopy(response_headers)
            if response == "png":
                headers["X-Mako-Preview-Expires-At"] = {"schema": {"type": "string", "format": "date-time"}}
            if response in ("video", "analysis", "lyricsContent", "templateArchive"):
                headers["Content-Disposition"] = {"schema": {"type": "string"}}
            media = {"asset": ["*/*"], "png": ["image/png"], "video": ["video/mp4", "video/quicktime"], "analysis": ["application/json"], "lyricsContent": ["text/plain"], "templateArchive": ["application/octet-stream"]}[response]
            content = {m: {"schema": ref("AudioAnalysisCanonical") if response == "analysis" else {"type": "string", "format": "binary"}} for m in media}
            success = {"description": "完整不可变内容；必须认证。HEAD无body。", "headers": headers}
            if method != "HEAD":
                success["content"] = content
            partial = {"description": "单段部分字节；analysis片段不保证是完整JSON。HEAD无body。", "headers": headers}
            if method != "HEAD":
                partial["content"] = {m: {"schema": {"type": "string", "format": "binary"}} for m in media}
            op["responses"].update({"206": partial, "304": {"description": "If-None-Match命中，无body。", "headers": headers}, "416": {"description": "无效/多段Range，无body；Content-Range: bytes */size。", "headers": headers}})
            if method == "HEAD":
                op["responses"]["default"] = {"description": "错误状态沿GET，但HEAD传输没有响应body。"}
        else:
            if response not in schemas:
                schema_for(response)
            success = {"description": "已接受，须读取job/preview权威终态。" if item["status"] == "202" else "成功；任务state及失败报告仍须检查。", "content": {"application/json": {"schema": ref(response)}}}
        op["responses"][item["status"]] = success
        # P4 compilation has two explicit successful statuses; configure after
        # installing the ordinary response so sync200 and async202 coexist.
        configure_p4_author(op, item)
        configure_render_backends(op, item)
        configure_w8_authoring(op, item)
        if request == "upload":
            for h in ("X-Mako-Request-Id", "X-Mako-Host-Instance-Id", "X-Mako-Valid-Until-Utc"):
                op["parameters"] = [p for p in op["parameters"] if not (h == "X-Mako-Host-Instance-Id" and p.get("$ref", "").endswith("/HostInstance"))]
                op["parameters"].append({"name": h, "in": "header", "required": True, "schema": {"type": "string", **({"format": "date-time"} if h.endswith("Utc") else {})}})
            op["parameters"] += [{"name": "fileName", "in": "query", "schema": {"type": "string"}, "description": "原始上传的安全文件名。中文名使用URLSearchParams编码，避免Fetch头ByteString限制。"}, {"name": "X-Mako-Asset-Name", "in": "header", "schema": {"type": "string"}, "description": "原始上传文件名备选；query优先。multipart也先取query/header，缺省才取文件part的filename。"}]
            content = {mime: {"schema": {"type": "string", "format": "binary"}} for mime in MIMES}
            content["multipart/form-data"] = {"schema": {"type": "object", "additionalProperties": {"type": "string", "format": "binary"}}, "encoding": {}}
            op["requestBody"] = {"required": True, "description": "原始非空bytes须提供fileName或X-Mako-Asset-Name；multipart必须恰好一个文件，字段名任意（推荐file），filename优先query、其次header、最后文件part。该跨header/query/part门禁不由JSON Schema单独表达。实际MIME与字节配额读取capabilities。", "content": content}
            op["x-receipt-location"] = "headers"
        elif request != "-":
            body_schema = schema_for(request, "request")
            op["requestBody"] = {"required": True, "content": {"application/json": {"schema": body_schema}}}
            op["x-receipt-location"] = "none" if request in ("ClientRegistrationRequest", "SceneMeasurementRequest", "LyricsImportPlanRequest", "FontInspectRequest", "EffectPackageInspectRequest", "TemplatePreviewOptionsRequest") else "body" if request in ("ProjectCommandRequest", "RequestMetadataDto") else "metadata"
        else:
            op["x-receipt-location"] = "none"
        paths.setdefault(path, {})[method.lower()] = op

    # The nullable DTO is a wire discriminated response, not a partially populated audio payload.
    waveform = schemas["ProjectWaveformResponse"]
    payload = ["waveformId", "sourceSha256", "profile", "sampleRate", "sampleCount", "durationUs", "sourceChannels",
               "waveformChannels", "baseBucketSamples", "format", "levels"]
    if set(waveform["properties"]) != {"projectSessionId", "available", "reason", "protocolVersion", *payload}:
        raise ValueError("Unreviewed waveform metadata fields")
    common = {name: copy.deepcopy(waveform["properties"][name]) for name in ("projectSessionId", "available", "protocolVersion")}
    available = {**copy.deepcopy(common), **{name: copy.deepcopy(waveform["properties"][name]) for name in payload}}
    available["available"] = {"type": "boolean", "const": True}
    available["waveformId"].update(minLength=1)
    available["profile"]["const"] = "self-contained-native-waveform-v1"
    available["format"]["const"] = "minmax-f32le-v1"
    available["waveformChannels"]["const"] = 1
    for name in ("sampleRate", "sourceChannels", "baseBucketSamples"):
        available[name]["minimum"] = 1
    schemas["ProjectWaveformAvailable"] = object_schema(available, strict=True)
    unavailable = {**common, "reason": {"type": "string", "enum": ["noAudio", "cacheBudgetExceeded", "audioIdentityChanged"]}}
    unavailable["available"] = {"type": "boolean", "const": False}
    schemas["ProjectWaveformUnavailable"] = object_schema(unavailable, strict=True)
    schemas["ProjectWaveformResponse"] = {"oneOf": [ref("ProjectWaveformAvailable"), ref("ProjectWaveformUnavailable")],
        "x-clr-type": waveform["x-clr-type"],
        "description": "available=true必须完整提供音频与层级字段且省略reason；false仅提供projectSessionId/available/reason/protocolVersion。身份绑定原音频SHA、实际准备profile与峰值位模式，不绑定歌词修订；不保证浏览器播放器逐sample对齐。",
        "x-waveform-cache": {"maxRetainedPayloadBytes": 67108864, "description": "整个Host已保留的flat峰值payload预算；不是整个托管堆、临时prepare、窗口响应或元数据总和。缓存不足不阻断原create/open。"}}
    schemas["WaveformLevelDto"]["properties"]["level"]["minimum"] = 0
    schemas["WaveformLevelDto"]["properties"]["bucketCount"]["minimum"] = 0
    # Semantic overlays: nullable CLR transport slots can still be mandatory HTTP input.
    def required_property(component, prop, value=None):
        item = schemas[component]
        if prop not in item["required"]:
            item["required"].append(prop)
        if value is not None:
            item["properties"][prop] = value
        elif "anyOf" in item["properties"][prop]:
            item["properties"][prop] = item["properties"][prop]["anyOf"][0]

    for name, value in list(schemas.items()):
        if name.endswith("Request") and "metadata" in value.get("properties", {}):
            required_property(name, "metadata", ref("RequestMetadataDtoInput"))
    # The configuration command envelope is a closed wire object; its
    # discriminated command union owns all variant fields.
    if "ScriptConfigurationCommandRequest" in schemas:
        schemas["ScriptConfigurationCommandRequest"]["additionalProperties"] = False
    # Author workflow identities have stable wire forms.  These patterns mirror
    # the persistence guards; package/export identifiers remain opaque because
    # their accepted forms are owned by the package contract.
    if "AuthorDraftCreateRequest" in schemas:
        schemas["AuthorDraftCreateRequest"]["properties"]["packageAssetId"].update(minLength=1) if schemas["AuthorDraftCreateRequest"]["properties"].get("packageAssetId", {}).get("type") != "null" else None
        schemas["AuthorDraftCreateRequest"]["properties"]["exportId"].update(minLength=1)
        schemas["AuthorDraftCreateRequest"]["properties"]["name"].update(minLength=1, maxLength=256)
    revision = {"type": "string", "pattern": r"^sha256:[0-9a-f]{64}$"}
    draft_id = {"type": "string", "pattern": r"^draft-[0-9a-f]{32}$"}
    for name in ("AuthorSourceRevisionRequest", "AuthorCompilationRequest", "AuthorDraftDeleteRequest", "AuthorAdoptSourceRevisionRequest"):
        if name in schemas:
            schemas[name]["properties"]["expectedDraftRevision"] = copy.deepcopy(revision)
    if "AuthorAdoptSourceRevisionRequest" in schemas:
        schemas["AuthorAdoptSourceRevisionRequest"]["properties"]["sourceRevision"] = copy.deepcopy(revision)
    if "AuthorSourceRevisionRequest" in schemas:
        files = schemas["AuthorSourceRevisionRequest"]["properties"]["files"]
        files.update(minItems=1, maxItems=256)
        file_schema = schemas["AuthorSourceFileDtoInput"]
        file_schema["properties"]["path"].update(minLength=1, pattern=r"\S")
    for name in ("AuthorDraftResponse", "AuthorSourceRevisionResponse", "AuthorCompilationResponse", "AuthorAdoptSourceRevisionResponse", "AuthorDraftDeleteResponse"):
        if name not in schemas:
            continue
        for field in ("draftId",):
            if field in schemas[name]["properties"]:
                schemas[name]["properties"][field] = copy.deepcopy(draft_id)
        for field in ("draftRevision", "baseSourceRevision", "currentSourceRevision", "sourceRevision", "previousSourceRevision", "adoptedSourceRevision"):
            if field in schemas[name]["properties"]:
                schemas[name]["properties"][field] = ({"anyOf": [copy.deepcopy(revision), {"type": "null"}]}
                                                       if field == "adoptedSourceRevision" else copy.deepcopy(revision))
    for component in ("RequestMetadataDtoInput", "ProjectCommandRequest"):
        for prop in ("requestId", "hostInstanceId", "validUntilUtc"):
            required_property(component, prop)
        schemas[component]["properties"]["requestId"].update(minLength=1, maxLength=128)
        schemas[component]["properties"]["hostInstanceId"]["minLength"] = 1
    base_command = schemas.pop("ProjectCommandDtoInput")
    variants = []
    for typ, component, fields in [
        ("lyrics.setTimeRange", "SetTimeRangeCommand", ["target", "startUs", "endUs"]),
        ("history.undo", "UndoCommand", []), ("history.redo", "RedoCommand", []),
        ("audio.analysis.apply", "ApplyAnalysisCommand", ["analysisJobId"]),
        ("audio.analysis.clear", "ClearAnalysisCommand", []),
    ]:
        props = {"type": {"type": "string", "const": typ}}
        # The server tolerates explicit null for irrelevant known nullable slots.
        for prop, value in base_command["properties"].items():
            if prop == "type":
                continue
            props[prop] = value["anyOf"][0] if prop in fields else {"type": "null"}
        schemas[component] = object_schema(props, ["type"] + fields, strict=True)
        variants.append(ref(component))
    # The custom converter exposes only the named fields, not every inherited CLR slot.
    # Audit the complete reflected shape before narrowing it to each public wire branch.
    typed_commands = [
        ("lyrics.stampTokenStart", "StampTokenStartCommandDto", ["target", "playheadUs"]),
        ("lyrics.stampTokenEnd", "StampTokenEndCommandDto", ["target", "playheadUs"]),
        ("lyrics.clearTokenTime", "ClearTokenTimeCommandDto", ["target"]),
        ("lyrics.nudgeTokenTime", "NudgeTokenTimeCommandDto", ["target", "frameDelta"]),
        ("lyrics.shiftLine", "ShiftLineCommandDto", ["target", "deltaUs"]),
        ("lyrics.setTokenTimes", "SetTokenTimesCommandDto", ["changes"]),
        ("lyrics.setLineText", "SetLineTextCommandDto", ["target", "text"]),
        ("lyrics.setRubyText", "SetRubyTextCommandDto", ["target", "text"]),
        ("lyrics.addLine", "AddLineCommandDto", ["branchId", "afterLineId"]),
        ("lyrics.duplicateLine", "DuplicateLineCommandDto", ["target"]),
        ("lyrics.removeLine", "RemoveLineCommandDto", ["target"]),
        ("composition.setCanvasSize", "SetCanvasSizeCommandDto", ["width", "height"]),
        ("composition.setFrameRate", "SetFrameRateCommandDto", ["numerator", "denominator"]),
        ("composition.setBackgroundColor", "SetBackgroundColorCommandDto", ["red", "green", "blue", "alpha"]),
        ("composition.setOutputDuration", "SetOutputDurationCommandDto", ["durationUs"]),
        ("composition.useLyricsEnd", "UseLyricsEndCommandDto", []),
        ("composition.useAudioDuration", "UseAudioDurationCommandDto", []),
        ("lyrics.addTranslationBranch", "AddTranslationBranchCommandDto", ["name", "language"]),
        ("lyrics.configureBranch", "ConfigureBranchCommandDto", ["branchId", "name", "language", "role", "timingMode"]),
        ("lyrics.removeBranch", "RemoveBranchCommandDto", ["branchId"]),
        ("lyrics.linkPrimaryLine", "LinkPrimaryLineCommandDto", ["branchId", "lineId", "primaryLineId"]),
        ("orchestration.setDefaultContainer", "SetOrchestrationDefaultContainerCommandDto", ["branchId", "containerId"]),
        ("orchestration.setLayoutDefaultPlacement", "SetOrchestrationLayoutDefaultPlacementCommandDto", ["containerId", "placement"]),
        ("orchestration.setNode", "SetOrchestrationNodeCommandDto", ["nodeId", "name"]),
        ("orchestration.reparent", "ReparentOrchestrationNodeCommandDto", ["nodeId", "parentId"]),
        ("orchestration.reorderChildren", "ReorderOrchestrationChildrenCommandDto", ["parentId", "orderedIds"]),
        ("orchestration.addSlot", "AddOrchestrationSlotCommandDto", ["containerId", "slotId", "name"]),
        ("orchestration.removeNode", "RemoveOrchestrationNodeCommandDto", ["nodeId"]),
        ("orchestration.reorderSlots", "ReorderOrchestrationSlotsCommandDto", ["containerId", "orderedIds"]),
        ("orchestration.createClip", "CreateOrchestrationClipCommandDto", ["branchId", "clipId", "name", "lineIds", "containerId"]),
        ("orchestration.renameClip", "RenameOrchestrationClipCommandDto", ["branchId", "clipId", "name"]),
        ("orchestration.setClipContainer", "SetOrchestrationClipContainerCommandDto", ["branchId", "clipId", "containerId"]),
        ("orchestration.deleteClip", "DeleteOrchestrationClipCommandDto", ["branchId", "clipId"]),
        ("orchestration.previewBasicLayout", "BasicLayoutCommandDto", ["branchId", "preset", "selectedLineIds", "layoutName", "slotNames", "selectedSegmentName", "retainedSegmentName"]),
        ("orchestration.applyBasicLayout", "BasicLayoutCommandDto", ["branchId", "preset", "selectedLineIds", "layoutName", "slotNames", "selectedSegmentName", "retainedSegmentName"]),
        ("lyrics.splitToken", "SplitTokenCommandDto", ["target"]),
        ("lyrics.mergeToken", "MergeTokenCommandDto", ["target"]),
        ("lyrics.reorderToken", "ReorderTokenCommandDto", ["target", "destinationIndex", "reassignTimeSlots"]),
        ("lyrics.resplitLine", "ResplitLineCommandDto", ["target", "segments", "estimateTimes"]),
        ("lyrics.resplitBranch", "ResplitBranchCommandDto", ["branchId", "rule", "estimateTimes"]),
        ("lyrics.importAsset", "ImportAssetCommandDto", ["assetId", "destination", "options"]),
        ("lyrics.appendText", "AppendTextCommandDto", ["text", "destination"]),
        ("presetApplications.apply", "ApplyPresetApplicationCommandDto", ["templateLibraryId", "templateId", "templateRevision", "targets", "branchInputs"]),
        ("presetApplications.update", "UpdatePresetApplicationCommandDto", ["applicationId", "templateLibraryId", "templateId", "templateRevision", "branchInputs"]),
        ("presetApplications.setEnabled", "SetPresetApplicationEnabledCommandDto", ["applicationId", "enabled"]),
        ("presetApplications.remove", "RemovePresetApplicationCommandDto", ["applicationIds"]),
    ]
    token_target = copy.deepcopy(schemas["LyricTimingTargetDtoInput"])
    token_target["required"].append("tokenId")
    token_target["properties"]["tokenId"] = {"type": "string", "minLength": 1, "maxLength": 128}
    schemas["LyricTokenTimingTargetDtoInput"] = token_target
    line_target = copy.deepcopy(schemas["LyricTimingTargetDtoInput"])
    line_target["properties"]["tokenId"] = {"type": "null"}
    schemas["LyricLineTimingTargetDtoInput"] = line_target
    for source, component in (("LyricTokenTimingTargetDtoInput", "LyricTokenEditTargetDtoInput"),
                              ("LyricLineTimingTargetDtoInput", "LyricLineEditTargetDtoInput")):
        target = copy.deepcopy(schemas[source])
        for value in target["properties"].values():
            if value.get("type") == "string":
                value.update(minLength=1, maxLength=128, pattern=r"\S")
        schemas[component] = target
    # Destination and options use custom converters; audit CLR facts before describing their exact wire unions.
    if {p["name"] for p in metadata["LyricsImportDestinationDto"]["properties"]} != {"kind"}:
        raise ValueError("Unreviewed import destination base shape")
    if metadata["LyricsImportSplitRule"]["values"] != ["none", "mixed", "perTextElement", "latinWords", "byPunctuation", "byWhitespace"]:
        raise ValueError("Unreviewed import split rule names")
    destination_shapes = [
        ("ExistingLyricsDestinationDto", "existing", ["branchId"]),
        ("NewBranchLyricsDestinationDto", "newBranch", ["name", "role"]),
        ("NewTranslationLyricsDestinationDto", "newTranslation", ["name"]),
    ]
    for clr_name, kind, fields in destination_shapes:
        info = metadata[clr_name]
        if info.get("baseType") != "LyricsImportDestinationDto" or {p["name"] for p in info["properties"]} != {"kind", *fields}:
            raise ValueError(f"Unreviewed import destination shape: {clr_name}")
        expected_types = {"kind": "String", **{field: "ProjectLyricBranchRole" if field == "role" else "String" for field in fields}}
        if {p["name"]: p["type"] for p in info["properties"]} != expected_types:
            raise ValueError(f"Unreviewed import destination field types: {clr_name}")
        component = schema_for(clr_name, "request")["$ref"].split("/")[-1]
        props = schemas[component]["properties"]
        props["kind"] = {"type": "string", "const": kind}
        for field in fields:
            if "anyOf" in props[field]:
                props[field] = props[field]["anyOf"][0]
        if kind == "existing":
            props["branchId"].update(minLength=1, maxLength=128, pattern=r"\S")
        if kind == "newBranch":
            props["role"] = {"type": "string", "enum": ["translation", "annotation", "custom"]}
        schemas[component] = object_schema(props, ["kind"] + fields, strict=True)
        schemas[component]["x-clr-type"] = "MakoKaraGenerator.Backend.Contracts." + clr_name
    schemas["LyricsImportDestinationDtoInput"] = {"oneOf": [ref(name + "Input") for name, _, _ in destination_shapes]}
    schemas["ImportLyricsDestinationInput"] = {"oneOf": [ref("ExistingLyricsDestinationDtoInput"), ref("NewBranchLyricsDestinationDtoInput")]}
    schemas["AppendLyricsDestinationInput"] = {"oneOf": [ref("ExistingLyricsDestinationDtoInput"), ref("NewTranslationLyricsDestinationDtoInput")]}
    schema_for("LyricsImportOptionsDto", "request")
    if {p["name"] for p in metadata["LyricsImportOptionsDto"]["properties"]} != {"importerId", "encodingName", "splitRule", "estimateSplitTimes", "sourceBranchId"}:
        raise ValueError("Unreviewed import options shape")
    option_schema = schemas["LyricsImportOptionsDtoInput"]
    option_schema["properties"]["sourceBranchId"] = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
    option_schema["additionalProperties"] = False
    option_schema["properties"]["splitRule"].update(default="none")
    option_schema["properties"]["estimateSplitTimes"].update(default=False)
    option_schema["description"] = "options 可省略；出现时必须是对象。importerId/encodingName可省略或null，未知值沿原自动回退。splitRule/estimateSplitTimes可省略取none/false，显式null拒绝。sourceBranchId可省略但不可null；多分支项目TTML导入须明确选择源分支。"
    for typ, clr_name, fields in typed_commands:
        info = metadata[clr_name]
        actual = {p["name"] for p in info["properties"]}
        expected = set(base_command["properties"]) | set(fields)
        if clr_name == "BasicLayoutCommandDto":
            expected.add("preview")
        if info.get("baseType") != "ProjectCommandDto" or actual != expected:
            raise ValueError(f"Unreviewed command shape: {clr_name}: base={info.get('baseType')}, fields={sorted(actual)}")
        component = schema_for(clr_name, "request")["$ref"].split("/")[-1]
        generated = schemas[component]
        if clr_name == "BasicLayoutCommandDto":
            component = ("Preview" if typ.endswith("previewBasicLayout") else "Apply") + "BasicLayoutCommandInput"
        props = {"type": {"type": "string", "const": typ}}
        for field in fields:
            value = generated["properties"][field]
            keep_nullable = ((typ == "lyrics.addLine" and field == "afterLineId")
                             or (typ == "composition.setOutputDuration" and field == "durationUs")
                             or (typ in ("lyrics.addTranslationBranch", "lyrics.configureBranch") and field == "language")
                             or (typ == "lyrics.linkPrimaryLine" and field == "primaryLineId")
                             or (typ == "orchestration.setLayoutDefaultPlacement" and field == "placement"))
            props[field] = value["anyOf"][0] if "anyOf" in value and not keep_nullable else value
        if "target" in fields:
            if props["target"] != ref("LyricTimingTargetDtoInput"):
                raise ValueError(f"Unreviewed command target type: {clr_name}")
            line_command = typ in ("lyrics.shiftLine", "lyrics.setLineText", "lyrics.duplicateLine", "lyrics.removeLine")
            props["target"] = ref("LyricLineTimingTargetDtoInput" if line_command else "LyricTokenTimingTargetDtoInput")
            if typ in ("lyrics.splitToken", "lyrics.mergeToken", "lyrics.reorderToken", "lyrics.resplitLine"):
                props["target"] = ref("LyricLineEditTargetDtoInput" if typ == "lyrics.resplitLine" else "LyricTokenEditTargetDtoInput")
        if typ in ("lyrics.importAsset", "lyrics.appendText"):
            props["destination"] = ref("ImportLyricsDestinationInput" if typ == "lyrics.importAsset" else "AppendLyricsDestinationInput")
        if typ == "lyrics.importAsset":
            props["assetId"].update(minLength=1)
        if typ == "lyrics.setTokenTimes":
            props["changes"].update(minItems=1, maxItems=1000)
        if typ == "lyrics.addLine":
            props["branchId"].update(minLength=1, maxLength=128)
            props["afterLineId"]["anyOf"][0].update(minLength=1, maxLength=128)
        if typ == "composition.setCanvasSize":
            for field in ("width", "height"):
                props[field]["minimum"] = 1
        if typ == "composition.setFrameRate":
            for field in ("numerator", "denominator"):
                props[field]["minimum"] = 1
        if typ in ("lyrics.configureBranch", "lyrics.removeBranch", "lyrics.linkPrimaryLine"):
            for field in ("branchId", "lineId", "primaryLineId"):
                if field in props:
                    target = props[field]["anyOf"][0] if "anyOf" in props[field] else props[field]
                    target["pattern"] = r"\S"
                    target["description"] = "Core稳定ID原值，至少一个非空白字符；不新增128字符上限。"
        if typ == "lyrics.resplitBranch":
            props["branchId"].update(minLength=1, maxLength=128, pattern=r"\S")
        if typ == "lyrics.resplitLine":
            props["segments"]["maxItems"] = 20000
            props["segments"]["description"] = "必须显式提供字符串数组；空数组形状合法。超过20000段为413 command.segments；空段、拼接或Unicode边界无效为422 InvalidTarget。"
        if typ == "orchestration.setDefaultContainer":
            for field in ("branchId", "containerId"):
                if "anyOf" in props[field]:
                    props[field] = props[field]["anyOf"][0]
                props[field].update(minLength=1, maxLength=128, pattern=r"\S")
        if typ == "orchestration.setLayoutDefaultPlacement":
            props["containerId"].update(minLength=1, maxLength=128, pattern=r"\S")
            props["placement"] = {"anyOf": [ref("ProjectOrchestrationPlacementDtoInput"), {"type": "null"}],
                                   "description": "必须显式提供对象或null；null清除内部布局默认位置。"}
        if typ in ("orchestration.setNode", "orchestration.reparent", "orchestration.reorderChildren", "orchestration.addSlot", "orchestration.removeNode", "orchestration.reorderSlots"):
            stable_id = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
            for field in ("nodeId", "containerId", "slotId"):
                if field in props:
                    props[field] = copy.deepcopy(stable_id)
            if "parentId" in props:
                props["parentId"] = {"anyOf": [copy.deepcopy(stable_id), {"type": "null"}], "description": "必填；显式null指向树根层。"}
            if "name" in props:
                props["name"] = {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "必填；显式null清除名称。"}
            if "orderedIds" in props:
                props["orderedIds"] = {"type": "array", "minItems": 1, "maxItems": 2000, "uniqueItems": True, "items": copy.deepcopy(stable_id)}
        if typ in ("orchestration.createClip", "orchestration.renameClip", "orchestration.setClipContainer", "orchestration.deleteClip"):
            for field in ("branchId", "clipId", "containerId"):
                if field in props:
                    value = props[field]["anyOf"][0] if "anyOf" in props[field] else props[field]
                    props[field] = value
                    value.update(minLength=1, maxLength=128, pattern=r"\S")
            if typ == "orchestration.createClip":
                props["name"] = {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "可选片段名称；省略或显式null表示无名称。"}
                props["lineIds"] = {"type": "array", "minItems": 1, "maxItems": 1000, "uniqueItems": True,
                    "items": {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}}
            elif typ == "orchestration.renameClip":
                props["name"] = {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "必须显式提供名称；null清除名称。"}
        if typ in ("orchestration.previewBasicLayout", "orchestration.applyBasicLayout"):
            for field in ("branchId", "preset", "layoutName", "selectedSegmentName", "retainedSegmentName"):
                props[field] = {"type": "string", "minLength": 1, "pattern": r"\S"}
            props["selectedLineIds"] = {"anyOf": [{"type": "array", "minItems": 1, "items": {"type": "string", "minLength": 1, "pattern": r"\S"}}, {"type": "null"}], "description": "G19允许连续稳定行范围；省略或显式null表示整来源。"}
            props["slotNames"] = {"type": "array", "minItems": 1, "maxItems": 3, "items": {"type": "string", "minLength": 1, "pattern": r"\S"}}
        if typ in ("presetApplications.apply", "presetApplications.update"):
            for field in ("templateLibraryId", "templateId", "templateRevision"):
                props[field].update(minLength=1, pattern=r"\S")
            props["templateLibraryId"] = {"type": "string", "enum": ["builtin", "personal"]}
            if "branchInputs" in props:
                props["branchInputs"] = {"anyOf": [{"type": "object", "additionalProperties": {"type": "string"}}, {"type": "null"}]}
        if typ == "presetApplications.apply":
            props["targets"] = {"type": "array", "minItems": 1, "maxItems": 1000, "uniqueItems": True, "items": ref("PresetApplicationTargetDtoInput")}
        if typ == "presetApplications.update":
            props["applicationId"].update(minLength=1, pattern=r"\S")
        if typ == "presetApplications.setEnabled":
            props["applicationId"].update(minLength=1, pattern=r"\S")
            props["enabled"] = {"type": "boolean"}
        if typ == "presetApplications.remove":
            props["applicationIds"] = {"type": "array", "minItems": 1, "maxItems": 1000, "uniqueItems": True,
                "items": {"type": "string", "minLength": 1, "pattern": r"\S"}}
        required_fields = [field for field in fields if not (
            typ == "lyrics.addLine" and field == "afterLineId"
            or typ == "lyrics.addTranslationBranch" and field == "language"
            or typ == "lyrics.importAsset" and field == "options")]
        if typ in ("orchestration.previewBasicLayout", "orchestration.applyBasicLayout"):
            required_fields = [field for field in fields if field != "selectedLineIds"]
        if typ == "orchestration.createClip":
            required_fields = [field for field in fields if field != "name"]
        if typ in ("presetApplications.apply", "presetApplications.update"):
            required_fields = [field for field in fields if field != "branchInputs"]
        schemas[component] = object_schema(props, ["type"] + required_fields, strict=True)
        schemas[component]["x-clr-type"] = "MakoKaraGenerator.Backend.Contracts." + clr_name
        if typ in ("lyrics.splitToken", "lyrics.mergeToken", "lyrics.reorderToken", "lyrics.resplitLine", "lyrics.resplitBranch"):
            schemas[component]["x-token-editing-limits"] = {
                "maxSingleLineBytes": 1048576, "maxSingleLineItems": 20000,
                "maxPreparedBranchBytes": 16777216,
                "description": "提交前验证完整候选；来源操作累计改变行的完整单行投影最多16MiB，超限413且整批不变。不是字符截断或部分提交。",
            }
        variants.append(ref(component))
    report_fields = {"importerId", "importerName", "sourceFormat", "sourceLineCount", "lineCount", "tokenCount", "timedTokenCount", "missingTokenCount", "estimatedTokenCount", "skippedLineCount", "mergedContinuationCount", "derivedLineEndCount", "fallbackLineEndCount", "offsetUs", "encodingName", "hadByteOrderMark", "newLineKind", "issues", "detectionReason", "candidateNames", "isFormatAmbiguous", "discardedFeatures", "splitLineCount", "splitCreatedTokenCount", "splitEstimatedTokenCount", "issueCount", "maxSeverity"}
    if {p["name"] for p in metadata["LyricsImportReportDto"]["properties"]} != report_fields:
        raise ValueError("Unreviewed complete import report shape")
    schemas["ProjectCommandResponse"]["properties"]["importResult"]["description"] = "仅再次导入/粘贴追加成功时提供实际行数和选择；文件导入带完整27字段报告，追加省略report。报告允许原Error诊断与合法行同时存在，不能一概拒绝或截断。"
    schemas["LyricsImportCommandResultDto"]["properties"]["lineCount"]["minimum"] = 1
    for field in ("selectedBranchId", "selectedLineId"):
        schemas["LyricsImportCommandResultDto"]["properties"][field].update(minLength=1, maxLength=128, pattern=r"\S")
    for field in report_fields:
        if field.endswith("Count"):
            schemas["LyricsImportReportDto"]["properties"][field]["minimum"] = 0
    schemas["LyricsImportIssueDto"]["properties"]["sourceLineNumber"]["minimum"] = 0
    for command in ("ImportAssetCommandDtoInput", "AppendTextCommandDtoInput"):
        schemas[command]["x-import-editing-limits"] = {"maxPreparationWorkBytes": 67108864, "maxSingleLineBytes": 1048576,
            "maxSingleLineItems": 20000, "maxPreparedRowsBytes": 16777216, "maxReceiptBytes": 65536,
            "description": "固定资源16MiB与每操作逻辑准备工作64MiB独立；完整最终新/替换行逐行预检并累计16MiB，完整回执受实际配置预算。超限413且不部分提交。这不是CLR heap或OS sandbox保证。"}
    for field in ("target", "startUs", "endUs"):
        required_property("TokenTimeChangeDtoInput", field)
    if schemas["TokenTimeChangeDtoInput"]["properties"]["target"] != ref("LyricTimingTargetDtoInput"):
        raise ValueError("Unreviewed batch timing target type")
    schemas["TokenTimeChangeDtoInput"]["properties"]["target"] = ref("LyricTokenTimingTargetDtoInput")
    schemas["ProjectCommandResponse"]["properties"]["structureResult"]["description"] = "仅G20六种结构命令成功时返回操作结果；可空身份与顺序槽为null时省略，根层parentId因此省略。"
    schemas["ProjectCommandResponse"]["properties"]["layoutResult"]["description"] = "仅G21 setLayoutDefaultPlacement成功时返回；changed表示内部布局默认位置是否实际变化。"
    schemas["ProjectOrchestrationPlacementDtoInput"] = object_schema({
        "x": {"type": "number", "description": "有限JSON数值；允许画布外位置，服务拒绝NaN/Infinity。"},
        "y": {"type": "number", "description": "有限JSON数值；允许画布外位置，服务拒绝NaN/Infinity。"},
        "anchor": {"type": "string", "enum": ["Center", "TopLeft", "TopCenter", "BottomRight", "LeftCenter", "RightCenter"], "description": "canonical名称枚举；服务端命名匹配不区分大小写。"},
        "rotationDegrees": {"type": "number", "description": "有限JSON数值；服务拒绝NaN/Infinity。"},
        "scale": {"type": "number", "exclusiveMinimum": 0, "description": "有限JSON数值且必须大于0。"},
    }, ["x", "y", "anchor", "rotationDegrees", "scale"], strict=True)
    schemas["OrchestrationStructureCommandResultDto"]["properties"]["operation"]["enum"] = ["setNode", "reparent", "reorderChildren", "addSlot", "removeNode", "reorderSlots"]
    schemas["ProjectCommandResponse"]["properties"]["timingResult"]["description"] = "仅元素起点/终点打点成功响应提供；其余及旧五命令省略。"
    schemas["LyricTimingCommandResultDto"]["properties"]["estimatedPlaceholder"]["description"] = "共享Application实际返回的估算占位标记，不由前端推测。"
    schemas["ProjectCommandResponse"]["properties"]["feedback"]["description"] = "仅五种歌词正文/行操作的有界真实反馈；省略selection表示不建议改变选择。"
    schemas["ProjectCommandResponse"]["properties"]["branchResult"]["description"] = "仅来源新增/删除真实变化时提供实际来源选择；Configure/Link/no-op省略。"
    schemas["LyricBranchCommandResultDto"]["properties"]["selectedBranchId"].update(pattern=r"\S", description="真实稳定来源ID，不新增128字符上限；完整反馈受回执预算约束。")
    schemas["ProjectCommandResponse"]["properties"]["tokenResult"]["description"] = "仅五种元素结构命令提供真实共享用例结果；无巨型身份映射。合并/重排省略切分统计，省略selection表示不建议改变选择。"
    schemas["LyricTokenSelectionDto"]["allOf"] = [{
        "if": {"properties": {"hasTokenSelection": {"const": True}}, "required": ["hasTokenSelection"]},
        "then": {"required": ["tokenId"]},
        "else": {"not": {"required": ["tokenId"]}},
    }]
    for field in ("changedLineCount", "preservedTokenCount", "createdTokenCount", "estimatedTokenCount"):
        schemas["LyricTokenCommandResultDto"]["properties"][field]["minimum"] = 0
    schemas["LyricContentSelectionDto"]["allOf"] = [{
        "if": {"properties": {"hasSelection": {"const": True}}, "required": ["hasSelection"]},
        "then": {"required": ["lineId"]},
        "else": {"not": {"required": ["lineId"]}},
    }]
    for field in ("insertedIndex", "preservedTokenCount", "resetTokenCount"):
        schemas["LyricContentCommandFeedbackDto"]["properties"][field]["minimum"] = 0
    extend_orchestration_schemas(schemas, metadata, schema_for, variants, base_command)
    extend_node_creation_schemas(schemas, metadata, schema_for, variants, base_command)
    extend_static_style_schemas(schemas, metadata, schema_for, variants)
    extend_editing_completion_schemas(schemas, metadata, schema_for, variants, base_command)
    extend_basic_command_schemas(schemas, metadata, schema_for, variants, base_command)
    extend_ruby_commands(schemas, metadata, schema_for, variants, base_command)
    # P1 strict custom-converter branches. CLR inheritance is audited before
    # exposing only the exact public fields, including optional non-null hints.
    for typ, clr, fields, required in [
        ("composition.setBackgroundAsset", "SetBackgroundAssetCommandDto", ["assetId", "kind", "fit", "playback"], ["assetId", "kind"]),
        ("composition.clearBackground", "ClearBackgroundCommandDto", [], []),
        ("composition.setBackgroundFit", "SetBackgroundFitCommandDto", ["fit"], ["fit"]),
        ("composition.setBackgroundPlayback", "SetBackgroundPlaybackCommandDto", ["playback"], ["playback"]),
        ("project.applyEdits", "ApplyProjectEditsCommandDto", ["edits"], ["edits"]),
    ]:
        info = metadata[clr]
        if info.get("baseType") != "ProjectCommandDto" or {p["name"] for p in info["properties"]} != set(base_command["properties"]) | set(fields):
            raise ValueError("Unreviewed P1 command fields/base: " + clr)
        for prop in info["properties"]:
            if prop["name"] not in fields:
                continue
            if (prop["type"] != ("ProjectCommandDto[]" if prop["name"] == "edits" else "String")
                    or not prop["nullable"] or prop["optional"] != (prop["name"] in ("fit", "playback") and typ.endswith("setBackgroundAsset"))
                    or prop["defaultValue"] is not None):
                raise ValueError("Unreviewed P1 command types/nullability/defaults: " + clr)
        props = {"type": {"type": "string", "const": typ}}
        for field in fields:
            props[field] = {"type": "string", "minLength": 1, "pattern": r"\S"}
        if "kind" in fields:
            props["kind"]["enum"] = ["image", "video"]
        if "fit" in fields:
            props["fit"]["enum"] = ["contain", "cover", "original"]
        if "playback" in fields:
            props["playback"]["enum"] = ["loop", "freezeLastFrame"]
        if "edits" in fields:
            props["edits"] = {"type": "array", "items": ref("ProjectEditDtoInput")}
        component = clr + "Input"
        schemas[component] = {**object_schema(props, ["type", *required], strict=True),
            "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr}
        variants.append(ref(component))
    excluded_edits = {"project.applyEdits", "history.undo", "history.redo", "audio.replaceAsset", "audio.clear",
                      "audio.analysis.apply", "lyrics.importAsset", "composition.setBackgroundAsset"}
    schemas["ProjectEditDtoInput"] = {"oneOf": [variant for variant in variants
        if schemas[variant["$ref"].split("/")[-1]]["properties"]["type"]["const"] not in excluded_edits],
        "description": "按数组顺序复用单命令解释器；不接受历史、资源导入、外部任务或嵌套批处理。不另设编辑项数门槛，完整候选与回执仍按实际配置验证。"}
    for component in ("ProjectEditPreviewRequest", "ApplyProjectEditsCommandDtoInput"):
        schemas[component]["properties"]["edits"] = {"type": "array", "items": ref("ProjectEditDtoInput")}
        if "edits" not in schemas[component]["required"]:
            schemas[component]["required"].append("edits")
    schemas["LyricsImportPlanRequest"] = {**object_schema({"assetId": {"type": "string", "minLength": 1, "pattern": r"\S"},
        "options": ref("LyricsImportOptionsDtoInput")}, ["assetId"], strict=True),
        "description": "只读授权资源检测，不创建批准票据或修改工程；options可省略，出现时必须为对象。解析失败以preparationError返回，正式导入独立分配身份。"}
    font = schemas["FontInspectRequest"]
    font["required"] = list(font["properties"])
    font["additionalProperties"] = False
    font["properties"]["text"].update(maxLength=100000)
    font["properties"]["family"] = {"anyOf": [{"type": "string", "maxLength": 256}, {"type": "null"}]}
    font["properties"]["fontSizePx"].update(exclusiveMinimum=0)
    font["properties"]["fontWeight"].update(minimum=100, maximum=900)
    font["properties"]["fallbackFamilies"] = {"anyOf": [{"type": "array", "maxItems": 32,
        "items": {"type": "string", "minLength": 1, "maxLength": 256, "pattern": r"\S"}}, {"type": "null"}]}
    font["description"] = "八字段均必填；family/fallbackFamilies/sessionId/expectedRevision允许null。检查真实Worker环境，不修改样式；sessionId非null时须提供匹配expectedRevision。UTF-16长度及字体闭包能力由服务验证。"
    schemas["ProjectCommandDtoInput"] = {"oneOf": variants, "discriminator": {"propertyName": "type", "mapping": {schemas[v["$ref"].split("/")[-1]]["properties"]["type"]["const"]: v["$ref"] for v in variants}}}
    schemas["PresetApplicationTargetDtoInput"] = preset_target_union()
    enums = {
        ("ClientRegistrationRequest", "protocolVersion"): ["1"],
        ("FileHandleRequest", "access"): ["read", "readWrite"],
        ("OpenProjectSessionRequest", "access"): ["read", "readWrite"],
        ("CloseProjectRequest", "policy"): ["requireClean", "save", "discard"],
        ("CloseProjectRequest", "activeJobs"): ["reject", "wait", "cancelAndWait"],
        ("PreviewSourceDtoInput", "kind"): ["project", "authorDraft"],
        ("PreviewSessionRequest", "purpose"): ["preview"],
        ("PreviewFrameCommandRequest", "intent"): ["playback", "seek"],
        ("ExportJobRequest", "format"): ["opaqueMp4", "transparentProRes4444"],
    }
    for (component, prop), values in enums.items():
        schemas[component]["properties"][prop]["enum"] = values
    schemas["CreateProjectSessionRequest"]["properties"]["splitRule"]["enum"] = list(range(6))
    for component in ("PreviewSizeDto", "PreviewSizeDtoInput"):
        schemas[component]["properties"]["width"].update(minimum=1, maximum=1280)
        schemas[component]["properties"]["height"].update(minimum=1, maximum=720)
    # Typed string integers; preserve the two legacy JSON number fields and raw analysis numbers.
    # Only project/preview DTOs carry decimal source revisions.  The same CLR
    # property name is intentionally reused by the independent template
    # library (where sourceRevision is an opaque `missing`/SHA identity), so
    # sourceRevision and expectedSourceRevision must not be global scalar rules.
    long_names = {"projectRevision", "previousRevision", "expectedRevision", "savedProjectRevision", "currentProjectRevision", "previewGeneration", "expectedPreviewGeneration", "requestSeq", "latestRequestSeq", "eventSeq", "sequence", "startUs", "endUs", "timeUs", "outputDurationUs", "derivedVideoDurationUs", "actualVideoDurationUs", "audioDurationUs", "actualAudioDurationUs", "sourceDurationUs", "maxDurationUs", "maxCompressedAudioDurationUs", "maxSourceDurationUs", "originUs", "sampleCount", "durationUs", "completedSamples", "totalSamples", "maxSamples"}
    project_revision_fields = {
        "BackendAnalysisJobInputDto": {"sourceRevision"},
        "BackendJobInputDto": {"sourceRevision"},
        "BackendJobInputDtoRaw": {"sourceRevision"},
    }
    long_names.update(("playheadUs", "deltaUs", "offsetUs", "bucketSamples", "baseProjectRevision"))
    signed = {"startUs", "endUs", "timeUs", "originUs", "playheadUs", "deltaUs", "offsetUs"}
    for name, value in schemas.items():
        for prop, field in value.get("properties", {}).items():
            target = field["anyOf"][0] if "anyOf" in field else field
            if (prop in long_names or prop in project_revision_fields.get(name, set())) and target.get("type") == "string" and not target.get("x-int64-string"):
                target.update(pattern=r"^-?[0-9]+$" if prop in signed else r"^[0-9]+$", **{"x-int64-string": True})
                target["description"] = "十进制Int64字符串；范围与领域关系由服务校验。前端保留字符串/BigInt。"
            if prop in ("sha256", "sourceSha256", "sourceHash", "pcmHash", "contentHash", "reportSha256") and target.get("type") == "string":
                target["pattern"] = "^[0-9a-f]{64}$"
            if prop == "protocolVersion" and target.get("type") == "string":
                target["const"] = "1"
    for name, prop in (("ProjectSessionSummaryResponse", "audioDurationUs"), ("CompositionDtoInput", "outputDurationUs")):
        schemas[name]["properties"][prop]["description"] = "旧合同JSON Int64 number。超过JS安全整数时须保真解析；不得擅自改发字符串。"
    schemas["PreviewFrameCommandRequest"]["properties"]["requestSeq"]["pattern"] = r"^0*[1-9][0-9]*$"
    schemas["PreviewFrameCommandRequest"]["properties"]["timeUs"]["pattern"] = r"^[0-9]+$"
    schemas["SetOutputDurationCommandDtoInput"]["properties"]["durationUs"]["anyOf"][0]["pattern"] = r"^0*[1-9][0-9]*$"
    for component in ("JobResponse", "JobSummaryDto"):
        schemas[component]["properties"]["kind"]["enum"] = ["open", "save", "close", "recover", "validation", "export", "analysis"]
        schemas[component]["properties"]["state"]["enum"] = ["queued", "running", "committing", "succeeded", "failed", "cancelled", "unknown"]
    schemas["PreviewSessionResponse"]["properties"]["state"]["enum"] = ["idle", "queued", "rendering", "ready", "failed", "closing", "closed"]
    schemas["ProjectChangeSetDto"]["description"] = "完整默认JSON超过16KiB时保留areas，所有ID数组为空且requiresFullRefresh=true；否则返回精确目标，不返回部分ID假装完整。"
    for component, collection in (("ProjectLyricBranchesResponse", "branches"), ("ProjectLyricsWindowResponse", "lines")):
        for prop in ("offset", "totalCount", "nextOffset"):
            schemas[component]["properties"][prop]["minimum"] = 0
        schemas[component]["properties"][collection]["maxItems"] = 100
    schemas["ProjectLyricBranchDto"]["properties"]["lineCount"]["minimum"] = 0
    for prop in ("tokens", "rubyAnnotations"):
        schemas["ProjectLyricLineDto"]["properties"][prop]["maxItems"] = 19999
    schemas["ProjectLyricLineDto"]["description"] = "原始保存时间与状态；FollowPrimary不合成时间。注音segments具有独立绝对时间与状态；无独立时间片段沿基础字高亮。单行整体预算含注音组及每个segment，超限413。"

    # Library revisions are opaque byte identities, never decimal project Int64s.
    # Apply this overlay after the legacy scalar-name overlay; old components stay unchanged.
    extend_template_library_schemas(schemas, metadata, schema_for)
    extend_basic_response_schemas(schemas, metadata, schema_for)
    extend_ruby_responses(schemas, metadata)
    extend_package_preview_schemas(schemas, metadata)
    extend_project_image_preview_schemas(schemas, metadata)
    extend_validation_package_schemas(schemas)
    # The image profile extends the existing package validation source union.
    extend_project_image_validation_schemas(schemas, metadata)
    extend_project_image_export_schemas(schemas, metadata)

    quality_values = ["draft", "standard", "high"]
    for component, field in (("ExportJobRequest", "quality"), ("BackendExportInputDto", "quality"),
                             ("BackendExportInputDtoRaw", "quality"), ("ExportReportDtoRaw", "requestedQuality"),
                             ("ExportReportDtoRaw", "actualQuality")):
        schemas[component]["properties"][field]["enum"] = quality_values
    schemas["ExportTaskCapabilitiesDto"]["properties"]["qualities"].update(items={"type": "string", "enum": quality_values}, uniqueItems=True)
    for field in ("maxFrames", "maxDurationUs", "maxFrameRate", "maxCanvasWidth", "maxCanvasHeight",
                  "maxRawFrameBytes", "operationTimeoutSeconds", "maxMediaBytes", "maxArtifactStorageBytes",
                  "maxCompressedAudioDurationUs", "maxAudioBytes"):
        schemas["ExportTaskCapabilitiesDto"]["properties"][field]["description"] = "宿主资源策略的可选限额；未施加该维度限额时响应省略此字段，客户端不得补成固定短片门槛。"
    background = schemas["ProjectBackgroundDto"]["properties"]
    for field, values in (("kind", ["color", "image", "video"]), ("fit", ["contain", "cover", "original"]),
                          ("playback", ["loop", "freezeLastFrame"])):
        background[field]["enum"] = values
    schemas["ProjectResourceDto"]["description"] = "当前工程资源闭包；背景媒体沿roles关联，available时可提供当前client授权assetId/contentUrl，不暴露本机路径。"
    schemas["ProjectCommandResponse"]["properties"]["editResults"]["description"] = "project.applyEdits按数组顺序返回逐项原命令反馈；整体一次历史提交。"
    schemas["PreviewSessionResponse"]["properties"]["editCandidate"]["description"] = "编辑候选的固定baseProjectRevision/candidateRevision、合并changeSet及逐编辑反馈；source.sourceRevision与candidateRevision相同，均为不透明身份。"

    extend_p1p2_runtime(schemas, metadata)
    extend_p3_workshop(schemas, metadata, schema_for)
    extend_p4_author(schemas, metadata, schema_for)
    extend_render_backends(schemas, metadata)
    extend_w8_authoring(schemas, metadata)

    # GPU streams use a smaller device pool than ordinary PNG previews.
    schemas["GpuPreviewSizeInput"] = copy.deepcopy(schemas["PreviewSizeDtoInput"])
    schemas["GpuPreviewSizeInput"]["properties"]["width"].update(minimum=64, maximum=2560)
    schemas["GpuPreviewSizeInput"]["properties"]["height"].update(minimum=64, maximum=1440)
    schemas["GpuPreviewSize"] = copy.deepcopy(schemas["PreviewSizeDto"])
    schemas["GpuPreviewSize"]["properties"]["width"].update(minimum=64, maximum=2560)
    schemas["GpuPreviewSize"]["properties"]["height"].update(minimum=64, maximum=1440)
    for component in ("GpuPreviewStreamCreateRequest", "GpuPreviewStreamResizeRequest"):
        schemas[component]["properties"]["size"] = ref("GpuPreviewSizeInput")
    gpu_create = schemas["GpuPreviewStreamCreateRequest"]["properties"]
    gpu_create["api"] = {"type": "string", "const": "vulkan"}
    gpu_create["consumerId"] = {"type": "string", "pattern": "^[0-9a-f]{32}$"}
    gpu_state = schemas["GpuPreviewStreamState"]["properties"]
    gpu_state["size"] = ref("GpuPreviewSize")
    gpu_state["api"] = {"type": "string", "const": "vulkan"}
    gpu_state["textureAlphaMode"] = {"type": "string", "const": "premultiplied"}
    gpu_state["registrationState"] = {"type": "string", "enum": ["ready", "failed", "closed"]}
    gpu_state["slotCount"] = {"type": "integer", "const": 3}
    gpu_state["allocationBytes"].update(minimum=1, maximum=67108864)
    for field in ("streamRevision", "inputGeneration", "producerEpoch", "poolGeneration"):
        gpu_state[field]["pattern"] = r"^(0|[1-9][0-9]*)$"
    worker_state = schemas["GpuStreamWorkerState"]["properties"]
    worker_state["state"]["enum"] = ["opening", "ready", "playing", "paused", "draining", "stopped", "failed"]
    worker_state["mode"]["enum"] = ["playing", "paused"]
    worker_state["timelineEpoch"]["pattern"] = r"^(0|[1-9][0-9]*)$"
    for field in ("lastAppliedControlSeq", "effectiveMediaTimeUs"):
        worker_state[field].update(minimum=0, maximum=9007199254740991)
    schemas["GpuStreamDiagnosticSummary"]["properties"]["items"]["maxItems"] = 4

    # ProjectPresetApplicationSourceDto explicitly uses JsonIgnore(Never) for
    # its three nullable source comparison fields.  They are therefore always
    # present on the actual response (including null), unlike ordinary nullable
    # response slots which the global serializer omits.  Keep the reviewed
    # response shape aligned with that wire behavior.
    source = schemas["ProjectPresetApplicationSourceDto"]
    source["properties"]["libraryId"] = {"anyOf": [{"type": "string"}, {"type": "null"}]}
    source["properties"]["revision"] = {"anyOf": [{"type": "string"}, {"type": "null"}]}
    source["properties"]["newVersionAvailable"] = {"anyOf": [{"type": "boolean"}, {"type": "null"}]}
    source["required"] = ["libraryId", "revision", "comparable", "newVersionAvailable"]
    source["description"] = "来源比较尚未可用时仍固定返回 libraryId/revision/newVersionAvailable:null；不把模板内容哈希当作库修订。"

    spec = {
        "openapi": "3.1.0", "jsonSchemaDialect": "https://json-schema.org/draft/2020-12/schema",
        "info": {"title": "MakoKaraGenerator local backend HTTP API", "version": CONTRACT, "description": f"冻结合同{CONTRACT}；HTTP协议1，产品{PRODUCT}。只含当前公共HTTP，不含未来功能及私有Worker。与api-contract.md和其固定行为源共同执行。info.version是合同版本，不是产品/HTTP版本。普通响应可忽略未知字段；请求遵守声明字段及业务门禁。"},
        "servers": [{"url": "http://127.0.0.1:{port}", "description": "将port替换为可信启动器ready中的实际随机端口；0是不可调用的占位值。不得假定5000。", "variables": {"port": {"default": "0", "description": "实际BACKEND_READY/WEB_BACKEND_READY监听端口"}}}],
        "x-product-version": PRODUCT, "x-http-protocol-version": "1", "x-source-baseline": BASELINE,
        "x-behavior-contract": "api-contract.md", "x-options": {"status": 204, "description": "全局CORS预检：允许的Origin，GET/POST/PUT/DELETE/HEAD及行为§1.1列出的请求头；无需client凭证。不是新增业务路由。"},
        "security": [{"ClientBearer": [], "ClientId": []}],
        "paths": dict(sorted(paths.items())),
        "components": {"securitySchemes": {
            "BootstrapBearer": {"type": "http", "scheme": "bearer", "description": "可信启动器私有bootstrap；仅登记/恢复使用，不是access/resume token。"},
            "ClientBearer": {"type": "http", "scheme": "bearer", "description": "当前accessToken，非JWT合同；不得放入URL。"},
            "ClientId": {"type": "apiKey", "in": "header", "name": "X-Mako-Client-Id", "description": "必须与ClientBearer同时发送，单独clientId不是凭证。"},
        }, "parameters": parameters, "schemas": dict(sorted(schemas.items()))},
    }
    return spec, operations


def source_routes():
    result = set()
    for file in sorted((ROOT / "src/MakoKaraGenerator.Backend.Server").glob("*.cs")):
        text = file.read_text()
        mappings = re.findall(r'app\.(Map\w+)\(', text)
        if any(m not in ("MapGet", "MapPost", "MapPut", "MapDelete", "MapMethods") for m in mappings):
            raise ValueError(f"Unreviewed route mapping method: {file}")
        for method, path in re.findall(r'app\.Map(Get|Post|Put|Delete)\("([^"]+)"', text):
            if (method.upper(), path) != ("POST", "/internal/gpu-preview-streams/{id}/claim"):
                result.add((method.upper(), path))
        for path, methods in re.findall(r'app\.MapMethods\("([^"]+)",\s*\[([^\]]+)\]', text):
            for method in re.findall(r'"([A-Z]+)"', methods):
                result.add((method, path))
        count = len(re.findall(r'app\.Map(?:Get|Post|Put|Delete|Methods)\(', text))
        recognized = len(re.findall(r'app\.Map(?:Get|Post|Put|Delete|Methods)\("', text))
        if count != recognized:
            raise ValueError(f"Unreviewed route mapping: {file}")
    return result


def inventory(spec, operations):
    lines = ["# 冻结 API 清单", "", f"合同 **{CONTRACT}** · HTTP **1** · 产品 **{PRODUCT}** · 基线 `{BASELINE}` · {FREEZE_DATE}。", "", "执行入口：[行为合同](api-contract.md)；机器规范：[openapi.json](openapi.json)；指纹：[contract-manifest.json](contract-manifest.json)。本表由核对过的路由定义生成，不能手工单独扩表。", "", f"共 **{len(spec['paths'])} 个路径、{len(operations)} 个显式方法**（含 5 个兼容别名；HEAD 单计，全局 OPTIONS 不计）。业务成功状态仅表示HTTP操作结果，202须等待job或preview状态。", "", "| 方法 | 路径 | 认证 | 请求 | 成功响应 | 行为源章节 |", "|---|---|---|---|---|---|"]
    for o in operations:
        auth = "无" if o["path"].endswith("health/live") else "bootstrap" if o["path"] == "/api/v1/clients" else "access + clientId"
        alias = "（别名）" if "alias" in o else ""
        successful = []
        for status, response in sorted(spec["paths"][o["path"]][o["method"].lower()]["responses"].items()):
            if not status.startswith("2"):
                continue
            schema = response.get("content", {}).get("application/json", {}).get("schema", {})
            label = schema.get("$ref", "").rsplit("/", 1)[-1] or o["response"]
            successful.append(f"{status} · {label}{'（无body）' if o['method'] == 'HEAD' else ''}")
        lines.append(f"| {o['method']} | `{o['path']}`{alias} | {auth} | {o['request']} | {'<br>'.join(successful)} | §{o['section']} |")
    lines += ["", "请求名对应 Contracts DTO；OpenAPI 内共享输入DTO带 Input 后缀，保留null的原始报告带 Raw 后缀。upload=原始字节/单文件multipart；asset/png/video=受认证字节；analysis=原Core完整JSON；sse=事件流。", "", "## 当前工程命令", "", "| type | 必需业务字段 |", "|---|---|", "| `lyrics.setTimeRange` | `target.branchId`、`target.lineId`、可选`target.tokenId`、字符串`startUs/endUs` |", "| `history.undo` | 无 |", "| `history.redo` | 无 |", "| `audio.analysis.apply` | `analysisJobId`，当前live、成功、同源分析任务 |", "| `audio.analysis.clear` | 无 |", "| `lyrics.stampTokenStart` / `lyrics.stampTokenEnd` | 完整元素`target`、字符串`playheadUs` |", "| `lyrics.clearTokenTime` | 完整元素`target` |", "| `lyrics.nudgeTokenTime` | 完整元素`target`、整数`frameDelta` |", "| `lyrics.shiftLine` | 整行`target`、字符串`deltaUs` |", "| `lyrics.setTokenTimes` | 1..1000个`changes`，各含完整元素`target`及字符串`startUs/endUs` |", "| `lyrics.setLineText` / `lyrics.setRubyText` | 行/元素`target`、必填非null的`text`（允许空串） |", "| `lyrics.addLine` | `branchId`、可省略或null的`afterLineId`（末尾追加） |", "| `lyrics.duplicateLine` / `lyrics.removeLine` | 行`target` |", "| `composition.setCanvasSize` | 正整数`width/height`（允许奇数） |", "| `composition.setFrameRate` | 正整数`numerator/denominator` |", "| `composition.setBackgroundColor` | 必填四个0..255整数`red/green/blue/alpha` |", "| `composition.setOutputDuration` | 必填`durationUs`：null或正Int64微秒字符串 |", "| `composition.useLyricsEnd` / `composition.useAudioDuration` | 无 |", "| `lyrics.addTranslationBranch` | 必填非null的`name`；`language`可省略/null |", "| `lyrics.configureBranch` | 必填`branchId/name/language/role/timingMode`；`language`可null |", "| `lyrics.removeBranch` | 必填非空白`branchId` |", "| `lyrics.linkPrimaryLine` | 必填`branchId/lineId/primaryLineId`；`primaryLineId`可null解除 |", "| `lyrics.splitToken` / `lyrics.mergeToken` | 元素`target` |", "| `lyrics.reorderToken` | 元素`target`、整数`destinationIndex`、必填布尔`reassignTimeSlots` |", "| `lyrics.resplitLine` | 行`target`、必填字符串数组`segments`（最多20000）、必填布尔`estimateTimes` |", "| `lyrics.resplitBranch` | `branchId`、字符串枚举`rule`、必填布尔`estimateTimes` |", "| `lyrics.importAsset` | 授权`assetId`、严格`destination`（existing/newBranch）、可省略对象`options` |", "| `lyrics.appendText` | 必填非null的`text`、严格`destination`（existing/newTranslation） |", "| `presetApplications.apply` | `templateLibraryId/templateId/templateRevision`、1..1000个严格目标`targets`、可选`branchInputs` |", "| `presetApplications.update` | `applicationId`、`templateLibraryId/templateId/templateRevision`、可选`branchInputs` |", "| `presetApplications.setEnabled` | `applicationId`、布尔`enabled` |", "| `presetApplications.remove` | 1..1000个不重复`applicationIds` |", "| `orchestration.setDefaultContainer` | 非空白、当前工程现有歌词容器的`branchId/containerId` |", "| `orchestration.setLayoutDefaultPlacement` | `containerId`与显式`placement`（对象或null；null清除内部布局默认位置） |", "| `orchestration.setNodeGeometry` | `nodeId/placement/referenceRect`（完整对象，不可null） |", "| `orchestration.setLayoutSettings` | `containerId/safeArea/maxWidthRatio`（完整安全区） |", "| `orchestration.setSlotAnimation` | `containerId/slotAnimation`（完整对象或显式null） |", "| `orchestration.setProgramAnimationPolicy` | `branchId/animationPolicy`（命名枚举或显式null） |", "| `orchestration.createCountdown` | `parentId/name/count`；name可null，count为1..64 |", "| `orchestration.createLeadIn` / `orchestration.updateLeadIn` | `branchId/leadInId/name/targetLineId/indicatorContainerId/durationUs/enabled`，全部必填，name可null |", "| `orchestration.deleteLeadIn` | `branchId/leadInId` |", "| `orchestration.setLineLocalTransform` | `branchId/lineId/transform`，transform为完整对象或显式null |", "| `orchestration.createClip` | `branchId/clipId/lineIds/containerId`；`name`可省略或显式null；lineIds为1..1000个非空稳定行ID |", "| `orchestration.renameClip` | `branchId/clipId/name`；name必须显式提供，可用null清除 |", "| `orchestration.setClipContainer` | `branchId/clipId/containerId` |", "| `orchestration.deleteClip` | `branchId/clipId` |", "| `orchestration.previewBasicLayout` / `orchestration.applyBasicLayout` | `branchId/preset/layoutName/slotNames/selectedSegmentName/retainedSegmentName`；`selectedLineIds`可省略或显式null表示整来源，非null数组表示连续范围并按G19规则校验；片段布局仅支持独立计时、Karaoke来源，其他来源返回`UnsupportedSource` |", "", "九十六种命令均在顶层带expectedRevision和回执三字段。OpenAPI按type区分分支；旧五命令接受已知无关字段显式null，新增分型命令只接受各自声明字段。批量目标不得重复，一次请求原子提交一次历史；presetApplications在Writer外固定精确模板快照后再Prepare→Commit，库更新不自动修改已分配工程；schema不证明目标存在或领域边界合法。", "", "S1提供来源列表、完整歌词编辑窗口（原始时间状态、关联主行、注音）与标量设置读取；三者必须携带expectedRevision，分页窗口不得混合修订。分页默认offset=0、limit=100，limit范围1..100；nextOffset在终页省略。完整JSON最多1MiB、来源/行/元素/注音组/注音片段合计最多20000条，超限413 resourceTooLarge，不截断单行。S3a五种歌词操作可提供feedback与真实selection建议，六种设置命令省略feedback。S3b新增/删除来源真实变化时提供branchResult.selectedBranchId；配置/关联/失败/no-op省略。来源ID沿Core非空白原值，不新增128字符上限；language/primaryLineId的必填字段显式null与缺字段不同。元素结构五命令的输入ID为非空白且最多128字符；tokenResult提供真实changedLineCount、可选selection及共享切分计数，不提供巨型身份映射。来源重切累计改变行完整投影最多16MiB，单行仍受1MiB/20000条目限制，超限整批不变。再次导入/粘贴追加复用共享用例；文件报告完整保留27字段，offsetUs为有符号Int64字符串，Error诊断与合法行并存不等同失败。options省略取原默认，显式null拒绝；输入目的地严格区分两命令。完整最终新/替换行仍受1MiB/20000项及累计16MiB预算，准备工作逻辑64MiB，成功回执受实际配置64KiB限制。波形提供同次音频准备的元数据和有界min/max float32 little-endian窗口，最多8192桶；元数据不接受query，窗口四字段必须唯一提供且拒绝未知query。Range一律400，HEAD所有状态无body。整个Host已保留峰值payload预算64MiB，不含临时prepare/窗口/元数据；缓存不足不阻断create/open，不可用有明确reason。身份不绑定歌词修订，不保证与浏览器逐sample对齐。历史静态样式批次仅canonical静态继承读写；后续公共package与当前统一Runtime来源见对应章节。Application用例、内部Worker能力和未来草案不能当作HTTP实现；G21～G24整体编辑及真实Worker同帧测量见行为§13.18～21；schema不证明真实Runtime像素或完整编排作者验收。", ""]
    completion_position = next(i for i, line in enumerate(lines) if line.startswith("| `orchestration.createClip`"))
    lines[completion_position:completion_position] = [
        "| `orchestration.setLayoutKind` | 显式containerId/kind/slotIds/newSlotCount；Auto空槽与0，SlotSequence总1..64 |",
        "| `orchestration.setLineEnterAnimation` | branchId/lineId/animation；完整五字段或null恢复继承 |",
        "| `orchestration.setProjectEnterAnimation` | animation；完整五字段或null关闭默认 |",
        "| `orchestration.setLeadInGeneration` | branchId/settings；六字段或null清设置 |",
        "| `orchestration.previewLeadInGeneration` | branchId/settings；完整分析，保持修订/history/redo |",
        "| `orchestration.applyLeadInGeneration` | branchId/settings/selectedLineIds；显式选择、完整重新分析后原子应用 |",
        "| `orchestration.deleteLeadInAndExclude` | branchId/leadInId；删除并排除目标 |",
        "| `orchestration.setLeadInAttachment` | branchId/leadInId/containerId/mode；FixedParent或CueTargetText，真实文字参考转换 |",
        "| `orchestration.addMark` / `orchestration.setMarkText` | containerId/text或nodeId/text；非空白文本≤4096 |",
        "| `orchestration.reorderMarks` / `orchestration.spaceMarks` | containerId/orderedIds或containerId；完整顺序或Core均匀排列 |",
        "| `orchestration.setCountdownAppearance` | containerId/appearance；完整六字段含明确stroke/shadow null |",
        "| `orchestration.setMarkAppearance` | nodeId/changes；完整静态override，空数组清除，禁几何三属性 |",
    ]
    structure_position = next(i for i, line in enumerate(lines) if line.startswith("| `orchestration.createClip`"))
    lines[structure_position:structure_position] = [
        "| `orchestration.createNode` | `kind/parentId/name/slotCount`全部显式；group/auto要求slotCount:null，slotSequence要求1..64整数；parentId:null为画布，否则已有普通组 |",
        "| `orchestration.copySubtree` | `nodeId/name`全部显式；同父完整复制，name:null清副本根名；Server生成身份，不复制歌词与业务绑定 |",
        "| `orchestration.setNode` | `nodeId/name`；name必须显式提供，可用null清除；仅修改名称 |",
        "| `orchestration.reparent` | `nodeId/parentId`；parentId必须显式提供，null表示根层 |",
        "| `orchestration.reorderChildren` | `parentId/orderedIds`；parentId可显式null，orderedIds为完整同级顺序 |",
        "| `orchestration.addSlot` | `containerId/slotId/name`；name必须显式提供，可为null |",
        "| `orchestration.removeNode` | `nodeId`；删除未被引用的子树 |",
        "| `orchestration.reorderSlots` | `containerId/orderedIds`；orderedIds为完整槽位顺序 |",
    ]
    position = next(i for i, line in enumerate(lines) if line.startswith("九十六种命令均在顶层"))
    lines[position - 1:position - 1] = ["| `styles.patch` | 同种唯一`targets` 1..1000（project仅一个），非空29种canonical属性/value的`changes`；value始终必填 |", "| `styles.restore` | 同种唯一branch/line/token `targets`，非空闭合`properties`；移除选中局部覆盖 |"]
    lines += ["样式GET/style必须唯一提供expectedRevision、kind及对应ID组合；完整baseline/staticHierarchy的字体及效果null不省略，local仅列本层实际覆盖。resolution固定staticHierarchy，不代表preset/node/clip叠加后的当前帧。包材质仅只读身份描述；写入只接受完整solid/gradient，未选字段和高级参数保留。styles.patch/restore成功带styleResult.changedTargetCount。新意图canonical command JSON最多64KiB；changes数组字节数乘目标数最多16MiB；新字体名256 UTF-16 code units、fallback32项、gradient256色标。完整读投影1MiB/20000项不套新输入长度限额，超限整批拒绝，不截断。", ""]
    lines += ["G20-B createNode/copySubtree成功structureResult返回createdNodeIds（新根第一，其余候选新增顺序）；旧六命令省略。名称256 UTF-16 units，输入ID128且非空白；command16KiB，源/候选各2000节点/深64，copy128节点，create初始槽位1..64。验证输入20000项及累计身份/名称/标记文本1048576 UTF-16 units，包含歌词branch/line/token与程序关联遍历项，长歌词也可能413；完整编排输出独立1MiB/20000项，完整回执提交前预检。见行为§13.17。", ""]
    lines += ["G20–G24整体补齐（合同1.24.0）：新增setLayoutKind、setLineEnterAnimation、setProjectEnterAnimation、setLeadInGeneration、previewLeadInGeneration、applyLeadInGeneration、deleteLeadInAndExclude、setLeadInAttachment、addMark、setMarkText、reorderMarks、spaceMarks、setCountdownAppearance、setMarkAppearance；完整字段和正反例见OpenAPI。单句展示GET提供局部transform与动画继承；measurements POST为只读真实Worker同帧几何及PNG，time/layout严格union。Node预设目标支持nodeId与显式includeDescendants；该历史1.24.0切片仅静态模板，当前完整模板/高级Node目标见行为§24。完整预算、no-op/redo及真实像素验收边界见行为§13.18～13.21。", ""]
    lines += ["## 独立模板库", "", "模板库不要求工程或目标，不改变工程修订/历史。builtin与personal描述不访问个人入口；坏个人库不遮蔽内置目录。Host新库按当前schema目录隔离；明确旧schema的历史固定入口保留原位，当前schema固定入口继续使用，未知版本、损坏或双入口歧义拒绝。规则见行为§13.1，不做旧库迁移。list初次读取可省expectedRevision，后续分页和detail使用精确修订；revision是missing或真实索引小写64位SHA，不能按Int64处理。detail始终保definition字段：省略definitionFormat或选择static时，staticAvailable提供完整canonical静态定义与26属性选择；advancedRuntimeAvailable表示现有Line/Token高级闭包可执行，definition仍为null；advancedUnsupported表示旧静态投影不可提供定义。显式definitionFormat=full返回fullAvailable、完整高级definition及packageClosureDetails，支持包材质、有序配方和固定包引用，详见行为§24。", "", "独立commands严格分型create/replace/copy/rename/delete，所有写入含expectedRevision/metadata。copy捕获sourceLibraryId/sourceRevision/templateId完整快照后对personal CAS；这是固定源复制，不是跨库分布式事务。create/replace保留旧静态定义分支，显式definitionFormat=full时接受完整高级定义；copy/rename/delete保完整原对象和包闭包，删除不清理旧包文件。损坏入口/闭包、版本错误及链接失败关闭，不能当空库覆盖。所有存在性/CAS位于client单层回执owner；成功回执在模板删除或库改变后仍原样重放，新ID只分配一次。", "", "完整新command canonical JSON最多256KiB，不含metadata；新名称/字体256 UTF-16 units、fallback32、gradient256。每次完整读取最多1MiB/20000 JSON值，完整回执预算提交前检查；新定义限额不套旧数据读取/服务端复制/改名/删除。既有index16MiB及完整包256MiB/1024files/单64MiB沿原规则。分页约束响应，不承诺全库IO已分页。", ""]
    command_count = len(spec["components"]["schemas"]["ProjectCommandDtoInput"]["oneOf"])
    lines = [line.replace("七十八种命令", f"{command_count}种命令").replace("九十一种命令", f"{command_count}种命令") for line in lines]
    position = next(i for i, line in enumerate(lines) if line.startswith("| `composition.setOutputDuration`"))
    lines[position:position] = [
        "| `composition.setBackgroundAsset` | 非空授权assetId、kind=image/video；fit及playback可省略但不可null，省略保现值 |",
        "| `composition.clearBackground` | 无；保底色、适配与视频时序 |",
        "| `composition.setBackgroundFit` | fit=contain/cover/original |",
        "| `composition.setBackgroundPlayback` | playback=loop/freezeLastFrame |",
        "| `project.applyEdits` | edits数组，顺序复用可接受的数据编辑；排除历史/资源导入/外部任务/嵌套批处理，一次提交历史 |",
    ]
    position = next(i for i, line in enumerate(lines) if line.startswith("| `orchestration.createClip`"))
    lines[position:position] = [
        "| `orchestration.setClipMembers` | branchId/clipId/lineIds；完整1..1000连续唯一行，保身份/名称/容器/原上下文 |",
        "| `orchestration.splitClip` | branchId/clipId/splitBeforeLineId/newClipId；左保旧身份，右用显式新身份，共享原调度上下文 |",
        "| `orchestration.mergeClips` | branchId/clipIds/targetClipId；至少两片段，同容器/上下文且合并成员连续，保目标身份 |",
        "| `audio.replaceAsset` / `audio.clear` | 授权assetId或无业务字段；真实受管理准备及历史音频租约，音轨无变化保redo |",
        "| `project.rename` | 显式name；非空白≤256 UTF-16 units，按Trim保存；不移动目录 |",
    ]
    lines += ["## 基础业务接口（0.95 / 合同1.25）", "",
        "resolved-style按真实line推导clip/container及Core调度slot，nodePath包含动态text叶节点，Node祖先预设遵守includeDescendants；返回26个Core属性来源。FollowPrimary仅跟随时钟/动画，容器路径保持自身来源。staticPresetHierarchy不是动画、脚本或最终像素。旧style staticHierarchy保持原wire。", "",
        "目录浏览只读授权根下一层且先扫描预算再分页；歌词导出支持branch及仅ttml.project的document范围，先完整计划再显式损失确认，UTF-8短期内容可Range/HEAD/ETag；模板.mktpl预检/导入/导出保完整高级闭包，预检与导入只允许personal，导入生成新identity，成功单次消费且metadata重放保持结果。资源页只列当前工程实际闭包，不暴露本机路径或未引用上传。完整限制及失败/生命周期见行为§17。", ""]
    lines += ["## 注音片段时间轴（0.96 / 合同1.27）", "",
        "| type | 必填业务字段 |", "|---|---|",
        "| `lyrics.setRubySegmentTime` | 五级`target`及字符串`startUs/endUs` |",
        "| `lyrics.clearRubySegmentTime` / `lyrics.removeRubySegment` | 五级`target` |",
        "| `lyrics.setRubySegmentText` | 五级`target`及字符串`text` |",
        "| `lyrics.addRubySegment` | 四级`target`、`text`、必填可null的`afterSegmentId`；新片段无时间 |",
        "| `lyrics.reorderRubySegments` | 四级`target`及完整`orderedSegmentIds` |",
        "| `lyrics.setRubySegmentTimes` | 1..1000条`changes[{target,startUs,endUs}]` |", "",
        "四级目标为branchId/lineId/tokenId/rubyId，五级再加segmentId。严格字段不可混用旧token目标或CLR别名。rubyResult提供真实数量/目标/建议选择/新身份；撤销重做保真实反馈。segments读模型保时间与状态，完整预算计入每个片段。多分支项目TTML导入须options.sourceBranchId选择源分支；全文档导出scope=document且只能ttml.project，禁止branchId含null。详细行为见§18。", ""]
    lines += ["## 直接效果包项目预览（0.98 / 合同1.29）", "",
        "历史0.98记录：复用现有project预览路由，当时实际Worker协商成功才公布package-lyrics-scene-v1。compileSource/source、imageEffect/imageEffect与material/material为完整互斥诊断payload；runtime保原occurrence。完整公共数组768项/512KiB，分页128项/最多32页，不截断。source IsError或runtime Error禁止新帧，failed无latest，旧成功lease保TTL。含preset的包、非Color背景、外媒体字体闭包与B4包任务仍拒绝；详见统一API§20。", ""]
    lines += ["## 同帧测量来源勘误（合同1.32.1）", "",
        "历史合同1.32.1记录：当时orchestration/measurements的完整固定输入沿真实Worker握手选择三种静态来源、package-lyrics-scene-v1 或 package-lyrics-scene-presets-v1。后者仅表示直接包与固定静态预设的有限测量，不扩大 preview/validation/export 的来源准入；仍限 Color 背景与完整 codec/测量预算。产品保持0.98.1，本次为已实现行为的合同勘误，未新增 fresh HTTP 验收；详见统一API§13.21。", ""]
    lines += ["## 已保存工程图片背景预览（产品0.99.0／合同1.33.0）", "",
        "历史0.99有限切片记录（现行来源见下方P1/P2节）：当时project预览自动选择project-image-preview-v1；runtime-info的兼容sourceProfile仍为static-lyrics-scene-v1，sourceProfiles仅在独立图片Worker握手成功后增加该值。客户端仍只交工程ID和精确修订，不提交图片bytes、路径、profile或grant。只读取ProjectStore已加载的固定Image闭包；原图2MiB、单边4096、解码16MiB、完整envelope4194280字节，超限拒绝。基础为无预设的static/static-analysis/直接package；该0.99预览验收未覆盖validation/export/measurement或模板候选图片输入。后续0.100图片正式验证另需独立project-image-validation-v1能力，见下节；此0.99历史切片当时不含export/measurement与模板候选图片输入。匹配发布d961895的有限fresh HTTP final6通过1175请求／9760断言；本地回归、Oracle预检与合同形状检查分开登记，具体范围和历史失败详见统一API§22与实施§77。", ""]
    lines += ["## 已保存工程图片背景正式验证（产品0.100.0／合同1.34.0）", "",
        "历史0.100有限切片记录（现行来源见下方P1/P2节）：当时validation-capabilities/validation-jobs/result/diagnostics接入独立project-image-validation-v1来源；图片预览与包Worker能力不能代替独立图片验证探测。公共请求保持精确expectedRevision、framesPerOutput、metadata；不接图片bytes/path/profile/grant。Host固定ProjectStore已加载的受管Image闭包，仅无preset的static/static-analysis/直接package基础源；完整验证输入4194272字节（4MiB减32字节KTI1头），原图2MiB、单边4096、解码16MiB、基础snapshot4MiB。报告与诊断沿原Runtime和既有取消/deadline/close排水；图片export/measurement、video/font/preset组合与完整B3/B4/B5/B6独立。匹配发布7a03863（sourceDirty=false）的fresh HTTP final1有限切片通过1412请求／10582断言，两次Server exit0、33个观察身份（26个Worker）全部退出、3349文件凭证扫描通过；三基础源普通/透明各4帧、完整报告与原Runtime报告Oracle、错误/预算/控制及一次orderly restart/recovery在有限范围内。正式validation没有PNG，报告Oracle不构成像素验收；旧0.99 Worker独立10请求／476断言、199文件负例拒绝503 taskUnavailable／WorkerImageValidationUnavailable，未并入主计数。实际证据与范围见统一API§10.1和实施§78。", ""]
    lines += ["## P1/P2 接口同步（合同1.36.0）", "",
        "现行P1/P2新增lyrics-import-plans、fonts、fonts/inspect和edit-previews四个公共操作，四个背景命令及project.applyEdits严格分型。创建可省略音频和歌词，直接从真实空工程开始；上传按流处理，部署字节限额maxAssetBytes/maxAssetQuotaBytes固定返回Int64或null。settings/resources提供背景与授权内容身份。匹配Worker独立协商后，preview/measurement/validation/export消费runtime-project-input-v1固定媒体/包字体闭包及完整包+预设来源，HTTP不接主机路径或私有grant；旧图片profile仅保留其历史能力与预算，不覆盖当前统一输入。候选candidateRevision与source.sourceRevision保持不透明candidate身份，baseProjectRevision仍是精确工程修订，测量候选三字段全有/全无且非null且expectedRevision必须匹配baseProjectRevision。导出quality三档和可空宿主策略沿原任务链；当前实际可用来源以各sourceProfiles为准，不从兼容单值推断。详见行为§6.12、§10、§11；合同形状、源码实现、真实Worker/HTTP及浏览器验收分开登记，历史证据数字不扩大为当前验收。", ""]
    lines += ["## P3/P4 交接（历史合同1.38.1）", "",
        "P3通过effect-packages/inspect和template-preview-options读取固定包声明、完整模板及试样目标；definitionFormat=full支持高级定义、包材质、有序配方，clips与高级Node目标复用工程分配命令。P4作者草稿支持完整源码/配置读取、固定source修订、同步200或异步202编译、当前live job编译结果、authorDraft试样预览/验证和个人配置库CAS命令。作者routes统一映射行为§21，模板工坊见§24。预览input保持原来源kind，authorDraft另保持同一draft ID；不能在project与authorDraft之间切换。产品0.104.1的边界修复和本次合同勘误独立于P4产品0.104.0的167请求／1142断言fresh HTTP证据；P5增量匹配发布源码f4d5e2a的P4作者流程fresh HTTP通过205请求／1368断言，证据根artifacts/p5-final-01041/http-r1，仍不扩大为浏览器、平台或性能验收。", ""]
    lines += ["## CPU/GPU 显式选择（产品0.106.0／合同1.39.0）", "",
        "GET render-backends返回部署启动时固定的defaultBackend与当前隔离Worker实际设备检查结果；公开选择仅cpu/gpu，底层API分别为raster/vulkan。available=true必须提供非空device摘要，available=false必须提供稳定unavailableReason；设备检查与后续业务产帧独立。显式GPU不可用时失败，不自动回退CPU。", "",
        "预览创建、工程/作者正式验证、导出与场景测量接受可选renderBackend；创建/任务省略或null使用启动默认，替换编辑候选或PUT input省略/null保留当前预览选择，候选测量省略/null使用候选固定选择。PUT preview-sessions/{id}/render-backend要求精确expectedPreviewGeneration、非null选择及metadata，保持固定输入，成功换代并排水旧Worker；相同后端通过CAS为no-op，失败保留旧代次与帧。", "",
        "预览会话、帧与事件必须回显renderBackend；新任务固定输入、验证/导出报告及测量结果回显本次执行选择，历史任务/报告缺失或null表示未知，禁止推断CPU。选择不改变工程修订/历史、Host环境或其他客户端偏好。详细生命周期和错误见行为§25；合同形状、匹配发布、真实GPU/HTTP产帧与排水证据分别登记，不扩大为浏览器、跨平台、性能或CPU/GPU位级像素一致性验收。", ""]
    return "\n".join(lines).encode()


def manifest(outputs):
    # Broad guard intentionally covers semantic changes outside DTO/route declarations.
    files = {ROOT / BEHAVIOR, ROOT / "Directory.Build.props", ROOT / "Directory.Packages.props", ROOT / "global.json"}
    for pattern in ("src/MakoKaraGenerator.Backend.*/**/*.cs", "src/MakoKaraGenerator.Backend.*/**/*.csproj", "src/MakoKaraGenerator.Core/Persistence/AudioAnalysisJson.cs"):
        files.update(p for p in ROOT.glob(pattern) if not {"bin", "obj"}.intersection(p.parts))
    # Static style constraints live in canonical Core/Application sources, not just transport DTOs.
    for pattern in ("src/MakoKaraGenerator.Application/Editing/*Style*.cs", "src/MakoKaraGenerator.Core/Projects/LyricStyle*.cs",
                    *["src/MakoKaraGenerator.Core/Projects/" + name + ".cs" for name in
                      ("TypographySpec", "FillSpec", "MaterialSpec", "GradientSpec", "StrokeSpec", "ShadowSpec", "GlowSpec", "GlowSpecOverride", "EffectSpecOverride", "TokenStyleSupport",
                       "RgbaColor", "OptionalValue", "LyricHorizontalAlignment")]):
        files.update(ROOT.glob(pattern))
    for pattern in ("src/MakoKaraGenerator.Application/*TemplateLibrary*.cs",
                    "src/MakoKaraGenerator.Application/LibraryRepository.cs", "src/MakoKaraGenerator.Application/LibraryCommitLease.cs",
                    "src/MakoKaraGenerator.Core/Persistence/StyleTemplateLibrary*.cs",
                    "src/MakoKaraGenerator.Core/Persistence/LibraryIndexRevision.cs", "src/MakoKaraGenerator.Core/Persistence/LibraryStoragePath.cs",
                    "src/MakoKaraGenerator.Core/Persistence/EffectPackageStorage.cs",
                    "src/MakoKaraGenerator.Core/Projects/StyleTemplate.cs", "src/MakoKaraGenerator.Core/Projects/StyleTemplateEffects.cs",
                    "src/MakoKaraGenerator.Core/Projects/TemplateEffectRecipe.cs"):
        files.update(ROOT.glob(pattern))
    # Explicit files, not class-name globs: shared types can live in a differently
    # named source file. A rename/removal requires review rather than dropping a hash.
    for filename in ("ScenePlacement.cs", "LyricPresentationDefinition.cs", "NormalizedSafeArea.cs", "SceneTransform.cs",
                     "SlotAnimationSpec.cs", "SlotSequenceAnimationProfile.cs", "SceneTreeGeometry.cs",
                     "SceneTreeEditor.Nodes.cs", "SceneTreeEditor.Reparent.cs", "SceneTreeEditor.Clips.cs",
                     "LeadInEditor.cs", "LyricLeadIn.cs", "LyricLocalTransform.cs", "PresentationEditor.cs", "LeadInGeneration.cs",
                     "SceneContainerDefinition.cs", "SceneContent.cs", "LyricPresentationDefinition.Tree.cs", "SceneTreeEditor.LineAnimation.cs"):
        source = ROOT / "src/MakoKaraGenerator.Core/Projects" / filename
        if not source.is_file():
            raise ValueError("Missing reviewed orchestration behavior source: " + str(source.relative_to(ROOT)))
        files.add(source)
    for filename in (
            "src/MakoKaraGenerator.Rendering.Abstractions/LayoutReferenceScene.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/MeasuredRenderFrame.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/SceneAttachmentReferences.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Geometry.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.TextMarks.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Measurements.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs",
            "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSession.cs",
            "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed real measurement behavior source: " + filename)
        files.add(source)
    for filename in ("PresentationValidator.cs", "PresentationValidator.LeadIns.cs"):
        source = ROOT / "src/MakoKaraGenerator.Core/Validation" / filename
        if not source.is_file():
            raise ValueError("Missing reviewed orchestration validation source: " + str(source.relative_to(ROOT)))
        files.add(source)
    for filename in (
            "src/MakoKaraGenerator.Application/ClipMembershipOperation.cs",
            "src/MakoKaraGenerator.Application/ClipSplitReferences.cs",
            "src/MakoKaraGenerator.Application/BasicLayoutOperation.cs",
            "src/MakoKaraGenerator.Application/ProjectHistory.cs",
            "src/MakoKaraGenerator.Application/StyleTemplateTransfer.cs",
            "src/MakoKaraGenerator.Core/Projects/PresetStyleResolver.cs",
            "src/MakoKaraGenerator.Core/Projects/ProjectAudioAnalysis.cs",
            "src/MakoKaraGenerator.Core/Projects/PresetApplication.cs",
            "src/MakoKaraGenerator.Core/Projects/PresetApplicationTargets.cs",
            "src/MakoKaraGenerator.Core/Projects/PresetApplicationsEditor.cs",
            "src/MakoKaraGenerator.Core/Projects/LyricDisplaySchedule.cs",
            "src/MakoKaraGenerator.Core/Projects/LyricDisplaySegment.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/LyricSceneSampler.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/LyricSceneSampler.LeadIns.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/SceneDefinition.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/PresetSceneStyleProjection.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed basic business behavior source: " + filename)
        files.add(source)
    files.update(ROOT.glob("src/MakoKaraGenerator.Core/Lyrics/Export/*.cs"))
    # TTML is also a public lyrics import path through lyrics.importAsset. Keep
    # the parser, format registration and timed ruby domain source in the
    # contract drift alarm alongside the exporter sources.
    for filename in (
            "src/MakoKaraGenerator.Core/Lyrics/Import/TtmlImporter.cs",
            "src/MakoKaraGenerator.Core/Lyrics/Import/AssImporter.cs",
            "src/MakoKaraGenerator.Core/Lyrics/Import/TtmlTimeTree.cs",
            "src/MakoKaraGenerator.Core/Lyrics/Import/ParsedLyrics.cs",
            "src/MakoKaraGenerator.Core/Lyrics/Import/ParsedLyricsImporterBase.cs",
            "src/MakoKaraGenerator.Core/Lyrics/Import/LyricsImporterRegistry.cs",
            "src/MakoKaraGenerator.Core/Lyrics/Import/LyricsFormatCatalog.cs",
            "src/MakoKaraGenerator.Core/Lyrics/RubyAnnotation.cs",
            "src/MakoKaraGenerator.Core/Lyrics/RubyTextSegment.cs",
            "src/MakoKaraGenerator.Core/Lyrics/LyricsDocument.cs",
            "src/MakoKaraGenerator.Core/Lyrics/RubySegmentEditor.cs",
            "src/MakoKaraGenerator.Core/Lyrics/LyricLineDuplicator.cs",
            "src/MakoKaraGenerator.Core/Persistence/LyricsV16ToV17Migration.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectDtoMapper.Branches.cs",
            "src/MakoKaraGenerator.Core/Persistence/PersistenceDtos.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectSchemaMigrator.cs",
            "src/MakoKaraGenerator.Core/Diagnostics/DiagnosticCode.cs",
            "src/MakoKaraGenerator.Core/Validation/ProjectValidator.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectJson.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectJsonReader.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectStore.cs",
            "src/MakoKaraGenerator.Application/Editing/RubySegmentEditUseCase.cs",
            "src/MakoKaraGenerator.Application/Editing/RubySegmentEditModels.cs",
            "src/MakoKaraGenerator.Application/Editing/LyricsImportEditUseCase.cs",
            "src/MakoKaraGenerator.Application/Editing/LyricContentEditUseCase.cs",
            "src/MakoKaraGenerator.Application/Editing/LyricTokenEditUseCase.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/ProjectSnapshot.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Ruby.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs",
            "src/MakoKaraGenerator.Scripting/Projection/TimelineProjection.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed TTML/ruby behavior source: " + filename)
        files.add(source)
    # Ruby spacing changes public PNG/geometry behavior without changing HTTP shapes.
    for filename in (
            "docs/runtime-validation-and-layout-contract.md",
            "src/MakoKaraGenerator.Text/RubySpacingSolver.cs",
            "src/MakoKaraGenerator.Text/ShapedLine.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaTextLayoutEngine.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Ruby.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Geometry.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.SceneLayout.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.SceneFrame.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.TokenGeometry.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.FamilyTransforms.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.TextPainting.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed ruby spacing behavior source: " + filename)
        files.add(source)
    files.add(ROOT / "src/MakoKaraGenerator.Rendering.Abstractions/RenderSessionFailureException.cs")
    # Image preview consumes fixed bytes in the real Runtime/Skia path. Hash
    # resolver ownership, background semantics and persistent media identity as
    # well as Backend codecs; a private IPC schema alone is not this boundary.
    for filename in (
            "src/MakoKaraGenerator.Rendering.Skia/MemoryImageBackgroundFrameSourceResolver.cs",
            "src/MakoKaraGenerator.Rendering.Skia/BackgroundFrameSources.cs",
            "src/MakoKaraGenerator.Rendering.Skia/BackgroundFrameSourceResolver.cs",
            "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs",
            "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSession.cs",
            "src/MakoKaraGenerator.Core/Projects/BackgroundSource.cs",
            "src/MakoKaraGenerator.Core/Projects/BackgroundKind.cs",
            "src/MakoKaraGenerator.Core/Projects/BackgroundFit.cs",
            "src/MakoKaraGenerator.Core/Projects/BackgroundPlaybackMode.cs",
            "src/MakoKaraGenerator.Core/Projects/MediaFileRevision.cs",
            "src/MakoKaraGenerator.Core/Media/BackgroundPlacement.cs",
            "src/MakoKaraGenerator.Core/Media/BackgroundPlayback.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectAssetCollector.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed project image behavior source: " + filename)
        files.add(source)
    # Unified Runtime media and package-font execution also depends on non-Backend sources.
    for filename in (
            "src/MakoKaraGenerator.Core/Projects/ProjectBackgroundEditor.cs",
            "src/MakoKaraGenerator.Media/SelfContainedVideoInput.cs",
            "src/MakoKaraGenerator.Media/FfmpegPcmReader.cs",
            "src/MakoKaraGenerator.Media/FfmpegMediaProbe.cs",
            "src/MakoKaraGenerator.Media/FfmpegVideoFrameProvider.cs",
            "src/MakoKaraGenerator.Media/FfmpegVideoFrameProvider.Source.cs",
            "src/MakoKaraGenerator.Media/OwnedMediaProcess.cs",
            "src/MakoKaraGenerator.Processes/OwnedProcessGroup.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaFontInspector.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaTypefaceCoverage.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaTextLayoutEngine.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed unified Runtime behavior source: " + filename)
        files.add(source)
    # Public CPU/GPU selection fixes the execution backend for each input and
    # changes real pixels, intermediate-surface ownership and readback. Keep
    # these reviewed sources in the drift alarm alongside the existing Runtime
    # session/factory and pipeline fingerprints; a rename must be reviewed.
    for filename in (
            "src/MakoKaraGenerator.Rendering.Skia/MakoKaraGenerator.Rendering.Skia.csproj",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderBackend.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderDeviceLifetime.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.AssembleColorParts.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.GroupIsolation.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.Surfaces.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.SceneSurfaces.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Sprites.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.Drawing.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.MaterialCoverage.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.PayloadCapture.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.PayloadComposition.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.PayloadLayers.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledPackageGraphExports.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/ImageCompositionPayload.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/PackageGraphImages.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/SceneOpacityProgram.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/SkiaGraphSurface.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/ImagePixelReadbackPool.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/MaterialAuthorGraphCache.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/ImageSurfaceBudgetSession.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledPackageGraphExports.Materials.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.Materials.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/PerformanceTelemetry.cs",
            "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.cs",
            "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Queries.cs",
            "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Emitters.cs",
            "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Visuals.cs",
            "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Particles.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed GPU rendering behavior source: " + filename)
        files.add(source)
    # Glow spans persistent source, read-only SDK projection and real color captures.
    for filename in (
            "docs/style-reference/glow.md",
            "src/MakoKaraGenerator.Core/Projects/Project.cs",
            "src/MakoKaraGenerator.Core/Projects/StyleBatchEditor.cs",
            "src/MakoKaraGenerator.Core/Projects/SceneTextAppearance.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectDtoMapper.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectDtoMapper.Glow.cs",
            "src/MakoKaraGenerator.Core/Persistence/ProjectDtoMapper.LeadIns.cs",
            "src/MakoKaraGenerator.Core/Persistence/LeadInDtos.cs",
            "src/MakoKaraGenerator.Core/Effects/EffectPackageImageGraphs.cs",
            "src/MakoKaraGenerator.Core/Effects/EffectPackageImageGraphs.Inputs.cs",
            "src/MakoKaraGenerator.Scripting.Abstractions/StyleView.cs",
            "src/MakoKaraGenerator.Scripting.Abstractions/StyleUpdate.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/StaticStyleProjection.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/SceneColorPart.cs",
            "src/MakoKaraGenerator.Rendering.Abstractions/SceneCapture.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Glow.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Capture.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ColorParts.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.Binding.cs",
            "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.MaterialCapture.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/SceneImageGraphProgram.cs",
            "src/MakoKaraGenerator.Rendering.Skia/Effects/SceneMaterialProgram.cs"):
        source = ROOT / filename
        if not source.is_file():
            raise ValueError("Missing reviewed glow behavior source: " + filename)
        files.add(source)
    files.update(p for p in TOOL.iterdir() if p.is_file())
    return {"contractVersion": CONTRACT, "httpProtocolVersion": "1", "productVersion": PRODUCT, "sourceBaselineCommit": BASELINE,
            "frozenOn": FREEZE_DATE, "status": "frozen", "behaviorSource": BEHAVIOR,
            "scope": "Implemented public HTTP API through static outer glow with inheritance, templates, text marks and per-input CPU/GPU selection. Contract shape, matched deployment, real Linux GPU/HTTP frames and lifecycle evidence remain separate; not browser, cross-platform or performance acceptance.",
            "files": {str(p.relative_to(ROOT)): sha(p.read_bytes()) for p in sorted(files)},
            "artifacts": {name: sha(data) for name, data in sorted(outputs.items())},
            "guards": "Source hashes require review even for behavior-preserving changes. They are drift alarms, not semantic proofs. Runtime/Media behavior still requires existing regression tests."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--write", action="store_true", help="Explicitly regenerate after contract review; default only checks")
    args = parser.parse_args()
    spec, operations = generate(json.loads(args.metadata.read_text()))
    listed = {(o["method"], o["path"]) for o in operations}
    actual = source_routes()
    if actual != listed:
        raise SystemExit(f"Route mismatch: undocumented={sorted(actual - listed)}, nonexistent={sorted(listed - actual)}")
    if len(actual) != len(operations):
        raise SystemExit("Duplicate operation")
    from render_backend_fixtures import append_to
    # The frozen file preserves the reviewed historical corpus; append_to
    # deterministically refreshes tagged P3/P4/render-backend fixture slices. Version
    # metadata belongs to the final aggregate, not its historical base file.
    from w8_authoring_fixtures import append_to as append_w8, PREFIX as W8_PREFIX
    from gpu_stream_fixtures import append_to as append_gpu_stream
    from glow_style_fixtures import fixtures as glow_fixtures, migrate_legacy_cases as migrate_glow
    existing_examples = json.loads((ROOT / "web/examples.json").read_text())
    existing_examples["cases"] = migrate_glow([case for case in existing_examples["cases"] if not case["name"].startswith("glow-")], upgrade=False)
    existing_examples["cases"] = [case for case in existing_examples["cases"] if not case["name"].startswith(W8_PREFIX)]
    example_document = append_gpu_stream(append_w8(append_to(existing_examples)))
    example_document["cases"] = migrate_glow(example_document["cases"]) + glow_fixtures()
    example_document["contractVersion"] = CONTRACT
    examples = encoded(example_document)
    outputs = {"web/openapi.json": encoded(spec), "web/api-index.md": inventory(spec, operations), "web/api-contract.md": (ROOT / "web/api-contract.md").read_bytes(), "web/examples.json": examples}
    outputs["web/contract-manifest.json"] = encoded(manifest(outputs))
    for name, data in outputs.items():
        path = ROOT / name
        if args.write:
            path.write_bytes(data)
        elif not path.exists() or path.read_bytes() != data:
            raise SystemExit(f"Contract drift: {name}; review source/behavior/schema and version before --write")
    print(f"{'Generated' if args.write else 'Verified'} {len(spec['paths'])} paths / {len(operations)} methods / {len(spec['components']['schemas'])} schemas; manifest matches.")


if __name__ == "__main__":
    main()
