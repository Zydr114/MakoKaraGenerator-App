# R2 静态基线与元素族样式帧

> §1～4 保留 0.9.6 当批记录；后续已实施能力以 roadmap 和本文接续章节为准。

> 2026-10-03；程序 0.9.6，工程／歌词／模板 schema 21／15／3 不变。
> 按已接受 R0 合同 §5.1／ADR-0071 实施。当前仅元素族 Style 输出与静态 Token 基线；不是 R2 全部出口、R3 材质执行或 R5 callback 交付。

## 1. 已接通的路径

```text
固定 ProjectSnapshot
  → StaticStyleProjection.SourceStyles（工程→分支→行→元素）
  → BaselineStyles（所有真实源 Token，不受输出绑定或当前可见区间限制）
  → FrameContext.Baseline / FrameWriter.Style
  → 按效果数组顺序合并 StyleUpdate
  → PreparedRenderFrame.TokenStyles / GetTokenStyle
  → 固定 glyph 单元及关联 Ruby 的同一绘制路径
```

每个会话固定源样式表。所有实例读取同一静态基线；后项不会读取前项已经调制的颜色。FrameWriter 接受本帧实际装载的 TokenFamilyTarget，检查绑定、Style 能力、可独立字簇、资源修订与调用阶段。离开 Evaluate 后失效。

基线不包含源入场 alpha、脚本输出、字体／排版修改或联合墨迹推算的颜色。无变化的最终输出会移除，保留原绘制路径及潜在 Core 字段；不修改 Project、歌词、模板或脏状态。

## 2. 当前公开值与 writer

| API | 当前行为 |
|---|---|
| BaselineStyles.Get(TokenId / TokenFamilyTarget) | 固定元素外观；源元素可不可见／未定时。其他类型目标后续接入 |
| StyleView | 两态 MaterialValue、StrokeValue?、ShadowValue?、Opacity |
| RequireSungSolidColor | 非纯色时 BaselineMaterialNotSolid，不从渐变／shader 猜基色 |
| MaterialValue.Solid / Gradient / PackageExport | 封闭描述；渐变保留全部色标和参数，包材质只含固定句柄和参数 |
| SetUnsungMaterial / SetSungMaterial | 后项 Set 覆盖；null 非法。禁用填充用透明 Solid |
| SetStroke / SetShadow | 显式 null 关闭；未写字段继续使用静态基线 |
| MultiplyOpacity | 非负有限系数相乘；全部实例合并后最终 alpha 必须在 [0,1]，不夹值 |

StyleUpdate 保存明确写入；即使某实例写入值等于其基线，也要保留到实例合并后。否则“先改红，后项恢复源色”无法成立。只在最终合并后消除无变化输出。先乘 2 再乘 0.5 合法；最终大于 1 或系数溢出失败。

GradientValue 保存 Linear／Radial、Canvas／Line／Glyph／Progress、LinearRgb／Hsv／Oklch、HuePath、角度、径向中心／半径及原序色标。至少两点，首尾 0／1，非递减；同位置硬边合法，不排序修复。内容相等，不比较色标数组引用。SDK 材质不保存第二个纯色基色；当前 Core FillSpec 的 Color 仅由适配边界填入，不作为脚本的另一真值。

颜色为 double sRGB 直乘值；当前 BGRA8888 后端转换至 RgbaColor 时按通道乘 255、四舍六入五成双，随后沿现有预乘绘制规则执行。宽度等后端 float 参数检查有限范围及非零下溢，不静默夹值。

## 3. 生产执行边界

本批可以执行：纯色两态填充，现有 Linear／Radial＋Canvas／Line＋LinearRgb／Hsv 填充渐变，纯色外部描边（宽度／连接方式），现有阴影和样式透明度。它们可以与元素族视觉缩放同时作用。

- 基础字和全部注音使用同一最终外观；注音的字号／位置仍来自原布局，相同演唱进度保持。
- 全行仍只整形一次。变化族固定单元从原画中移除，再绘制一次；邻字不重排，原字／描边／阴影不叠画。
- Style alpha 在填充／描边／阴影各一次，源入场 alpha 在行图层一次。
- FinalGeometry 仍表达字形／注音墨迹和变换，不把新装饰外扩并入墨迹。
- 无变化的共享合字请求可保持原画；独立改色仍报 UnsupportedGlyphPartition。

静态元素覆盖也改用同一独立固定单元绘制入口。原矩形覆盖重画整行可能把邻字阴影染色、裁掉装饰或在旋转后移除邻字边缘；本批像素回归先暴露差异，再统一路径修复。原本没有元素覆盖的普通画面保持；源共享字簇的现行静态裁切策略保持，不拆字形。没有 JSON 或源排版变化。

当前明确失败而不会恢复无效果原画：

| 请求 | 诊断代码 |
|---|---|
| 未接入的 Glyph／Progress／Oklch 填充渐变 | UnsupportedEffectGradient |
| 包材质生产执行 | UnsupportedMaterialBackend |
| 渐变／包描边材质 | UnsupportedStrokeMaterial |
| 最终 alpha 越界／乘积溢出 | InvalidStyleOpacity |
| 后端参数溢出／下溢，径向实际半径需夹值 | UnsupportedNativeStyleValues |

PackageExport 的逻辑资源归属已检查，但生产执行仍在 R3；不能把句柄描述当成 shader 已交付。当前错误仍沿 EffectFailureException／PreparedFrameException 阻止导出，不与已接受的“透明模式缺背景有意跳过”混淆；后者在 R3 实施。

## 4. 验证与剩余工作

真正 Roslyn 编译的 StyleProbe 只引用公开 SDK，进入 PureFrameSourceFactory 和实际 Skia 管线。新增 Rendering 22、Scripting 9 项：

- 两态填充／描边／阴影／透明度、线性／径向／画布渐变与静态外观逐字节一致。
- 样式＋缩放与静态外观＋等价父组变换逐字节一致，包含关联注音。
- 前奏、父组旋转、槽位、单句微调及入场 alpha 与源覆盖逐字节一致。
- 缺时间字保持未唱外观；邻字像素不变，字形几何不变。
- 完整继承及不可见未定时源、共享合字、恢复基线、多实例 alpha、往返跳转、无变化的旧配置字段保留。
- 阶段／能力／绑定／资源修订、非法渐变、明确装饰关闭和后端精度／材质门禁。

本批全量结果见 [roadmap §18.35](archive/history/roadmap.md#1835-脚本-r2-静态基线与生产样式帧2026-10-03)。本机日志：`/tmp/makokara-r2-style-build.log`、`/tmp/makokara-r2-style-tests.log`；focused 像素／SDK 日志分别为 `style-pixels.log`／`style-sdk.log`，相同前缀。

仍需 R2：完整六阶段和上下文／诊断、行／组／标记基线与目标、Materials／ImageEffects／Mappings 及视觉协议、最终光标／转换／锚点和未来查询。R3～R8 的生产 shader／图、持久包与格式断代、120 Hz 重放／callback／音频分析、粒子、工作区与六包成片继续保持原范围；本批没有新增 GUI 脚本入口或宣称 RepeatEmphasis 完成。

## 5. 直接渐变描边与一次渐变 alpha（0.13.2 实施设计）

按已接受 R0 合同 §5／§6.1 补齐现有接口，无新数据编码或程序集。`SetStroke(TokenFamilyTarget／TextMarkTarget, StrokeValue)` 接受已支持的 Linear／Radial、Canvas／Line、LinearRgb／Hsv 渐变，保留全部色标、角度、中心／半径、宽度与连接方式。外部轮廓仍先扩张再减去字形内部，基础字与关联注音使用同一外观，不重整形或重新排版。

Core 的 `StrokeSpec.Gradient` 已存在；有效渐变允许 Color=null，仍是启用的描边。支持的渐变是实际颜色来源，Color 不作为第二个颜色或透明度来源。未接入的脚本渐变报告 UnsupportedEffectGradient；原生角度、径向中心或实际半径溢出／退化报告 UnsupportedNativeStyleValues，不夹值。包材质仍走 0.13.1 真实材质协调器。

现有渐变 shader 的色标已经包含 style.Opacity；普通文字／描边画笔须为不透明白色，避免再次乘 Color.alpha 或 style.Opacity。纯色继续沿原转换规则，源入场／对象／父组 alpha 按已有共同图层各一次。包基础外观的独立覆盖路径不额外改变。普通支持渐变的半透明像素会按此修正；无渐变的画面不应变化。

验收：实际 Roslyn 作者源码到准备帧及主 Render，与静态渐变描边逐字节一致；基础字／注音与前奏标记、两态、Canvas／Line、径向／线性及连接方式覆盖。独立不透明几何参考验证色标 alpha ×样式 alpha 各一次，不能只靠静态／脚本相等；非法组合与原生精度明确失败。实际 MP4／ProRes4444 与预览同帧对照，全量构建／测试后递增 PATCH、同步文档并提交远程。

本节先定设计再实施，现已完成自动验证。新增Core1／Rendering21／Export3，全量2485项通过、构建0警告／0错误；定向渲染114项通过。独立部件捕获检查128×0.65约83，历史纯色为透明或不透明均不改变结果，基础字／注音分别验证避免轮廓重叠误判。真实源码包经MP4／ProRes4444同帧验证，中途径向退化不覆盖已有输出。模板及当前工程重开保留Gradient与Color=null。具体日志、运行PID和剩余范围见[roadmap §18.64](archive/history/roadmap.md#1864-直接渐变描边与普通渐变单次透明度01322026-10-04)。完整 R2～R8 及容器组图待决策范围保持。
