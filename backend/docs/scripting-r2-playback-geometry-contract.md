# R2 播放头、几何查询与锚点映射实施合同

> 2026-10-03；设计基线 0.9.7。按已接受 ADR-0071～0072／SDK §3～5 继续施工。该记录不缩减完整 R2～R8，也不把数学映射描述冒充已执行的图像变形。

## 1. 坐标与当前播放头

- 播放头只来自真实 Current 的逐字区间及当前装载；准备、保留和缺时间元素不制造播放头。全行没有逐字时间时使用原行区间，ProgressSource.Line，没有 TokenId／OccurrenceId。
- 增加中立 LineTarget／LineGeometrySample，整行固定整形产生的字形／Advance／基线来自同一 Skia 几何入口；不是用元素均分行宽。TokenGeometrySample 增加真实 ProgressPath，横向坐标来自已有高亮的 shaped.XAt；与高亮共用浮点边界计算，避免字距／共享字簇／空白使墨迹框推算偏离。
- CursorAnchorMode 必须由消费者显式选 Baseline 或 AdvanceCenter，不藏一个 UI 默认。坐标是逻辑画布 px，Direction 是进度路径在最终矩阵下的单位方向；零长路径明确零向量。
- PreparedRenderFrame.Playback 保存所有合法并发光标，按完整目录稳定顺序。当前元素族缩放后的矩阵同样影响播放头，父组／槽位／微调不再乘一次；注音不改变基础字的进度路径或枢轴。
- 先构建 final 文字几何，再暴露 final 播放头；FrameContext 只读 base 几何查询，不能在文字 Evaluate 中取尚未生成的 final 光标形成自依赖。

## 2. 真实接续表

0.29.0 按当前脚本目标的代理决策授权实施，旧待答由 ADR-0125 裁决。源顺序的相邻演唱接续、同时起唱的并发组、缺行时间阻断、重叠零空档及真实端点装载规则见[后继合同](scripting-cursor-successor-contract.md)。CursorTransitionEvent 进入固定事件表及真实 callback／历史，保持结束／进入／起唱／转换排序；后继集合两端均按实际输出绑定检查，不从源订阅扩大粒子范围。

独立后继端点查询已接通；0.30.0 按[桥接合同](scripting-cursor-bridge-contract.md)／ADR-0126 接入固定半开飞行区间及逐边独立配额。多个合法后继各自发射，不猜唯一光标或修改演唱时间；空档仍没有 Current。

## 3. 基本与未来查询

- GeometryQueries.GetBaseAt(timeUs,SourceFilter) 返回中立 BaseSceneGeometry（真实 frame lines、token／line geometry），只调用共享布局／整形入口，不调用作者、绘制背景、生成粒子或推进主历史。完整源筛选独立于输出绑定；非法 ID 明确失败。
- 查询通道随作者阶段结束失效，取消保持取消。服务不暴露 ProjectSnapshot、Skia 或 native 资源。
- 最终未来锚点由宿主使用同一固定效果输入创建 ReplayReason.AnchorQuery 的独立语义会话，从零重放至目标起唱；不执行视觉／粒子／图像阶段，也不将实例或未来事件写回主会话。
- PreviousEndLimit 在整数微秒模型中取原区间的最后可表示时刻 EndUs-1；NextStart 取 StartUs。不能取 EndUs 而获取下一句，也不把进度伪造为 1。装载不见时明确返回不可用端点，不套用编辑试样或另一句坐标。

## 4. Typed AnchorMap

SDK 提供封闭 Affine／AxisWave 值，显式有限逻辑坐标定义域、映射方向、幅度／波长／相位；有界域外、不可逆仿射／非有限运算报错，禁止任意作者 native 回调。波形保留驱动轴，沿另一轴做正弦位移；因此可直接验证正／逆向一致。

本批数学值和查询不修改生产像素；完整 Mappings writer 与实际 MappedWarp 必须和 R3 图输出／依赖／bypass 一起接通，不能只移动播放头却让文字留在原处。后续按效果顺序在所属逻辑空间组合映射，ImageOnly 仍须显式选语义光标或整体 capture，不能猜 shader 的唯一锚点。

## 5. 验证与交付

验证真实 clip 边界／空白／字距／emoji／共享字簇／无逐字时间、前奏／任意槽位／父组／局部／族缩放下的光标；基础查询不派发事件，未来 callback 查询不污染主状态；稳定身份／多装载／绑定外后继、24／30／60 fps、跳转及独立导出一致。AnchorMap 数学值覆盖定义域／有限性、组合及波形正逆。构建／全测试／版本／文档／Git／启动按仓库 DoD。

## 6. 0.9.8 实施结果

§1、§3 的真实 Current／行依据、最终播放头、四阶段基本查询及独立语义端点均已接通；§4 数学值已实现，writer／生产图像映射仍未完成。§2 接续规则待答，不生成转换表。全量构建 0 警告／0 错误，1716 项通过；精确证明、日志及剩余出口见 [R2 记录 §3.6](archive/research/scripting-r2-sdk-geometry-progress.md#36-098-播放头与查询验证)。

补充：PlaybackFrame.IsAvailable 区分缺后端几何与当前没有演唱；不可用时 GetCursors 失败。半开 Current 以整数区间判断，极长区间的 double 进度可能舍入为 1，不因此把合法 Current 拒绝或静默改值。基础字与注音父矩阵一致，进度路径不使用注音联合框；彩色 Emoji 固有颜色、双向文字等既有绘制策略未新增推测性修正。本批不把路径值当作所有复杂脚本图像已经有唯一锚点。
