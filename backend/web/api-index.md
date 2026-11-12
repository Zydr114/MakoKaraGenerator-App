# 冻结 API 清单

合同 **1.50.0** · HTTP **1** · 产品 **0.112.85** · 基线 `a929faa601ca40fc27918b7acdbc24fc04726b7f` · 2026-11-13。

执行入口：[行为合同](api-contract.md)；机器规范：[openapi.json](openapi.json)；指纹：[contract-manifest.json](contract-manifest.json)。本表由核对过的路由定义生成，不能手工单独扩表。

共 **115 个路径、128 个显式方法**（含 5 个兼容别名；HEAD 单计，全局 OPTIONS 不计）。业务成功状态仅表示HTTP操作结果，202须等待job或preview状态。

| 方法 | 路径 | 认证 | 请求 | 成功响应 | 行为源章节 |
|---|---|---|---|---|---|
| GET | `/api/v1/health/live` | 无 | - | 200 · HealthResponse | §2 |
| GET | `/api/v1/capabilities` | access + clientId | - | 200 · BackendCapabilitiesResponse | §2 |
| POST | `/api/v1/clients` | bootstrap | ClientRegistrationRequest | 201 · ClientRegistrationResponse | §3 |
| POST | `/api/v1/clients/{id}/heartbeat` | access + clientId | - | 200 · ClientHeartbeatResponse | §3 |
| GET | `/api/v1/clients/{id}/state` | access + clientId | - | 200 · ClientStateResponse | §3 |
| GET | `/api/v1/events` | access + clientId | - | 200 · sse | §4 |
| POST | `/api/v1/assets` | access + clientId | upload | 201 · AssetResponse | §5 |
| GET | `/api/v1/fonts` | access + clientId | - | 200 · FontListResponse | §6.12 |
| POST | `/api/v1/fonts/inspect` | access + clientId | FontInspectRequest | 200 · FontInspectResponse | §6.12 |
| POST | `/api/v1/lyrics-import-plans` | access + clientId | LyricsImportPlanRequest | 200 · LyricsImportPlanResponse | §6.12 |
| GET | `/api/v1/assets/{id}/content` | access + clientId | - | 200 · asset<br>206 · asset | §5 |
| HEAD | `/api/v1/assets/{id}/content` | access + clientId | - | 200 · asset（无body）<br>206 · asset（无body） | §5 |
| POST | `/api/v1/project-sessions` | access + clientId | CreateProjectSessionRequest | 201 · ProjectSessionSummaryResponse | §6 |
| GET | `/api/v1/project-sessions/{id}` | access + clientId | - | 200 · ProjectSessionSummaryResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/history` | access + clientId | - | 200 · ProjectHistoryResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/lyrics/lines` | access + clientId | - | 200 · LyricsLinesResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/lyrics/branches` | access + clientId | - | 200 · ProjectLyricBranchesResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/lyrics/window` | access + clientId | - | 200 · ProjectLyricsWindowResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/settings` | access + clientId | - | 200 · ProjectSettingsResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/resolved-style` | access + clientId | - | 200 · ProjectResolvedStyleResponse | §17 |
| GET | `/api/v1/project-sessions/{id}/resources` | access + clientId | - | 200 · ProjectResourcesResponse | §17 |
| POST | `/api/v1/project-sessions/{id}/resources/package-asset` | access + clientId | ProjectPackageResourceAssetRequest | 201 · ProjectPackageResourceAssetResponse | §17 |
| GET | `/api/v1/project-sessions/{id}/style` | access + clientId | - | 200 · ProjectStyleResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/orchestration` | access + clientId | - | 200 · ProjectOrchestrationResponse | §13 |
| GET | `/api/v1/project-sessions/{id}/orchestration/geometry` | access + clientId | - | 200 · ProjectGeometryReferenceResponse | §13 |
| GET | `/api/v1/project-sessions/{id}/orchestration/line-local-transform` | access + clientId | - | 200 · ProjectLineLocalTransformResponse | §13 |
| GET | `/api/v1/project-sessions/{id}/orchestration/line-presentation` | access + clientId | - | 200 · ProjectLinePresentationResponse | §13 |
| POST | `/api/v1/project-sessions/{id}/orchestration/measurements` | access + clientId | SceneMeasurementRequest | 200 · SceneMeasurementResponse | §13 |
| GET | `/api/v1/project-sessions/{id}/preset-applications` | access + clientId | - | 200 · ProjectPresetApplicationsResponse | §6 |
| GET | `/api/v1/template-libraries` | access + clientId | - | 200 · TemplateLibrariesResponse | §13 |
| GET | `/api/v1/template-libraries/{id}/templates` | access + clientId | - | 200 · TemplateListResponse | §13 |
| GET | `/api/v1/template-libraries/{id}/templates/detail` | access + clientId | - | 200 · TemplateDetailResponse | §13 |
| POST | `/api/v1/template-libraries/{id}/commands` | access + clientId | TemplateLibraryCommandRequest | 200 · TemplateLibraryCommandResponse | §13 |
| GET | `/api/v1/project-sessions/{id}/waveform` | access + clientId | - | 200 · ProjectWaveformResponse | §6 |
| GET | `/api/v1/project-sessions/{id}/waveform/peaks` | access + clientId | - | 200 · waveformPeaks | §6 |
| HEAD | `/api/v1/project-sessions/{id}/waveform/peaks` | access + clientId | - | 200 · waveformPeaks（无body） | §6 |
| POST | `/api/v1/project-sessions/{id}/commands` | access + clientId | ProjectCommandRequest | 200 · ProjectCommandResponse | §6 |
| GET | `/api/v1/file-roots/{rootGrantId}/entries` | access + clientId | - | 200 · ProjectDirectoryEntriesResponse | §17 |
| POST | `/api/v1/project-sessions/{id}/lyrics-export-plans` | access + clientId | LyricsExportPlanRequest | 201 · LyricsExportPlanResponse | §17 |
| POST | `/api/v1/project-sessions/{id}/lyrics-exports` | access + clientId | LyricsExportRequest | 201 · LyricsExportResponse | §17 |
| GET | `/api/v1/lyrics-exports/{id}/content` | access + clientId | - | 200 · lyricsContent<br>206 · lyricsContent | §17 |
| HEAD | `/api/v1/lyrics-exports/{id}/content` | access + clientId | - | 200 · lyricsContent（无body）<br>206 · lyricsContent（无body） | §17 |
| POST | `/api/v1/template-libraries/{id}/imports/preflight` | access + clientId | TemplateImportPreflightRequest | 201 · TemplateImportPreflightResponse | §17 |
| POST | `/api/v1/template-libraries/{id}/imports` | access + clientId | TemplateImportRequest | 200 · TemplateImportResponse | §17 |
| POST | `/api/v1/template-libraries/{id}/exports` | access + clientId | TemplateArchiveExportRequest | 201 · TemplateArchiveExportResponse | §17 |
| GET | `/api/v1/template-transfers/{id}/archive` | access + clientId | - | 200 · templateArchive<br>206 · templateArchive | §17 |
| HEAD | `/api/v1/template-transfers/{id}/archive` | access + clientId | - | 200 · templateArchive（无body）<br>206 · templateArchive（无body） | §17 |
| GET | `/api/v1/file-roots` | access + clientId | - | 200 · FileRootsResponse | §7 |
| POST | `/api/v1/file-handles` | access + clientId | FileHandleRequest | 201 · FileHandleResponse | §7 |
| POST | `/api/v1/project-sessions/open-jobs` | access + clientId | OpenProjectSessionRequest | 202 · JobResponse | §7 |
| POST | `/api/v1/project-sessions/recover-jobs` | access + clientId | RecoverProjectSessionRequest | 202 · JobResponse | §7 |
| POST | `/api/v1/project-sessions/{id}/save-jobs` | access + clientId | SaveProjectRequest | 202 · JobResponse | §7 |
| POST | `/api/v1/project-sessions/{id}/close` | access + clientId | CloseProjectRequest | 202 · JobResponse | §7 |
| GET | `/api/v1/jobs/{id}` | access + clientId | - | 200 · JobResponse | §8 |
| POST | `/api/v1/jobs/{id}/cancel` | access + clientId | RequestMetadataDto | 200 · JobResponse | §8 |
| GET | `/api/v1/recovery-candidates` | access + clientId | - | 200 · RecoveryCandidatesResponse | §8 |
| GET | `/api/v1/recovery-jobs` | access + clientId | - | 200 · RecoveryJobsResponse | §8 |
| GET | `/api/v1/runtime-info` | access + clientId | - | 200 · PreviewRuntimeInfoResponse | §9 |
| GET | `/api/v1/render-backends` | access + clientId | - | 200 · RenderBackendsResponse | §25 |
| POST | `/api/v1/gpu-preview-streams` | access + clientId | GpuPreviewStreamCreateRequest | 201 · GpuPreviewStreamState | §25 |
| GET | `/api/v1/gpu-preview-streams/{id}` | access + clientId | - | 200 · GpuPreviewStreamState | §25 |
| PUT | `/api/v1/gpu-preview-streams/{id}/source` | access + clientId | GpuPreviewStreamReplaceRequest | 200 · GpuPreviewStreamState | §25 |
| PUT | `/api/v1/gpu-preview-streams/{id}/size` | access + clientId | GpuPreviewStreamResizeRequest | 200 · GpuPreviewStreamState | §25 |
| DELETE | `/api/v1/gpu-preview-streams/{id}` | access + clientId | GpuPreviewStreamCloseRequest | 200 · GpuPreviewStreamState | §25 |
| POST | `/api/v1/preview-sessions` | access + clientId | PreviewSessionRequest | 201 · PreviewSessionResponse | §9 |
| POST | `/api/v1/project-sessions/{id}/edit-previews` | access + clientId | ProjectEditPreviewRequest | 201 · PreviewSessionResponse | §6.12 |
| POST | `/api/v1/project-sessions/{id}/preset-application-previews` | access + clientId | PresetApplicationPreviewRequest | 201 · PreviewSessionResponse | §13 |
| POST | `/api/v1/effect-packages/inspect` | access + clientId | EffectPackageInspectRequest | 200 · EffectPackageInspectResponse | §24 |
| POST | `/api/v1/template-preview-options` | access + clientId | TemplatePreviewOptionsRequest | 200 · TemplatePreviewOptionsResponse | §24 |
| POST | `/api/v1/template-preview-sessions` | access + clientId | TemplatePreviewSessionRequest | 201 · PreviewSessionResponse | §13 |
| PUT | `/api/v1/preview-sessions/{id}/input` | access + clientId | PreviewProjectInputRequest | 200 · PreviewSessionResponse | §9 |
| PUT | `/api/v1/preview-sessions/{id}/render-backend` | access + clientId | PreviewRenderBackendRequest | 200 · PreviewSessionResponse | §25 |
| POST | `/api/v1/preview-sessions/{id}/frame-requests` | access + clientId | PreviewFrameCommandRequest | 202 · PreviewFrameAcceptedResponse | §9 |
| GET | `/api/v1/preview-sessions/{id}` | access + clientId | - | 200 · PreviewSessionStateResponse | §9 |
| POST | `/api/v1/preview-sessions/{id}/close` | access + clientId | ClosePreviewSessionRequest | 200 · PreviewSessionStateResponse | §9 |
| GET | `/api/v1/preview-frames/{id}/content` | access + clientId | - | 200 · png<br>206 · png | §9 |
| HEAD | `/api/v1/preview-frames/{id}/content` | access + clientId | - | 200 · png（无body）<br>206 · png（无body） | §9 |
| GET | `/api/v1/preview-frames/{id}/inspection` | access + clientId | - | 200 · PreviewFrameInspectionResponse | §9 |
| GET | `/api/v1/preview-sessions/{id}/diagnostics` | access + clientId | - | 200 · PreviewDiagnosticsPageDto | §9 |
| GET | `/api/v1/validation-capabilities` | access + clientId | - | 200 · ValidationTaskCapabilitiesDto | §10 |
| POST | `/api/v1/project-sessions/{id}/validation-jobs` | access + clientId | ValidationJobRequest | 202 · JobResponse | §10 |
| GET | `/api/v1/jobs/{id}/validation-result` | access + clientId | - | 200 · ValidationReportDtoRaw | §10 |
| GET | `/api/v1/jobs/{id}/validation-diagnostics` | access + clientId | - | 200 · ValidationDiagnosticsPageDto | §10 |
| GET | `/api/v1/recovery-jobs/{id}/validation-result` | access + clientId | - | 200 · ValidationReportDtoRaw | §10 |
| GET | `/api/v1/export-capabilities` | access + clientId | - | 200 · ExportTaskCapabilitiesDto | §11 |
| POST | `/api/v1/project-sessions/{id}/export-jobs` | access + clientId | ExportJobRequest | 202 · JobResponse | §11 |
| GET | `/api/v1/jobs/{id}/export-result` | access + clientId | - | 200 · ExportResultReport | §11 |
| GET | `/api/v1/recovery-jobs/{id}/export-result` | access + clientId | - | 200 · ExportResultReport | §11 |
| GET | `/api/v1/jobs/{id}/export-content` | access + clientId | - | 200 · video<br>206 · video | §11 |
| HEAD | `/api/v1/jobs/{id}/export-content` | access + clientId | - | 200 · video（无body）<br>206 · video（无body） | §11 |
| GET | `/api/v1/recovery-jobs/{id}/export-content` | access + clientId | - | 200 · video<br>206 · video | §11 |
| HEAD | `/api/v1/recovery-jobs/{id}/export-content` | access + clientId | - | 200 · video（无body）<br>206 · video（无body） | §11 |
| GET | `/api/v1/jobs/{id}/export-diagnostics` | access + clientId | - | 200 · ExportDiagnosticsPageDto | §11 |
| GET | `/api/v1/analysis-capabilities` | access + clientId | - | 200 · AnalysisTaskCapabilitiesDto | §12 |
| POST | `/api/v1/project-sessions/{id}/analysis-jobs` | access + clientId | AnalysisJobRequest | 202 · JobResponse | §12 |
| GET | `/api/v1/jobs/{id}/analysis-result` | access + clientId | - | 200 · AudioAnalysisCanonical<br>206 · analysis | §12 |
| HEAD | `/api/v1/jobs/{id}/analysis-result` | access + clientId | - | 200 · analysis（无body）<br>206 · analysis（无body） | §12 |
| GET | `/api/v1/recovery-jobs/{id}/analysis-result` | access + clientId | - | 200 · AudioAnalysisCanonical<br>206 · analysis | §12 |
| HEAD | `/api/v1/recovery-jobs/{id}/analysis-result` | access + clientId | - | 200 · analysis（无body）<br>206 · analysis（无body） | §12 |
| POST | `/api/v1/drafts` | access + clientId | AuthorDraftCreateRequest | 201 · AuthorDraftResponse | §21 |
| GET | `/api/v1/drafts` | access + clientId | - | 200 · AuthorDraftListResponse | §21 |
| GET | `/api/v1/drafts/{id}` | access + clientId | - | 200 · AuthorDraftResponse | §21 |
| GET | `/api/v1/drafts/{id}/sources` | access + clientId | - | 200 · AuthorDraftSourcesResponse | §21 |
| GET | `/api/v1/profile/author-drafts` | access + clientId | - | 200 · ProfileAuthorDraftListResponse | §21 |
| POST | `/api/v1/profile/author-drafts/{id}/claim` | access + clientId | AuthorDraftClaimRequest | 200 · AuthorDraftClaimResponse | §21 |
| GET | `/api/v1/drafts/{id}/package-files` | access + clientId | - | 200 · AuthorPackageFilesResponse | §21 |
| PUT | `/api/v1/drafts/{id}/package-files` | access + clientId | AuthorPackageFilesRequest | 200 · AuthorDraftResponse | §21 |
| POST | `/api/v1/drafts/{id}/package-file-replacement-inspection` | access + clientId | AuthorPackageFileReplacementInspectionRequest | 200 · AuthorPackageFileReplacementInspectionResponse | §21 |
| GET | `/api/v1/drafts/{id}/source-revisions` | access + clientId | - | 200 · AuthorSourceRevisionsResponse | §21 |
| GET | `/api/v1/drafts/{id}/source-inspection` | access + clientId | - | 200 · AuthorSourceInspectionResponse | §21 |
| GET | `/api/v1/drafts/{id}/package` | access + clientId | - | 200 · packageArchive | §21 |
| GET | `/api/v1/script-configurations/{id}/package` | access + clientId | - | 200 · packageArchive | §21 |
| PUT | `/api/v1/drafts/{id}/configuration` | access + clientId | AuthorDraftConfigurationRequest | 200 · AuthorDraftResponse | §21 |
| GET | `/api/v1/drafts/{id}/compilation` | access + clientId | - | 200 · AuthorCompilationResponse | §21 |
| GET | `/api/v1/jobs/{id}/compilation-result` | access + clientId | - | 200 · AuthorCompilationResponse | §21 |
| POST | `/api/v1/drafts/{id}/validation-jobs` | access + clientId | AuthorValidationJobRequest | 202 · JobResponse | §21 |
| POST | `/api/v1/drafts/{id}/source-revision-jobs` | access + clientId | AuthorSourceRevisionRequest | 200 · AuthorSourceRevisionResponse | §21 |
| POST | `/api/v1/drafts/{id}/compilation-jobs` | access + clientId | AuthorCompilationRequest | 200 · AuthorCompilationResponse<br>202 · JobResponse | §21 |
| POST | `/api/v1/drafts/{id}/adopt-source-revision` | access + clientId | AuthorAdoptSourceRevisionRequest | 200 · AuthorAdoptSourceRevisionResponse | §21 |
| DELETE | `/api/v1/drafts/{id}` | access + clientId | AuthorDraftDeleteRequest | 200 · AuthorDraftDeleteResponse | §21 |
| GET | `/api/v1/script-configurations` | access + clientId | - | 200 · ScriptConfigurationListResponse | §21 |
| GET | `/api/v1/script-configurations/{id}` | access + clientId | - | 200 · ScriptConfigurationDetailResponse | §21 |
| POST | `/api/v1/script-configurations/commands` | access + clientId | ScriptConfigurationCommandRequest | 200 · ScriptConfigurationCommandResponse | §21 |
| GET | `/health/live`（别名） | 无 | - | 200 · HealthResponse | §2 |
| GET | `/capabilities`（别名） | access + clientId | - | 200 · BackendCapabilitiesResponse | §2 |
| POST | `/assets`（别名） | access + clientId | upload | 201 · AssetResponse | §5 |
| POST | `/project-sessions`（别名） | access + clientId | CreateProjectSessionRequest | 201 · ProjectSessionSummaryResponse | §6 |
| GET | `/project-sessions/{id}`（别名） | access + clientId | - | 200 · ProjectSessionSummaryResponse | §6 |

请求名对应 Contracts DTO；OpenAPI 内共享输入DTO带 Input 后缀，保留null的原始报告带 Raw 后缀。upload=原始字节/单文件multipart；asset/png/video=受认证字节；analysis=原Core完整JSON；sse=事件流。

## 当前工程命令

| type | 必需业务字段 |
|---|---|
| `lyrics.setTimeRange` | `target.branchId`、`target.lineId`、可选`target.tokenId`、字符串`startUs/endUs` |
| `history.undo` | 无 |
| `history.redo` | 无 |
| `audio.analysis.apply` | `analysisJobId`，当前live、成功、同源分析任务 |
| `audio.analysis.clear` | 无 |
| `lyrics.stampTokenStart` / `lyrics.stampTokenEnd` | 完整元素`target`、字符串`playheadUs` |
| `lyrics.clearTokenTime` | 完整元素`target` |
| `lyrics.nudgeTokenTime` | 完整元素`target`、整数`frameDelta` |
| `lyrics.shiftLine` | 整行`target`、字符串`deltaUs` |
| `lyrics.setTokenTimes` | 1..1000个`changes`，各含完整元素`target`及字符串`startUs/endUs` |
| `lyrics.setLineText` / `lyrics.setRubyText` | 行/元素`target`、必填非null的`text`（允许空串） |
| `lyrics.addLine` | `branchId`、可省略或null的`afterLineId`（末尾追加） |
| `lyrics.duplicateLine` / `lyrics.removeLine` | 行`target` |
| `composition.setCanvasSize` | 正整数`width/height`（允许奇数） |
| `composition.setFrameRate` | 正整数`numerator/denominator` |
| `composition.setBackgroundColor` | 必填四个0..255整数`red/green/blue/alpha` |
| `composition.setBackgroundAsset` | 非空授权assetId、kind=image/video；fit及playback可省略但不可null，省略保现值 |
| `composition.clearBackground` | 无；保底色、适配与视频时序 |
| `composition.setBackgroundFit` | fit=contain/cover/original |
| `composition.setBackgroundPlayback` | playback=loop/freezeLastFrame |
| `project.applyEdits` | edits数组，顺序复用可接受的数据编辑；排除历史/资源导入/外部任务/嵌套批处理，一次提交历史 |
| `composition.setOutputDuration` | 必填`durationUs`：null或正Int64微秒字符串 |
| `composition.useLyricsEnd` / `composition.useAudioDuration` | 无 |
| `lyrics.addTranslationBranch` | 必填非null的`name`；`language`可省略/null |
| `lyrics.configureBranch` | 必填`branchId/name/language/role/timingMode`；`language`可null |
| `lyrics.removeBranch` | 必填非空白`branchId` |
| `lyrics.linkPrimaryLine` | 必填`branchId/lineId/primaryLineId`；`primaryLineId`可null解除 |
| `lyrics.splitToken` / `lyrics.mergeToken` | 元素`target` |
| `lyrics.reorderToken` | 元素`target`、整数`destinationIndex`、必填布尔`reassignTimeSlots` |
| `lyrics.resplitLine` | 行`target`、必填字符串数组`segments`（最多20000）、必填布尔`estimateTimes` |
| `lyrics.resplitBranch` | `branchId`、字符串枚举`rule`、必填布尔`estimateTimes` |
| `lyrics.importAsset` | 授权`assetId`、严格`destination`（existing/newBranch）、可省略对象`options` |
| `lyrics.appendText` | 必填非null的`text`、严格`destination`（existing/newTranslation） |
| `presetApplications.apply` | `templateLibraryId/templateId/templateRevision`、1..1000个严格目标`targets`、可选`branchInputs` |
| `presetApplications.update` | `applicationId`、`templateLibraryId/templateId/templateRevision`、可选`branchInputs` |
| `presetApplications.setEnabled` | `applicationId`、布尔`enabled` |
| `presetApplications.remove` | 1..1000个不重复`applicationIds` |
| `orchestration.setDefaultContainer` | 非空白、当前工程现有歌词容器的`branchId/containerId` |
| `orchestration.setLayoutDefaultPlacement` | `containerId`与显式`placement`（对象或null；null清除内部布局默认位置） |
| `orchestration.setNodeGeometry` | `nodeId/placement/referenceRect`（完整对象，不可null） |
| `orchestration.setLayoutSettings` | `containerId/safeArea/maxWidthRatio`（完整安全区） |
| `orchestration.setSlotAnimation` | `containerId/slotAnimation`（完整对象或显式null） |
| `orchestration.setProgramAnimationPolicy` | `branchId/animationPolicy`（命名枚举或显式null） |
| `orchestration.createCountdown` | `parentId/name/count`；name可null，count为1..64 |
| `orchestration.createLeadIn` / `orchestration.updateLeadIn` | `branchId/leadInId/name/targetLineId/indicatorContainerId/durationUs/enabled`，全部必填，name可null |
| `orchestration.deleteLeadIn` | `branchId/leadInId` |
| `orchestration.setLineLocalTransform` | `branchId/lineId/transform`，transform为完整对象或显式null |
| `orchestration.setLayoutKind` | 显式containerId/kind/slotIds/newSlotCount；Auto空槽与0，SlotSequence总1..64 |
| `orchestration.setLineEnterAnimation` | branchId/lineId/animation；完整五字段或null恢复继承 |
| `orchestration.setProjectEnterAnimation` | animation；完整五字段或null关闭默认 |
| `orchestration.setLeadInGeneration` | branchId/settings；六字段或null清设置 |
| `orchestration.previewLeadInGeneration` | branchId/settings；完整分析，保持修订/history/redo |
| `orchestration.applyLeadInGeneration` | branchId/settings/selectedLineIds；显式选择、完整重新分析后原子应用 |
| `orchestration.deleteLeadInAndExclude` | branchId/leadInId；删除并排除目标 |
| `orchestration.setLeadInAttachment` | branchId/leadInId/containerId/mode；FixedParent或CueTargetText，真实文字参考转换 |
| `orchestration.addMark` / `orchestration.setMarkText` | containerId/text或nodeId/text；非空白文本≤4096 |
| `orchestration.reorderMarks` / `orchestration.spaceMarks` | containerId/orderedIds或containerId；完整顺序或Core均匀排列 |
| `orchestration.setCountdownAppearance` | containerId/appearance；完整六字段含明确stroke/shadow null |
| `orchestration.setMarkAppearance` | nodeId/changes；完整静态override，空数组清除，禁几何三属性 |
| `orchestration.createNode` | `kind/parentId/name/slotCount`全部显式；group/auto要求slotCount:null，slotSequence要求1..64整数；parentId:null为画布，否则已有普通组 |
| `orchestration.copySubtree` | `nodeId/name`全部显式；同父完整复制，name:null清副本根名；Server生成身份，不复制歌词与业务绑定 |
| `orchestration.setNode` | `nodeId/name`；name必须显式提供，可用null清除；仅修改名称 |
| `orchestration.reparent` | `nodeId/parentId`；parentId必须显式提供，null表示根层 |
| `orchestration.reorderChildren` | `parentId/orderedIds`；parentId可显式null，orderedIds为完整同级顺序 |
| `orchestration.addSlot` | `containerId/slotId/name`；name必须显式提供，可为null |
| `orchestration.removeNode` | `nodeId`；删除未被引用的子树 |
| `orchestration.reorderSlots` | `containerId/orderedIds`；orderedIds为完整槽位顺序 |
| `orchestration.setClipMembers` | branchId/clipId/lineIds；完整1..1000连续唯一行，保身份/名称/容器/原上下文 |
| `orchestration.splitClip` | branchId/clipId/splitBeforeLineId/newClipId；左保旧身份，右用显式新身份，共享原调度上下文 |
| `orchestration.mergeClips` | branchId/clipIds/targetClipId；至少两片段，同容器/上下文且合并成员连续，保目标身份 |
| `audio.replaceAsset` / `audio.clear` | 授权assetId或无业务字段；真实受管理准备及历史音频租约，音轨无变化保redo |
| `project.rename` | 显式name；非空白≤256 UTF-16 units，按Trim保存；不移动目录 |
| `orchestration.createClip` | `branchId/clipId/lineIds/containerId`；`name`可省略或显式null；lineIds为1..1000个非空稳定行ID |
| `orchestration.renameClip` | `branchId/clipId/name`；name必须显式提供，可用null清除 |
| `orchestration.setClipContainer` | `branchId/clipId/containerId` |
| `orchestration.deleteClip` | `branchId/clipId` |
| `orchestration.previewBasicLayout` / `orchestration.applyBasicLayout` | `branchId/preset/layoutName/slotNames/selectedSegmentName/retainedSegmentName`；`selectedLineIds`可省略或显式null表示整来源，非null数组表示连续范围并按G19规则校验；片段布局仅支持独立计时、Karaoke来源，其他来源返回`UnsupportedSource` |
| `styles.patch` | 同种唯一`targets` 1..1000（project仅一个），非空29种canonical属性/value的`changes`；value始终必填 |
| `styles.restore` | 同种唯一branch/line/token `targets`，非空闭合`properties`；移除选中局部覆盖 |

九十六种命令均在顶层带expectedRevision和回执三字段。OpenAPI按type区分分支；旧五命令接受已知无关字段显式null，新增分型命令只接受各自声明字段。批量目标不得重复，一次请求原子提交一次历史；presetApplications在Writer外固定精确模板快照后再Prepare→Commit，库更新不自动修改已分配工程；schema不证明目标存在或领域边界合法。

S1提供来源列表、完整歌词编辑窗口（原始时间状态、关联主行、注音）与标量设置读取；三者必须携带expectedRevision，分页窗口不得混合修订。分页默认offset=0、limit=100，limit范围1..100；nextOffset在终页省略。完整JSON最多1MiB、来源/行/元素/注音组/注音片段合计最多20000条，超限413 resourceTooLarge，不截断单行。S3a五种歌词操作可提供feedback与真实selection建议，六种设置命令省略feedback。S3b新增/删除来源真实变化时提供branchResult.selectedBranchId；配置/关联/失败/no-op省略。来源ID沿Core非空白原值，不新增128字符上限；language/primaryLineId的必填字段显式null与缺字段不同。元素结构五命令的输入ID为非空白且最多128字符；tokenResult提供真实changedLineCount、可选selection及共享切分计数，不提供巨型身份映射。来源重切累计改变行完整投影最多16MiB，单行仍受1MiB/20000条目限制，超限整批不变。再次导入/粘贴追加复用共享用例；文件报告完整保留27字段，offsetUs为有符号Int64字符串，Error诊断与合法行并存不等同失败。options省略取原默认，显式null拒绝；输入目的地严格区分两命令。完整最终新/替换行仍受1MiB/20000项及累计16MiB预算，准备工作逻辑64MiB，成功回执受实际配置64KiB限制。波形提供同次音频准备的元数据和有界min/max float32 little-endian窗口，最多8192桶；元数据不接受query，窗口四字段必须唯一提供且拒绝未知query。Range一律400，HEAD所有状态无body。整个Host已保留峰值payload预算64MiB，不含临时prepare/窗口/元数据；缓存不足不阻断create/open，不可用有明确reason。身份不绑定歌词修订，不保证与浏览器逐sample对齐。历史静态样式批次仅canonical静态继承读写；后续公共package与当前统一Runtime来源见对应章节。Application用例、内部Worker能力和未来草案不能当作HTTP实现；G21～G24整体编辑及真实Worker同帧测量见行为§13.18～21；schema不证明真实Runtime像素或完整编排作者验收。

样式GET/style必须唯一提供expectedRevision、kind及对应ID组合；完整baseline/staticHierarchy的字体及效果null不省略，local仅列本层实际覆盖。resolution固定staticHierarchy，不代表preset/node/clip叠加后的当前帧。包材质仅只读身份描述；写入只接受完整solid/gradient，未选字段和高级参数保留。styles.patch/restore成功带styleResult.changedTargetCount。新意图canonical command JSON最多64KiB；changes数组字节数乘目标数最多16MiB；新字体名256 UTF-16 code units、fallback32项、gradient256色标。完整读投影1MiB/20000项不套新输入长度限额，超限整批拒绝，不截断。

G20-B createNode/copySubtree成功structureResult返回createdNodeIds（新根第一，其余候选新增顺序）；旧六命令省略。名称256 UTF-16 units，输入ID128且非空白；command16KiB，源/候选各2000节点/深64，copy128节点，create初始槽位1..64。验证输入20000项及累计身份/名称/标记文本1048576 UTF-16 units，包含歌词branch/line/token与程序关联遍历项，长歌词也可能413；完整编排输出独立1MiB/20000项，完整回执提交前预检。见行为§13.17。

G20–G24整体补齐（合同1.24.0）：新增setLayoutKind、setLineEnterAnimation、setProjectEnterAnimation、setLeadInGeneration、previewLeadInGeneration、applyLeadInGeneration、deleteLeadInAndExclude、setLeadInAttachment、addMark、setMarkText、reorderMarks、spaceMarks、setCountdownAppearance、setMarkAppearance；完整字段和正反例见OpenAPI。单句展示GET提供局部transform与动画继承；measurements POST为只读真实Worker同帧几何及PNG，time/layout严格union。Node预设目标支持nodeId与显式includeDescendants；该历史1.24.0切片仅静态模板，当前完整模板/高级Node目标见行为§24。完整预算、no-op/redo及真实像素验收边界见行为§13.18～13.21。

## 独立模板库

模板库不要求工程或目标，不改变工程修订/历史。builtin与personal描述不访问个人入口；坏个人库不遮蔽内置目录。Host新库按当前schema目录隔离；明确旧schema的历史固定入口保留原位，当前schema固定入口继续使用，未知版本、损坏或双入口歧义拒绝。规则见行为§13.1，不做旧库迁移。list初次读取可省expectedRevision，后续分页和detail使用精确修订；revision是missing或真实索引小写64位SHA，不能按Int64处理。detail始终保definition字段：省略definitionFormat或选择static时，staticAvailable提供完整canonical静态定义与26属性选择；advancedRuntimeAvailable表示现有Line/Token高级闭包可执行，definition仍为null；advancedUnsupported表示旧静态投影不可提供定义。显式definitionFormat=full返回fullAvailable、完整高级definition及packageClosureDetails，支持包材质、有序配方和固定包引用，详见行为§24。

独立commands严格分型create/replace/copy/rename/delete，所有写入含expectedRevision/metadata。copy捕获sourceLibraryId/sourceRevision/templateId完整快照后对personal CAS；这是固定源复制，不是跨库分布式事务。create/replace保留旧静态定义分支，显式definitionFormat=full时接受完整高级定义；copy/rename/delete保完整原对象和包闭包，删除不清理旧包文件。损坏入口/闭包、版本错误及链接失败关闭，不能当空库覆盖。所有存在性/CAS位于client单层回执owner；成功回执在模板删除或库改变后仍原样重放，新ID只分配一次。

完整新command canonical JSON最多256KiB，不含metadata；新名称/字体256 UTF-16 units、fallback32、gradient256。每次完整读取最多1MiB/20000 JSON值，完整回执预算提交前检查；新定义限额不套旧数据读取/服务端复制/改名/删除。既有index16MiB及完整包256MiB/1024files/单64MiB沿原规则。分页约束响应，不承诺全库IO已分页。

## 基础业务接口（0.95 / 合同1.25）

resolved-style按真实line推导clip/container及Core调度slot，nodePath包含动态text叶节点，Node祖先预设遵守includeDescendants；返回26个Core属性来源。FollowPrimary仅跟随时钟/动画，容器路径保持自身来源。staticPresetHierarchy不是动画、脚本或最终像素。旧style staticHierarchy保持原wire。

目录浏览只读授权根下一层且先扫描预算再分页；歌词导出支持branch及仅ttml.project的document范围，先完整计划再显式损失确认，UTF-8短期内容可Range/HEAD/ETag；模板.mktpl预检/导入/导出保完整高级闭包，预检与导入只允许personal，导入生成新identity，成功单次消费且metadata重放保持结果。资源页只列当前工程实际闭包，不暴露本机路径或未引用上传。完整限制及失败/生命周期见行为§17。

## 注音片段时间轴（0.96 / 合同1.27）

| type | 必填业务字段 |
|---|---|
| `lyrics.setRubySegmentTime` | 五级`target`及字符串`startUs/endUs` |
| `lyrics.clearRubySegmentTime` / `lyrics.removeRubySegment` | 五级`target` |
| `lyrics.setRubySegmentText` | 五级`target`及字符串`text` |
| `lyrics.addRubySegment` | 四级`target`、`text`、必填可null的`afterSegmentId`；新片段无时间 |
| `lyrics.reorderRubySegments` | 四级`target`及完整`orderedSegmentIds` |
| `lyrics.setRubySegmentTimes` | 1..1000条`changes[{target,startUs,endUs}]` |

四级目标为branchId/lineId/tokenId/rubyId，五级再加segmentId。严格字段不可混用旧token目标或CLR别名。rubyResult提供真实数量/目标/建议选择/新身份；撤销重做保真实反馈。segments读模型保时间与状态，完整预算计入每个片段。多分支项目TTML导入须options.sourceBranchId选择源分支；全文档导出scope=document且只能ttml.project，禁止branchId含null。详细行为见§18。

## 直接效果包项目预览（0.98 / 合同1.29）

历史0.98记录：复用现有project预览路由，当时实际Worker协商成功才公布package-lyrics-scene-v1。compileSource/source、imageEffect/imageEffect与material/material为完整互斥诊断payload；runtime保原occurrence。完整公共数组768项/512KiB，分页128项/最多32页，不截断。source IsError或runtime Error禁止新帧，failed无latest，旧成功lease保TTL。含preset的包、非Color背景、外媒体字体闭包与B4包任务仍拒绝；详见统一API§20。

## 同帧测量来源勘误（合同1.32.1）

历史合同1.32.1记录：当时orchestration/measurements的完整固定输入沿真实Worker握手选择三种静态来源、package-lyrics-scene-v1 或 package-lyrics-scene-presets-v1。后者仅表示直接包与固定静态预设的有限测量，不扩大 preview/validation/export 的来源准入；仍限 Color 背景与完整 codec/测量预算。产品保持0.98.1，本次为已实现行为的合同勘误，未新增 fresh HTTP 验收；详见统一API§13.21。

## 已保存工程图片背景预览（产品0.99.0／合同1.33.0）

历史0.99有限切片记录（现行来源见下方P1/P2节）：当时project预览自动选择project-image-preview-v1；runtime-info的兼容sourceProfile仍为static-lyrics-scene-v1，sourceProfiles仅在独立图片Worker握手成功后增加该值。客户端仍只交工程ID和精确修订，不提交图片bytes、路径、profile或grant。只读取ProjectStore已加载的固定Image闭包；原图2MiB、单边4096、解码16MiB、完整envelope4194280字节，超限拒绝。基础为无预设的static/static-analysis/直接package；该0.99预览验收未覆盖validation/export/measurement或模板候选图片输入。后续0.100图片正式验证另需独立project-image-validation-v1能力，见下节；此0.99历史切片当时不含export/measurement与模板候选图片输入。匹配发布d961895的有限fresh HTTP final6通过1175请求／9760断言；本地回归、Oracle预检与合同形状检查分开登记，具体范围和历史失败详见统一API§22与实施§77。

## 已保存工程图片背景正式验证（产品0.100.0／合同1.34.0）

历史0.100有限切片记录（现行来源见下方P1/P2节）：当时validation-capabilities/validation-jobs/result/diagnostics接入独立project-image-validation-v1来源；图片预览与包Worker能力不能代替独立图片验证探测。公共请求保持精确expectedRevision、framesPerOutput、metadata；不接图片bytes/path/profile/grant。Host固定ProjectStore已加载的受管Image闭包，仅无preset的static/static-analysis/直接package基础源；完整验证输入4194272字节（4MiB减32字节KTI1头），原图2MiB、单边4096、解码16MiB、基础snapshot4MiB。报告与诊断沿原Runtime和既有取消/deadline/close排水；图片export/measurement、video/font/preset组合与完整B3/B4/B5/B6独立。匹配发布7a03863（sourceDirty=false）的fresh HTTP final1有限切片通过1412请求／10582断言，两次Server exit0、33个观察身份（26个Worker）全部退出、3349文件凭证扫描通过；三基础源普通/透明各4帧、完整报告与原Runtime报告Oracle、错误/预算/控制及一次orderly restart/recovery在有限范围内。正式validation没有PNG，报告Oracle不构成像素验收；旧0.99 Worker独立10请求／476断言、199文件负例拒绝503 taskUnavailable／WorkerImageValidationUnavailable，未并入主计数。实际证据与范围见统一API§10.1和实施§78。

## P1/P2 接口同步（合同1.36.0）

现行P1/P2新增lyrics-import-plans、fonts、fonts/inspect和edit-previews四个公共操作，四个背景命令及project.applyEdits严格分型。创建可省略音频和歌词，直接从真实空工程开始；上传按流处理，部署字节限额maxAssetBytes/maxAssetQuotaBytes固定返回Int64或null。settings/resources提供背景与授权内容身份。匹配Worker独立协商后，preview/measurement/validation/export消费runtime-project-input-v1固定媒体/包字体闭包及完整包+预设来源，HTTP不接主机路径或私有grant；旧图片profile仅保留其历史能力与预算，不覆盖当前统一输入。候选candidateRevision与source.sourceRevision保持不透明candidate身份，baseProjectRevision仍是精确工程修订，测量候选三字段全有/全无且非null且expectedRevision必须匹配baseProjectRevision。导出quality三档和可空宿主策略沿原任务链；当前实际可用来源以各sourceProfiles为准，不从兼容单值推断。详见行为§6.12、§10、§11；合同形状、源码实现、真实Worker/HTTP及浏览器验收分开登记，历史证据数字不扩大为当前验收。

## P3/P4 交接（历史合同1.38.1）

P3通过effect-packages/inspect和template-preview-options读取固定包声明、完整模板及试样目标；definitionFormat=full支持高级定义、包材质、有序配方，clips与高级Node目标复用工程分配命令。P4作者草稿支持完整源码/配置读取、固定source修订、同步200或异步202编译、当前live job编译结果、authorDraft试样预览/验证和个人配置库CAS命令。作者routes统一映射行为§21，模板工坊见§24。预览input保持原来源kind，authorDraft另保持同一draft ID；不能在project与authorDraft之间切换。产品0.104.1的边界修复和本次合同勘误独立于P4产品0.104.0的167请求／1142断言fresh HTTP证据；P5增量匹配发布源码f4d5e2a的P4作者流程fresh HTTP通过205请求／1368断言，证据根artifacts/p5-final-01041/http-r1，仍不扩大为浏览器、平台或性能验收。

## CPU/GPU 显式选择（产品0.106.0／合同1.39.0）

GET render-backends返回部署启动时固定的defaultBackend与当前隔离Worker实际设备检查结果；公开选择仅cpu/gpu，底层API分别为raster/vulkan。available=true必须提供非空device摘要，available=false必须提供稳定unavailableReason；设备检查与后续业务产帧独立。显式GPU不可用时失败，不自动回退CPU。

预览创建、工程/作者正式验证、导出与场景测量接受可选renderBackend；创建/任务省略或null使用启动默认，替换编辑候选或PUT input省略/null保留当前预览选择，候选测量省略/null使用候选固定选择。PUT preview-sessions/{id}/render-backend要求精确expectedPreviewGeneration、非null选择及metadata，保持固定输入，成功换代并排水旧Worker；相同后端通过CAS为no-op，失败保留旧代次与帧。

预览会话、帧与事件必须回显renderBackend；新任务固定输入、验证/导出报告及测量结果回显本次执行选择，历史任务/报告缺失或null表示未知，禁止推断CPU。选择不改变工程修订/历史、Host环境或其他客户端偏好。详细生命周期和错误见行为§25；合同形状、匹配发布、真实GPU/HTTP产帧与排水证据分别登记，不扩大为浏览器、跨平台、性能或CPU/GPU位级像素一致性验收。
