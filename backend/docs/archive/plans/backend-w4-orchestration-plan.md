# W4 编排第一切片计划

日期：2026-10-17  
状态：**第一切片已交付（0.87.0，合同1.13.0）；后续编排能力仍在计划中**

本计划承接 Web 设计稿 [§5.3「编排」](../../../../MakoKaraGenerator-Web/docs/web-ui-design.md#53-编排) 与 [§11 用户决定](../../../../MakoKaraGenerator-Web/docs/web-ui-design.md#11-用户决定-2026-10-16)。当前合同已升为 1.13.0；第一切片已实现并记入 [实施§46](../history/backend-extraction-progress.md#w4-orchestration-0870) 与统一 API §13.5。本文保留后续 W4 边界和验收约束，不把未实现的结构编辑能力写成已交付。

## 1. 第一切片的有限目标

先交付一条能被前端消费、能被撤销的最小闭环：

1. 一个精确修订的编排读模型，读取现有工程的**结构树**（组、歌词容器、槽位）和每个歌词来源的默认容器。
2. 一个工程命令 `orchestration.setDefaultContainer`，把一个来源的默认容器设置为现有的非空容器。

读模型与命令使用同一份稳定 ID 和同一工程修订。一个成功命令可立即用同一 `expectedRevision` 加一读取验证，然后用 `history.undo`／`history.redo` 验证历史闭环。第一切片使用带预置结构的 fixture 验收；**不**以“自动创建一套完整场景”作为隐含前置。

选择这个边界的理由是：它能让编排页先显示真实结构并完成“来源 → 默认容器”的首个直接生效动作，同时不把设计稿中尚未冻结的片段、前奏、几何或渲染语义偷偷变成公共合同。

## 2. 拟定 HTTP 入口与 DTO

### 2.1 读模型

已新增并已同步 OpenAPI、`web/api-index.md`、`web/api-contract.md`、manifest 和行为合同：

`GET /api/v1/project-sessions/{id}/orchestration?expectedRevision=<int64>`

认证、client 归属、session 关闭和跨 client 拒绝沿现有工程读接口。`expectedRevision` 必填且是非负十进制 Int64；读发生在 `ProjectSession.Writer` 内，响应中的 `projectRevision` 是实际投影修订。

实际响应为 `ProjectOrchestrationResponse`：

```text
projectSessionId: string
projectRevision: string
nodes: ProjectOrchestrationNodeDto[]
programs: ProjectOrchestrationProgramDto[]
```

`ProjectOrchestrationNodeDto` 返回稳定 `id`、可空 `name`／`parentId`、`placement`、`referenceRect`、可空 `layout` 与可空 `contentKind`；layout 返回 `kind`、可空 `defaultPlacement`、`safeArea`、`maxWidthRatio` 与 `slotIds`。`ProjectOrchestrationProgramDto` 返回 `id`、`branchId`、`rootContainerId`、`defaultContainerId` 与可空 `animationPolicy`。

本响应**不**返回最终样式、模板、字体、颜色、动画、画布矩形、时间采样或渲染 PNG。也不返回片段、前奏标记、行微调或完整场景 JSON；这些字段必须等后续切片冻结后再加入，不能用 `null` 伪装成已支持。

### 2.2 命令

继续使用现有 `POST /api/v1/project-sessions/{id}/commands`，沿用顶层 `expectedRevision`、`requestId`、`hostInstanceId`、`validUntilUtc` 和 `command` 分型。新增分型：

```json
{
  "type": "orchestration.setDefaultContainer",
  "branchId": "branch-main",
  "containerId": "container-main"
}
```

`containerId` 本批必须是非空字符串，并且必须指向当前工程中已存在的歌词容器。命令不接受 `null`、缺省值或“清除直接默认值”的隐式语义；当前 Core 的 `LyricDisplayProgram.DefaultContainerId` 是 required string，真正 nullable 清除需要另行 schema/渲染迁移。若产品需要重置，后续可显式提交规范默认容器 ID；该重置语义不在本批另造。命令不接受节点创建、父级、排序、槽位、片段或前奏字段。成功回执沿 `ProjectCommandResponse`，`changeSet.areas` 为 `orchestration` 和 `renderInputs`，并返回 `orchestrationResult:{branchId,containerId,changed:boolean}`；不返回大规模节点差集。

## 3. 修订、历史与 no-op

- 读模型必须精确匹配 `expectedRevision`。过期即 409 `revisionConflict`，即使 undo 后内容相同也不能复用旧修订。
- 成功改变默认容器：工程修订单调增加一次、产生一条历史、标记 dirty，并清理 redo；一次 HTTP 请求只能有一次历史。
- 设置当前已是相同容器：是 no-op，不增加修订、历史、dirty 或事件，并保留 redo；回执仍按现有成功命令格式返回 `changed:false`。本批没有 null 清除或缺省重置操作；显式提交规范默认容器 ID 若与当前值相同，同样按 no-op 处理。
- 成功的 `requestId` 重试返回原回执且不重复历史；相同 ID 搭配不同规范化载荷返回 `requestIdReuse`。客户端、工程代次和单写者门禁沿现有命令实现。
- `history.undo` 恢复上一个默认容器状态，`history.redo` 再次应用；保存、关闭、重开后读模型必须保留该状态。

## 4. 输入、输出和错误预算

- 命令 canonical JSON（不含顶层 metadata）最多 64 KiB；`branchId`、`containerId` 非空、最多 128 个字符，名称最多 256 个 UTF-16 code units。不得截断或自动规范化 ID。
- 编排读模型最多 2,000 个节点、1,000 个来源默认项，完整 UTF-8 JSON 最多 1 MiB、最多 20,000 个 JSON 值；超限整体 413 `resourceTooLarge`，不截断、不返回半棵树。回执继续受现有 64 KiB 上限约束。
- 400 `invalidRequest`：缺失/重复/未知 query、类型错误、Int64 形状错误、`containerId` 缺失、空白、null 或未知 command 字段。
- 404 `sessionNotFound`：不存在、关闭或跨 client 的工程会话，沿现有中立语义。
- 422 `commandRejected`：`BranchNotFound`、`OrchestrationContainerNotFound`、`OrchestrationTargetNotContainer`、`OrchestrationNodeNotInProject` 等稳定 `failureCode`；失败不得修改工程、历史、redo、dirty 或修订。
- 不从路径、文件、模板库或客户端提交的任意场景 JSON 读取结构；所有结构身份必须来自当前工程领域模型。

## 5. 验收与测试计划

实现本切片时，测试必须分开记录，不能用编译通过代替 HTTP 验收：

1. **Contracts/静态**：DTO 序列化、严格 `kind` union、非空 `containerId`、null/缺字段拒绝、未知字段、预算计数、OpenAPI/index/manifest 一致性。
2. **Hosting/Application**：读投影修订门禁；显式容器和 no-op；null/空白拒绝；目标不存在、来源不存在、容器非容器节点、跨工程节点身份；一次历史、dirty/redo、undo/redo；保存重开。
3. **真实 HTTP**：匹配 self-contained Server/Worker（本切片不应依赖 Worker）执行正向读写、过期修订 409、重复 requestId 与载荷复用拒绝、跨 client/session 404、413 预算、422 领域拒绝、关闭排水。
4. **不纳入本批的检查**：浏览器 W4 页面验收、真实成片像素、布局参考 PNG、拖拽交互和平台矩阵。它们只能在后端合同和实际 HTTP 证据完成后单独记账。

## 6. 明确不做范围

本切片不实现或承诺以下能力：

- 节点/组/容器/槽位的创建、删除、复制、改父级、排序或批量重排；
- 片段（clips）、前奏提示（leadIns）、自动生成、快速布局和歌词分配到片段；
- 容器几何、位置/旋转/缩放、画布矩形、选中手柄和本句微调；
- 自由布局与槽位轮换的调度参数、动画、冲突策略和布局参考渲染；
- 时间预览、成片预览、Worker 渲染、最终外观解析、模板/效果/字体/背景媒体；
- 外观分配扩展到容器、片段或前奏标记；现有四类 `presetApplications` 目标合同保持不变；
- 外观方案、作者包、公共 package 执行、完整场景 schema 迁移、性能/长片/GUI/平台矩阵。
- `DefaultContainerId` 的 nullable schema、渲染迁移或真正“清除直接默认值”命令；若后续需要，须另立迁移计划和合同。

后续切片应在本读模型和命令的真实证据基础上分别冻结：结构编辑、片段/前奏、几何与布局参考。不得因为本计划存在，就把 W4 的完整页面或完整编排能力标记为已交付。
