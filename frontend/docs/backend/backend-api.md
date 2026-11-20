# 当前后端 HTTP API

更新：2026-11-15。当前 **0.113.0／HTTP协议1／合同1.54.0**。1.54.0 新增前奏提示组图形／自定义静态内联 SVG 与闪烁渐进擦除动画命令 `orchestration.setCountdownPresentation`、`countdownSequence` 读模型 `shape`/`animation` 字段及 `orchestration.setLeadInAttachment` 的 `CueTargetAboveStart` 模式；继续保持 M14 SDK v1 移除与版本收敛（效果包、作者源码检查、编译、预览、验证与导出仅准入 SDK v2：`packageVersion=3`、`shaderProfile="skia-ganesh-3.119.4-v2"`）。合同为115路径／128方法／672 schemas、3995个合成例（2879反例）；精确源码基线和文件指纹见重新冻结的[执行合同](../../web/api-contract.md)与manifest。保留 1.52.1 的失效预设应用筛选与布局参考测量修复、1.52.0 的真实源材质失败安全结构与默认256张表面／512MiB预算，SDK2连续／Burst粒子及晚期画布图元见§26。静态外辉光继续贯通四层样式、模板、文字标记、Worker快照与颜色捕获，语义见§6.11和[辉光参考](../style-reference/glow.md)。既有作者工作流、浏览器预览与桌面GPU流保持；匹配发布、HTTP、浏览器及导出证据分别登记，历史证据保留原归属。

历史1.44.0的HTTP DTO结构沿用1.43.0，当时固定112路径／125方法／597 schemas、3434 examples（2548反例），以匹配生成物和合同校验报告为准。公开 `renderBackend` 严格为 `cpu|gpu`，GPU实际API为Vulkan；创建/任务省略或null使用启动默认，已有预览更新省略或null保留当前选择，候选测量省略沿候选固定选择。各渲染任务在准入时固定选择，预览/帧/事件、job input、验证/导出报告及测量响应回显；历史缺失值表示未知。设备可用性不等同于业务渲染成功，202只表示接受，GPU失败不回落CPU。合同静态门禁、本机硬件、HTTP、浏览器、跨平台及性能分别验收。

§20 的0.98直接包预览、§10.1的0.100图片验证、§22的0.99图片预览及§11.5的0.101图片导出，均保留各自历史 profile 与证据归属；这些独立 profile 的旧限制不再是本轮统一输入的全局限制。0.99的1175请求／9760断言、0.100的1412请求／10582断言等历史数字不并入本轮。更早实现、失败过程与验收数字仍见 [实施记录](../backend-extraction-progress.md)。 原顶部历史摘要保持归属：0.75内部诊断各类256条/64KiB、合计512条/128KiB与真实0.74拒绝；0.72修复`1c70fae`后的775/775及HTTP699/4239、58终态、六恢复、七controls、四Server/172成员排水，repair1的753及失败HTTP137/987仍单列于原实施记录，不计本轮。运行能力必须采用实际部署的 Worker 握手与 capabilities，不能由产品版本推定；内部进程隔离不构成任意恶意脚本 OS sandbox，也不代表浏览器或跨平台验收。

## 1. 公共传输规则

监听只接受显式loopback地址，默认 `http://127.0.0.1:0`。使用启动输出 `BACKEND_READY <地址> <hostInstanceId>` 中的实际地址。JSON请求使用 `Content-Type: application/json`；普通响应字段为camelCase，枚举输出camelCase字符串，空的可选响应字段省略（§6.11样式字体／效果／value及§18 document branchId等明确required-null字段除外）；完整validation-result／export-result／analysis-result为登记的原始JSON字节，可能保留显式null。**不是所有DTO都有protocolVersion**，以各响应字段为准。日期是ISO 8601 `DateTimeOffset`；截止时间用服务公布的 `serverTimeUtc` 计算。

下文JSON中的 `host-H`、`client-C`、`project-P`、`asset-A` 等是示例身份，时间和hash也是示例，不能当作实际凭证、固定ID或验收产物。客户端必须采用实际返回值。大小与帧数为JSON整数，布尔为JSON布尔。

### 1.1 认证与请求头

除了两条health路由和客户端登记路由，所有路由（包括capabilities、runtime-info、二进制、SSE）都要求：

```http
Authorization: Bearer <accessToken>
X-Mako-Client-Id: <clientId>
X-Mako-Host-Instance-Id: <hostInstanceId>
```

`X-Mako-Host-Instance-Id`对普通读取是可选的旧主机门禁；提供时必须匹配。`X-Mako-Client-Id`必须只有一个值，裸clientId不授予访问权。登记使用**bootstrap启动凭证**，详见§3；accessToken与resumeToken不能替代bootstrap。

连接两端、Host及Origin都校验：本机连接只能使用实际监听authority；Origin缺省可用，提供时必须是监听origin或可信启动器配置的精确origin。禁止URL参数 `token/access_token/accessToken/bootstrapToken/resumeToken`。响应默认 `Cache-Control: no-store`、`X-Content-Type-Options: nosniff`；允许的Origin返回对应 `Access-Control-Allow-Origin` 与 `Vary: Origin`，不使用通配origin。

OPTIONS预检不要求client凭证，但要求允许的Origin；只允许GET／POST／PUT／DELETE／HEAD及以下请求头，成功204：`Authorization, Content-Type, X-Mako-Client-Id, X-Mako-Host-Instance-Id, X-Mako-Request-Id, X-Mako-Valid-Until-Utc, X-Mako-Asset-Name, Last-Event-ID, Range, If-None-Match, If-Range`。非法Host／Origin为403，非法预检方法／头为400。

### 1.2 有副作用请求与回执

上传、工程创建／命令、目录句柄、持久化／正式验证／导出／音频分析任务与预览修改共用**Hosting级client回执**，不是每工程独立去重。JSON通常嵌套：

```json
{"metadata":{"requestId":"op-001","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

例外：工程commands的三个字段在顶层；job cancel正文直接为三个字段；二进制上传用 `X-Mako-Request-Id`、`X-Mako-Host-Instance-Id`、`X-Mako-Valid-Until-Utc`请求头。client注册／heartbeat不使用该回执。所有JSON样例中的metadata均遵循这份定义。

`requestId`为不超过128字符的非空值；host必须当前；deadline必须晚于服务时间且不超过公布的600秒重试窗口。固定同client／requestId／规范操作与语义载荷／deadline重试返回原结果，包括原jobId或seek生成的原代次；不同操作、目标、载荷或deadline复用ID返回409 `requestIdReuse`。兼容别名可映射同一规范操作，不把别名当作新的请求作用域。先查有效回执，再执行修订门禁。默认每client1024有效／在途回执、单项64KiB、合计上限64MiB，满额拒绝新请求，不驱逐有效回执。

HTTP断开不撤销已接受操作。202只表示任务／帧请求已接受，必须读权威状态；不能凭一次断流重发新requestId猜测操作未执行。停止服务时拒绝新的POST／PUT／DELETE并等待已接受操作、预览及保存事务排水。

### 1.3 精度与修订

| 字段 | 当前HTTP类型／约束 |
|---|---|
| projectRevision、expectedRevision、savedProjectRevision、预览代次、requestSeq、eventSeq、诊断sequence | 十进制字符串；工程修订、代次非负，帧requestSeq必须正数且当前代次严格递增；实际整型范围为Int64 |
| 预览 sourceRevision／expectedSourceRevision | 不透明字符串；工程预览当前通常是十进制工程修订，G17 模板试样可以是 `libraryRevision:templateRevision` 等复合身份，客户端不得按 Int64 解析 |
| 命令startUs／endUs、歌词time端点、预览timeUs、诊断occurrence.timeUs | Int64微秒的十进制字符串；预览timeUs非负，歌词命令解析有符号值后由领域用例验证合法性；未打轴歌词返回 `time:{}`，不是零时间 |
| eventCursor、diagnostics cursor、ID、SHA | 不透明字符串；不要按工程修订或本机路径解释 |
| 正式任务input.outputDurationUs、progress.timeUs、issues.timeUs、导出derivedVideoDurationUs／actualVideoDurationUs／audioDurationUs／actualAudioDurationUs、能力maxDurationUs／maxCompressedAudioDurationUs | Int64微秒十进制字符串，input.sourceRevision也是字符串；这些新字段不改变旧composition／summary的number合同 |
| analysisInput.sourceRevision／sourceDurationUs、analysisProgress.completedSamples／totalSamples／timeUs、analysisResult及summary.audioAnalysis的originUs／sampleCount／durationUs | 十进制字符串；采样数是48kHz单声道采样计数，不是渲染帧数；算法revision／contentHash为不透明SHA身份，见§12 |
| 完整analysis-result内originUs／sampleCount／durationUs／points[].timeUs | 保留原Core规范JSON的整数number；本profile上限使这些数均在JS安全整数内，不把原文件改为字符串或重新量化double |
| composition.outputDurationUs、summary.audioDurationUs | **当前仍为JSON Int64 number**，不能宣称全部64位时间都已字符串化；超过JS安全整数时客户端须用保留整数的JSON解析或拒绝不能保真的值 |
| 文件／帧大小、配额、canvas尺寸、帧率分子分母、计数 | JSON整数；读取服务公布上限，不从示例推定配置 |

工程真实变更、undo、redo递增projectRevision，no-op不递增；修订不等于Workspace.Generation、预览代次或eventSeq。保存捕获某修订后继续编辑，完成只标记已保存修订，不能把随后编辑覆盖或自行清dirty。

未知具名命令拒绝。命令／生命周期／预览等带严格标注的请求DTO拒绝未知JSON成员；create及composition未设同一严格未知字段门禁，客户端仍应只发此规范字段，不据此推定任意patch可用。

### 1.4 错误与HTTP状态

错误正文为 `application/problem+json` 的 `BackendErrorResponse`，**不是完整RFC Problem Details结构**。当前代码在尾部增加可空的 `failureCode`；值为空时字段省略，旧错误wire不增加字段：

```json
{"code":"revisionConflict","message":"工程修订已经改变，请先读取当前状态。","field":"expectedRevision","protocolVersion":"1"}
```

`message`供显示，不能作为程序分支；field可省略，实际大小写随产生处而异。分类如下，各节列出主要领域拒绝：

| HTTP | code |
|---:|---|
| 400 | invalidRequest、resourceEmpty、resourcePathNotAllowed、pathNotAllowed、diagnosticLimitExceeded等未另映射错误 |
| 401 | unauthorizedClient、clientTokenExpired、resourceUnauthorized |
| 403 | readOnlySession；Host／Origin、预检Origin或其他client身份门禁可显式返回unauthorizedClient／403 |
| 404 | resourceNotFound、sessionNotFound、jobNotFound、previewSessionNotFound、previewFrameNotFound、previewInspectionUnavailable、exportArtifactNotFound；不存在歌词来源可为commandRejected／404 |
| 409 | hostRestarted、sessionConflict、revisionConflict、requestExpired、requestIdReuse、writeLeaseConflict、sessionClosing、dirtySession、activeJobs、operationNotCancellable、previewGenerationConflict、previewRequestOutOfOrder、diagnosticCursorExpired |
| 410 | resourceExpired、previewFrameExpired、exportArtifactExpired |
| 413／415 | resourceTooLarge／unsupportedMediaType |
| 422 | commandRejected、commandNotSupported、lyricsImportFailed、emptyLyrics、noAudioStream、audioPreparationFailed、projectLoadFailed、validationFailed、exportInputRejected、exportAudioUnsupported |
| 429 | requestQuotaExceeded、resourceQuotaExceeded、clientQuotaExceeded、sessionQuotaExceeded |
| 500／503 | internalError、taskExecutionFailed／previewUnavailable、taskUnavailable；服务关闭时停止接受POST／PUT显式sessionClosing／503 |

`taskExecutionFailed`可带尾部`failureCode`，用于受管理Worker的稳定程序分支；当前B4-I编号包括`WorkerAudioPreparationTimedOut`、`WorkerAudioPreparationBudgetExceeded`、`WorkerAudioPreparationTransportFailed`、`WorkerAudioPreparationProtocolInvalid`和`WorkerAudioPreparationSourceChanged`。其中`WorkerAudioPreparationSourceChanged`仅用于Worker前后复核固定输入时的实际身份完整性失败，不属于原输入格式拒绝；Host原`VerifyFixedAudioSource`身份拒绝仍返回原`taskExecutionFailed`，不新增`failureCode`。路径、FFmpeg原始参数、内部异常和凭证不进入`message`或`failureCode`。用户取消仍沿原cancelled／OperationCanceled语义，不把deadline写成取消。

无效二进制Range为416、空body及 `Content-Range: bytes */<size>`，不强塞JSON错误。304／HEAD没有二进制响应body。未知HTTP路由／方法为404／405 `invalidRequest`。

## 2. 路由索引与能力

所有以下路径均已映射；“B”表示bootstrap，“C”表示client accessToken，health无凭证但仍过Host／Origin。

| 方法 | 路径 | 认证 | 成功／详述 |
|---|---|---|---|
| GET | `/api/v1/health/live` | 无 | 200，§2 |
| GET | `/api/v1/capabilities` | C | 200，§2 |
| POST | `/api/v1/clients` | B | 201，§3 |
| POST | `/api/v1/clients/{id}/heartbeat` | C | 200，§3 |
| GET | `/api/v1/clients/{id}/state` | C | 200，§3 |
| GET | `/api/v1/events` | C | SSE 200，§4 |
| POST | `/api/v1/assets` | C | 201，§5 |
| GET | `/api/v1/fonts` | C | 200，§6.12 |
| POST | `/api/v1/fonts/inspect` | C | 200，§6.12 |
| GET／HEAD | `/api/v1/assets/{id}/content` | C | 200／206／304，§5 |
| POST | `/api/v1/project-sessions` | C | 201，§6 |
| GET | `/api/v1/project-sessions/{id}` | C | 200，§6 |
| GET | `/api/v1/project-sessions/{id}/history` | C | 200，§6 |
| GET | `/api/v1/project-sessions/{id}/lyrics/lines` | C | 200，§6 |
| GET | `/api/v1/project-sessions/{id}/lyrics/branches` | C | 200，§6.4 |
| GET | `/api/v1/project-sessions/{id}/lyrics/window` | C | 200，§6.4 |
| GET | `/api/v1/project-sessions/{id}/settings` | C | 200，§6.4 |
| GET | `/api/v1/project-sessions/{id}/style` | C | 200，§6.11 |
| POST | `/api/v1/project-sessions/{id}/commands` | C | 200，§6 |
| POST | `/api/v1/lyrics-import-plans` | C | 200，§6.12 |
| GET | `/api/v1/template-libraries` | C | 200，§13 |
| GET | `/api/v1/template-libraries/{id}/templates` | C | 200，§13 |
| GET | `/api/v1/template-libraries/{id}/templates/detail` | C | 200，§13 |
| POST | `/api/v1/template-libraries/{id}/commands` | C | 200，§13 |
| GET | `/api/v1/file-roots` | C | 200，§7 |
| POST | `/api/v1/file-handles` | C | 201，§7 |
| POST | `/api/v1/project-sessions/open-jobs` | C | 202，§7 |
| POST | `/api/v1/project-sessions/recover-jobs` | C | 202，§7 |
| POST | `/api/v1/project-sessions/{id}/save-jobs` | C | 202，§7 |
| POST | `/api/v1/project-sessions/{id}/close` | C | 202，§7 |
| GET | `/api/v1/jobs/{id}` | C | 200，§8 |
| POST | `/api/v1/jobs/{id}/cancel` | C | 200，§8 |
| GET | `/api/v1/recovery-candidates` | C | 200，§8 |
| GET | `/api/v1/recovery-jobs` | C | 200，§8 |
| GET | `/api/v1/validation-capabilities` | C | 200，§10 |
| POST | `/api/v1/project-sessions/{id}/validation-jobs` | C | 202，§10 |
| GET | `/api/v1/jobs/{id}/validation-result` | C | 200，§10 |
| GET | `/api/v1/jobs/{id}/validation-diagnostics` | C | 200，§10 |
| GET | `/api/v1/recovery-jobs/{id}/validation-result` | C | 200，§10 |
| GET | `/api/v1/export-capabilities` | C | 200，§11 |
| POST | `/api/v1/project-sessions/{id}/export-jobs` | C | 202，§11 |
| GET | `/api/v1/jobs/{id}/export-result` | C | 200，§11 |
| GET | `/api/v1/jobs/{id}/export-diagnostics` | C | 200，§11 |
| GET／HEAD | `/api/v1/jobs/{id}/export-content` | C | 200／206／304，§11 |
| GET | `/api/v1/recovery-jobs/{id}/export-result` | C | 200，§11 |
| GET／HEAD | `/api/v1/recovery-jobs/{id}/export-content` | C | 200／206／304，§11 |
| GET | `/api/v1/analysis-capabilities` | C | 200，§12 |
| POST | `/api/v1/project-sessions/{id}/analysis-jobs` | C | 202，§12 |
| GET／HEAD | `/api/v1/jobs/{id}/analysis-result` | C | 200／206／304，§12 |
| GET／HEAD | `/api/v1/recovery-jobs/{id}/analysis-result` | C | 200／206／304，§12 |
| GET | `/api/v1/runtime-info` | C | 200，§9 |
| POST | `/api/v1/preview-sessions` | C | 201，§9 |
| POST | `/api/v1/project-sessions/{id}/edit-previews` | C | 201，§6.12 |
| PUT | `/api/v1/preview-sessions/{id}/input` | C | 200，§9 |
| POST | `/api/v1/preview-sessions/{id}/frame-requests` | C | 202，§9 |
| GET | `/api/v1/preview-sessions/{id}` | C | 200，§9 |
| POST | `/api/v1/preview-sessions/{id}/close` | C | 200，§9 |
| GET／HEAD | `/api/v1/preview-frames/{id}/content` | C | 200／206／304，§9 |
| GET | `/api/v1/preview-frames/{id}/inspection` | C | 200，§29 |
| GET | `/api/v1/preview-sessions/{id}/diagnostics` | C | 200，§9 |

上表保留早期56个路由模板（51个v1＋5个兼容别名）、62个显式方法；当前合同1.40.0的完整范围为108路径／120方法，以[生成清单](../../web/api-index.md)为准，阶段变更见[目标目录](../backend-api-target-catalog.md)。GET／HEAD分别计方法，全局OPTIONS不计。

兼容别名为 `GET /health/live`、`GET /capabilities`、`POST /assets`、`POST /project-sessions`、`GET /project-sessions/{id}`，认证及DTO与对应v1路由相同。当前公开写入使用 GET/POST/PUT/DELETE；没有 PATCH 接口或客户端任意路径读取接口。作者草稿删除使用显式 `DELETE /api/v1/drafts/{id}`，仍遵守客户端归属、回执和草稿修订门禁。

health返回：`{"status":"live","hostInstanceId":"host-H","protocolVersion":"1"}`。capabilities返回当前产品／协议、serverTimeUtc、MIME、features及限额。以下保留0.71.0历史有限部署示例（其中16MiB/128MiB不是当前上传默认值，当前见§5/§15）；历史配额：

```json
{
  "protocolVersion":"1","hostInstanceId":"host-H","productVersion":"0.71.0",
  "maxAssetBytes":16777216,"maxAssetQuotaBytes":134217728,
  "assetMimeTypes":["application/octet-stream","text/plain","text/lrc","text/lyrics","audio/aac","audio/flac","audio/m4a","audio/mp4","audio/mpeg","audio/ogg","audio/wav","audio/x-flac","audio/x-wav"],
  "features":["clients.register","clients.heartbeat","clients.state","events.replay","assets.upload","assets.content.range","project-sessions.create","project-sessions.summary","project-sessions.lyrics.read","project-sessions.history.read","project-sessions.commands.lyrics.setTimeRange","project-sessions.commands.history","project-sessions.commands.audio.analysis.apply","project-sessions.commands.audio.analysis.clear","file-handles.create","project-sessions.open-jobs","project-sessions.save-jobs","project-sessions.close-jobs","project-sessions.recover-jobs","jobs.read","jobs.cancel","recovery-candidates.read","recovery-jobs.read","preview.frame.content.range","preview.diagnostics","preview.runtime-info","preview.state","preview.close","validation.capabilities","export.capabilities","project-sessions.validation-jobs","jobs.validation-result","jobs.validation-diagnostics","recovery-jobs.validation-result","project-sessions.export-jobs","jobs.export-result","jobs.export-content","jobs.export-diagnostics","recovery-jobs.export-result","recovery-jobs.export-content","project-sessions.analysis-jobs","jobs.analysis-result","recovery-jobs.analysis-result","preview.create.static-project","preview.input.static-project","preview.frame-requests"],
  "serverTimeUtc":"2026-10-13T10:00:00Z","maxCommandRetryWindowSeconds":600,
  "maxCommandReceiptsPerSession":1024,"maxCommandReceiptsPerClient":1024,
  "maxReceiptBytes":65536,"maxReceiptBytesPerClient":67108864,
  "clientTokenLifetimeSeconds":120,"clientLeaseLifetimeSeconds":300,
  "maxClients":16,"maxSessionsPerClient":16,"unreferencedAssetLifetimeSeconds":600,
  "maxEventsPerClient":256,"maxUploadPreparations":4,"maxUploadPreparationsPerClient":2,
"maxEventConnections":4,"maxEventConnectionsPerClient":2,"maxAssets":4096,
"audioPreparation":{"enabled":false,"disabledReason":"WorkerAudioPreparationUnavailable","profile":"self-contained-native-waveform-v1","families":["wav","mp3","flac","aac","mov","ogg"],"maxPayloadBytes":4194304,"maxBaseBuckets":500000,"baseBucketSamples":64,"operationTimeoutSeconds":60,"cancellationGraceSeconds":2,"maxRunning":1,"maxRunningPerClient":1,"maxQueued":4,"maxQueuedPerClient":4}
}
```

`audioPreparation`是`capabilities`尾部的可空对象；Server当前组合始终公布其能力形状，`enabled`只有真实准备Worker完成EngineIdentity、profile、六家族、预算、native工具和private-group/subreaper握手后才为true，`disabledReason`只在禁用时出现。它不是新路由或feature字符串，也不表示任意音频、整曲波形或跨平台隔离已经验收。上面的disabled值只是未验收部署的示例，客户端必须采用实际返回值。

preview create／input／frame-requests及四项validation执行／结果features仅对应Worker配置可用时出现；六项export执行／结果features仅export-capabilities.enabled为true时出现；三项analysis执行／读取features仅analysis-capabilities.enabled为true时出现。validation.capabilities和export.capabilities始终公布；analysis-capabilities路由始终映射，当前features没有单列analysis.capabilities字符串。报告／媒体／分析结果读取不启动Worker。preview的文件存在只表示 `configured-unverified`，不是已握手；export能力另经真实Worker有界工具／文件guard探测，见§11。音频准备能力只在`audioPreparation.enabled`为true时用于Server的工程准备，禁用时不回退Host native。旧字段maxCommandReceiptsPerSession保留兼容，生产实际为client级回执。capabilities不承诺任意字体／脚本、其他音频容器或分析结果应用工程能力。

## 3. 客户端登记、心跳与状态

`POST /api/v1/clients`首次正文 `{"protocolVersion":"1"}`，Authorization为bootstrap。恢复访问令牌时仍使用bootstrap，正文为 `{"protocolVersion":"1","resumeClientId":"client-C","resumeToken":"<resumeToken>"}`。成功201：

```json
{"clientId":"client-C","hostInstanceId":"host-H","accessToken":"<accessToken>","resumeToken":"<resumeToken>","tokenExpiresAtUtc":"2026-10-13T10:02:00Z","leaseExpiresAtUtc":"2026-10-13T10:05:00Z","serverTimeUtc":"2026-10-13T10:00:00Z"}
```

凭证由服务随机生成，恢复轮换accessToken，旧页凭证随即失效；同进程保留client身份及工程，服务重启不能用旧client恢复。协议不匹配400，凭证错误401，client配额429。

**0.75.1 G1 配额按需回收：** HTTP 新登记达到默认16客户端上限时，异步登记入口才尝试回收已过租约且真正空闲的身份；未到上限不主动清理。ClientRegistry 两阶段 claim，锁外调用 Hosting；有效租约、认证在途请求（包括 SSE／流式响应）、未关闭工程、运行／排队任务、未到保留期限的终态 job、在途或有效回执、仍有引用／读取的资源均保护。claim 期间恢复、心跳或认证临时返回401 `unauthorizedClient`，不与回收并发成功；检查不确定／失败则恢复可resume身份。有保护依据时可以继续429配额拒绝，不保证任意过期身份马上回收。同步 `ClientRegistry.Register` 为兼容入口，本身不调用 Hosting 或自动回收。

回收不是注销、自动关闭工程或 discard。工程／job／回执门禁通过后，按既有策略排水过期预览并核资源引用；只有无保护依据才移除 client-local 内存索引、事件和不再有效回执；晚到预览通知不重建已删除事件流。持久工程、ledger、报告与资源文件不随 client 删除；未引用上传资源仍按原 TTL 自行清理，仍有引用的资源继续保护。**实际回收后旧 access 与 resume 均401 `unauthorizedClient`**，须使用 bootstrap 新登记；有工程／任务等保护的过期身份仍可 resume。默认 access120秒／lease300秒、maxClients16与回执／job保留合同保持，未新增注销路由或公开回收开关。

`POST /api/v1/clients/client-C/heartbeat`无JSON正文，认证后续accessToken及client租约；200返回 `clientId,hostInstanceId,tokenExpiresAtUtc,leaseExpiresAtUtc,serverTimeUtc`，不返回新token、不修改工程。路径id必须匹配认证client，否则403。

`GET /api/v1/clients/client-C/state`无正文，返回同次权威读取捕获的工程摘要／job摘要与游标：

```json
{"clientId":"client-C","hostInstanceId":"host-H","dormant":false,"leaseExpiresAtUtc":"2026-10-13T10:05:00Z","eventCursor":"host-H:client-C:7","sessions":[],"jobs":[]}
```

session结构见§6，job摘要字段为 `jobId,kind,state,projectSessionId?,canCancel,expiresAtUtc,errorCode?,progress?,failureCode?,analysisProgress?`。休眠保留工程，关闭／排水预览并释放其资源，已接受正式validation／export／analysis任务继续；恢复client后需重新建预览。没有显式client suspend／release路由，不由网页断线自动discard未保存工程。

## 4. SSE与重同步

`GET /api/v1/events`使用带Authorization头的fetch流。游标优先 `Last-Event-ID`，否则 `?after=<cursor>`；可传完整不透明cursor，纯非负数字序号也会绑定到当前host／client。默认每client重放256条，全局4／每client2连接。无事件时约15秒发 `: heartbeat` 注释，这不是client租约心跳。

```text
id: 7
event: project.changed
data: {"eventSeq":"7","eventCursor":"host-H:client-C:7","type":"project.changed","hostInstanceId":"host-H","occurredAtUtc":"2026-10-13T10:00:00Z","summary":{...},"changeSet":{...}}

```

`data`为BackendEventResponse：固定公共身份／时间，加可选summary、changeSet、job或preview小投影。已生产事件包括 `project.opened, project.changed, project.saved, project.closed, job.changed, job.progress, job.terminal, preview.stateChanged, preview.frameReady, preview.failed, preview.closed, project.mediaFeaturesChanged`（宿主媒体分析完成，见§28.6）；新建发布也使用project.opened，不携带像素、完整工程或完整日志。

游标不匹配、超前或重放窗口缺口时，流发 `event: resync.required`，data为 `{"eventCursor":"...","events":[],"resyncRequired":true}` 后关闭；重新GET client state和需要的preview／job，再以新游标连接。访问凭证过期时发 `event: client.expired` 和错误DTO后关闭；先恢复登记再重连。SSE输出 `id`为eventSeq，事件data同时保留完整eventCursor；断线不取消已接受操作。预览提示另有128条有界advisory队列，可能丢旧提示，GET状态始终权威。

## 5. 上传资源与二进制读取

`POST /api/v1/assets`支持原始字节（Content-Type为允许MIME，文件名用 `?fileName=song.wav` 或 `X-Mako-Asset-Name`）或multipart/form-data的**恰好一个文件**，字段名不固定。两种方式均流式读取并写入受控暂存，不把完整资源读入Host内存；完整SHA/size固定后才发布asset。默认Server的单资源/总字节策略为null，表示不施加对应部署限额；显式正值经capabilities公布并在读取中继续检查，断连/失败清理本次暂存。文件名不能是路径／越界片段；上传需§1.2三个请求头。MIME白名单见capabilities，MIME允许不等于内容已通过音频／歌词准备。缺名／多文件400、空资源400、类型415、配置的单项超限413、资源或上传准备配额429。

201返回AssetResponse，哈希为完整字节小写SHA-256，没有服务端路径：

```json
{"assetId":"asset-A","sha256":"<64位小写hex>","size":176444,"contentType":"audio/wav","fileName":"song.wav","expiresAtUtc":"2026-10-13T10:10:00Z"}
```

资源按client隔离；未引用资源默认600秒到期，工程或预览等拥有引用时由实际租约保持，最后释放后重新进入保留期。没有上传后删除或手动续TTL路由。

`GET/HEAD /api/v1/assets/asset-A/content`读取不可变字节，服务持read lease至响应结束。返回实际MIME、Content-Length、`ETag:"<sha256>"`、`Accept-Ranges: bytes`；支持一个 `bytes=start-end`／`start-`／`-suffix` Range，匹配If-Range才走206，否则完整200；If-None-Match支持列表、`*`及同hash弱tag，命中304。头字段对允许Origin暴露。未知404、越client401、已知过期410；416规则见§1.4。

## 6. 工程创建、查询与具名命令

### 6.1 创建与摘要

0.70.1 Backend的默认prepare loader改为自包含固定音频策略，App／Media原`AudioWaveformLoader.CreateDefault()`不变。当前B4-I的Server组合在真实受管理Worker能力开启时，将新建、open和recover的媒体准备切到Worker-only执行；能力禁用时返回`taskUnavailable`，不回退Host native。未配置B4-I组合的进程内Hosting仍使用Backend默认`CreateForSelfContainedFiles()`；App继续使用自身原默认loader。自包含策略允许native的wav／mp3／flac／aac／mov（M4A家族）／ogg六家族，probe与typed decode共用受限格式／file,pipe政策；保持原native选流、源采样率及PeakBuilder，不套分析48kHz重采样、压缩export的300秒预算或PCM16限制。六家族／九健康组合只证明本机有限兼容，不是所有QuickTime变体。

可识别／plausible首8／16字节BMFF（含合法unknown前置box）在probe前做有界结构门禁，实际probe为MOV后及所有MOV decode前再强校验；MOV drefs／absolute path禁用，外部dref／rmra／rdrf及非标self-contained entry带payload拒绝。非BMFF／任意polyglot不保证native probe前发现，也不是完整媒体验证器。结构界限depth32／100k总box条目／4096dref条目，64bit box头保持、mdat／未知非container payload不解析。

Host对固定音源在prepare前后核完整SHA／size、规范fullpath及自身／祖先no-link，fallible读取及read lease释放先于发布；retention／已发布会话资源引用沿真实所有者持有。前后hash／read lease不是OS不可变快照，不承诺任意ABA并发写入检测。发布前失败无候选会话半发布，membership已加入后的event／后置异常不回滚真实成功session并保pin；不能从一次HTTP错误推定未注册session，须读权威状态。失败可产生job／cursor／events，不保证完全无事件。

`POST /api/v1/project-sessions`同步create-only，**201摘要，不是202 job**。必填非空name及metadata；audioAssetId／lyricsAssetId各自可省略或为null，提供时必须是自己上传的非空白资源ID。两项都省略时直接创建真实空工程：默认primary（主歌词）来源且无歌词行、无音轨、无波形、未指定输出时长；摘要沿用无音轨表示audioAssetId=""、audioDurationUs=0，waveform返回available=false／reason=noAudio。仅歌词使用原importer及未指定媒体时长的导入规则，空导入结果仍拒绝；仅音频准备真实音轨／波形并保留空歌词。只有提供音频时才占用受管理音频准备名额，无音频创建不受音频Worker能力禁用影响。四种组合均保持revision0、新建dirty、hasPath=false、无undo/redo及原owner、收据重放与失败清理语义。资源／歌词／音频准备失败保持旧会话。0.68在发布revision0前从已授权StoredAsset固定AudioMedia完整hash／size／安全显示名，保持新建dirty／hasPath和资源租约；不在apply中补metadata或额外制造历史。可选importerId／encodingName，splitRule为0 None／1 Mixed／2 PerTextElement／3 LatinWords／4 ByPunctuation／5 ByWhitespace，默认0；estimateSplitTimes默认false。

空工程请求可直接为`{"name":"新工程","metadata":{"requestId":"create-empty-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-24T10:01:00Z"}}`。后续通过既有歌词导入、音频替换及编辑命令逐步填充。

```json
{"name":"示例歌曲","audioAssetId":"asset-A","lyricsAssetId":"asset-L","composition":{"canvasWidth":320,"canvasHeight":180,"frameRateNumerator":30,"frameRateDenominator":1,"backgroundRed":0,"backgroundGreen":0,"backgroundBlue":0,"backgroundAlpha":255,"outputDurationUs":3000000},"splitRule":0,"estimateSplitTimes":false,"metadata":{"requestId":"create-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

composition可省略，默认1920×1080／30:1／黑色alpha255／未指定输出时长；颜色为byte。工程canvas与preview输出大小分开，默认工程尺寸不表示Worker已支持FullHD帧。

摘要示例（也用于 `GET /api/v1/project-sessions/project-P` 与job.result）：

```json
{"projectSessionId":"project-P","hostInstanceId":"host-H","projectRevision":"0","name":"示例歌曲","audioAssetId":"asset-A","lyricsLineCount":2,"audioDurationUs":3000000,"isDirty":true,"canUndo":false,"canRedo":false,"eventCursor":"host-H:client-C:1","protocolVersion":"1","hasPath":false,"isReadOnly":false}
```

可选`audioAnalysis`为已接受分析的有界投影，未设置时省略（概念值null）。available示例：

```json
{"audioAnalysis":{"status":"available","algorithm":"bass-pulse-rbj-v1","sampleRate":48000,"hopSamples":960,"originUs":"0","sourceHash":"<完整WAV SHA>","pcmHash":"<完整PCM SHA>","sampleCount":"48000","durationUs":"1000000","revision":"sha256:<语义SHA>","contentHash":"<完整canonical SHA>","pointCount":50}}
```

`failed`／`noStream`仅给status，不给points、原始诊断或路径；这些状态来自已有工程数据，apply只接受完整available成功结果。新增originUs／sampleCount／durationUs为字符串，采样配置及pointCount是整数；原summary.audioDurationUs的number合同保持。摘要不依赖原job保留期，保存／重开保留原完整canonical，而非用摘要替代存储。

可选savedProjectRevision表示已保存的精确修订；新建未保存，undo回初始也仍dirty。其他client工程按不存在处理404。只读打开摘要isReadOnly:true，编辑／保存403。

0.80.1首音轨修正：受限self-contained准备的probe与完整decode都显式选择`0:a:0`，mono downmix沿原`-ac 1`，不加重采样。此前双轨MOV首轨8kHz、默认次轨48kHz会用首轨Info配次轨样本，将1秒误算6秒；真实Worker与HTTP已复现并独立修复。WAV等单轨及原strict/预算语义保持；此修正不新增PCM资源或波形接口，不承诺浏览器与FFmpeg压缩音频逐样本时间基一致。

### 6.2 历史与歌词

`GET /api/v1/project-sessions/project-P/history`返回：

```json
{"projectRevision":"1","canUndo":true,"canRedo":false,"undoName":"<领域命令名>","undoCount":1,"redoCount":0,"protocolVersion":"1"}
```

可空undoName／redoName省略。`GET /api/v1/project-sessions/project-P/lyrics/lines?branchId=primary`必须提供来源ID，默认新建主来源为primary；返回该来源全部最小行／元素投影，**没有cursor／limit分页或来源列表HTTP入口**：

```json
{"projectSessionId":"project-P","projectRevision":"1","branchId":"primary","lines":[{"branchId":"primary","id":"line-1","text":"歌词","time":{"startUs":"1000000","endUs":"2000000"},"tokens":[{"id":"token-1","text":"歌","time":{}},{"id":"token-2","text":"词","time":{}}]}],"protocolVersion":"1"}
```

稳定行／元素ID采用实际查询值，不保证示例line-1。读模型绑定同次读取的修订；仅text／time／tokens，没有完整注音／样式／场景JSON。缺branchId400、不存在来源404 commandRejected，旧工程ID404。

### 6.3 命令

`POST /api/v1/project-sessions/project-P/commands`顶层为expectedRevision、command和三个回执字段。原五命令为：`lyrics.setTimeRange`、`history.undo`、`history.redo`、`audio.analysis.apply`、`audio.analysis.clear`。新增六时间动作详见§6.5、十一正文/行/设置动作详见§6.6；分析两命令详见§12.5；原时间命令示例：

```json
{"expectedRevision":"0","command":{"type":"lyrics.setTimeRange","target":{"branchId":"primary","lineId":"<实际行ID>","tokenId":"<实际元素ID>"},"startUs":"1000000","endUs":"1500000"},"requestId":"timing-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}
```

省略tokenId表示整行。startUs及endUs都必填字符串；此命令没有清除端点能力。目标存在、时间合法且符合媒体／跟随来源规则才提交。undo／redo正文将command换为 `{"type":"history.undo"}`／redo，不能带target、时间字段或analysisJobId；原时间命令同样拒绝analysisJobId，无可用历史422。

200响应的历史字段平铺，changeSet只列area及稳定目标，不是任意对象patch：

```json
{"projectSessionId":"project-P","previousRevision":"0","projectRevision":"1","changed":true,"isDirty":true,"canUndo":true,"canRedo":false,"undoName":"<领域命令名>","changeSet":{"areas":["lyricsTiming","timeline","renderInputs"],"branchIds":["primary"],"lineIds":["<实际行ID>"],"tokenIds":["<实际元素ID>"],"requiresFullRefresh":false},"eventCursor":"host-H:client-C:2","protocolVersion":"1"}
```

分析采用／清除沿同一响应结构；实际变化的changeSet保守返回`areas:["project"]`／`requiresFullRefresh:true`，客户端再读summary，不新增分析points或任意patch。

领域no-op仍200但changed:false，不改修订／dirty／redo，不产生本操作project.changed事件；响应eventCursor为client事件流当前位置，其他任务／操作可使其前进。未知命令422 commandNotSupported，旧修订409 revisionConflict，非法JSON／字段400，目标／规则拒绝422 commandRejected。当前九十七种命令：背景与批量编辑见§6.12，正文／行／设置见§6.6、来源见§6.7、元素结构见§6.8、导入见§6.9，静态样式见§6.11；完整清单以冻结API index为准。

### 6.4 精确修订的编辑读模型（0.76.0）

三个认证 GET 都必须提供唯一 `expectedRevision` 查询参数，其值为非负十进制 Int64 字符串。读取在命令同一写者闸门内核对修订并投影；修订不匹配409 `revisionConflict`，不能把两页或不同端点的不同修订拼成同一状态。undo回相同内容也不恢复旧修订。无工程或跨client均404 `sessionNotFound`；不存在歌词来源404 `commandRejected`。没有回执、历史、dirty、redo或事件副作用。

- `GET .../lyrics/branches?expectedRevision=0&offset=0&limit=100`：返回 `projectSessionId/projectRevision/primaryBranchId/offset/totalCount/nextOffset?/branches/protocolVersion`。来源为 `id/name/role/language?/timingMode/lineCount`；role 为 primary/translation/annotation/custom，timingMode 为 independent/followPrimary/none。按保存顺序分页，不推定主来源ID固定为primary。
- `GET .../lyrics/window?branchId=ID&expectedRevision=0&offset=0&limit=100`：返回 `projectSessionId/projectRevision/branchId/offset/totalCount/nextOffset?/lines/protocolVersion`。行保留 `branchId/id/text/time/timingStatus/primaryLineId?/tokens/rubyAnnotations`；元素为 `id/text/time/timingStatus`。行text是原始权威正文，不从元素重组。时间端点是Int64字符串，缺失为 `{}`，零起点为 `"0"`；状态为 missing/imported/estimated/edited/confirmed/invalid。FollowPrimary保留原时间与主行关联，不合成独立时间。
- `GET .../settings?expectedRevision=0`：返回 `projectSessionId/projectRevision/name/canvasWidth/canvasHeight/frameRateNumerator/frameRateDenominator/backgroundRed/backgroundGreen/backgroundBlue/backgroundAlpha/outputDurationUs?/audioDurationUs/protocolVersion`。新时长字段均为Int64字符串；outputDurationUs省略代表尚未确定，导出前必须显式设置，服务不会自动采用音频或歌词末端。尺寸/有理fps/颜色为整数，保持旧composition与summary的number例外。

注音完整保留 `id/baseTokenId/text/language?/relativeScale/gapPx/alignment/segments`，alignment为center/start/end，比例与间距为number；每个segment带独立身份、正文、time与timingStatus，text保兼容拼接投影（详见§18）。当前投影供正文/注音/时间编辑使用，不包含完整样式或场景。

两个分页端点默认offset=0、limit=100；limit为1..100，offset为0..totalCount，末尾允许空页，终页省略nextOffset。缺失/重复必填参数、重复分页参数、非法数字或越界400 `invalidRequest`。每次完整响应最多1MiB UTF-8 JSON，来源/行/元素/注音组/注音片段合计最多20000项；超限413 `resourceTooLarge`，不截断单行。缩小limit可重试，单行自身超限仍拒绝。此预算不改旧 `/lyrics/lines` 的兼容行为。

capability新增 `project-sessions.lyrics.branches.read`、`project-sessions.lyrics.window.read`、`project-sessions.settings.read`。全部五种原命令、回执及静态预览profile保持。后续写命令属于独立切片，不由这些查询接口推定已经可用。

### 6.5 时间动作与严格命令分型（0.77.0）

在同一 `POST .../commands` 增加下列六type，顶层精确修订/回执三字段不变。旧五命令及已知无关nullable槽的显式null保持兼容；新命令只接受本type的字段，拒绝未知/跨type/重复命令字段，frameDelta必须是JSON整数。稳定ID非空白且最多128字符，新微秒是有符号十进制Int64字符串，字段缺失或类型错误400 invalidRequest；未知type仍422 commandNotSupported，不变成反序列化500。

| type | 业务字段 | 历史名 |
|---|---|---|
| `lyrics.stampTokenStart` | `target:{branchId,lineId,tokenId}`、`playheadUs` | token.stamp |
| `lyrics.stampTokenEnd` | 同上 | token.stamp |
| `lyrics.clearTokenTime` | `target:{branchId,lineId,tokenId}` | token.time.clear |
| `lyrics.nudgeTokenTime` | 元素target、整数`frameDelta` | token.time |
| `lyrics.shiftLine` | `target:{branchId,lineId}`、`deltaUs`，不允许tokenId | line.shift |
| `lyrics.setTokenTimes` | `changes:[{target:{branchId,lineId,tokenId},startUs,endUs}]` | token.time |

每个HTTP确认动作前后断开桌面自动合并，不跨请求BeginGroup；一次真实变化一次历史/修订/事件，无变化changed:false且保留redo。原client回执先于修订检查；所有派生字段都参与语义指纹，微秒等价十进制写法归一。同ID改变playhead/frameDelta/delta/batch任一值409 requestIdReuse。准入时固定批次数组，排队执行与回执使用同一输入。

start/end只处理元素。播放头沿现桌面用例夹到0及已知音频末尾；start无可用终点时补当前有理fps一帧并记estimated，容纳不了时拒绝；end确认写edited，时间相同但状态改变仍是真变化。响应仅打点增加 `timingResult:{estimatedPlaceholder:boolean}`，其他及旧命令省略。clear使元素时间缺失、状态missing；再次clear是no-op。nudge按工程有理fps进行有检查的整数微秒换算，不自动夹紧；零帧无变化。

元素不得自动扩展已有行时间或与前后定时元素重叠/倒序。整行shift原子平移行与已定时元素，未打轴元素保持缺失。单元素端点/打点/微调及整行平移采用服务端真实准备音频时长；批量保留既有Application CommitTokenBatch的无媒体上界语义，不能把单元素限制推定为batch限制。batch1..1000项、重复目标400；按输入顺序校验每个中间candidate，任一非法整体拒绝，不重排输入或提交前半批。

新六命令领域拒绝422 commandRejected，field使用实际command.target/playheadUs/frameDelta/deltaUs/changes路径，领域原因放稳定PascalCase failureCode：Untimed（终点/微调没有有效原时间）、InvalidRange（终点不大于起点）、NudgeOutOfBounds（微调负值/超媒体/溢出）、BeyondContentDuration（估算一帧终点或平移超媒体）、DomainValidationFailed（越行/重叠/非法时间/平移溢出）、TargetStale（稳定目标不存在）等实际用例原因。400形状拒绝不伪造领域原因；旧五命令错误wire不改。changeSet仍提供lyricsTiming/timeline/renderInputs及受影响来源/行/元素ID。若完整changeSet默认JSON超过16KiB，为保护64KiB回执，保留Areas并清空全部ID数组、requiresFullRefresh=true；这是完整失效通知，不返回部分ID冒充完整清单。正常小结果保持精确ID，undo/redo同样适用。

capabilities.features逐项公布六种 `project-sessions.commands.<type>`。0.77.0当批只扩时间编辑；0.78.0正文/行/设置增量见下节，有界波形后来在§6.10开放。

### 6.6 正文、行操作与标量设置（0.78.0）

继续同一commands端点/修订/回执；新派生DTO严格限定type字段，一次HTTP动作一次历史，正文不trim或Unicode规范化。本批产品0.78.0／合同1.3.0，HTTP协议仍1。

| type | 业务字段 | 历史名 |
|---|---|---|
| lyrics.setLineText | 行target、text必填非null，可空串 | lyrics.set-line-text |
| lyrics.setRubyText | 元素target、text必填非null，空串删除该基础字注音 | line.ruby |
| lyrics.addLine | branchId，afterLineId可省略/null表示末尾追加 | lyrics.add-line |
| lyrics.duplicateLine | 行target | lyrics.duplicate-line |
| lyrics.removeLine | 行target | lyrics.remove-line |
| composition.setCanvasSize | width、height，正整数，保留奇数尺寸 | composition.canvas |
| composition.setFrameRate | numerator、denominator，正整数，沿原GCD约分 | composition.frame-rate |
| composition.setBackgroundColor | red、green、blue、alpha四个必填0..255 JSON整数 | composition.background |
| composition.setOutputDuration | durationUs必填，null为尚未确定（导出前阻断），或正Int64微秒字符串；缺字段400 | composition.duration |
| composition.useLyricsEnd | 无 | composition.duration |
| composition.useAudioDuration | 无，用服务端真实准备时长，不接受客户端伪造值 | composition.duration |

正文沿原Unicode重切和ID分配，只保留公共前后缀内完整元素的身份/时间/样式；两端都改变时，整个中间区域按原规则重切，不按任意相同中间文字匹配；注音保身份和全部原元数据，无独立发音计时。新增空行未定时、无token；副本紧接原行、按原有效行时长后移并复制展示覆盖，保原状态/样式/主行关联/注音；后行重叠只报告overlapsNext，不自动修正，也不额外加媒体上界。删行沿原展示清理及悬空关联/注音警告规则。useLyricsEnd沿原Core有效末端过滤，无有效末端为no-op；音频时长不可用422 MediaDurationUnavailable。

五种歌词命令尾部可空feedback：selection?={branchId,hasSelection,lineId?}，缺selection表示不建议改变选择，hasSelection=true有lineId，false省略lineId表示清空该来源选区。新增/复制选新行，删除选真实相邻行或清空；正文/注音不强制选择。另含createdLineId?/deletedLineId?/insertedIndex?/overlapsNext?，正文含原用例preservedTokenCount/resetTokenCount（no-op为0/0）。不提供巨型token/ruby ID差集或臆造复制映射；相关正文/结构changeSet保守requiresFullRefresh=true并带必要来源/行ID。

桌面输入及共享用例原本没有低文本长度上限。共享Application提供中立Prepare＋CommitPrepared接缝：一次纯准备产生固定不可变candidate/反馈/ID，提交核workspace引用、generation与原Project引用并一次历史；桌面原入口复用这套实现，不受HTTP预算。Hosting在真正提交前，用S1同一投影预算校验受影响产物单行limit=1；若单行会超过1MiB/20000项，HTTP413且不改工程/历史/修订/dirty/redo，不生成第二轮ID或执行后回滚。删除超大旧行仍可修复。HTTP正文整体沿既有有界请求体，不另造低字符截断。

400用于字段/类型/必填/null/数值形状，409修订冲突，422 commandRejected带原Application稳定failureCode（如TargetStale、IdentifierExhausted、DomainValidationFailed、InvalidCanvasSize、InvalidFrameRate、InvalidOutputDuration、MediaDurationUnavailable），413用于完整产物预算。相同正文/设置、重复清注音等no-op保持redo。来源管理见下节；固定资源再次导入/追加见§6.9；元素结构见§6.8。capabilities逐项公布这十一type；旧五和S2六命令wire保持。


所有预算都按写入后的真实修订序列化；包括9→10等修订字符串长度变化。HTTP正文先检查必要JSON字节预算，新元素在分配ID前限制最终20000项；不会把旧合并元素的Unicode字符数当作新token数。413保原工程/历史/dirty/redo/修订/事件，删除旧超大行仍可修复。所有设置写入保留原工程格式与Core语义，不增加数据迁移。

### 6.7 来源管理（0.79.0）

产品0.79.0／合同1.4.0新增四type，仍使用commands的精确修订/回执/一次动作历史：

| type | 字段 | 历史名 |
|---|---|---|
| lyrics.addTranslationBranch | name必填、language可省/null | branch.add |
| lyrics.configureBranch | branchId/name/language/role/timingMode全部必填；language可null清除 | branch.configure |
| lyrics.removeBranch | branchId | branch.remove |
| lyrics.linkPrimaryLine | branchId/lineId/primaryLineId全部必填，primaryLineId可null解除 | branch.configure |

role/timingMode沿S1已知string enum。Add沿原业务Trim名称，创建空Translation/FollowPrimary来源；Configure保输入原名、不做语言归一化，保持StyleOverride和既有Presentation，不开放其修改。主来源不能删除或改为其他角色，非主来源不能冒充主来源；TimingMode沿原Core合法枚举及PresentationValidator验证，主来源不额外固定为Independent；link目标必须非主来源行，新非null关联必须是真实主来源行。原相同悬空关联的no-op与警告保持，不因HTTP新增自动修复。

来源使用纯Prepare→实际写后修订的S1单来源/受影响单行预算→Commit；固定新ID只分配一次、提交核workspace/gen/原Project引用，借用同一ProjectEditSubmission处理展示删除引用清理与验证，不复制规则或临时工作区。删除不检查被删大来源，允许修复旧不可读数据；必要反馈同时须符合64KiB回执，拒绝前不提交。

Add/Remove真实变化时返回可选branchResult:{selectedBranchId}，分别为新来源/真实primaryBranchId；Configure/Link/失败/no-op省略。结构changeSet保守完整刷新，不截断ID。400为字段/必填/null/enum错误，409修订冲突，422 commandRejected携原LyricBranchEditErrorCode的failureCode与实际输入field，413为完整读投影/回执预算；旧命令wire不改。

来源管理这些新type的稳定ID只沿Core非空白要求，不套S2时间目标128字符上限；原时间/正文目标上限保持。所有响应按实际下一修订校验完整单来源/单行投影；选区反馈还在提交前包含完整64KiB回执预算检查，超限不提交。不接受数字或数字字符串枚举；configure.language与link.primaryLineId缺字段400，显式null分别清除语言/关联。

### 6.8 元素拆合、重排与重切（0.80.0）

产品0.80.0／合同1.5.0新增五type，HTTP协议仍1。仍用commands的精确修订、稳定目标及原回执；所有目标ID非空白、最多128字符。原Core算法为业务权威，一次动作最多一次历史，no-op保redo。

| type | 业务字段 | 历史名 |
|---|---|---|
| lyrics.splitToken | 元素target | line.split |
| lyrics.mergeToken | 元素target，与下一元素合并 | line.merge |
| lyrics.reorderToken | 元素target、destinationIndex必填Int32、reassignTimeSlots必填bool | line.reorder |
| lyrics.resplitLine | 行target、segments必填string数组、estimateTimes必填bool | line.split-rule |
| lyrics.resplitBranch | branchId、rule必填string enum、estimateTimes必填bool | lyrics.split-rule |

split要求原元素有效时间且至少两个Unicode文本元素，否则no-op；按Unicode元素数floor(n/2)分，原Int128 ties-to-even分配时间，左元素保ID/样式、右元素新ID，两个状态estimated。merge最后一个为no-op；合并下一元素、左ID/样式保留，时间有缺失则missing，否则取并集，同状态保留/不同为estimated；原注音悬空规则保持，不自动重绑。

reorder同位置no-op，正文按新元素顺序拼接。reassignTimeSlots=false沿原领域顺序验证，非法422；true把原合法时间槽依次赋给新顺序的已定时元素并标edited，未定时仍缺失。resplitLine要求非空片段精确拼回原正文且均为Unicode元素边界，空行允许[]；沿原保留ID、首段继承和显式估算逻辑，手工合并段可创建新ID；规则/手工比例估算使用UTF-16长度权重，不能与splitToken的grapheme中点混同。resplitBranch rule为none/mixed/perTextElement/latinWords/byPunctuation/byWhitespace；none/空来源no-op，整来源原子提交，不逐行发历史。

可选tokenResult只用于这五type：changedLineCount为真实变动行数，selection?={branchId,lineId,hasTokenSelection,tokenId?}。split/merge真变化选实际元素；resplitLine真变化选该行并hasTokenSelection=false清元素；reorder/branch/no-op省略selection。preservedTokenCount?/createdTokenCount?/estimatedTokenCount?仅split/resplitLine/resplitBranch用原统计（no-op为0），merge/reorder省略，不伪造统计。无巨大映射数组。结构changeSet带四内容区域/必要来源行ID并完整刷新，branch不枚举所有行ID。

纯Prepare固定candidate/ID→宿主预算→核workspace/gen/source一次Commit；桌面旧入口复用原规则，不套HTTP限制。segments在JSON数组长度检查后才分配字符串数组，超过20000项413 command.segments；其余形状/缺失/null/非string400，空片段/拼接/Unicode边界非法422 InvalidTarget。完整最终单行S1投影仍≤1MiB/20000项；resplitBranch增加本次变更行完整单行投影累计16MiB资源预算，超限413、整批无变化，可逐行操作。

为在分配大批片段/ID前执行预算，采用中立流式Unicode枚举与唯一原拆分规则，创建片段/ID前可选回调；旧两参数API无预算且共用同一算法，不按旧合并元素字符数猜最终token数。手工segments以原字符串span及流式边界验证，不构造巨大Concat/全边界HashSet。准入固定segments副本供回执和执行共用。不是恢复全量或性能优化范围；以固定旧0.78 Core程序集与新算法独立反射对照1237输入×六rule共7422项片段，精确相等；原Core/编辑回归另记。

400形状、409修订、422 commandRejected带原稳定failureCode及真实command.*字段、413资源预算；预算拒绝保历史/dirty/redo/修订/事件。后续再次导入/追加另冻结。

原Core按已有oldTokens构建的映射仍随已有行规模分配；本批限制新segments/新ID和完整产物，不宣称已封顶所有旧数据工作内存，也不以旧元素数量阻止合并修复。

### 6.9 再次导入与粘贴追加（0.81.0）

产品0.81.0／合同1.6.0新增两type，HTTP协议仍1。同commands、无新job类别，精确修订/回执/一次动作历史保持。HTTP只接受本client授权上传资源，不接受本机路径；该0.81历史批次保留当时上传限制；当前流式上传与可空部署策略见§5。

- `lyrics.importAsset`：assetId和destination必填；options可省，若出现须object，含importerId?/encodingName?（可null，未知沿原回退自动检测）、splitRule（默认none，出现须合法string enum）及estimateSplitTimes（默认false，出现须bool）。destination为严格union：existing{kind,branchId}或newBranch{kind,name,role}，新角色仅translation/annotation/custom。
- `lyrics.appendText`：text与destination必填；destination为existing{kind,branchId}或newTranslation{kind,name}。不允许其他分支字段混用。

import existing完整替换行，保来源名/角色/时间策略/样式，未知导入语言按原回退；newBranch沿原Trim名称与Independent策略。支持既有八导入器，文本MIME与application/octet-stream可作为新import候选（SRT/VTT/ASS/TTML常以octet上传），不是修改旧create准入。新来源/行/元素/注音组及segment由原一次前缀映射产生固定ID，同资源的新意图仍是新导入/历史，同回执重试不重复解析或生成ID。新/替换的目标及结果ID须≤128，不截断或重写，追加不扫描旧未触及行。

append按原CRLF/CR→LF、跳过空白行，保其他原始空白，Unicode元素未定时；新翻译FollowPrimary，existing保原来源策略。空结果沿原emptyLyrics拒绝。真实历史名lyrics.import/branch.import/lyrics.paste。

可选importResult含lineCount/selectedBranchId/selectedLineId/report?；file import完整report，append不造report。完整保27字段：导入器/格式/实际编码/检测依据、全部源/结果/计时/跳过/续行/推导/兜底数量、offsetUs十进制Int64字符串、BOM/newLineKind、完整issues(kind/severity/sourceLineNumber/rawText/message，0行号合法)、候选名/歧义/丢弃特征、重切统计及issueCount/maxSeverity。无问题maxSeverity省略。原有Error诊断但有合法行的输入仍沿原业务可提交，不一概拒绝，也不截断报告。

资源准备在回执owner之后获取无队列名额（Host2/client1，满429 requestQuotaExceeded）。Writer捕获固定source/workspace/gen/下一revision/audioDuration与目标，然后Writer外资源lease读/身份hash与size核验、纯parse/candidate；资源及所有易失败cleanup完成后重入Writer，任意修订变化精确409、close/只读等再核，不复活旧会话。已接受owner固定在途/回执/client生命周期，断流只停止等待；shutdown排水覆盖准备，不把Task.Run cancellation称为硬截止。

共享可选preparation guard，桌面默认无预算。HTTP每次64MiB累计准备工作配额：保留的IR/行/元素/issue/片段、字符串副本与集合/数组容量在分配/扩容前扣；原始有界bytes/有限缓冲/完整UTF16解码另受输入上限约束。这是逻辑工作配额，不是CLR heap精确上限或OS sandbox，不加任意源行/grapheme硬帽。最终新/替换行逐行按已固定前缀ID和写后revision执行S1真实1MiB/20000项；累计投影16MiB。完整成功回执（含完整report）仍≤实际配置64KiB，超限413且工程/历史/dirty/redo/修订/事件全不变，无第二次ID/提交后回滚。

本片修复LRC原强制编码被忽略的缺陷，使EncodingOverride按原公开参数承诺生效；默认auto检测保持。单列共享回归与固定旧程序集的八格式默认结果/report独立对照，不把这项业务修正隐藏在HTTP传输内。纯Prepare/Commit复用原业务、严格DTO及全部预算/权限/receipt/并发/close/shutdown回归和真正HTTP保存重开后才交付。

新增type只在成功时给importResult，不混入timingResult/feedback/branchResult/tokenResult；不是异步job。成功总会追加/替换为新ID，因此一次新意图一历史；空输入拒绝，无伪造no-op。错误：形状400 invalidRequest、修订409、只读403、目标/领域422 commandRejected附原稳定failureCode；空结果422 emptyLyrics附EmptyImport/EmptyText；跨client资产401 resourceUnauthorized、未知资源404、MIME415 unsupportedMediaType、准备名额429 requestQuotaExceeded、任一完整资源预算413 resourceTooLarge。field为实际command.assetId/destination.*/text/options.*或command，未知会话/跨client中立404保持。

默认工作计费为每模型/中间集合项512＋2×文本UTF16长度，另预付数组/行切分/字符串副本等逻辑字节，使用后不返还；它是确定的准备工作配额，不是实际分配测量。报告问题保留前可先检查完整回执的必要下界，最后仍检查完整响应上界；不截断问题。options省略与空对象虽然默认效果相同，保留实际不同输入指纹；未知importer/encoding虽回退，指纹仍保输入值。目标及新ID的128上限按.NET UTF-16长度计，旧未触行不受新的地址性门禁扫描。

默认八格式×两输入编码自动检测×三换行×六rule×估算两值，共576组旧/新独立runtime完整文档与报告投影相等；强制LRC修复单列。旧public方法CLR签名及public抽象导入器protected List扩展入口保留，新增guard-aware方法默认forward旧扩展，HTTP仅固定八受信导入器，不开放新插件入口。同步托管解析/完整解码输入依然有不可避免临时内存，64MiB工作配额不构成硬deadline或OS隔离。

**0.110.2 歌词兼容修复（合同 1.42.2）：** 新建工程接受文本 MIME、`application/ttml+xml`，以及已登记扩展名（`.lrc/.elrc/.txt/.srt/.vtt/.ass/.ssa/.ttml/.xml`）的 `application/octet-stream`；未知扩展的二进制和视频 MIME 不作为新建歌词输入。既有导入规划／`lyrics.importAsset` 的 octet 内容嗅探保持，不收紧未知扩展名输入。前端为上述歌词扩展名补齐文本／TTML MIME。

ASS 保留普通 `Dialogue` 与 `\k/\kf/\ko` 解析。文件同时具有 Mako LRC Studio 头标识和 `Comment` 的 `Effect=karaoke` 源事件时，改从这些源事件还原歌词，忽略绘制 Dialogue 层，支持 `正文|读音`、`#|续读音`、裸 `#` 延音和空拍；时间仍是源厘秒精度。样式／特效／分部及翻译、罗马音字幕不映射，报告说明损失；不新增 ASS 导出。

TTML 正文默认仍须处于 `http://www.w3.org/ns/ttml` 命名空间。只有同时带明确 AMLL/Apple 标记（`itunes:timing` 为 Word/Line、AMLL metadata，或 `mkg:timing-profile=amll-absolute`／`mkg:profile=ttml.amll`）且不是项目 profile 时，才把结构可确认的空命名空间内容元素（`body/div/p/span/br`，父级已是 `tt` 或 TTML 内容）在内存中恢复为 TTML 命名空间，并在导入报告写明兼容处理；不改源文件。无标记文档、项目 profile、冲突声明，以及认不出的空命名空间本地名保持拒绝或诊断，不按标签名静默恢复。标准 media 时间与 AMLL absolute 仍然分开。non-drop SMPTE 在已知帧率下按帧换算；`clock` 与 drop-frame 拒绝。原生 AMLL 在基础元素没有独立时间、且该注音组每个片段都有合法时间时，用片段最小起点和最大终点推导正文时间并标为 Estimated；项目 profile 的 Missing 原样往返，不凭注音时间改写。标准简单 ruby（含样式引用上的 `tts:ruby`）和既有 container/base/textContainer/text 可导入；多个 base 无法唯一绑定基础元素时诊断 `rubyAssociationAmbiguous` 并保留正文。`ttm:agent` 的定义、继承、单行与行内覆盖，以及多个 ID 的合唱，映射为独立计时的具名分支；未定义引用单独警告并保留正文。`x-bg`／`x-translation`／`x-roman` 旁轨进入和声、翻译或注音来源，不再从正文里静默丢掉。区域排版、动画、嵌入媒体和需要外部时钟的特性仍不能伪装成歌词数据。

### 6.10 有界同源波形读取（0.82.0）

产品0.82.0／合同1.7.0／协议1新增两路径三方法；能力逐项公布project-sessions.waveform.read与project-sessions.waveform.peaks.read。

首轨修复0.80.1完成后，在原一次create/open/recover准备结果上保留实际峰值，不重新decode，不引入PCM播放资源或跨重启持久缓存。Host默认64MiB已保留峰值payload预算（8×全部层桶数，非总heap，原始准备临时内存另计），按源SHA/准备profile/样本与源Info/所有层实际floatbits的规范身份去重；同身份已留存可增加session引用，即使剩余额度0。不同client即使知道hash也不能越权取他人的binding。

无容量时原创建/打开/恢复仍成功，session不保留失败的LoadedWaveform作为旁路，元数据available:false reason=cacheBudgetExceeded；之后缓存空出也不自动重新准备。无音轨noAudio，检测固定asset/path/已有AudioMedia身份变化则audioIdentityChanged，不返回旧峰值；旧AudioMedia缺失的合法来源不自动补元数据/历史。正常关闭、候选注册失败、registry退出均释放binding，最后引用释放entry；关闭后新读取404，已取得≤64KiB独立窗口字节的在途请求可完成。

| 路由（session子路径） | 请求／结果 |
|---|---|
| GET /waveform | 无查询、不带expectedRevision；返回projectSessionId、available、protocolVersion。不可用仅多reason；可用含下列完整payload |
| GET /waveform/peaks | waveformId、level、start、count全部必填唯一；count1..8192、start≥0且完整窗口不越level实际桶数，8N bytes |
| HEAD /waveform/peaks | 同GET状态/长度/ETag，所有状态无body |

可用payload：waveformId/sourceSha256/profile/sampleRate(int)/sampleCount(Int64string)/durationUs(Int64string)/sourceChannels(int)/waveformChannels:1/baseBucketSamples(int)/format:"minmax-f32le-v1"/levels[{level:int,bucketSamples:Int64string,bucketCount:int}]。数组给全部真实层，不截断。waveform身份包含明确峰值codec/首轨mono语义标识first-audio-mono-peaks-v1，不随歌词修订改变；metadata不触源文件IO/native/Worker握手，实际来源身份核当前固定会话事实。

peaks为application/octet-stream，float32小端min/max交错、原bits完整；ETag绑定waveformId/level/start/count和窗口bytes，If-None-Match支持原资源常规匹配→304，HEAD仍无body。此版Range明确400 invalidRequest field=Range，不提供Content-Range；未知/重复query及格式/窗口越界400，field定位实际参数。未知或旧waveformId404 resourceNotFound，无会话/跨client沿中立sessionNotFound404。只读会话可读取，仍要求通用认证/Origin/Host/client读保护。

峰值描述服务实际首轨downmix单声道（不是多channel极值合并），非finite按现准备规则处理、合法float不强夹[-1,1]；最小精度为base64sample完整尾桶。对比独立原prepare/pyramid及HTTP完整各层分窗bytes、ETag/HEAD/304/Range、跨client/close/同源去重/低预算/先前不变scope。WAV浏览器与MP3/FLAC逐样本时间基仍需各自实测，不从原上传SHA或服务峰值一致推定播放器一致。

JSON元数据只提供事实，不需要副作用回执、也不改变历史/dirty/redo/事件。HEAD的400/401/404同样无body；GET/HEAD认证先于If-None-Match，因此旧ETag不绕过权限。成功与304暴露ETag/Content-Length，沿现安全头和私有缓存策略。窗口count按请求完整返回，越尾拒绝而非悄悄裁剪；无页游标/无需把不同歌词修订当不同音频。

### 6.11 四层静态样式读写（0.83.0；0.110.0 新增外辉光；0.112.85／合同1.50.0 改为两态）

产品0.83.0／合同1.8.0／协议1新增一个GET和两命令。能力为 `project-sessions.style.read`、`project-sessions.commands.styles.patch`、`project-sessions.commands.styles.restore`；旧三十三命令及原静态预览profile保持。规则复用共享Application的typed canonical Prepare／Commit，不序列化Core平面Color／Gradient访问外观。

**读取。** `GET /api/v1/project-sessions/{id}/style` 必须提供唯一expectedRevision及kind。kind为project时不带ID；branch带branchId；line带branchId／lineId；token带branchId／lineId／tokenId。所有查询名和值精确匹配；拒绝多余、重复、缺少或不符合kind的字段。ID仅要求非空白，保原Unicode及前后空格，不trim、不新增128字符上限。认证、归属和修订门禁沿§6.4，未知／跨client session为404 sessionNotFound，陈旧修订409 revisionConflict，形状400 invalidRequest。稳定目标不存在为404 commandRejected／failureCode TargetStale。

响应为 `projectSessionId/projectRevision/target/baseline/local/staticHierarchy/hasPresetApplications/resolution/protocolVersion`。baseline和staticHierarchy是完整嵌套canonical样式（合同1.50.0起）：typography七字段；unsung为fill（唯一material）、strokes（由内到外0..4层，每层material／widthPx／join）、nullable shadow／glow；sung为fill及strokes／shadow／glow三个跟随项，每项为 `{"kind":"follow"}` 或 `{"kind":"set","value":…}`（strokes的value为层数组，空数组即关闭；shadow／glow的value为对象或null关闭）；wipe为featherRatio与nullable highlight；再加verticalCenterRatio／horizontalAlignment／opacity／scale。字体null、效果null与高亮null**始终保留**。local为本层真实覆盖的property／value数组，按属性路径表顺序：缺少属性表示本层继承，fontFamilyName:null表示明确选择后端默认字体，unsung.shadow／unsung.glow／wipe.highlight:null表示明确关闭，sung.shadow／sung.glow为 `{"kind":"set","value":null}` 表示已唱一侧明确关闭、`{"kind":"follow"}` 表示本层明确跟随；描边按整表出现。非空局部阴影／辉光／高亮及已唱显式阴影／辉光展开为实际存在的叶成员，未覆盖叶不凭空补齐；project没有上层覆盖，local为空数组，baseline即其本层真值。

resolution固定 `staticHierarchy`，仅解析baseline→branch→line→token。hasPresetApplications只提示整个工程存在固定预设应用，包含禁用或不作用于此目标的应用；不证明该目标被覆盖。该投影不是实际帧最终外观，真实Runtime还会叠加固定预设、节点祖先和clip上下文。读取的包材质仅为 `kind:package/packageId/revision/exportId` 只读身份描述，不传参数，不编译或执行；不能作为新写入材质回传。读取不改变工程、历史、dirty、redo或事件。

**命令。** 沿§6.3顶层expectedRevision与三回执字段。新命令分别为：

```json
{"type":"styles.patch","targets":[{"kind":"line","branchId":"primary","lineId":"<实际ID>"}],"changes":[{"property":"typography.fontSizePx","value":80},{"property":"unsung.strokes","value":[]},{"property":"sung.glow","value":{"kind":"follow"}}]}
```

```json
{"type":"styles.restore","targets":[{"kind":"line","branchId":"primary","lineId":"<实际ID>"}],"properties":["typography.fontSizePx","unsung.strokes"]}
```

target是严格kind union，ID组合与GET相同。每次1..1000个同种且不重复目标，project只能一个；patch全批使用同一changes，restore全批使用同一properties。两数组均非空且最多43项（合同1.50.0前为29项），禁止重复属性、unsung.shadow／sung.shadow／unsung.glow／sung.glow／wipe.highlight父属性与其任一叶同时出现。value必须显式提供，不能用缺失value表示恢复继承。kind／property／所有样式枚举只接受准确camelCase字符串，数值仅JSON number，整数不接受小数词法；未知、重复、跨type字段或非法null均400 invalidRequest。

| canonical property | value |
|---|---|
| typography.fontFamilyName | string或显式null；空串／空白沿Core合法语义 |
| typography.fallbackFamilyNames | 有序string数组，空数组清空链；成员非空白且不重复 |
| typography.fontSizePx／fontWeight／isBold／isItalic／letterSpacingPx | 正有限float／100..900整数／bool／bool／有限float；字重不要求整百 |
| unsung.fill／sung.fill | 完整solid或gradient；null及package写入拒绝 |
| unsung.strokes | 0..4层数组（由内到外），每层完整material／widthPx（非负有限float）／join（miter、round、bevel）；空数组即关闭；null及任一层package写入拒绝；整表替换上层全部层 |
| sung.strokes | `{"kind":"follow"}` 或 `{"kind":"set","value":<同unsung.strokes>}` |
| unsung.shadow | 完整color／opacity／angleDegrees／distancePx／blurRadiusPx对象或显式null关闭 |
| sung.shadow | `{"kind":"follow"}` 或 `{"kind":"set","value":<完整阴影或null>}` |
| unsung.shadow.*／sung.shadow.*（color／opacity／angleDegrees／distancePx／blurRadiusPx） | 完整RGBA／0..1有限float／有限double／非负有限float／非负有限float |
| unsung.glow | 完整color／opacity／blurRadiusPx／spreadPx对象或显式null关闭 |
| sung.glow | `{"kind":"follow"}` 或 `{"kind":"set","value":<完整辉光或null>}` |
| unsung.glow.*／sung.glow.*（color／opacity／blurRadiusPx／spreadPx） | 完整RGBA／0..1有限float／非负有限float／非负有限float |
| wipe.featherRatio | 0..2有限double（行字号倍数，注音按注音字号） |
| wipe.highlight | 完整color／widthRatio／opacity／blend对象或显式null关闭 |
| wipe.highlight.color／widthRatio／opacity／blend | 完整RGBA／(0,2]有限double／0..1有限float／normal、screen、add |
| verticalCenterRatio／horizontalAlignment／opacity／scale | 0..1有限double／left、center、right／0..1有限float／正有限float |

颜色red／green／blue／alpha四个0..255整数全部必填。solid材质为kind+color；gradient为kind+完整gradient。渐变完整保kind、angleDegrees、centerX/Y、radiusX/Y、coordinateSpace（canvas、line、glyph；合同1.50.0删去progress，写入为400）、colorSpace、huePath、stops；所有几何有限，radial的中心0..1且半径>0，linear不额外套径向范围。色标至少2个，offset非降序且首0末1，允许相等位置，保原顺序；不排序、不夹紧或归一化。枚举值及完整shape见OpenAPI。

patch只改指定本层成员，不能把整个解析结果固化为覆盖。明确选中材质槽才整体替换；未选的包身份、完整参数、其他效果和几何原样保留。关闭后的局部效果再set叶会成为partial overlay并从真实上层解析，上层也无效果时才用Core透明／零宽基准；project无效果时同样使用该基准。已唱阴影／辉光的叶写入总把本层已唱一侧改为显式：本层原为跟随、未设或关闭时只含该叶，其余分量从该态的上层解析补齐。restore仅清本层对应覆盖，最后一个成员移除后归一为无覆盖；对显式关闭或本层跟随的效果restore某叶是no-op，restore整个效果才取消关闭或跟随。project没有上层，restore返回422 commandRejected／UnsupportedProperty。

元素仅允许两态填充、描边、阴影、辉光、扫字样式（wipe.*）及整体opacity，字体、位置、对齐、scale均422 UnsupportedProperty。新值领域非法为422 commandRejected／InvalidValue；失效目标为422 InvalidTarget。已有非法基线沿原resolver规则明确拒绝，不在此批新增自动修复：GET为422 InvalidValue、field=style，写入422且不修改工程。新typed进程内调用的非有限CLR浮点在序列化准入返回400 command，不产生未映射任务异常；HTTP parser本身拒绝非有限JSON数值。

**静态外辉光（合同1.42.0）。** `glow` 默认为 null；完整对象默认白色、opacity 1、blurRadiusPx 8、spreadPx 0。部分覆盖没有上层对象时从透明色／1／0／0合并。模糊半径为高斯 sigma，spread 为 round 字形轮廓外扩；辉光来源为不透明真实字形，减去字形内部，不读取填充 alpha，也不随已唱切分重复绘制。公共样式透明度只参与一次。Ruby继承基础 token 外观，倒计时组和标记通过原外观命令支持相同字段。Core解析来源由21增至26项，模板整体/叶选择随之扩展。SDK只读 `StyleView.Glow`，现有 writer 保留该静态值。TargetColor 可由包 capture 的可选 `includeGlow`（默认 true）控制，coverage 和材质原始区域排除辉光。工程／歌词／模板 schema 为29／18／7，不新增旧格式自动迁移；更多实现边界见[静态外辉光](../style-reference/glow.md)。

**两态外观的过渡期（0.112.74，P3 第 2～6 步）。** Core 样式改为未唱／已唱两态外观：未唱为填充、多层描边（由内到外，最多 4 层）、阴影、辉光；已唱的描边、阴影、辉光各自可「跟随未唱」或显式设置，默认跟随；另有扫字样式（羽化比例与高光）。描边去掉位置字段，按整表覆盖：本层设置即替换上层全部描边层，空表即关闭。工程／歌词／模板 schema 为 31／19／9，旧版本明确拒绝、不迁移。本阶段 HTTP 合同形状不变，宿主按旧的单层描边与 26 项属性换算：读取的 `stroke` 是最内层，`stroke.position` 恒为 outside；local 的本层描边按整层展开为 material／widthPx／join／position 四叶，关闭仍为 `stroke:null`。写入描边叶时以本层已有描边、否则（未设或已关闭）以上层解析结果为基线，都没有时用透明零宽描边，写成完整一层并保留更外的层；写入时刻的结果与旧的部分覆盖相同，但上层之后再改不会传到这一层；结果宽度为 0 时即无描边（空表，等同关闭）。`stroke.position` 写入被接受但不改变工程。渐变 `coordinateSpace:progress`（Core 已删去，D-P3-7）写入返回 422 InvalidValue；包材质回退渐变声明 progress 时读取即报 `PackageMaterialFallbackInvalid`（此前到编译时才报 `PackageMaterialFallbackUnsupported`）。restore 描边叶时把该分量恢复为上层的值，整层与上层相同则整表恢复继承。模板的旧整项（描边、阴影、辉光）对应两态的整项，已唱一侧按模板原样复制；旧的分量只对应未唱一侧；扫字样式不出现在旧清单中。已唱一侧的显式效果与扫字样式在第 7 步随合同改版开放。0.112.75 起渲染按态读取外观（P3 第 3 步）：未唱、已唱两遍各画本态的描边与阴影（描边仍只画最内层，多层在第 4 步接入）；两态辉光相同（含已唱跟随）时整段只画一次，不同时在各自的演唱区域内各画本态一次（注音的分态辉光仍先于全部注音填充）；文字标记按待唱／已过整体取一态。两态装饰不同时，已唱区域按扫字线延到行两端之外：行首元素开唱后向左延到画面边缘，行尾元素（注音则为末段）唱完后向右延到画面边缘，字框外的描边、阴影、辉光随之切换，不在字框边被竖直切开；两态装饰相同时区域照旧。装饰外扩、材质与图的目标框、工作框和部件判空（F3）都取两态之大或之并。脚本 SDK 的描边、阴影、辉光仍只读写未唱一态（两态读写在第 6 步）：与基线不同的写入两态一起生效（已唱一侧改为跟随），与基线相同的写入视同未写，未改的成员保留已唱一侧；已唱显式描边与未唱描边的材质不同且其中一侧是包材质时，准备帧以 `SungStrokeMaterialPending` 拒绝（描边材质槽在第 6 步分成两态）。合同改版前，已唱显式装饰只能来自工程或模板文件。0.112.76 起渐变按 P3 第 4 步绘制：重合色标即硬边，取样改按色标分段、在重合处提交位置相同的两个色标，0°／90° 线性渐变的分界不混色，斜向线性与径向渐变在分界处留约 1 设备像素的过渡；没有硬边的渐变保持原 65 点均匀取样。按单字坐标（`glyph`）接入：每个绘制单元按自己的字框（横向为前进宽度，纵向为字体上升部到下降部）各建一个着色器，填充与描边同理；渐变支持矩阵只剩 Oklch 插值未接入。整行坐标（`line`）的纵向参考框由按字号估算（基线上 0.85 字号起、高 1 字号）改为本行各绘制单元字体度量的并集（D-P3-8，已接受的观感变化）；注音的整行渐变横向取所属底字整行，纵向取注音自己的字体度量。同版修复径向渐变的半径：此前着色器的局部矩阵把半径取了倒数，椭圆只有约 1/半径 像素，径向渐变几乎整片是末端色；现在半径为参考框宽高乘 `radiusX`／`radiusY`，与 `GradientSampler` 的径向取色一致。0.112.77 起多重描边接入：各层由内向外依次占据一圈，在同一张描边层里由外向内绘制（外层画笔取累计宽度，内层替换外层内侧，最后减去字形），单层与此前逐像素相同。阴影与辉光改以整体轮廓（字形加本态全部描边）为来源并画在全部描边之下，层序为辉光 → 阴影 → 描边 → 填充（D-P3-9，已接受的观感变化：带描边时阴影变大并退到描边之下，透明填充加描边也有阴影，辉光从描边外缘起算）；两态轮廓（总宽或最外层拐角）不同时辉光按态分开绘制。图场景部件顺序同上；原样描边部件含全部各圈，描边材质的区域只是最内层一圈，这时外圈由新增的外圈描边部件（`BaseOuterStroke`／`RubyOuterStroke`）原样捕获，没有外圈时判空跳过；描边槽没有材质时不列外圈部件，层数不变。`StrokeCoverage` 为全部描边圈的并集。装饰外扩按 P3 设计 §10.6：描边宽度一律取累计宽度；元素框的辉光项加累计宽度，排版留白的辉光项不变；区域与原始捕获外扩为整体轮廓外扩（斜接按上限）加阴影、辉光中较大者。准备帧的绘制外观里，含源包材质而脚本未改描边时只把最内层换成几何种子，外圈各层原样保留；脚本改了描边时仍整表换成脚本给出的单层。0.112.78 起注音的装饰按注音比例（`relativeScale`）缩放：描边各层宽度、阴影距离与模糊、辉光模糊与扩张，已唱一侧显式的值同样缩放（D-P3-10，已接受的观感变化：默认比例 0.5 时带装饰的注音描边变细、阴影与辉光变小）；填充不缩放。注音的对象区域、原样辉光工作框、网格分层框与准备帧里注音字簇网格定义域的校验都按缩放后的样式计（§10.6 第 1 条的 S 取缩放后的累计描边宽度）：比例小于 1 时定义域只需容纳缩小后的装饰，大于 1 时须容纳放大后的装饰。0.112.79 起扫字羽化在直接绘制与原样捕获中生效（P3 第 5 步第一部分）：每个已定时元素按不夹值的演唱位置 x(t) = L + (R − L)(t − s)/(e − s) 求遮罩 m(x) = clamp(0.5 + d(x(t) − x)/W, 0, 1)，只作用在元素自己的横向区间内；带宽 W = 羽化比例 × 行字号（注音按注音字号），起止 L、R 与硬切换相同，d 为 R − L 的符号（从右向左排版时反向）。过渡带在元素开始前 W/(2v) 进入、结束后 W/(2v) 退出，相邻元素各按自己的速度，交界那一刻两侧相等。两态装饰不同时，行两端之外的延伸部分取区间端点处的 m。未定时、零宽、零长的元素，前奏准备阶段与非逐字分支照旧硬切换。注音分段有时间时按分段时间，跟随元素时按元素时间。过渡带内两态各画一层，未唱 ×(1 − m)、已唱 × m 相加后普通叠加；已唱硬区、过渡带、未唱三块都换成整数设备像素区域（过渡带先沿横向各外扩 1 个设备像素），CPU 与 Vulkan 同一做法。两态画出来相同时仍按硬切换只分两块。`SungCoverage` 在带内为字形覆盖 × m，其他覆盖不受羽化影响。图场景的原样部件各自按 m 合成后按部件层序叠回；填充包材质按两态拆侧的部件在 0.112.79 暂以 `WipeFeatherMaterialPending` 拒绝。0.112.80 起这类部件在过渡带内按 m 合并（P3 设计 §10.2，第 5 步第二部分）：两侧各画本侧硬区 ∪ 过渡带，本帧确有过渡带时，每侧先求（前景 c，擦除 k）——原样一侧 k = α，材质一侧取其链上历史（SourceOver 为 α，ReplaceCoveredBackdrop 为声明覆盖与历史替换范围之并）——合并为 (lerp(c<sub>U</sub>, c<sub>S</sub>, m), lerp(k<sub>U</sub>, k<sub>S</sub>, m)) 一层，按替换规则合成一次，结果等于整字未唱与整字已唱两种结果按 m 的交叉淡化。m 取该部件已唱覆盖 ÷ 字形覆盖（两张覆盖按部件捕获相同的世界矩阵与扫字分区在对象工作框内画出，字形覆盖为 0 处两侧直接相加）；覆盖与两侧载荷在合并后即释放，计入共享帧账。合并层保留两侧的材质历史，后续图读取它时替换范围与不合并时相同；组合调度的逐层导出直接导出合并后的一对。本帧没有过渡带（羽化比例为 0、带尚未进入或已经离开）时两侧照旧依次合成，像素与表面峰值不变；不再报 `WipeFeatherMaterialPending`。合同改版前羽化比例只能来自工程或模板文件。0.112.81 起扫字线高亮生效（P3 设计 §10.3）：以演唱位置 x(t) 为中心、全宽 w = 宽度比例 × 行字号（注音按注音字号）的升余弦峰 h(d) = 强度 × 高亮色 α × ½(1 + cos(2πd/w))（|d| < w/2），叠在本态填充与各圈描边上，阴影与辉光不叠。混合方式为普通／滤色／相加，按未预乘颜色计算并保持原有透明度：普通 mix(c, 高亮色, h)，滤色 1 − (1 − c)(1 − 高亮色 · h)，相加 min(1, c + 高亮色 · h)；缺省混合方式由普通改为滤色。峰与羽化无关，羽化比例为 0 时同样显示；峰按整行坐标绘制，可延伸到相邻元素上。峰的位置取正在演唱的元素（s ≤ t < e）的 x(t)，交接时刻只取后一个元素；没有正在演唱的元素时，取按时间外推后峰仍压到自己区间的元素（行首之前滑入、行尾之后滑出）。参数取该元素解析后的扫字样式，元素覆盖关闭高亮时它的峰不画；未定时元素、前奏准备阶段与非逐字分支没有峰，注音分段各按自己的进度。过渡带内两态各叠高亮后再按 m 交叉淡化。高亮在本笔画完后单独叠一遍，只改峰的横向范围内已有的像素，峰外像素与不开高亮逐字节相同。图场景中填充、描边与外圈描边的原样部件含高亮，覆盖类输入（`GlyphCoverage`、`StrokeCoverage`、`SungCoverage`）不含；包材质部件的材质输出暂不叠高亮（已知限制）。合同改版前高亮只能来自工程或模板文件。0.112.82 起脚本 SDK 两态读写外观（P3 第 6 步，P3 设计 §9）：只读基线 `StyleView` 改为 `Unsung`／`Sung` 两个 `AppearanceView`（填充、由内向外的描边各层、阴影、辉光）加整体透明度与扫字样式 `Wipe`，已唱一侧跟随时与未唱取同一值；`StyleView.Material(MaterialSlot)` 按四个槽（未唱／已唱填充、未唱／已唱描边）取材质。`StyleUpdate` 改为按态的 `AppearanceUpdate` 加透明度系数与可选 `WipeValue`。`TextOutput` 的 `SetFill`／`SetStrokes`／`SetShadow`／`SetGlow` 都带 `TextState`（`Unsung`、`Sung`、`Both`，`Both` 等于两态各写一次）；`SetStrokes` 整表替换一态的描边，0～4 层，包材质只能在最内层（`InvalidStrokeLayers`）；新增 `SetWipe`（能力 `style.wipe`，两态共享，只用于元素族与底字，文字标记为 `PropertyTargetUnsupported`）。投影回绘制描述时每态每成员与基线比较：已唱一侧写了且与结果的未唱相同即跟随（两态写同一值与此前逐字节一致），不同即显式；只写未唱而已唱原为跟随时，已唱钉成源的未唱值，不跟着变；扫字样式与基线不同才写，写入的羽化与高亮都按它绘制。描边材质槽分为未唱描边、已唱描边（`PreparedMaterialSlot`／`MaterialSlot` 各 4 个值）：两态绑定同一份材质（含已唱跟随未唱时源材质的重复绑定）时照旧是一个描边材质部件；不同时与填充一样按侧拆开，没有材质的一侧原样捕获该态最内层一圈，过渡带内按 m 合并，m 取该态最内层一圈的已唱覆盖 ÷ 该圈覆盖（两态最内层形状不同时为近似）；拆侧描边的已唱区域按扫字线延到行两端之外，字框外的描边随之切换（拆侧填充照旧只在两态装饰不同时延伸）。不再报 `SungStrokeMaterialPending`。HTTP 诊断的材质槽在合同 1.49 只有 0～2，已唱描边（3）暂按描边（2）报告，第 7 步合同放宽到 0～3；Worker 内部诊断映射保留 3，已唱描边槽可引用跟随来源的未唱描边路径。0.112.83 起脚本 SDK 的渐变坐标只剩 `Canvas`／`Line`／`Glyph`（删去从未支持的 `Progress`，P3 设计 D-P3-7），`Glyph` 按单字：每个字簇以自己的字框（字簇宽度 × 字体上升部到下降部）为参考框，与工程样式的按单字坐标同一绘制；新增 `GradientValue.Bands(colors, boundaries, …)` 按「颜色＋分界」构造硬边分层渐变（每层首尾各一个色标，分界须严格递增且在 (0,1) 内），与直接写出同样色标的渐变逐字节相同。HTTP 合同的 `StyleGradientCoordinateSpace` 仍含 `Progress`（写入照旧拒绝），第 7 步随合同次版本删除。0.112.84 起（P3 第 6 步，作者可见 SDK 表面收口）能力名集中为 `CapabilityNames` 常量，SDK 写入检查与宿主准入共用，Core 清单解析另持同值集合并由测试保证一致；`TextOutput`、`TextFrame`、`DeclareContext` 的构造与 `TextOutput.Complete`／`Invalidate` 改为宿主内部成员；新增泛型入口 `IPureKaraokeEffect<TPrepared>`／`IPureVisualEffect<TPrepared>`，`Evaluate`／`EvaluateVisuals` 直接收到 `Declare` 返回的准备记录，宿主仍经非泛型入口调用，纯度检查同样覆盖泛型入口，非泛型写法保留。

**两态样式合同（合同1.50.0／0.112.85，P3 第 7 步）。** HTTP 样式形状改为两态，与1.49.x客户端不兼容；`protocolVersion` 仍为 "1"，以合同版本区分。上段过渡期的单层描边换算与26项旧属性全部删除：旧路径 `unsungFill.material`、`sungFill.material`、`stroke`／`stroke.*`、`shadow`／`shadow.*`、`glow`／`glow.*` 与渐变 `coordinateSpace:progress` 一律400 invalidRequest，不再换算或静默接受。样式读写、模板属性、解析来源与文字标记共用一张43项属性路径表（OpenAPI `StylePropertyPath`，顺序即local与sources的输出顺序），值域见上表。跟随项JSON：`{"kind":"follow"}` 不带value；`{"kind":"set"}` 必须带value（strokes为数组，shadow／glow可为null表示已唱一侧关闭）；follow带value或set缺value均400。描边最多4层，HTTP写入只接受solid／gradient；读取中的package材质只会在最内层（第0层）出现，来自模板、预设或包定义。**模板：** 静态模板、完整模板与预设快照的 `properties` 改为属性路径字符串数组（不重复、≤43，未知路径400）；元素模板不能含typography、verticalCenterRatio、horizontalAlignment、scale，组合模板不能含verticalCenterRatio、horizontalAlignment；模板样式与完整模板的外观同为unsung／sung／wipe两态。完整模板与工坊的材质槽名（unsungFill／sungFill／stroke）不变，stroke指未唱最内层，已唱描边暂无工坊槽。`GET …/resolved-style` 的sources固定43项，按路径表顺序。**前奏：** 倒计时外观 `SceneTextAppearanceDto` 为typography、pending（待唱，`StyleAppearanceDto`）、elapsed（已过，`StyleSungAppearanceDto`，可跟随待唱）与opacity，四项必填；文字标记的外观覆盖与 `orchestration.setMarkAppearance` 的changes只用其中34项（不含wipe.*、verticalCenterRatio、horizontalAlignment、scale），父项与其叶同批400。**诊断：** 材质诊断source owner的slot放宽为0..3（0未唱填充、1已唱填充、2未唱描边、3已唱描边），已唱描边不再折算为2，已唱跟随时可引用未唱描边的来源路径。**例子：** examples.json中1.49.2的旧形状例子按新形状迁移，主体已删除的48例退役；1.49.2原文按序号存于 `tools/backend-api-contract/style_v2_legacy_cases.json`，校验时逐字节还原并审计历史门禁。

**历史、反馈与预算。** 全目标／属性／值先验、纯Prepare和宿主预算均先于唯一CommitPrepared；绑定workspace／generation／原Project，Writer内完成并在提交前重核close／readOnly。所有目标全有或全无；一次真实变化一次历史／修订。等值数组／渐变按内容比较，无变化保原Project、redo及LyricStylePresetId；只有工程基线真实改变才清该旧标识，PresetApplications不随之删除。读取及未选正文、时间、注音、场景、分析与音频身份保持。

成功两命令始终提供 `styleResult:{changedTargetCount:<真实改变目标数>}`，包括no-op的0；旧命令省略。变化的changeSet为areas `[styles,renderInputs]`、requiresFullRefresh:true；普通目标提供完整各类去重ID，超过16KiB则全部ID数组清空，不截断前缀。无变化changeSet为空；undo／redo沿原完整刷新 `[project]`，不返回styleResult。回执先于修订，固定整个递归payload给指纹及执行共用，同receipt重试不重复提交，真实配置完整回执预算先于历史。

新意图按canonical整个command JSON计≤64KiB，不含顶层metadata；新字体名称≤256 UTF-16 code units、fallback≤32项、gradient≤256色标，超限413 resourceTooLarge、field=command。patch的canonical changes数组UTF-8字节数×全部目标数≤16MiB，restore无新增值展开。每个真实改变目标完整新GET投影按实际下一修订计≤1MiB／20000项，改变目标投影另有累计16MiB门禁；所有预算先于提交，不当作总heap或性能保证。新输入长度不套旧数据读取，no-op不为旧大值分配完整新投影；restore可移除旧大覆盖，若剩余完整投影仍超限则整批拒绝。GET超限413、field=style，不截断；项数计canonical style／material、色标、回退项及local属性。完整回执仍受实际配置默认64KiB约束。

## 6.12 P1 输入、背景、字体与编辑候选

### 歌词导入规划

`POST /api/v1/lyrics-import-plans` 接受 `{"assetId":"asset-L","options":{...}}`。请求不接受 query；`assetId` 必须是当前 client 已授权的固定资源，`options` 可省略并沿 `LyricsImportOptionsDto` 的原默认。服务读取并释放固定资源租约后，复用原导入器的 Detect/Prepare，返回 `LyricsImportPlanResponse`：文件名与字节长度、检测到的 importer、格式候选及原因、编码结果与候选、TTML/多来源分支候选、完整 `LyricsImportReportDto` 和所选分支的预览行。它是只读信息读取，不创建批准票据、不修改工程历史；正式 `lyrics.importAsset` 仍独立分配工程身份。

解析失败仍返回可读的 `preparationError`（例如 `UnsupportedLyricsFormat`、`LyricsParseFailed`、`SourceBranchNotFound` 或 `EmptyImport`），不会用空报告冒充成功。该接口已通过本轮代表HTTP；本节记录wire与语义，证据见§23及实施§80。

### 字体目录与整形检查

`GET /api/v1/fonts` 读取实际受管理 Worker 的字体环境。可选 `family` 查询参数最多出现一次；Host 不按该参数过滤目录，以免把 Worker 环境中的缺失字体伪装成空目录。Worker 未部署或协议失败返回 `taskUnavailable` 与稳定 `FontWorkerUnavailable`/Worker 错误码。

`POST /api/v1/fonts/inspect` 不接受 query，正文严格包含 `text/family/fontSizePx/fontWeight/italic/fallbackFamilies/sessionId/expectedRevision` 八个字段；`family/fallbackFamilies/sessionId/expectedRevision` 可按 DTO 语义为 null。文本最多100000个 UTF-16 单元，族名最多256单元，回退最多32项，字号必须为有限正数，字重100..900。若提供工程 `sessionId`，必须同时提供匹配的 `expectedRevision`；检查不修改样式或工程历史。

成功响应包含 `requestedAvailable/resolvedFamily/availableWeights/supportsItalic/elements/environment/protocolVersion`。`elements` 逐元素给出 UTF-16 起点、实际字体族、是否回退和是否缺字。目录与检查均绑定同一 Worker 环境；该接口只诊断当前部署的字体环境；包内字体的固定闭包与实际渲染消费由§23的共享Runtime输入负责，字体检查不安装字体或授予任意主机路径。

### 背景命令与资源读模型

`POST /api/v1/project-sessions/{id}/commands` 新增四个严格分型：`composition.setBackgroundAsset`、`composition.clearBackground`、`composition.setBackgroundFit`、`composition.setBackgroundPlayback`。setAsset 只接受当前 client 授权 `assetId` 与 `kind:image|video`，可选 `fit:contain|cover|original`、`playback:loop|freezeLastFrame`；其余三个分别清除媒体、修改适配和修改时间映射。服务在提交前固定资源身份与完整 SHA/size，并在第二次 Writer 中复核 owner、session 对象身份、工程 revision 与关闭状态；失败释放未转移的资源 lease，成功变化只产生一次工程历史。视频的 Loop/FreezeLastFrame 沿原背景时间映射进入§23的统一Runtime输入。既有 `composition.setBackgroundColor` 仅修改底色，保留当前媒体背景；连同四个新增背景操作共五个背景相关命令。

`GET .../settings?expectedRevision=R` 的 `background` 在存在时包含 `kind/fit/playback/resourceId`；`GET .../resources` 对已保存背景返回实际受控 `assetId/contentUrl`、身份/状态和可用性。content URL 仍需当前认证与资源读取租约，不能解读为主机路径或永久公开 URL。背景媒体和历史引用经共享准备进入预览、测量、验证和导出；能力采用对应Worker实际握手，代表HTTP证据见§23及实施§80。

### 编辑候选预览与批量应用

`POST /api/v1/project-sessions/{id}/edit-previews` 接受固定 `expectedRevision`、严格 `edits[]`、`size`，可选 `alphaMode/previewId/expectedPreviewGeneration/metadata`。服务在独立候选工作区按顺序复用原命令 Prepare/Commit 解释器，返回现有 `PreviewSessionResponse`，其 `editCandidate` 包含 `baseProjectRevision/candidateRevision/changeSet/editResults`。候选帧、诊断和测量沿 preview generation 读取；候选创建、替换、关闭和丢弃不改变工程 revision、dirty、history 或 redo。

`project.applyEdits` 在同一 commands 路由提交 `edits[]`，复用候选的顺序准备和整体校验，一次请求至多一次工程历史。编辑数组禁止资源导入、外部任务、undo/redo 和嵌套批处理；中途失败整体拒绝，不提交前半批。正式提交仍核对当前工程 revision 与 session owner，过期候选不能覆盖后续工程修改。本轮代表HTTP已验证候选/同帧测量/一次应用及undo/redo；浏览器验收独立。

## 7. 目录句柄与持久化任务入口

`GET /api/v1/file-roots`返回 `{"rootGrantIds":["projects"],"protocolVersion":"1"}`，根来自可信启动配置，HTTP不能增加授权。`POST /api/v1/file-handles`正文：

```json
{"rootGrantId":"projects","relativePath":"example-song","access":"readWrite","metadata":{"requestId":"handle-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

access为read或readWrite（默认）。201返回 `{"handleId":"handle-D","rootGrantId":"projects","relativePath":"example-song","canWrite":true}`。相对目录不得为空、绝对路径、`.`／`..`、空片段、冒号或链接；可用于待创建的保存目录。默认每client128句柄；未授权／非法path400 pathNotAllowed、句柄非自己401 resourceUnauthorized、写只读句柄403，已有工程目录写者409 writeLeaseConflict。

open／recover在Server真实Worker能力开启时也使用§6.1同一受限loader和Worker-only准备；能力禁用时请求仍可先接受为原job，但终态为`failed`／`taskUnavailable`，不启动Host native fallback。已有AudioMedia时实际固定bytes必须匹配hash／size，缺AudioMedia时Host不附加此identity门禁、不主动补metadata或改历史／dirty，仍先受原Core格式／版本／闭包约束；这不是任意旧版本path-only音频包的实跑兼容承诺。AudioPath:null的合法工程仍可无音频打开；有声明但源缺失不能静默变成无音频成功。准备前后实际源校验及发布前候选／目录write lease cleanup保primary／OCE；membership后真实成功会话仍持资源。Core准确产品版本门禁保持，不能把0.70工程改版本字段冒充0.70.1兼容。

以下均采用嵌套metadata，成功202返回完整JobResponse（见§8）：

| 路由 | 除metadata外的正文样例 | 语义 |
|---|---|---|
| POST `/api/v1/project-sessions/open-jobs` | `{"projectDirectoryHandleId":"handle-D","access":"readWrite"}` | access为read／readWrite，默认readWrite；加载实际工程与闭包后才发布新会话，read只读打开 |
| POST `/api/v1/project-sessions/recover-jobs` | `{"recoveryId":"<实际候选ID>"}` | 使用服务实际恢复候选，不接受任意备份路径，不保证恢复历史 |
| POST `/api/v1/project-sessions/project-P/save-jobs` | `{"expectedRevision":"1","targetHandleId":"handle-D"}` | 捕获当前精确修订保存；已有路径可省略target，新工程必须给可写句柄；随后编辑可继续 |
| POST `/api/v1/project-sessions/project-P/close` | `{"expectedRevision":"1","policy":"requireClean","activeJobs":"reject"}` | policy为requireClean／save／discard，默认requireClean；save可带targetHandleId，未保存工程需目标；activeJobs为reject／wait／cancelAndWait，默认reject |

表中示例是字段说明，真实请求必须加入metadata。关闭默认拒dirty／活动任务；wait或cancelAndWait须等实际安全结束，不能杀死committing保存。关闭先门禁写入，保存／释放成功后才移除会话，失败保留。新建、打开、保存与恢复保持现有精确产品版本／格式门禁，不替用户迁移历史文件。

主要拒绝：修订409、只读403、dirtySession／activeJobs／writeLeaseConflict409、无效策略／缺新工程目标400、未知句柄401、工程加载／媒体／闭包失败在job终态报告。POST关闭是持久化job入口，和预览关闭直接200不同。

## 8. 当前jobs与恢复读取

当前源码job kind为 `open, save, close, recover, validation, export, analysis, author-compilation`，共用唯一注册表／账本。没有通用任意任务创建接口；validation专用结果与诊断见§10，export报告／下载见§11，独立analysis结果见§12。`GET /api/v1/jobs/job-J`200返回完整固定状态：

```json
{"jobId":"job-J","kind":"save","state":"succeeded","projectSessionId":"project-P","result":{"projectSessionId":"project-P","hostInstanceId":"host-H","projectRevision":"2","name":"示例歌曲","audioAssetId":"asset-A","lyricsLineCount":2,"audioDurationUs":3000000,"isDirty":true,"canUndo":true,"canRedo":false,"hasPath":true,"savedProjectRevision":"1","isReadOnly":false,"protocolVersion":"1"},"savedProjectRevision":"1","currentProjectRevision":"2","isDirty":true,"expiresAtUtc":"2026-10-13T11:00:00Z","cancellationRequested":false,"canCancel":false,"diagnostics":[]}
```

可选result、savedProjectRevision、currentProjectRevision、isDirty、error、diagnostics为空时省略。error使用§1.4错误DTO；diagnostics是固定BackendDiagnosticDto数组。validation另有input／progress／validationResult／failureCode可选字段，见§10；export另有exportInput／exportResult／failureReportRef，见§11；analysis另有analysisInput／analysisProgress／analysisResult，见§12，其input／progress帧字段省略。state依次可能queued／running／committing及终态succeeded／failed／cancelled／unknown；状态可快速前进，202响应不保证仍queued。没有通用percent／ETA；正式验证／导出实际进度为嵌套progress，音频分析使用analysisProgress，不混用采样与帧计数。默认每client保留128job、全局持久账本1024／8MiB、单记录32KiB、完成后保留1小时；运行任务不因期限到达移除。其他client／未知／已淘汰live job404 jobNotFound。

BackendDiagnosticDto.severity是**字符串，不是JSON enum**：既有open／save等持久化诊断可为 `Info/Warning/Error`，Worker runtime／validation／export发生使用 `info/warning/error`。全局enum的camelCase规则不会改写这些字符串；客户端须按生产来源兼容两种形式，不能推定所有诊断都小写。

`POST /api/v1/jobs/job-J/cancel`JSON正文**直接**为 `{"requestId":"cancel-1","hostInstanceId":"host-H","validUntilUtc":"..."}`；可取消时200返回当前JobResponse并cancellationRequested:true，不等于立即终态。终态返回其当前状态；canCancel:false且未终态时409 operationNotCancellable。保存或正式任务公开提交阶段不可安全回滚，必须查询终态。崩溃或终态无法持久化可为unknown，不能假称已取消或自动重放写入。

`GET /api/v1/recovery-candidates`返回 `{"candidates":[{"recoveryId":"recovery-R","projectName":"示例歌曲","savedAtUtc":"2026-10-13T10:00:00Z","hasOriginalPath":true}],"protocolVersion":"1"}`。`GET /api/v1/recovery-jobs`返回 `{"jobs":[{"job":{...},"previousHostInstanceId":"host-old","outputRootGrantId":"projects","outputRelativePath":"example-song","projectFileSha256":"<hash>"}],"protocolVersion":"1"}`。两者是当前profile的实际持久恢复清单，没有分页；不是旧client凭证的恢复或旧工程会话重新激活。当前读取要求已认证client，但清单按profile读取，不按旧client过滤。可选输出根／相对路径／hash省略，不返回服务端绝对路径。

## 9. 工程预览与固定Runtime输入

### 9.1 runtime-info与输入门禁

**0.86.1无音轨预览修复：** 合法无音轨工程可打开并创建静态预览；`CaptureAsync` 仅在存在音频身份时申请资源租约，无音轨直接固定静态输入。PNG帧、SSE状态和关闭排水沿原认证、帧租约与会话生命周期合同执行。含音频资源的完整静态预设工程从0.86.0起可进入公共预览；创建前会按 `static-lyrics-scene-presets-v1` 选择快照并要求实际 Worker 能力握手。

`GET /api/v1/runtime-info`读取配置及启动时独立来源探测的事实，本次GET不重新握手；`enabled/status`本身不证明图片能力。下例表示对应来源探测均成功（实际数组采用部署返回值）：

```json
{"enabled":true,"status":"configured-unverified","sourceProfile":"static-lyrics-scene-v1","sourceProfiles":["static-lyrics-scene-v1","static-lyrics-scene-analysis-v1","static-lyrics-scene-presets-v1","package-lyrics-scene-v1","package-lyrics-scene-presets-v1","project-image-preview-v1","runtime-project-input-v1"],"executionMode":"worker-process","maxPixelWidth":1280,"maxPixelHeight":720,"maxRawFrameBytes":4194304,"maxSnapshotBytes":4194280,"frameLeaseLifetimeSeconds":30,"maxRetainedFramesPerSession":2,"maxRetainedFrameBytesPerSession":8388608,"maxReadLeases":8,"maxReadLeaseBytes":536870912,"clientLeaseExpiryPolicy":"close-previews-preserve-projects","eventDelivery":"bounded-advisory-read-state-on-gaps"}
```

无可用Worker配置时enabled:false／status:disabled，带disabledReason错误DTO，create等能力不公布。普通工程请求使用 `source:{kind:"project",sourceId:"<工程会话ID>",sourceRevision:"<精确修订>"}`，用途为purpose:preview；不能上传裸snapshot／脚本／Worker路径。来源由Host按完整工程选择，保留歌词、多来源、注音、样式、场景、分析、包及全部预设实例；本轮Image/Video、包内字体和高级Line/Token完整闭包经§23共享准备。编辑候选、预设候选和模板固定试样沿各自公开入口创建并复用既有会话；完整作者草稿来源已接入P4；作者试样仍受独立能力与验证边界约束。

兼容单 `sourceProfile:"static-lyrics-scene-v1"` 保持；`sourceProfiles` 是实际来源能力列表，客户端不提交profile字段。原三静态来源、直接包、包与预设混合来源继续按完整声明选codec；需要固定媒体或包字体时选 `runtime-project-input-v1`。统一输入能力须由对应Worker实际验证；历史 `project-image-preview-v1` 仍可独立公布，不能据其成功推定统一输入可用。AudioPath/AudioMedia及完整已采用分析保留；available的canonical、contentHash/revision、points和sourceHash匹配检查保持，不为满足预算降成null或剥字段。

snapshot-v1总字节仍4MiB、总200,000值；分析／歌词／场景／wrapper／Envelope共享总额，不各自另给分析4MiB。JSON深层points全部计入，最多15,000点／14.4M样本，完整canonical和网格／数值门禁沿原Core。前置图检查后，Encoder实际wire再复用Decode同一重复成员／深值规则计数，包含Envelope及converter新增字段；超限明确拒绝，不截断或剥字段。

最大输出1280×720，且width×height×4≤4MiB；size必填，不是自动缩放默认尺寸。静态codec净字节≤4,194,280（已扣24字节open头）。默认总32／每client4预览、一执行＋一个最新待处理槽、每会话2帧／8MiB、帧TTL30秒、诊断页128项。底层读租约另有8条／512MiB预算，PNG全局帧存储最多8条；这些不把裸帧预算提高到FullHD。

### 9.2 创建与换输入

`POST /api/v1/preview-sessions`：

```json
{"source":{"kind":"project","sourceId":"project-P","sourceRevision":"1"},"size":{"width":320,"height":180},"alphaMode":"straight","purpose":"preview","metadata":{"requestId":"preview-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

alphaMode默认opaque，允许opaque／straight；purpose默认preview，其他422 commandNotSupported。201直接返回PreviewSessionResponse：

```json
{"previewId":"preview-V","source":{"kind":"project","sourceId":"project-P","sourceRevision":"1"},"previewGeneration":"1","size":{"width":320,"height":180},"alphaMode":"straight","state":"idle","limits":{"maxWidth":1280,"maxHeight":720,"maxInFlightRequests":1,"maxRetainedFrames":2,"maxRetainedFrameBytes":8388608,"frameLeaseLifetimeSeconds":30,"maxDiagnosticsPageSize":128,"maxRawFrameBytes":4194304},"protocolVersion":"1"}
```

`PUT /api/v1/preview-sessions/preview-V/input`正文为 `expectedPreviewGeneration,expectedSourceRevision,source,size,alphaMode?,metadata`，例如把上例增加 `"expectedPreviewGeneration":"1","expectedSourceRevision":"1"` 并移除purpose、把sourceRevision改为新的实际修订。200直接返回同previewId的新generation会话，旧帧／诊断失效；即使工程已继续编辑，只有明确捕获的新精确sourceRevision被采用。旧输入基线409 previewGenerationConflict，来源工程旧修订409 revisionConflict、缺工程404、缺Worker503 previewUnavailable。

### 9.3 帧请求、状态与关闭

`POST /api/v1/preview-sessions/preview-V/frame-requests`：

```json
{"expectedSourceRevision":"1","previewGeneration":"1","requestSeq":"1","timeUs":"1500000","intent":"playback","metadata":{"requestId":"frame-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

202返回 `{"previewId":"preview-V","previewGeneration":"1","requestSeq":"1","timeUs":"1500000","state":"accepted","protocolVersion":"1"}`，不保证PNG已经完成。intent仅playback／seek；同代次requestSeq严格递增，seek提交时也须大于旧代次已接受序号。playback时间不后退，同代次连续前向帧复用Worker／Runtime；HTTP seek生成新generation；0.110.6 起，同一固定输入、输出规格与后端可继续使用原Worker。已验证的协作取消回执会先被完整消费，再在原Worker内用同一固定产物重开Runtime；未发出的请求取消不重开。硬超时、作者错误、IPC或设备失败仍使会话失效并排水，真实输入／规格／后端改变仍建立新Worker。客户端必须采用seek响应返回的新generation，旧代结果不能发布。v1回退仍可能从零重放时间线，不承诺任意时刻纯求值。旧代次／来源409、非递增或playback后退409 previewRequestOutOfOrder，非法时间／参数或超过固定工程时长400。

`GET /api/v1/preview-sessions/preview-V`200返回 `{"session":<PreviewSessionResponse>,"failure":<可选错误>,"renderFailureCode":<可选稳定编号>}`。session.state可能idle／queued／rendering／ready／failed／closing／closed，ready包含latestRequestSeq／latestFrame：

```json
{"frameId":"frame-F","previewId":"preview-V","previewGeneration":"1","sourceRevision":"1","requestSeq":"1","timeUs":"1500000","width":320,"height":180,"alphaMode":"straight","content":{"handleId":"handle-F","contentType":"image/png","sizeBytes":1200,"sha256":"<64位小写hex>","expiresAtUtc":"2026-10-13T10:00:30Z","contentUrl":"/api/v1/preview-frames/frame-F/content"}}
```

失败不发布占位PNG；例如Worker被终止可在GET状态／SSE出现 `renderFailureCode:"WorkerSessionConflict"`，首次执行体丢失为WorkerUnavailable。failure公开消息脱敏，程序按编号分支；没有实际Runtime报告时使用transport诊断。已固定预览不随工程编辑自动变更，工程关闭后仍可持其独立资源引用。

`POST /api/v1/preview-sessions/preview-V/close`正文 `{"expectedPreviewGeneration":"1","expectedSourceRevision":"1","metadata":{...}}`，200返回session.state:closed的状态包装，等待排水／资源释放；随后GET404，已保存回执重试仍返回原关闭结果。跨client会话404；客户端休眠清理预览，工程保留。

### 9.4 PNG与诊断页

`GET/HEAD /api/v1/preview-frames/frame-F/content`规则同§5不可变二进制，并有 `X-Mako-Preview-Expires-At` ISO日期头，对允许Origin暴露。PNG为straight alpha，内部BGRA为premultiplied；帧绑定generation／sourceRevision／requestSeq／time，不能将不同代次拼接。到期410 previewFrameExpired正常可恢复，淘汰／换代／关闭后可404；跨client401 resourceUnauthorized。使用返回的contentUrl，路由id是frameId，不是previewId或独立content.handleId。

`GET /api/v1/preview-sessions/preview-V/diagnostics?cursor=<opaque>&limit=128`返回固定代次页：

```json
{"previewId":"preview-V","previewGeneration":"1","sourceRevision":"1","diagnostics":[],"hasMore":false,"protocolVersion":"1"}
```

缺cursor读首张已发布页；请求超过128、非正数或小于实际固定页长度400 diagnosticLimitExceeded，不进行任意截断；无效／旧游标409 diagnosticCursorExpired，未发布页也不得推定已有空报告。hasMore:true时按nextCursor继续，不混合旧代次。

BackendDiagnosticDto字段为 `code,severity,message,location?`，发生诊断增加originKind／occurrence。实际Runtime发生保留 `originKind:"runtime"` 与occurrence：`epoch` GUID、`sequence`字符串、instanceId、artifactHash、stage、timeUs字符串、可选逻辑location `{path,line,column}`、可选package `{packageId,revision,exportId}`。非Runtime失败仅 `originKind:"transportFailure"`，没有occurrence；静态成功诊断通常为空，不代表脚本真实发生已验收。Worker完整诊断上限256项／64KiB，超限失败而不截断。

## 10. 固定工程正式验证任务

**P2统一Runtime输入（本计划代表HTTP已通过）：** 原validation-jobs使用§23的固定工程/资源，匹配Worker通过独立 `runtime-validation-capabilities-v1` 的同进程能力核对后才执行。Color无外置资源保持实际基础sourceProfile，Image/Video或包字体使用 `runtime-project-input-v1`；包与预设混合可使用 `package-lyrics-scene-presets-v1`。共享ProjectRuntimeInput调用原RuntimeFrameValidator，报告字节、诊断分页、取消、预算及恢复读取沿原链路；正式验证没有PNG产物，报告成功不等于像素验收。

> **历史B4-P1 package profile（合同 1.30.1，有限真实HTTP已收口）**：validation 任务可在实际 Worker 能力握手成功时选择 `package-lyrics-scene-v1`。该 profile 复用本节的任务生命周期、精确 revision、scheduler、取消/deadline、报告原始字节、诊断分页和 owner/recovery ACL；输入快照由 Hosting 从当前工程固定并使用 package codec，不接受客户端裸 snapshot、路径或脚本。包 profile 只允许无预设、Color 背景且无外部媒体/字体闭包的直接效果包；`compileSource`、`imageEffect`、`material` 与 `runtime` 是互斥诊断 payload，compile/runtime Error 使对应输出失败并禁止发布新帧，image/material warning 可保留成功帧。包 codec 4 MiB／200000 source values／深度门禁、验证报告 256 KiB／256 issues／512 occurrences 和既有帧/画布上限继续适用；超限不截断、不伪造成功。当前有限证据见[实施§66](../backend-extraction-progress.md#b4-p1-package-validation-warning-recovery-0981)，已覆盖合法 image/material warning 与成功帧共存及一次 previous-host restart/recovery；完整多代恢复和完整B3/B4仍未完成。上述是该历史profile的范围；本轮跨任务/媒体/字体执行见§23，作者写入仍沿独立作者接口。详见 [B4-P1计划](../backend-package-validation-plan.md)。

### 10.1 能力、固定来源与帧计划

`GET /api/v1/validation-capabilities`返回实际配置，例如默认：

```json
{"enabled":true,"sourceProfile":"static-lyrics-scene-v1","sourceProfiles":["static-lyrics-scene-v1","static-lyrics-scene-analysis-v1"],"maxFramesPerOutput":600,"maxCanvasWidth":1920,"maxCanvasHeight":1080,"maxRawFrameBytes":8388608,"operationTimeoutSeconds":60,"maxReportBytes":262144,"maxIssues":256,"maxOccurrences":512,"maxRunning":2,"maxRunningPerClient":1,"maxRunningPerProject":1,"maxQueued":16,"maxQueuedPerClient":4,"maxReportStorageBytes":16777216,"maxDiagnosticsPageSize":128}
```

未部署可用Worker时enabled:false并有disabledReason，创建503 taskUnavailable；已有报告读取不隐式启动Worker。operationTimeoutSeconds是实际配置值，默认60，可由宿主配置为1～60秒，不能把60写成所有部署的固定值。旧sourceProfile字段保留；sourceProfiles按实际验证Worker能力公布基础、包/预设及统一Runtime来源，不能从enabled推断未列出的来源或任意媒体已通过。

只接受当前client拥有工程的精确expectedRevision及每输出1～600帧。支持对应Worker公布的§9/§23来源；unsupported非空字段明确拒绝。音频声明及完整已采用分析保持，Hosting持已授权资源引用；验证本身不创建音轨输出。每个任务拥有独立Worker，普通／透明各一个独立原Runtime会话，Final质量。固定帧时刻为 `floor(index × 1_000_000 × frameRateDenominator / frameRateNumerator)`，从0开始；报告**只证明该固定来源和计划**，不表示整首歌曲、导出成片或未采样时刻均通过。

工程必须有**显式正Composition.OutputDurationUs**，计划最后时刻不得超过它；画布≤1920×1080且本地裸帧≤8MiB。这里在Worker内验证，不传pixels，不扩大HTTP preview的1280×720／4MiB合同。任务封包净snapshot最多4,194,272字节（4MiB扣32字节任务头），区别于preview open的24字节头。

新建HTTP省略composition.outputDurationUs时可合法保持null，正式验证会400拒绝；可以通过既有 `composition.setOutputDuration` 显式设置正时长；不能假设所有刚新建工程立即可验证。

### 10.2 创建、状态与进度

`POST /api/v1/project-sessions/project-P/validation-jobs`使用嵌套metadata：

```json
{"expectedRevision":"1","framesPerOutput":3,"metadata":{"requestId":"validate-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

202返回原共享JobResponse，kind为validation；同requestId重试返回原jobId。Writer内完成修订／计划／profile门禁、快照与资源捕获及任务登记。随后编辑不改变任务输入；HTTP断连、request deadline过去或client休眠不取消已接受任务。只读工程可执行不修改内容的验证。

当前job输入／进度字段示例，采用GET job或client state／SSE的实际身份：

```json
{"jobId":"job-J","kind":"validation","state":"running","projectSessionId":"project-P","expiresAtUtc":"2026-10-13T11:00:00Z","cancellationRequested":false,"canCancel":true,"input":{"sourceKind":"project","sourceId":"project-P","sourceRevision":"1","sourceProfile":"static-lyrics-scene-v1","sourceSha256":"<固定snapshot的64位小写SHA>","framesPerOutput":3,"canvasWidth":320,"canvasHeight":180,"frameRateNumerator":30,"frameRateDenominator":1,"outputDurationUs":"3000000"},"progress":{"stage":"validating","completedFrames":3,"totalFrames":6,"timeUs":"66666","transparent":false}}
```

input不含裸snapshot或本机路径；input.sourceRevision不是后来编辑后的当前修订。progress来自真实完成帧数，stage可能validating／disposing／committing／completed／failed／cancelled，可有timeUs／transparent；不是计时器百分比，也没有ETA。job.progress事件只携小摘要，不携完整报告。

执行许可为全局2／每client1／每project1，独立排队全局16／每client4；被同client／project阻塞的队列不占其他client的全局运行许可。超限429 requestQuotaExceeded；原job保留配额仍适用。旧修订409、缺来源404、unsupported profile422、非法／缺显式时长／超末时刻／画布／计划400、快照超字节413。

显式 `POST /api/v1/jobs/{id}/cancel` 沿§8回执，最终cancelled须等待实际释放；Worker deadline是failed并保留 `failureCode:"WorkerTaskTimedOut"`。默认取消宽限2秒，超期回收拥有的Worker进程树。project close的reject／wait／cancelAndWait沿原生命周期；服务关闭主动取消可取消正式任务并等待排水，不能撤回已有保存committing。

### 10.3 完整结果与实际发生分页

Worker和Runtime释放完成后，Hosting释放输入资源、做最后取消检查，再进入不可撤回的committing原子发布报告sidecar与job终态。真实验证未通过的完整报告也可发布，job为failed；预算／执行／发布错误不伪造报告。终态示例的附加字段：

```json
{"validationResult":{"passed":true,"opaqueFrames":3,"transparentFrames":3,"report":{"reportId":"report-job-J","sizeBytes":86,"sha256":"<实际完整报告SHA>"}},"progress":{"stage":"completed","completedFrames":6,"totalFrames":6,"timeUs":"66666","transparent":true}}
```

该片段为字段示例，实际job还包含身份／输入／保留字段；大小和hash采用实际返回值。job.validationResult只保留有界摘要／引用，不保留完整报告数组。failureCode是稳定内部编号，和error.code的公开分类分别使用；验证未通过（包括保留完整 compileSource/imageEffect/material 诊断的失败）为validationFailed／422分类；Worker deadline为taskExecutionFailed／500分类并保留failureCode:`WorkerTaskTimedOut`；执行或报告完整性失败为taskExecutionFailed／500分类。正常GET job本身仍200并在正文表示failed。

`GET /api/v1/jobs/job-J/validation-result`要求当前live owner，200返回**登记的原始JSON字节**、`Content-Type: application/json`，长度和SHA与report引用一致，无协议包装、路径、PNG、Range或ETag合同。成功完整报告：

```json
{"succeeded":true,"opaqueFrames":3,"transparentFrames":3,"issues":[],"diagnostics":[]}
```

issues每项为 `transparent,timeUs,code,message,isError,location?,sampleFailureCode?`；diagnostics每项为 `transparent,diagnostic`，diagnostic使用§9.4的实际发生身份。原始报告可能保留null字段。汇总issues可以去重，实际occurrence不能因同文而丢失；静态成功通常实际空诊断，不算脚本诊断验收。完整报告≤256KiB／256issues／512occurrences，超限明确失败、不截断；全局sidecar≤16MiB，存储满映射resourceQuotaExceeded。

尚无登记完整报告、非validation job、其他client或已过保留期404 jobNotFound；报告缺失／链接／大小／SHA／固定profile／帧计划／摘要不一致500 taskExecutionFailed，不重建成成功报告。

`GET /api/v1/jobs/job-J/validation-diagnostics?cursor=<opaque>&limit=128`也严格live owner。默认及最大128，实际按完整报告发生数组分页（不同于§9的预发布固定页）：

```json
{"jobId":"job-J","reportSha256":"<登记报告SHA>","diagnostics":[]}
```

nextCursor存在才继续，cursor与job／报告SHA绑定；没有hasMore字段。limit非整数／重复query400 invalidRequest，≤0或超上限400 diagnosticLimitExceeded；外来／超界游标409 diagnosticCursorExpired。先完成报告授权／完整性读取，没有报告不能用分页制造空诊断。

### 10.4 重启读取与保留

live `GET job`／validation-result／validation-diagnostics严格按当前owner，服务重启后不能用旧clientId／resumeToken恢复旧身份，也不会将旧job放进当前live集合。**`GET /api/v1/recovery-jobs/{oldJobId}/validation-result` 是单独的恢复读取**：只读previousHost且唯一ledger已登记的validation报告引用，沿既有B2本机profile-wide恢复权限；当前持bootstrap登记的profile client可读，GET仍用其accessToken。这不是旧owner认证恢复，也不回退live owner ACL或公开孤立sidecar。

恢复时同样复核report身份／size／SHA、sourceProfile（静态profile及经握手公布的`package-lyrics-scene-v1`，未知profile拒绝）、固定帧率／时长／计划及结果摘要，200为同一登记原始报告。未知／未登记报告404，畸形或篡改500 taskExecutionFailed；unknown不自动升succeeded、不自动重跑任务。

授权保留沿job默认1小时，到期读取404；报告文件在启动和接受新验证任务时物理清理，不保证每秒立即删除。单ledger记录≤32KiB，只保存报告引用和固定输入摘要；完整256KiB报告独立sidecar。账本终态持久化失败可为unknown，已发布报告不反向回滚。任务资源直到实际Worker排水才释放；正式验证没有视频artifact或分析结果应用工程能力。


<a id="project-image-validation-api"></a>

### 10.1 历史：已保存工程图片正式验证有限来源（0.100.0／合同1.34.0，2026-10-24有限HTTP验收）

本节仅描述0.100独立图片profile及当时证据；本轮媒体/预设/字体共享输入见§23，不沿用本节作为全局准入限制。

既有 `GET /api/v1/validation-capabilities`、`POST /api/v1/project-sessions/{id}/validation-jobs` 与报告/诊断读取复用既有job及owner/recovery生命周期。客户端仍只提交工程ID、精确revision、帧计划和metadata，不提交图片bytes、路径、profile、grant或裸snapshot；Host只捕获ProjectStore已加载Image闭包。基础工程限无预设static、static-analysis或直接package。

source profile为 `project-image-validation-v1`；私有capability kind为 `project-image-validation-capabilities-v1`，task kind为 `task-project-image-validation-v1`／二进制magic `KTI1`。图片envelope codec仍为 `project-image-preview-v1`，实际decoder profile为 `memory-image-bgra-v1`，两者不能混称。Worker能力核engine、恰三个基础profile、完整refs/package capability、diagnostics版本和全部预算，实际任务Worker重新复核；预览能力不授予正式验证能力，不回退原静态任务或Host native。

完整图片envelope作为任务输入至多4,194,272字节（4MiB−32字节任务头），包含base64与完整基础源；嵌套base snapshot保留4MiB及200000 source values门禁，原图2MiB／单边4096／BGRA16MiB。图片预览的4,194,280字节上限不能用于此任务。报告仍沿原256KiB／256 issues／512 occurrences及实际配置门禁。

Host第一次在Writer核owner/session对象身份/revision/ClosePending，捕获snapshot及图片/音频引用；Writer外读取并核完整size/SHA、无链接及封包预算。第二次进入Writer必须复核当前owner、注册session仍为同一对象、revision和ClosePending，之后才reserve并登记job。读取/编码/预算/复核/配额/任务登记任一失败都释放未转移图片lease、音频reference与reservation，成功接受后由work等待Worker/native排水再释放。

Worker每Runtime session建立独立内存resolver并复用原RuntimeFrameValidator；图片解码/预算失败保留 `PackageImageDecodeFailed`／`PackageImageBudgetExceeded`，报告完整性、issues/diagnostics、帧计划、progress、取消/deadline/close按原链路处理。能力列表仅在独立握手成功后追加本profile，旧兼容`sourceProfile`保持原静态值；真实0.99预览Worker拒绝503 `taskUnavailable`／`WorkerImageValidationUnavailable`，field为worker。

匹配self-contained Server/Worker发布`dist-7a03863`（源码`7a03863`、`sourceDirty=false`）的fresh HTTP `http-final1`通过1412请求／10582断言；两次Server exit0，33个观察身份（含26个Worker）全部退出、无强制清理，3349个文件凭证扫描通过。三基础源普通／透明各4帧、完整报告bytes/size/SHA与诊断分页、ACL/revision/SSE、错误与输入/报告预算、固定接受bytes、queued/running控制、Initialize/Evaluate×cancel/deadline/close六组真实Worker/child及一次orderly restart/recovery均在该有限范围内。旧Worker负例10请求／476断言、199文件另计。正式报告无PNG，独立原Runtime报告Oracle不构成像素验收；详见[独立计划](../backend-project-image-validation-plan.md)与[实施§78](../backend-extraction-progress.md#project-image-validation-01000)。该历史图片验证证据不覆盖export/measurement或preset/video/font；本轮对应实现见§23，浏览器及其他原矩阵不由此历史证据完成。

## 11. 固定工程导出与成片读取

### 11.1 能力、来源与计划

`GET /api/v1/export-capabilities`始终映射并要求client认证。以下为显式配置有限资源策略的响应示例，默认 Server 未配置的策略字段为 null 并可省略；enabled及formats来自真实Worker有界探测，不能照抄示例假定工具或guard可用：

```json
{"enabled":true,"sourceProfile":"static-lyrics-scene-v1","sourceProfiles":["static-lyrics-scene-v1","static-lyrics-scene-analysis-v1","static-lyrics-scene-presets-v1","package-lyrics-scene-v1","package-lyrics-scene-presets-v1","runtime-project-input-v1"],"formats":["opaqueMp4","transparentProRes4444"],"audioProfiles":["pcm16-wave-self-contained","mp3-layer3-self-contained","native-flac-self-contained","aac-adts-self-contained","mov-audio-self-contained","ogg-audio-self-contained"],"quality":"standard","qualities":["draft","standard","high"],"renderQuality":"final","maxFrames":600,"maxDurationUs":"10000000","maxFrameRate":60,"maxCanvasWidth":1920,"maxCanvasHeight":1080,"maxRawFrameBytes":8388608,"operationTimeoutSeconds":120,"cancellationGraceSeconds":2,"maxMediaBytes":67108864,"maxReportBytes":262144,"maxOccurrences":512,"maxArtifactStorageBytes":536870912,"maxRunning":2,"maxRunningPerClient":1,"maxRunningPerProject":1,"maxQueued":16,"maxQueuedPerClient":4,"outputFileGuard":"linux-x64-rlimit-fsize","maxDiagnosticsPageSize":128,"executionProfile":"static-export-pcmwav-v1","executionProfiles":["static-export-pcmwav-v1","static-export-compressed-audio-v1"],"maxCompressedAudioDurationUs":"300000000","compressedAudioDurationBasis":"strict-source-rate-decoded-sample-frames"}
```

单job仅选择普通H.264／MP4或透明ProRes 4444／MOV；请求 `quality` 为 `draft`、`standard` 或 `high`，省略为 `standard`，报告回显 requested/actual quality。工具参数、输出路径或目录覆盖字段仍不属于HTTP请求。质量档位映射原Exporter的编码质量预设，Runtime渲染仍为Final，画布/fps不随档位改变。未部署可用Worker／工具／文件guard时enabled:false且有disabledReason，创建503 taskUnavailable；支持一种格式不代表另一种已可用。当前私有export probe须匹配EngineIdentity、六个来源（三静态、直接包、包与预设混合、统一Runtime）、六个AudioProfiles及两个ExecutionProfiles；缺失、重复、未知或错配能力以WorkerExportProfileMismatch禁用。公开singular executionProfile仍为static-export-pcmwav-v1；executionProfiles声明旧WAV／纯视频与新增压缩音轨两项。disabled export的sourceProfiles／audioProfiles／executionProfiles均为空数组，兼容singular字符串保留。Linux-x64仅在配置媒体文件大小限制时使用单文件 `RLIMIT_FSIZE`；未配置时能力声明为 `linux-process-group`。它们不表示跨平台、全目录、内存或整个磁盘硬限额。

当前导出复用§23共享准备，支持三静态来源、直接包、包与预设混合、Image/Video及包字体；带音轨时继续核授权源和实际audio profile，不剥字段运行。原ProjectValidator的Export上下文仍拒绝阻断性领域问题，正式验证报告成功不替代导出门禁；只读工程可执行不修改内容的导出。历史 `4e0f909` 匹配发布的457请求／2575断言、普通／透明各21帧仅属于无音轨直接包导出，详见 [实施§76](../backend-extraction-progress.md#b4-p2-package-no-audio-export-http-0981)；该批ffmpeg检查不是Runtime内容oracle，不能倒记为本轮带音轨/统一输入验收。

Composition须有显式正时长；帧率、画布、单裸帧、总帧数和总时长受 `WorkerExportResourcePolicy` 的部署值约束。默认 Server 的导出帧数、时长、帧率、画布、裸帧、媒体、音频和压缩音频策略均为null，表示该维度不施加部署限制；宿主可显式配置正值并通过 capabilities 公布；普通MP4宽高必须偶数。总帧数沿原Exporter规则：`max(1, Round(outputDurationUs / 1_000_000 × fps, AwayFromZero))`；第index帧时刻为`floor(index × 1_000_000 × denominator / numerator)`。派生视频时长`floor(totalFrames × 1_000_000 × denominator / numerator)`也须正，并受宿主 `MaxDurationUs`（若有）约束，不等同于未量化的Composition时长。pixels仅在Worker本地渲染／编码，不扩大预览IPC上限。

配置媒体字节预算时，Linux 编码器在文件达到预算且被 `SIGXFSZ` 终止后返回 `WorkerExportArtifactBudgetExceeded`，包括写帧阶段提前断管与编码收尾阶段；不发布残片。恰好达到预算但正常退出，或其他退出码的普通编码失败，不仅凭长度判为超限。Windows 保持既有实际长度监控及收尾校验。

`includeAudio`默认true：工程没有音频声明且没有受管音频时，按纯视频导出，`exportInput.includeAudio`及报告`audioIncluded`固定为false，音频引用与音轨字段为空。声明了音频但授权缺失、文件损坏或无法解码仍拒绝，不作为无音频处理。有音频时必须是已授权ResourceStore中自包含的受支持音轨；具体 codec、采样率和字节上限由能力响应与 `WorkerExportResourcePolicy.MaxAudioBytes` 公布，null 表示不施加该宿主字节限额。当前执行器按profile选择PCM16 WAV、MP3 LayerIII、native FLAC、AAC ADTS、MOV/M4A音轨或Ogg音轨，mono/stereo、codec与采样率门禁由真实Worker校验。Host直接pin授权资源，不复制音频；复用固定bytes分类、actual hash／size、canonical fullpath／祖先no-link与read lease出口。WAV保原精确采样帧admission和旧执行profile；MP3/FLAC先按非free LayerIII／有界ID3或native FLAC STREAMINFO固定候选，不凭MIME／后缀或probe估时长授予完整解码能力。AAC/MOV/Ogg由中央profile映射选择受限demuxer，实际codec核对与完整严格解码后才授予音轨执行能力，不凭MIME或后缀接受任意容器变体。

压缩Worker按中央AudioProfile映射强制mp3／flac／aac／mov／ogg demuxer及file,pipe协议，实际probe核codec／声道／源采样率；完整strict decode至EOF且进程实际exit0，再按**源采样率的每声道sample frames**计数。64KiB流式读取不保存整曲PCM；帧数受宿主 `MaxCompressedAudioDurationUs`（若有）约束。能力响应中的 `maxCompressedAudioDurationUs` 可省略；`compressedAudioDurationBasis` 仍为 `strict-source-rate-decoded-sample-frames`。该预算仅压缩export，不套旧WAV或通用prepare，也不借用48k分析采样数。

0.114.1／合同1.55.1起不再要求源音频覆盖派生视频时长，视频帧计划保持不变。完整源decode及前后actual identity核验后，短音轨尾部补静音，长音轨截断，最终均按完整帧计划的有理时长输出；不使用`-shortest`缩短画面。报告`audioDurationUs`保留源真实解码时长`floor(sampleFrames × 1_000_000 / sampleRate)`，`actualAudioDurationUs`才是归一化后产物音轨时长，并继续要求与派生视频时长差不超过50ms；不把源时长改写为目标时长，也不采用probe／header／summary估值或把未知源时长记零。普通MP4固定转码AAC，透明MOV固定PCM16；受限输入使用`-xerror`／`err_detect explode`，编码以`apad`补静音和计划`-t`封顶，保留末帧。损坏、不可解码、授权／身份错误、预算或取消不发布partial媒体／虚构Runtime报告。完整源decode与导出共用部署声明的任务deadline，默认null不施加该时限。该补静音策略只作用于音轨编码，不生成或延长分析；效果继续按原有`NoAudio`／`NotAnalyzed`／`OutsideRange`缺失语义处理，显式必需分析输入的原阻断诊断保留。

`includeAudio:false`为显式纯视频：完整snapshot／已接受分析／AudioPath／AudioMedia声明保持，仅以IncludeAudio=false禁止源音频Probe与编码器音轨IO，不剥分析、不伪造无音频状态，不授予或读取快照描述路径的文件能力。

`input.sourceSha256`是**原接受的完整固定source codec字节SHA**，在音频访问路径重绑定之前捕获；`exportInput.audio`另记授权assetId／sizeBytes／sha256。AudioPath只是源描述，不授任意文件IO；includeAudio:true由可信grant重绑定实际访问路径，不要求声明路径字符串等于访问路径，保留原AudioMedia身份／显示名及完整分析。HTTP不能上传snapshot、访问grant、工具路径或编码参数。

### 11.2 创建、状态与两层提交

`POST /api/v1/project-sessions/project-P/export-jobs`正文示例：

```json
{"expectedRevision":"2","format":"opaqueMp4","includeAudio":true,"metadata":{"requestId":"export-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

0.69导出input总额仍4MiB，按实际metadata＋32字节头扣除snapshot预算；executor EncodeInput在Worker spawn／send前复核。可在registration前计算的超限返回413 resourceTooLarge且清私有candidate；实际metadata尚未知时不重造jobId，202后也可能按WorkerSnapshotBudgetExceeded稳定失败。不能据此声称所有未来封包超限都在admission之前拒绝；不截断分析来满足预算。

format省略为opaqueMp4，includeAudio省略为true。DTO拒绝未知成员；metadata沿§1.2唯一client级回执，重试返回原job。接受在工程writer内完成精确修订／Export领域／计划／profile／音频授权门禁、原snapshotSHA及资源／容量／执行占位，202返回共享JobResponse，kind为export。只代表接受，须读job终态。

固定输入示例片段如下；source中的framesPerOutput在export中表示这一种输出的完整帧数：

```json
{"input":{"sourceKind":"project","sourceId":"project-P","sourceRevision":"2","sourceProfile":"static-lyrics-scene-v1","sourceSha256":"<原固定codec SHA>","framesPerOutput":90,"canvasWidth":320,"canvasHeight":180,"frameRateNumerator":30,"frameRateDenominator":1,"outputDurationUs":"3000000"},"exportInput":{"format":"opaqueMp4","includeAudio":true,"audio":{"assetId":"asset-A","sizeBytes":264644,"sha256":"<固定音频SHA>"}},"progress":{"stage":"rendering","completedFrames":30,"totalFrames":90}}
```

压缩任务的exportInput尾部显式记录`executionProfile:"static-export-compressed-audio-v1"`，audio尾部显式记录`audioProfile:"mp3-layer3-self-contained"`或`"native-flac-self-contained"`。旧WAV／纯视频继续省略两个nullable新增字段，null只解释legacy WAV；客户端不能自行指定执行profile或音频授权。

validation／export／analysis共用同一scheduler：running全局2／每client1／每project1、queued全局16／每client4，持久化控制job不占这些slot。媒体预留和产物存储按部署的 `MaxMediaBytes`/`MaxArtifactStorageBytes` 计算；默认两者为null，不施加原64MiB单产物/512MiB累计部署限制。显式配置存储限额时，queued/running预留、retained及到期仍active-read的实际占用均计入；字节预算可能先于queue名额耗尽。完整报告另限256KiB／512实际occurrences，超限失败，不截断伪完整。

progress来自原Exporter实际阶段与帧数：validating／probing／rendering／muxing／verifying；Worker的私有candidate提交映为preparing-result，准备完成映为prepared，均仍可public cancel。随后Worker、Runtime及FFmpeg／ffprobe实际排水，Host核对完整报告、固定计划、媒体size／hash并写完整报告。最后取消检查→唯一job进入committing／canCancel:false→同文件系统一次Directory.Move公开媒体＋report包→唯一ledger终态。没有第二份job或ledger；私有candidate不提供下载引用。

显式cancel沿§8：提交前取消须等实际排水；公开committing不能撤回，迟到cancel不改成功。任务deadline默认null，可信host可配正整数秒并由能力响应公布，取消宽限2秒；超时是failed，不能冒充用户取消。HTTP断流／client休眠／接受后编辑或undo不改变固定来源或取消job；工程关闭沿activeJobs政策，shutdown取消可取消正式任务并等排水。ledger终态写入失败可为unknown，已发生的公开提交不回滚或自动重跑。

主要创建拒绝：修订409、unknown／其他client工程404；Export领域／几何／时长／计划422 exportInputRejected；音频能力／授权身份／不足422 exportAudioUnsupported；unsupported格式或静态字段422 commandNotSupported；字节占位429 resourceQuotaExceeded、执行queue429 requestQuotaExceeded；部署或该格式不可用503 taskUnavailable。

### 11.3 完整报告、验证标记与诊断

成功job包含`exportResult:{artifact:{artifactId,contentType,sizeBytes,sha256},report:{reportId,sizeBytes,sha256}}`。Content-Type分别为video/mp4或video/quicktime。`GET /api/v1/jobs/job-J/export-result`严格live owner，200返回登记原始完整ExportReportDto JSON，可能保留null；示例说明字段，不作为实际产物：

```json
{"format":"opaqueMp4","audioIncluded":true,"expectedFrames":90,"decodedFrames":90,"framesWritten":90,"droppedFrames":0,"failedFrames":0,"width":320,"height":180,"frameRateNumerator":30,"frameRateDenominator":1,"derivedVideoDurationUs":"3000000","actualVideoDurationUs":"3000000","audioDurationUs":"3000000","videoCodec":"h264","audioCodec":"aac","pixelFormat":"yuv420p","hasAlpha":false,"mediaSize":123456,"sha256":"<登记媒体SHA>","warnings":[],"diagnostics":[],"isVerified":true,"actualAudioDurationUs":"3000000","strictlyVerified":true}
```

expectedFrames／decodedFrames／framesWritten必须精确等于计划，droppedFrames／failedFrames为0；实际尺寸、有理帧率和codec匹配。普通输出为h264／yuv420p，透明为prores／yuva444系列且HasAlpha为true。实际视频时长与派生时长容差200ms；带音频实际codec为普通aac或透明pcm_s16le，正音轨时长与派生视频时长容差50ms。`audioDurationUs`是**源**音频实际时长，`actualAudioDurationUs`是**产物**音轨时长；纯视频两字段及audioCodec为null。音轨容差不是逐样本无损保证，源实际sample frames覆盖时长门禁没有放宽，新增压缩源报告时长还须符合能力响应公布的 `MaxCompressedAudioDurationUs`；null 时不施加该宿主维度限额。

`isVerified`保留原Exporter针对Composition nominal时长的验证值；`strictlyVerified`是新服务针对派生帧计划的严格标记。低fps的至少一帧规则可导致isVerified:false及原警告，但派生计划／实际媒体全部匹配时strictlyVerified:true仍允许成功；不能把旧值改成true。发布必须strictlyVerified:true并通过上述实际门禁，不能仅凭旧IsVerified、编码退出0或报告非空判定成功。

**兜底诊断（0.112.86／合同1.51.0，P3 第 7 步 B）。** 导出不再因图效果或材质缺背景而失败，改为与预览同样兜底：透明输出（`transparentProRes4444`）没有背景，声明必需 `BackgroundMedia` 输入的图效果旁路为原样、材质退回包内兜底外观，code 为 `TransparentBackgroundDependency`；不透明输出没有可用背景媒体（未设置、文件丢失或该时刻无帧）时 code 为 `BackgroundMediaUnavailable`，可选输入照旧以工程底色代替且没有诊断。新登记的完整报告总含三项（0.112.86 之前登记的报告按原字节返回、没有这三项，读取照旧成功，按空数组、0 与 false 理解）：`fallbackDiagnostics` 为去重后的兜底诊断数组，按首次出现排序，最多128项、省略null的公共JSON不超过96KiB，每项 `originKind` 为 imageEffect 或 material、severity 为 warning、没有 location／occurrence，payload 与 §20.3 相同（登记原样报告中空槽位显式为null）；`fallbackFrameCount` 为至少有一条兜底诊断的帧数，不超过 expectedFrames；`fallbackDiagnosticsTruncated` 表示是否因条数、字节或完整报告256KiB预算丢掉了部分去重诊断。帧数为0时数组为空且未截断；帧数大于0时至少一条诊断或已截断。兜底诊断不写入 `warnings` 文字，也不进入 `diagnostics`（后者仍只放 Runtime 实际发生），`export-diagnostics` 分页不读取它们。成功 job 的 `exportResult` 另含 `fallbackDiagnosticCount`（0..128），等于报告中 `fallbackDiagnostics` 的条数；完整列表、帧数与截断标记读报告。没有这类依赖的工程三项为空／0／false，像素与产物字节不变。静态渲染会话照旧遇到任何图／材质诊断即失败（`WorkerDiagnosticsUnsupported`，该路径不承载包、源材质与背景媒体）。

失败若带真实非空RuntimeDiagnosticSnapshot，job尾部可有`failureReportRef:{reportId,sizeBytes,sha256}`，没有exportResult／ArtifactRef或媒体内容。同一export-result端点200返回**分型失败报告**，判别字段`kind:"exportFailure"`；其字段为kind、failureCode、source（原BackendJobInputDto）、input（原BackendExportInputDto）与diagnostics。诊断沿§9.4完整Runtime发生身份，次数／字节超限明确失败，不截断；报告只含实际非空snapshot。报告准备或cleanup失败保留原primary原因，不能伪造成功或空报告。用户cancel先赢公开闸门则不发表失败报告。

纯传输失败只在job保留stable failureCode，不创造snapshot、occurrence或报告。未知／未登记报告／其他client／过期404 exportArtifactNotFound；登记报告缺失、链接、长度／hash／来源／profile／计划／摘要不符500 taskExecutionFailed。成功／失败report引用不能并存；失败content读取404，不用空影片占位。静态成功实际诊断通常为空；注入snapshot回归不等同真实脚本执行，当前脚本来源仍unsupported。

`GET /api/v1/jobs/job-J/export-diagnostics?cursor=<opaque>&limit=128`读已登记成功或失败报告的实际diagnostics（不含 `fallbackDiagnostics`）。默认及最大128，响应为`{jobId,reportSha256,diagnostics,nextCursor?,cursor?}`；nextCursor存在才继续，无hasMore。游标绑定job／报告SHA／偏移，不可跨报告。重复／非整数query400 invalidRequest，≤0或超上限400 diagnosticLimitExceeded，外来／超界cursor409 diagnosticCursorExpired。报告未登记或完整性失败不能制造空页。

### 11.4 成片读取、租约与重启恢复

`GET|HEAD /api/v1/jobs/job-J/export-content`严格live owner，先核登记报告／媒体身份、size和完整SHA，再流式读64KiB缓冲，不把整片装入byte[]。默认200，单Range／suffix／open-ended为206，If-None-Match为304，精确ETag If-Range不匹配则整片200，无效或多段Range416。响应带`ETag:"<媒体SHA>"`、Accept-Ranges、Content-Length、必要Content-Range和`Content-Disposition: attachment; filename="MakoKara-<jobid后缀>.mp4|.mov"`；HEAD无body。沿§1认证和缓存策略，不能把内容URL当公开凭证。

授权保留沿job默认1小时；过期读取404 exportArtifactNotFound，不能仅因存在exportArtifactExpired枚举推定所有过期均410。启动及新export admission物理prune，删除失败仍占预算；已取得的active-read lease可读完并继续占实际包字节，最后读者释放后再回收。真实HTTP验收不会缩短产品1小时TTL；到期及reader记账由受控TimeProvider回归覆盖时须单独注明。

服务重启后旧job不进入live集合；`GET /api/v1/recovery-jobs/{oldJobId}/export-result`及`GET|HEAD .../export-content`只读previousHost且唯一ledger登记的报告／媒体引用。沿§10.4既有B2本机profile-wide授权，当前持bootstrap登记的profile client用当前accessToken读取，不恢复旧owner／resume凭证，也不回退live ACL或公开孤立目录。恢复报告保留success或kind=exportFailure分型，复核来源／profile（§9双固定allowlist，未知profile拒绝）／固定计划、size与SHA；failure-only没有content。exportInput恢复严格核执行／音频profile组合：null／旧marker配audio null或WAV，新marker配显式MP3／FLAC／AAC／MOV／Ogg；单字段缺失、错配或unknown拒绝。删除两字段并协调改写成合法legacy，在不读源音频的恢复合同下仍不可辨识，不宣称签名或穷尽任意篡改保护。没有单独recovery export-diagnostics路由，完整失败诊断可由登记报告取得。unknown不自动变成功、不重跑。

<a id="audio-analysis"></a>
## 12. 独立离线音频分析（六音轨家族）

### 12.1 能力与固定音频

`GET /api/v1/analysis-capabilities`始终映射并要求client认证。下面是当前默认可用部署的字段示例，实际enabled／guard／工具启动状态必须采用服务返回值：

```json
{"enabled":true,"sourceProfile":"pcm16-wave-analysis-v1","sourceProfiles":["pcm16-wave-analysis-v1","fixed-compressed-audio-analysis-v1"],"algorithm":"bass-pulse-rbj-v1","audioProfiles":["pcm16-wave-self-contained","mp3-layer3-self-contained","native-flac-self-contained","aac-adts-self-contained","mov-audio-self-contained","ogg-audio-self-contained"],"maxAudioBytes":536870912,"maxSourceSampleRate":192000,"maxSourceDurationUs":"300000000","sampleRate":48000,"hopSamples":960,"maxSamples":"14400000","maxPoints":15000,"maxResultBytes":4194304,"maxResultStorageBytes":67108864,"operationTimeoutSeconds":60,"cancellationGraceSeconds":2,"maxRunning":2,"maxRunningPerClient":1,"maxRunningPerProject":1,"maxQueued":16,"maxQueuedPerClient":4,"guardAvailable":true,"toolStartupVerified":true}
```

原WAV profile分析接受工程已授权的固定、自包含PCM16 RIFF WAV，mono或stereo、正采样率≤192kHz、文件≤512MiB、源真实采样帧派生时长≤300秒。Writer按精确工程修订捕获assetId／sizeBytes／sha256并pin不可变资源，Worker前后复核身份；HTTP不接受文件路径、工具路径、grant、分析参数或裸PCM。analysisInput.sourceDurationUs为floor(源采样帧数×1,000,000/源采样率)，300秒门禁按真实分数时长检查，不靠向下取整绕过。任务使用独立`pcm16-wave-analysis-v1`来源profile，不捕获ProjectSnapshot、不调用staticcodec；含effects或已有AudioAnalysis的工程也可分析其已授权音频。预览／验证／导出的旧`static-lyrics-scene-v1`门禁保持；0.69仅以独立`static-lyrics-scene-analysis-v1`完整接入非空已接受分析，见§9。

0.70最初新增 `fixed-compressed-audio-analysis-v1` 的MP3/FLAC来源；本轮该来源扩展为MP3/FLAC/AAC ADTS/MOV音轨/Ogg音轨，具体audioProfiles见能力响应。只按固定bytes轻量识别候选：非free MPEG LayerIII首完整帧／有界ID3（含footer核对），或native FLAC首STREAMINFO；不是完整二进制解码器，也不凭MIME／后缀授能力。真实Worker按profile强制mp3／flac／aac／mov／ogg demuxer、file／pipe协议，实际probe的codec／mono-stereo／正采样率≤192kHz严格匹配；strict解码用-xerror／err_detect explode，损坏不发布partial结果。当前固定音频预算为512MiB，实际codec、源采样率、完整解码与分析300秒/样本/结果预算仍独立检查；不承诺任意容器变体。

压缩input的`sourceDurationUs`为null并**省略**，不填0、header或probe估值；不存在新增durationBasis字段。原WAV仍返回精确采样帧派生时长。压缩300秒能力字段表示实际48k解码≤14.4M样本／15k points，而非admission已获精确源时长；完成后的sampleCount／durationUs按真实完整样本生成。删掉完整压缩帧后仍可能成为合法短音频，不能承诺识别所有原文件删尾。

analysisInput.audio尾增可空`audioProfile`：旧WAV省略或显式pcm16-wave-self-contained只在旧sourceProfile解释为WAV；新compressed必须显式MP3／FLAC／AAC／MOV／Ogg，缺失、错配或unknown拒绝。两nullable属性显式JsonIgnoreWhenNull，旧账本／默认serializer同样省略而非仅依HTTP设置。Host／private grant只尾AudioProfile，默认WAV，demuxer中央唯一映射，不给HTTP InputFormat或工具参数。

分析单独验证真实Worker私有进程组／subreaper与可信FFmpeg／ffprobe有界启动；不要求export编码器或RLIMIT_FSIZE。`toolStartupVerified:true`只说明工具有界启动成功，**不等于已对任意音频完成解码验收**。当前analysis executor probe须匹配engine、恰六个audioProfiles／两个sourceProfiles数组；缺字段／旧0.69 Worker／错engine或profiles返回WorkerAnalysisProfileMismatch禁用。能力缺失时enabled:false、audioProfiles／sourceProfiles为空数组并带`disabledReason`错误DTO，POST返回503 taskUnavailable；已经登记的结果读取不启动Worker。私有执行profile为原`analysis-pcmwav-v1`（仅WAV）或`analysis-compressed-audio-v1`（MP3／FLAC／AAC／MOV／Ogg），严格匹配AudioProfile，不是HTTP可选算法。

### 12.2 创建、进度与工程状态

`POST /api/v1/project-sessions/project-P/analysis-jobs`正文只有精确修订和§1.2的metadata，拒绝未知JSON成员：

```json
{"expectedRevision":"2","metadata":{"requestId":"analysis-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-13T10:01:00Z"}}
```

202返回共享JobResponse，kind为analysis。同client回执重试返回原jobId，接受不等于完成。只读工程可执行；不要求显式composition.outputDurationUs，因为边界来自固定音频。HTTP断连、client租约过期／休眠或后续工程编辑不会取消已接受任务；工程关闭仍沿§7的activeJobs策略。任务与validation／export共用§11.2同一scheduler，持久化控制job不占执行slot；operationTimeoutSeconds是Worker执行调用的期限，不包含等待scheduler slot的排队时间。

运行状态片段如下；其他身份／保留字段沿§8，不把采样数放进frame input／progress：

```json
{"jobId":"job-J","kind":"analysis","state":"running","projectSessionId":"project-P","expiresAtUtc":"2026-10-13T11:00:00Z","cancellationRequested":false,"canCancel":true,"analysisInput":{"sourceKind":"project","sourceId":"project-P","sourceRevision":"2","sourceProfile":"pcm16-wave-analysis-v1","audio":{"assetId":"asset-A","sizeBytes":192044,"sha256":"<固定WAV完整SHA>"},"sourceDurationUs":"1000000"},"analysisProgress":{"stage":"decoding","completedSamples":"24000","timeUs":"500000"}}
```

压缩job的固定input示例（原WAV示例及省略字段形状保持）：

```json
{"analysisInput":{"sourceKind":"project","sourceId":"project-P","sourceRevision":"2","sourceProfile":"fixed-compressed-audio-analysis-v1","audio":{"assetId":"asset-M","sizeBytes":16000,"sha256":"<原MP3完整SHA>","audioProfile":"mp3-layer3-self-contained"}}}
```

`analysisProgress`为`stage,completedSamples,totalSamples?,timeUs?`，计数／微秒均字符串。Worker实际阶段为validating-source／probing／decoding／finalizing-analysis／prepared；Host提交和终态改为committing／completed／failed／cancelled。采样进度只在原Builder实际接收后递增，可合并跳过中间消息；不能保证每阶段都可观察，也没有timer百分比。解码中timeUs为已完成48k样本的向下取整微秒，prepared采用完整样本的ceil时长，此时才公布totalSamples。SSE的job.progress／job.terminal及client state摘要使用同一analysisProgress；重同步沿§4。

**接受、完成和读取结果都不调用SetAudioAnalysisCommand，也不修改工程AudioAnalysis、projectRevision、dirty或撤销历史。** 0.68显式采用／清除沿§12.5的具名命令独立执行；分析结束不写“最新工程”。

主要接受拒绝：unknown／其他client工程404 sessionNotFound、修订409 revisionConflict；没有当前授权音频、身份／格式／源采样率不符400 invalidRequest，资源门禁沿§5；原WAV精确源超过300秒413 resourceTooLarge，压缩源在实际解码样本budget超限时failed而非裁成300秒；结果字节占位429 resourceQuotaExceeded、共享queue429 requestQuotaExceeded。接受后解码、source hash、预算、canonical或发布失败体现在job failed／error.code=taskExecutionFailed与稳定failureCode，GET job本身仍200；deadline为WorkerAnalysisTimedOut，和用户取消终态cancelled区分。不用空points或失败占位文件伪造available。

### 12.3 完整结果、数值与身份

原算法固定48kHz mono／960sample hop（20ms），保持整曲归一化及原浮点精度；beatPhase／beatConfidence是原近似估计，不承诺精准节拍识别。最多14,400,000实际样本／15,000points；超限在Builder.Append前拒绝，不能裁切前300秒后报完整成功。最后不足一hop按真实短尾计算，不补零。没有音频流、不可用或原分析失败均失败；真实静音可以是available完整结果，其phase为null，不与失败混同。

成功job新增`analysisResult`摘要，`report`仍使用共享引用DTO，不把完整points塞进32KiB账本：

```json
{"analysisResult":{"algorithm":"bass-pulse-rbj-v1","sampleRate":48000,"hopSamples":960,"originUs":"0","sourceHash":"<固定WAV完整SHA>","pcmHash":"<完整48k mono float32 PCM SHA>","sampleCount":"48000","durationUs":"1000000","revision":"sha256:<语义修订的64位小写SHA>","contentHash":"<完整canonical JSON SHA>","pointCount":50,"report":{"reportId":"analysis-<jobid后缀>","sizeBytes":9000,"sha256":"<与contentHash相同的完整SHA>"}}}
```

大小／hash只示意字段，实际采用响应。`sourceHash`是原上传音源（WAV／MP3／FLAC）完整字节SHA；`pcmHash`是完整解码48kHz mono的little-endian float32样本SHA；`revision`以`sha256:`为前缀，绑定算法／采样配置／origin／sourceHash／pcmHash／sampleCount，**不是工程修订或完整结果文件SHA**。`contentHash`是原Core完整AudioAnalysisJson的SHA，等于report.sha256；私有IPC输入wire SHA又是另外身份，HTTP不暴露或用它替代上述值。

`GET|HEAD /api/v1/jobs/job-J/analysis-result`严格要求当前live owner。返回原Core **AudioAnalysisJson规范字节**，Content-Type为application/json；没有协议／任务包装，也没有额外contentHash字段。完整文件字段为`version:1,algorithm,sampleRate,hopSamples,originUs,sourceHash,pcmHash,sampleCount,durationUs,revision,points`；每point为`timeUs,bassLevel,pulse,beatPhase,beatConfidence`，beatPhase可null，其余特征保留原double，不重新量化。points按index×20,000µs网格，长度ceil(sampleCount/960)，durationUs为ceil(sampleCount×1,000,000/48,000)。文件内整数保持number，摘要／进度的对应长整数保持string；当前预算下完整文件整数均JSsafe。

完整结果≤4MiB，无points分页、analysis-diagnostics或流式最终features接口。Host先复核完整SHA、canonical字节、固定来源、网格／revision／摘要，再经读租约传输；默认200，单Range／suffix／open-ended206、If-None-Match304、精确ETag If-Range不匹配整份200，无效／多段Range416。响应带ETag、Accept-Ranges、Content-Length、必要Content-Range及`Content-Disposition: attachment; filename="MakoKara-analysis-<jobid后缀>.json"`；HEAD无body。沿§1认证／no-store策略，URL不是凭证。

### 12.4 发布、保留与重启读取

每接受任务先预约完整4MiB结果预算，独立store默认64MiB，含queue／running预留、retained和过期active-read；字节预算可先于queue名额耗尽。Worker／native及管道排水后才返回完整结果；Host有界校验、准备并flush／hash、最后检查取消，随后唯一Job.EnterCommitting→原子发布→ledger终态。公开committing后不可取消；此前取消不发布结果。终态无法登记可为unknown，已发布文件不自动回滚或重新执行。

默认完成后保留1小时；启动及新analysis admission物理prune，不保证每秒立即删除。已取得active-read可读完，仍占实际存储直到最后读者释放；TTL到期不能授予新读。共用sidecar IO不改变原validation目录、IDs、原始字节、预算或权限。缺登记结果、非analysis job、其他client或到期404 jobNotFound；登记文件缺失／链接／长度／SHA／canonical／来源／摘要损坏500 taskExecutionFailed，不重建成成功。

服务重启后旧job不进入live集合；`GET|HEAD /api/v1/recovery-jobs/{oldJobId}/analysis-result`只读previousHost且唯一ledger登记的完整结果引用，沿§10.4既有B2本机profile-wide授权。当前持bootstrap登记的profile client以当前accessToken读取，不恢复旧owner／resume身份、不回退live ACL或猜孤立文件；同样按旧WAV／新compressed两sourceProfile与AudioProfile／SourceDurationUs固定allowlist复核完整文件及输入／结果摘要，未知或错配拒绝，不重新分析、不修改工程。unknown不自动升成功或重放。**0.67本批有限出口已验收；完整终态、源码／部署／证据SHA及明确边界见[实施§21](../backend-extraction-progress.md#b4-audio-analysis-0670)。**

<a id="audio-analysis-apply"></a>
### 12.5 显式采用／清除（0.68）

沿§6.3唯一commands路由、精确当前expectedRevision及§1.2同一client回执，正文示例：

```json
{"expectedRevision":"3","command":{"type":"audio.analysis.apply","analysisJobId":"job-J"},"requestId":"apply-1","hostInstanceId":"host-H","validUntilUtc":"2026-10-14T10:01:00Z"}
```

apply只带非空且≤128字符的analysisJobId；clear用`{"type":"audio.analysis.clear"}`且不带analysisJobId。两者均拒绝target／startUs／endUs，严格未知成员规则保持。只读工程403 readOnlySession。相同有效回执先于修订检查返回原200，异载荷复用409 requestIdReuse；新请求的旧expectedRevision为409 revisionConflict。

只可采用当前live host、当前owner、同project session、succeeded且未过期的analysis job登记结果。job.analysisInput.sourceRevision仅证明来源，允许分析后先改歌词再按**当前**revision显式采用；当前audioAssetId／hash／size与AudioMedia须匹配固定来源，当前工程AudioPath还须等于该session构造时捕获的非空canonical绝对prepared source path。保存重开／恢复的固定闭包路径可以与新导入ResourceStore副本不同，不改写工程描述路径或sourceSHA；同字节换到另一绝对路径、空或相对路径仍拒绝。实际IO只使用已授权asset，继续核actual bytes及lease出口。服务有界复核完整≤4MiB canonical、Core网格／revision／摘要和已授权且在当前分析字节预算内的源音频实际hash；全部文件读取及可能失败的租约释放在mutation前完成，最后重查live状态／TTL／工程／来源。

未知／其他client／过期／previousHost job为404 jobNotFound；同owner非analysis、非succeeded、不同session或当前音频声明／assetId／hash／size／AudioMedia／访问路径异源为422 commandRejected；已登记sidecar／canonical／摘要损坏、当前登记音频文件缺失／link／bytes hash或size损坏、IO／cleanup失败为500 taskExecutionFailed。previousHost recovery仅提供§12.4只读结果，不能用于apply，不重新执行或猜孤立文件。

实际变化只执行一次原`audio.analysis`领域历史，projectRevision递增一次并发一次project.changed；undo／redo恢复完整分析。相同规范结果再次采用或空分析clear为200 changed:false，不改修订／dirty／undo／redo，不产生本操作project.changed事件；响应cursor为client事件流当前位置，其他任务／操作可使其前进。实际变化遇活动History group拒绝422，no-op仍可接受；共享用例额外按Workspace／Generation／Project／音频／原分析及历史连续性拒绝旧基线。没有新增job、结果副本、scheduler或异步apply状态。

采用后summary显示有界audioAnalysis，保存重开仍保完整canonical／revision／contentHash，原job过期不会抹掉已接受数据。clear后有音频工程的原schema28持久化状态为`{"state":"notAnalyzed"}`，不是JSON null；不能把summary字段省略推定为保存资源的null。旧`static-lyrics-scene-v1`保持拒绝任何非空已接受分析；0.69 Host自动选择新完整分析静态profile，预览换输入／正式验证／导出保留完整数据，不静默剥离。clear或undo回空后重新接受的输入自动回旧profile，已有任务仍绑定原固定profile／sourceSHA。当前存在的旧预览仍绑定它已捕获的输入，采用不把旧帧伪称新工程修订；包/预设对完整已采用分析的消费沿§23共享输入；实际渲染结果按本轮代表流程登记。

## 13. 独立模板库（原0.84.0／合同1.9.0，P3扩展见§24）

模板库是 profile 级独立资源，不要求工程、播放头或工程修订。四条路由复用本节开头的认证、Origin／Host、client 回执和关闭排水规则；成功的模板库操作不产生工程事件，不改变任何工程 revision、dirty、undo 或 redo。`builtin` 是只读内置目录，`personal` 是当前 backend profile 的个人目录；发现接口只返回这两个逻辑描述，不因坏个人库而隐藏内置目录。

### 13.1 读取与修订

0.110.1 起，Host 的新个人库使用 `state/libraries/templates-v7/index.json`（目录随当前模板 schema）。历史固定入口 `state/libraries/templates/index.json` 只有在有界读取确认其声明为正整数旧 schema 时才保留原位并隔离，不加载、迁移或改写其模板及包；新目录可正常建立当前格式的库。固定入口已经声明当前 schema 时继续使用原入口及同目录包闭包，避免隐藏已保存的当前模板。未知、未来版本、无法读取或无法判定版本的入口严格拒绝；固定入口为当前格式且新目录也有入口时拒绝歧义。选址首次成功后在该仓储生命周期内固定，当前库损坏不会触发回退或创建替代空库。发现与内置模板读取不依赖此个人库选址。

`GET /api/v1/template-libraries` 返回两个固定描述。`GET /api/v1/template-libraries/{id}/templates` 返回同一次完整索引读取的 `revision`、`offset`、`totalCount`、摘要窗口和可选 `nextOffset`；`offset` 默认0，`limit` 默认50，范围1..100。列表首次读取可以省略 `expectedRevision`，分页继续读取时必须把首次返回的修订作为唯一查询值。`GET .../templates/detail` 必须唯一提供 `expectedRevision` 和 `templateId`；可额外提供唯一 `definitionFormat=full` 读取完整定义；ID 保留原 Unicode、空格、斜杠，不 trim，也不新增128字符限制。

个人索引修订是实际入口 JSON 的小写64位 SHA-256；不存在的入口是 `missing`，与已发布空索引不同。过期修订返回409，非法修订形状或未知／重复查询参数返回400。个人索引、schema、包资源或链接闭包损坏时返回中立的422 `commandRejected`／`LibraryInvalid`，不能把坏库当空库覆盖；内置读取不依赖个人磁盘。

摘要提供模板 ID、名称、kind、固定模板修订、26个属性选择及完整能力标志（配方、包材质、包）。静态模板 detail 的 `definitionAvailability` 为 `staticAvailable`，`definition` 包含完整 canonical 静态样式和属性选择，即使未选属性也必须有合法完整值；字体、描边和阴影的显式 null 保留。省略 `definitionFormat` 时，含配方、包或包材质的合法模板（含Composite）返回 `advancedRuntimeAvailable` 与显式 `definition:null`，静态定义字段保持原形状。显式 `definitionFormat=full` 返回 `fullAvailable`、完整可编辑 `definition` 和只读 `packageClosureDetails`，详见§24.1。原0.102.0只允许Line/Token高级闭包的状态保留其历史来源，当前兼容性由原领域模型判断。完整读取单响应最多1MiB／20000个JSON值，超限413且不截断。

### 13.2 个人库命令

`POST /api/v1/template-libraries/{id}/commands` 只接受 `personal`，请求必须包含唯一 `expectedRevision`、完整 `metadata` 和严格分型的 `command`。命令为：

| type | 语义 |
|---|---|
| `create` | 省略格式提交原静态定义；显式 `definitionFormat:"full"` 提交完整高级定义。服务端在回执执行 owner 内生成一次新 ID。 |
| `replace` | 按精确 ID 整体替换并保持身份；省略格式仍只允许静态分支，显式full允许原模型合法的完整高级定义。 |
| `copy` | 固定读取 `sourceLibraryId/sourceRevision/templateId` 的完整对象和闭包，再以个人库 CAS 写入新名称；不是跨库分布式事务。 |
| `rename` | 只改名称，保留高级模板的全部配方、包、参数和未选材质。 |
| `delete` | 删除个人索引条目，不删除历史包文件。 |

名称沿既有工厂 trim；新建／替换的静态定义必须通过 kind、26属性选择、canonical 样式、solid／gradient 及所有 null／required 门禁，任何未选包材质也会使定义成为高级对象。完整新 command canonical JSON（不含 metadata）最多256KiB；新名称／字体最多256个 UTF-16 code units，回退链最多32项，单渐变最多256个色标。上述静态新输入限额不反向限制既有摘要、完整读取或服务端 copy／rename／delete。完整full分支沿原完整模板工厂校验，不添加静态控件限额；既有command总体256KiB预算仍适用。

所有存在性检查、CAS复核、ID生成、响应预算和发布都在同一 client 回执 owner 的模板库操作生命周期内完成。候选先纯准备，随后检查完整列表／detail／回执预算，再以原仓储的文件锁和实际修订一次提交；失败保留原库。相同成功 requestId 重试返回原回执，即使模板随后删除或库继续改变也不重复生成 ID或写入。等待取消、晚期取消、关闭排水和未知终态沿现有 client 规则处理；个人库的旧模板包资源不由删除清理。分页只限制响应窗口，不声称全库读取已经分页。

本节只描述独立模板库。固定模板试样已在§13.7开放，工程内显式分配见§13.3，静态样式底层接口见§6.11；已有高级闭包运行见§23，P3完整模板高级编辑见§24，作者配置库已在P4接入；运行证据独立登记。不能把模板库摘要或 `staticHierarchy` 称为最终渲染外观。

### 13.3 工程内显式预设分配（0.85.0，合同1.10.0）

工程命令 `presetApplications.apply`、`presetApplications.update`、`presetApplications.setEnabled` 和 `presetApplications.remove` 把独立模板库中的完整模板快照写入当前工程；P3允许原模型合法的完整静态/高级模板、clips及高级Node，具体兼容性由 `PresetApplicationTargets.CanApply` 判断。四类命令仍复用工程命令的精确 `expectedRevision`、client 回执、单写者、事件和一次历史语义；模板库操作本身不改变工程。

`apply` 和 `update` 必须同时提供 `templateLibraryId`（`builtin` 或 `personal`）、`templateId` 和个人库/内置库列表返回的库级修订（该命令字段名为 `templateRevision`）。它与候选/试样请求单列的模板内容修订不同。Host 在工程 Writer 外读取并固定模板完整快照，随后只把该不可变快照交给 Application 的 Prepare→Commit；库在捕获之后改名、替换或删除，不会改变这次分配。模板快照不是工程引用，已经写入的工程分配不随库更新自动变化。

`apply` 的 `targets` 是 1..1000 个不重复目标；当前严格区分 `project`、`branch`、`line`、`token`、`clips` 和 `node`。project 不带 ID，branch 带 `branchId`，line 带 `branchId/lineId`，token 带 `branchId/lineId/tokenId`；合同1.24.0新增nodeId与显式includeDescendants的Node目标，完整规则见§13.20。各kind拒绝其他父子字段的非null值，保留已知无关字段显式null兼容。`branchInputs` 可省略或为字符串到字符串的对象，实际来源存在性和模板属性适用性仍由领域校验决定。clips带 `branchId` 和1..1000个不重复非空 `clipIds`，每项须属于该来源；目标不存在、模板与绑定不兼容或修订不匹配时整批拒绝，不提交部分应用。

`update` 按一个 `applicationId` 更新同一模板身份，保留应用身份、实例身份／种子和启用状态；更换模板身份须先 remove 后 apply。`setEnabled` 只改变一个应用记录的启用状态；`remove` 接受 1..1000 个不重复应用身份，移除失效应用也不要求重新解析旧模板。无变化不增加工程修订或历史，并保留 redo；成功变化只增加一次修订和一条工程历史，失败不改变工程、dirty、redo 或事件。

当前目标绑定包括上述六类。Node规则在§13.20的历史静态切片上扩展到原模型合法高级模板，clips和高级Node当前行为见§24.4。此前匹配self-contained部署对静态预设Worker的validation/export及公共preview PNG有限链路的真实HTTP验收保持原归属；本轮Node与G20～G24测量证据独立记录。公开编排编辑见§13.11～13.21；公共预览按§23消费完整固定快照和资源；分配操作不授予任意路径或作者草稿执行能力。模板摘要、`staticHierarchy`或分配记录本身不等同最终帧外观。P4完整作者流程、任意编排能力、前端浏览器和跨平台验收继续独立。

<a id="g15-preset-applications"></a>

### 13.4 工程预设分配读模型（G15，合同1.14.0）

`GET /api/v1/project-sessions/{id}/preset-applications?expectedRevision=...` 在当前工程 Writer 内读取完整分配列表，并要求唯一的精确工程修订。每项返回稳定 `applicationId`、`target` union、`targetValid`、`enabled`、模板快照摘要和 `branchInputs`。目标有效性按当前歌词来源、行、元素及现有展示节点／片段身份只读计算；失效引用仍会完整返回，不能因读取而修改或删除工程。

合同1.52.1／产品0.112.90起，已经保存的应用若其顶层 `target` 已不存在（包括对应具体clip缺失），运行时跳过整条应用的实例、材质准备和媒体依赖查询；不会把它改成禁用、删除或裁剪。持久快照继续保留原 `enabled`、应用／配方／实例身份、种子、模板定义和包资源，`SourceMaterialCatalog`、源指纹与codec仍检查完整持久闭包。`ProjectValidator` 在领域工程检查中对缺失目标给出明确warning，工程仍可测量、预览和导出；撤销删除、恢复相同ID的目标后，原应用按原启停状态和身份重新参与运行。客户端以 `targetValid:false` 提示当前被跳过，并保留恢复目标或显式移除应用的操作。该规则只针对应用的顶层目标；目标仍有效但内部binding／subscription、直接effect、包或其它闭包损坏继续沿原规则拒绝，不能据此跳过。新apply／update的目标存在性与原子校验不变。没有新增路由、DTO、schema或fixture。

现有 `PresetApplication` 快照没有持久化来源模板库身份和库级 revision，因此响应中的 `source` 明确且固定返回 `comparable:false`、`libraryId:null`、`revision:null`、`newVersionAvailable:null`。这三个字段即使为 null 也不会省略，OpenAPI `ProjectPresetApplicationSourceDto` 明确声明 `string|null`／`boolean|null` 并将其列为必返字段；模板内容哈希不冒充库 revision，“有新版本”比较待后续持久化迁移与合同切片。能力列表独立公布 `project-sessions.preset-applications.read`。读取遵循统一 1 MiB JSON／20000 项预算，超限整批返回 `413 resourceTooLarge`，不截断。

四个分配命令成功回执可带 `presetApplicationResult.applicationIds`；分配状态变化的 `changeSet.areas` 包含 `presetApplications` 与 `renderInputs`。applicationIds只是该命令的真实应用反馈；当前Node/clips目标写入见§24.4，原静态切片保留于§13.20。它不代表作者包、脚本、媒体或任意场景编排能力。

<a id="w4-orchestration-default"></a>

### 13.5 工程编排读模型与默认容器（W4，合同1.13.0）

`GET /api/v1/project-sessions/{id}/orchestration?expectedRevision=...` 在工程 Writer 内按唯一精确修订读取当前歌词编排结构。响应 `ProjectOrchestrationResponse` 包含 `projectSessionId`、`projectRevision`、`nodes` 与 `programs`。每个 node 返回稳定 `id`、可空 `name`／`parentId`、`placement`（`x`、`y`、`anchor`、`rotationDegrees`、`scale`）、`referenceRect`（宽高比例）、可空 `layout` 和可空 `contentKind`。layout 返回 `kind`、可空 `defaultPlacement`、`safeArea`、`maxWidthRatio` 与 `slotIds`；program 返回 `id`、`branchId`、`rootContainerId`、`defaultContainerId` 和可空 `animationPolicy`。这些字段是当前工程真实结构投影，不以数组位置代替身份。

该结构读模型不返回最终样式、模板、字体、颜色、动画/时间采样、画布测量矩形、PNG或任意场景JSON。当前增量包含clips/scheduleContext（§13.9）、完整leadIns与倒计时/mark内容（§13.15/13.19）、projectEnterAnimation与leadInGeneration（§13.18/13.19）；真实同帧测量与PNG由独立§13.21提供。查询只接受唯一 `expectedRevision`；未知或重复 query、缺失或非负十进制 Int64 形状错误返回 `400 invalidRequest`，修订不匹配返回 `409 revisionConflict`，读取不修改工程。现有读模型预算继续适用：完整UTF-8 JSON最多1MiB、最多20000个JSON值；超限整体 `413 resourceTooLarge`，不截断。

`POST /api/v1/project-sessions/{id}/commands` 新增严格类型 `orchestration.setDefaultContainer`，业务字段仅为非空白 `branchId` 与 `containerId`，均保留稳定ID原值并最多128字符。`branchId` 必须指向当前工程来源程序；`containerId` 必须指向当前工程现有且确为歌词容器的节点。缺失、显式 `null`、空白或未知字段由命令 converter 以 `400 invalidRequest` 拒绝；来源不存在、容器不存在、目标不是容器或工程校验失败以 `422 commandRejected` 拒绝。命令不创建、删除、改父级、排序、槽位、片段或前奏，也不接受清除默认值的隐式语义；当前 `DefaultContainerId` 为必填身份，nullable 迁移另属后续合同。

成功改变默认容器时只产生一次工程修订和历史，标记 dirty、清理 redo，并在回执 `orchestrationResult` 返回 `branchId`、`containerId`、`changed:true`；`changeSet.areas` 为 `orchestration` 与 `renderInputs`，需要完整刷新。当前值相同是 no-op，回执返回 `changed:false`，修订、历史、dirty、redo和事件均不改变。成功重试沿现有 requestId 回执缓存，不重复历史；修改同 requestId 载荷返回 `requestIdReuse`。`history.undo`／`history.redo` 沿既有精确修订与单写者规则恢复／重应用默认容器状态，保存、关闭、重开后读模型保留该状态。

本节1.13.0原批仅交付结构读取和默认容器设置；后续节点、容器、槽位、几何、片段、前奏及自动生成的当前入口见§13.8～13.21。当前后端合同仍不代替前端拖拽交互、浏览器W4或用户验收。

<a id="g16-preset-candidate-preview"></a>

### 13.6 工程预设候选试看（G16，合同1.15.0）

`POST /api/v1/project-sessions/{id}/preset-application-previews` 在当前工程的精确 `expectedProjectRevision` 上创建只读候选预览。请求使用原 `templateLibraryId/templateLibraryRevision/templateId/templateRevision` 四引用，或提供互斥的 `source`（savedTemplate/full draft，见§24.3）；同时提交1..1000 个严格 `project`／`branch`／`line`／`token`／`clips`／`node` 目标（当前规则见§24.4）、可选 `branchInputs`、尺寸和透明模式。库修订或模板内容修订不匹配返回409；未知库／模板返回404；既有高级Line/Token模板的完整配方/包材质/固定包可进入候选，复用§23准备；图片/视频背景沿同一资源授权。高级Node与clips按原领域兼容判据准入，不支持的定义或绑定明确拒绝，不会创建空会话。

服务先在模板库只读操作中固定完整模板闭包，再在工程 Writer 内核对工程修订并复制不可变 `Project` 到隔离工作区，仅运行 `PresetApplicationsEditUseCase.PrepareApply`。候选捕获不调用 `CommitPrepared`，不写 `ProjectHistory`、工程 dirty／redo、projectRevision、应用身份、工程事件或模板库；候选响应沿既有预览生命周期返回 `PreviewKind:"presetCandidate"`、`Committable:false`，后续帧请求、状态、PNG、诊断和关闭复用同一认证／SSE／租约／排水规则。候选固定后工程或模板库变化不会追改候选，客户端必须重新请求新修订；正式 `presetApplications.apply/update` 仍是唯一提交入口。

当前候选支持上述六类目标及完整合法模板，通过§23统一Runtime输入消费固定包、媒体与包字体；P3补充的完整草稿来源见§24.3。正式应用仍通过已有apply/update，不提供把候选会话隐式提交为工程的操作。`project-sessions.preset-application-previews.create` capability 仅表示该只读入口已部署，不能推断完整 W4 或浏览器验收完成。

<a id="g17-template-fixed-sample"></a>

### 13.7 模板固定试样（G17，合同1.16.0）

`POST /api/v1/template-preview-sessions` 创建脱离工程的固定模板试样。请求 `source.kind` 为 `savedTemplate` 时必须同时提供 `libraryId/libraryRevision/templateId/templateRevision`；服务在该精确库修订下读取完整受支持模板闭包，源修订锁定为 `libraryRevision:templateRevision`。`draft` 来源必须提供客户端生成的 `draftRevision` 与完整 `StaticTemplateDefinitionDto`，仅保存在会话中，不写个人库。

当前提供版本化样本 `sampleId:"default-v1"`，支持整行/逐元素静态模板及库内已有完整高级闭包。服务复用canonical校验和原PresetApplicationsEditor创建固定 `640x360` 歌词样本；高级模板的具名BranchInput映射到试样primary，包字体与效果配方经§23共享准备。P3的 `workshop-single-line-v1`／`workshop-multi-line-v1` 与完整 `draft`／`savedTemplate` definition、稳定目标和材质槽位选择见§24.3；旧静态draft与default-v1保持兼容。完整Composite或其它原模型合法模板是否可运行由领域兼容性判断，不再由本节添加静态种类限制。返回沿预览会话生命周期，`previewKind:"template"`、`committable:false`；后续请求、状态、PNG/ETag/HEAD、SSE 事件、诊断和关闭复用现有认证、租约、代次与排水语义。

模板试样不创建工程、不改变工程修订／dirty／redo，不读取用户音频或播放头。样本像素是模板卡片试看，不等价于目标工程最终外观；前端浏览器卡片布局与缓存验收另计。

<a id="g18-basic-layout"></a>

### 13.8 G18 快速布局（合同1.17.0）

`POST /api/v1/project-sessions/{id}/commands` 新增 `orchestration.previewBasicLayout` 与 `orchestration.applyBasicLayout` 两种严格命令。两者共享 `branchId`、`preset`、`layoutName`、`slotNames`、`selectedSegmentName`、`retainedSegmentName`；`selectedLineIds` 可省略或显式 `null` 表示整来源，也可提供非空稳定行 ID 数组表示连续范围；范围必须连续、按来源顺序且目标存在，否则以 `InvalidRange` 或 `DiscontiguousRange` 稳定拒绝。`previewBasicLayout` 只运行快速布局预演，不写工程历史、修订、dirty 或 redo；`applyBasicLayout` 在同一精确修订下执行一次历史应用，成功回执提供 `basicLayoutResult`（含新建 clipIds）与 `orchestration`／`renderInputs` 完整刷新区域。

当前只接受已存在歌词容器／结构树可表达的快速布局预设，且片段范围要求来源为独立计时、Karaoke 播放策略；FollowPrimary／Static 来源会以 `UnsupportedSource` 稳定拒绝（整来源与范围一致），避免在展示树校验末端返回不透明 `PresentationInvalid`。字段和槽位数量（按预设要求精确为1/2/2/3）在命令准入阶段校验；未知预设、无效来源、非法槽位或领域布局失败稳定拒绝，不创建部分结果。结果包含 scope、识别出的预设、受影响行数、既有 clips、保留节点和拆分 clips 统计。该快速布局命令本身不承担任意node/clips/leadIns编排；当前节点、几何、前奏及测量分别见§13.11～13.21。该布局命令不负责资源授权；共享Runtime媒体/字体消费见§23，真实预览像素与前端W4浏览器验收按各批证据另计。

<a id="b4-managed-audio-preparation-api"></a>

## 14. B4-I 受管理媒体准备（0.72.0有限出口已完成）

B4-I在当前工作区把原有Backend自包含音频准备放入一个私有、受管理的Worker执行边界。它没有新增HTTP路由、公开job种类、工程schema或项目格式；新建继续同步201并沿原client receipt，open／recover继续使用既有jobs、ledger、candidate和目录租约。App保持原`AudioWaveformLoader.CreateDefault()`；未调用`ConfigureAudioPreparation`的进程内Hosting保留Backend默认`CreateForSelfContainedFiles()`。Server组合显式配置Worker-only，只有真实能力握手成功后才启用，失败时禁用并拒绝执行。

`GET /api/v1/capabilities`的`audioPreparation`尾字段公布实际能力：固定profile为`self-contained-native-waveform-v1`，families恰为`wav`、`mp3`、`flac`、`aac`、`mov`、`ogg`；完整结果上限4MiB，base桶上限500000，桶宽64 samples；默认deadline 60秒（启动配置允许1～120），取消宽限2秒；全局／单client最多running 1／1、queued 4／4。`enabled`只有实际Worker握手同时确认EngineIdentity、profile、六家族、预算、native工具启动与private-group／subreaper能力时才为true；缺失、未知、重复或不匹配均disabled。disabled时Server不回退Host native：同步create返回503 `taskUnavailable`，已接受的open／recover job以`failed`／`taskUnavailable`终态结束。

Worker私有封包只授予Host已固定的绝对路径、size和SHA-256 grant。Worker前后复核实际文件身份、祖先no-link及取消；结果保留原`AudioStreamInfo`五字段`DurationUs`、`SampleRate`、`Channels`、`CodecName`、`ContainerName`，以及源采样率和完整解码sampleCount。Info的probe时长与波形实际采样数各保原语义。传输base层min／max float32 bits，含最后不足64 samples的尾桶；Host用与原Builder相同的pairwise merge重建完整pyramid，不合成PCM重新聚合。接受下一decoded block前核500000桶容量，超限使用`WorkerAudioPreparationBudgetExceeded`，不截断或发布partial。4MiB是协议的额外payload边界；合法结果先受500000桶约4004160字节上限约束，不能把真实HTTP的base桶超限另记为独立payload-budget失败；结果来源SHA、计数、Info、bucket shape和float值不一致时拒绝。准备不套分析48kHz重采样、压缩export的300秒、mono／stereo或192kHz上限；4MiB准备结果预算也独立于snapshot和analysis预算。

准备从进入内部许可队列起计deadline，内部上限为running1／client1／queued4／client4。create的名称、composition、资源ID非空与splitRule等基础字段先校验，再预约Worker许可，保原`invalidRequest`优先级；实际资源授权与读取仍沿原资源门禁。用户取消保原取消／OCE语义；deadline使用`WorkerAudioPreparationTimedOut`，transport／protocol失败保稳定`WorkerAudioPreparationTransportFailed`／`WorkerAudioPreparationProtocolInvalid`；Worker复核实际来源身份变化以`taskExecutionFailed`／`WorkerAudioPreparationSourceChanged`拒绝；Host原`VerifyFixedAudioSource`拒绝保`taskExecutionFailed`且不新增`failureCode`。冻结兼容合同按入口与错误种类区分：Worker确认InputRejected转sanitized IOException，create沿原422 `audioPreparationFailed`及`field:AudioPath`，open／recover沿原`projectLoadFailed`且无failureCode；NoAudioStream仍返回null，create为`noAudioStream`，open／recover为原`audioPreparationFailed`，两者不额外添failureCode。仅open／recover的generic fallback不因mapper额外添`TaskExecutionFailed`，其他job种类保原逻辑；deadline／transport／protocol／SourceChanged等typed Worker执行失败仍保稳定failureCode。取消、超时、Worker退出和Server shutdown在公开终态前必须等实际Worker及其private group/native成员排水；停止后拒绝新准备和重新配置。候选session、资源read lease、未发布candidate和同句柄重试保原所有权。

本节合同已完成本批有限验证。真正HTTP final1发现的open ffconcat错误wire兼容回归已由`1c70fae`最小修复，原断言不放宽；repair2统一775／775及严格真正HTTP699请求／4239断言终态通过。HTTP只公开duration与state，完整Info／sampleCount／base float bits／pyramid与协议预算由独立Worker／Media／Hosting回归验证，不新增波形路由；owned同client ffconcat是HTTP直接负例，不扩称MOV／损坏／NoStream／预算全部经HTTP或任意／跨client路径隔离。完整部署／终态／排水／SHA与历史失败见[实施§27](../backend-extraction-progress.md#b4-managed-audio-preparation-0720)。有限准备能力不扩为整曲、任意媒体、跨平台隔离或完整B3／B4验收。

## 15. 配置与部署

以下属于可信组合根配置，不能由HTTP正文授权；普通正整数变量由ResolveInt／ResolveLong读取，缺失／非正／不可解析时取默认，随后仍受领域上限检查。正式验证／导出／音频分析timeout分别严格解析，非法值拒绝启动；预览TTL大于300也直接拒绝。

| 配置 | 默认／含义 |
|---|---|
| `--urls`（优先）／`ASPNETCORE_URLS` | `http://127.0.0.1:0`；显式http／https loopback，不能0.0.0.0或通配监听 |
| `MAKO_BACKEND_RESOURCE_ROOT` | 临时目录下 `makokara-backend/<hostInstanceId>`；固定上传资源 |
| `MAKO_BACKEND_STATE_ROOT` | LocalApplicationData下 `MakoKaraGenerator/backend/state`；独立持久profile与独占写租约 |
| `MAKO_BACKEND_ALLOWED_ROOTS` | 空授权；JSON对象如 `{"projects":"/absolute/path"}`，目录根由启动器控制 |
| `MAKO_BACKEND_ALLOWED_ORIGINS` | 额外允许origin列表，以分号分隔；显式http／https origin，不带路径／query／fragment |
| `MAKO_BACKEND_BOOTSTRAP_TOKEN` | 未设置则生成32随机字节的hex；设置值须32～256非空白字符 |
| `MAKO_BACKEND_CREDENTIAL_FILE` | 无继承bootstrap时默认resource root下bootstrap.json；CreateNew，Unix0600，拒覆盖，正常退出删除拥有文件；继承bootstrap时默认不写文件 |
| `MAKO_BACKEND_MAX_ASSET_BYTES`／`MAKO_BACKEND_MAX_ASSET_QUOTA_BYTES` | 默认null，无对应单资源/总字节部署限额；显式值须为正整数，两个值同时配置时单资源不能大于总量 |
| `MAKO_BACKEND_ASSET_TTL_SECONDS` | 600，1～86400 |
| `MAKO_BACKEND_CLIENT_TOKEN_SECONDS`／`MAKO_BACKEND_CLIENT_LEASE_SECONDS` | 120／300，合法上限分别3600／86400 |
| `MAKO_BACKEND_MAX_EVENTS` | 256，1～4096 |
| `MAKO_BACKEND_AUTOSAVE_SECONDS` | 30，固定工程恢复快照周期 |
| `MAKO_BACKEND_WORKER_PATH` | Server同目录Worker apphost（Windows.exe）；显式值须绝对路径／存在／可执行。Server publish不自动复制Worker，须部署其依赖；没有同进程fallback |
| `MAKO_BACKEND_PREVIEW_FRAME_LEASE_SECONDS` | 30，正值最大300；验收探针10秒只是夹具 |
| `MAKO_BACKEND_VALIDATION_TIMEOUT_SECONDS` | 默认60，提供时须规范无符号整数1～60，否则启动拒绝；validation-capabilities公布实际值，验收探针8秒只是夹具 |
| `MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS` | 默认null，无该维度部署时限；提供时须为正整数秒，export-capabilities公布实际值 |
| `MAKO_BACKEND_EXPORT_MAX_FRAMES`／`MAKO_BACKEND_EXPORT_MAX_DURATION_US`／`MAKO_BACKEND_EXPORT_MAX_FPS` | 默认null，无对应帧数/时长/帧率部署限制；显式值须为正整数 |
| `MAKO_BACKEND_EXPORT_MAX_CANVAS_WIDTH`／`MAKO_BACKEND_EXPORT_MAX_CANVAS_HEIGHT`／`MAKO_BACKEND_EXPORT_MAX_RAW_FRAME_BYTES` | 默认null，无对应画布/裸帧部署限制；显式值须为正整数 |
| `MAKO_BACKEND_EXPORT_MAX_MEDIA_BYTES`／`MAKO_BACKEND_EXPORT_MAX_AUDIO_BYTES`／`MAKO_BACKEND_EXPORT_MAX_COMPRESSED_AUDIO_DURATION_US`／`MAKO_BACKEND_EXPORT_MAX_ARTIFACT_STORAGE_BYTES` | 默认null，无对应媒体/音频/压缩音频时长/产物存储部署限制；显式值须为正整数 |
| `MAKO_BACKEND_ANALYSIS_TIMEOUT_SECONDS` | 默认60，提供时须规范无符号整数1～60，否则启动拒绝；analysis-capabilities公布实际值 |
| `MAKO_BACKEND_AUDIO_PREPARATION_TIMEOUT_SECONDS` | 默认60，提供时须规范无符号整数1～120，否则Server拒绝启动；`audioPreparation.operationTimeoutSeconds`公布实际值，取消宽限固定2秒 |
| `MAKO_BACKEND_FFMPEG_PATH`／`MAKO_BACKEND_FFPROBE_PATH` | 导出／音频分析／B4-I准备Worker使用的可信绝对可执行路径；未设置时从PATH的绝对目录解析ffmpeg／ffprobe；HTTP不得覆盖。不存在／不可执行或对应真实Worker能力探测失败时各能力独立disabled，准备不回退Host native |

Kestrel当前限制：配置单资源大小时请求body最多该配置＋64KiB，未配置时不设该body字节上限；64并发连接、16KiB请求头、15秒头读取超时。资源准备仍要求FFmpeg／ffprobe，静态Worker渲染需要实际字体和固定native依赖，configured-unverified不替代能力验证。当前 Linux-x64 匹配部署与稳定启动入口见 [Web交接§3](../../web/README.md#3-在本仓库启动-headless-后端) 和 [backend-web-host](../../tools/backend-web-host/README.md)；历史机制见[runbook](../backend-server-runbook.md)，私有snapshot／Worker／PNG合同不是HTTP客户端API。

## 16. 维护与验证边界

0.75.1 G1与Web后端启动基础有限出口已完成：正常solution零警告错误，Hosting711＋self-contained Server46＝757／757，1375输入SHA无漂移。默认TTL／16MiB真实HTTP79请求491断言、两Serverexit0／12实际观察身份gone；独立测试lease2秒下70请求494断言、64新登记／有效lease配额保护／旧access与resume拒绝通过；launcher同profile拒绝与异常EOF退出另测。真实PNG核完整解码／内容存在与传输身份，不作独立Runtime像素oracle；不宣称外部Web W0浏览器验收。完整范围、历史失败、部署／证据SHA见[实施§31](../backend-extraction-progress.md#web-g1-backend-0751)。以下B4-I及更早数字保持原批次历史归属。

0.72.0 B4-I有限出口已完成。`1c70fae` repair2原八commands实际exit0，正常solution零警告错误，七TRX775／775（Hosting555／Worker70／self-contained Server44／Media80／Processes2／Application11／App13）；1602source与5849实际bin SHA无漂移，465观察身份gone，manifest SHA`c4304684c49cadb4f88a22150a3d0de5332c98e1f70846be57b3cb4daed09c55`。严格真正HTTP原session38819 exit0，699请求／2655工作流＋1584凭证扫描＝4239断言，58whole终态／六actual-marker恢复／七controls通过；四Servers exit0／172观察成员（52groups）gone，publish21与握手13另计。1342HTTP输入／1448四role部署文件／1586HTTP证据SHA已核，HTTP manifest SHA`4022d9e3186e11d18a1c2c10dd4017aa12291700a9aaf92a93f14e6c4b33c04e`。原753、final1失败137／987、Server42／39／3测试修正、CA1869零执行及runner快照缺口独立保留；完整证据与独立审查见[实施§27](../backend-extraction-progress.md#b4-managed-audio-preparation-0720)。以下B4-H及更早数字保持原批次历史归属。

0.71.0 B4-H固定压缩音轨有限出口已完成，固定端点99189f2。原六TRX716／716保留修前历史；晚期同源分析采用修复后正常solution原29952 exit0、零警告错误，新Hosting504＋self-contained Server36两TRX540／540，未改Worker54／Media24／Export100／Runtime8四TRX186／186只由原SHA引用、不重复计入新测试数。修后manifest SHAd0a04a82ebc485401024f9f51714096b85abd3686a21952162072403f6247628。真正HTTP原47134 exit0，385请求／2621工作流＋939凭证扫描＝3560断言，28完整终态与原响应一致，七产物报告／媒体登记及旧新六恢复完整字节一致；六带音轨影片各90完整帧与Runtime内容oracle、源实际解码音轨通过，纯视频另90帧／无音轨、没有独立内容oracle。FLAC有理尾源／透明PCM144144采样帧精确，AAC用有损容差；重开后新分析完整canonical及apply200／修订1通过。4failed＋1cancel无artifact；三Server exit0／38观察private成员（13groups）身份gone，27publish identities另计。1568统一source、1308HTTP deployment inputs、1444当前部署文件及434真实旧Worker文件分别核；942HTTP证据文件SHA已核，独立源码和原HTTP／媒体审查均通过。完整SHA、失败20／191与94／911的独立归档及边界见[实施§26](../backend-extraction-progress.md#b4-compressed-export-0710)，不把原716重写为修后全套或虚构新726。

以下0.70.1及更早条目保留各自历史范围。

0.70.1主体及统一有限验证已实现：原solution99164退出0／零警告错误，Hosting460／self-contained Server33／Media六相关类64，三TRX557执行／557通过；1561输入SHA无漂移，manifest SHAa204fa4fc42ab6ddeed4b6385bcdd253f36b0e70cbf05378f67d7de8ae40af2a。旧0.70真正HTTP repro原38709的30请求／238断言只作修前证据；修后原69113实际exit0，131请求／570工作流＋366凭证扫描＝936断言，六家族创建保存重开实际duration一致、ffconcat拒绝及Host直接native取消／同句柄重试通过。29完整终态JSON与原响应一致、Server退出0／31direct native及4Worker成员（2groups）身份gone；1391证据文件SHA与1014部署文件核验通过，有限出口已完成，完整证据见[实施§25](../backend-extraction-progress.md#b4-self-contained-preparation-0701)。analysis／export profile与路线不扩。

0.70固定压缩分析有限出口已完成，正常solution原23492零警告错误，Hosting442／Worker37／self-contained Server33／Media五类39，四TRX551执行／551通过；统一终态时1553输入无漂移，随后仅新薄probe.py两处工具修正，产品源不变、最终工具c00c3a5补充冻结，统一manifest SHA324b8ab0d46f7899189bc66cebb1c00a6e8dd4b4c58372861471c435d05a753a。真正HTTP原37041 exit0／152请求／873工作流＋382扫描＝1255断言，四完整canonical（3压缩＋原WAV）byteexact、三采用保存重开、PNG57,600像素零差／正式1＋1、真实strict MP3取消及旧0.69 Workerdisabled／旧新恢复通过；三Server退出0／32观察身份gone。最终SHA／原两工具失败及声明sourceSHA／grant区分见[实施§24](../backend-extraction-progress.md#b4-compressed-analysis-0700)。Export／VideoExporter／StaticExportTask不改，includeAudio:true仍PCM16 WAV，新增分析格式不扩大带音轨export。

0.69主体已实现，正常solution原94483退出0／零警告错误，Rendering61／Hosting402／Worker20／self-contained Server30／Core限定28／Runtime四类18，六TRX559执行／559通过，1542输入SHA无漂移；统一manifest SHA9227195818f6e65a8364a1af162a4b14a449e647d4d8ed94c64a765db75bcbd9。真正HTTP原23038 exit0、232请求／1313工作流＋551凭证扫描＝1864断言；完整canonical／同声明snapshotSHA、三组PNG像素零差、新旧正式1＋1、三片各完整30解码帧及真实0.68 Worker export disabled通过，三个Server与26观察group身份退出。完整source／部署／证据SHA见[实施§23](../backend-extraction-progress.md#b4-analysis-static-profile-0690)。

0.68产品6264a94，统一正常solution0警告／错误，四TRX Application530／Hosting390／self-contained Server29／Core限定28共977执行／977通过，无失败／跳过／中止；统一验证时1533源／资源／构建输入无SHA漂移；后续工具schema28清除状态、PNG格式转换及结果快照clone三处工具修正另以f4787bd冻结（同一probe.py输入），产品C#未改，原977 manifest保持。真正HTTP原21974退出0、367请求／2522断言；两音频完整canonical byteexact，采用／保存重开及clear后PNG像素零差／正式1＋1验证／30帧纯视频影片恢复通过。两个Server退出0、本轮16观察private-group身份gone；两工具失败及final3归档alias过程单列，最终source／部署SHA见[实施§22](../backend-extraction-progress.md#b4-analysis-apply-0680)。

0.67独立音频证据仅六默认真实输入的完整canonical对照；第二Server-re用于实际进程控制，最长接受6.513秒，未实际解码300秒／等满1h TTL。Hosting全项目保留旧frame／export兼容，本批不增加独立渲染／影片验收。

本规范以 [ServerEndpoints](../../src/MakoKaraGenerator.Backend.Server/ServerEndpoints.cs)、其Persistence／Preview／Streaming／Validation／Export／Analysis部分、[Contracts](../../src/MakoKaraGenerator.Backend.Contracts/BackendContracts.cs)及实际Hosting校验为准。后续每批新增／修改路由、DTO、auth、配额或配置时同步这份文件；设计草案只保留目标，不再维护另一份当前列表。

历史0.65最终solution0警告／错误，限定332/332、真正HTTP389请求／2152断言（1344工作流＋808凭证扫描），FullHD3＋3正式计划的报告／进度／整数时刻及固定codec源SHA／profile与独立原验证器一致；完整终态及SHA见[实施§19](../backend-extraction-progress.md#b4-static-validation-0650)。Server16使用受控执行器验证接线，真正Worker链由独立HTTP探针验证，不混用其计数。历史0.64的315／112及PNG像素证据仍见[实施§18](../backend-extraction-progress.md#b3-static-preview-0640)，不倒记到本批。该0.65批次不是PNG／影片或脚本验收，不能作为0.66成片证明。0.66最终正常solution0警告／错误、7组8TRX合计436/436；独立真正HTTP764请求／5682断言（4057工作流＋1625凭证扫描），普通／透明／有理fps三片各90完整解码帧及原Runtime内容oracle通过，完整双声道离线附证29另列不并HTTP计数。最终source／部署／证据SHA见[实施§20](../backend-extraction-progress.md#b4-static-export-0660)；受控执行器或注入诊断不等于实际成片或脚本运行。0.67统一有限测试460／460及真正HTTP789请求／5943断言已终态通过，六音频完整canonical与独立原Analyzer byteexact；最终source／部署／证据总SHA已核验，两Serverexit0／43观察group身份全gone，见[实施§21](../backend-extraction-progress.md#b4-audio-analysis-0670)。完整B3／B4、包脚本／图片视频／音频驱动视觉、其他音频、Web产品和全量／性能／任意字体跨机器等价／平台人工范围仍未完成。


<a id="g19-clip-range"></a>

### 13.9 G19 片段范围与编排投影（合同1.18.0）

G19 放开快速布局的连续范围模式：`selectedLineIds` 为非空、唯一、按来源顺序的连续行 ID；整来源仍可省略或显式 `null`。布局预演不写历史，应用一次历史并返回 `basicLayoutResult.clipIds`，用于识别本次新增片段。

`GET .../orchestration` 的每个 program 现在返回 `clips`。每个 clip 仅公开稳定 `id`、可选 `name`、连续 `lineIds`、`containerId` 与可选 `scheduleContext`（成员、默认段标记及前后边界）；不公开 leadIns、包材质或脚本闭包。读模型继续受精确修订、身份、1 MiB／20000项预算约束。

范围无效、乱序、断续或重复行、目标来源／容器不存在，以及拆分后 recipe/reference/schedule 冲突均在提交前稳定拒绝；前端应按 failureCode 处理，不把部分候选当作成功。

<a id="g19b-clip-lifecycle-0910"></a>

### 13.10 G19-B 显式片段生命周期命令（合同1.19.0）

`POST /api/v1/project-sessions/{id}/commands` 新增四种严格编排命令：`orchestration.createClip`、`orchestration.renameClip`、`orchestration.setClipContainer` 与 `orchestration.deleteClip`。四种命令都必须携带 `branchId` 与 `clipId`，稳定 ID 为非空白、最多128字符；命令仍受顶层 `expectedRevision`、request metadata、唯一 Writer、一次请求一次历史和原回执缓存约束。

`createClip` 还要求 `lineIds`（1..1000个非空、唯一稳定行 ID，按来源顺序形成连续范围）与 `containerId`；`name` 可省略或显式 `null`。`renameClip` 要求 `name` 字段显式出现，可用 `null` 清除名称；`setClipContainer` 要求非空 `containerId`；`deleteClip` 不带其他业务字段。未知字段、缺少必填字段、显式 null 违反字段规则和空白／超长 ID 在 converter 阶段以 `400 invalidRequest` 拒绝。

命令目标必须属于存在的歌词来源程序；来源必须是独立计时且有效播放策略为 Karaoke。创建／移动的容器必须是当前来源展示根下真实存在且有歌词布局的容器。不存在的来源或片段、重复片段身份、行不存在／重复／乱序／不连续／未定时、容器越界以及提交后展示树校验冲突返回 `422 commandRejected`，分别使用 `SourceMissing`、`ClipExists`、`TargetStale`、`InvalidRange`、`DiscontiguousRange`、`UnsupportedSource`、`ContainerInvalid`、`ClipScheduleConflict` 或 `TargetConflict` 等稳定 `failureCode`；整批候选在拒绝时不写入。删除成功后成员回到来源默认容器的核心语义由领域编辑器负责。

成功回执在 `ProjectCommandResponse.clipResult` 返回 `branchId`、`clipId`、可选 `name`、完整 `lineIds`、`containerId` 与 `changed`。创建、改名、移动和删除的真实变化各产生一个工程修订／历史步骤，标记 dirty，清理 redo，`changeSet.areas` 为 `orchestration` 与 `renderInputs` 并要求完整刷新；同值改名或同容器移动为 no-op，保留修订、历史、dirty、redo 与事件语义。删除不返回残留片段投影；客户端应重新读取精确修订的编排读模型。

本合同新增四个命令 wire 分型、`clipResult` 及 capabilities 中的四项命令能力：`project-sessions.commands.orchestration.createClip`、`project-sessions.commands.orchestration.renameClip`、`project-sessions.commands.orchestration.setClipContainer`、`project-sessions.commands.orchestration.deleteClip`。不新增路由或扩大 `GET .../orchestration` 的资源闭包。完整片段作者包、leadIns、公共 package、媒体／字体闭包、最终渲染外观与浏览器 W4 验收仍不在本切片。

<a id="g20-structure-0910"></a>

### 13.11 G20 有限节点结构编辑（合同1.20.0）

在既有 `POST .../commands` 新增六种严格命令，共用顶层精确修订、回执、身份与 Writer 门禁。ID 均为非空白稳定字符串，最多128个 UTF-16 code units；名称字段不作 trim 或额外长度限制。请求只接受本 type 的业务字段，拒绝未知字段、重复字段、缺少必填字段和错误 JSON 类型。

| type | 必需业务字段及语义 |
|---|---|
| `orchestration.setNode` | `nodeId/name`；仅改现有节点名称，name 必须显式出现，null 清除名称 |
| `orchestration.reparent` | `nodeId/parentId`；parentId 必须显式出现，null 表示树根层；由 Core 保持世界外观并更新必要局部几何 |
| `orchestration.reorderChildren` | `parentId/orderedIds`；parentId 可显式 null 指树根层；orderedIds 必须完整包含该父节点全部直接子节点各一次 |
| `orchestration.addSlot` | `containerId/slotId/name`；name 必须显式出现，可为 null；只能向现有 SlotSequence 容器追加空槽位，不隐式修改容量 |
| `orchestration.removeNode` | `nodeId`；删除节点及其子树，仍有程序根、默认归属、片段或前奏引用时拒绝，不隐式改绑 |
| `orchestration.reorderSlots` | `containerId/orderedIds`；仅重排现有 SlotSequence 的全部槽位各一次 |

两种 orderedIds 均为1..2000个唯一稳定 ID。子节点显示顺序与槽位轮换顺序分别由两种命令控制；reorderChildren 不修改槽位轮换或媒体绘制顺序。重挂拒绝不存在的父节点、循环、不可逆父变换和非法槽位归属，移动槽位时由 Core 同步两边槽位引用；所有候选须通过完整展示树校验，不允许半批提交。节点不存在返回 `422 commandRejected`／`TargetStale`，新增槽位身份重复返回 `IdentityConflict`；其他 Core 结构拒绝返回 `PresentationInvalid`。非法 ID／数组输入返回 `400 invalidRequest`；重复数组成员由 Hosting 拒绝。

成功回执新增可选 `structureResult`：`operation` 是不带 `orchestration.` 前缀的六种操作名，`changed` 表示实际变化，按操作返回 `nodeId`、`parentId`、`containerId` 或完整 `orderedIds`；null 响应槽省略，根层 parentId 因此省略。真实变化一次修订／历史，标记 dirty、清理 redo，`changeSet.areas` 为 `orchestration`／`renderInputs` 且要求完整刷新；相同名称、相同父级等由核心识别的 no-op 保留修订、历史与 redo。客户端重新读取精确修订的 orchestration 投影获取完整结构。

capabilities.features 分别公布 `project-sessions.commands.orchestration.setNode`、`reparent`、`reorderChildren`、`addSlot`、`removeNode`、`reorderSlots`（后五项采用相同完整前缀）。不新增路由；`setNode` 不接受 placement、referenceRect、layout 或 content。本节六命令不含创建/复制；后续有限几何/布局/动画与手工前奏见§13.13～13.16，三种有限结构创建及同父复制见§13.17。通用节点JSON、完整作者包及完整W4仍独立。

<a id="g21-layout-placement-0910"></a>

### 13.12 G21 内部布局默认位置（合同1.21.0）

在既有 `POST .../commands` 新增 `orchestration.setLayoutDefaultPlacement`。命令必须显式提供非空 `containerId` 与 `placement`；`placement` 可为完整对象（`x`、`y`、`anchor`、`rotationDegrees`、`scale`）或显式 `null`，后者清除已有的内部布局默认位置。目标必须是当前工程已有、带布局的歌词容器，所有数值须有限且 `scale` 大于0；非法目标或值在提交前拒绝。

成功变化只提交一次历史／修订，标记 dirty、清理 redo，并在 `changeSet.areas` 返回 `orchestration` 与 `renderInputs`；相同值（包括重复清除）是 no-op，保留修订、历史与 redo。回执使用 `layoutResult`，返回 `containerId` 与 `changed`。编排读模型的 `layout.defaultPlacement` 反映精确修订下的值，可为 null。

G21-A 单独只编辑已有容器的内部布局默认位置；合同1.22.0新增的外层几何、布局/动画、固定参考、手工前奏和单句局部微调见§13.13～13.16。三种有限初始结构创建与同父复制见§13.17；通用节点JSON、完整编排作者与浏览器W4验收仍独立。

<a id="g21b-orchestration-properties-0910"></a>

### 13.13 G21-B 有限几何、布局与动画属性（合同1.22.0）

沿既有 `POST /api/v1/project-sessions/{id}/commands`，新增以下 strict 分型；每种只接受表内业务字段及自身type，不接受继承自CLR的target/startUs/endUs/analysisJobId（即使null）。外层仍要求expectedRevision与requestId/hostInstanceId/validUntilUtc。

| type | 必填业务字段与完整替换语义 |
|---|---|
| `orchestration.setNodeGeometry` | `nodeId`、完整非null `placement`、完整非null `referenceRect`；修改已有节点外层几何 |
| `orchestration.setLayoutSettings` | `containerId`、完整非null `safeArea`、`maxWidthRatio`；保留布局类型与槽位身份 |
| `orchestration.setSlotAnimation` | `containerId`、显式 `slotAnimation`；完整对象或null |
| `orchestration.setProgramAnimationPolicy` | `branchId`、显式 `animationPolicy`；Karaoke/Static/FollowPrimary或null |

placement完整五字段为 `x/y/anchor/rotationDegrees/scale`：四数值必须有限，scale>0，x/y允许画布外；anchor只接受Center、TopLeft、TopCenter、BottomRight、LeftCenter、RightCenter六个命名枚举（命名不区分大小写，canonical响应使用标准名称）。referenceRect完整widthRatio/heightRatio必须有限且>0，以父参考矩形为基准；不把1当作比例上限。safeArea完整left/top/right/bottom均有限、0..1且right>left、bottom>top；maxWidthRatio有限且0<值≤1。

slotAnimation对象必须显式提供全部十四字段：revealMode、revealProgress、revealDelayUs、revealLeadUs、maxVisibleLines、conflictPolicy、retentionPolicy、enterDurationUs、exitDurationUs、enterFrom、hold、exitTo、exitEasing、easing。revealMode为Progress/TimeAfterPreviousStart/LeadBeforeNextStart；revealProgress为0..1；四时间为非负十进制Int64微秒字符串；maxVisibleLines为正Int32；conflictPolicy为QueueUntilSlotFree/AllowOverflow/ReplacePrevious/Skip，retentionPolicy为UntilLineEnd/UntilNextReveal/Immediate，两个easing均为Linear/EaseIn/EaseOut/EaseInOut。enterFrom/hold/exitTo各包含完整opacity、scale、offsetXPx、offsetYPx、rotationDegrees：opacity在0..1，scale>0，全部有限；像素偏移和旋转无画布范围限制。未知/重复/缺失/null字段拒绝，命名枚举按标准名称canonical化；动画不保存槽位位置。

null slotAnimation只能清除普通布局配置，SlotSequence的完整领域校验仍要求动画及有效槽位；null animationPolicy恢复按来源角色解释。输入错误400 invalidRequest；不存在节点/程序或非布局目标422 commandRejected、failureCode=TargetStale；完整展示校验失败422、PresentationInvalid。稳定ID非空白且≤128字符。成功提供propertiesResult（operation、可选nodeId/branchId、changed），只返回实际目标；canonical值通过精确修订orchestration读取。真实变化一次历史/修订及orchestration/renderInputs完整刷新，no-op保redo；提交前验证完整编排读模型与完整回执预算，超限413 resourceTooLarge且不改工程。capability为 `project-sessions.commands.orchestration.` 后接表内四动作。

<a id="g22-fixed-reference-0910"></a>

### 13.14 G22-A 单节点固定容器参考（合同1.22.0）

`GET /api/v1/project-sessions/{id}/orchestration/geometry` 必须且只能唯一提供expectedRevision和nodeId；查询名称大小写敏感，缺失/重复/未知query为400 invalidRequest field=query。expectedRevision为当前工程修订的非负Int64十进制字符串；修订不匹配409 revisionConflict。nodeId必须非空白且1..1024 UTF-8字节，非法为400 field=nodeId。沿既有client授权、工程归属与Writer一致读取门禁；read capability为 `project-sessions.orchestration.geometry-reference.read`。

响应含projectSessionId/projectRevision/nodeId，可选parentId（根节点省略）、canvasWidth/canvasHeight、reference、parentReference，以及固定referenceKind="fixedContainerReferenceV1"、finalPixelGeometry=false、protocolVersion="1"。reference与parentReference均含width/height、worldMatrix（a/b/c/d/tx/ty）、pivot（x/y）、恰好四个worldCorners（左上、右上、右下、左下）。矩阵采用x'=a*x+c*y+tx、y'=b*x+d*y+ty，父矩阵乘子矩阵；根节点parentReference是画布宽高、单位矩阵和原点pivot。复用Core SceneTreeGeometryResolver，兼容缺省来源程序投影且不写工程。

固定参考不包括文本测量、自动布局、动画、效果与SceneEditorGeometry，也不是最终Runtime像素边界。输入最多1024节点（包含补齐的默认节点）、父链深度64、20000输入项、65536累计ID UTF-8字节；结果沿1MiB/20000读模型预算，不截断。缺失目标404 resourceNotFound field=nodeId；坏树、重复ID/缺父/循环、无效或不可逆数值422 commandRejected field=geometry、failureCode=GeometryReferenceInvalid；输入预算超限413 resourceTooLarge field=geometry、GeometryReferenceBudgetExceeded。完整树校验与预算先后顺序沿源码，不能把任意坏工程都解释为目标不存在。

<a id="g23-lead-ins-0910"></a>

### 13.15 G23 手工前奏与提示容器（合同1.22.0）

新增四种严格命令，capability为 `project-sessions.commands.orchestration.` 后接动作；沿既有精确修订、单写者、回执owner和一次历史门禁。

| type | 全部必填业务字段 |
|---|---|
| `orchestration.createCountdown` | `parentId/name/count`；name可null，count为1..64整数 |
| `orchestration.createLeadIn` | `branchId/leadInId/name/targetLineId/indicatorContainerId/durationUs/enabled`；name可null |
| `orchestration.updateLeadIn` | 与createLeadIn相同，完整替换可编辑字段 |
| `orchestration.deleteLeadIn` | `branchId/leadInId` |

不接受未知/重复/缺失字段或无关继承槽。稳定ID非空白且≤128字符；durationUs为正Int64微秒字符串，enabled为布尔。createCountdown的parentId必须为已有普通组（无Layout/Content）；Core分配真实稳定容器/标记ID，名称null时用原默认提示名。前奏必须引用该来源真实歌词行、同程序根下的CountdownSequence容器；createLeadIn的leadInId不得与工程内既有前奏重复。新建为Manual来源，修改真实变化按Core规则转Manual，不开启自动生成设置；删除只删除前奏，保留提示容器和自动生成设置。同值修改为no-op，不追改自动来源。

成功leadInResult含operation、changed和实际有效身份：createCountdown返回indicatorContainerId与markNodeIds；前奏操作返回branchId/leadInId/indicatorContainerId，null字段省略。orchestration.programs始终返回leadIns数组，每项保id、可选name、enabled、targetLineId、indicatorContainerId、durationUs、origin；可计算时间时提供startUs/endUs，否则省略，时间来自Core LeadInTiming.Range。nodes为CountdownSequence投影contentKind=countdownSequence及content.markNodeIds/attachmentMode；TextMark投影contentKind=textMark及content.text，不开放任意内容JSON。layout.slotAnimation始终返回完整对象或显式null。

不存在来源422 SourceMissing，重复前奏身份422 IdentityConflict，缺失目标行/前奏422 TargetStale，父组或提示容器不合法422 ContainerInvalid，时间冲突422 LeadInTimeConflict，其余完整展示校验失败422 PresentationInvalid，均使用commandRejected；格式错误400 invalidRequest。单command canonical JSON≤16KiB，完整候选编排读投影/回执仍受共同预算，超限413 resourceTooLarge且整批不变。真实变化一次历史并刷新orchestration/renderInputs；no-op保redo；不修改歌词源时间。

<a id="g24-line-local-transform-0910"></a>

### 13.16 G24 单句局部微调（合同1.22.0）

`GET /api/v1/project-sessions/{id}/orchestration/line-local-transform` 必须且只能唯一提供expectedRevision、branchId、lineId，名称大小写敏感；未知/重复/缺失query返回400 invalidRequest field=query，精确修订不匹配409 revisionConflict。两个ID必须非空白且≤128字符；来源/行归属不存在422 commandRejected field=lineId、failureCode=TargetStale。响应含projectSessionId/projectRevision/branchId/lineId/transform/protocolVersion，transform必须存在，对象或显式null，null表示未覆写；不虚构缺省变换对象。

`orchestration.setLineLocalTransform` 必须提供branchId、lineId与显式transform，transform为完整offsetXRatio/offsetYRatio/rotationDegrees/scale对象或null清除。所有数值有限、scale>0；偏移按当前父参考宽高比例，旋转单位度，比例偏移允许画布外。拒绝未知/重复/缺失/无关字段。成功lineLocalTransformResult返回branchId/lineId/transform/changed，其中transform始终保留，即使null；真实变化一次历史及orchestration/lineLocalTransform/renderInputs刷新，同值/重复清除no-op保redo且不物化隐式程序。不修改源歌词或时间，不表示最终帧几何。完整展示校验失败422 commandRejected、PresentationInvalid；完整读取与回执沿共同预算。

capability为 `project-sessions.orchestration.line-local-transform.read` 和 `project-sessions.commands.orchestration.setLineLocalTransform`。本节原1.22.0微调批次不包含批量微调、完整行覆写读模型或最终Runtime像素；当前行展示继承读取见§13.18、真实同帧几何与PNG见§13.21。浏览器W4、任意完整行覆写和完整B3/B4验收仍独立。

<a id="g20b-node-creation-0930"></a>

### 13.17 G20-B 有限节点创建与子树复制（合同1.23.0）

既有 `POST /api/v1/project-sessions/{id}/commands` 新增两个严格分型，累计64种工程命令；不新增路由。两种命令的全部业务字段均须显式出现，缺失与显式null不同，拒绝重复/未知/继承的无关字段。`kind` 大小写精确；输入身份为1..128个UTF-16代码单元的非空白字符串，名称为最多256个UTF-16代码单元的字符串或显式null。JSON Schema的字符串code-point长度上限只是必要条件，实际UTF-16门禁由服务执行。

| type | 必填命令体字段与语义 |
|---|---|
| `orchestration.createNode` | `kind/parentId/name/slotCount`；kind仅`group`、`auto`、`slotSequence`。group/auto的slotCount必须显式null，slotSequence必须1..64整数。parentId:null表示画布根层，非null必须指向已有普通组（Layout与Content均null）。 |
| `orchestration.copySubtree` | `nodeId/name`；nodeId指向已有节点，同父复制；name:null清除副本根名称，后代名称保留。不接受另一个父目标或客户端生成的新ID。 |

创建由Server生成新身份并复用Core `SceneTreeEditor.TryAddNodes`。节点采用 `ScenePlacement.Default` 与 `SceneReferenceRect.Canvas`；group无布局/内容，auto使用默认 `LyricContainerLayout`（真实Kind为Auto），不自动绑定歌词程序。slotSequence在同一次候选中生成根及全部空槽位，设置SlotSequence布局、顺序槽位ID及默认SlotAnimation，`maxVisibleLines = min(2, slotCount)`。64只限制本命令初始slotCount，不是已存序列的全局槽位上限；后续几何、布局/动画、槽位和分配沿已有命令配置。

复制复用Core `SceneTreeEditor.TryCopySubtree`：全部节点生成新身份，保留几何、布局、内容等完整节点值；内部parent、`Layout.SlotIds`及倒计时 `MarkNodeIds`重映射到副本。复制单个槽位或mark时，按Core原语义把副本追加到外部owner的槽位/mark引用序列；复制包含owner的完整子树时只重映射副本内部引用。候选仍须通过统一展示校验；不复制歌词、来源程序、默认容器归属、片段、前奏或预设分配绑定，也不修改源时间。

成功回执复用 `structureResult`：`operation` 为 `createNode` 或 `copySubtree`，`nodeId` 为新根，`parentId` 为实际父节点（根层null时省略），`changed:true`；省略containerId/orderedIds。新增可选 `createdNodeIds`，两种新操作必返回完整唯一新增身份，根第一、其余按候选新增顺序，最多128项；不提供旧新映射。旧六种结构命令继续省略该字段。每次成功创建/复制真实提交一次修订和历史，标记dirty并清redo，要求完整刷新orchestration/renderInputs；重放同一有效回执返回原新增ID，undo/redo恢复同一已接受身份。

准入先约束源，再形成完整候选，在一次提交前验证全部预算与Core展示规则：

- canonical command JSON最多16KiB；源树及完整候选树各最多2000节点、深度64（画布直接子节点为第1层），复制完整子树最多128节点。
- 源/候选验证输入各最多20000项，计入节点、程序、引用、覆盖、歌词branch/line/token及程序关联遍历项；累计读取的身份/名称/标记文本最多1048576个UTF-16代码单元。长歌词即使编排节点很少，也可能触发本批有限额度并返回413。
- 完整编排输出独立受1MiB JSON字节/20000项预算；实际配置的完整成功回执预算同样在提交前预检。不能用输入字符数或副本根数量替代完整候选/输出/回执预算；超限整批拒绝，不截断、不部分提交。

非法wire形状返回400 `invalidRequest`；合法源树下缺失源节点或父节点为422 `commandRejected`／`TargetStale`，父目标不是普通组为422 `ContainerInvalid`，非法源树或候选展示关系为422 `PresentationInvalid`。预算超限为413 `resourceTooLarge`。鉴权、client/session归属、精确修订409、回执及MutationBlocked沿共同门禁，失败保持工程、修订、历史和redo。

capabilities分别公布 `project-sessions.commands.orchestration.createNode` 和 `project-sessions.commands.orchestration.copySubtree`。本节1.23.0原批仅交付三种有限初始结构与Core已有同父复制；mark编辑、附着切换、前奏自动生成和行进入动画的当前接口见§13.18～13.21。通用节点JSON、任意内容/动画轨道、完整编排作者、浏览器W4、公共package、完整B3/B4/B5/B6及原全量/性能/长片/GUI/平台矩阵仍独立；真实Worker像素只按相应测量/预览证据认定。


## 13.18 布局类型与单句展示继承（合同1.24.0）

新增严格工程命令均要求完整业务字段，拒绝未知/重复字段，包括旧基类的无关null字段；外层仍含精确`expectedRevision`和回执身份。真正变化原子提交一次历史，no-op保redo。G20原创建、复制、删除、改名、父子和槽位顺序命令继续使用原语义。

- `orchestration.setLayoutKind`：显式`containerId/kind/slotIds/newSlotCount`。kind严格为`Auto`或`SlotSequence`，与createNode的小写kind不同。Auto要求空slotIds、newSlotCount=0；SlotSequence要求唯一现有直属纯节点与新槽位合计1..64，新槽位由Server生成身份。保留原节点、几何、安全区、最大宽度和默认位置；Auto移除布局槽引用和slotAnimation，原节点仍保留。候选完整验证后提交。
- `orchestration.setLineEnterAnimation`：显式`branchId/lineId/animation`；`orchestration.setProjectEnterAnimation`：显式`animation`。animation为完整五字段`durationUs/easing/startOpacity/startScale/startOffsetPx`或null。时长0..10000000微秒字符串，允许原解析接受的符号和前导零并canonical化；easing严格`Linear/EaseIn/EaseOut/EaseInOut`，opacity0..1，scale>0且≤4，offset绝对值≤10000画布像素，全部有限。
- 行animation:null清除局部覆盖并继承工程；工程animation:null关闭默认。0时长/不透明度1/缩放1/偏移0仍是明确局部覆盖。已有`setLineLocalTransform`继续设置/清除本句位移、旋转和缩放。
- `GET /api/v1/project-sessions/{id}/orchestration/line-presentation`严格唯一提供`expectedRevision/branchId/lineId`，拒绝其他query。返回`localTransform/localEnterAnimation/effectiveEnterAnimation`，三个字段始终出现，可显式null；`animationSource`为`line/project/none`。这是静态继承读模型，同帧实际几何使用§13.21。

编排根投影始终返回可空`projectEnterAnimation`，包括零行工程。三种新命令复用`propertiesResult`，canonical状态由精确修订GET读取。输入command≤16KiB；源/候选2000节点、深64、验证输入20000项/累计字符1MiB；完整编排读取1MiB/20000项，实际完整回执默认64KiB，全部提交前检查。ID为非空白且≤128 UTF-16 units。目标缺失422 TargetStale、错误容器422 ContainerInvalid、候选非法422 PresentationInvalid、预算413 resourceTooLarge；失败不改变工程。

## 13.19 前奏生成、mark与附着（合同1.24.0；1.54.0 扩展图形／SVG／动画与首字上方附着）

每个program始终包含可空`leadInGeneration`。完整settings为`includeOpening/includeLongGaps/gapThresholdUs/durationUs/indicatorContainerId/excludedLineIds`六字段：gapThresholdUs非负、durationUs正Int64微秒字符串，indicatorContainerId必须显式给出，可null；排除行ID≤1000、唯一且属于来源。沿原时间解析canonical化，分析以歌词时间和起唱锚点为依据。

| 命令 | 完整业务字段与行为 |
|---|---|
| `orchestration.setLeadInGeneration` | `branchId/settings`；settings:null清除设置；修改设置本身不自动生成 |
| `orchestration.previewLeadInGeneration` | `branchId/settings`；settings非null，只读分析，保持修订、dirty、redo |
| `orchestration.applyLeadInGeneration` | `branchId/settings/selectedLineIds`；selectedLineIds必须显式数组，允许空数组，按当前精确修订重新分析并只应用明确选择；不以省略表示全选 |
| `orchestration.deleteLeadInAndExclude` | `branchId/leadInId`；删除并排除该目标；原deleteLeadIn仅删除并保持原设置 |
| `orchestration.addMark` / `setMarkText` | 分别`containerId/text`与`nodeId/text`；text非空白且≤4096 UTF-16 units；新增身份由Server生成 |
| `orchestration.reorderMarks` / `spaceMarks` | 分别`containerId/orderedIds`与`containerId`；完整mark顺序、唯一成员，单容器最多64个mark；均调用原Core规则 |
| `orchestration.setCountdownAppearance` | `containerId/appearance`；四字段两态`typography/pending/elapsed/opacity`（历史六字段已随1.50.0样式改版统一） |
| `orchestration.setCountdownPresentation` | （1.54.0）`containerId/shape?/animation?`（至少一项非null，命令JSON上限128KiB）；`shape`为`CountdownShapeSpec`（`kind`: `Text|Circle|SteppedBars|Capsule|Diamond|Star|CustomSvg`，`barMinHeightRatio`: `0.15..1`，`svgXml`仅`CustomSvg`非空白静态安全内联SVG且UTF-8≤64KiB，否则为`null`，`svgColorMode`: `SilhouetteStyle|OriginalColor`）；`animation`为`CountdownAnimationSpec`（`mode`: `DiscreteSteps|BlinkThenWipeFill|BlinkThenErase`，`blinkRatio`: `0..0.85`，`blinkCount`: `0..16`且非`DiscreteSteps`并`blinkRatio>0`时`>=1`，`featherRatio`: `0..0.5`） |
| `orchestration.setMarkAppearance` | `nodeId/changes`；完整本层override数组，空数组清除；≤34种静态属性（不含wipe.*、scale、verticalCenterRatio、horizontalAlignment），拒绝重复property |
| `orchestration.setLeadInAttachment` | `branchId/leadInId/containerId/mode`；mode为`FixedParent`、`CueTargetText`或`CueTargetAboveStart`（1.54.0新增首字上方对齐并避让注音），容器须为该前奏提示组；真实Worker测量目标文字参考（含双锚点与容器局部安全区退让），转换位置后在同一修订提交；缺真实参考明确失败；相同mode为no-op，不要求Worker或再次文字测量，保redo |

合同1.24.1修正附着错误定位：精确修订检查优先；来源不存在返回422 `commandRejected`、`failureCode:TargetStale`、`field:command.branchId`，此前奏不存在于指定来源时为`TargetStale`／`command.leadInId`；容器不存在、不是倒计时提示组或与该前奏关联不符时为`ContainerInvalid`／`command.containerId`。拒绝保留工程、修订、dirty、历史和redo，并释放测量名额；同意图重试仍取原回执。

`leadInGenerationResult`完整返回operation/branchId、可用settings、全部candidates、diagnostics、selectedLineIds与changed。每候选含targetLineId/status/reason以及可用的existing/proposed完整前奏、startUs/endUs、anchor；status为New/Update/Remove/Preserved/Skipped，reason为None/MissingLineTime/UnknownPrecedingTime/TooShort/NotEligible/ExistingManual/Disabled/Excluded/TimingConflict。可选数据不可用时按普通响应省略，不能用空成功替代分析错误。预览与应用的完整候选均不截断；analysis最多1000来源行，完整回执超限在提交前413。

普通mark/图形动画/外观操作复用`leadInResult`；附着使用`propertiesResult.operation=setLeadInAttachment`。内容投影提供提示组完整`appearance`、`shape`、`animation`、mark完整`appearanceOverride`及`attachmentMode`（读模型兼容保留历史字符串值）；样式新输入沿原字体名256/fallback32/渐变256规则，拒绝包材质，普通Node静态预设见§13.20。`orchestration.setCountdownPresentation` 单条command≤128KiB（SVG XML ≤64KiB UTF-8），其余本节command≤64KiB；源/候选2000节点/深64/20000验证项/1MiB累计字符，完整读取1MiB/20000项，默认完整回执64KiB；任何失败均原子不变，不截断。自动生成的前奏ID沿已有Core规则，deleteAndExclude及attachment容许≤4096字符身份，普通branch/node/line为≤128。

## 13.20 历史：Node静态预设目标（合同1.24.0）

本节保留原静态切片；当前高级Node与clips扩展见§24.4，以下原静态限定不再作为全局模板准入。

既有presetApplications.apply与候选试看共用严格目标union，新增`{kind:"node",nodeId:"服务返回的真实节点身份",includeDescendants:true|false}`；nodeId非空白≤128 UTF-16 units、includeDescendants必须显式bool。Node可为已有真实节点，包括mark；继承是否覆盖后代由includeDescendants明确决定。project/branch/line/token沿原目标语义。所有kind仍接受已知但无关构造字段显式null（branchId/lineId/tokenId/nodeId/includeDescendants），拒绝跨kind非null、未知字段和重复key。

Node apply/update/候选仅接受完整静态模板，拒绝效果recipe、package、source材质与非空branchInputs；token-kind模板不能用于Node。成功沿既有应用身份、保存/重开、撤销/重做和候选资源生命周期。现有静态预设snapshot/profile继续承载Node，不新增公共package profile。所有preset命令完整意图≤64KiB，候选targets+branchInputs≤64KiB，完整新preset-applications投影≤1MiB/20000项，完整回执提交前检查。

## 13.21 真实Worker同帧测量（原合同1.24.0，本轮统一输入扩展）

`POST /api/v1/project-sessions/{id}/orchestration/measurements`不接受query，普通工程请求body包含六个显式字段：`expectedRevision/mode/timeUs/width/height/alphaMode`。revision为canonical非负Int64字符串；mode严格time或layout；time模式timeUs为canonical非负Int64字符串，layout模式必须显式null。width1..1280，height1..720；alphaMode为opaque或transparent。拒绝符号、前导零（单独"0"除外）、缺字段、额外字段和重复key。测量编辑候选时另提供成组的 `previewId/expectedPreviewGeneration/candidateRevision`，返回对应candidate修订；缺省测当前工程。本请求不携metadata，不产生回执、持久job、preview session或资源句柄，可按同一修订重试。

固定精确工程修订后，由受管理Worker复用真实Runtime/Skia场景采样、文字整形和渲染；同一次原准备帧产生geometry和PNG，不能以固定矩形推导替代文字测量。`measurement`包含原sourceSha256、源+采样说明的sampleInputFingerprint、sourceProfile、samplingProfile、mode、始终出现的可空timeUs、画布/输出尺寸、alphaMode、elements、pngSha256和base64字符串png。执行profile为scene-measurement-v1，sampling为time-v1或layout-reference-v1。来源由完整固定快照选择三静态、直接包、包与预设混合或统一 `runtime-project-input-v1`；实际Worker须握手公布对应来源及匹配引擎。Image/Video与包字体通过§23固定资源消费，Host用同一受信snapshot纯校验完整wire及回执geometry身份，不重新打开Worker资源目录。Runtime Error仍拒绝本次测量像素发布。0.112.86／合同1.51.0 起imageEffect/material兜底warning不再拒绝：测量与预览同样兜底，`measurement.fallbackDiagnostics` 始终出现，列出本帧去重后的兜底诊断（形状同§11.3，公共wire省略null，最多128项／96KiB），超过预算时本次测量以 `SceneMeasurementBudgetExceeded` 失败、不截断；没有这类依赖时为空数组。实际证据按本轮代表流程登记。

每element为container/slot/line/ruby/mark之一，提供实际身份与父子/歌词关联、representative/visible/opacity、geometryStatus（notVisible/emptyReference/measuredInk）、worldMatrix/pivot/worldCorners/fixedReference/editReference。visible表示当前scene内容装载，opacity为动画opacity、独立于样式；worldCorners为实际ink角点集合，容器/组可含多组后代四角，无ink为[]；fixedReference为持久固定框，editReference为真实父编辑参考，附着组使用目标文字基底。四个可空几何对象始终出现；无当前文字时明确没有ink，不能将固定参考框当glyph。SlotSequence行的slotId为稳定持久槽节点身份，等于其parentId，不传内部调度器的压缩role编号。坐标保持原画布坐标，PNG按请求尺寸输出。

layout模式为Auto、全部槽位及提示mark构建代表试样；representative明确标记合成行，不覆盖原歌词。代表行不应用原来源行/token局部覆盖，保留工程/来源/Node静态预设继承；time模式按真实指定时间采样。代表试样与真实时间测量分别以mode/samplingProfile和fingerprint区分。

0.112.90／合同1.52.1的布局参考修复：布局代表行使用同一不可变试样快照的样式解析器，不再到原工程歌词中查找临时 `__layout_reference__` 行；同一源快照复用同一布局试样。只有严格匹配本次试样身份的行进入该解析路径，普通未知行继续按原规则拒绝。此缺陷在不含失效预设的工程同样存在，与失效预设运行筛选分别验证；HTTP形状、layout-reference-v1采样说明与原真实行样式继承规则保持。

全局2/client1并发，20秒期限并排水；进行中测量保护client回收，Registry shutdown拒绝新测量、取消并等待全组排水；没有受管理能力或旧Worker明确taskUnavailable，不回退Host native。源持久节点≤1024/深64，真实源行≤2048，definition（节点+行+ruby）≤4096，正文+ruby累计≤200000字符，layout样本≤512，完整elements≤4096、全部corners≤20000；含metadata的Worker输入≤4MiB，完整HTTP响应≤2MiB。任何超限整体413，无截断。PNG/SHA与真实几何、附着转换、取消/期限及真实Worker兼容必须由独立运行验收证明；schema正反例只证明wire形状，不代表浏览器或完整B3/B4/B5/B6验收。


<a id="basic-business-0950"></a>
## 17. 基础工程业务（0.95.0／合同1.25.0）

本节按本轮实现固定行为，验收状态见[实施§58](../backend-extraction-progress.md#basic-business-0950)；版本和冻结统计以生成合同为准。新增工程写入共用 `POST /api/v1/project-sessions/{id}/commands`，沿用精确 `expectedRevision`、client receipt、唯一Writer、一次历史、no-op保redo和64KiB完整回执保护。评估、浏览、导出和下载不推进工程历史。所有内容URL仍须认证，不含凭证或Host路径。

### 17.1 授权单层目录

`GET /api/v1/file-roots/{rootGrantId}/entries?relativePath=&offset=0&limit=100` 使用启动器已有root grant；授权根的发现仍沿现有file-roots读取，不新增另一套授权。空relativePath表示根本身，非空路径沿既有安全相对路径规则，长度≤2048。只列直接子目录，Ordinal名称排序；`isProject`仅表示存在普通工程入口文件，不载入工程、不探测媒体。返回rootGrantId/relativePath/offset/totalCount/nextOffset/entries，每项含name/relativePath/isProject。

每页1..100，单层扫描上限4096（包含未返回的普通文件）。链接或链接工程入口、越界、未授权根、不可访问目录整体拒绝；超限429 `resourceQuotaExceeded`，不返回截断列表。offset不得超过当前目录数量。分页是每次独立枚举的offset分页，不持有文件系统快照；目录变化时客户端应从第一页刷新，不能将多页视为同一目录快照。它不授予新根权限，打开/保存仍取得原目录句柄。

### 17.2 工程改名与资源清单

`project.rename` 的command字段为 `type/name`。name为非空白、原输入≤256 UTF-16 units，提交值为Trim后名称；同名no-op。只改显示名称，沿settings/summary、保存重开与undo/redo，不移动工程目录。

`GET /api/v1/project-sessions/{id}/resources?expectedRevision=R&offset=0&limit=100` 返回捕获修订的工程实际引用资源，按resourceId进行Ordinal排序、去重并合并roles。覆盖当前音频、已有背景声明、已采用分析、工程效果包manifest/文件和预设快照及其包；不混入无关上传，不执行包或枚举字体。每项含resourceId/kind/roles/status及适用的sizeBytes、sha256、assetId、contentUrl、preparationStatus、waveformStatus。sizeBytes使用十进制字符串；未证明的事实省略。embedded、missing、noStream、failed、identityUnavailable与available等状态说明当前可见事实，不能以声明存在推定渲染闭包已支持。

直接效果包在清单中展开为两类只读资源：manifest 项的 `kind` 为 `effectPackageManifest`，文件项的 `kind` 为 `effectPackageFile`。manifest 的 `resourceId` 为 `packageManifest:{packageRevision}`，文件项为 `packageFile:{packageRevision}:{packagePath}`；两者均提供 `packageId/packageRevision`，manifest 还提供固定 `packagePath:"manifest.json"` 与 `packageRole:"manifest"`，文件项提供包内逻辑 `packagePath` 与实际 `packageRole`。`roles` 仍按工程引用来源合并、去重并以 Ordinal 顺序输出。包资源不提供 `assetId` 或 `contentUrl`，该清单不会授予包文件下载权限。

仅 manifest 项提供 `packageClosure`：它包含 `manifestSizeBytes`、小写64字符 `manifestSha256`、`totalSizeBytes`、`fileCount`、`validatedFileCount`、`unsupportedFileCount` 与 `status`。这些大小均为十进制字符串；`status` 只有 `validated` 或 `unsupported`。`validatedFileCount` 表示当前可识别 role 的文件数，不能单独解释为所有身份、引用或运行检查均已通过。文件项的 `status` 逐项反映包闭包检查；未知 role 会保留原文件并标记 `unsupported`，不会被静默丢弃。package manifest/file 的资源清单状态与 package validation 的任务准入分开，不能把 `validated` 当成已编译、可执行或可下载。

资源闭包检查会在内存中重建同一包并复核 manifest/files 身份、内容 SHA/长度、包定义引用和路径/内容预算；当前可识别 role 为 `asset`、`csharp`、`graph`、`image`、`shader`、`shaderProgram`、`svg`、`svgResource`。SVG 文件和描述的结构引用纳入闭包观察，完整 SVG 语义与图片解码仍在作者编译及正式运行资源准备边界校验。该检查不编译 C#、不执行作者代码、不解码图片、不打开媒体，也不枚举或授予外部图片、视频、字体路径；因此它不证明字体/媒体闭包、连续预览资源租约、导出、分析或场景测量已经支持。完整闭包先受本节的20000项/1MiB读模型预算，再受 package codec 的源/内容预算；超限整体拒绝，不返回截断清单。

对已通过上述有限检查的包内 manifest/file，可调用 `POST /api/v1/project-sessions/{id}/resources/package-asset`，请求为 `expectedRevision/resourceId/metadata`。服务在当前客户端和精确工程修订下固定包文件字节，惰性复制到现有 `ResourceStore`，返回 `ProjectPackageResourceAssetResponse`，其中包含包身份、逻辑 `packagePath/packageRole`、受控 `asset` 摘要及认证 `contentUrl`。该操作沿客户端 receipt 幂等语义，同一工程、包修订和资源标识复用同一 asset；不接受裸路径、外部文件或未知/unsupported role。返回的 contentUrl 仍要求 Authorization 和当前 client，沿既有 GET/HEAD、Range、ETag、304、读取租约与未引用 TTL 规则；工程关闭、客户端回收或服务停止会释放工程持有的引用，正在进行的读取完成后再按 TTL 清理。该接口只证明固定原始字节可受控读取，不证明图片/视频/字体解码、脚本编译或运行能力。

沿通用分页（每页≤100）与ReadModelBudget：完整闭包先受20000项、1MiB累计字符、1MiB序列化投影约束，再生成所需页；超限整体拒绝。页必须使用同一工程修订，不得拼接不同修订。音频内容仅在授权身份与实际资源可用时提供现有assets认证URL，其他embedded项不暗示新增下载路由。

### 17.3 来源与TTML全文档歌词评估、确认与下载

- `POST .../{id}/lyrics-export-plans`：`expectedRevision/format/options/metadata`，可选`scope`。省略scope或`branch`要求非空`branchId`；`document`仅允许`ttml.project`且禁止branchId（含显式null）。format支持`lrc.plain/lrc.enhanced/ttml.project/ttml.amll`。options沿标题/作者/专辑、LF或CRLF、BOM与毫秒精度门禁。固定branch或完整document不可变快照；document响应branchId显式null、omittedBranchCount=0，branch报告其它来源省略。project profile恢复明确分支集合及私有身份/关联/时间状态/ruby参数，未承载样式仍须完整损失计划；不合成FollowPrimary时间。
- 201评估返回planId、工程/来源/修订、固定选项、encoding、expiresAtUtc、Core完整plan（losses/issues及导出/跳过行数）、additionalLosses、requiresLossAcceptance。补充说明注音、行/元素终点、主行关联和非法时间损失。它不是整个工程的无损导出报告。
- `POST .../{id}/lyrics-exports`：`expectedRevision/planId/acceptLosses/metadata`。计划须属于同client/会话、未过期且修订完全一致；有损失/issue须acceptLosses=true，否则422且无产物。以固定快照生成完整UTF-8内容，发布前复核修订，不重新导出最新工程。成功201返回exportId/planId、来源身份、sizeBytes/SHA、encoding、contentUrl、expiresAtUtc、Core实际report和additionalLosses。
- `GET|HEAD /api/v1/lyrics-exports/{id}/content`：认证读取固定bytes，沿不可变内容ETag/Range规则，返回附件；LRC使用`text/plain`和`.lrc`，TTML使用`application/ttml+xml`和`.ttml`。当前client归属及到期检查独立于URL。纯歌词导出不需要音频Worker，不新增公开job类型。

请求JSON≤16KiB，拒绝未知/重复/缺失字段。选中来源≤10000行、100000元素及对应注音检查、4MiB累计文本；输出≤4MiB，完整报告≤64KiB且受完整receipt预算。计划与产物合计client≤8、全局≤32，准备并发全局2/client1，保留300秒；执行将计划转为固定内容占位，操作并发计入配额。超限明确拒绝，无歌词/报告截断。取消接受等待与已接纳操作沿既有receipt/tracker规则，不宣称独立公开cancel job。

### 17.4 替换/清除工程音频

`audio.replaceAsset` 只接受 `type/assetId`，asset属于当前client，不接受路径。Writer内捕获原工程和revision，Writer外取得源资源租约并经受管理Worker准备，再次核对修订/工程身份后预检完整候选/回执并一次提交。使用既有首音轨、格式、媒体身份及波形预算；不增加PCM出口。坏源、准备失败、并发冲突或关闭不提交候选。

成功替换更新AudioPath/AudioMedia和准备绑定，清除已采用分析；同一当前asset是no-op。`audio.clear` 只接受type，清除音频、媒体声明、分析与波形；已经清空时no-op。两者不移动歌词时间、不采用音频时长、不改outputDurationUs；null仍为未确定。

当前工程与undo/redo保留音频源和准备波形的资源租约，历史切换后同步AudioAssetId/AudioDurationUs/PreparedAudioSourcePath及波形身份；历史不再保留的源释放引用。会话关闭释放全部历史绑定。保存/重开沿现有工程资源闭包与Worker准备，不要求客户端复用旧上传ID；已有预览/任务持各自固定输入租约。history.undo/redo按实际before/after合并audio/audioAnalysis/waveform/timeline/renderInputs/project等受影响区域，保留其他区域及稳定IDs；音频绑定改变时requiresFullRefresh=true，no-op不发变更事件。

### 17.5 完整.mktpl归档传输

传输复用Application `StyleTemplateTransfer`，接受其当前支持的合法完整模板（包括包/配方/Source材质等高级对象），保真传输且不执行、不编译、不安装。高级Line/Token导入后可按 `advancedRuntimeAvailable` 进入既有应用/更新/预览执行；导入不自动修改工程，也不等于高级定义编辑或P4作者/配置库完成。

- `POST /api/v1/template-libraries/{id}/imports/preflight`：`assetId/metadata`，从当前client固定上传解析全归档，201返回preflightId/assetSha256/archiveBytes/expandedBytes/templates/expiresAtUtc，identityPolicy固定 `generateNewIds`。
- `POST .../{id}/imports`：`expectedRevision/preflightId/metadata`。绑定owner/个人库、有效预检和精确库revision，为所有模板生成新ID，返回sourceTemplateId→templateId完整映射；一次CAS写整个归档，不覆盖builtin，不按名字隐式覆盖。解析、预算或CAS失败不得留下部分模板。库更新不产生工程history。
- `POST .../{id}/exports`：`expectedRevision/templateIds/metadata`，明确唯一选择集，捕获库快照并由原传输器生成固定归档。201返回exportId/libraryId/revision/templateIds/sizeBytes/sha256/contentUrl/expiresAtUtc；后续库更新不改变bytes。内置库模板的归档局部ID确定性映射为 `exported:<原builtinID>`（例如 `exported:builtin:line-classic`），响应templateIds仍是原选择ID；个人模板ID原样导出。该映射只作用于归档捕获对象，不修改库及定义/包资源其他内容，不放松Core对个人库保留身份的保护；导入仍为全部模板生成新ID。
- `GET|HEAD /api/v1/template-transfers/{id}/archive`：认证固定归档，附件名MakoKara-templates.mktpl，沿ETag/Range与读取租约。

请求≤16KiB；归档≤256MiB，完整解压≤256MiB、单文件≤64MiB、≤1024条归档项、1..100模板、完整报告≤64KiB。路径穿越、链接、重复/未声明文件及原传输器结构错误整体拒绝。预检/归档client≤2、全局≤8、累计保留预算1GiB，300秒；并发预留计费，读取中保留租约，到期不截断在途读取。模板自身领域预算仍由原codec检查。预检/导出每次使用独有临时目录，Unix权限显式0700（Windows沿继承ACL），归档与原codec的.tmp均在其中；finally仅清理本次自有目录。

### 17.6 最终静态预设样式与来源解释

`GET /api/v1/project-sessions/{id}/resolved-style?expectedRevision=R&branchId=B&lineId=L` 可带tokenId/clipId/nodeId。每字段唯一，拒绝未知字段；单目标line/token读取，不新增mark或批量读取。clip与Node由真实程序和实际成员推导，客户端可选字段只能验证归属，不能任填父链。Context返回branchId/lineId/tokenId/clipId/containerId/nodeId/textNodeId/nodePath/contextKind；tokenId/clipId即使null也出现。

独立Karaoke来源使用真实display schedule推导槽位；其他合法来源按真实容器上下文解析，FollowPrimary不假造独立Karaoke槽位。nodePath包含实际父链与Runtime动态text叶，故容器/slot对文字而言均为祖先，Node应用必须遵守includeDescendants；不能把持久slot当作文字叶。解析复用PresetStyleResolver：工程/branch/Node根到父/clip/line/token的真实静态覆盖与应用顺序，返回canonical style及固定21属性的property/sourcePath。

`resolution=staticPresetHierarchy`、`finalPixelAppearance=false`。来源是Core真实持久路径，不是新造的像素贡献权重；不运行动画、脚本、字体测量或采样材质像素。四层staticHierarchy原义保持。沿节点≤2000/深64与通用完整读投影预算；无效归属/失效引用/超预算整体拒绝。

### 17.7 clip成员扩缩与显式拆合

三个command共用原工程事务。只接受独立计时Karaoke来源，完整连续、唯一且有合法时间的成员；完整候选通过Core展示树、调度、引用、预设读投影和完整回执预算后才提交。意图≤64KiB，lineIds/clipIds≤1000、节点≤2000/深64，沿通用20000项/1MiB读投影预算。

- `orchestration.setClipMembers`：branchId/clipId/lineIds，完整替换成员，保留原clip ID/名称/容器/原调度上下文。新增成员若不在既有scheduleContext内则ScheduleContextConflict；重叠/不连续或非法时间拒绝。涉及相对效果配方的隐式范围改变拒绝。相同成员no-op。
- `orchestration.splitClip`：branchId/clipId/splitBeforeLineId/newClipId。newClipId由调用者显式提供且必须未占用；在非首行成员前一分为二，原身份留左段，新身份用于右段。两段共享原scheduleContext，复用ClipSplitReferences迁移合法引用，并验证所有原成员SlotIndex/RevealTimeUs/HideTimeUs不变；无法迁移或调度改变原子拒绝。不是Server生成clip ID，也不是任意多分区拆分。
- `orchestration.mergeClips`：branchId/clipIds/targetClipId，target必须是选择集成员。同来源、同容器、相同调度上下文、成员并集完整连续才可合并；保留target的身份/名称/配置，以源歌词顺序合成成员。直接clip效果绑定整体重映射去重。所有片段无应用，或每个均有相同snapshot/enabled/branchInputs且无包/Source材质/配方/固定作者身份的纯静态应用时可合并；后者只保留target应用。部分应用、冲突外观或高级固定引用不能择一丢弃，返回ReferenceConflict。

成功 `clipMembersResult` 返回operation/branchId、完整受影响clips、removedClipIds、changed及createdApplicationIds/removedApplicationIds。稳定领域拒绝沿422 commandRejected、field=command和failureCode（例如TargetStale/UnsupportedSource/InvalidRange/DiscontiguousRange/ScheduleContextConflict/ClipScheduleConflict/ReferenceConflict）。重放、undo/redo和保存重开沿原身份及单层history语义。

## 18. TTML 与独立注音时间轴（0.96.0／合同1.27.0）

实施目标与验收边界见[ADR-0233](../decisions.md#adr-0233)及[计划](../backend-ttml-ruby-timeline-plan.md)。本节描述公共行为；构建、HTTP、Worker/PNG及浏览器验收分别登记，合同冻结不替代这些证据。

### 18.1 片段读模型与严格命令

`lyrics/window` 的每个 `rubyAnnotations[]` 保留 `id/baseTokenId/text/language?/relativeScale/gapPx/alignment` 并必返 `segments[]`。每片段为 `id/text/time/timingStatus`；`time`保原始绝对整数微秒端点，HTTP以十进制字符串表示（含超过2^53的值），缺失端点省略。text是片段正文按保存顺序拼接的兼容投影。状态仍为missing/imported/estimated/edited/confirmed/invalid，不从基础元素或行时间补入缺失片段时间。

| 命令type | 必填业务字段 |
|---|---|
| `lyrics.setRubySegmentTime` | `target/startUs/endUs` |
| `lyrics.clearRubySegmentTime` | `target` |
| `lyrics.setRubySegmentText` | `target/text` |
| `lyrics.addRubySegment` | 四级`target/text/afterSegmentId`；afterSegmentId必填可null，新片段无时间 |
| `lyrics.removeRubySegment` | `target`；删除末片段同时移除注音组 |
| `lyrics.reorderRubySegments` | 四级`target/orderedSegmentIds`；完整身份顺序，保各自时间 |
| `lyrics.setRubySegmentTimes` | 1..1000个`changes[{target,startUs,endUs}]` |

四级目标严格包含`branchId/lineId/tokenId/rubyId`，单片段五级再包含`segmentId`。身份非空白且≤128 UTF-16 units，必须逐级真实隶属；不能跨组按全局segmentId命中。未知、重复、缺失、null字段与CLR内部别名均拒绝（只有add的afterSegmentId允许null）；新增/修改片段正文不能是空串。reorder为1..20000个稳定身份，完整集合、唯一性由领域验证；batch重复目标整批拒绝。

七命令分别公布`project-sessions.commands.lyrics.<命令名>` capability，仍须精确expectedRevision及原回执三字段。完整意图≤64KiB，完整最终改变行累计≤16MiB，单行完整投影≤1MiB/20000项；预算同时统计branch、line、token、ruby group及每个segment，提交前验完整回执预算。超限413，领域失败422 commandRejected并带failureCode/field；失败不改revision/dirty/redo，no-op保redo。时间合法区间可空档、重叠和超出基础token/行，不夹紧、不延长行显示窗。

成功可带`rubyResult={changedSegmentCount,target?,selectedSegmentId?,createdSegmentId?,selectionChanged}`。target为四级组目标；selectionChanged=true时使用建议ID，ID省略明确清空选择。批次不伪造单组选择。撤销/重做对实际ruby变化返回对应反馈。changeSet使用`lyricsRubyTiming/lyricsRubyContent`，不把segmentId放入TokenIds；超过16KiB保areas并转完整刷新。旧`lyrics.setRubyText`对同文保持no-op，对已有独立时间或多片段的实际改字明确拒绝`RubySegmentsRequireExplicitEdit`。

### 18.2 TTML导入、完整document导出与报告

上传、创建及再次导入接受`application/ttml+xml`；授权资源、字节和准备预算仍适用。标准TTML父容器、par/seq、begin/end/dur和frame/tick按有效时间计算；可识别AMLL profile使用其绝对时间约定。没有歌唱者或旁轨标记时，外部多个div仍按文档顺序合入一个候选来源，div本身不是工程分支。明确mkg项目profile恢复原分支集合，未知扩展版本、重复身份和坏关联拒绝/诊断。`ttm:agent`与`x-bg`／`x-translation`／`x-roman`在非项目档映射为具名分支并写入报告；无法无歧义表达的结构只诊断，不静默变成普通正文。

再次导入`options.sourceBranchId`可省略但不可null，出现时须非空白≤128字符。单源默认选择唯一来源；多来源文档走`existing`或`newBranch`时须显式选源，否则422 `SourceBranchRequired`，不存在为`SourceBranchNotFound`，field均为`command.options.sourceBranchId`。选定来源经原目标映射写入；省略其它来源与不可恢复跨来源行关联进入报告。`destination.kind=allSources`没有其它字段，把解析文档里每个非空分支追加为新来源：一次编辑、一次撤销，重映射全部分支的行、元素、注音和片段，并重映射本次一起导入的跨分支`PrimaryLineId`。它不替换工程已有来源；导入文档里的主来源角色改为custom，避免出现第二个主歌词。与`sourceBranchId`同时出现为400，field为`command.options.sourceBranchId`。粘贴命令不接受`allSources`。新建工程仍直接采用完整解析文档，不经过这条追加命令。旧的单来源请求保持原语义。

HTTP import仍是显式目标编辑命令。

导出沿§17.3 Plan→显式acceptLosses→认证下载。`scope=document`仅ttml.project，无branchId字段（null也拒绝），保完整分支顺序、主来源、branch role/timingMode、PrimaryLineId、各级稳定ID、独立line.Text、raw微秒与状态、ruby参数及片段顺序。`scope=branch`或省略保持单来源行为，AMLL只用于选定来源交换。计划与导出响应均必返scope及branchId，document的branchId显式null；document不以选定来源冒充完整工程。

未承载的样式、其它profile的身份/状态/ruby参数、未导出分支、跳过或舍入时间必须完整报告；损失未确认不发布内容/句柄。`LyricsExportReportDto`必返`writtenRubySegmentCount/skippedRubySegmentCount/droppedRubySegmentTimeCount`及原计数。官方AMLL TTML parser的消费端限制也进入Plan损失：XML保微秒原值，parser按Math.round换成毫秒（行/元素/注音各按至少一个端点非整毫秒或超出JavaScript Number安全整数毫秒范围的区间计数）；无begin/end的Missing注音文本在XML中保留但parser省略；无时间普通元素也不进入words逐字列表，与timed词混合时不能宣称正文词列表完整，整行无时间仅保行正文；ruby基础元素时间会由有时间注音的最早开始/最晚结束替代，无timed片段时为0..0。不能把XML保真等同外部消费端无损。内容仍为UTF-8、`.ttml`和`application/ttml+xml`，沿原短期owner/ETag/Range/HEAD/到期规则；无新公开job。

### 18.3 持久化、采样与渲染边界

歌词schema v17保存segments并保text兼容投影；仅v16→v17迁移生成稳定、无时间Missing单片段。v1..v15及未来版本拒绝；读取不就地改文件。复制/平移/基础元素结构编辑按原业务规则保关联、时间与稳定身份，无悬空注音。`DiagnosticCode.RubySegmentOutsideParent=57`提示超父区间，但合法独立片段不被自动夹紧。

timed片段按自己半开区间计算progress/current/sung；无独立时间片段逐片沿基础token规则。基础token进度、脚本token事件与行显示窗保持原定义；Preparation待唱清零高亮。注音组聚合整形一次，片段高亮改变裁切而保持几何。静态/package快照、真实Worker和脚本只读projection传递已验证片段数据；公共profile范围仍按既有capabilities，不因此开放完整公共B3/B4、包脚本、作者、浏览器或长片能力。


<a id="ruby-style-01150"></a>

## Ruby 大小与距离（0.115.0／合同1.56.0）

静态样式新增 `ruby.relativeScale` 和 `ruby.gapPx`，通过 `styles.patch`／`styles.restore`、模板属性和完整模板样式支持工程、来源、行、元素层级；标记外观不支持这两项。字号比例须为有限正数，距离须为有限非负数，均不超过 float 最大值。完整 canonical 样式必含 `ruby` 对象及这两个可空字段；局部 patch 的显式 null 表示遮蔽上层、使用歌词自身的注音参数，restore 则移除本层覆盖并恢复继承。样式设置不会改写歌词保存的原始 `relativeScale`／`gapPx`；字体及其他外观仍继承正文。预览与导出消费相同解析结果。

工程、歌词及模板持久格式分别为 32、20、10；不兼容格式按各入口的严格版本保护处理，不自动迁移或覆盖原文件。渲染与平台兼容性以实际运行验收为准。

<a id="preview-transport-01160"></a>

## 桌面连续预览多设备与传输协商（0.116.0／合同1.57.0）

桌面端连续预览流接口保留 `/api/v1/gpu-preview-streams` 路由不变，向 `GpuPreviewStreamCreateRequest.api` 与 `GpuPreviewStreamState.api` 放开 `"vulkan"` 与 `"cpu"` 双渲染设备，并在 `GpuPreviewStreamState` 中新增必需字段 `transport`（`"dma-buf" | "shm"`），如实公开本流协商到的底层传输通道（私有 claim 回包同步携带 `transport` 供 Electron 主进程使用）。传输模式覆写仍仅由启动环境变量 `MAKO_PREVIEW_TRANSPORT=shm|dma-buf` 控制，不在公开创建请求中暴露。

- **协商与零静默降级规则**：Linux 下未设置 `MAKO_PREVIEW_TRANSPORT` 时，`api:"vulkan"` 默认使用 `"dma-buf"`，`api:"cpu"` 默认使用 `"shm"`；显式设置 `MAKO_PREVIEW_TRANSPORT=shm` 时，`vulkan` 与 `cpu` 均使用 `"shm"`；显式设置 `MAKO_PREVIEW_TRANSPORT=dma-buf` 且请求 `api:"cpu"` 时，服务直接以 `422 commandNotSupported` / `failureCode:"PreviewTransportUnsupported"` 拒绝，严禁静默回退至 `shm` 或静态 PNG。
- **能力宣告与 Worker 生命周期**：桌面端在默认或显式 `shm` 模式下只要 GPU 或 CPU 任一后端可用即可宣告 `preview.gpu-streams`（显式 `dma-buf` 仍要求 Linux 且 GPU 可用）；`vulkan` 继续复用预热备用池，`cpu` 按需冷启动轻量 Worker 且与 GPU 共享单主机最多 2 条活动流配额。含 SDK v2 效果的工程在 `api:"cpu"` 下仍按既有规则返回 `commandNotSupported` / `PackageBackendUnsupported`。纯浏览器环境维持既有 HTTP 静态 PNG 预览与 500ms 节奏不变。

<a id="gpu-stream-recovery-1580"></a>

## 桌面连续预览流权威查询与配额回收语义（0.116.0／合同1.58.0）

- 新增只读查询端点 `GET /api/v1/gpu-preview-streams`，返回当前已认证客户端名下全部连续预览流权威状态（`GpuPreviewStreamListResponse`：`streams` 与 `protocolVersion:"1"`）；读取同样对未忙条目执行 Worker 存活检查，仅用于核对 `OutcomeUnknown`、配额占用和最新 `streamRevision`。
- `GpuPreviewStreamState` 新增可选只读布尔字段 `quarantined`（未隔离时省略，进入消费者或回滚隔离时为 `true`）。已隔离流保留配额直至服务进程退出，对其发起 `DELETE /api/v1/gpu-preview-streams/{id}` 或来源／尺寸变更直接返回显式 `503 previewUnavailable`（保留隔离 `failureCode`），不重复触碰已终止进程或破坏隔离保护；未隔离流的 Worker 在确认进程终止后，即使协议通道断开或 Windows 临时输入目录短暂占用也不阻塞 `CompleteClose`／`CompleteChange` 归还配额。工程会话关闭完成时（`project.closed`）服务端同步兜底关闭绑定该工程且未隔离的活动流。

<a id="ruby-spacing-0970"></a>

## 19. 自动 ruby 水平间距（0.97.0／合同1.28.0）

既有预览/同帧geometry+PNG测量及Runtime渲染使用共享完整行+ruby布局，横排单调映射中自动消解注音间距冲突。聚合ruby墨迹垂直区间须有严格正长度交集；仅相切或不相交不约束。按基础视觉单调顺序检查所有此前ruby，包含跨无注音token的宽注音；水平墨迹间距至少为两段连续排版时的自然接缝：前段末字右侧留白、前段字距及后段首字左侧留白之和，并保留至少1个缩放前布局像素；自然距离已足够时无需改变。正文的完整grapheme/HarfBuzz cluster与基础token合成刚性组，只在合法组间插入最小正向平移；不重新整形子串，不拉伸glyph，不按时间切换布局。

ruby沿原Start/Center/End对齐，锚点使用原始基础token自然左右边界加刚性组位移，新增gap不计入前token的ruby右锚点；保留真实自然overhang，不强制把所有基础列扩宽。正文XAt与progress path使用最终位置，新增gap归前token无墨迹进度区间。所有line/token/family/ruby geometry、固定绘制单元及脚本只读几何投影与实际PNG消费同一布局；未新增画布命中API，客户端仍可按既有geometry拾取。

ruby文本完整聚合整形和原segment cluster归属保持，独立segment时间/ID/状态和基础token采样不改；progress只改变clip/paint。无冲突或单ruby保持原坐标，原无碰撞legacy图像保持；历史无时间冲突画面也可能改变，这是spacing改善的预期效果。

联合墨迹交既有BuildGeometry/maxWidthRatio与safe-area宽度策略统一缩放整个家族；正常中立位置的窄画布按该宽度容纳，不对子ruby单独压缩。用户主动平移、旋转、局部/脚本变换不自动钳制；geometry沿原不含描边/阴影外扩定义。共享字簇组内冲突、非单调/不精确映射、非法或非有限坐标使用内部状态与保守原布局，不能据此声称完整RTL/竖排或任意组内冲突已解决；不扩张公共diagnostics/failureCode契约。

本批只改变布局行为，无新route/DTO/command/持久化字段：80路径、89方法、449schemas、91命令；歌词schema17及原工程schema保持，产品0.97.0／合同1.28.0。发布必须匹配Server与Worker；既有EngineIdentity仍拒绝不匹配版本。完整公共B3/B4、B5/B6、浏览器/用户及性能/长片/平台验收独立。


<a id="public-package-preview-0980"></a>

## 20. 历史：直接效果包公共项目预览（0.98.0／合同1.29.0）

本节保留0.98独立包profile与当时验收；本轮包与高级预设、媒体/字体、候选/模板试样的统一输入见§23，下述旧限制不作为全局准入。

### 20.1 来源与实际能力

本批通过已有合法工程open、`POST /api/v1/preview-sessions`、`PUT .../input`及帧/状态/诊断/关闭接口接入直接效果包；请求仍仅`source.kind=project`，不新增路由/命令/裸snapshot/源码或Worker路径输入。HTTP protocol仍1。`sourceProfile`兼容字段保持`static-lyrics-scene-v1`；`sourceProfiles`在实际Worker协商成功后追加`package-lyrics-scene-v1`。原两baseline static来源保持，preset能力探测失败仅不公布preset，包探测失败不影响static。

服务端预览专用选择器根据完整包/实例/非默认效果预算/fingerprint/源包材质判断，不能只按实例数选择，也不得静默剥离字段。包profile与原三静态profile使用独立编码/工厂；Hosting Registry还要求固定输入带服务端`VerifiedProfile`验证标记，并再次对实际完整包闭包执行codec门禁，只有project来源允许包输入。该标记是进程内可信组合根元数据，不是HTTP字段，客户端不能提交或绕过；静态codec、B4正式验证/导出、同帧场景测量、静态模板/候选试看保持原能力范围。包工程中的非空PresetApplications及非Color背景、外部媒体/可搬移字体闭包继续明确拒绝；固定内存包资源允许，不授予任意本机文件能力。

必须实际核对Worker EngineIdentity、包profile、PackageVersion/SDK/shader、完整compiler refs身份与diagnosticsVersion1，绑定实际进程及原pipe。未公布能力或协议/refs/engine/diagVersion不匹配：503 `previewUnavailable`，`failureCode=WorkerPackageProfileMismatch`；其他Worker不可用为`WorkerPackageUnavailable`，不回退Host执行/静态profile。实际open/render保`WorkerPackageCompilationFailed`、`WorkerRuntimeDiagnosticError`、`WorkerPackageTimedOut`等稳定失败编号，通过预览state的renderFailureCode读取，不能依赖message。

### 20.2 固定输入与生命周期

在唯一Writer中原子核对精确revision并捕获snapshot、对应audio asset身份和快速resource reservation；Worker握手、编码及脚本执行在Writer外。固定asset用于独立retention/runtime leases，工程音频后续替换/清除不使既有snapshot引用错配。包bytes一次open固定，逐帧不重读工程/库、不重编译；总4MiB、200000源值及原深度门禁不变，净IPC snapshot最多4194280字节。

既有previewId/sourceRevision/requestSeq/代次规则保持。playback同代仅替换最新pending；seek采用响应新generation，同输入经验证软取消后复用Worker并重开Runtime；PUT input切新固定输入，排水旧会话。编辑不自动跟随，关闭工程后预览保独立lease。关闭/失败/取消/替换输入均完整归还本路径拥有引用。

### 20.3 完整公共诊断

诊断仍由`GET /api/v1/preview-sessions/{id}/diagnostics`分页读取，旧通用六字段及旧静态wire保持。新增三个可选payload，未使用字段省略，不发送null占位：

| originKind | payload | 完整内容 |
|---|---|---|
| compileSource | source | packageId/revision、可选instanceId/exportId、code/message/isError、可选path、line/column；0行列合法 |
| imageEffect | imageEffect | instanceId/artifactHash、graph{packageId,revision,exportId}、inputId/code |
| material | material | owner、material{packageId,revision,exportId}、inputId/code |
| runtime | occurrence | 原epoch/sequence/instance/artifact/stage/time/location/package，无新增包payload |
| transportFailure | 无 | 无真实runtime occurrence，不伪造源码/图/材质发生 |

每项恰有与origin对应的payload，compileSource/imageEffect/material不能带occurrence。source顶层code/message保持原值，severity由isError映射error/warning。图/材质顶层severity为warning；message分别固定“图效果使用了已声明的降级行为。”/“材质使用了已声明的降级行为。”（缺背景的两种code另有写明原因的message，见下段），payload保真实code/owner，不用格式化message替代结构身份。

**缺背景媒体（0.112.61／合同1.49.2）。** 不透明输出的工程没有可用背景媒体（未设置、文件丢失、解码失败或视频该时刻无帧）时，预览不再整帧失败：声明必需 `BackgroundMedia` 输入的图效果旁路为原样、材质退回包内基础外观，各给一条 `code:"BackgroundMediaUnavailable"` 的warning，payload的 `inputId` 为该输入；message改为写明原因的“背景媒体不可用（未设置、文件丢失或该时刻无帧），图效果已旁路为原样。”与“背景媒体不可用（未设置、文件丢失或该时刻无帧），材质已退回包内兜底外观。”，其余code的message不变。可选的 `BackgroundMedia` 输入以工程底色代替，照常执行、没有诊断。该code只对声明了媒体依赖的必需输入有效，Worker包诊断映射拒绝其他来源。透明输出仍按 `TransparentBackgroundDependency` 处理。静态渲染会话与导出任务沿用原规则，出现任何图／材质诊断即失败（`WorkerDiagnosticsUnsupported`／`WorkerExportDiagnosticsUnsupported`），缺背景媒体的工程在这两条路径上仍不出片。DTO、schema与例子不变。0.112.86／合同1.51.0 起导出与同帧测量照预览兜底并在结果中列出去重诊断（§11.3、§13.21），不再报 `WorkerExportDiagnosticsUnsupported`；静态渲染会话不变。预览、导出与测量共用同一映射，`TransparentBackgroundDependency` 的message随之改为“透明输出没有背景，图效果已旁路为原样。”与“透明输出没有背景，材质已退回包内兜底外观。”，code与payload不变。

material.owner是互斥union：`{kind:"script",script:{instanceId,artifactHash,packageIdentity?}}`或`{kind:"source",source:{path,region,slot,sourceMaterial,parametersJson}}`。资源身份均为packageId/revision/exportId；source.slot为0..2整数。source Path/InstanceId/ExportId和script PackageIdentity可空时按原响应省略null语义。来源路径只用包内或源样式逻辑位置，不加入服务端私有路径。

compile failure、runtime Error失败路径保留全部实际报告及同期图/材质旁报。私有回执的基础shape、包帧合同和proxy操作关联均严格校验：open编译失败仅有非空source、无runtime/image/material；render的runtime Error允许HasError=true、source为空的完整image/material旁报，不能混入像素/geometry或伪装编译失败。此处复用已有私有封包字段，不新增公共Worker wire；compileSource没有epoch/sequence/time，不得制造runtime occurrence。任何source IsError或runtime Error禁止新像素，failed状态无latestFrame。图/材质合法旁路warning不等于Error；非法owner/handle拒帧。新PNG只有完整诊断shape/身份/Error/预算均验证通过才可放入可读取frame store；失败不能泄露本次新帧。此前成功frame lease按原TTL保留，不能因failed仍可读取旧frame而误判失败。

### 20.4 分页、预算与恢复

公共完整诊断DTO数组最多768项/512KiB UTF-8；runtime及每种包类各最多256项，三包类合计最多512项。私有runtime256项/64KiB、包各256项/64KiB与合计512项/128KiB保持独立，不把旧64KiB合并门禁套公共wrapper。每页最多128项、最多32页，实际配置较小值继续生效；超限failed，`failure.code=diagnosticLimitExceeded`、`renderFailureCode=PreviewDiagnosticsBudgetExceeded`，不截断或伪造空成功。

完整页链一次发布，页绑定previewId/generation/sourceRevision，旧cursor换代失效。error不发布占位PNG，失控deadline/取消后等待真实Worker及观察后代排水；健康新输入/新会话可恢复。本机Linux组隔离不构成任意恶意脚本OS sandbox或跨平台保证。

公共preview state/帧/诊断响应不返回完整snapshot sourceSha256；HTTP可证明previewId/generation/sourceRevision/requestSeq及返回PNG内容hash，完整source SHA与原Runtime等价需独立固定输入oracle/Worker内部证据，不得声称仅凭公共HTTP已核对source SHA。

schema合成例不证明真实代码执行/完整性或性能。匹配Server/Worker fresh发布、真实HTTP及直接Runtime像素对照、compile/runtime/旁路诊断和失控恢复证据在实施记录单独登记；完整B3/B4、作者、媒体字体、浏览器/用户及原长片/GUI/平台验收不由本批扩大。

<a id="backend-author-p4"></a>
<a id="21-无-gui-作者草稿与源码采用0981合同1320"></a>

## 21. 无 GUI 作者草稿、试样与个人配置库（0.112.14／合同1.46.0）

本节当前源码增加精确作者源码声明检查，以及采用源码时可选的完整配置迁移；既有创建、源码固定、同步编译和删除保持 HTTP `200`/`201`。P4行为已纳入历史1.40.0合同，W8增加的 profile 草稿发现/claim、包文本编辑、源码修订历史和精确包下载继续保留。真实HTTP验收与完整产品能力仍单独登记；历史匹配发布`6569bf1`的有限fresh HTTP已覆盖重启后的新client认领、继续预览/下载及删除，证据与限制见[W8修复记录](../backend-w8-authoring-fixes-20261030.md)。

P4 N07～N14/E12～E15 的历史实现与 W8 修复共同组成作者闭包。历史合同1.40.0已通过 fresh CLR metadata 生成、freeze 与 validate；1.46.0新增行为仍须从匹配生产源码重新导出metadata、freeze与validate，并单独登记匹配发布/fresh HTTP证据。本文描述源码规则，不将历史验收扩大为本批验收。

N07 `GET /api/v1/drafts` 只列出当前客户端拥有的草稿。N08 `GET /api/v1/drafts/{id}/sources?expectedDraftRevision=...&sourceRevision=...` 在精确草稿修订下返回指定保留源码修订的完整C#文件path/text及包身份；草稿修订冲突409，源码不存在404，包内逻辑路径不授予服务器文件读取能力。

N09 `PUT /api/v1/drafts/{id}/configuration` 携带 `expectedDraftRevision/configuration/metadata`；配置包含name/packageId/sourceRevision/exportId/parametersJson/relativeTarget/readScope/branchInput/fixedSeed/overlayLayer。fixedSeed为UInt64十进制字符串或null，固定源码须属于该草稿。原ScriptConfiguration/参数验证通过后一次更新草稿，不写个人库或工程历史；SDK2 C# 来源发生变更时须有该草稿保留历史中的成功编译记录，否则拒绝 `commandRejected/AuthorSourceCompilationRequired`，普通配置 PUT 不能绕过此门禁。SDK1 保持原行为。E12的GET draft同时返回完整configuration和sampleOptions；kind/bindingKinds由领域配置派生。create可从已授权packageAssetId或精确configurationId/expectedConfigurationRevision建立草稿；配置来源要求packageAssetId为null，exportId须匹配保存的配置，name由本次请求明确提供。

作者工作流固定一个已授权的效果包资源和导出身份，创建持久草稿。草稿只由当前所有者客户端操作，所有权仅通过显式 claim 转移；服务端将包内容写入自己的状态根并以 `sha256:<64位小写十六进制>` 作为不可变源码修订身份，不暴露主机路径。`GET /api/v1/drafts/{id}` 返回当前草稿修订、基础修订、当前源码修订、最近编译摘要和是否可编译/采用，并以 `packageVersion/sdkVersion` 返回固定包的正式身份。`canCompile` 要求编译参考集可用且当前源码修订含 C# 文件，不按在用配置修订推断。`GET /api/v1/drafts/{id}/compilation` 只读取最近一次完整编译结果；没有编译结果返回404 `resourceNotFound`，读取不携带metadata且不改变草稿修订。

`POST /api/v1/drafts/{id}/source-revision-jobs` 必须携带精确 `expectedDraftRevision`，一次提交当前包中全部且仅全部 C# 文件。服务重新计算清单哈希并固定新的包修订；相同字节保留修订与摘要。SDK1 内容变化后保持原跟随/清除编译摘要行为。SDK2 只推进 `currentSourceRevision`，保留在用 `configuration.sourceRevision`、采用标记和旧编译身份；旧摘要的 `stale` 表示它不验证最新源码。缺文件、重复路径、越界文件或过大源码整批拒绝，历史不产生半成品。

SDK2 作者界面分别展示最新、最近编译通过、已采用和在用修订。保存、编译均不改变试样与组件库保存所用的在用来源，成功编译后仍需显式采用。源码采用的撤销/重做使用独立历史，不混入参数配置撤销；撤销目标也必须先编译通过，CAS 或存储失败保留撤销记录。可以查看、编译原包或旧修订后再撤销采用。编译旧修订的成功只验证该固定源，不能把最新源码标成通过。该交互不声称预览已实际显示任何一帧，预览仍受既有来源/代次与帧响应协议约束。

W8 增加 profile 级恢复入口。`GET /api/v1/profile/author-drafts` 只返回当前状态根内的草稿摘要、当前 `draftRevision`、所有权修订和 `active`、`dormant`、`orphaned`、`ownedByCaller` 状态；支持按草稿身份的有界游标分页。`POST /api/v1/profile/author-drafts/{id}/claim` 必须同时提交发现时的 `expectedDraftRevision` 与 `expectedOwnershipRevision`，服务在草稿锁、客户端所有权 reservation 和存储发布边界内原子认领。活动 owner、在途操作、客户端回收、CAS 冲突或目标 client 的16草稿/256 MiB配额会拒绝；旧格式草稿只在成功 claim 时补写当前 schema。普通 `GET /api/v1/drafts` 与所有作者读写仍执行 owner ACL，profile 列表不泄露包内容。孤立草稿不会自动删除；认领成功后由新 owner 按精确草稿修订调用 DELETE 清理，既有 live job/preview 不随 claim 转移。

`GET /api/v1/drafts/{id}/package-files?expectedDraftRevision=...&sourceRevision=...` 返回完整 manifest、文件角色、大小、SHA 和可编辑文本。`PUT /api/v1/drafts/{id}/package-files` 要求提交 manifest 及全部 `csharp`、`shader`、`shaderProgram`、`graph`、`svg`、`svgResource` 文本，服务在包结构、闭包、导出配置、路径和 UTF-8 预算检查完成后一次发布新源码修订；非文本文件的路径、角色、SHA 和字节集合必须保持不变。文本单文件最多4 MiB，文本与 manifest 合计最多16 MiB，最多256个文本文件，HTTP JSON最多32 MiB。原源码入口提交字节无变化时保留原草稿修订和最近编译摘要。

包文本保存保持草稿的 SDK profile，不能通过清单降级。SDK2 包文本变更同样只推进最新修订；附带配置仍以原在用源码验证和保存，采用新包修订须经成功编译门禁。SDK1 保持原子迁移配置到新包的行为。纯资源包无需 C# 编译。 因而跨参数schema或导出身份的迁移，应保存候选源码、编译该精确修订，再通过下述adopt完整配置一次采用；不能把候选配置提前交给SDK2 package-files并按新源码解释。

<a id="author-package-replacement"></a>**整份替换包内图片与字体（0.112.39／合同1.49.0）**。能力 `author.drafts.package-files.replace` 存在时，`PUT /api/v1/drafts/{id}/package-files` 可选 `replacedFiles:[{path,assetId}]` 与 `baseSourceRevision`。不可编辑文件取自 `baseSourceRevision`（编辑器所看的保留修订；省略/null为当前最新修订，非法格式400、不在保留历史404 `field=baseSourceRevision`），manifest 中这些文件的 path/role/sha256 须与基准修订一致；`replacedFiles` 列出的文件换成本客户端上传且未过期资源的完整字节，清单 sha256 由服务重算后一次发布新修订。只允许 role 为 `image` 或 `font` 的文件，每个 path 最多一次；不新增、删除或改名文件，也不替换视频等其它二进制。SVG 属可编辑文本，前端把新文件文本载入编辑器后按文本保存。

新文件须与原文件同一格式：图片按 Skia 识别的编码（png、jpeg、webp 等），字体按文件头区分 TrueType（`00010000`/`true`）、OpenType CFF（`OTTO`）与集合（`ttcf`）；并通过与渲染相同的完整图片解码或固定包字体目录加载。单文件上限沿包预算64 MiB（`PackageFileBudgetExceeded`）；资源字节与上传记录的大小或摘要不符按 `resourceExpired` 拒绝，需重新上传。失败均为 `invalidRequest`（资源错误沿资源 code），不发布修订：目标不对 `AuthorReplacementTargetInvalid`（`field=replacedFiles[i].path`），格式不同 `AuthorReplacementFormatMismatch`、字体无法加载 `AuthorReplacementFontInvalid`、图片 `PackageImageDecodeFailed`/`PackageImageBudgetExceeded`（`field=replacedFiles[i].assetId`）。同字节再替换得到相同修订，按既有规则保留原草稿修订。

`POST /api/v1/drafts/{id}/package-file-replacement-inspection` 是只读检查，请求 `{expectedDraftRevision,sourceRevision,path,assetId}`（严格字段，JSON最多64 KiB，不接受query，无metadata与回执）。服务在草稿锁内核对 owner、草稿CAS与保留修订后释放锁再解码，返回 `{draftId,draftRevision,sourceRevision,path,role,current,replacement,warnings,protocolVersion}`；`current/replacement` 含 `format/sizeBytes/sha256`，图片另含 `width/height`，字体另含 `familyNames`（去重、按出现顺序）。`warnings[].code` 为 `imageSizeChanged`（尺寸变化，按像素裁切或假定尺寸的效果可能需要调整）或 `fontFamilyChanged`（家族名集合按不区分大小写比较变化；工程与模板按原名字引用的文字不会用到新字体），只是提醒，不阻止保存。检查与保存走同一套校验；失败码同上，field 为 `path` 或 `assetId`；草稿修订冲突409，修订不属于保留历史404。

`GET /api/v1/drafts/{id}/source-inspection?expectedDraftRevision=...&sourceRevision=...` 以能力 `author.drafts.source-inspection` 提供只读声明检查。两个query必须各出现一次、非空且为精确小写SHA修订；未知query或任何body均拒绝，不携带metadata、不创建回执或job。服务在草稿锁内核对当前owner、草稿CAS、所选源码的retained成员资格与固定包身份，未编译源码也可检查；检查不编译或执行作者，不准备native SVG或其它运行资源。成功与失败均设置 `Cache-Control: no-store`。

`AuthorSourceInspectionResponse` 为独立、无reference的完整响应，返回 `draftId/draftRevision/sourceRevision/packageId/packageRevision/name/version/packageVersion/sdkVersion/shaderProfile/structureValid/parameters/exports/bindingKinds/inputs/files/diagnostics/protocolVersion`。合同 1.53.0／产品 0.113.0 起已全面移除 SDK v1，仅准入 SDK v2 包，`AuthorSourceInspectionResponse` 的 `packageVersion` 仅允许 `3`、`shaderProfile` 仅允许 `"skia-ganesh-3.119.4-v2"`。`sourceRevision` 与 `packageRevision` 相等，`packageId` 为草稿固定包身份；SDK版本沿既有profile兼容规则保留，不将SDK2补丁版本限定为2.0.0。参数、导出、input、文件和结构诊断复用既有DTO；参数 `defaultValueJson/minimumJson/maximumJson` 仍为精确JSON文本字符串，保留超过2^53的整数，`unit/minimumJson/maximumJson` 为空时省略。`declaration/inputs` 保留原JSON值；文件长度字段为Int64 JSON number `size`，不是 `sizeBytes`。结构无效时可返回诊断与文件列表，typed参数/导出为空。普通响应保持可扩展，读取方忽略未来未知字段。原manifest文本仍由package-files读取，两次读取须核对相同 `draftId/draftRevision/sourceRevision/packageId/packageRevision`，并将检查响应的 `packageVersion/sdkVersion/shaderProfile` 与package-files的manifest原文身份对应；检查入口不把草稿加入公共 `EffectPackageReferenceDto` 的kind，公共kind仍仅为asset/library/configuration。

响应按生产HTTP相同的Web JSON序列化、空值省略及UTF-8编码计算完整体积，最多 `8388608` 字节；超限整次拒绝，HTTP `413 resourceTooLarge`、`field=sourceRevision`、`failureCode=AuthorSourceInspectionTooLarge`，不返回截断数组。owner拒绝沿 `401 resourceUnauthorized`，草稿/保留源码缺失沿 `404 resourceNotFound`，草稿CAS沿 `409 revisionConflict`。固定源码或历史读取失败为 `422 projectLoadFailed/AuthorSourceInspectionReadFailed`，固定包身份不符为 `422 projectLoadFailed/AuthorSourceIdentityMismatch`，字段均为 `sourceRevision`；包结构读取拒绝保留原 `commandRejected`、failureCode与逻辑path。非法query或body沿 `400 invalidRequest`，客户端登记及宿主停止沿统一认证/生命周期错误。


`GET /api/v1/drafts/{id}/source-revisions` 在精确草稿修订下按仍保留的源码包修订返回有界历史页。每项可带保存时间和该源码修订的编译摘要；旧草稿无法证明保存时间时省略 `savedAtUtc`，未知或已清理游标返回404。`GET /api/v1/drafts/{id}/package` 与 `GET /api/v1/script-configurations/{id}/package` 分别按精确草稿/库修订返回认证 `.mkfx` 流；服务全局最多同时保留2个下载槽位，超额429；每次下载30秒期限，流关闭、断开或宿主停止释放临时归档和槽位。此入口仅支持完整 GET，不承诺 Range、HEAD 或条件304；返回 `Content-Disposition`、归档 SHA-256 ETag、长度及 `Cache-Control: no-store`。stale revision、owner ACL 和包结构错误在响应体开始前返回。

`POST /api/v1/drafts/{id}/compilation-jobs` 可选 `sourceRevision`，仅接受该草稿保留的源码修订；省略或 null 绑定接受时的最新修订。服务先对固定包的全部 SVG 执行正式静态语义与图片闭包校验（不创建 native Picture／GPU 资源），再独立执行脚本编译，真实 DLL 入口须符合包 SDK 身份，并返回完整诊断；编译失败是可读取的诊断结果，不改变源码修订。SVG 校验失败沿同一报告返回精确资源错误码与包内逻辑路径，行列为 0、artifactHash 为 `none`；不会编译部分 C# 或静默使用旧产物。包文件保存仍只验证结构与资源描述，允许保存可修复的无效 SVG 正文；修复文本生成新源码修订后须重新编译。编译诊断最多256项且完整UTF-8回执最多48KiB，超限整次拒绝并保留原草稿状态。`POST /api/v1/drafts/{id}/adopt-source-revision` 同样要求精确草稿修订，显式选择已固定且导出身份有效的源码修订。SDK2 C# 目标要求此草稿历史中该精确源码的成功编译，未满足则返回 `commandRejected/AuthorSourceCompilationRequired`，纯资源包和 SDK1 保持原行为；成功返回实际前后在用修订身份。

采用请求保留 `expectedDraftRevision/sourceRevision/metadata`，新增可选 `configuration`；省略或显式null沿原配置采用行为。非null必须一次给齐 `name/exportId/parametersJson/relativeTarget/readScope/branchInput/fixedSeed/overlayLayer` 八字段，未知或重复配置字段拒绝；`branchInput/fixedSeed` 两字段可显式null但不可省略。包ID、源码修订、kind和bindingKinds由服务派生，不接受客户端覆盖。名称必须非空白且至多256个UTF-16码元，保存时trim；fixedSeed只接受UInt64十进制字符串 `0..18446744073709551615` 或null，允许前导零，拒绝JSON number、符号、空串和溢出。`readScope=branchInput` 时必须提供非空branchInput，其它scope必须null；源材质只允许application、boundSources、null seed和package overlay。

服务按目标retained源码验证导出、参数、绑定与真实sampleOptions，全部通过后在同一次document发布中更新配置、名称、导出和采用标记；失败保留完整旧状态。SDK2 C#目标须在本草稿保留该精确sourceRevision的成功编译记录，编译A不能验证B。`currentSourceRevision` 保持最新候选含义，SDK2最近编译摘要仍代表最近一次编译的固定源，不因采用旧源而重标。相同有效状态保持no-op/CAS语义；回执指纹包含完整configuration，原样重放返回原结果，同requestId改payload按既有回执规则拒绝。配置格式或绑定错误沿 `invalidRequest/AuthorConfigurationInvalid` 及 `configuration.<字段>`；导出/参数包错误沿 `commandRejected`，保留原failureCode并定位配置字段。新对象完整性和原子性须由实际HTTP与存储失败回归验证，schema仅表达wire形状。


**0.110.3 编译缓存：** 作者编译使用当前 profile 的 `script-artifacts` 内容地址缓存。同键并发请求共享编译，
各调用者独立取消；失败编译不发布可运行产物。缓存身份包括源码、入口、编译选项、编译器、SDK、
引用集、引擎和规则版本，参数与种子不改变编译输入键。返回的 `artifactHash` 仍是不可解析的内容身份，
不是 PE 字节摘要，也不构成公开的产物读取路径。内部缓存默认 256 MiB，PE／PDB 各 32 MiB、清单 1 MiB；
配额、损坏摘要或读取失败沿既有编译任务失败路径处理，不能回退旧成功产物。磁盘在用租约保护淘汰，
跨进程同键互斥和崩溃暂存回收不改变 owner／CAS／任务终态。此处是产物读写预算，不保证 Roslyn
编译期间的峰值内存上限。

**0.110.6 HTTP seek 与关闭恢复：** 公开请求 generation 与固定运行输入身份分离，保持旧帧、来源、
序号、owner/CAS 与帧租约隔离。同输入成功帧之间不重建 Worker；仅协作取消 ACK 验证成功时允许原进程
重新建立 Runtime，复用同一固定产物目录，不重新编译／复制。关闭或输入替换中的所属进程排水失败时，
保留会话及资源引用，重试成功前不新建替代会话。预览关闭失败返回既有 `internalError`，状态保留 `closing`，
不虚报 `closed` 或提前释放会话配额；同一来源／generation 可用新 requestId 重试关闭。
私有输入目录删除失败同样保留最后引用并拒绝新借用，成功清理后才归还所有权。

**0.110.5 固定产物消费：** 预览、验证、导出和场景测量复用 Host 的同一编译服务；
渲染 Worker 校验固定产物字节、SDK／参考集／引擎及入口后加载，不再运行 Roslyn 编译。
shader、图像、图、材质和作者运行诊断仍在原执行边界校验，公共请求、owner／CAS、来源 profile
与终态语义保持。产物是私有 Worker 输入，不增加公共 DLL 路径或读取接口。详细预算及目录
所有权见[固定产物架构](script-compilation-artifacts.md)。

`DELETE /api/v1/drafts/{id}` 必须携带 `AuthorDraftDeleteRequest` 的精确 `expectedDraftRevision` 和metadata。成功返回 `AuthorDraftDeleteResponse`（200），删除草稿状态及其固定包，并释放客户端作者草稿的存储与回收保护；修订冲突返回409，跨客户端读取仍为401 `resourceUnauthorized`。删除是显式操作，不会自动修改个人模板库或工程历史；同一metadata重试返回原回执。

`POST /api/v1/drafts/{id}/compilation-jobs` 可选 `executionMode:"async"` 返回 `202` 与普通 `JobResponse`；省略该字段或显式 `sync` 保持原同步 `200` 回执。异步任务在接受时固定 `draftId`、`draftRevision` 和所选 `sourceRevision`，沿用现有任务状态、事件/SSE、取消、保留期、客户端授权及前一宿主账本。`GET /api/v1/jobs/{id}/compilation-result` 只返回该任务固定源码修订的完整 `AuthorCompilationResponse`；异步任务成功产出失败编译报告时，job 仍为 `succeeded`，报告内 `succeeded:false` 才是编译失败事实；草稿之后的源码或配置修订不会覆盖它。任务结果发布时 `stale` 比较其固定源与当时草稿最新源码，结果保留后不追改；草稿读取则相对当前最新源码计算 `stale`。草稿在任务期间改变时不覆盖最新编译摘要，但仍按原源码修订保存历史编译记录。任务进入提交阶段后取消按既有 `operationNotCancellable` 语义处理；取消、停止或重启造成的无结果终态从普通 job 查询读取，不能用空诊断伪造编译成功。N10只读取当前宿主的live job结果；sidecar与账本持久化不构成公开的编译结果恢复路由。

E14 authorDraft 预览复用既有 preview-sessions、帧、诊断与 close 生命周期。source 形状为 kind=authorDraft、sourceId=草稿ID、sourceRevision=草稿修订、authorSourceRevision=固定配置源码修订，并携带 templateSample 的 sampleId/targetKey/materialSlot；创建请求需 previewKind=authorDraft 且 committable=false，PUT input 保持同一draft ID，可更新draft/source修订及sample/target/materialSlot，并沿generation发布新的固定输入。N11/E15 的验证任务固定 640×360、30fps、12秒、360帧/输出；JobResponse 与报告分别回显草稿/源码/试样身份，sourceKind=authorDraft、sourceProfile=author-script-sample-v1。

SDK2 作者试样与验证要求 Vulkan。作者验证在配置固定后、排队和准备运行输入前拒绝非 GPU 选择，返回 `commandNotSupported/PackageBackendUnsupported`，字段为 `renderBackend`；界面提示在顶栏明确选择 GPU，保留用户设备选择。实际 Runtime 仍核验设备身份，成功接收请求不等于完成验证。

N12 `GET /api/v1/script-configurations` 返回库revision和配置摘要，可选expectedRevision；N13 `GET /api/v1/script-configurations/{id}?expectedRevision=...` 返回完整配置与固定包检查结果，精确修订必填。N14 `POST /api/v1/script-configurations/commands` 使用严格的expectedRevision/metadata/command，支持create、replace、copy、rename、delete五种命令；copy/rename传configurationId和name，delete只传configurationId，create/replace携带完整configuration。库CAS与作者草稿修订分别管理，不混入工程历史。

库configuration完整传输name/exportId/parametersJson/relativeTarget/readScope/branchInput/fixedSeed/overlayLayer/source；branchInput和fixedSeed允许null但不可省略。source严格三选一：draftId/expectedDraftRevision/sourceRevision，configurationId/expectedConfigurationRevision，或package引用，不接受跨分支字段。固定包引用的kind=configuration需packageId/packageRevision/configurationId/configurationRevision，能供包检查、模板材质/配方及新作者草稿使用。库写入固定所选来源，不强制成功编译/验证，不追改既有模板或工程快照。

所有作者写入均使用现有回执、客户端身份和草稿锁；重试必须复用相同 request metadata。每客户端最多16个草稿，作者草稿固定状态及包内容合计最多256MiB；完整编译回执最多48KiB UTF-8，诊断数组最多256项，超限整次拒绝并保留原状态。源码固定、编译和采用均不自动修改个人模板库或工程历史。该切片只覆盖公共后端作者闭包与生命周期，完整脚本执行隔离、任意媒体/字体组合和最终分发平台仍由对应能力验收。


<a id="project-image-preview-0990"></a>

## 22. 历史：已保存工程图片背景的受管预览（0.99.0／合同1.33.0）

本节保留0.99独立图片profile与既有验收，以下预算/限制只约束该历史profile；本轮普通工程采用§23统一准备，图片无需先保存重开。

### 22.1 公共来源与能力

现有 `POST /api/v1/preview-sessions` 与 `PUT /api/v1/preview-sessions/{id}/input` 接受合法已加载工程的固定 Image 背景；请求仍是 `source:{kind:"project",sourceId,sourceRevision}` 与原 size/alphaMode/metadata，客户端不提交 profile、图片字节、路径、grant 或裸 snapshot。`runtime-info.sourceProfile` 兼容单值始终为 `static-lyrics-scene-v1`；`sourceProfiles` 只有在独立 `project-image-preview-v1` Worker 能力握手通过后才追加此项。未通过图片探测只省略图片来源，不关闭原静态/包能力；创建及实际 open 继续复核，不能以 enabled、status 或产品版本推定图片可用。

图片来源仅能包裹无预设的 `static-lyrics-scene-v1`、`static-lyrics-scene-analysis-v1` 或直接 `package-lyrics-scene-v1` 完整基础源。包内 sprite/graph 图片已有 Runtime 完整解码和消费，本批新增的是工程主背景 Image 的内存闭包与生产渲染接入。固定预设与图片、视频背景、外部字体、任意本机路径、模板固定试样/候选试看、validation/export/measurement 均不由此 profile 开放；它不进入原静态或正式任务 allowlist。

### 22.2 授权、固定输入及预算

Host 在同一工程 Writer 中核对 owner/精确修订，捕获不可变 snapshot、音频资源 reservation 与 ProjectStore 已加载 `loaded-input` 图片的短期读引用；捕获不做文件 IO、图片头读取、native 解码或 Worker 调用。文件读取在 Writer 外，核对 Background.Path 与 MediaFileRevision.RelativePath 的受管内容地址、所有祖先/文件无链接、完整 size 和 SHA-256；工程关闭先阻止新捕获，再等待读引用排水后清理 loaded-input。成功后预览绑定独立固定 bytes，工程关闭或原文件后续变化不改写该预览。

私有 image envelope 保留背景 kind/fit/playback、完整 Media 身份、composition 底色及完整基础源。Host 的工作绝对路径不进入封包，Worker只使用逻辑内容地址；原始名称保留为显示元数据，不解释为路径。Color 替换仅用于复用基础 codec 的完整检查，按原领域规则重算/核对效果 fingerprint 后恢复 Image。Worker 再核同一 size/SHA，并在自己的 native 边界完整解码和消费。

| 门禁 | 固定上限 |
|---|---|
| 编码图片 bytes | 2 MiB |
| 图片单边尺寸 | 4096 像素 |
| 解码 BGRA | 16 MiB |
| 完整 image envelope（含 base64 扩张） | 4,194,280 字节，已扣24字节 open 头 |
| 嵌套基础 snapshot | 原4 MiB及200000 source values门禁 |
| 公共预览输出 | 原1280×720、裸帧4 MiB及帧/诊断/读取租约预算 |

超限整体拒绝，不截断、不缩放原图或发布替代黑底。支持格式以匹配 Worker 的实际完整解码结果为准；本批不承诺任意图片格式或动画播放。

### 22.3 稳定失败与生命周期

create/input 的固定输入失败使用既有错误 DTO，消息和字段不包含主机路径：

| HTTP / code | field | failureCode |
|---|---|---|
| 503 / previewUnavailable | worker | WorkerImagePreviewProfileMismatch |
| 503 / previewUnavailable | worker | WorkerImagePreviewUnavailable |
| 422 / commandNotSupported | background | ProjectImageSourceUnavailable |
| 422 / commandNotSupported | background | ProjectImageSourceChanged |
| 413 / resourceTooLarge | background | SnapshotSizeExceeded |

首项表示未公布或未通过独立能力/引擎/参考集/预算核对；第二项表示能力探测执行不可用。缺失已保存授权闭包使用 SourceUnavailable，size/SHA变化使用 SourceChanged。其他不支持的基础源沿原 source 字段和 Snapshot* 失败编号；不借图片profile绕过静态/包门禁。

异步解码失败通过 state 的 `renderFailureCode` 返回 `PackageImageDecodeFailed`，尺寸/解码预算为 `PackageImageBudgetExceeded`；通用 `failure` 沿既有 `internalError` 预览失败语义，不把它改成create的422。超时、包编译或Runtime Error继续复用 `WorkerPackageTimedOut`、`WorkerPackageCompilationFailed`、`WorkerRuntimeDiagnosticError`；失败无本次 latestFrame，不发布成功PNG，旧成功帧租约按原TTL保留。

每个 Runtime session 独占 resolver/native bitmap；无效果、包编译失败、准备失败、取消和正常关闭均由原工厂/会话所有权链释放。连续 playback 与固定输入不变的 seek 可复用持久 Worker；seek 在途渲染获协作取消确认后重新创建 Runtime session，未获安全回执或协议失败则排水 Worker。resize、透明切换和 input 更新改变固定输入，旧会话排水；generation/序列规则继续阻止发布迟到帧。图片factory保留原音频租约包装；无音轨工程也可使用。原会话关闭、客户端过期、工程关闭后固定预览、Host shutdown 和帧读取ACL/TTL规则保持。

### 22.4 证据边界

匹配self-contained Server/Worker发布源码 `d961895`、`sourceDirty=false`；fresh HTTP `artifacts/project-image-preview-0990-acceptance/http-final6/` 通过1175请求／9760断言。static/static-analysis/直接package普通与透明各4帧及回退seek、40组原Runtime完整PNG/像素比较、Initialize/Evaluate×cancel/deadline/close六组实际Worker/child控制与排水通过；Server exit0、105个观察身份（含83个Worker身份）全部退出，2752个证据文件凭证扫描通过。独立codec/Host/Runtime/Worker/HTTP传输本地回归611/611、变异红1/24与恢复绿24/24、Oracle预检及合同形状检查分别登记于[计划](../backend-project-image-preview-plan.md)和[实施§77](../backend-extraction-progress.md#project-image-preview-0990)，不与HTTP断言相加。以上结论只属于0.99历史切片；当前视频/包字体/包与预设共享消费见§23，浏览器、跨平台、性能及完整工程验收仍不由该历史证据完成。

### 11.5 历史：已保存工程Image背景导出（0.101.0／合同1.35.0，原HTTP未收口）

本节保留0.101独立图片导出实现及当时未收口状态；当前Image/Video/预设/字体导出使用§23共享输入，不把本节旧准入当作全局限制。

已保存Image主背景的有限导出使用独立 `project-image-export-v1` Worker能力，复用既有 export-jobs、报告、诊断、取消与成片读取路由。客户端请求字段不变；仅登记job input/exportInput出现成对的 `taskProfile` 与 `baseSourceProfile`。基础来源仅无预设static、static-analysis、直接package；当前源码的带音轨接缝仍须按实际音频 profile 及授权校验。

KIE1完整任务输入上限4194272字节（4MiB减32字节头），metadata最多32KiB；图片2MiB、单边4096、BGRA解码16MiB、基础snapshot4MiB、报告256KiB。媒体/音频大小沿宿主 `WorkerExportResourcePolicy`；默认null无对应部署限制。Worker独立核decoder、package refs/diagnostics、静态四来源导出能力及engine；sourceSha覆盖完整project-image-preview-v1 envelope。Host二次Writer重新核owner、session对象身份、revision与ClosePending，失败释放未转移lease/reservation。0.101生产实现1276982已推送，该批fresh HTTP当时未收口；不据此补记本轮测量、preset/video/font及最终HTTP。


<a id="unified-runtime-input-01020"></a>
## 23. P1/P2统一Runtime输入（历史产品0.102.0／合同1.36.0）

本轮新增公开操作为歌词导入规划、字体目录、字体检查及工程编辑候选预览；工程commands新增四个媒体背景操作和 `project.applyEdits`，既有底色命令保持。空创建支持无歌词/无音频、仅歌词、仅音频及二者都有；上传沿§5流式处理，可空宿主字节策略不等于无限内存或磁盘保证。

工程、编辑候选、预设候选与库内完整模板固定试样共用“固定snapshot与资源引用→原Runtime/Skia”链。保留Color/Image/Video、fit、Loop/FreezeLastFrame、分析、直接包、完整预设实例及其有序配方/包材质/参数/种子/分支输入；包内字体按内部家族/字重/斜体及原回退规则解析，TTC可枚举各字面。字体来自固定包闭包，不接受任意系统路径或字体安装请求。已有高级Line/Token以及P3完整合法模板定义可进入同一运行链；完整草稿、试样目标、clips/高级Node扩展见§24，作者草稿试样仍属P4。模板工坊与作者草稿试样每次捕获按配方与绑定次序分配固定实例身份，配方未给 fixedSeed 时用固定试样种子，同一草稿／模板反复刷新时随机取值不变（0.112.88 起；此前每次捕获重新抽取，粒子等随机效果每次刷新散布不同）。

Host在工程Writer内捕获owner、注册session对象、精确revision及资源引用，Writer外复制媒体与完整包资源目录并核size/SHA/包修订，返回Writer再核同一身份、revision及关闭状态才登记消费者。旧来源保留原wire；需要媒体或包字体时使用私有 `runtime-project-input-v1`，文件不挤入4MiB消息。Worker核固定授权与完整内容，再由原Runtime消费；Host测量/验证回执仅用已固定snapshot与封包纯校验，不重新读取私有资源目录。包内容仍受原包codec总量/引用检查，消息、帧、报告及各任务限额分别保持；授权不由客户端构造。

预览及候选持独立引用跨工程后续编辑，测量取得独立引用跨候选关闭；正式任务持固定副本至实际Worker/native排水。准备、第二次Writer复核、取消或创建失败释放未转移引用/预留；旧generation迟到结果不能发布。预览/测量、正式验证和导出分别核实际Worker能力，不能用预览profile授予正式任务能力。兼容旧图片profile继续存在，正常请求无需选择它。

准备/分析/带音轨导出声明的六家族为PCM16 WAV、MP3 LayerIII、native FLAC、AAC ADTS、MOV/M4A音轨、Ogg音轨；实际codec/声道/采样率及完整严格解码仍由Worker检查。分析保持显式apply/clear、300秒/样本/结果预算，完整已采用分析可被包/预设Runtime消费。导出支持opaqueMp4与transparentProRes4444、draft/standard/high，省略quality仍为standard；请求/报告实际质量、源采样帧覆盖、媒体完整性、进度/取消、下载与恢复读取沿原合同。导出与上传可空策略表示未施加对应宿主限制，不移除其他任务的固定协议预算。

实现与定向检查、最终匹配发布/HTTP结果统一登记在 [补齐计划§8](../backend-api-completion-plan.md#8-p1p2-实施记录)。本计划代表后端范围已通过匹配发布的fresh HTTP：320请求／2215断言，含180秒带音轨成片、运行中取消及视频+高级Line模板+包字体代表流程；像素、媒体、进程排水与失败归属见[实施§80](../backend-extraction-progress.md#api-completion-01020)。P3源码与 fresh HTTP 见§24及补齐计划§9；不外推任意媒体/字体组合、P4/P5、浏览器、跨平台或性能矩阵。


<a id="p3-template-workshop-api"></a>
## 24. P3完整模板与预设工坊（0.103.0／合同1.37.0，2026-10-25）

本节描述P3新增/扩展的当前源码行为。合同 freeze 提交为 `051f597`，manifest/validate 提交为 `c4146d4`；匹配self-contained Server/Worker发布源码基线为 `3005d5c`，fresh HTTP 证据位于 `artifacts/p3-http-3005d5c-r1/`，通过174请求／1151断言；本地回归、发布指纹与失败归属分别登记于[补齐计划§9](../backend-api-completion-plan.md#9-p3-实施记录)。P4作者/配置库、浏览器、跨平台和性能矩阵独立。

### 24.1 完整定义读取与保存

`GET /api/v1/template-libraries/{id}/templates/detail?expectedRevision=...&templateId=...&definitionFormat=full` 返回 `definitionFormat:"full"`、`definitionAvailability:"fullAvailable"` 与完整 `FullTemplateDefinitionDto`。只允许精确字符串full；省略格式保留§13.1旧静态wire。full定义包含 `name/kind/style/properties/effectRecipes/packages`：完整style包括所有未选字段；填充/描边材质保留solid、gradient或package的 `packageId/revision/exportId/parametersJson`。所有配方按请求顺序保留 `recipeId/name/target/packageId/revision/exportId/parametersJson/overlayLayer/enabled/readScope/branchInput/fixedSeed/configurationId`。`parametersJson` 是对象JSON的字符串，保留Int64和原数值token精度；`fixedSeed` 是UInt64十进制字符串或显式null。branchInput/configurationId可空字段也显式写出。客户端编辑参数时须用无损整数处理或保留原字符串，不能先转为JavaScript Number再保存。

`command:{type:"create",definitionFormat:"full",definition:{...}}` 和 `replace` 同样选择完整分支；replace另带 `templateId`。省略format继续使用原严格静态parser，不能借未选字段写入包材质。full允许原 `StyleTemplate.Create` 支持的Line/Token/Composite，支持纯配方且 `properties:[]`；至少一属性或配方、材质及配方参数、目标/读取范围、闭包精确性继续由原领域工厂验证，不增加静态种类限定。格式/成员形状错误（包括definition:null、配方或包数组中的null）为400；领域定义无效为422，失败保留原库。

`definition.packages` 是 `EffectPackageReferenceDto[]`，只固定包身份与可由服务端解析的来源。asset引用带 `kind:"asset"/packageId/packageRevision/packageAssetId`；library引用带 `kind:"library"/packageId/packageRevision/libraryId/libraryRevision/templateId/templateRevision`，各来源字段互斥。完整GET为库内每个包提供library引用，无需重传归档或接触机器路径。详情外层只读 `packageClosureDetails` 复用§24.2包检查投影，包含manifest与完整文件角色/长度/SHA；保存只提交definition，不回传这些只读说明。

服务在个人库IO gate外固定所有完整包字节，进入原库CAS前纯准备并验证完整模板、列表/旧详情/full详情及回执预算；通过原仓储一次发布，不另造存储或分享格式。库修订和模板内容修订是精确快照引用：保存造成库修订变化后，继续编辑前须重新读取full详情获取新引用；已有成功回执按原requestId重放。库更新不会追改已经采用的工程快照。

### 24.2 包检查

`POST /api/v1/effect-packages/inspect` 接受已授权 `packageAssetId`（可带 `expectedPackageRevision`），或互斥的完整 `reference`。asset沿ResourceStore当前client授权和原归档读取；library按库/模板/包三层精确修订解析。未知/他人资源沿现有ACL返回错误，修订冲突409。响应为200，包含固定reference、包身份、名称/版本、完整manifest及其保真 `manifestJson`、exports声明、bindingKinds、图inputs、完整files路径/角色/长度/SHA和结构诊断；路径仅是包内逻辑路径。

参数schema包含九类原类型、标签/单位、`defaultValueJson/minimumJson/maximumJson`与枚举选项，数值以JSON文本保留完整精度。查看只调用原 `EffectPackageDefinition` 结构解析，不编译C#、运行脚本或解码图片。可读取的包闭包结构语义非法时保留原manifest，`structureValid:false`且返回诊断，typed schema不伪造成功；闭包本身损坏沿原读取失败处理。

完整模板的预览与新建/替换共用定义校验。重复配方/包引用、闭包缺失或多余返回具体 `definition.*` 字段与 `InvalidDefinition`；包导出与参数错误保留原 `EffectPackageException` 的字段和稳定 failureCode，拒绝不改变库修订。

### 24.3 试样选项与完整草稿候选

`POST /api/v1/template-preview-options` 请求为 `{source}`，source为精确 `savedTemplate` 四引用，或 `draft` 的 `draftRevision/definitionFormat:"full"/definition`；旧静态draft省略format继续工作。返回固定source、两个十二秒样本 `workshop-single-line-v1` / `workshop-multi-line-v1`（`durationUs:"12000000"`）、稳定目标key与原领域binding，以及 `unsungFill/sungFill/stroke` 三个材质槽的 `editable/selected/present/material`。目标按原 `PresetApplicationTargets.CanApply` 筛选；stroke不存在时present=false/material=null。默认是workshop单行样本。原三秒英文 `default-v1` 保留兼容用途，不改成十二秒样本。

`POST /api/v1/template-preview-sessions` 使用上述source/sample，新增可选 `targetKey/materialSlot`。workshop使用options公布的实际目标；materialSlot只是编辑焦点，仍运行完整模板，不隔离某一材质/配方。响应 `source.templateSample` 回显实际 `sampleId/targetKey/materialSlot`；default-v1不接受这两个新增选择字段。单/多行样本复用原TemplateSampleScene及自有媒体，生成的来源修订包含模板/样本/目标/槽焦点；后续PNG、诊断、SSE、关闭和资源排水沿原预览生命周期。

`POST /project-sessions/{id}/preset-application-previews` 新增互斥 `source`，提供source时原 `templateLibraryId/templateLibraryRevision/templateId/templateRevision` 四字段必须省略或null；省略source仍使用旧四引用。full draft与库模板共用固定闭包解析及原PrepareApply，仍核精确工程修订，不写库、歌曲或历史。正式保存模板与工程apply/update分别调用各自命令。

### 24.4 clips与高级Node应用

apply、候选及读回目标union新增 `{kind:"clips",branchId,clipIds:[...]}`。clipIds为1..1000个非空不重复ID，每项须属于指定来源；无关非null字段、重复/未知字段拒绝。读回按原绑定推导branchId并返回完整clipIds；目标删除后仍返回原应用及targetValid:false。

Node保留显式 `includeDescendants`，允许原模型合法的高级模板和branchInputs；模板kind/属性/配方写权、读取范围与bindingKinds交原 `PresetApplicationTargets.CanApply` 判断。原静态Node限制不再叠加；不兼容输入仍原子拒绝。apply/update仍固定完整模板快照、一次领域历史；no-op、undo/redo、保存重开、显式更新与候选隔离继续沿既有行为，不随库编辑自动改变已应用工程。

<a id="render-backend-selection"></a>
## 25. CPU/GPU 显式选择（0.106.0／合同1.39.0）

本节描述当前实现与合同；本机有限fresh HTTP和GPU不可用负例已通过，来源、硬件、媒体、回归及边界见[本批验收报告](../render-backend-api-acceptance-20261026.md)和[独立计划](../backend-render-backend-api-plan.md)。前端选择公开值 `cpu` 或 `gpu`，本批 GPU 实现为 Vulkan。选择属于预览/任务的执行配置，不写工程内容、修订、撤销历史或个人配置库。

### 25.1 设备可用性和部署默认

`GET /api/v1/render-backends` 沿现有 client 鉴权，返回 `defaultBackend` 和 `backends`。每项包含 `renderBackend`、`available`、`api`（CPU 为 `raster`，GPU 为 `vulkan`）、可用时的设备摘要 `device`，以及不可用时的 `unavailableReason`。摘要仅含 vendor/device/driver/apiVersion，不提供主机文件路径或驱动环境变量。

可用性来自隔离 Worker 的真实设备创建、表面绘制、提交和像素读回；与来源 profile、导出编码器及字体能力分别检查。它表示本次检查可运行，不保证后续设备故障、资源不足或来源执行永不失败。正式渲染 Worker 启动后也必须核对固定选择和设备能力，旧 Worker 不支持新核验时明确拒绝。

0.110.7 起，配置桌面 GPU stream 的 Host 使用其现有已准备 Worker 核对同部署设备，避免在两个
stream Worker 之外增开可用性 helper。备用准备包含真实 Vulkan／Skia 初始化及绘制，业务使用原设备；
未配置桌面流的浏览器入口仍沿独立设备检查。GPU stream 的活动、备用、开启和未完成排水共用两个
进程名额，最多一个未绑定备用；HTTP 静态帧、正式导出等其他 Worker 预算分别管理。
备用不绑定作者输入，每输入代次仍使用新进程身份；TTL、部署内容失效及失败退避不改变公开 DTO／路由。

部署默认在 Server 启动时从 `MAKO_RENDER_BACKEND=cpu|vulkan` 固定，未配置仍为 CPU。环境中的 `vulkan` 映射为公开 `gpu`；客户端不能把 `vulkan`、`auto`、空串或其他大小写作为公开枚举值。显式选择不修改 Host 环境、其它客户端、其它预览或已登记任务。

### 25.2 创建、更新和正式任务

以下请求新增可选 `renderBackend`：工程/作者草稿预览、模板试样、预设候选、编辑候选、工程正式验证、作者正式验证、导出、场景测量。创建/任务省略或 null 使用启动默认；编辑候选替换、原预览 `PUT .../input` 的省略/null 保留当前预览选择。候选测量省略/null 使用候选已固定的后端，显式选择只影响这次测量。

预览会话、帧元数据和事件回显固定的 `renderBackend`；任务在登记时把选择写入 input，验证/导出报告和测量结果也回显选择。它是执行配置，成功仍须依据预览 ready/PNG 或任务终态与完整报告判断。202 只表示接受。任务排队后不重新读取前端偏好或全局环境。历史任务/报告缺少字段表示当时未记录，不能推断 CPU 或 GPU。

静态、直接包、图片和统一 Runtime 输入沿原来源能力与资源授权，不因选择 GPU 扩大支持范围。GPU 失败不自动使用 CPU；前端若要改用 CPU，应显式发起新的选择/任务。

### 25.3 已有预览只切换设备

`PUT /api/v1/preview-sessions/{id}/render-backend` 请求为：

```json
{
  "expectedPreviewGeneration": "当前精确代次",
  "renderBackend": "gpu",
  "metadata": {
    "requestId": "本次唯一请求ID",
    "hostInstanceId": "当前宿主ID",
    "validUntilUtc": "本次有效截止时间"
  }
}
```

`renderBackend` 在此接口必需且不可为 null。metadata 沿既有回执合同。200 返回 `PreviewSessionResponse`；同一请求ID的重试按原回执重放，改变后端属于不同意图。

接口适用于 project、authorDraft、template、presetCandidate、editCandidate 五种预览，保留已固定 source、尺寸、透明模式、候选身份及完整媒体闭包，不重新捕获工程/草稿，也不产生工程历史。原 `PUT .../input` 的来源切换限制继续保持。

服务先核对所属 client 和精确 generation。相同后端为 no-op，保留 generation、已发布帧和诊断。不同后端在锁外完成设备和来源检查，提交时再次核对原会话/代次，成功才原子换代、清理旧帧与诊断并使旧在途结果失效。失败释放新准备引用，保留旧输入和旧代次；旧 Worker 的释放沿原协调器排水。后续首帧仍可能因脚本、媒体或设备运行错误失败，这不属于切换检查成功的保证范围。

前端收到成功响应后采用返回的新 generation，随后发起新的 frame-request；继续丢弃不匹配当前身份的晚到帧。并发修改导致的 `previewGenerationConflict` 应通过 GET 状态重新同步，不能强行复用旧代次。

### 25.4 错误和验收边界

非法字段为400 `invalidRequest`；不支持、不可用、实际设备不匹配或核验回执错误通过 `field:renderBackend` 和稳定 failureCode 返回。当前设备层编号为 `RenderBackendUnavailable`、`RenderBackendUnsupported`、`RenderBackendMismatch`、`RenderBackendProtocolInvalid`；业务来源错误继续使用原 source/profile 编号。设备不可用不返回替代帧或成功任务。

本批在当前Linux/RADV机器完成五类预览CPU→GPU→CPU、no-op/回执/旧generation/ACL、两client独立选择、固定来源在原工程修改/关闭后切换、input省略保留GPU、正式验证/短片导出及测量的有限HTTP矩阵。硬件采样对GPU帧和运行中任务绑定新鲜Worker身份；同步测量只定位请求区间内硬件Worker，不能精确区分探测和测量执行Worker。合同形状、CPU回归、HTTP、媒体和进程排水分别登记。本批不增加GPU视频编码接口，也不宣称浏览器、跨平台、性能、独立Runtime内容像素Oracle或CPU/GPU位级像素一致性。

<a id="sdk2-m08-particles"></a>

## 26. SDK2 M08 精确粒子与晚期画布输出（0.112.1／合同1.44.0）

本节固定 M08 已接入的包执行行为，HTTP `protocolVersion` 仍为1。本轮没有新增路由、请求／响应
DTO、HTTP 粒子表或工程命令，schema 与合成 wire 例继续为597和3434例（2548反例），112路径／
125方法。C# SDK 的结构身份、出生窗和晚期视图属于 Worker 内的执行合同，不把这些值直接序列化成
新的公共响应。包导入／检查、作者草稿／编译／采用、工程固定输入、浏览器预览、验证和导出沿现有
入口、owner／CAS、修订、任务及代次规则执行。匹配生成物、Server／Worker 发布、fresh HTTP、
GPU 像素和性能验收分别登记，本节不宣布这些验收已经通过。

### 26.1 包声明、阶段与 GPU 准入

SDK2 使用 `packageVersion:3`、`sdkVersion:2.0.x` 与 `shaderProfile:skia-ganesh-3.119.4-v2` 的明确
profile；未知 profile 不回退 SDK1。`requires` 中以下能力已接入实际执行：

| 包能力 | 固定执行用途 |
|---|---|
| `particles.continuous` | `cursor` 发射器的歌曲时间常速／线性速率轨，真实播放头与合法 Bridge 来源 |
| `particles.burst` | `burst` 发射器的固定真实事件行，按源事件和明确锚点出生 |
| `visuals.dots` | 自动粒子圆点与具名晚期圆点批次 |
| `visuals.segments` | 具名晚期平端线段批次 |
| `visuals.trails` | 具名晚期带状拖尾批次，每个节点计入绘制预算 |
| `visuals.sprites` | 本固定包修订图片的完整仿射精灵批次 |

这些是包清单能力名，不是 HTTP capabilities 的新增布尔字段。`emitters` 必须具有唯一 `id`、
明确的 `cursor|burst` 种类以及1至5000000整数微秒的 `maxLifetimeUs`，并在包 `requires` 中声明
相应粒子能力。controller 的具名画布输出使用 `kind:Overlay`、`target:canvas`，输出自身的
`requires` 同样必须覆盖实际能力。连续／Burst 计划引用同名、同种类发射器；自动圆点的该输出还需
`visuals.dots`。Declare 返回后再次核对固定计划、寿命、能力、具名输出的种类和层，不能仅凭清单
通过或作者构造了合法对象跳过运行门禁。晚期 Overlay 不能作为文字输出或发射轨的依赖。

顺序为固定 Declare／计划准备、当前文字、出生时刻最终文字锚点补算、解析存活粒子、可选
`EvaluateVisuals`。晚期只能读取本实例粒子和最终几何并写声明的临时图元，不能反馈速率或改写文字；
调用结束或失败后视图／writer 失效。精灵句柄必须属于该实例固定包修订。`Custom` 关闭自动粒子
绘制，保留粒子运动、完整出生窗和所有预算。

原 SDK2 GPU 门禁继续用于工程和作者预览、正式验证与导出：非 GPU 选择在运行输入准备前以
`commandNotSupported/PackageBackendUnsupported`、`field:renderBackend` 拒绝；运行时仍验证实际
Vulkan 设备，失败不回落 CPU。拒绝预览设备切换继续保留原固定输入、代次和已发布帧；HTTP `202`
只表示已接受，不能据此认定脚本、粒子或绘制成功。

### 26.2 出生、寿命与最终锚点

粒子使用实际实例、发射器以及完整 Continuous／Burst 来源构成结构身份，不压缩为旧的稠密
BirthIndex。连续出生对原始 IEEE-754 速率作精确有理数积分和整数阈值反解；普通 head 在同轨道、
同装载的相接 token 间保留配额，真实 gap、disable 或 reload 重置；每个真实 Bridge 转换独立。
同 head 的重叠 Current 来源以 `EmissionHeadAmbiguous` 拒绝。Burst 绑定真实固定事件、计划序号、
实际锚点和粒内序号，不能仅凭时间相同冒充事件；相同边界保留既有源事件排序。

当前存活条件为 `birthUs <= timeUs < deathUs`。每次查询同时固定闭出生窗
`[max(0,timeUs-5000000),timeUs]`，其中已死亡粒子仍可读取，不能从 Alive 反推完整 Births；文字
离场不提前杀死粒子。出生锚点取该真实出生时刻的最终文字、基础字／Ruby 分区及层次变换，之后
散布、恒加速度、年龄外观与绝对歌曲120Hz拖尾节点按固定值解析，不逐帧积分。

默认锚点采样为 `Exact`。只有完整必要文字依赖均声明同区段连续时，显式 `Continuous120Hz`
才可在固定网格插值；断点和真实来源边界继续拆分。不能因耗时、预算或 memo 命中情况自动降低
精度。若真实锚点依赖有状态 SDK1 文字输出，拒绝 `FinalAnchorLegacyDependencyUnsupported`；
无关绑定的 SDK1 输出不扩大拒绝范围。补算时刻只调用绑定范围在该时刻含文字、行、标记或可写组的纯实例；
范围为空的实例不能写文字（越界写入一律拒绝），不调用也不计入“实际文字实例调用”，模板逐行应用的
补算成本因此随该时刻在场行而非全曲行数增长。正式帧同样只调用这些纯实例（含宿主直接图，0.112.48）：
纯求值没有跨帧状态，范围为空时输出必为空，不调用不改变像素、诊断与锚点；SDK1 实例照旧逐帧调用。基础场景测量（正式帧与补算共用）的场景定义、样式解析器与实际父链底层只依赖不可变工程输入，0.112.49 起按输入引用复用，每帧只为在场行求一次父链并按（分支，行）索引在场装载，不再随全曲行数重建；结果与像素不变。有预设应用或退出副本时的逐帧样式基线（0.112.50 起）由会话基线派生：不在场行与场景无关、原样共享，只重算本帧装载行、其元素与标记，与完整重建逐目标相同。补算判断各实例能否写入锚点来源时（0.112.51 起），同一来源同一时刻只建一次绑定范围、来源帧对象与运行父树，逐实例只做范围查询，判定与拒绝码不变。0.112.52 起，帧求值对 Lines／Tokens 绑定的实例先按本帧在场行与元素身份判定，绑定的行或元素都不在本帧时直接取空范围（Lines 不含组、Tokens 不含标记与组，与逐目标查询结果相同），不再逐目标扫描；容器定义与几何按（不可变场景定义，合成尺寸）复用，定义层父关系按定义复用、本帧装载与提示附着只叠加小覆盖层。0.112.53 起，同一快照的样式解析按（行，文字对象，片段，实际父链）记忆，预设投影行按（源行对象，片段，实际父链）复用同一投影行对象，测量、布局与绘制的行基础样式按行对象记忆并核对工程样式与分支覆盖引用；解析失败不记忆、照旧抛出，结果与逐次重新解析逐项相同（各记忆有容量上限，超出后照常解析）。0.112.54 起，展示片段的跨度与校验按（分支对象，程序引用或值相同）复用，逐时刻只挑选跨度、默认段成员按（起，止）复用，校验失败不记忆、照旧抛出；渲染路径按行 ID 定位源行不再复制分支行数组；节点测量对全曲定义的遍历不再为每个文字元素分配闭包。0.112.55 起，整行与逐字／字簇几何在每个渲染管线内按行对象记忆最近一次结果，键为行基础样式、分支、装载身份、逐字与行文字是否一致及本帧行原点、基线与世界矩阵；任一变化即重测，命中时复用不可变测量结果。0.112.56 起，Hosting 启动的 Worker 子进程默认设置 `DOTNET_GCgen0size=0x4000000`（gen0 预算 64 MiB，约 +100 MiB 常驻内存），运维已显式设置 `DOTNET_GCgen0size` 或 `COMPlus_GCgen0size` 时沿用其值；Server 进程不受影响，像素与协议不变。0.112.57 起，图／材质逐像素检查（预乘颜色扫描、masked 材质越界扫描、为检查而做的同步提交，以及图像效果场景为共享空表面所做的部件 alpha 读回）只在作者草稿试样、包与运行时校验任务及进程内调用（测试、公开 `CompiledImageGraph.Compile`）开启；正式项目预览、GPU 流与导出关闭。Worker 由会话打开请求的可选 `graphPixelChecks` 决定（其他种类携带即拒绝，缺省为关）。关闭时作者错误输出原样渲染，不报 `GraphColorNotPremultiplied`／`MaterialOutputOutsideRegion`；结构、尺寸、密度、色彩空间与预算检查照常。未发起任何绘制的原始部件仍共享空表面；发起过绘制但实际全透明的部件保留独立表面，像素不变，表面账可能多占。0.112.58 起，包图校验（`PackageImageGraphValidator`）显式使用 CPU 设备编译，不再随进程 `MAKO_RENDER_BACKEND` 另建 Vulkan 设备；GPU Worker 的设备状态与预热绑定证明要求进程内恰好一次设备／上下文初始化，此前含图或材质包的 GPU 流打开因此被拒（前端显示请求结果未知），像素与协议不变。0.112.59 起，GPU 流硬失败（`drained=false`）的 `failed` 消息在原 `code` 之外附带 `failureType`（根异常类型名）、`failureSite`（抛出方法）与 `failureDetail`（仅图／中间表面预算／GPU 预览引擎文本，最多 512 字），代码改为沿内层异常取首个稳定代码（如 `GraphRequiredInputUnavailable`、`ImageSurfaceBudgetExceeded`），不再一律报 `WorkerGpuStreamFailed`；桌面主进程把经格式校验的这些字段写入 `desktop-broker-error`／`desktop-fail-stop` 日志并以 `workerCode` 告知渲染端，作者与脚本文本不进入，像素、排水与 HTTP 合同不变。0.112.60 起，背景媒体每次渲染只向帧来源取一次帧（主画布与各材质／图背景输入共用；视频帧本次渲染固定一份不可变副本，图片背景解码后即不可变，绘制共享同一纹理），同一行／标记的多次材质执行（未唱／已唱、正文／注音）共用背景捕获，空闲时可被预算回收，像素与合同不变。0.112.61 起，不透明输出缺背景媒体（未设置、文件丢失、解码失败或视频该时刻无帧）不再使整帧失败：必需的 BackgroundMedia 图输入与透明输出同路旁路、材质退回包内基础外观，诊断原因为 `BackgroundMediaUnavailable`（预览诊断说明写明原因，Worker 包诊断映射接受该原因且只对声明了媒体依赖的必需输入）；可选的 BackgroundMedia 输入以工程底色捕获代替，正常执行、无诊断。Worker 静态导出与静态渲染会话仍按既有约定拒绝任何材质／图诊断（与透明背景依赖相同），这两条路径上缺背景媒体仍以 `WorkerExportDiagnosticsUnsupported`／`WorkerDiagnosticsUnsupported` 失败，处理方式另行约定。0.112.62 起，场景表面（下层快照、部件与材质区域捕获、背景、层组装、透明度）绘制后只提交、不等待 GPU 完成，CPU 可与 GPU 重叠；同设备后续绘制按提交顺序执行，像素检查等读回路径由读回函数自行同步，帧交付（CPU 读回、GPU 帧复制）照旧同步，像素不变。0.112.63 起，行／文字标记上的材质按本帧对象实际区域执行：目标框为各元素、注音、字簇墨迹与 Advance 框加描边、阴影、辉光外扩后映射到画布（加 2 px 余量，与画布相交），再与程序框相交；独立对象（不经组合调度）的工作框随之收紧并按输出像素对齐，中间表面不再按整张画布分配。材质输出受区域覆盖遮罩，CPU 光栅像素逐字节不变；Vulkan 下阴影、辉光等 Skia GPU 模糊随源表面尺寸与原点变化，换框后有 1～3／255 的差异。程序声明的 `Bounds` 仍是外限，公开协议不变；含 Transform 节点的材质图、组合调度的工作框、以及图导出仍按程序框（整张画布）执行——图根框按合同同样来自目标区域，收紧另行安排。0.112.64 起，行／文字标记对象先判空再分配：本行没有注音时，注音描边、阴影、填充部件与注音填充材质不再捕获或执行；行基础样式、元素覆盖、本帧装载源外观、脚本字族与基础字外观都未启用描边（或阴影）时，原样描边（或阴影）部件不再捕获。跳过的部件仍以共享空层占原位，层数、层序与输出像素不变；描边材质仍按自身区域执行。被跳过的注音材质执行不再产生透明背景、缺背景媒体等旁路诊断；同一绑定在基础字上照常执行时诊断不变。图像素检查开启时判空部件仍按原样部件捕获（表面与读回次数同前）并核对，判错即以 `SceneColorPartKnownEmptyMismatch` 失败。0.112.65 起，对象层组装（含图的目标颜色输入）把相邻的无历史层在同一张表面上依次 SrcOver 绘制，共享空层直接跳过，只在有历史（图链）的层处断开，不再逐层整张复制；像素逐字节不变，组装表面由「1＋层数」张降到「1＋图链层数」张左右，预算峰值不变。组合调度路径的逐层导出不受影响。0.112.66 起，Skia 管线对同一请求实例只做一次准备帧门禁（源材质、输入指纹、快照、时刻、目标、输出模式），对同一准备帧实例只做一次原生范围校验（矩阵、几何坐标、渐变、叠加图元）；帧内各次捕获不再重复。0.112.67 起，行、元素族与文字标记上的图导出按绑定目标的实际区域执行（区域算法同材质；元素族图只取该元素的基础字、注音与字簇），目标框再加同一对象内先于它、产物流入它的图的声明最大外扩之和，含模糊节点时按该帧物理 σ 对应的 Skia 降采样倍数把框对齐到物理像素网格，与程序框相交；含 Transform 节点、目标无墨迹或任一前序图未收紧时沿用程序框。独立对象与组合调度两条路径得到相同的目标框；独立对象的工作框随之收紧，图输入、临时表面与输出不再按整张画布分配。0.112.68 起，GPU 流 Worker 的启动时钟校准在单次往返不确定度超过 25 ms 时重新发起（一次流内最多 8 次），校准成功才发 `clock-calibrated`，用尽次数后才按原样回报 `calibrated:false`；此前只校准一次，对端一时繁忙（如换源时桌面主进程忙于接入新流）即被桌面端当作协议错误停流。桌面端在收到 `clock-calibrated` 之前本就不下发播放控制，私有协议的消息形状与HTTP合同不变。0.112.69 起，整形、注音与文字标记布局、场景测量和装饰外扩量由 `SkiaSceneGeometry` 承担（实现 `ILineGeometryProvider`，只持有排版引擎与布局缓存），`SkiaRenderPipeline` 只负责绘制并把几何接口调用转交给自己持有的几何实例；工程会话不再为脚本宿主另建绘制管线，一个会话只有一个绘制管线，脚本宿主拿到的是与绘制共用布局缓存和渲染设备同步对象的几何实例。纯搬迁，像素、诊断与协议不变。0.112.70 起，图像效果场景里每个对象（一行或一个文字标记）先建立只含数据的对象计划（部件清单及其原样／材质／已知为空、图的执行序列与各自目标框、工作框、下层来源、是否导出给组合调度），再由执行器照计划分配、捕获、执行与合成；两个调度器调用同一入口。像素、诊断代码与失败归属不变；部件判空由取下层快照之后提前到建计划时，仅当计划期错误与快照预算失败同时成立时先报前者。0.112.71 起，对象计划分步建立：基本计划（身份、程序、图的执行序列，区域取程序框）→ 区域收紧（O7 材质区域、第 12 条图目标框）→ 工作框定稿（辉光外扩、像素对齐）→ 部件清单 → 判空（F3）→ 层组装方式（F4）；三个优化各是只改计划的一步，层组装方式由计划带给执行器，不再在组装时读管线开关。像素、诊断与表面数不变。0.112.72 起，组合调度（前缀调度）下对象的工作框也按自身范围收紧：对象本帧墨迹与装饰外扩的范围并入各图目标框加外扩、各材质工作框以及它们声明的取样偏移，按输出像素对齐，不超出调度器给定的框；逐元素几何不能代表整个对象、没有可绘制墨迹，或没有材质的对象带网格映射（Warp）时沿用调度器的框。对象在自己的工作框内解析下层：进入前缀只需同帧、同画布、同密度与输出模式，前缀与下层的合成按对象的框裁剪。CPU 像素逐字节不变，Vulkan 在既有容差内；对象的部件捕获、闭合层与导出载荷不再按整框分配。0.112.73 起，声明了下层、背景底色或背景媒体输入（`BackdropBelow`／`BackgroundColor`／`BackgroundMedia`，含可选来源）的图与材质，其取样偏移（各节点 `maxSampleOffsetPx` 之和）在两条调度路径上都并入收紧后的工作框：独立对象（遗留调度，以目标快照作下层）不超出收紧前的整框（程序框加外扩），此前只含声明外扩，取样偏移大于外扩时在目标框外读到透明或边缘像素，与整框结果不一致；只读对象自身输入（目标颜色、各类覆盖）或包图片的图与材质不再并入（对象自身的捕获在目标框外本就透明，包图片不按工作框裁剪），组合调度下这类对象的工作框比 0.112.72 小。现有样例包中只有 GlassInk 读取下层与背景媒体（取样偏移 24＋8），其独立对象的工作框与中间表面各边随之变大 32 px，读取仍落在原工作框内，CPU 像素不变；Vulkan 模糊随表面尺寸与原点变化，在既有容差内。0.112.74 起 Core 样式改为两态外观、跟随未唱、多层描边与扫字样式，工程／歌词／模板 schema 为 31／19／9；HTTP 形状不变，按旧单层描边与 26 项属性换算（§6.11）。不使用新能力的内容 CPU 像素逐字节不变。0.112.75 起渲染按态绘制描边、阴影与辉光（§6.11），装饰外扩与部件判空取两态；不使用新能力的内容 CPU 像素逐字节不变。0.112.76 起渐变支持硬边分层与按单字坐标，整行坐标的纵向参考框改用字体度量，注音的整行渐变横向取底字整行（§6.11）；同版修复径向渐变半径取了倒数的缺陷；不用整行坐标、没有硬边的线性渐变 CPU 像素逐字节不变。0.112.77 起多重描边接入，阴影与辉光以整体轮廓为来源并画在描边之下，图场景新增外圈描边部件（§6.11）；不同时带描边与阴影／辉光的内容 CPU 像素逐字节不变。0.112.78 起注音的描边、阴影、辉光按注音比例缩放（§6.11）；不带装饰的注音像素不变。0.112.79 起扫字羽化在直接绘制与原样捕获中生效（§6.11），填充包材质按两态拆侧的图场景暂以 `WipeFeatherMaterialPending` 拒绝；羽化比例为 0（默认）的内容 CPU 像素逐字节不变。0.112.80 起图场景中填充包材质按两态拆开的部件在过渡带内按 m 合并成一层（§6.11），不再拒绝；羽化比例为 0 或本帧没有过渡带时像素与表面峰值不变。0.112.81 起扫字线高亮生效（§6.11）：峰叠在填充与各圈描边上，三种混合按未预乘颜色计算、缺省为滤色，与羽化无关；未开高亮（默认）的内容像素不变。0.112.82 起脚本 SDK 两态读写外观与扫字样式（`TextState`、`SetWipe`、能力 `style.wipe`），描边材质槽分为两态，两态材质不同时按侧拆开（§6.11）；脚本两态写同一值、两态描边同一材质的内容像素不变。0.112.83 起脚本渐变坐标删去 `Progress`、新增分层便捷工厂 `GradientValue.Bands`（§6.11），不影响已有内容的像素。0.112.84 起能力名集中为 `CapabilityNames`、帧与输出句柄的构造和完成成员改为宿主内部、新增泛型入口 `IPureKaraokeEffect<TPrepared>`／`IPureVisualEffect<TPrepared>`（§6.11），只改作者可见表面，像素与时序不变。0.112.85 起公共 HTTP 样式改为两态形状（合同 1.50.0，§6.11）：43 项属性路径、已唱跟随项、最多 4 层描边、扫字样式、模板属性路径与前奏两态外观，删去渐变 `progress`，材质诊断槽放宽为 0～3；与 1.49.x 客户端不兼容，渲染、像素与时序不变。0.112.86 起导出与同帧测量在图效果或材质缺背景（透明输出、不透明输出无可用背景媒体）时照预览兜底，不再失败（合同 1.51.0，§11.3、§13.21）：导出报告新增去重的 `fallbackDiagnostics`、`fallbackFrameCount` 与 `fallbackDiagnosticsTruncated`，job 结果新增 `fallbackDiagnosticCount`，测量新增 `fallbackDiagnostics`；预览的 `TransparentBackgroundDependency` message 改为写明原因；没有这类依赖的工程像素与导出字节不变。请求与准备帧不可变，只按引用记且失败不记，内容不同的新请求实例仍完整校验，错误码不变。memo 只保存完整成功查询的最终锚点，不保存半帧输出；
取消和失败不发布新条目。

### 26.3 独立预算与诊断

准备／帧查询账本在宿主内按全部实例累计（发射器数例外：按实例计，与清单上限一致，模板逐行应用
产生的多个实例各自受 32 约束）；作者不能放大以下硬限额。原工程绘制预算还会约束
实际图元输出。数量和保守字节记账是完整性门禁，不是运行内存峰值或性能指标。

| 范围 | 默认上限 |
|---|---|
| 单粒寿命／清单最大发射器数 | 5000000 µs／32 |
| 准备账本 | 发射器每实例32；以下全部实例累计：速率段与启用窗合计4096；来源区段与 episode 合计200000；Burst 行50000；256 MiB |
| 精确整数 | 4096 bits |
| 一次帧查询 | 完整出生50000；Alive 20000；反解操作4000000；拖尾节点200000；64 MiB |
| 最终锚点补算 | 唯一时刻4096；实际文字实例调用32768；寿命窗外／未来端点1024；累计补算挂死保护15000 ms |
| 最终锚点 memo | 2048条目／64 MiB，达到保留预算按有界缓存策略淘汰 |

正常内容的锚点补算是否失败只由唯一时刻、文字实例调用和端点三项次数预算决定，同一工程在不同机器上结果一致；15000 ms 只是挂死保护（低于 Worker 单帧 20 秒操作期限），不是性能验收阈值，也不用于筛选粒子或改变采样算法。固定预算、
取消、非法来源或非有限几何均失败，不截断出生表、不少画图元、不吞掉失败实例。

包读取错误保留 `PackageEmitterBudgetExceeded`、`PackageEmitterDuplicate`、
`PackageEmitterKindUnsupported`、`PackageEmitterLifetimeInvalid`、`PackageCapabilityMissing`、
`PackageOutputTargetUnsupported`、`PackageOutputStageDependencyInvalid` 等具名原因；不支持的
能力仍为 `PackageCapabilityUnsupported`。准备／查询和晚期失败通过现有 runtime 诊断载荷传播，
保留实际 `instanceId`、artifact／包身份、阶段与时刻，新增原因不意味着新增顶层 HTTP `code`：

| 阶段／范围 | 主要具名原因 |
|---|---|
| Declare／计划准备 | `InvalidParticleDeclaration`、`UndeclaredCapability`、`UndeclaredEmitter`、`UndeclaredPackageOutput`、`ParticleLifetimeExceeded`、`InvalidParticleRate`、`InvalidParticleSource`、`EmissionHeadAmbiguous` |
| 准备预算与精确运算 | `ParticlePrepareBudgetExceeded`、`ParticlePrepareMemoryExceeded`、`ParticleIntegerBudgetExceeded`、`ParticleTimeOrIdentityOverflow` |
| 帧粒子预算 | `EmissionRecordBudgetExceeded`、`LiveParticleBudgetExceeded`、`TrailSampleBudgetExceeded`、`ParticleInversionBudgetExceeded`、`ParticleFrameMemoryExceeded` |
| 最终锚点 | `FinalParticleAnchorUnavailable`、`FinalAnchorLegacyDependencyUnsupported`、`FinalAnchorPrepareBudgetExceeded`、`FinalAnchorTimeBudgetExceeded`、`FinalAnchorTextCallBudgetExceeded`、`FinalAnchorFutureEndpointBudgetExceeded`、`FinalAnchorWallBudgetExceeded` |
| 晚期图元／结构 | `InvalidVisualDeclaration`、`InvalidVisualOutputStage`、`InvalidParticleFrame`、`ParticleOutsideInstance`、`DuplicateOverlayBatch`、`InvalidOverlay`、`InvalidSpriteResource`、`VisualOutputBudgetExceeded` |

业务终态仍从现有 preview／job 状态和完整诊断读取；HTTP 接收成功、合成样例通过或有限工具完成
均不能替代真实帧／任务完成。取消继续沿 `OperationCanceledException` 和原任务取消语义传播。


<a id="sdk2-m09-svg-warp"></a>
## 27. SDK2 M09 Warp 与固定 SVG（0.112.8／合同1.45.0）

正式准入 `token.warp`、`cluster.warp`、`mark.warp`、`visuals.paths`、
`resources.svg.vector`、`resources.svg.atlas`。Warp 沿唯一文字映射同时作用于几何、
最终锚点及像素；SVG vector 使用 `PathBatch`，atlas 使用 `AtlasSpriteBatch`，
后者同时需要既有 `visuals.sprites`。两者声明为 controller 的 `Overlay/canvas`，
绘制和资源 capability 均需声明。句柄固定包修订与 atlas variant，不接受跨修订句柄。

SVG 的 `svgResourceVersion:1`、`mako-svg-static-1/grammar-2` 采用受控内存闭包。
外部 URI、脚本、动画、文字／机器字体、未知语法和未声明图片均具名拒绝；
clipPath 仅允许直接形状，clip 内 g/symbol/use 明确拒绝。包图片和 data 图片完整解码，
图片、展开命令、filter、atlas padding/mip 和共享表面均计预算。
固定 Picture／独立 atlas 页与 GPU 纹理按会话持有，渲染只借用；关闭与失败按资源所有权释放。
半透明 tint 作用于整体矢量结果，atlas 使用完整仿射、独立 padding 页与 mip 采样。
取消不转译成脚本错误，运行失败保留真实实例、包修订、导出与阶段。

作者完整包文本读取／保存扩展详见§21.5。保存允许保留可修复的无效 SVG 正文，
但正式同步／异步编译先验证全部 SVG，失败返回资源代码与包内路径，产物为 `none`。
修复后生成新源码修订，必须重新编译并显式采用；历史失败报告和在用修订不被覆盖。
该静态校验不创建 Picture 或 GPU 纹理，不替代实际 GPU 会话准备和像素验收。
资源读模型把 svg/svgResource 识别为受支持角色，未知 role 仍标为 unsupported。

本节的0.112.8历史切片未新增 HTTP 入口或 DTO，保留原浏览器静态预览协议与500ms控制节奏。
当时图绑定、Base材质、完整组调度及浏览器完整包文本编辑尚未接通；后续0.112.9～0.112.13的
图／材质／组实现见§28，作者精确声明与完整配置采用见§21。媒体轨和九样例继续按阶段计划独立验收，
不能将本节0.112.8证据扩大到后续实现或浏览器可用性。


<a id="sdk2-m09-graph-material-group"></a>

## 28. SDK2 M09 图绑定、Base材质与真实组（0.112.9～0.112.13／合同1.46.0）

本节记录已进入当前源码的G1～G4执行规则：0.112.9完整图值与动态Transform，0.112.10 typed
controller图事务，0.112.11无DLL直接图与材质uniform，0.112.12独立Base包材质，以及0.112.13
真实组调度与owned Below。它们沿现有包导入、作者、预览、验证、导出入口传递固定输入，不新增
本节专用HTTP路由或DTO；1.46.0新增的作者声明检查与完整配置采用另见§21。包仍须为明确支持的
v3／SDK2 profile，生产运行保留§25的Vulkan准入和实际设备核验，不因CPU参考执行器回归而允许
公开路径回落CPU。SDK接口见[SDK 2.0参考§5](../../../docs/style-reference/script-sdk-v2.md#5-文字输出)，
组像素与父树语义详见[组图合同](../scripting-group-image-composition-contract.md)，实施证据和剩余
门禁见[M09记录](../../../docs/plans/script-sdk-render-m09-evidence.md)。

### 28.1 完整图值、动态Transform与事务

`GraphBindingValue` 固定本实例包修订中的GraphHandle、完整ParameterValues、逐节点uniform及
Transform覆盖；每次`TextOutput.BindGraph`整体替换，不与上一绑定合并。值身份按参数曲线内容、
uniform类型／数组及数值位、矩阵／输出框内容比较，保留shader可观察的正负零。同实例同目标
最后绑定生效，跨实例沿工程顺序；每次原始写入仍检查固定资源、完整参数、声明与目标写权并计费。
固有坏值和预算失败锁存，作者捕获异常也不能发布部分事务；最终图节点、uniform ABI及Transform
节点兼容性只针对存活完整绑定，不能把旧图覆盖自动套到新图。

Transform覆盖须指向固定图真实Transform节点，提供完整源至目标矩阵及语义输出框。形状预检、
分配前核验和实际绘制共用原生float32解释，核对有限／可逆、非空框、声明外扩与当前密度对齐后的
完整逆采样范围；未执行覆盖和透明旁路也不跳过校验。保留v1固定Transform的历史采样声明语义；
Decal／Clamp／Repeat读取完整有限源域，不通过裁短输入改变边缘或周期。ColorOnly图改变实际颜色，
不改写最终文字几何、播放头或粒子锚点映射；Mapping仍以`PackageGraphMappingPending`拒绝。

`image.graph`已准入Family／Line／Mark，真实Group另须§28.3的双能力与父树门禁。controller及
实际graph须各有唯一匹配的ImageGraph输出；同类别多输出、额外能力及跨输出依赖仍沿
`PackageOutputPlanPending`拒绝。无DLL直接图采用独立宿主活动，不编译C#、不创建作者对象或调用
Declare，按宿主实际授予的Family／Line／Mark／Group范围发布同样的固定绑定；token绑定不能扩大
成整行，Base／Cluster图输出仍未开放。

### 28.2 最终材质uniform与独立Base

`material.uniform`通过`TextOutput.SetMaterialUniform`明确定位真实Family／Base／Mark、
UnsungFill／SungFill／Stroke部位、固定MaterialHandle及`nodeId/name`。跨实例按原始材质替换与
uniform调用顺序归约；完整替换清除旧覆盖作用域，后续覆盖必须匹配最终安装材质及真实节点ABI。
每次原始写入的固有类型、资源、参数和float32范围均独立校验，不能由后写替换掩盖；未修改的
opacity／affine不抢走材质错误owner。覆盖只属于本次帧，不修改包、工程参数或shader源码。

自0.112.12起，Base包材质已接通两态填充、描边和uniform，不再沿早期BaseOnly拒绝说明处理。
Base从最终Family中立外观继承，逐部位保存真实Source／Script owner；Solid／Gradient或
Stroke=null明确遮蔽父材质。先归约父族uniform，再归约Base作用域；Base捕获不含Ruby，Ruby保留
父族外观，后继Family／Line图消费实际Base＋Ruby颜色。材质不重复Warp，装饰范围由最终映射计算；
资源、预算及native失败保留实际材质owner与Base TextTarget，Warp几何错误仍归Warp，取消透传。
此处开放独立Base材质，并未开放Base图或独立Cluster材质输出。

### 28.3 真实组、独立颜色／擦除与owned Below

组图须同时声明`image.graph`与`image.graph.group`；controller和实际graph的唯一
ImageGraph/group输出均须显式声明组能力。只允许Project或显式Node授予的真实运行父树范围，
分支／行／元素／片段筛选不授予组写权。目标须为真实source Group、无SourceNodeId alias且有
对应最终几何；冻结退场alias可参与渲染及读取，不能独立获写权。v1组输出与缺能力沿
`GroupImageEffectUnsupported`等具名错误拒绝；不具备真实父树调度的旧单绑定执行入口仍拒绝组。

主画布沿真实运行树隔离：子材质／子图／子组先完成，父图读取已处理部件。每层保留独立预乘颜色C
与擦除E，对下层B按`C+B*(1-E)`应用；同目标历史replacement floor取max，独立孩子按绘制序合成。
父图变透明也保留孩子已发生的擦除，不能用普通输出alpha替代历史floor。孩子共同透明度在父捕获前
完成一次；有效部分保留来源与顺序，完整连续选择另用先合成C/E再乘alpha的产物，成功写入使触及
的完整范围失效。每输入按自己的真实范围／部位配方选择，未选部分保留；已经融合的来源不能再拆选
一部分，融合结果放在所选最高层。交错后代改为连续隔离单位时保留`GroupIsolationChangesOrder`诊断。

父BackdropBelow固定为进入组时的组外前缀，孩子读取此前缀加已完成兄弟，普通前序leaf也参与；
前缀保留完整C/E，不能用背景媒体或颜色alpha代替。真实背景在BehindText前只快照一次，
Dots／Segments／Trails／Sprites／Paths／AtlasSprites六类批次按原序重绘：Script请求仅排除
直接同实例BehindText，Source材质不排除脚本。图、Family材质和Base材质共享resolver，同一leaf
各部位读取固定进入前缀；不同owner不复用过滤表面。最终主画布保留全部真实BehindText，已完成
颜色曾主动采样的图元不递归净化，不重跑作者图或Below背景provider；显式BackgroundMedia沿原
独立捕获协议，此切片不开放target owned-overlay捕获。

隐藏但存在的组仍完整验证身份、参数、资源、声明、尺寸和图峰值预算。无实际可见孩子记录
`GroupImageEffectNoVisibleChildren`并消费绑定，不执行图或生成凭空内容。非空组有active target
输入但配方选择并集为空时，记录`GroupImageEffectNoSelectedChildren`且保留原C/E、来源与顺序；
只有完全没有active target输入才选择整个scope。部分输入为空而并集非空时，空输入提供零颜色／覆盖。

### 28.4 固定预算、诊断与证据范围

图／Fill／Stroke／uniform在SDK调用事务及全部实例帧准入共用写入预算，包含后续被替换的原始值。
捕获、颜色／擦除、有效层、Below、作者图与宿主合成产物共用会话／帧表面账，分配前检查，拥有资源
在成功释放、失败或取消时归还；借用输入保持原所有权。默认门禁如下，逻辑记账不代表完整native RSS
或性能上限：

| 范围 | 固定限制 |
|---|---|
| 图／材质原始写入 | 256次、65536标量、8MiB保守验证字节 |
| 组调度 | 32层递归、256层祖先、4096运行单元、256组绑定、256图结果 |
| 活动来源与实际工作 | 512活动来源层、8192累计捕获／图／合成pass |
| Below重绘 | 4096批次、200000实际图元／轨迹点；SVG继续扣原native工作账 |
| 默认图／表面账 | 每图32 pass、每帧256活动表面、会话512MiB估算像素存储、表面每边8192像素 |

来源层在实际捕获前逐层登记，真实分配入口累计pass；缺失背景、借用输入及缓存命中不虚计新pass。
空组／空选择同样先作完整图峰值预检，层数、表面数、字节、取消分别生效。超限沿
`GraphOutputBudgetExceeded`、`GroupCompositionBudgetExceeded`、`ImageSurfaceBudgetExceeded`等
原具名诊断失败，不降质、截断或遗漏实例；诊断保留实际实例、固定包／artifact、阶段、时刻及目标。

G1～G4已有真实包、CPU参考、显式Vulkan、像素／确定性、预算红绿、取消与资源恢复回归，具体版本、
数量、设备和共享输入限制均以[M09记录](../../../docs/plans/script-sdk-render-m09-evidence.md)为准。
这些回归不等于匹配Server／Worker的fresh HTTP、浏览器或导出验收；0.112.8的历史HTTP和0.112.14
作者metadata／adopt的HTTP结果也不自动覆盖图／材质／组同帧矩阵。该矩阵仍须独立收口，本节不宣布
M09关闭；媒体轨、九样例及后续作者完整链继续按阶段计划登记，性能门槛按用户指示暂不验收。


### 28.5 固定场景目录纯度准入（0.112.15／合同1.46.1）

SDK2作者可读取固定SceneCatalog中的SourceNode、SourceProgram、SourceClip与SourceCue全部只读值，
包括真实根组／默认容器、父节点、片段成员及前奏关联；这些固定值可进入递归只读PreparedEffect。
纯度分析仍核对真实SDK程序集，不放行旧生命周期写者、GetHashCode或外部IO。该修复解决真实组
作者读取RootNodeId时误报MKV2006；没有新增HTTP字段或更改owner、CAS和采用编译门禁。

<a id="sdk2-m10-media-features"></a>
### 28.6 宿主媒体特征轨（0.112.37／合同1.47.0）

**Windows x64（0.116.1）。** 随部署提供受控 `mkg-presentation.exe` 时，沿用相同背景媒体特征协议和失败兜底；未安装探针时不启用该服务。探针与加载库清点均在进程及整组各 `128 MiB` 的 Job Object 中执行。仅收到对应 Job 的内核内存超限事件才认定原生容量超限；普通损坏或无超限通知的失败不得通过，也不当作内存超限。无新增 HTTP 路由、字段或浏览器预览节奏变化。

**包能力。** v2包在 `manifest.json.requires` 声明 `media.features` 后，可在清单根写 `mediaQueries`
（每项 `id`、`space` 为 `sourceNormalized|canvasPixels`、`window` 为字面量或同包Duration参数，
0..2000000µs），在export中引用查询并为 `pending`、`unavailable`、`noMedia` 三种状态各写
fallback。作者在纯函数中用 `frame.Media.Sample(query, rect, timeUs)` 读取固定结果。v1包出现
`mediaQueries` 被拒绝；输出 `requires` 中的能力未在包 `requires` 声明时为 `PackageCapabilityMissing`。
能力名属于包清单，不是新增HTTP字段。

**宿主分析与固定输入。** Server启动时创建宿主背景分析服务，创建失败只在日志记
`BACKEND_MEDIA_FEATURES_UNAVAILABLE`，不阻止启动，依赖媒体的包走fallback。每个运行输入（预览、
编辑与预设候选、模板工坊与作者草稿试样、一次性渲染、正式验证、导出）在准入时固定媒体输入：
工程预览和候选只等待检查，未完成的分析按 `pending` 处理；工程与作者草稿的正式验证、导出等待
完整分析制品后再开始。模板工坊与作者草稿试样的背景是服务生成的固定图片，分析在本次运行内只做
一次；试样最多等待20秒拿到完整结果，超时按 `pending` 处理。Worker按Host私有授权重建采样绑定，HTTP不暴露
制品路径或授权。透明输出（任何来源的straight透明预览与候选、正式验证的透明半程、透明导出）固定为显式 NoMedia，
不采样不会被渲染的工程背景；与普通输出共用同一工程修订，互不借用对方的媒体输入。`PUT .../input`
同时切换 alphaMode 时按切换后的模式固定（0.112.87 起；此前沿用旧会话模式，opaque→straight 后依赖媒体的包预览失败）。

**事件。** 分析完成只改变Host派生的输入，不改变工程修订、dirty和撤销历史。此时Server对每个
打开的工程会话发 `project.mediaFeaturesChanged`，data只带公共身份和summary，没有changeSet。
客户端收到后可用同一精确修订重新固定预览输入（`PUT /api/v1/preview-sessions/{id}/input`，或GPU流 `PUT .../source`）；已接受的验证
和导出任务不受影响。该事件与其他工程事件共用重放窗口与resync规则，GET状态始终权威。

**失败码。** 以下错误出现在预览失败、作者验证报告与任务诊断中，均不回落为无媒体渲染：
`MediaFeatureOutputMismatch`（输出模式与固定媒体输入不符）、`MediaFeatureBindingRequired`
（需要媒体闭包时缺少同一固定输入和宿主绑定）、`MediaFeatureBindingMismatch`（绑定不属于本次
快照、owner、参数或依赖闭包）、`MediaFeatureResultMismatch`（结果未保留固定查询身份或数值分支）。
固定输入与依赖计划在宿主绑定创建时、以及每个会话借用绑定时完整校验（含身份摘要）；两者不可变，
逐帧、逐次采样只核对采样器仍暴露同一对象（0.112.48 起不再逐次重算摘要），失败码不变。

**失败重试（0.112.40／合同1.49.1）。** 分析失败（检查不出来源、解码或制品生成失败）不再在本次
运行内一直固定。失败后的下一次准入若距失败已过退避延迟（依次30秒、2分钟、10分钟），就以新的
尝试身份（新 `requestId`）重新分析，此前固定的输入保持不变；三次重试仍失败后，本次运行内固定
为该失败。重试只由准入触发，没有后台定时任务；有了新结果时照常发 `project.mediaFeaturesChanged`。

**限制。** 作者草稿与模板工坊试样不在工程会话里，分析完成时没有事件；超时退回 `pending` 的试样
要靠客户端重新创建或刷新输入才能看到完整结果。没有新增路由或DTO字段，路径、方法、schema与
例子计数沿用1.49.0。

<a id="preview-frame-inspection"></a>
## 29. 预览帧检查投影与单帧成本（0.112.38／合同1.48.0）

**开启。** `POST /api/v1/preview-sessions/{id}/frame-requests` 正文可带可选 `inspect`（boolean，可为null）。
省略、null 或 false 时与以前完全相同，不采集；true 时这一帧额外生成只读检查投影。所有静态预览会话
（工程、编辑候选、预设候选、模板试样、作者草稿）都接受该字段；GPU 共享纹理流、正式验证和导出不支持。
`inspect` 属于回执正文，同一 requestId 改变 inspect 视为重用冲突。

**生成。** Worker 在同一次准备与渲染后，从同一个准备帧生成投影，与 PNG 同输入、代次、时刻和来源修订；
不另跑一次准备，Host 也不重新整形。投影不改变像素、导出、缓存指纹或工程内容，打开与关闭检查得到的 PNG
字节相同。生成失败只会没有投影，帧照常发布。

**读取。** 成功时帧元数据多一个 `inspectionUrl`（`/api/v1/preview-frames/{frameId}/inspection`），SSE 帧事件和
GET 状态都带它。`GET` 该地址返回 `PreviewFrameInspectionResponse`：帧身份（frameId、previewId、
previewGeneration、sourceRevision、timeUs、width、height、renderBackend）、`inspection` 与 `host`。
投影与 PNG 放在同一保留条目，到期、淘汰、换代和关闭都同时失效。错误：未开启或没有生成
404 `previewInspectionUnavailable`；帧不存在或已淘汰 404 `previewFrameNotFound`；到期 410
`previewFrameExpired`；非 owner 401 `resourceUnauthorized`。

**内容。** 坐标是工程画布坐标（`inspection.canvas`），客户端按图片实际显示尺寸缩放（可以非等比）。
多边形是 x0,y0,x1,y1,… 的扁平数组；Warp 后可多于 4 点，超过 16 点时退为外接四边形，不可得时省略。

- `tokens[]`：基础字的最终墨迹、能否独立变形与原因码；`clusters[]` 为真实整形单元（`ownerTokenIds`
  多于一个即跨元素，`independent=false`）；`ruby[]` 为该字注音的墨迹。
- `lines[]`：行的最终 Advance 外框。`cursors[]`：演唱光标当前位置、进度、进度来源和该行进度路径的起止点。
- `particles[]`：每个实例本帧出生数、存活数、拖尾采样数与出生锚点。
- `effects[]`：实例的包身份、导出、SDK 主版本、绑定种类、绑定源行和 `scriptId`。
- `cost`：Worker 内各阶段耗时、脚本调用数与按实例和阶段拆开的脚本耗时（最多16项，其余计入
  `scriptOverflow`）、锚点补算与 memo 命中、读回字节、全进程分配增量、GC 次数、会话打开耗时。
  各阶段是 Host 墙钟包含时间，可能重叠，不能相加，也不是 GPU 硬件时间；热路径 Evaluate 不单独计时。
- `host`：Host 侧渲染往返（不含会话打开）、PNG 编码耗时与 PNG 字节数。

**预算。** 投影的 Web UTF-8 JSON 最多 262144 字节。默认上限：tokens 1000、每字 clusters 16、ruby 8、
lines 256、cursors 64、每实例出生点 256、effects 64、每效果源行 64。超出字节预算时按比例收紧上限重建，
仍超出时只保留成本与计数。省略数量记在 `truncated` 和各条目的 `omitted*` 字段，不会无声地只画一半。

**Worker 接缝。** Host 到 Worker 的二进制帧请求第 7 字节（原保留位）为检查位，只允许 0 或 1；
未请求检查的帧回执携带投影会被拒绝。Host 与 Worker 总是同一发布，不存在混合版本。

**计数。** 新增 1 个路径和 1 个方法，15 个 schema；合同为 114 路径／127 方法／613 schemas，
3561 个合成例（2646 反例）。


### M13 阻断补充：源材质预算失败（0.112.89／合同 1.52.0）

默认每帧活动表面由 64 调整为 256，仍同时受工程会话字节预算（默认 512 MiB）约束；不预分配，超限仍拒绝。
生产路径按成员判空、在羽化闭合产物独立拥有且不再被后续图读取时释放原产物，关闭像素检查不会增加同步读回。

导出源材质执行失败保留稳定根因码。任务失败报告中的 `BackendDiagnosticDto` 可具有
`originKind: sourceMaterialFailure` 与 `sourceMaterialFailure`：包含真实 `owner`、`stage`、`timeUs`、`code`；
表面超限时另有 `budget`，记录 current/additional/maximum 的表面数和字节数，字节及微秒为十进制字符串。
这类诊断不具有脚本 `occurrence`，Host 校验来源属于本次固定工程；报告不包含原始异常消息与堆栈。
失败依旧不发布新成片，客户端可从既有失败报告入口读取原因。

桌面已知预算失败在真实排空后停止预览并保留应用。GPU 完成未知时隔离故障预览、由 Host 终止所属 Worker，
不伪造排空或释放 ACK；该 Electron 会话保留未知引用并禁用再次 GPU 接入，用户仍可编辑保存，重启后恢复 GPU 预览。
内部故障隔离接缝沿用桌面 consumer secret、host 与精确流代次校验，不是浏览器公共 API。
