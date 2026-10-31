# R2 视觉阶段、最终几何与圆点输出合同

> 2026-10-03；**0.9.11 已实施六阶段、最终几何视觉上下文与生产圆点输出，完整 R2 尚未完成**。施工基线 0.9.10，实施依据为已接受 R0 §4～5／§8、SDK 六阶段及 ADR-0071／0075。设计先于代码；本批不改 JSON、程序集拆分或 UI。整行样式覆盖、组输出范围及重叠接续继续等待原提问答复。

> 本文保存 0.9.11 六阶段出口；0.10.0 已按用户确认新增独立 EvaluateEmitters 并移除 VisualWriter.Emitters，现行七阶段及作者粒子／出生记录见 [发射阶段合同](scripting-emission-stage-proposal.md)／ADR-0088。

## 1. 真正接通的阶段

同一作者实例增加 AdvanceVisuals／EvaluateVisuals，仍在主进程内串行执行。共享状态只由 OnEvent／Advance 更新，文字 Evaluate 先完成全实例合并和最终几何；随后 AdvanceVisuals 处理前一固定区间，EvaluateVisuals 提交只读帧描述。初始 0 没有零长视觉推进。非边界帧只纯求值，不增加推进、事件或持久对象。

VisualSimulationStep 为严格正长整数微秒半开区间。VisualStateContext 提供前一及当前边界的最终几何，以及截至当前边界的完整历史；PreviousHistory 明确保留区间左端前缀。VisualFrameContext 提供目标时刻最终几何、最近边界几何及 FractionalElapsedUs；作者可用自身固定状态作解析呈现，不在分数帧再次积分。上下文只有不可变 SDK 值，没有文字 writer、原生绘制对象、未来几何回调或可写工程。受信作者仍须遵守共享／视觉字段分区，宿主不宣称能检测任意字段修改。

最终几何包括真实 FrameLine／Token／Mark、FinalLine／Token／Ruby／Mark／Group 和 Playback。源绑定筛选匹配对象与播放头，Tokens 不获得行依据的假播放头；组仍为完整只读目录，读取不授予组写权。Unavailable 后端保留明确不可用状态，不伪造空光标。尚未接通的 Particles／EmissionSchedule／Emitters 不提供假服务。

纯帧工厂允许纯 EvaluateVisuals，但实现 AdvanceVisuals 的作者必须使用时间线会话；纯帧不提供模拟边界或历史。输出模式／目标密度改变时从 0 重建会话状态，原因 InputsChanged，避免旧边界几何继续驱动新输出。AnchorQuery 会话只重放语义和文字输出，跳过两个视觉阶段及所有 overlay；未来端点不能触发视觉 Error、发射或递归绘制，重建仍保持 AnchorQuery 原因。

## 2. 实际圆点输出与门禁

VisualWriter 仅在 EvaluateVisuals 有效，Overlay.DrawDots 接受稳定 OutputId、不可变 Dot 数组和 BehindText／AboveText。Dot 使用最终画布逻辑坐标、正有限半径和 sRGB 直乘颜色；不是修改源字字号或树。输出须声明 Overlay 能力；同实例同帧批次 ID 唯一，批次按 ordinal ID 排序，实例按固定数组顺序；批次内部按数组顺序。

预算沿用已接受 B：每帧所有实例合计绘制项初始上限 100,000，可由固定工厂显式传入 VisualOutputBudget；R4 再持久化到工程设置。超限列出请求值／限制，明确失败，不减少输出。default 数组、无效 ID／层级、非正半径、过期 writer 和能力缺失均失败；实际 float 坐标／半径越限或下溢由后端具名拒绝，不夹值。Error 诊断沿用 ADR-0072，取消／失败卸载并在后续请求重建。

PreparedRenderFrame 保存不可变圆点批次和完整诊断，文字及最终几何保持同一闭包；Skia 在背景之后、全部文字之前画 BehindText，在文字／注音／提示之后画 AboveText。逻辑尺寸随 RenderTarget 缩放，透明输出保留圆点自身 alpha；尚未有目标 capture／glitch／shader 的图执行，不将直接圆点绘制称为通用图已经完成。

## 3. 验证和剩余出口

真实 Roslyn 作者须在源 callback、共享 Advance、文字 Evaluate、视觉 Advance／Evaluate 中共享同一实例，读取生产最终矩阵与播放头，并实际绘制圆点。验证整数网格／精确事件、初始 0、历史前缀、分数帧／暂停重绘、24／30／60 fps／中段直接请求／回退／独立会话、前奏／注音／整行与族组合、两个图层和透明 alpha、密度、范围、批次顺序、全局预算、错误／取消／旧 writer 与真实编码中止。

本批只兑现有实际最终几何和圆点呈现的两个阶段，不替代 R6 的宿主管理出生、Burst 去重、拖尾、桥接及粒子视图。R2 其余 writer／转换／映射、R3 材质／图和 R4～R8 包／工作区／六包成片保持完整范围；实际版本、日志与自动验证证据见 §4。

## 4. 0.9.11 实施证据

全量构建 **0 警告／0 错误，1797 项通过、0 失败／跳过**；新增 Rendering 21、Scripting 4、Export 2。真实 Roslyn VisualProbe 在同一实例推进共享／视觉状态并输出圆点，固定网格加精确事件的顺序、左／右历史前缀和最终注音／播放头检查通过；24／30／60 fps、中段直接请求、回退及独立会话的实际画面一致。输出模式／密度变化从 0 重建且原因 InputsChanged；AnchorQuery 跳过两个视觉阶段，带视觉 Error 的包仍能独立解析文字端点。

纯 EvaluateVisuals 已实际消费生产最终几何；普通行、无逐字时间的行依据、绑定之外的演唱、后端明确不可用及三槽前奏两种附着覆盖。独立 Skia 圆点绘制与生产输出逐字节一致，覆盖透明 alpha、等比／非等比密度、前／后文字图层和多实例半透明叠加；既有文字闭包保持引用。声明能力、数组／半径／批次、全局预算、旧 writer、Error、异常／取消和原生越限／下溢具名失败。

两个视觉阶段的作者 Error 均在真实编码中途阻止导出，保留原文件并清理本次临时产物。最终日志 `/tmp/makokara-r2-visual-build.log`、`/tmp/makokara-r2-visual-tests.log`；focused 日志为初期定向检查，最终门禁以全量日志为准。没有新增脚本 GUI、Emitter／Particles／Sprites／Trails 或通用图执行，不能将本批圆点算作 R6 粒子完成。
