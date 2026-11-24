# 前端 API 调用速查

本文是产品 **0.120.0**、API 合同 **1.62.0**、HTTP 协议 **1** 的前端使用顺序。它只描述当前公开 HTTP；字段形状以同一提交的 [openapi.json](openapi.json) 和 [api-contract.md](api-contract.md) 为准，跨请求语义以 [详细行为合同](../docs/architecture/backend-api.md) 为准。当前清单为 103 个路径、115 个显式方法、106 个工程命令（准确计数以 [api-index.md](api-index.md) 为准）。CPU/GPU选择与作者闭包分别按当前合同使用，各版本功能与性能证据单列。

## 1. 从启动到第一个业务请求

1. 可信本机启动器以匹配的 self-contained Server/Worker 发布目录启动后端，解析 `WEB_BACKEND_READY`，从私有 `credentialFile` 读取 bootstrap。浏览器不能读取该文件，Server 也没有任意网页可取的 bootstrap 路由。
2. 用 bootstrap Bearer 调用 `POST /api/v1/clients`，正文为 `{"protocolVersion":"1"}`；恢复时增加 `resumeClientId` 和 `resumeToken`。登记成功是 `201`，得到 `clientId`、`hostInstanceId`、`accessToken`、`resumeToken`、`serverTimeUtc`、过期时间。
3. 后续请求使用：

   ```http
   Authorization: Bearer <accessToken>
   X-Mako-Client-Id: <clientId>
   X-Mako-Host-Instance-Id: <hostInstanceId>
   Content-Type: application/json
   ```

   `X-Mako-Host-Instance-Id` 对普通读取可省略，但前端建议始终发送。所有 SSE 和二进制内容读取也必须带认证头。
4. 立即读取 `GET /api/v1/capabilities`、`GET /api/v1/runtime-info`、`GET /api/v1/render-backends`，再按工作流读取 `validation-capabilities`、`export-capabilities`、`analysis-capabilities`。能力列表和 `sourceProfiles` 是部署事实；缺能力时禁用对应 UI 并显示响应中的 reason，不根据产品版本或兼容单值猜测。
5. 读取 `GET /api/v1/clients/{clientId}/state` 获得当前工程摘要、job 摘要和 `eventCursor`；用带认证头的 fetch 连接 `GET /api/v1/events`。优先使用 `Last-Event-ID`，否则使用 `after`。SSE 的事件只是提示，`resync.required` 后必须重新读取 state、preview 和 job 权威状态。

服务只监听启动输出中的随机 loopback 地址。Origin 必须是启动器允许的精确值；不能假设端口 5000，也不能把 token 放入 URL、localStorage、日志或提交。

## 2. 身份、修订和请求回执

后端有四个独立的并发域：

| 域 | 字段 | 用途 |
|---|---|---|
| 工程 | `projectRevision` / `expectedRevision` | 工程命令、工程读模型、保存/关闭/预览输入 |
| 预览 | `previewGeneration`、`requestSeq`、`sourceRevision` | 换输入、seek、帧请求和晚到帧过滤 |
| 作者草稿 | `draftRevision`、`sourceRevision`、`authorSourceRevision` | 草稿配置、固定源码、编译、试样和采用 |
| 模板/个人配置库 | `revision`、`templateRevision`、`expectedConfigurationRevision` | 库 CAS、详情读取、固定来源 |

这些修订不可混用。整数修订和大多数微秒字段是十进制字符串；`composition.outputDurationUs`、`summary.audioDurationUs`及完整 analysis 原始 JSON 的旧 number 字段保持各自合同。`sourceRevision` 可能是十进制工程修订、SHA 或 `libraryRevision:templateRevision` 复合身份，始终按不透明字符串处理。

有副作用的 JSON 请求通常带：

```json
{"metadata":{"requestId":"<unique>","hostInstanceId":"<current>","validUntilUtc":"<server-time-plus-deadline>"}}
```

工程 commands 的这三个字段位于顶层；job cancel 直接以 metadata 对象为正文；上传把它们放在 `X-Mako-Request-Id`、`X-Mako-Host-Instance-Id`、`X-Mako-Valid-Until-Utc` 请求头。登记和 heartbeat 不带回执。相同 client、操作、载荷和 deadline 重试必须复用原 requestId；未知是否执行时先读权威状态，不能用新 ID 猜测重放。`202` 只表示接受，必须等待 job/preview 终态。

工程写入按单工程串行发送。成功真实变化、undo、redo 才递增 projectRevision；no-op 不递增修订并保留 redo。409 `revisionConflict` 时重新读摘要/目标模型并保留本地草稿，不能盲目重放旧 payload。

## 3. 资源上传、空工程和持久化

### 3.1 上传

- `POST /api/v1/assets` 支持原始字节或恰好一个文件的 multipart。
- 原始字节用允许的 MIME，并以 `?fileName=` 或 `X-Mako-Asset-Name` 提供安全文件名。
- 上传按流处理；默认 `maxAssetBytes`/`maxAssetQuotaBytes` 可为 `null`，实际限制以 capabilities 为准。
- `201` 返回 `assetId`、SHA-256、size、MIME、文件名和过期时间。资源按 client 隔离；工程、预览和任务租约会延长保留期。
- `GET/HEAD /api/v1/assets/{id}/content` 必须认证，支持单段 Range、If-Range、If-None-Match、304、416；前端 fetch 后再交给 Blob/播放器。

### 3.2 创建和打开

1. 可选先上传歌词和音频；四种组合（两者都有、仅歌词、仅音频、都没有）都可创建，实际音频准备由 `audioPreparation.enabled` 决定。
2. `POST /api/v1/project-sessions` 返回 `201` 和 revision-0 摘要。请求携带 `audioAssetId`、`lyricsAssetId` 时才固定相应资源身份。
3. 已保存工程使用目录授权：`GET /api/v1/file-roots` → `POST /api/v1/file-handles` → `POST /api/v1/project-sessions/open-jobs`。恢复 profile 中登记的自动备份工程使用 `POST /api/v1/project-sessions/recover-jobs`；请求只提交服务返回的 `recoveryId`，不提交任意路径。
4. `open-jobs`、`recover-jobs`、`save-jobs`、`close` 均返回 `202 JobResponse`。轮询 `GET /api/v1/jobs/{id}` 或接收 SSE，只有 `state:"succeeded"` 才进入下一步。
5. 保存 → 关闭 → 重开必须按顺序等待成功。`failed`、`cancelled`、`unknown` 都不能当作成功；unknown 不自动重跑写入。

工程查询使用 `GET /api/v1/project-sessions/{id}`、`history`、`lyrics/branches`、`lyrics/window`、`settings`、`style`、`resources` 和 `orchestration`。精确读模型必须带唯一 `expectedRevision`；分页窗口不能拼接不同修订。

## 4. 编辑和工程历史

发送 `POST /api/v1/project-sessions/{id}/commands`：

```json
{
  "expectedRevision":"<current>",
  "command":{
    "type":"lyrics.setLineText",
    "target":{"branchId":"...","lineId":"..."},
    "text":"..."
  },
  "requestId":"...",
  "hostInstanceId":"...",
  "validUntilUtc":"..."
}
```

实际字段按 `type` 的 OpenAPI 分支发送。当前 96 个命令包括：

- 歌词正文、行、来源、元素拆合/重排、TTML/ruby segment 时间轴；
- token/line 打点、清除、微调、平移和批量时间；
- composition 画布/帧率/底色/输出时长/音频背景，媒体背景 set/clear/fit/playback；
- `project.applyEdits` 原子候选编辑；
- `styles.patch`/`styles.restore`；
- `presetApplications.apply/update/setEnabled/remove`；
- 编排节点、槽位、片段、前奏、mark、布局、动画和快速布局；
- `audio.replaceAsset`/`audio.clear`、`project.rename`、分析 apply/clear。

命令成功回执带新修订和 changeSet；完整 changeSet 超过预算时返回 `requiresFullRefresh:true`，前端必须重新 GET 相关读模型。错误响应解析 `code`、可选 `failureCode` 和 `field`，不要按 `message` 分支。

## 5. 预览、候选与同帧测量

### 5.1 工程预览

1. `POST /api/v1/preview-sessions` 以当前精确工程来源创建会话，固定 `sourceRevision`、尺寸、`alphaMode`和解析后的 `renderBackend`；选择规则见§5.3。
2. 保存返回的 `previewId`、`previewGeneration`、`renderBackend`和limits。
3. `POST /api/v1/preview-sessions/{id}/frame-requests` 带递增 `requestSeq`，`intent` 为 `playback` 或 `seek`；`202` 只表示接受。
4. 通过认证 fetch 读取 `GET /api/v1/events` 的 preview 事件，再 GET `preview-sessions/{id}` 获取权威状态；`latestFrame.content.contentUrl` 只在仍匹配 previewId、generation、sourceRevision 和最新 requestSeq 时显示。
5. `GET /api/v1/preview-frames/{frameId}/content` 读取认证 PNG；HEAD/Range/ETag 遵守二进制合同。
6. 编辑后显式 `PUT /api/v1/preview-sessions/{id}/input`，带 `expectedPreviewGeneration`、`expectedSourceRevision` 和新 source。省略/null的 `renderBackend`保留当前选择；换输入会推进generation并使旧帧失效。
7. 关闭用 `POST /api/v1/preview-sessions/{id}/close`，等待 `closed` 或响应完成排水后释放 Blob URL。

HTTP seek 会创建新 generation，旧 generation 的迟到帧不会发布。固定输入不变时可复用 Worker；在途渲染经协作取消确认后，Worker 内的 Runtime 会话会重新创建，异常取消或协议失败仍可能排水 Worker。客户端不应依赖进程身份，也不要把当前接口当作 WebSocket 或固定 30/60 FPS 流。

工程预览会话可以在**同一 client 所有的工程之间**换源：仍发送 `source.kind:"project"`，并提供目标工程的精确 `sourceId/sourceRevision`；服务重新捕获目标工程并推进 generation。工程来源与 `authorDraft` 来源不能互换，作者会话也不能换成另一个草稿；这类换源返回 `422 commandNotSupported`。authorDraft 会话只能在同一 `draftId` 内更新 draft/source 修订和试样选择，详见 §8。

### 5.2 预设候选、模板试样和 authorDraft

- 预设候选：`POST /api/v1/project-sessions/{id}/preset-application-previews`，固定模板/草稿来源和目标；正式写入仍由工程 command 完成。
- 模板固定试样：`POST /api/v1/template-preview-options` → `POST /api/v1/template-preview-sessions`，试样不创建工程。
- 同帧测量：`POST /api/v1/project-sessions/{id}/orchestration/measurements`，读取真实 Worker 的 geometry/layout union 和 PNG；候选测量同时带 previewId、expectedPreviewGeneration、candidateRevision。

所有候选/试样都复用 preview 帧、SSE、诊断和 close 生命周期；候选结果不能直接当作工程已应用。

### 5.3 CPU/GPU选择和已有预览切换

1. 认证调用 `GET /api/v1/render-backends`，读取 `defaultBackend` 和 `backends[]`；每项包含 `renderBackend`、`available`、`api`及可选设备摘要/不可用原因。公开值严格为小写 `cpu` 或 `gpu`，本批GPU实际API为Vulkan。可用性只代表本次检查，不能代替后续来源能力和业务渲染结果。
2. 前端保存界面偏好，创建预览、验证/导出或普通测量时按需传 `renderBackend`。省略或null使用服务启动默认；已有预览input更新/候选替换省略或null保留原选择，候选测量省略或null沿候选固定选择。不要向公共API传 `vulkan`、`auto`、空串或大写枚举。
3. 只切已有预览的设备时，向 `PUT /api/v1/preview-sessions/{id}/render-backend` 提交当前 `expectedPreviewGeneration`、必需的 `renderBackend` 和metadata。project、authorDraft、template、presetCandidate、editCandidate均适用，原固定来源、候选身份和媒体继续复用，不重新读取工程/草稿。
4. 200后采用返回的generation和选择，再请求帧。相同后端通过CAS后no-op，保留原帧和generation；不同后端成功切换会清理旧帧/诊断并失效旧在途结果。准备失败保留原状态，generation冲突则GET预览重新同步。同一请求意图重试复用原metadata，改选后端须使用新requestId。
5. 用预览/帧/事件、job input、验证/导出报告和测量响应中的固定 `renderBackend`展示选择，继续等PNG或任务终态确认成功。历史结果缺少字段时显示未知。改变偏好不修改已排队/运行任务；GPU失败不会自动转CPU，改用CPU须显式提出新选择/任务。

详细错误和生命周期见[行为§25](../docs/architecture/backend-api.md#render-backend-selection)，设备选择不写工程修订或撤销历史。本机HTTP已覆盖五类预览CPU→GPU→CPU、no-op/重放/旧代次/ACL及GPU不可用保留CPU状态；前端仍须实际验收按钮、晚到帧过滤和失败反馈。

## 6. 正式验证、导出和分析

先读取对应 capabilities 和 sourceProfiles，再创建任务：

| 操作 | 接受 | 结果读取 |
|---|---|---|
| 工程验证 | `POST /project-sessions/{id}/validation-jobs` → 202 | `GET /jobs/{id}`、`/validation-result`、`/validation-diagnostics` |
| 导出 | `POST /project-sessions/{id}/export-jobs` → 202 | `GET /jobs/{id}`、`/export-result`、`/export-content`、`/export-diagnostics` |
| 音频分析 | `POST /project-sessions/{id}/analysis-jobs` → 202 | `GET /jobs/{id}`、`/analysis-result`；需要时显式 `audio.analysis.apply` |

每个任务在接受时固定工程修订和输入快照；随后编辑不会改变任务。工程/作者验证与所有导出来源还在准入时固定解析后的renderBackend，并在job input和报告回显；音频分析不使用该渲染开关。任务结果可能是 `failed`，完整报告/分析 JSON 按原始字节读取并校验返回的 size/SHA。导出和分析内容支持认证 Range/HEAD/ETag；验证完整报告没有 Range 合同。取消用 `POST /jobs/{id}/cancel`，先检查 `canCancel`，再等待真实终态和资源排水。

导出前必须先通过 `composition.setOutputDuration` 写入正的 `outputDurationUs`；省略或 `null` 会使正式导出请求被拒绝，服务不会自动采用音频时长或歌词末端。创建导出请求时从 `export-capabilities.formats` 选择已公布的 `opaqueMp4` 或 `transparentProRes4444`，`quality` 选择 `draft`、`standard` 或 `high`（省略时为 `standard`），并按工程是否要音轨设置 `includeAudio`。格式、质量、音频 profile 以及画布/fps/时长限制均以本次部署返回的 capabilities 为准；支持一种格式不代表另一种格式可用。

### 6.1 重启后的读取

当前 live job 只属于当前 client/host。服务重启后重新 bootstrap/登记；不能用旧 access 或旧 client 读取 live job。工程恢复候选使用 `GET /api/v1/recovery-candidates` 发现，再经 `POST /api/v1/project-sessions/recover-jobs` 显式恢复。

## 7. 完整模板和预设工坊

模板库不属于工程历史。先读取：

1. `GET /api/v1/template-libraries/{id}/templates` 或 detail；需要完整定义时使用 `definitionFormat=full`。
2. 有包时先 `POST /api/v1/effect-packages/inspect`，使用授权 asset 或精确 library/template reference；此接口检查声明，不编译/执行脚本。
3. 通过 `POST /api/v1/template-preview-options` 获取 `workshop-single-line-v1`、`workshop-multi-line-v1`、目标 key 和材质槽位，不在前端猜目标。
4. `POST /api/v1/template-preview-sessions` 试看；保存使用 `POST /api/v1/template-libraries/{id}/commands` 的严格 create/replace/copy/rename/delete union。
5. 工程应用使用 `presetApplications.apply/update/setEnabled/remove`；库 revision 捕获后，工程保存的是固定快照，库更新不追改工程。

full definition 的 `parametersJson`、`fixedSeed` 和 Int64 字符串必须无损保存；不要先转为 JavaScript Number。`advancedRuntimeAvailable` 表示既有高级闭包可执行，不能把 `definition:null` 当作可编辑完整定义；P3 已接入的完整定义和 author configuration 以当前合同为准。

模板库响应中的 `revision` 是**库级 CAS 修订**（个人库为空时为 `missing`），用于列表分页、detail、库 command、导入和导出请求的 `expectedRevision`。每个模板摘要另有 `templateRevision`，它是该模板内容身份；savedTemplate 试样/候选来源同时携带 `libraryRevision` 与 `templateRevision`，两者都必须按不透明字符串保存。工程 `presetApplications.apply/update` 的历史 wire 字段虽然名为 `templateRevision`，实际提交的是上一步捕获的库级 revision；不要把它与模板摘要或试样来源中的内容 `templateRevision` 混用。

完整模板也可以通过 `.mktpl` 传输：先把归档作为当前 client 拥有的 asset 上传，调用 `POST /api/v1/template-libraries/personal/imports/preflight`，取得一次性的 `preflightId`、归档/解压大小和模板摘要；再以精确 `expectedRevision` 调用 `POST /api/v1/template-libraries/personal/imports`。预检对象只由原 client 消费，一次 CAS 整体导入并为模板生成新 ID，解析、预算或修订冲突不会留下部分模板。导出调用 `POST /api/v1/template-libraries/{id}/exports`，提交库级 `expectedRevision` 和 1～100 个 `templateIds`，成功后通过带认证的 `GET/HEAD /api/v1/template-transfers/{exportId}/archive` 读取固定 `.mktpl` 字节；支持 Range/ETag，响应文件名为 `MakoKara-templates.mktpl`。`builtin` 目录只读，客户端不提交主机路径，也不能把归档 URL 当作永久公开地址。

## 8. 作者草稿和个人配置库闭环

### 8.1 草稿与源码

1. 来源可为授权 `packageAssetId`，或个人配置库的 `configurationId + expectedConfigurationRevision`。调用 `POST /api/v1/drafts` 创建草稿，返回 `draftId`、`draftRevision`、配置和 `sampleOptions`。
2. 同一 client 刷新读取用 `GET /api/v1/drafts`，再 `GET /api/v1/drafts/{id}`。服务重启或 owner 不在当前 client 时，先用 `GET /api/v1/profile/author-drafts` 分页发现草稿；对 `canClaim:true` 的 `orphaned` 或 `dormant` 项用 `POST /api/v1/profile/author-drafts/{id}/claim` 提交 `expectedDraftRevision` 与 `expectedOwnershipRevision`，值取发现结果的 `draftRevision` 与 `ownershipRevision`。`active` 且不可 claim 的草稿必须等待原 owner，不能抢占。读取固定源码使用 `GET /api/v1/drafts/{id}/sources?expectedDraftRevision=...&sourceRevision=...`，返回完整 C# path/text 和包身份。
3. 保存配置使用 `PUT /api/v1/drafts/{id}/configuration`，带精确 `expectedDraftRevision` 和完整 configuration。`fixedSeed` 是 UInt64 十进制字符串或 null；`branchInput`/`fixedSeed` 的 required-null 语义按 OpenAPI 发送。
4. 保存新源码使用 `POST /api/v1/drafts/{id}/source-revision-jobs`，一次发送当前包内全部且仅全部 C# 文件；实际源码变化后 draftRevision 与 sourceRevision 更新，旧编译摘要清除；相同字节是 no-op，保留修订和摘要。

源码 path 是包内逻辑路径，不能据此读取服务器任意文件。草稿仍由当前 owner ACL 保护；profile 发现只返回摘要和所有权状态，claim 成功后才可继续普通作者操作。孤立草稿不自动删除，认领后可显式 DELETE；旧 live job/preview 不转移。草稿修订列表使用 `GET /api/v1/drafts/{id}/source-revisions?expectedDraftRevision=...&afterSourceRevision=...&limit=...`，游标必须属于仍保留的源码修订。

### 8.1.1 包文件编辑和下载

- `GET /api/v1/drafts/{id}/package-files?expectedDraftRevision=...&sourceRevision=...` 返回固定包 manifest、文件角色、大小和 SHA；只对 `csharp`、`shader`、`shaderProgram`、`graph` 返回文本。
- `PUT /api/v1/drafts/{id}/package-files` 必须提交 manifest 与全部可编辑文本文件。服务原子校验包结构和配置；非文本文件的 path、role、SHA 与字节不能改变。文本单文件最多4 MiB，文本与清单合计最多16 MiB，最多256个文本文件。
- `GET /api/v1/drafts/{id}/package?expectedDraftRevision=...&sourceRevision=...` 下载精确源码修订的 `.mkfx`；`GET /api/v1/script-configurations/{id}/package?expectedRevision=...` 下载精确个人配置库修订。下载最多并行2个，客户端应消费完响应以释放租约和名额；stale revision 或 owner ACL 错误在响应体开始前返回。

### 8.2 编译、试样和采用

- 同步编译：`POST /api/v1/drafts/{id}/compilation-jobs`，省略 `executionMode` 或使用 `sync`，返回 `200 AuthorCompilationResponse`。
- 异步编译：同一路由使用 `executionMode:"async"`，返回 `202 JobResponse`。接受时固定 draftRevision/sourceRevision；轮询 `GET /api/v1/jobs/{jobId}`，成功后读取 `GET /api/v1/jobs/{jobId}/compilation-result`。结果固定旧源码，即使草稿之后被修改；结果只支持当前宿主 live job。
- 最近同步/草稿结果：`GET /api/v1/drafts/{id}/compilation`，无结果是 `404 resourceNotFound`。
- authorDraft 预览：复用 `POST /api/v1/preview-sessions`，使用 `source.kind:"authorDraft"`、`sourceId:draftId`、`sourceRevision:draftRevision`、`authorSourceRevision:configuration.sourceRevision` 和服务返回的 `templateSample`；请求固定 `previewKind:"authorDraft"`、`committable:false`。
- 换试样：`PUT /api/v1/preview-sessions/{id}/input`，保留 draft ID，带 `expectedPreviewGeneration`，更新 sample/target/materialSlot；随后按普通 preview 读取帧/诊断/close。
- 验证试样：`POST /api/v1/drafts/{id}/validation-jobs`，带 `expectedDraftRevision`、`sampleId`、`targetKey`、可选 `materialSlot` 和 metadata，返回 `202`。固定 640×360、30 fps、12 秒、每输出 360 帧；按普通 validation 读取 job、report、diagnostics 和 cancel。
- 采用源码：`POST /api/v1/drafts/{id}/adopt-source-revision`，显式给出固定 `sourceRevision` 和精确 draftRevision。采用不要求编译或验证成功。
- 删除草稿：`DELETE /api/v1/drafts/{id}`，带精确 draftRevision 和 metadata；不会修改工程或个人配置库。

编译诊断可失败但仍可读取；保存合法配置和源码不要求成功编译。不要把 adopt 当作发布到个人库，也不要用空诊断伪造异步任务成功。

### 8.3 个人配置库

1. `GET /api/v1/script-configurations` 取得当前库 `revision` 和摘要；第一次可省 `expectedRevision`。
2. `GET /api/v1/script-configurations/{id}?expectedRevision=<library-revision>` 读取完整配置和固定包检查结果。
3. `POST /api/v1/script-configurations/commands` 使用外层 `expectedRevision`、metadata 和严格 command union：`create`、`replace`、`copy`、`rename`、`delete`。
4. `create/replace` 的 configuration.source 严格三选一：草稿修订（draftId + expectedDraftRevision + sourceRevision）、个人配置修订（configurationId + expectedConfigurationRevision）或授权包引用。库 CAS 成功返回 previous/new revision 和 configurationId。
5. 配置库变化不追改已保存模板或工程快照；若要继续编辑，按新的库 revision 重新读取 detail。

库 revision 是小写 SHA-256 或 `missing`，不是数字。个人配置库 CAS 与草稿 expectedDraftRevision、工程 expectedRevision 分开处理。

## 9. 主要错误处理

| 情况 | 前端动作 |
|---|---|
| `401 unauthorizedClient` / `clientTokenExpired` | 重新读取 bootstrap；服务重启时重新登记并比较 hostInstanceId |
| `401 resourceUnauthorized` | 停止当前 client 的资源读取，重新获取正确 owner/权限 |
| `403 readOnlySession` | 停止当前 client 的写入，按只读会话重新打开或申请可写句柄 |
| `404 resourceNotFound` / `jobNotFound` | 区分“尚无结果”与“已过保留期”；不要创建空成功结果 |
| `409 revisionConflict` | 读取当前摘要/读模型，合并本地草稿后重新提交新意图 |
| `409 previewGenerationConflict` / `previewRequestOutOfOrder` | 丢弃旧帧/旧序列，使用最新 generation 和递增 requestSeq |
| `409 requestIdReuse` | 保留原意图，检查原回执；不要修改载荷继续复用 ID |
| `409 operationNotCancellable` | 继续读取 job 终态，不能假设取消成功 |
| `413 resourceTooLarge` / `429 *Quota*` | 按 capabilities/响应限制缩小或排队；不要静默截断 |
| `422 commandRejected` / `commandNotSupported` | 显示领域原因和 field/failureCode；不把 schema 合法当作目标可用 |
| `field:renderBackend`及`RenderBackendUnavailable/Unsupported/Mismatch/ProtocolInvalid` | 显示稳定原因并重读可用性；保留已有预览状态，改用CPU须显式新意图 |
| `500/503 taskExecutionFailed` / `previewUnavailable` | 读取 job/preview 状态和稳定 failureCode；匹配 Server/Worker 后再重试新意图 |
| SSE `resync.required` | 重新读取 client state、preview、job，再从新 cursor 订阅 |
| 二进制 `416`、`304`、`HEAD` | 按 HTTP body 规则处理；不要尝试把空 body 当 JSON 错误 |

诊断 `severity` 是字符串，历史持久化诊断可能为 `Info/Warning/Error`，Runtime/任务诊断可能为 `info/warning/error`。客户端应兼容两种大小写。

## 10. 当前文档权威和验证边界

- 路由、DTO、成功状态：`web/openapi.json` 与 `web/api-index.md`。
- 跨请求修订、回执、SSE、资源租约、任务、预览、作者语义：`docs/architecture/backend-api.md`。
- 合同版本、文件指纹、源码覆盖：`web/contract-manifest.json`。
- 阶段提交、匹配发布、fresh HTTP 和证据边界：`docs/backend-api-completion-plan.md`、`docs/backend-render-backend-api-plan.md`、`docs/backend-extraction-progress.md`。
- Linux x64 发布/ready/退出：`tools/backend-web-host/README.md` 和 `docs/architecture/backend-api.md §15`。

合同 freeze/validate、真实 HTTP、Worker/媒体结果、像素比较和浏览器验收是不同证据层。不要用生成合同、单元测试、产品版本、能力字段或一个有限 HTTP probe 推断完整 Web 产品、跨平台支持或性能达标。
