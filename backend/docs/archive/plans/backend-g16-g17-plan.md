# G16／G17 有限后端接续方案：候选分配试看与模板固定试样

日期：2026-10-17。本文记录 G16/G17 的合同、复用边界和证据，并作为后续 W4 接续入口。G16 候选分配试看与 G17 模板固定试样已交付；本文不打开完整场景、作者包或脚本能力。

## 1. 当前基线与问题定义

当前产品为 0.89.0、HTTP 合同 1.16.0（66 paths／73 methods）。G16 候选试看与 G17 模板固定试样均已交付；R3 已修复 nullable source 的合同 schema，并公布 `project-sessions.preset-applications.read` capability。模板库已经提供 builtin／personal 的精确 revision 读取和个人库五种命令；工程已经提供四种 `presetApplications` 命令、G15 精确修订读模型和 W4 有限 orchestration 读写。静态预设工程可以通过公共 `preview-sessions` 生成实际 Worker PNG；预览输入固定工程修订，编辑后必须显式换 input。上述能力不包含下面两个前端需要的瞬时视图：

* **G16：候选分配试看。** 用户在当前工程修订下选择一个模板、目标和 branchInputs，先看应用后的画面，再决定是否执行 `presetApplications.apply/update`。试看不得写工程历史、dirty、redo、应用身份或模板库。
* **G17：模板固定试样。** 模板库卡片和未保存模板草稿需要稳定、可缓存的 PNG 试样。试样不应要求创建工程，不应污染工程历史或个人库，也不能把缩略图称作目标工程的最终外观。

## 2. 先决依赖和明确不做的事

### 2.1 依赖

1. G16 依赖 G15 的严格目标 union、模板快照摘要和 G16 请求中的 `expectedProjectRevision`；库快照必须同时锁定 `libraryId/libraryRevision/templateId/templateRevision`。应用命令的 Writer/CAS 逻辑仍是最终提交的唯一来源。
2. G17 依赖模板库的 canonical static definition、库 revision 和现有静态样式 resolver。保存中的 personal 模板必须先形成完整 canonical definition；草稿试样使用请求内完整定义，不把尚未保存的名称或临时 ID写入库。
3. 两者都依赖现有 PNG 编码、帧大小（最大 1280×720、裸帧 4 MiB）、认证 fetch、ETag/TTL 和排水规则。应先让 Worker 暴露与现有 `static-lyrics-scene-v1`／`static-lyrics-scene-presets-v1` 相同的静态 capability，再开放 HTTP。

### 2.2 不在本批范围

不开放脚本、配方、包材质、图片／视频背景、任意字体闭包、完整 node／clip／前奏编排、作者包发布、公共 package 执行、连续 30/60fps、工程保存或隐式应用。G16 不能用“预览成功”代替应用命令成功；G17 不能用静态模板试样代替当前工程像素或浏览器验收。

## 3. G16 合同建议：候选快照预览

### 3.1 路由和输入

建议新增独立入口，避免把已有 `PreviewSessionRequest` 的 project-source 语义改成可写工程：

`POST /api/v1/project-sessions/{projectSessionId}/preset-application-previews`

请求至少包含：

```json
{
  "expectedProjectRevision": "123",
  "templateLibraryId": "personal",
  "templateLibraryRevision": "sha256...",
  "templateId": "line-classic",
  "templateRevision": "sha256...",
  "targets": [{"kind":"line", "branchId":"...", "lineId":"..."}],
  "branchInputs": [],
  "width": 1280,
  "height": 720,
  "alphaMode": "opaque"
}
```

字段必须严格分型、拒绝未知字段和重复目标；模板和库 revision 必须精确匹配；工程 revision 过期返回 409，不能按新修订静默重算。目标有效性、静态 definition、branchInputs、预算和 preset profile capability 在产生会话前全部验证。高级模板或任何不支持的闭包返回稳定 `commandNotSupported`／`previewUnavailable`，不创建半成品会话。

响应可复用现有 `previewId`、`generation`、`sourceRevision`、限制和状态字段，并明确 `previewKind:"presetCandidate"`、候选摘要及 `committable:false`。后续帧请求、状态、SSE、PNG、diagnostics、close 沿现有 preview-session 规则；每次候选变化以新的固定输入／generation 取代旧结果，迟到帧不能覆盖。

### 3.2 写入和并发语义

候选解析在工程 Writer 外完成，随后在创建会话时短暂取得工程读快照并再次核对 revision；整个过程不调用 `ProjectHistory`、不改变 dirty／redo／应用身份／工程事件，也不触发库写入。库或工程在会话创建后变化不会追改候选；前端必须重新提交新 revision。关闭或 TTL 到期释放候选快照和 Worker，不能保留可提交句柄。

真正应用仍由现有四种命令完成。G16 不新增“应用试看结果”或隐式提交按钮，避免把预览状态当作历史命令。

## 4. G17 合同建议：模板与草稿固定试样

建议与工程预览使用同一异步帧生命周期，但用独立资源前缀和来源 union，避免把模板样本伪装成 project session：

`POST /api/v1/template-preview-sessions`

来源二选一：

* `savedTemplate`：`libraryId`、`libraryRevision`、`templateId`、`templateRevision`，服务端捕获完整静态 definition；
* `draft`：请求携带完整 canonical static definition、`draftRevision`（由客户端生成的 opaque id），服务端只在会话内保存，不写 personal library。

请求还应固定 `sampleId`（首批只提供服务端版本化的一个或少量歌词／时间样本）、尺寸、alphaMode。服务端返回 `templatePreviewId`、`generation`、`sampleId`、`definitionRevision` 和标准限制；帧请求与 PNG 读取建议复用现有 requestSeq、SSE、ETag、TTL、关闭和鉴权语义，但路由明确为 template preview，不能接受 projectSessionId 或目标 ID。

固定样本必须是合同资产，版本化且字节稳定；不读取用户音频、不受播放头影响，不产生工程修订。草稿 definition 通过与模板库相同的 canonical validator、属性预算、字体／效果拒绝和 PNG 预算；非法草稿返回可定位的 422，不显示旧 PNG 冒充当前草稿。相同 `definitionRevision + sampleId + size + alphaMode` 可缓存，但缓存命中仍须校验 client 访问权。

## 5. 复用与隔离决策

| 能力 | G16 | G17 | 决策 |
|---|---|---|---|
| PNG 编码、帧句柄、ETag、TTL、HEAD/GET、SSE 提示、close 排水 | 复用 | 复用 | 抽出与来源无关的 bounded preview runner；保持现有错误和租约语义 |
| Worker `static-session-capabilities-v1` 与静态 resolver | 复用 | 复用 | 仅允许 static definition；不扩大到 package/script |
| `PreviewSessionRegistry` 的 project ownership／工程 revision | 复用其生命周期，不复用输入模型 | 不复用 | G17 不应伪造工程会话；两者各自固定 source identity |
| Project Writer、ProjectHistory、preset application commit | 只读校验 | 不使用 | G16 试看绝不写历史；G17 完全脱离工程 |
| TemplateLibraryRepository／CAS | 只读固定快照 | savedTemplate 只读；draft 不写 | 库更新不追改已有试样 |

若当前 registry 无法安全承载两类 source identity，应新增中立的 `PreviewInputSnapshot`／`TemplatePreviewRegistry`，而不是给现有 project DTO 塞可选字段；共用 Worker/PNG 管道但保持计数、授权和关闭隔离。

## 6. 有限验收矩阵

### G16

* 同一工程 revision、builtin/personal 静态模板、line/token/branch 合法目标得到实际 PNG；与随后真实 `apply` 后的静态预览在相同尺寸／时刻逐像素一致。
* 工程 revision、库 revision 或模板 revision 任一变化后旧候选返回 409／过期，不产生工程历史；读取 history、dirty、redo、G15 列表前后一致。
* 无效目标、重复目标、branchInputs 不匹配、超预算、高级定义、旧 Worker capability 均稳定拒绝且无残留会话。
* 跨 client 认证、Origin/Host、SSE 丢失后的 GET 状态、PNG 过期、close 排水、重启／观察身份退出和回执重试通过；至少覆盖分配前后像素差异和移除后的恢复。

### G17

* builtin 与 personal 已保存静态模板的固定 sample PNG 可重复；同一 definition/sample/尺寸的 bytes、SHA 和 ETag 稳定。
* 合法 draft 改色／改字体等 canonical 属性后 PNG 改变；放弃或关闭不改变库 revision、工程 revision、dirty、redo。非法 draft 保留错误并不发布旧帧为当前帧。
* personal 库替换／删除、客户端切换、重启和缓存 TTL 不让旧会话读取新定义或越权读取；损坏 definition、未支持字体／效果、4 MiB／深度／值预算均在渲染前拒绝。
* 真实 Worker 静态 profile、PNG 下载、HEAD/ETag/304、SSE/close 和全部观察进程排水通过；浏览器卡片布局和缩略图加载另由 Claude 验收，不计入后端证据。

## 7. 交付顺序和证据边界

G16 候选工程快照与 G17 saved/draft 静态样本已按上述边界接入；后续每一步仍须一个提交，合同、OpenAPI、manifest、正反例和行为文档同提交，并记录 solution 零警告／错误、定向回归、真实请求／断言、PNG SHA、租约排水和失败变异。不得把静态编译、模板 detail 或已有预览证据重算为完整 W4、完整 B3/B4 或浏览器闭环。


## 8. G17 后续优先级

用户已决定下一切片优先交付 **G18 快速布局**。在实现 G18 前，先完成 **R4 新增来源编排程序缺口**的前置兼容修复，确保来源身份、分支输入和现有编排读模型能够被新布局流程稳定识别。

G18 首批范围为整来源模式的预演、应用与识别：先冻结合同、读写与历史语义，再用真实预览验证布局结果和应用后的识别结果。G19 已放开 selectedLineIds 的连续范围能力，要求非空、唯一、按来源顺序且真实存在；不把快速布局扩展为完整 node／clips 编排或完整 W4。

后续顺序为：

1. **G19** 片段范围与编排投影（本批已交付）；
2. **G20** 及后续 G21、G22、G23、G24 编排切片；
3. 公共 package、受授权媒体与字体闭包；
4. B5 作者草稿／库／编译／采用／发布；
5. B6 协议与部署收口。

上述顺序不代表完整 W4、完整 B3/B4、作者闭环或浏览器验收已经完成；每个切片仍需独立合同、真实 HTTP、实际预览和边界证据。
