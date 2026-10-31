# G19-B 片段生命周期能力缺口与有限切片

状态：审计与实施回顾（2026-10-18）。本文先记录能力边界，随后作为 G19-B 实施验收索引；当前合同和行为以 `docs/architecture/backend-api.md` §13.10 与合同 1.19.0 为准。

## 1. 审计结论

当前 0.91.0/G19 已交付的是“范围布局产生片段”和“片段只读投影”，还不是片段的独立 Web 生命周期。

| 层 | 已有能力 | 缺口 |
|---|---|---|
| Core 数据 | `LyricDisplayClip` 保存稳定 `id`、可选名称、连续 `lineIds`、`containerId` 和可选 `scheduleContext`。 | 没有面向 HTTP 的命令 DTO、错误码或结果回执。 |
| Core 编辑 | `SceneTreeEditor.TrySetClip` 可新建/替换片段；`TryDeleteClip` 删除片段后成员回默认容器；统一工程校验在提交前运行。 | `TrySetClip` 是通用 upsert，未拆出新建、改名、换容器的命令语义；调用者仍须自行构造完整有效片段。 |
| Core 调度 | `LyricDisplaySegmentResolver` 校验成员存在、连续、有序、有时间、互不重叠，并按当前歌词时间重建区间；默认段边界通过 `scheduleContext` 保存相邻成员身份。 | 不提供 HTTP 可直接消费的冲突分类或字段定位。 |
| Hosting dispatch | 已有 `orchestration.setDefaultContainer`、`orchestration.previewBasicLayout`、`orchestration.applyBasicLayout`，均走精确修订、单写者和历史门禁。 | 没有片段新建、改名、换目标容器、删除的 dispatch。 |
| Web 合同 | G19 允许 `selectedLineIds` 连续范围；应用回执返回 `basicLayoutResult.clipIds`；`GET .../orchestration` 投影 clips 和 `scheduleContext`。 | 没有任何片段生命周期 route、命令 schema、capability、正反例或真实 HTTP 验收。 |

依据：`docs/architecture/backend-api.md` §13.9、`docs/backend-extraction-progress.md` §51、`src/MakoKaraGenerator.Core/Projects/SceneTreeEditor.Clips.cs`、`LyricDisplaySegment.cs` 及 `src/MakoKaraGenerator.Backend.Hosting/ProjectSession.OrchestrationReadModels.cs`。

## 2. G19-B 建议的有限边界

G19-B 只补“现有歌词分支中显式片段”的生命周期。它复用当前 `LyricDisplayClip` 和展示树校验，不扩展为完整 SceneTreeEditor Web 化。

纳入：

* 在一个现有 `branchId` 下创建片段，指定稳定 `clipId`、非空连续 `lineIds` 和现有歌词容器 `containerId`。
* 修改片段名称（名称可为空以清除名称）。
* 将片段改到同一工程中已有的歌词容器。
* 删除片段；删除后成员回到该程序的 `defaultContainerId`，不删除歌词行或容器。
* 每个写命令使用 `ProjectCommandRequest.expectedRevision`，由现有单写者串行提交，并返回现有命令回执、历史状态和事件游标。

不纳入：创建/删除歌词行、节点树增删或拖放、片段拆分/合并、前奏（`leadIns`）、调度时间副本、包/脚本/材质/媒体/字体闭包、任意 JSON patch、跨分支片段、公共 package 执行以及浏览器 W4 验收。

## 3. 建议命令语义

建议命令名使用现有 `orchestration.*` 命名空间，并保持一命令一种严格 DTO。以下字段均为建议，落地时须同步 `ProjectCommandJsonConverter`、OpenAPI、manifest、README 和真实 HTTP 探针。

### 3.1 `orchestration.createClip`

输入：`branchId`、`clipId`、`lineIds`、`containerId`、可选 `name`。`clipId` 由客户端提供且必须是稳定、非空、未占用的 ID，便于重试和回执关联；`lineIds` 必须非空、唯一、按该分支源顺序连续，所有行必须存在且有有效行时间；`containerId` 必须是当前工程已有且带歌词布局的容器。服务端构造 `LyricDisplayClip`，不接受客户端写入 `scheduleContext`，由领域逻辑按当前结构生成或保留必要边界。

成功：一次历史、清理 redo、`Changed=true`，changeSet 至少为 `areas=["orchestration","renderInputs"]`，回执返回 `branchId`、`clipId`、`lineIds` 和 `containerId`（或要求客户端按 revision 重新读取 orchestration）。

### 3.2 `orchestration.renameClip`

输入：`branchId`、`clipId`、`name`（允许显式 null 清除名称）。只改变名称和对应的片段身份，不重算成员或调度边界。名称与当前值相同是 no-op：不新增历史、不清理 redo，回执 `Changed=false`。

### 3.3 `orchestration.setClipContainer`

输入：`branchId`、`clipId`、`containerId`。目标必须是当前工程已有歌词容器；不能为 null，也不能隐式创建节点或改变程序默认容器。相同目标是 no-op。成功一次历史，`changeSet.areas` 为 `orchestration` 与 `renderInputs`。

### 3.4 `orchestration.deleteClip`

输入：`branchId`、`clipId`。目标不存在返回稳定 `ClipNotFound` 类错误，不把重复删除视为成功。成功调用现有删除语义：仅移除显式片段，成员回默认容器，歌词和容器身份保留；一次历史并清理 redo。回执返回被删除 `clipId`，并建议包含 `returnedToDefaultContainer=true`。

## 4. 错误、no-op 与历史约束

* 所有命令先检查 JSON 严格字段、稳定 ID 和 `expectedRevision`；修订冲突沿用 `RevisionConflict`，不执行部分写入。
* `branchId` 不存在使用 `SourceNotFound`；片段不存在使用 `ClipNotFound`；容器不存在或不是歌词容器使用 `ContainerNotFound`/`InvalidContainer`；行不存在、重复、乱序、断续或无有效时间使用 `InvalidClipRange`；片段与其他片段或源行时间冲突使用 `ClipScheduleConflict`。具体 wire 名称应在合同冻结时一次确定。
* 任何失败都保持工程 revision、dirty、undo/redo 不变，且不发布 `project.changed`。
* 只有实际领域变化才创建一条历史。四个命令均禁止合并到相邻历史；成功后 redo 清空。no-op 保留 redo，返回 `Changed=false` 和空变化区域，遵循当前默认容器命令约定。
* `changeSet` 超过现有 16 KiB 预算时继续使用完整刷新保护（areas 保留、各 ID 数组清空、`requiresFullRefresh=true`），不能把空 ID 数组解释为“没有变化”。片段范围通常可只返回 `branchIds=[branchId]`，不把行 ID 当作歌词内容变更。

## 5. 读模型要求

继续使用 `GET /api/v1/project-sessions/{id}/orchestration?expectedRevision=...`，不新建片段专用读路由。每个 clip 至少返回当前已有字段：`id`、`name`、连续 `lineIds`、`containerId`、可选 `scheduleContext`（成员、`isDefaultSegment`、`previousLineId`、`nextLineId`）。

写回执中的 revision 是提交后的精确 revision；客户端遇到成功响应应以该 revision 读取 orchestration，遇到 `requiresFullRefresh` 必须完整刷新。读模型继续受现有身份、1 MiB 和 20,000 项预算约束；不公开 `leadIns`、效果包、脚本、媒体或字体闭包。

## 6. 验收清单

1. Core：四种命令分别覆盖成功、无效 branch/clip/container、重复/乱序/断续/无时间行、片段重叠和删除后默认归属；验证稳定 ID、调度上下文和保存重开。
2. Hosting：严格 DTO 分型、精确 revision、单写者串行、一次历史、no-op 保 redo、失败不改 revision/dirty/redo，回执和 changeSet 正确。
3. Server/HTTP：能力清单、OpenAPI、正反例、错误 failureCode、ETag/事件游标（如适用）与 16 KiB 完整刷新路径一致；至少覆盖重试、并发修订冲突和认证边界。
4. Read model：创建/改名/换容器/删除后，`orchestration` 的 clips 和 `scheduleContext` 与领域解析一致；删除后成员显示默认容器。
5. 明确不宣称：上述通过只能证明片段生命周期有限后端出口；不代表完整 W4 编排、最终外观、作者包、脚本隔离、媒体/字体闭包或浏览器产品验收完成。

## 7. G19-B 实施核对结果

G19-B 已按本稿四命令边界交付：`orchestration.createClip`、`orchestration.renameClip`、`orchestration.setClipContainer`、`orchestration.deleteClip`。实现提交为 `3ab9f92`，合同升级为 1.19.0；Hosting 922/922、Server 261/261、真实 HTTP 片段链 2/2 通过，合同冻结校验为 66 paths／73 methods／328 schemas／1063 examples（721 negative）。

因此本稿第 1 节中的“缺口”是实施前快照；当前仍未交付的范围仅是第 2 节“不纳入”项目及 G20 结构节点编辑、G21 位置／内部布局／动画、G22 几何参考渲染、G23 前奏提示和 G24 本句微调。

