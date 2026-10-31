# B4-P1：`package-lyrics-scene-v1` 正式验证任务计划

日期：2026-10-20。施工分支：`refact/ttml-first-support`。本文件定义一个有限的 B4 切片：把已有 `package-lyrics-scene-v1` 工程快照接入正式 validation job 链。它不是完整 B3/B4 交付，也不是完整实时 HTTP 验收记录；当前有限切片状态见[实施记录§69](../history/backend-extraction-progress.md#b4-p1-package-validation-success-multiframe-0981)，历史§63～§68证据保留原归属。

## 1. 目标与边界

目标是复用已经存在的 package snapshot codec、受管理 Worker 和验证报告存储，让合法工程通过现有
`GET /api/v1/validation-capabilities`、`POST /api/v1/project-sessions/{id}/validation-jobs`、job 状态、报告和诊断读取接口完成双输出（普通/透明）固定帧验证。

本切片允许：

- 已由工程加载器接受、由 `PackageProjectSnapshotCodec` 固定的完整包 manifest/files、直接效果实例、包材质、效果预算、分析和 fingerprint；
- `package-lyrics-scene-v1` 作为 validation 的 `sourceProfile`，但只有实际 Worker 能力握手成功后才公布；
- 既有的精确 revision、保存/重开、job scheduler、取消/deadline/close、报告引用、分页诊断和 owner ACL；
- 现有歌词、ruby 独立时间片段、共享 ruby spacing，以及普通/透明双输出。

继续拒绝：

- 非空 `PresetApplications`，包括“静态预设 + 直接包”混合工程；不得剥离预设后继续；
- 图片/视频背景、外部媒体、需要搬移的字体闭包、任意本机路径或裸 snapshot/source/script 输入；
- 包的 export、audio analysis、scene measurement、公共 preview 以外的新入口；作者写入、脚本作者工作流、B5/B6、浏览器/用户验收和完整平台/性能矩阵。

静态 `static-lyrics-scene-v1`、`static-lyrics-scene-analysis-v1`、`static-lyrics-scene-presets-v1` validation 语义保持不变。package preview 的能力不能反向扩大 validation/export/analysis 的 profile allowlist。

## 2. 历史施工起点与已实施方向

本计划施工起点的代码已能按 profile 选择 static/package codec，并投影 package compile/image/material 诊断；当时能力公布只检查 Hosting executor 接口，尚未证明实际 Worker 的 package profile、EngineIdentity、SDK/shader、compiler refs 和 diagnostics version。下列握手、能力公布、报告和生命周期步骤现已按§63～§69的有限证据实施；当前未完成范围以§8及实施§69边界为准。

当时固定实施顺序为：

1. 增加 validation 专用 Worker capability handshake；握手须绑定真实进程与原 pipe，在发送 package snapshot 前完成；
2. 只有握手完整匹配时，`ValidationCapabilities.sourceProfiles` 才加入 `package-lyrics-scene-v1`；配置存在或 executor 类型存在只能产生 disabled/unverified 状态；
3. 复用现有 validation 输入/报告 wire。输入 profile 由固定工程快照推导，客户端不能提交任意 profile；报告保留 compile source、image effect、material 与 runtime 的互斥诊断 union；
4. 通过现有 scheduler/ledger/owner/retention/恢复读取路径完成任务生命周期；不要新增 package 专用公共路由或权限。

## 3. 私有任务与公共报告合同

### 3.1 输入与能力

Worker validation 输入继续使用 `task-validation-v1` / `KTV1`。profile 标志只能是 static 或 `package-lyrics-scene-v1`，并与已编码 snapshot codec 一致。包输入在 IPC 前继续扣除既有任务头，拒绝空包、坏 hash、超深度、超值数和超过 4 MiB codec 预算。

能力握手至少核对：

- `EngineIdentity` 与当前 Server/Worker 发布匹配；
- `package-lyrics-scene-v1`、PackageVersion/SDK/shader、完整 compiler refs；
- `diagnosticsVersion=1`；
- validation task protocol 与最大帧/画布/报告预算。

缺失、重复、未知或不匹配字段均在任务入队前以稳定 `taskUnavailable` / profile mismatch 类错误拒绝，不回退 Host、static codec 或静默剥字段。静态 profile 能力独立保留。

### 3.2 诊断 union

`ValidationDiagnosticDto.diagnostic` 使用现有 `BackendDiagnosticDto`。包来源的三类 payload 只允许下列组合：

| `originKind` | payload | 严格内容 |
|---|---|---|
| `compileSource` | `source` | package/revision、可选 instance/export、code/message/isError、可选 path、line/column |
| `imageEffect` | `imageEffect` | instance、artifact、graph resource、input、code |
| `material` | `material` | script/source owner、material resource、input、code |
| `runtime` | `occurrence` | 沿用现有 runtime occurrence |
| `transportFailure` | 无 | 不伪造源码、图、材质或 runtime occurrence |

每条诊断只能有与 `originKind` 对应的 payload。`compileSource` 的 `isError=true` 和 runtime Error 使对应输出失败且不发布新帧；image/material 旁路为 warning，可与成功帧共存。报告保留失败发生前已产生的完整诊断，不把失败改写为空报告。

## 4. 预算与拒绝语义

- 包 snapshot 继续使用 codec 的 4 MiB、200,000 source values、深度门禁；validation IPC 使用既有 Worker payload 上限；
- 每个输出最多 600 帧，画布最大 1920×1080，原始帧和报告上限沿现有 validation contract；
- 报告最多 256 issues、512 occurrences、256 KiB；诊断分页默认/最大 128 项并受实际配置更小值约束；
- 任一预算或完整性检查失败：任务稳定失败，不截断报告、不伪造成功、不发布新帧；
- 超时、取消、close 必须等待 Worker/子进程和观察到的后代排水，再释放 snapshot/resource/ledger 引用；`202` 只表示已接受。

## 5. 合同源与冻结

本切片不新增路径、方法、命令或 HTTP protocol。合同生成器新增审阅 overlay：

- `BackendJobInputDto(.Raw).sourceProfile` 枚举包含现有三个 static profile 和 `package-lyrics-scene-v1`；
- `ValidationTaskCapabilitiesDto.sourceProfile` 与 `sourceProfiles[]` 使用同一枚举；`sourceProfiles[]` 表示本次部署经过实际握手后可用的来源，不能把 `configured-unverified` 当作可用能力；
- 既有 `BackendDiagnosticDto` 的 package payload union 继续由 package preview schema overlay 审阅，validation 报告复用该 union；
- 生成的 OpenAPI、api-index、examples、manifest 已在源码稳定后冻结为合同 **1.30.1**，产品为 **0.98.1**。§67～§69只扩展探针和证据文档，不改变生产语义或合同版本。

合成 schema/example 只能证明 wire 形状。它们不证明 Worker 执行、真实包字节、PNG、报告 hash、排水或浏览器能力。

## 6. 分层验收

### A. 源码与模块

- Contracts/Hosting/Worker/Rendering 定向回归；正常 build 0 warning/error；
- 正反例覆盖 static 三 profile 与 package profile、profile mismatch、diagnostic union、预算边界、required/null 和失败帧禁发；
- 变异验证：移除 handshake、放行旧 Worker、丢失 source/material 字段或在 Error 后发布帧时，测试必须失败；恢复后再跑正式集。

### B. 匹配发布与真实 HTTP

使用匹配 self-contained Server + Worker，记录 source HEAD/dirty、发布文件 hash 与 EngineIdentity。至少覆盖：

- package 普通/透明验证成功；compile failure；runtime Error；image/material warning；预算拒绝；
- cancel/deadline/close、重启恢复、精确 revision、保存/重开、跨 client/ACL、旧报告/诊断 cursor；
- 缺 refs、旧/不匹配 Worker、错误 diagnostics version 在入队前拒绝；
- 静态三 profile 回归不变；所有 Worker、Library.Worker 和子进程身份排水；
- 报告原始 JSON bytes、size/hash、分页和失败 code 与合同一致。

本轮已完成有限匹配发布与真实 HTTP 收口，但只能标记为“B4-P1有限真实HTTP切片已收口”，不能标记完整 B4-P1，更不能宣称完整 B3/B4。当前主成功路径证据为 `artifacts/package-validation-http-success-multiframe-0fdf080/manifest.json`：450 requests／1533 assertions、Server exit0、12个观察 Worker、4个终态排水检查和1个 crash 排水检查、无强制清理/残留；产品仍0.98.1、合同仍1.30.1，源码/发布指纹及明确边界见[实施§69](../history/backend-extraction-progress.md#b4-p1-package-validation-success-multiframe-0981)。基础 scale 已覆盖普通／透明各4帧、完整成功报告与 diagnostics page；image/material warning保持各4+4帧，warning helper 未读取其 diagnostics page，细节按§68。一次 orderly recovery与一次 abrupt crash recovery保持§67范围，crash-success仍各1帧。§66的432／1427和其余历史证据保持原归属；完整多帧矩阵、完整warning输入闭包和其余 B3/B4 范围继续独立。

## 7. 明确不在本切片内

preset + package、图片/视频背景、外部媒体、字体闭包、作者源码执行、导出、音频分析、场景测量、完整资源包、安全沙箱、浏览器/用户闭环，以及 B5/B6 均保持独立计划和独立证据。

## 8. 2026-10-20 有限收口状态

生产语义保持0.98.1／合同1.30.1；此前 `7878b3a` 修复 compileSource 报告误拒、`b9fb9f4` 修复 capability handshake deadline 映射、`9ec7ca5` 稳定拒绝三类 diagnostic union null。当前 probe 提交 `0fdf080` 只新增主成功路径固定4+4帧与成功 helper 一致性断言，其匹配发布及450／1533 fresh HTTP见[实施§69](../history/backend-extraction-progress.md#b4-p1-package-validation-success-multiframe-0981)。warning多帧见§68，orderly／abrupt recovery见§67；历史§63～§68目录、计数与验证边界均不被覆盖。完整B3/B4仍未完成。
