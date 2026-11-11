# 阶段二路线与交接（M12 收口 → M14，2026-11-10，11-12 更新）

分支 `dev/script-sdk-phase-two-structure`（2026-11-11 自 main 开出；此前的 `dev/script-sdk-render-upgrade` 已快进合并到 main），产品 0.112.72，HTTP 合同 1.49.2，已推送到私有远程。**P1（M12 收口）与时钟校准修复（§4.5）于 2026-11-11 完成，M12 关闭；P2 结构整理（A 与 C 第 1～3 刀）于 2026-11-12 完成（§5.4）。下一步是 P3 样式完善，从 §6.6 第 1 步设计定稿开始，SDK 写入签名须先与用户确认。**

**本文是阶段二唯一的计划入口。** 新对话从这里开始；其他文档只作背景、证据或原始约束：

| 文档 | 作用 |
|---|---|
| [主计划](script-sdk-v2-render-pipeline-plan.md) | 原始设计与 D1～D17（§1.1）、阶段划分（§7.10）、执行计划设计（§4.4）。阶段二的顺序与范围以本文为准 |
| [验收表](script-sdk-v2-render-acceptance.md) | 各里程碑的正式验收要求（M12～M14 行）。本文调整的范围在 P1 收口时同步写回验收表 |
| [M12 测量记录](script-sdk-render-m12-evidence.md) | M12 全部数据与决定原文（§3 起始测量，§5 门槛与修复，§5.14 审查后决定，§6 收口复测与关闭；§6.3 为 P2、P3 的回归基准） |
| [M12 交接](script-sdk-render-m12-handoff.md) | M12 优化期的状态与参考文件，「下一步」已移到本文 |
| [架构审查](../reviews/2026-11-10-architecture-review.md) | 结构整理 A～D 的依据（§3～§4、§8.1） |

## 1. 当前状态

- **M12 性能目标基本达成。** 测试包 18 组（7 个包 × 纯色／棋盘格背景，两个模糊包 σ 0／8）在 0.112.66 全部出帧：
  720p ≥ 23.99 fps，1440p ≥ 29.2 fps，无预算失败；材质组 Render p50 由 36～118 ms 降到 6～12 ms，图组由 6～8 ms 降到 3～4 ms
  （M12 记录 §5.12）。0.112.67 收紧图导出后图组在噪声内，1440p 图表面峰值约 12 屏降到 2.2 屏（§5.13）。
- **已完成的结构性修复**：O2（0.112.47）、O1（0.112.48～55，G2 3.3 → 1.04）、Worker gen0 预算（0.112.56）、O4（0.112.57）、
  F1～F6 与 O7（0.112.60～66）、图导出按目标区域收紧（0.112.67）。逐项见 M12 记录 §5.2、§5.7～§5.13。
- **M12 已关闭（2026-11-11）**：收口复测在 0.112.67 完成（M12 记录 §6）。门禁 H1～H4 通过；G1 六组加 GlassInk 棋盘格全部跑完 720p、1440p，
  只有混合组 Render p95（44.2 ms，目标 40）未达，G2～G4 达到（混合组改输入 p95 1.6 s）；后端全量 8274 项通过；
  合同冻结到 1.49.2（只改行为：缺背景媒体的预览诊断），前端副本已同步。S5 余项的例外经用户确认写入验收表 M12 行。
- **P2 结构整理已完成（2026-11-12，0.112.69～72）**：几何提供者 `SkiaSceneGeometry` 拆出渲染器，一个会话只建一个绘制管线；
  对象绘制分成建计划（`ObjectRenderPlan`）与执行计划两段，判空、层合并、区域收紧是计划上的步骤，两个调度器共用同一执行器；
  组合调度下导出对象的工作框按自身范围收紧。样例帧摘要新旧相同，桌面流同时段 A/B 无回退（§5.4）。当前结构见
  `backend/docs/architecture/render-pipeline.md` §0。

## 2. 已确认的决定

更早的决定（D1～D17、M12 门槛分门禁／目标／观察、O4 只在调试开）见主计划 §1.1 与 M12 记录 §5.0～§5.2。阶段二相关的：

1. **性能目标**是本机 720p24 稳定预览；门禁不达标保持阻塞，目标不达标登记差距、不阻塞（2026-11-09）。
2. **M12 先收口**：跑测试包基线、G1、门禁与观察项，防止回归，再合同冻结；之后才做结构整理（2026-11-10）。
3. **O3 不做**：现有「每个输入代次换新 Worker＋一个预热备用」够用，**D17 不变**；G4 混合组差距按目标未达登记（2026-11-10）。
4. **结构整理**在 M12 收口后做：审查 A（几何提供者拆出渲染器，为提升维护性）与审查 C 前三刀（对象级渲染计划最小版本）。
   C 第 4 刀（跨对象表面复用、每对象一屏下层快照）不排期（2026-11-10）。
5. **审查 B 第 2 层（作者可见的 SDK 2.0 表面）在 M13 冻结之前做**，与样式完善一起改，SDK 只改一次（2026-11-10）。
6. **完整基础样式在 M13 之前引入**（2026-11-10）：
   - 已唱与未唱**共享形状**：字形、字号、描边粗细／位置／拐角、阴影角度／距离／模糊、辉光模糊／扩张。
   - **分开外观**：填充、描边、阴影、辉光的颜色、不透明度、材质（含渐变）各有已唱、未唱两份。
   - 行样式的整体不透明度（`LyricStyle.Opacity`）**共享**。
   - 新增**走字线羽化**：宽度按字号比例；可在行样式、预设、元素覆盖中设置，SDK v2 脚本也能写。
   - **旧文件不做兼容**，不写迁移。
7. **S5 的文字类优化依赖样式稳定**，排在样式完善之后（分层光栅缓存、演唱揭示复用、缩放／倾斜／Warp 容差等，见 P4）。
8. **暂不动**：Windows 与跨平台（审查 §5）、前端手写 API 类型与合同双份（审查 F1、F2）。用户判断这部分改起来快，以后再做。
9. **缺背景**：渲染时退回兜底并给诊断，编辑时只提示不阻止，可选背景媒体缺失用工程底色（M12 记录 §5.7，已实施，前端提示未做）。
10. **测速**期间电脑空闲，可直接测；**G1（`run-g1.sh`）须先问用户**。
11. **导出遇到图／材质诊断时放开并上报**（2026-11-11）：导出（视频、图片）与预览一致，用兜底外观继续，
    警告随导出结果返回，前端提示；静态渲染会话与场景测量按同一规则处理。排在 P3 第 6 步，与合同次版本一起做（§6.6）。

## 3. 路线总览

| 阶段 | 内容 | 出口 |
|---|---|---|
| **P1 M12 收口**（已完成） | 回归基线、门禁与观察项复测，补 S5 报告类数据，合同冻结，写回验收表 | 2026-11-11 关闭，M12 记录 §6 |
| **时钟校准修复**（已完成） | 新 Worker 时钟校准失败后重试，不因一次往返过长停流（§4.5） | 0.112.68，`795ba4b` |
| **P2 结构整理**（已完成） | A 几何拆出；C 第 1～3 刀 | 2026-11-12 完成，0.112.69～72，§5.4 |
| **P3 样式完善** | 已唱／未唱外观分离、走字线羽化、SDK 2.0 表面整理（含 B 第 2 层）、格式与合同、前端 | 新能力有像素与结构回归；未使用新能力的内容像素与性能不变 |
| **P4 文字优化** | S5 余项，开工前与用户确认范围 | 按确认范围 |
| **M13** | 能力、版本、合同的发布候选验收，冻结 SDK 2.0 与格式 | 验收表 M13 行 |
| **M14** | 删除 v1 与最终验收 | 验收表 M14 行 |

P2、P3 不与其他改 `ImageScene*.cs`、`GroupComposition*.cs` 的工作并行。每个程序改动一个 PATCH（下一版 0.112.73）。

## 4. P1 M12 收口（已完成，2026-11-11）

结果见 M12 记录 §6。以下保留当时的清单，供 M14「M12 门禁全部通过」复测时照做；本轮的脚本在 `.local/script-sdk-m12-20261111/`
（`run-tests.sh`、`run-h2.sh`、`run-http.sh`、`run-g1.sh`、`aggregate-p1.py`，不入库），整轮串行约 3 小时。

目的：给当前渲染锁一份防回归的基准，并按关闭条件复测登记。测量期间冻结 `backend/src` 与 `Directory.Build.props`
（否则启动器会按工作区重建发布物）；测前 `node scripts/app.mjs build:desktop` 构建发布物。

### 4.1 复测清单

| 项 | 类别 | 工具（本地脚本不入库） | 对照 |
|---|---|---|---|
| 测试包 18 组 | 回归基准 | `.local/script-sdk-m12-20261110/run-baseline.sh`，汇总 `aggregate-p3.py` | M12 记录 §5.12（0.112.66） |
| G1：C、P、G、M-CustomInk、M-GlassInk、X | 门禁 H1、目标 G1／G3／G4 | `.local/script-sdk-m12-20261108/run-g1.sh`，**先问用户**，桌面窗口须在屏可见 | §5.3（0.112.58） |
| G2：T01 1080p 导出 96 行 ÷ 8 行 | 目标 | 同 §5.2 的 `g2-*` 运行方式 | 0.112.56：21.1 ms／帧，比值 1.04 |
| H2：M07 小场景像素与结构、M08 跨进程确定性 | 门禁 | `backend/tools/backend-script-render-upgrade/m07_matrix.py`、`m08_matrix.py`（调用见 `.local/script-sdk-m12-20261108/run-batch-04.sh`） | §3 ①②：M07 196 项中 195 项，M08 `equal` |
| H3：像素检查开／关一致 | 门禁 | 后端全量测试中的开／关对照 | 0.112.57 通过 |
| H4：≥ 6 min 浸泡 | 门禁 | `m12_http_probe.py`（同上目录），批次见 `run-batch-06.sh`（`soak-06/`），趋势 `.local/script-sdk-m12-20261108/soak_trend.py` | 后半斜率 ≤ 10 MiB/min，FD 平稳，旧 Worker 全部退出 |
| 观察：1080p 导出逐帧、HTTP seek 与首帧、作者改动到新帧 | 观察 | `run-formal-02.sh`、`run-batch-06.sh`（seek-soak、author-soak） | §3 ③④⑤ |
| 后端全量测试 | 回归 | 各测试项目 `dotnet test`，GPU 用例 `MAKO_GPU_SMOKE=1` | 全部通过（已知偶发见 §10） |

注意：
- G1 的 GlassInk 在纯色背景下按 0.112.61 起的行为走兜底（诊断 `BackgroundMediaUnavailable`），测不到真实材质路径；
  需另跑一次带棋盘格背景的（`run-g1.sh` 如不支持背景参数，按 `run-baseline.sh` 的 `--background-image` 补上）。
- 发现某组明显慢于对照时，用同时段 A/B 确认（`ab-run.sh`，用法见 M12 交接 §3 第 5 项），不与跨日基线直接比。

### 4.2 S5 报告类数据

验收表 M12 行要求报告缓存命中、峰值、读回量、剩余等待与帧延迟。收口时用现有数据与本轮复测汇总一份：
- 读回量：O4 后正式路径中间读回为 0，每帧只剩一次输出读回（§5.2）。
- 剩余等待：F2 后每帧只在交付前同步一次（§5.8）。
- 峰值：1440p 表面峰值回归（O7、第 12 条）与 Worker RSS／VRAM（G1、导出）。
- 帧延迟：P3 新鲜度 p95。
- 缓存命中：目前没有光栅缓存，写明「不适用，移交 P4」。

### 4.3 合同冻结

按 `backend/tools/backend-api-contract/README.md` 从当前源码重建 metadata，`freeze.py` 检查，`validate.py` 校验。
0.112.40 之后有行为变化（如缺背景诊断 `BackgroundMediaUnavailable` 进入 Worker 包诊断映射与预览诊断说明），
按合同变更流程确定版本（预计补丁版）并 `--write`。合同双份与前端类型生成不在此处处理（§2 第 8 条）。

### 4.4 关闭与写回

- M12 记录增加关闭一节：复测表、门禁结论、目标差距与原因（G4 混合组按 §2 第 3 条）。
- S5 余项的处理写进验收表 M12 行，**须用户在收口时确认**：
  - 有限采样 ROI：材质（O7）与行／字图（第 12 条）已覆盖；组目标图与组合调度工作框随 P2 C 第 3 刀。
  - 执行计划：最小版本移交 P2（C 前三刀）。
  - 分层光栅缓存、演唱揭示复用、粒子／Warp 批次、缩放／倾斜／Warp 容差：移交 P4，属批准的例外，不是放弃。
- 验收表 M12 行的关闭条件改为「选定的结构性修复」不含 O3。
- 更新本文 §1 与 M12 交接。

以上四项已于 2026-11-11 完成；S5 余项的例外登记经用户确认。

### 4.5 时钟校准修复（已完成，0.112.68；用户 2026-11-11 决定在 P2 之前单独修）

- **现象**：收口复测中 G1 图组有一次运行在改输入（换新 Worker）时停流，桌面主进程报 `Invalid calibrated clock`、
  `gpuBrokerProtocolFailed`（M12 记录 §6.6 ①）。约 190 份历史测速日志中没有出现过，重跑两次通过。
- **原因**：Worker 启动时只发一次 `clock-ping`（`GpuStreamSession.cs:124`），往返超过 50 ms 就回报 `calibrated:false`
  （`GpuClockCalibration.cs:21`）；桌面端把 `calibrated!==true` 当协议错误（`desktop/gpu-preview/wire.cjs:9`），双方都不重试。
- **修复**（`795ba4b`）：只改 Worker。单次往返不确定度超过 25 ms 时重新发 ping，一次流内最多 8 次，校准成功才发 `clock-calibrated`，
  用尽后才按原样回报 `calibrated:false`。桌面端在收到 `clock-calibrated` 之前本就不下发播放控制（`desktop/main.cjs` 的 `flushControl`），
  所以桌面端与消息形状都不用改。回归三项（`GpuStreamSessionTests` 两项、`GpuClockCalibrationTests` 一项），去掉重试后会话两项失败。
- **验证**：全量构建零警告；Worker、Hosting、Runtime.Hosting 测试与桌面协议测试通过；0.112.68 发布物上 G1 图组、粒子组各跑一遍正常
  （24.01 fps，1440p 30.0／29.2 fps）。原问题约 190 次才出现一次，实机无法直接复现，靠回归覆盖。
- 合同版本仍为 1.49.2，产品版本与源码指纹更新到 0.112.68，前端副本同步。

## 5. P2 结构整理（已完成，2026-11-12）

§5.1～§5.3 是开工前的计划原文，实际做法与结果见 §5.4。

### 5.1 A　几何提供者拆出渲染器（先做）

依据：审查 §3.3 R1、§8.1.2。
- **现状**：`SkiaRenderPipeline` 实现 `ILineGeometryProvider`，脚本宿主经它取场景帧（`FrameEffectHost.cs:158`、`FrameEffectHost.V2Anchors.cs:47`、
  `PureSourceIndexes.cs:345` 等）；`ProjectRenderSessionFactory` 为此每个会话建两个管线（`:55`、`:130`～`:134`）；
  `MeasureSceneFrame` 在渲染设备锁内执行（`SkiaRenderPipeline.Geometry.cs:24`）。
- **目标**：独立类型持有 `SkiaTextLayoutEngine` 与 `LayoutCache`，实现 `ILineGeometryProvider`；绘制管线依赖它；一个会话一个绘制管线。
- **范围**：几何侧 6 个 partial（`Geometry`、`Measurements`、`SceneFrame`、`SceneLayout`、`TokenGeometry`、`GroupGeometry`），
  加上它们调用的 `GetLayout`、`GetLineLayout`、`GroupInk`、`MeasureTextMarks`、`DecorationExtent`、`ElementPadding`，
  以及 `Ruby.cs`、`TextMarks.cs` 与主文件里的布局部分；`RenderCore` 留在绘制侧。
- **验收**：纯搬迁，CPU 像素逐字节不变，Vulkan 在既有容差内，结构化确定性测试不变。
- **注意**：`LayoutCache` 按引擎区分可共享与不可共享的布局（动态回退字体），归属保持原样。
- 与 P3 的关系：描边、阴影、辉光的外扩计算（`DecorationExtent`）在这里，P3 改样式时改动面更清楚。

### 5.2 C　对象级渲染计划最小版本（第 1～3 刀）

依据：审查 §3.3 R2／R3、§8.1.4；主计划 §4.4 `RenderExecutionPlan` 限于图场景单个对象的最小形态。
- **现状**：`DrawImageEffectScene` 内的 `DrawObject` 约 180 行、8 个局部函数，一边决定部件、判空、区域、层序，一边分配表面并绘制；
  `DrawGroupCompositionScene` 与 `DrawIsolatedScene` 各自调用它，要求结果一致。
- **第 1 刀**：拆成「建计划」与「执行计划」两段。计划只含数据：部件清单（成员、色彩部件、材质槽、是否已知为空）、
  图的执行序列与各自目标框（第 12 条的 `GraphTargetBounds`）、工作框、层顺序与可合并性、下层来源。行为与像素不变。
- **第 2 刀**：判空（F3）、层合并（F4）、区域收紧（O7、第 12 条）从执行路径移为计划上的步骤。像素不变；
  `PerfFixturePackageTests` 中按设备表面计数断言 F3、F4 的测试可改为断言计划内容。
- **第 3 刀**：两个调度器共用一个执行器，差别只剩下层来源；原「组合调度的工作框」在这一刀收紧。
  按现有两条路径的一致性测试验收；工作框变化后按 O7 的做法确认 Vulkan 容差与预算峰值。
- **不确定**：第 3 刀能否完全合一，取决于前缀调度与遗留调度在下层快照上的差异，开工时先读清再定。
- 每刀一个 PATCH，测试包同时段 A/B 复测。

### 5.3 收尾

- 更新过期文档：`backend/docs/architecture/render-pipeline.md`（基线仍是 0.70.1）、`SkiaRenderPipeline` 类注释（审查 R7）。

### 5.4 结果（2026-11-12）

| 步骤 | 版本／提交 | 做了什么 |
|---|---|---|
| A | 0.112.69，`2385e20` | 新类型 `SkiaSceneGeometry` 持有布局引擎与 `LayoutCache`，实现 `ILineGeometryProvider`；几何侧 partial 与注音、文字标记、装饰外扩的计算搬过去。绘制管线持有它并转交接口；会话工厂不再为脚本宿主另建一个管线。测量仍在渲染设备锁内 |
| C 第 1 刀 | 0.112.70，`a5fdc2b` | `DrawObject` 拆成 `BuildObjectPlan`（只产出数据）与 `ExecuteObjectPlan`；两个调度器都经 `DrawSceneObject` 进入 |
| C 第 2 刀 | 0.112.71，`5ec4cad` | 建计划分步：基础计划 → 区域收紧 → 工作框 → 部件 → 判空 → 层组装方式，每个优化只改写计划上自己的字段；计划类型对测试可见（`ObjectPlanBuilt`），新增按计划内容断言的测试 |
| C 第 3 刀 | 0.112.72，`54ccae6` | 组合调度下导出对象的工作框由整块导出框收紧到对象自身墨迹加图／材质声明的外扩与取样距离；前缀合成允许产物框小于下层框 |
| 收尾 | `8736cf4` | `render-pipeline.md` 新增 §0 当前结构，更正函数表 |

- **第 3 刀的实际形态**：前两刀之后两个调度器已经共用建计划与执行计划，差别只剩计划上的下层来源（目标快照／组合前缀），
  §5.2 的「不确定」不成立，不需要再合并代码。这一刀的改动是工作框收紧。
- **收紧的条件**：只在行的全部绘制单元都落在元素基础字几何内时收紧（`ObjectInkCoversWholeObject`）；带 Mapping（Warp）变换且没有材质的导出对象不收紧，保持整框。
- **正确性**：每步后端全量测试通过（第 3 刀 8280 项），构建零警告；t01、WordEmphasis、CustomInk、GlassInk、LineGlitch 五组样例在 0.112.68 与各步发布物上
  各 40 个跳转时刻的帧 RGBA 摘要全部相同；M08 矩阵 `equal`。新增回归 `G8_组合调度下对象工作框收紧且像素与整框一致`（4 例，变异验证会失败）、
  `对象计划的三个优化各自只改写自己的字段`。没有改动任何既有测试的断言。
- **M07 矩阵**：只有编译、准备会话两类耗时门槛超出（第 3 刀 4 项），同一时段在 0.112.68 的工作树上结果相同（5 项），属机器状态，渲染阶段的门槛全部通过。
- **行为差异（第 1 刀起）**：部件判空提前到建计划时；仅当计划期错误与下层快照预算失败同时成立时，先报前者。
- **性能**：桌面流同时段 A/B，旧 0.112.68（`795ba4b`）对 0.112.72，纯色背景，720p24 各 60 s，两轮；第 1 刀（0.112.70）此前按同样方式比过，结论相同。
  四组新旧都是 24.0 fps、完成比 1.000、无超过 250 ms 的卡顿，1440p 30 fps，暂停／跳转／改输入延迟与 Worker 内存、显存峰值持平。

  | 组 | Render p50／p95 ms，旧（两轮） | 新（两轮） |
  |---|---|---|
  | P（CursorTrail） | 9.9／15.3，10.2／14.7 | 10.2／14.3，10.1／14.1 |
  | C2（ClusterExit＋WaveWarp） | 2.7／4.1，2.8／4.2 | 2.7／4.2，2.7／4.1 |
  | GraphBlur σ8 | 3.0／4.0，3.0／3.9 | 3.0／4.0，3.0／4.1 |
  | MaterialBackdrop σ8 | 10.5／16.2，10.7／16.5 | 10.7／19.4，10.6／19.6 |

  MaterialBackdrop 的 p95 新侧高约 3 ms，逐帧按 5 s 分段看是每轮各一段零星尖峰、位置不固定；单独加跑三轮，旧 14.3／17.2／17.6，新 16.3／16.6／17.5，
  p50 都是 10.4～10.7。五轮合计旧 14.3～17.6、新 16.3～19.6，第 1 刀那次旧侧还出现过 18.0 与 23.8，判为噪声，不算回退。
  这次的收益在表面占用而不是耗时：组合导出对象不再按整块导出框分配工作表面。
  旧侧第一轮 C2 的 1440p 阶段一次未取到足够的当前代次帧（0.112.68 上，第二轮正常），与本次改动无关，按偶发记录。

## 6. P3 样式完善

### 6.1 模型

| 部分 | 已唱、未唱共享（形状） | 已唱、未唱各一份（外观） |
|---|---|---|
| 字体排版 | 字体、字号、字重、斜体、字距、对齐 | — |
| 填充 | — | 材质（纯色／渐变／包材质）；现有 `UnsungFill`／`SungFill` 已是这样 |
| 描边 | 宽度、位置（外／中／内）、拐角；开关 | 材质（含颜色、渐变） |
| 阴影 | 角度、距离、模糊半径；开关 | 颜色、不透明度 |
| 辉光 | 模糊半径、扩张；开关 | 颜色、不透明度 |
| 整体 | 不透明度（`LyricStyle.Opacity`）、缩放 | — |
| 羽化（新增） | 宽度（字号的比例），0 为硬边 | — |

- 开关属于形状：两态同时开或同时关。两态外观完全相同时视为未分开，绘制只做一份。
- 元素覆盖、预设、模板沿用同一划分；`LyricStyleProperty` 的外观属性按已唱／未唱各一个。
- 现状：`LyricStyle` 只有填充分两态，描边、阴影、辉光各一份（`Core/Projects/LyricStyle.cs`、`StrokeSpec.cs`、`ShadowSpec.cs`、`GlowSpec.cs`）。

### 6.2 走字线羽化

- **现状**：直绘路径按已唱区域画两遍，未唱一遍、已唱一遍，两遍用互补的硬裁剪
  （`SkiaRenderPipeline.cs` 的 `BuildSungRegions`、`ClipSungRegions`；Vulkan 下用整数像素区域保证互补）。
- **做法**：改为软遮罩混合，结果 = 未唱 ×（1−m）＋ 已唱 × m，m 沿走字方向线性过渡，宽度 = 比例 × 该元素字号。
  两态共享形状，覆盖范围完全相同，过渡带内不会出现晕圈或缝隙。实现上两态各画进一张层再按遮罩合成，每行多一到两张层表面。
- **羽化为 0 时走原来的硬裁剪**，像素逐字节不变，作为回归。
- 只在正在推进的边上过渡；已唱区域仍由每个已定时元素各自贡献，夹在中间的未定时元素不被染色。
- 进度与遮罩的映射（开始、结束、跨元素衔接时不跳变；元素级覆盖导致相邻元素宽度不同时的衔接）在 P3 第 1 步定稿并用测试覆盖。
- 文字标记按整体已唱／未唱切换（`mark.IsElapsed`），不羽化。
- 图场景路径的已唱覆盖输入（`SungCoverage`）与两态材质的合成用同一遮罩。

### 6.3 SDK 2.0 表面（含审查 B 第 2 层）

- **样式写入**：现在 `TextOutput.SetFill(target, FillState, material)` 已分两态，`SetStroke`／`SetShadow`／`SetGlow` 不分
  （`Scripting.Abstractions/V2/TextOutput.cs:87`～`:114`，值类型在 `StyleView.cs`）。改为形状与外观分开写：
  形状每个目标写一次，外观按 `FillState` 写，使「两态形状不同」在接口上无法表达。另加羽化写入。具体签名在第 1 步定稿。
- **B 第 2 层**（审查 §4.2 S3／S4、§8.1.3）：
  - 能力名集中定义（现约 40 个字符串字面量散在 SDK、宿主与 Core 清单解析），新增样式能力从同一处登记；
  - `TextOutput` 等句柄的构造器、`Complete()`、`Invalidate()` 改为内部成员；
  - `Evaluate` 的准备结果改泛型，去掉强转（审查 S6）。
- 宿主侧：`FrameEffectHost.Pure.cs` 的 `ComposePureOutput` 与跨实例合成按新写入调整（表驱动重写属 B 第 1 层，M14 之后）。
- 纯度分析器白名单、能力清单校验、样例与夹具中的 `SetStroke`／`SetShadow`／`SetGlow`（3 个文件）、D8 能力台账同步更新。

### 6.4 改动面

| 层 | 位置 |
|---|---|
| Core 样式 | `LyricStyle.cs`、`LyricStyleOverride.cs`、`LyricStyleProperty.cs`（现 26 个属性）、`LyricStyleResolver.cs`、`StyleBatchEditor.cs`、`TokenStyleSupport.cs`、`SceneTextAppearance.cs`、`SourceMaterialCatalog.cs`、预设相关 |
| 格式 | 工程 `Project.CurrentSchemaVersion` 30、模板库 `StyleTemplateLibrary.CurrentSchemaVersion` 8；`Persistence/PersistenceDtos.cs` |
| 准备帧 | `PreparedRenderFrame`（`BaseStyles`、`GetBaseDrawingStyle`）、材质槽 `PreparedMaterialSlot`（现为未唱填充、已唱填充、描边，描边拆两态） |
| 直绘 | `SkiaRenderPipeline.cs`（辉光现每行只画一次且在两遍之前，`DrawLineGlow`；两遍绘制与裁剪）、`TextPainting.cs`、`Ruby.cs`、`TextMarks.cs`、`TextPartitions.cs`、`WarpPartitions.cs`、`FamilyTransforms.cs` |
| 图场景 | `ImageScene.cs`（`ColorPartOrder`：底字／注音 × 辉光、描边、阴影、填充）、`ColorParts.cs`、`Capture.cs`、材质捕获；P2 之后在计划上增加两态部件 |
| HTTP 合同 | 样式属性 typed union（`static_style_schema.py`，现 24 个属性）、模板与预设 DTO；合同次版本升级 |
| 前端 | `protocol/style.ts`、`protocol/templates.ts`、`editor/model/style-overrides.ts`、`style-sources.ts`、`full-template.ts`、`editor/library/TemplateEditor.tsx`、`MaterialTab.tsx`、`draft-validation.ts` |

### 6.5 格式

旧文件不做兼容、不写迁移。工程格式升到 31、模板库升到 9，旧版本按现有机制明确拒绝（`UnsupportedSchemaVersion`），
而不是读到一半报字段错误；验收表 M13 行的「工程 30／模板库 8」相应改写。效果合同 2、脚本库 2、包 v3 不受影响。

### 6.6 实施步骤（每步一个或多个 PATCH）

1. **设计定稿（文档）**：SDK 写入签名、羽化进度映射、格式字段、合同变化。SDK 签名请用户确认后再写代码。
2. **Core 模型与格式**：样式、覆盖、解析、预设、模板，格式升版并拒绝旧版。
3. **渲染分态**：直绘与图场景按两态取外观，两态相同只画一份；描边材质槽拆两态；注音、文字标记、元素族、Warp 同步。
4. **羽化**：直绘与图场景两条路径；羽化为 0 的逐字节回归。
5. **SDK 表面与宿主**：§6.3 全部，样例、夹具、分析器、能力台账。
6. **合同与前端**：合同次版本、前端协议与样式／模板编辑器；顺带做缺背景的编辑时提示（§2 第 9 条）。
   **导出诊断放开**（§2 第 11 条）也在这一步：
   - 现状：`StaticExportTask.cs:455`（视频与图片导出）、`StaticRenderSession.cs:62`、`SceneMeasurementWorker.cs:52` 遇到任何图／材质诊断即失败
     （`WorkerExportDiagnosticsUnsupported`／`WorkerDiagnosticsUnsupported`）。诊断只有两种，都已有兜底：
     `TransparentBackgroundDependency`（透明输出）与 `BackgroundMediaUnavailable`（不透明缺背景媒体）。
     后果是依赖背景的材质缺背景时不能导出，并且永远不能出透明片。
   - 目标：导出照预览用兜底外观继续；警告去重后随导出结果返回（导出报告与任务结果加字段，有数量与字节上限），
     前端在导出前或导出后提示有几处用了兜底外观。静态渲染会话与场景测量同样放开，不静默丢弃。
   - 验收：缺背景与透明两种场景的导出成功且报告含对应警告；没有这类依赖的工程导出字节不变；合同例子覆盖新字段。
7. **复测**：测试包基线加「样式-分态」「样式-羽化」两组（纯色、棋盘格）；不用新能力的 18 组与 P1 基线比较，应在噪声内。

### 6.7 验收

- 两态外观相同、羽化为 0 的内容：CPU 像素逐字节不变，Vulkan 在既有容差内，表面数与 Render 时间不变。
- 分态与羽化：像素回归覆盖直绘与图场景两条路径、注音、元素覆盖、材质两态、透明输出；新回归须在旧代码上失败。
- SDK：形状无法按状态写入；能力缺失时按现有诊断拒绝。
- 格式：新格式保存重开一致，旧格式明确拒绝；合同 `freeze.py`／`validate.py` 通过；前端 `pnpm typecheck`、`pnpm test:unit` 通过。

## 7. P4 文字优化（S5 余项）

开工前与用户确认范围；先按 P3 后的基线测一次，确有余量需求再做。候选：
- 分层光栅缓存（主计划 §4.4）：缓存键含字体、资源、样式两态外观、羽化、裁剪、输出密度、设备代次；字节配额与淘汰。
- 演唱揭示复用：P3 的羽化遮罩独立于外观缓存。
- 粒子／Warp 批次。
- 缩放／倾斜／Warp 像素容差：引入缓存后测量提出，用户确认（主计划 §8、D13）。
- 报告缓存命中、峰值、读回量、等待与帧延迟的前后对照。

## 8. M13、M14

- **M13 发布候选**（验收表 M13 行、主计划 S6）：D8 每项能力收齐表达、作者入口、执行、诊断、确定性与性能证据；
  工程、效果合同、模板库、脚本库、包 v3、SDK2 一致读写与旧格式拒绝；CPU 对含 v2 效果的预览／验证／导出拒绝；
  合同从当前构建生成，前端同步，全量后端与相关前端回归。冻结 SDK 2.0，之后改作者可见表面须升 SDK 版本。
- **M14 删除 v1**：只做删除与随之而来的机械收敛（`FrameSession` 重放循环、`preservePure` 分支、`IsV2` 判断），
  以像素与结构确定性不变为门禁，不夹带重新设计；120 Hz 最终锚点记忆属于 v2，不能误删。
  重跑 M00 对照矩阵与资源趋势，M12 门禁全部通过，浏览器静态预览与桌面 GPU 分别验收。

## 9. 不做、未排期与待定

**不做或未排期**
- O3 输入改动按变化范围处理（D17 不变）；若以后重提，放在 M14 之后并先讨论 D17。
- C 第 4 刀：跨对象表面复用、每对象一屏下层快照（`SnapshotSceneBelow`，1440p 仍有约一屏固定占用）。
- 审查 B 第 1 层（宿主合成表驱动）：须在 M14 之后。
- Windows 与跨平台（审查 §5）、双端安装包：暂缓。
- 前端类型从 OpenAPI 生成、合同只留一份（审查 F1、F2）：暂缓。
- O5 粒子准备细分、O6 ClusterExit 分层 JIT；主计划 §7.9 独立研究（GPU 异步回读、NV12、桌面零等待、自研 Vulkan）。
- 审查 §7 的遗留清理（`SoundFlowAudioPlayback`、`probes/skia-gpu-target` 位置等）：随手可做。

**待用户决定**
- P3 第 1 步的 SDK 写入签名。
- P4 范围。
- 独立对象路径是否把节点声明的取样偏移并入工作框（见已知问题末条）。

**已知问题**
- 切到 1440p 时偶发新流停在暂停（5 次 1 次，原因未查，M12 记录 §5.8 ①）；收口复测 26 次运行未出现。
- Hosting 两项真实 Worker 用例在全量首轮偶发失败，单独重跑通过（M12 记录 §6.1）。
- 个别组最差 5 s 窗口 115～117 帧（目标 118），按机器抖动记录。
- G1 混合组 Render p95 44.2 ms（目标 40），约为四组内容之和，无单独异常项，按目标未达登记。
- M07 ClusterExit 热查询准备 p95 8.26 ms（观察项）。
- 带 Mapping（Warp）变换且没有材质的组合导出对象工作框不收紧，仍占整块导出框（P2 第 3 刀的保守条件）。
- 独立对象路径（O7 与图导出收紧之后）的工作框只含声明的外扩，不含节点声明的取样偏移（`MaximumSampleOffsetPx`）：
  图或材质声明了取样偏移、却没有同样大的外扩，并且读取下层或背景时，读不到目标框之外的像素。组合导出对象在第 3 刀已并入取样距离。
  现有样例包的偏移都等于外扩，不受影响；补上后样例工作框各边多 12～32 px，Vulkan 模糊在容差内变化，帧摘要基准要重取。

## 10. 工作约定

- Git：每步一个中文 `type(scope): 摘要` 提交，按路径暂存，里程碑推送到私有远程，提交尾行 `Co-Authored-By`。
  不碰未跟踪的 `.VSCodeCounter/`、`backend/tools/backend-script-render-upgrade/M07DiagnosticRunner/`、`TraceRead/`、
  `docs/plans/script-sdk-v2-m11-author-notes.md`；不改两个兄弟源仓库（棋盘格图只读取）。
- 程序改动：`backend/Directory.Build.props` 递增 PATCH，在 `backend/docs/architecture/backend-api.md` 第 1789 行的版本说明段落追加一句；
  `flock -o /tmp/mkg-gpu-render-dotnet.lock dotnet build MakoKaraGenerator.slnx` 零警告；新缺陷回归须在旧代码上失败（变异验证）。
- 测试：GPU 用例需 `MAKO_GPU_SMOKE=1`；Server 的 `RubyTimelineTransportTests` 三项需要已发布的 Server／Worker
  （`MAKO_RUBY_HTTP_SERVER_PATH`／`MAKO_RUBY_HTTP_WORKER_PATH` 指向 `.local/backend-dist/<目录>/server|worker` 下的可执行文件）；Rendering 全量偶发 Roslyn 内部异常或遥测计数失败，单独重跑通过即非回归；
  Vulkan 模糊随表面尺寸与原点变化，换框类改动按实测设容差。前端改动先过 `pnpm typecheck`、`pnpm test:unit`；不手改 `docs/backend/contract/`。
- 测速：先提交再测，测速期间不改 `backend/src`、`Directory.Build.props` 等会触发重建的文件；不并行构建或占用 GPU。
  比较用同时段 A/B：`bash .local/script-sdk-m12-20261110/ab-run.sh <输出目录名> <旧提交> <旧发布物> <新发布物> <轮数> "<背景> <组...>"`，
  输出目录名相对 `.local/script-sdk-m12-20261110/`、不带路径前缀，发布物为 `.local/backend-dist/` 下的目录名。
  两个提交之间有文件改名或新增时改用 `.local/script-sdk-p2-20261111/ab.sh`（用 `git restore --source` 让工作区逐文件等于目标提交，
  组写成 `"<名>|<样例,样例>|<参数>|<solid|checker>"`）；A/B 期间索引是旧内容，不得提交。
- 进程：按本次拥有的 PID 与身份停止子进程，禁止按名称批量终止。需要 root 的命令交给用户执行。
