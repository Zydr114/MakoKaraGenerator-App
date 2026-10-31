# 后端完整目标 API 目录与阶段变动日志

更新：2026-10-27；当前开发入口为 `main`，已快进纳入P3/P4/P5及两批GPU工作，产品0.106.0／合同1.39.0／HTTP协议1，见[main合入记录](archive/history/backend-main-integration-20261027.md)。固定生产源码基线仍以manifest为准；P1/P2、P3/P4/P5与0.105/0.106原发布/HTTP证据各保阶段归属。本次合入不构成新部署或fresh HTTP。执行顺序和桌面覆盖依据见[补齐计划](archive/plans/backend-api-completion-plan.md)。

**本文是当前 API 目录与阶段变动日志，不替代同一提交中的冻结合同。** §2/§3通过生成清单和严格 OpenAPI union 指向当前公开边界；§4～§7保留阶段扩展的来源和兼容语义，历史提案只在其来源明确标记时保留。P1/P2、P3和P4均已按各自范围登记，P5完成合同勘误、目录/行为核对与前端交接；当前冻结合同为1.39.0，包含CPU/GPU显式选择、设备可用性和固定预览后端切换。源码、合同形状、真实HTTP与完整产品能力仍分别判断，浏览器、跨平台及性能验收继续独立。

## 1. 阅读与调用约定

- 当前完整规范：[生成 API 清单](../web/api-index.md#冻结-api-清单)、[OpenAPI](../web/openapi.json)、[行为合同](architecture/backend-api.md)。当前合同为 **102 个路径／113 个显式方法、560 schemas、3331 examples（2483 negative examples）**；已有 DTO、错误和查询参数以这些生成物为准，本目录不复制另一套 schema。
- §2 是按领域的导航，完整 method/path/status/request/response 表只维护在生成的 [API 清单](../web/api-index.md#冻结-api-清单)。后面的阶段表均省略共同前缀 `/api/v1`。`{id}` 的业务对象由路径决定，不把工程、库、草稿或 job 的 ID 混用。
- 已有身份和请求方式不变：注册使用 bootstrap；其余业务入口使用 access/clientId，遵守当前 Origin 规则。数据修改使用现有 metadata/回执，工程 expectedRevision、库 expectedRevision、作者 expectedDraftRevision 分别对应自己的状态。
- 现有 GET 表中请求 `-` 只表示没有命名 JSON 请求 DTO，**不表示没有必需 query**。歌词/样式/场景等使用精确修订查询；分页、kind/target 等见 OpenAPI。新接口的输入在 §4 明列。
- `202` 是接收，不是完成；通过 jobs/SSE 或 preview state 取得终态。Content URL 沿现有认证和 Range/HEAD 规则使用，不将 token 拼进 URL。
- **P3历史合同（1.37.0）**范围为 **93 个路径 / 103 个显式方法；530 schemas；3042 examples（2294 negative examples）；96 种工程命令；模板库 5 种命令**。当时新增歌词导入规划、字体目录/检查、编辑候选预览、效果包检查和模板试样选项；HEAD 单计，5 个兼容别名保留，不计全局 OPTIONS。该批生成物与验收登记于 `c4146d4`，不覆盖当前1.39.0计数。
- **P4/P5历史合同（1.38.0／1.38.1）**范围为 **100 个路径 / 111 个显式方法；556 schemas；3156 examples（2374 negative examples）；96 种工程命令；模板库 5 种命令；个人配置库 5 种命令**。P4的主要工作是已有端点的业务能力扩展，新增路由只是其中一部分。

## 2. 当前全部 HTTP 操作

完整的 method/path、认证、请求 DTO、成功状态、响应 DTO 和行为章节由路由元数据生成，维护于[冻结 API 清单](../web/api-index.md#冻结-api-清单)；机器调用形状以[OpenAPI](../web/openapi.json)为准，跨请求语义以[行为合同](architecture/backend-api.md)为准。本节不再复制一份会漏掉 P3/P4 路由的手写全表。当前冻结范围是 **102 个路径／113 个显式方法**（含5个兼容别名；HEAD单计；全局OPTIONS不计），另有 **560 schemas、3331 examples（2483 negative examples）**。

| 领域导航 | 公开入口（共同前缀为 `/api/v1`） | 前端依据 |
|---|---|---|
| 渲染后端选择 | `render-backends`、`preview-sessions/{id}/render-backend`，各预览/验证/导出/测量请求的 `renderBackend` 与结果回显 | [行为§25](architecture/backend-api.md#render-backend-selection)；[前端CPU/GPU流程](../web/frontend-workflows.md#53-cpugpu选择和已有预览切换) |
| 主机、客户端和事件 | `health/live`、`capabilities`、`clients`、`clients/{id}/heartbeat`、`clients/{id}/state`、`events` | [行为§2～§4](architecture/backend-api.md#2-路由索引与能力)；[启动与身份](../web/frontend-workflows.md#1-从启动到第一个业务请求) |
| 资产、字体、目录和导入 | `assets`、`assets/{id}/content`、`fonts`、`fonts/inspect`、`file-roots`、`file-roots/{rootGrantId}/entries`、`file-handles`、`lyrics-import-plans` | [前端资源与空工程](../web/frontend-workflows.md#3-资源上传空工程和持久化)；OpenAPI 路由清单 |
| 工程生命周期与基础读模型 | `project-sessions`、`project-sessions/{id}`、`history`、`lyrics/lines`、`lyrics/branches`、`lyrics/window`、`settings`、`style`、`resolved-style`、`resources`、`preset-applications` | [行为§6、§17](architecture/backend-api.md#6-工程创建查询与具名命令)；[前端编辑流程](../web/frontend-workflows.md#4-编辑和工程历史) |
| 歌词、样式、编排和工程命令 | `project-sessions/{id}/commands`、`orchestration/*`、`lyrics-export-plans`、`lyrics-exports` 及内容读取 | [当前工程命令](../web/api-index.md#当前工程命令)；[OpenAPI strict union](../web/openapi.json) |
| 波形、预览和编辑候选 | `waveform`、`waveform/peaks`、`runtime-info`、`preview-sessions`、`edit-previews`、`preset-application-previews`、`template-preview-sessions`、`template-preview-options`、`preview-frames/*`、`preview-sessions/{id}/diagnostics` | [预览、候选与同帧测量](../web/frontend-workflows.md#5-预览候选与同帧测量)；[统一运行输入§23](architecture/backend-api.md#unified-runtime-input-01020) |
| 模板、包检查与预设工坊 | `template-libraries`、`templates`、`templates/detail`、`template-libraries/{id}/commands`、`effect-packages/inspect`、`.mktpl` imports/exports/transfers | [完整模板工坊](../web/frontend-workflows.md#7-完整模板和预设工坊)；[行为§24](architecture/backend-api.md#24-p3-完整模板预设工坊01030合同1370) |
| 作者草稿、源码、编译、试样和个人配置库 | `drafts`、`drafts/{id}/sources`、`drafts/{id}/configuration`、`drafts/{id}/compilation*`、`drafts/{id}/validation-jobs`、`adopt-source-revision`、`script-configurations*` | [作者草稿和个人配置库](../web/frontend-workflows.md#8-作者草稿和个人配置库闭环)；[行为§21](architecture/backend-api.md#backend-author-p4) |
| 任务、恢复、验证、导出和分析 | `open-jobs`、`recover-jobs`、`save-jobs`、`close`、`jobs/{id}`、`recovery-*`、`validation-*`、`export-*`、`analysis-*` 及各结果/诊断/内容/取消入口 | [正式验证、导出和分析](../web/frontend-workflows.md#6-正式验证导出和分析)；[任务行为§8～§12](architecture/backend-api.md#8-当前jobs与恢复读取) |
| 兼容别名 | `/health/live`、`/capabilities`、`/assets`、`/project-sessions`、`/project-sessions/{id}` | 只按生成清单列出的别名调用，不把别名计为新业务能力 |

所有写入仍遵守 access/clientId、Origin、request receipt、expectedRevision、SSE、Range/HEAD 和资源租约规则；`202` 只代表接收，必须通过 job/preview 状态读取终态。`GET` 请求写作 `-` 只表示没有命名 JSON body，不表示没有必需 query。

## 3. 当前全部工程命令及独立模板库/个人配置库命令

工程命令的完整96项严格 union 由生成的[当前工程命令清单](../web/api-index.md#当前工程命令)和 OpenAPI `ProjectCommandDtoInput` 共同维护；本节只给领域导航，避免再次手写一份容易落后的96项表。工程写请求 `POST /api/v1/project-sessions/{id}/commands` 的外层字段是 `expectedRevision`、`command`、顶层 `requestId`、`hostInstanceId`、`validUntilUtc`；工程命令没有嵌套 `metadata`。每次请求最多一次原子工程历史提交，schema 不替代目标存在性和领域校验。

| 命令领域 | 包含的 type 族 | 前端依据 |
|---|---|---|
| 历史、歌词、来源和注音 | `history.*`、`lyrics.*`（时间、正文、来源、元素、ruby） | [编辑和工程历史](../web/frontend-workflows.md#4-编辑和工程历史)；生成96项清单 |
| 音频、媒体和批量编辑 | `audio.*`、`composition.*`、`project.applyEdits` | [资源上传、空工程和持久化](../web/frontend-workflows.md#3-资源上传空工程和持久化)；统一运行输入 |
| 样式、预设和模板目标 | `styles.*`、`presetApplications.*` | [模板工坊](../web/frontend-workflows.md#7-完整模板和预设工坊)；行为§13/§24 |
| 编排、节点、布局、动画和片段 | `orchestration.*` | [行为§13](architecture/backend-api.md#13-独立模板库原0840合同190p3扩展见24)；OpenAPI strict union |

模板库写入口 `POST /api/v1/template-libraries/{id}/commands` 只有 **5 种**严格 type：`create`、`replace`、`copy`、`rename`、`delete`。其外层是 `expectedRevision`、嵌套 `metadata` 和 `command`；库修订是缺失或 SHA-256 字符串，不是工程 Int64 修订。完整输入/结果和 full definition 见[生成清单的独立模板库章节](../web/api-index.md#独立模板库)及[行为§24](architecture/backend-api.md#24-p3-完整模板预设工坊01030合同1370)。

个人配置库写入口 `POST /api/v1/script-configurations/commands` 也只有 **5 种**严格 type：`create`、`replace`、`copy`、`rename`、`delete`。其外层同样是 `expectedRevision`、嵌套 `metadata` 和 `command`；配置来源必须明确区分草稿修订、配置库修订或授权 package 引用，不能由服务静默取最后一次成功编译版本。完整字段和 CAS 语义见[作者/配置工作流](../web/frontend-workflows.md#83-个人配置库)及[行为§21](architecture/backend-api.md#backend-author-p4)。

## 4. 新增 HTTP 操作及阶段归属（14 个）

N01～N04已实现并通过代表HTTP，见补齐计划§8；N05/N06已接入源码并通过匹配发布 fresh HTTP，见补齐计划§9；P4 N07～N14及E12～E15已接入源码（`3738748`、`91b8038`、`046e0ec`、`2115037`、`aac4aa7`）。所有条目复用现有身份、错误、资源、任务与回执机制；不引入新的上传系统、播放器服务、模板存储或作者运行引擎。

| 编号/阶段 | 方法与路径 | 关键输入 | 完整输出/作用 | 复用实现 |
|---|---|---|---|---|
| N01 / P1 已接入 | `POST /lyrics-import-plans` | `assetId`；可选现行 `options`（importerId/encodingName/sourceBranchId/splitRule/estimateSplitTimes） | 固定授权资源的检测、格式/编码候选、TTML 来源分支、完整报告和预览行；不创建批准票据、不修改工程；解析失败放 `preparationError` | LyricsImportEditUseCase.Detect/Prepare、原导入器；源码已接入，代表 HTTP 验收另行登记 |
| N02 / P1 已接入 | `GET /fonts` | 可选单次 `family` 查询（仅校验查询形状，不在 Host 过滤 Worker 环境） | 实际 Worker 环境、字体族目录及协议版本；Worker 未部署返回明确不可用 | SkiaFontInspector.AvailableFamilies、FontWorkerContract；源码已接入，未宣称跨平台等价 |
| N03 / P1 已接入 | `POST /fonts/inspect` | 严格八字段：text、family、fontSizePx、fontWeight、italic、fallbackFamilies、可选 sessionId/expectedRevision | 实际 Worker 整形结果、解析族、可用字重/斜体和逐元素 fallback/missingGlyph 诊断；sessionId 与 expectedRevision 成对校验；不改样式 | SkiaFontInspector.Inspect/FontWorkerContract；源码已接入，HTTP/跨平台验收另计 |
| N04 / P1 已接入 | `POST /project-sessions/{id}/edit-previews` | `expectedRevision/edits/size`，可选 `alphaMode`、`previewId`、`expectedPreviewGeneration`、metadata | 固定工程修订上的候选预览会话；返回 candidateRevision、合并 changeSet、逐编辑反馈，帧/诊断沿既有 preview；候选不改工程/history，过期工程修订拒绝 | 同一 Prepare + PreviewSession；源码与定向测试已接入，真实 HTTP/浏览器验收另计 |
| N05 / P3 已接入 | `POST /effect-packages/inspect` | `packageAssetId` 可与 `expectedPackageRevision` 成对提供，或改用互斥的完整 `reference`（`asset`／`library`／`configuration` 来源） | 包身份、manifest、exports、参数 schema/默认值/约束、bindingKinds/inputs、完整文件角色/身份摘要、结构诊断；不运行脚本 | EffectPackageDefinition/现有包读取 |
| N06 / P3 | `POST /template-preview-options` | 库模板精确引用或完整草稿 definition | 单/多行 sample 选项、可用目标稳定 key/binding、材质槽位、试样时长等；前端无需自行生成目标 | TemplateSampleScene/TemplateSampleResources |
| N07 / P4 已接入 | `GET /drafts` | 当前 client 身份；沿现有列表方式 | 所有本 client 可恢复草稿的摘要、draftRevision/current/adopted sourceRevision；供刷新后找回 | 现有作者草稿存储 |
| N08 / P4 已接入 | `GET /drafts/{id}/sources` | expectedDraftRevision、sourceRevision | 固定修订的 C# 文件完整 path/text 集合及包身份；可读 current/adopted 的保留修订 | 现有不可变源码闭包 |
| N09 / P4 已接入 | `PUT /drafts/{id}/configuration` | expectedDraftRevision、完整 configuration、metadata | 新 draftRevision、完整配置与参数/试样可用性；不保存个人库、不改当前工程 | ScriptConfiguration + draft 状态 |
| N10 / P4 已接入 | `GET /jobs/{id}/compilation-result` | jobId | 异步编译固定 sourceRevision 的完整结果/诊断；不依赖草稿随后是否又编辑 | 现有编译结果与 JobStore |
| N11 / P4 已接入 | `POST /drafts/{id}/validation-jobs` | expectedDraftRevision、sampleId、targetKey、可选 materialSlot、metadata | 202 JobResponse；固定 640×360、30fps、12秒、360 frames/output；结果/诊断/取消沿既有 validation-result/validation-diagnostics/jobs/cancel | ScriptSampleBuilder/ScriptSampleValidator + validation jobs |
| N12 / P4 已接入 | `GET /script-configurations` | 首次可不带 expectedRevision；后续按库修订读取 | 库 revision、配置 id/name/kind、包/export/sourceRevision 和可用性摘要 | ScriptConfigurationRepository |
| N13 / P4 已接入 | `GET /script-configurations/{id}` | 精确库 expectedRevision | 完整配置、固定包引用/参数 schema、完整保存参数/相对目标/范围/种子/层 | ScriptConfigurationLibrary/Repository |
| N14 / P4 已接入 | `POST /script-configurations/commands` | expectedRevision、metadata、create/replace/copy/rename/delete union | 配置 id、previousRevision/new revision、changed；固定源码来源，库 CAS | ScriptConfigurationRepository 与现有回执模式 |

P3接入前历史基线为 97 方法/87 路径；N01～N06 接入后为 103 方法/93 路径（N05/N06各新增一条路径）；N07～N14随后进入当前源码，其中N07复用既有 `/drafts` 路径。当前冻结合同共96种工程命令，另有模板库5种和个人配置库5种。

### 新接口的业务边界

- N01 已是可选的导入信息读取；不生成必须消费的批准票据，不要求导入命令必须先调用它。源格式/分支/编码选项沿已有导入器，禁止根据显示名称猜 ID。
- N02/N03 已接入真实 Worker 字体环境；环境必须与实际渲染一致。服务器枚举字体后却让另一环境的 Worker 渲染，不能作为完成。暂不可用的信息明确返回状态，不伪造字体可用。
- N04 已复用现有 preview 的生命周期。`candidateRevision` 表示固定候选，帧/测量绑定该候选及 generation；基础工程修订单独保留。候选替换或丢弃不修改工程，不增加撤销历史。正式应用仍校验当前工程修订，不直接提交一个过期候选。
- N05 返回完整声明和结构化参数类型，前端可据此构建原参数面板；取资源字节复用已有 asset/content。库内包由服务固定并提供可用引用，不迫使前端从服务器路径读取或把整包重复上传。
- N06/N11 复用原试样目标/材质语义。普通/透明选择和诊断能被读取；不新建第二份手写样例业务规则。
- N08 不把“已有保存源码接口”误当成“前端可读源码”；必须实际返回完整固定文本，并保留源码定位。原桌面仅编辑固定包中 C# 文件的行为保持，不另扩为通用文件管理器。
- N10 返回 job 固定结果；草稿最新编译查询仍是旧接口，二者用途不同。任务失败/取消先从 GET job 获得，不用空诊断假装成功。
- N12～N14 的 personal 配置复用原存储模型；配置source严格区分draft、configuration revision与授权package三分支。保存错误源码与运行验证状态相互独立，不强制成功编译/验证成为保存前提；合法结构和引用继续由 Core 验证。

## 5. 已接入工程命令（5 种，P1）

共用现有 `POST /project-sessions/{id}/commands`，均在 P1。不得为这些命令另建工程修订/撤销机制。

| 编号 | 新 type | 业务输入 | 结果与语义 | 复用 |
|---|---|---|---|---|
| C01 已接入 | `composition.setBackgroundAsset` | assetId、kind=image/video；fit/playback 可省略沿原默认 | 固定受权图片/视频替换当前背景；返回新 settings/resource 身份；一次历史；无需先保存工程 | ProjectBackgroundEditor.SetImage/SetVideo、资源准备 |
| C02 已接入 | `composition.clearBackground` | 无业务字段 | 清除媒体引用，保底色；撤销可恢复原资源 | ProjectBackgroundEditor.Clear |
| C03 已接入 | `composition.setBackgroundFit` | fit=contain/cover/original，对应原枚举 | 更新适配；一次历史/no-op 保持 | ProjectBackgroundEditor.SetFit |
| C04 已接入 | `composition.setBackgroundPlayback` | playback=loop/freezeLastFrame，对应原枚举 | 视频时间映射沿原 Core 规则；不引入独立音轨/视频轨道 | ProjectBackgroundEditor.SetPlaybackMode |
| C05 已接入 | `project.applyEdits` | edits[]，元素复用既有工程编辑的 type/字段；禁止资源导入、任务、历史和嵌套批处理 | 基础修订上顺序准备、整体校验、一次历史提交；中途失败不部分写入；返回合并 changeSet/逐编辑反馈 | 原 Prepare/Commit、ProjectWorkspace 历史；定向测试已接入 |

`applyEdits` 服务于一次编辑草稿的歌词、样式、场景/动画等数据修改；不把上传、保存 job、编译、undo/redo 或嵌套 applyEdits 当作草稿项，不另建跨服务事务。背景导入等已有单操作可直接调用，无需批处理。候选试看和正式提交共用相同编辑解释，不能各写一套纯函数而逐渐漂移。不给编辑数组新增与业务无关的计数门槛。

## 6. 现有 API 的必要扩展

没有列在本表的现有操作继续保留。E01～E08已接入P1/P2统一实现并通过本计划代表HTTP；E09～E11已接入P3源码、匹配发布与 fresh HTTP；E12～E15已接入源码。已有高级Line/Token闭包运行不等于高级定义编辑或作者配置库完成。

| 编号/阶段 | 现有接口/DTO | 当前实现扩展 | 兼容及前端意义 |
|---|---|---|---|
| E01 / P1 已接入 | GET settings/resources；POST assets | settings 增 background{kind,fit,playback,resourceId}；resources 为实际背景/已保存资源给可消费 assetId/contentUrl 与准备状态；上传接收原媒体支持的视频 MIME | 底色/音频字段不删；资源不再只能看到 unsupportedContent；新工程不依赖旧桌面预存背景 |
| E02 / P1/P2 已接入 | GET resources/waveform、媒体准备能力 | 返回实际源音频content及波形；准备/分析/导出声明六音轨家族，实际解码门禁保持 | 现有音频 content 和波形不换协议；不新增播放/暂停 API；不把一切编码格式都要求前端重解码 |
| E03 / P1/P2 已接入 | POST orchestration/measurements | 可显式选择 edit-preview 的 previewId/expectedPreviewGeneration/candidateRevision；缺省仍测量当前工程；统一接入Image/Video、完整包/高级预设与包字体 | 真实同帧几何与 PNG；候选与正式修订可区分；保留 time/layout union |
| E04 / P1 已接入候选来源 | PreviewSession/换源/帧请求/diagnostics | 支持固定工程编辑候选来源，按候选 revision/generation 更新、测量和读取 | 沿用 GET 状态、frame requests、close、SSE；不以每次指针移动写工程替代试看 |
| E05 / P2 已完成 | POST export-jobs；ExportJobRequest/Input/Report | `quality=draft/standard/high`，报告回显请求/实际质量；formats/includeAudio 保持 | 省略质量沿现行默认；三档及180秒代表整曲产物已通过真实HTTP，见实施§80 |
| E06 / P2 已接入 | runtime-info、validation/export/analysis-capabilities | 导出能力 DTO 可公布质量、可空宿主帧/时长/媒体/音频/超时策略；资源策略由宿主配置，null 不施加该维度限额 | 不把策略接缝误称为整曲验收；完整输入组合与实际产物另行登记 |
| E07 / P2 已接入 | project/template/preset preview、validation、export、measurement 的准备与 Worker | 固定snapshot+媒体/完整包目录统一准备；direct package+preset、Image/Video、包字体、材质/配方与六家族音轨经原Runtime执行 | 旧来源继续工作；能力公布必须与实际输入执行一致；新功能主要在已有端点里 |
| E08 / P2 已接入 | analysis-jobs、analysis-result、audio.analysis.apply/clear | 使用PCM16 WAV/MP3/FLAC/AAC/MOV/Ogg声明家族；包/预设消费完整已采用分析；结果固定源身份 | 沿原显式采用/清除；不另造“package analysis”路由 |
| E09 / P3 | template detail/create/replace | 显式 `definitionFormat: full`（GET 用同名 query）提供完整定义分支；旧请求省略仍为静态结构 | 高级对象可读可改，不丢包/配方/材质/未选属性；原严格 parser/生成 schema 同步更新 |
| E10 / P3 | template-preview-sessions、preset-application-previews | 接完整库/草稿 definition、单/多行 sample 与选定 target/materialSlot；已有高级Line/Token候选与库内固定试样已可运行；P3扩展完整定义编辑/试样目标 | 不写个人库/工程；固定源修订；使用同一运行链 |
| E11 / P3 | presetApplications.apply/update 及 target/read DTO | 增 clips union（branchId/clipIds）；node 接原模型支持的高级模板；读回完整绑定与可用性 | project/branch/line/token/node 旧写法保持；更新库不自动追改工程 |
| E12 / P4 已接入 | POST/GET drafts、GET compilation、adopt、source-revision-jobs | GET 增完整 configuration/参数与 sample options；create支持授权包asset或configurationId+expectedConfigurationRevision精确个人配置来源；保持 current/adopted 修订可辨 | 不要求成功编译才能保存源码/采用；adopt 不再被说明成个人库发布 |
| E13 / P4 已接入 | POST drafts/{id}/compilation-jobs；GET job/SSE/cancel | 请求可选 executionMode=async 返回202 job；省略保旧同步200；job 关联固定 sourceRevision，结果走 N10 | 可取消编译；不把原同步接口无告知改成202；旧结果标明所编译版本 |
| E14 / P4 已接入 | POST preview-sessions、PUT input | source.kind=`authorDraft`，sourceId为草稿ID，sourceRevision为草稿修订，authorSourceRevision为固定配置源码修订，并带`templateSample:{sampleId,targetKey,materialSlot}`；PUT input保持draft ID，可更新draft/source修订、sample/target/materialSlot并递增generation；创建请求固定previewKind=`authorDraft`、committable=false | 复用 PNG/诊断/帧/close；作者试样不会改歌曲 |
| E15 / P4 已接入 | GET job、validation-result/diagnostics、相关恢复结果 | 接 N11 作者任务；JobResponse输入含draftId/draftRevision/sourceRevision/sampleId/targetKey/materialSlot，报告sourceKind=`authorDraft`、sourceProfile=`author-script-sample-v1`，固定640×360、30fps、12秒、360帧；复用原终态和报告结构 | 不假装作者任务有 projectSessionId；已有工程报告不改语义；持久报告沿现有 recovery 规则 |

### 完整模板定义的最低内容

P3 的 full definition 必须足以读回并原样保存，不能只扩充 summary：

| 内容 | 必须可读写的信息 | 领域依据 |
|---|---|---|
| 模板身份与样式 | name/kind、完整 style、选中 properties；稳定 templateId 由库负责 | StyleTemplate |
| 材质 | 各填充/描边位置的 solid/gradient 或包/export/revision/parameters；原样式支持的其它字段完整保留 | Material 定义与 LyricStyle |
| 配方有序集合 | recipeId/name/target/packageId/revision/exportId/parameters/overlayLayer/enabled/readScope/branchInput/fixedSeed/configurationId | TemplateEffectRecipe |
| 包闭包 | 固定包 revision、manifest、资源文件身份/角色及服务端可解析引用；不暴露任意主机路径 | EffectPackageContents |
| 应用目标 | project、branch、line、token、node(includeDescendants)、clips；真实兼容性信息 | ProjectEffectBinding/PresetApplicationsEditUseCase |
| 试看 | sample、target 稳定 key、materialSlot、来源 revision；完整 frame/diagnostics 链 | TemplateSampleScene |

库更新仍采用完整候选加 CAS；不要求前端逐个控件调用保存。模板中引用的配置会固定包/参数快照，配置库随后变更不静默追改旧模板或工程。

### 作者配置数据与库命令

N09/N13 至少完整传输：name、固定 package/sourceRevision、exportId、parameters、relativeTarget、readScope、branchInput、fixedSeed、overlayLayer，以及派生 kind/bindingKinds。与脚本参数面板有关的类型、默认值、枚举选项、范围和输入声明来自原包定义；这些是业务 schema，不是新增 HTTP 限制。

N14 的 5 个独立库命令：

| type | 主要输入 | 作用 |
|---|---|---|
| `create` | 完整configuration，source为draftId/expectedDraftRevision/sourceRevision、configurationId/expectedConfigurationRevision或package三分支 | 分配配置 id，保存至个人库，返回库新 revision |
| `replace` | configurationId + 同上固定配置来源 | 保 id 替换内容，不自动更新使用方快照 |
| `copy` | configurationId、name；源库由外层expectedRevision固定 | 复制为新 id；对应另存 |
| `rename` | configurationId、name | 保其它字段/包闭包 |
| `delete` | configurationId | 删除库项；已被模板/工程固定的快照不受影响 |

请求外层统一带库 expectedRevision/metadata。保存引用当前明确选择的源码，不能用 sourceRevision 缺失时静默取“最后一次编译成功版本”。代码语法错误不妨碍保存草稿；非法配置结构仍由原 Core 返回诊断。前端的未提交参数撤销/重做在本地进行，工程历史仅用于工程。

## 7. 目标调用顺序（前端交接）

### 工程与歌词

注册 client → GET capabilities/state → 创建空工程（或带可选歌词/音频）→ 上传歌词/音频 → 可选 N01 读取导入选项 → 创建工程或 importAsset/audio.replaceAsset → GET branches/window/settings/waveform → 原编辑 commands → undo/redo → file-handles + save-jobs → GET job 直到终态。再次进入可从授权目录 open-jobs；recent 列表由前端记 root/relativePath，不记旧访问 token。

### 外观与媒体

上传背景 → C01/C03/C04 → GET settings/resources → N02 选择实际字体 → styles.patch。草稿编辑用 N04 → 既有 frame requests/测量 → 确认后 C05 → 用新工程 revision 更新主预览输入；取消只关闭/丢弃候选。背景视频 Loop/FreezeLastFrame 和字幕时间共享工程时间轴，前端不实现另一套视频取帧规则。

### 预览、验证、整曲导出

读取 runtime/task capabilities → project preview session → 按前端播放头请求帧，SSE 通知后取 PNG；编辑后换源 generation。验证走 validation-jobs/报告；导出显式工程时长 + format/includeAudio/quality → export-jobs → 进度/取消/终态 → export-result/export-content。无需强制先成功验证才允许导出。音频分析若需要，先 analysis-job，再显式 apply；不自动改工程历史。

### 模板与作者

P2已有高级Line/Token可从完整.mktpl导入→apply/update或候选/固定试样。P3当前实现提供模板库 detail(full)/完整 definition、N05包检查、N06试样选项、完整 draft/library preview source、template commands 保存，以及 clips/高级Node目标扩展；顺序为 full detail或draft → N05/N06 → template preview → template commands → preset candidate → apply/update。配方/材质包先 N05 检查声明；分享沿现有 `.mktpl` 传输。

作者从 N05 或 N13 固定包/配置 → create draft → N08 读源码 → 原 source-revision 保存 → 可选异步 compilation/取消/N10 → N09 改参数 → authorDraft preview 或 N11 验证 → 显式 adopt → N14 保存个人配置 → 转为模板配方/材质 → 应用工程。源码保存、编译、验证、采用与入库各有明确状态，前端可显示错误并继续编辑。

## 8. 阶段 API 变动日志

| 日期/状态 | 阶段 | API变化 | 实际源码、合同与验收依据 | 前端调用示例 |
|---|---|---|---|---|
| 2026-10-25 / 代表范围已完成 | P1/P2 | 91路径/101方法、96工程命令；四个新增HTTP操作、四个媒体背景命令与`applyEdits`；空创建/流式上传 | 源码/发布基线 `92c3e84`；[补齐计划§8](archive/plans/backend-api-completion-plan.md#8-p1p2-实施记录)、[实施§80](archive/history/backend-extraction-progress.md#api-completion-01020)、[行为§23](architecture/backend-api.md#unified-runtime-input-01020)、[合同执行§13](../web/api-contract.md#13-p1p2统一输入与编辑合同)。产品0.102.0／合同1.36.0；匹配 fresh HTTP 320请求／2215断言 | [资源、空工程与持久化§3](../web/frontend-workflows.md#3-资源上传空工程和持久化)、[编辑§4](../web/frontend-workflows.md#4-编辑和工程历史)、[预览§5](../web/frontend-workflows.md#5-预览候选与同帧测量)、[任务§6](../web/frontend-workflows.md#6-正式验证导出和分析) |
| 已接入 | P1 | N01～N04、C01～C05、E01～E04 | 同上；导入规划、字体检查、候选预览和原子应用的实际边界见[实施§80](archive/history/backend-extraction-progress.md#api-completion-01020)及生成合同[API清单](../web/api-index.md) | [上传与创建](../web/frontend-workflows.md#3-资源上传空工程和持久化)、[候选预览](../web/frontend-workflows.md#5-预览候选与同帧测量) |
| 已接入 | P2 | E05～E08及E02/E03完整输入；已有高级Line/Token闭包可执行，`advancedRuntimeAvailable` | 同上；统一Runtime、媒体/包字体/包预设、六音轨家族、两格式三质量与nullable策略均在[行为§23](architecture/backend-api.md#unified-runtime-input-01020)和[补齐计划§8](archive/plans/backend-api-completion-plan.md#8-p1p2-实施记录)登记。180秒整曲/运行中取消等结果仍是代表范围 | [预览与候选](../web/frontend-workflows.md#5-预览候选与同帧测量)、[正式任务](../web/frontend-workflows.md#6-正式验证导出和分析) |
| 源码、匹配发布与 fresh HTTP 已通过 | P3 | N05～N06；E09完整定义读写；E10完整草稿/试样目标；E11 clips/高级Node | 源码/发布基线 `3005d5c`；[补齐计划§9](archive/plans/backend-api-completion-plan.md#9-p3-实施记录)、[行为§24](architecture/backend-api.md#24-p3-完整模板预设工坊01030合同1370)、[合同执行§14](../web/api-contract.md#14-p3完整模板与预设工坊01030合同1370)。fresh HTTP `artifacts/p3-http-3005d5c-r1/` 通过174请求／1151断言；freeze/validate登记 `c4146d4` | [完整模板和预设工坊§7](../web/frontend-workflows.md#7-完整模板和预设工坊) |
| 源码、合同与有限HTTP已通过 | P4 | N07～N14/E12～E15 | P4源码/发布基线 `aa663b4`，合同生产基线 `878f947`；[补齐计划§10](archive/plans/backend-api-completion-plan.md#10-p4实施记录)、[行为§21](architecture/backend-api.md#backend-author-p4)、[合同执行§15](../web/api-contract.md#15-p4作者与个人配置库01041合同1381)。产品0.104.0／合同1.38.0的匹配 fresh HTTP `artifacts/p4-author-01040-r1/http-final1/` 通过167请求／1142断言；P5勘误不重命名该历史证据 | [作者草稿和个人配置库§8](../web/frontend-workflows.md#8-作者草稿和个人配置库闭环) |
| 2026-10-25 / 审查已完成 | P5 | 按真实实现收口完整路由、命令、例子、能力声明与兼容说明；预览来源kind边界修复 | 当前源码修复提交 `ac685e1`；[P5审查报告](archive/research/backend-api-p5-closure-audit-20261025.md)、[当前生成清单](../web/api-index.md)、[当前合同执行§15](../web/api-contract.md#15-p4作者与个人配置库01041合同1381)。产品0.104.1／合同1.38.1；P5匹配发布源码`f4d5e2a`的增量fresh HTTP通过205请求／1368断言，P4原0.104.0证据仍独立归属 | [作者/配置工作流](../web/frontend-workflows.md#8-作者草稿和个人配置库闭环)、[验证边界§10](../web/frontend-workflows.md#10-当前文档权威和验证边界) |
| 2026-10-26 / 本机验收完成 | GPU管线 | 显式Vulkan管线与默认CPU，原公共HTTP形状保持 | 产品0.105.0／合同1.38.1；[GPU验收报告](archive/acceptance/gpu-render-acceptance-20261026.md)，原fresh HTTP2376请求／18405断言 | 运行配置与边界见原报告 |
| 2026-10-26 / 本机有限HTTP完成 | CPU/GPU显式API | 新增render-backends及固定预览后端切换，各渲染入口固定选择/回显 | 产品0.106.0／合同1.39.0；[验收报告](archive/acceptance/render-backend-api-acceptance-20261026.md)，原fresh HTTP770／5013及不可用负例46／343 | [CPU/GPU选择](../web/frontend-workflows.md#53-cpugpu选择和已有预览切换) |
| 2026-10-27 / main合入 | 分支集成 | 95个提交完整快进纳入main，API/产品版本不变 | [main合入记录](archive/history/backend-main-integration-20261027.md)，新跑定向回归与原部署/留档复核；没有新部署或fresh HTTP | 从同一main提交的web合同接入 |

每次交付在相应行登记实际新增/扩展/兼容行为；HTTP最终证据按阶段分别见上表的[补齐计划§8](archive/plans/backend-api-completion-plan.md#8-p1p2-实施记录)、[§9](archive/plans/backend-api-completion-plan.md#9-p3-实施记录)和[§10](archive/plans/backend-api-completion-plan.md#10-p4实施记录)。历史提案不作为当前冻结 API；当前可调用范围以同一提交生成的 `web/*`、行为合同和前端工作流为准。
