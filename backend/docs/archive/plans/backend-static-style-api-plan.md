# Web 静态样式编辑 API 接续方案

日期：2026-10-16。用户直接报告已完成前端现有编辑、撤销／重做测试，后端已提供接口无误，并授权继续补齐编辑 API。该反馈按用户所述范围记录；未提供前端提交或逐项清单，不据此宣布全部高级编辑、完整 B3/B4 或平台验收。

2026-10-16补充裁决：Claude同步用户要求“歌词与时间／编排／目标无关样式库”职责分离、模板通过显式分配连接对象。主代理说明本候选会直接修改目标样式后，用户明确选择“作为底层能力一并交付，再补模板库与分配”。因此本批底层接口继续交付，随后优先模板库／PresetApplication／ProjectEffectBinding API；不要求前端暴露按目标修改颜色字体的入口。staticHierarchy仍不等于预设／场景上下文下的最终外观。

本批接续[原编辑方案](backend-web-editing-plan.md)，静态样式有限读写出口已实现为0.83.0／合同1.8.0，证据见[实施§40](../history/backend-extraction-progress.md#web-static-style-0830)。下列候选保留设计来源，精确wire及错误／预算以[行为§6.11](../../architecture/backend-api.md#611-四层静态样式读写0830)和同提交冻结合同为准。主要编码交子代理，主代理负责合同、审查、实际 HTTP、文档、提交和推送。

## 1. 目标与顺序

先补齐工程默认、来源、歌词行及元素四层静态外观：字体、两种填充、渐变、描边、阴影、默认纵向位置、对齐、透明度和缩放。随后独立安排进入动画与基本布局，再接场景草稿及完整展示编辑。公共 package、预设应用／库、媒体／字体资源、作者和完整 B3/B4 不作为本批前置，也不随本批自动完成。

规则真值为 Core 的 canonical LyricStyle／LyricStyleOverride／MaterialSpec／EffectSpecOverride；不用平面 color／gradient 兼容外观重建样式。未选择的覆盖、材质参数、预设应用、效果、场景、正文、注音与时间完整保留。不会因修改字体而把包材质读成纯色或静默替换。

## 2. 候选读取合同

新增认证 `GET /api/v1/project-sessions/{id}/style`。expectedRevision、kind 必填且唯一；kind 为 project／branch／line／token，分别只允许无 ID／branchId／branchId+lineId／branchId+lineId+tokenId。拒绝未知、重复或不符合 kind 的 query；稳定 ID 按原非空白字符串精确匹配，不 trim、不改为数组下标。

读取在唯一 Writer 内核对修订及权限，不改变历史、dirty、redo 或事件。响应包含 session／revision／target、工程基线、本层原始覆盖及四层静态继承结果。原始覆盖用闭合 canonical 属性和值列表表示，省略的属性表示本层继承，显式 null 不被全局空值省略策略吞掉。包材质提供明确只读身份描述，不伪造颜色或开放参数写入。提供明确的 staticHierarchy 解析标记及工程存在固定预设应用的提示；该结果只解释 baseline→branch→line→token，不称为实际帧最终外观。真实渲染还会叠加固定预设、node／clip 等上下文，见 PresetStyleResolver。

完整读取预算为 1MiB UTF-8 JSON／20000 项，超限 413、不截断。新写入的字符串或色标限额不能用来拒绝旧数据读取。

## 3. 候选写入合同

复用现有 `POST .../commands`、顶层 expectedRevision 及回执三字段，新增两种严格命令：

```json
{"type":"styles.patch","targets":[{"kind":"line","branchId":"primary","lineId":"line-1"}],"changes":[{"property":"typography.fontSizePx","value":80},{"property":"stroke","value":null}]}
```

```json
{"type":"styles.restore","targets":[{"kind":"line","branchId":"primary","lineId":"line-1"}],"properties":["typography.fontSizePx","stroke"]}
```

targets 为严格 project／branch／line／token union，同一命令仅一个 kind，1..1000 个无重复稳定目标；project 仅一个。全部目标使用同一 patch，禁止空 changes／properties、重复属性和父子属性冲突。kind／property／枚举区分大小写，数值仅 JSON number，禁止字符串数值、未知字段及重复字段。value 必须显式提供，nullable 是具体属性语义，不能用 value 缺失表示恢复继承。

闭合属性为 typography 的 fontFamilyName／fallbackFamilyNames／fontSizePx／fontWeight／isBold／isItalic／letterSpacingPx，两填充的 material，stroke 整体及 material／widthPx／join／position，shadow 整体及 color／opacity／angleDegrees／distancePx／blurRadiusPx，以及 verticalCenterRatio／horizontalAlignment／opacity／scale。整体 stroke／shadow 接完整对象或 null，叶属性只改对应成员。restore 直接删除本层选定覆盖，允许整个 stroke／shadow，禁止 project 恢复上层继承。

字体名 null 是显式选择后端默认字体；stroke／shadow null 是明确关闭，均不同于恢复继承。fill／material null 非法。材质写入仅严格 solid（完整 RGBA）或 gradient（完整规范渐变）；明确选中材质槽才整体替换，未选择的包材质保留。渐变保所有几何、坐标／色彩空间、色相路径和色标；允许相等 offset，必须非降序、首 0 尾 1，不排序、不归一化修补。字重按原 100..900，不额外要求整百。token 只开放原允许的填充、描边、阴影及 opacity，不开放字体、位置、对齐或 scale。

一次命令纯 Prepare→完整预算→一次 CommitPrepared，绑定 workspace／generation／源工程；任何非法目标、值或预算超限整批不变。实际变化一次历史及修订，等值输入／已恢复字段 no-op 保 redo 和预设标识；工程基线真正变化才沿原语义清除 LyricStylePresetId。旧 33 命令的 wire、历史、回执及错误保持。

新样式意图预算为 64KiB JSON；新增字体名每个最多 256 UTF-16 code units，回退链最多 32 项，渐变最多 256 色标，新增值在本批目标展开的逻辑预算 16MiB。只限制本次新值，不把它误称总 heap 或耗时保证。改变目标的完整新读投影在提交前验证；旧大值的无变化／恢复路径不得无理由分配完整副本。回执遵守实际配置的 64KiB 上限，changeSet 超过 16KiB 采用已有完整刷新兜底，不截断身份数组；反馈仅有界 changedTargetCount。

## 4. 验收与交付

共享用例核 canonical 局部更新、四层继承与非主来源、三态、元素限制、数组／渐变值相等、候选失效和整批原子性。Hosting／Server 核严格输入、单写者、修订、回执重试／并发、no-op 保 redo、只读／跨 client、读及提交前预算。

正常 solution 零警告构建后执行相关定向回归；使用自有素材、工程和 profile 发布匹配 self-contained Server＋完整 Worker，真正 HTTP 证明读写→撤销／重做→保存关闭重开、失败零变化及完整终态。独立核验未选字段、部署／源码／输入 SHA、脱敏留档及自有进程排水。各批计数分列，不恢复全量、性能、长片或人工平台矩阵。

实际实现后另提升产品／合同版本，同步唯一行为源、五份冻结合同、工具／正反例和 roadmap；候选不预写为已完成。交付 Claude 提交、版本、匹配部署、启动器兼容情况及真实证据；前端此新切片仍需独立接入验收。
