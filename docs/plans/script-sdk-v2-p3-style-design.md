# P3 样式完善设计（2026-11-12）

状态：**需求与主要设计已由用户确认（§2 的 D-P3-1～13）；第 0 步前置修复已完成（0.112.73），第 1 步定稿细节已写入 §10（2026-11-12）。** 本文是 P3 的设计依据，记录需求、决定、代码调研结论、剩余细节和实施顺序。
[阶段二路线](script-sdk-v2-phase-two-roadmap.md) §6 只留摘要、进度与导出诊断放开（§6.3）；旧稿已删，对照请看 `e1306e4` 及更早的版本。
代码位置以 0.112.72（`54ccae6`）为准，路径相对 `backend/src/`。

**新会话从 §11「开发方向」开始。**

## 1. 需求（用户 2026-11-12）

1. **文字属性分三层**
   - 字形与布局：字体、字号、字重、字距、注音排版。两态共享。
   - 外观：填充、描边、阴影、辉光。未唱、已唱各一套。
   - 脚本动态效果：随演唱进度驱动，叠在最上面。
2. **通用 Fill**：至少支持线性渐变（已有）与硬边分层填充（字内分色，日式卡拉 OK 常见，也用于标识多位演唱者）；
   两者都能设角度，都能选坐标参考：按单个文字（内部渐变）或整行（外部整体渐变）。
3. **扫字**：基础是羽化边缘；在此之上扩展扫字线高亮，并把扫字线样式作为属性暴露给脚本。
4. **多重描边**（同日追加）：内外多层描边。
5. **注音**：用户设想注音继承所属字簇的样式（结论见 §7）。

## 2. 已确认的决定（2026-11-12）

| # | 决定 | 说明 |
|---|---|---|
| D-P3-1 | 外观层两态**全部独立** | 含描边宽度／拐角、阴影角度／距离／模糊、辉光模糊／扩张。**推翻**路线 §2 第 6 条「形状共享、只有颜色与材质分两态」；SDK「形状无法按状态写」的约束随之取消 |
| D-P3-2 | 硬边分层指**字内分色** | 一个字内部按角度分成若干色带 |
| D-P3-3 | 多重描边要做 | 做法见 §6；其中两点待确认（§6.3） |
| D-P3-4 | 「按单字」的参考框用**字框** | 字簇的宽度 × 字体上升部到下降部；单位是字簇（grapheme cluster） |
| D-P3-5 | 羽化带**以实际演唱位置为中心** | 过渡带 50% 处对应准确时间点；下一个字提前半个带宽开始变色 |
| 沿用 | 旧文件不做兼容、不写迁移；行整体不透明度与缩放共享；完整样式在 M13 之前引入 | 路线 §2 第 6 条其余部分不变 |

同日稍后用户又确认了以下各条（原为助手建议）：

| # | 决定 | 说明 |
|---|---|---|
| D-P3-6 | 已唱的描边、阴影、辉光默认**跟随未唱** | 显式设置后才分开；「未设置」与「显式关闭」区分 |
| D-P3-7 | 删除未实现的「进度」坐标 | 扫字已覆盖其用途；Oklch 插值仍不做 |
| D-P3-8 | 整行渐变的参考框改用真实字体度量 | 现有渐变工程像素有细微变化 |
| D-P3-9 | 多重描边按 §6 的设计做 | 上限 4 层；**阴影与辉光以整体轮廓（字形加全部描边）为来源，画在所有描边之下**；包材质只允许用在最内层描边，外圈只支持纯色与渐变 |
| D-P3-10 | 注音按 §7.2 做 | 继承所属元素的两态外观与扫字样式，P3 不做独立注音样式；装饰按 `RelativeScale` 缩放；渐变参考框按 §7.2 第 3 条 |
| D-P3-11 | 数据模型与 SDK 表面一次设计到位，实现分段验收 | M13 冻结 SDK |
| D-P3-12 | **SDK 写入签名**按 §9 | 外观写入全部带状态参数（未唱／已唱／两态）；描边写入接受多层；新增扫字样式写入与分层渐变的便捷构造；脚本只写未唱时已唱不跟着变 |
| D-P3-13 | **属性粒度**按 §4 的表 | 阴影、辉光保持分量级并按态展开；填充是整个材质；描边按整个列表覆盖 |

## 3. 现状（调研结论）

### 3.1 样式模型
- `Core/Projects/LyricStyle.cs`：`Typography`、`UnsungFill`、`SungFill`、`Stroke?`、`Shadow?`、`Glow?`、`VerticalCenterRatio`、`HorizontalAlignment`、`Opacity`、`Scale`。
  只有填充分两态。
- `FillSpec` 只包一个 `MaterialSpec`；`MaterialSpec` = `Solid(color)`｜`Gradient(GradientSpec)`｜`PackageMaterial(包, 修订, 导出, 参数)`。
  `StrokeSpec` 也持有 `MaterialSpec`（描边已支持渐变与包材质），另有 `WidthPx`、`Join`、`Position`（枚举只有 `Outside`）。
- `ShadowSpec`：颜色、不透明度、角度、距离、模糊。`GlowSpec`：颜色、不透明度、模糊、扩张。两者只有纯色。
- `LyricStyleProperty` 26 个属性，描边与辉光有重复粒度（`StrokeColor`／`StrokeWidthPx`／`StrokeSpec`；`GlowSpec` 加 4 个分量）。
  覆盖、预设、模板、HTTP 合同（typed union 24 个属性）与前端编辑器都按这份清单展开。

### 3.2 渐变
- `GradientSpec`：`Kind`（Linear／Radial）、`AngleDegrees`、径向中心与半径、`CoordinateSpace`（Canvas／Line／Glyph／Progress）、
  `ColorSpace`（LinearRgb／Hsv／Oklch）、`HuePath`、`Stops`（≥2，首 0 末 1，位置不递减）。
- `GradientSupport.IsSupported` 只放行 Canvas／Line 与 LinearRgb／Hsv；Glyph、Progress、Oklch 明确拒绝。前端 `TemplateEditor.tsx:534`～`:548` 同样标为不支持。
- **重合色标（硬边）在模型里已合法**（校验只拒绝位置倒序）。但 `SkiaRenderPipeline.cs` 的 `CreateGradientShader` 先用 `GradientSampler` 均匀重采样 65 点
  再交给 Skia，硬边被抹成渐变长度 1/64 的过渡。
- 参考框：Canvas 为整个画布；Line 为 `originX`、`baselineY − 0.85×字号`、宽 `shaped.Width`、高 `字号`（估算，不是字体度量）。
  线性渐变取参考框在角度方向上的投影半长，0°／90° 时首末色标贴住左右／上下边。
- SDK 侧镜像：`Scripting.Abstractions/GradientValue.cs`、`MaterialValue.cs`（Solid／Gradient／PackageExport）。

### 3.3 两遍绘制与已唱裁剪
- 直绘入口在 `SkiaRenderPipeline.cs`（`DrawStylePass`）：先 `DrawLineGlow`（每行一次，不分态），再未唱一遍、已唱一遍；
  每遍调用 `DrawStyledLine`（`TextPainting.cs`）画描边层、阴影滤镜与填充。两遍读同一份 `lineStyle`，只有填充颜色／渐变按态取。
- 已唱区域：`BuildSungRegions` 为每个有时间的元素加一个矩形（左端到 `TextProgressPath.XAt(left, right, progress)`），
  `ClipSungRegions` 用互补硬裁剪；Vulkan 下先转成整数设备像素区域保证两遍互补。未定时元素不被染色。
- 同一套裁剪在 6 处调用：主路径、`TextPartitions.cs`、`WarpPartitions.cs`、`FamilyTransforms.cs`（两处）、元素覆盖重画（`DrawTokenOverrides`）。
- 图场景路径复用这些绘制函数：每个色彩部件（`SceneColorPart`：底字／注音 × 填充、描边、阴影、辉光）用 `CapturePaint` 限定后捕获成一层；
  带包材质的填充部件拆成未唱、已唱两侧（`RequestedSungPass`），各是一层，层之间按普通叠加（SrcOver）组装（`ObjectPlan.cs` 的 `PlanObjectParts`、`AssembleColorParts.cs`）。
  材质槽 `PreparedMaterialSlot` 现为未唱填充、已唱填充、描边三个。

### 3.4 逐字簇绘制
- `SkiaTextLayoutEngine.DrawLine` 本来就按 `ShapedDrawUnit`（不可拆字簇）逐个提交文本图元；每个单元带 `X`、`Advance`、`Ascent`、`Descent` 和字形墨迹框。
  现在所有单元共用一个画笔和着色器。

### 3.5 进度与脚本
- `TokenSample` 只带夹到 0～1 的进度；元素的起止时间在 `LyricLine.Tokens[i].Time`，绘制处两者都拿得到。注音分段有自己的进度（`RubyTextSegmentSample`）。
- 脚本已能读扫字位置：`PlaybackCursor`（位置、方向、进度，含 Warp 映射）。不能写扫字线样式。
- 脚本样式写入：`TextOutput.SetFill(target, FillState, material)` 分两态；`SetStroke`／`SetShadow`／`SetGlow` 不分态（`Scripting.Abstractions/V2/TextOutput.cs`）。
  可写目标：元素族、底字、文字标记。

## 4. 外观两态（D-P3-1）

**可行，直绘路径几乎不增加开销**：两遍本来各画一整套样式，只需让每遍读自己那一态的外观。

- 模型方向：`LyricStyle` = 字形与布局 ＋ 整体（不透明度、缩放）＋ 未唱外观 ＋ 已唱外观 ＋ 扫字样式。
  外观 = 填充、描边、阴影、辉光。已唱的描边／阴影／辉光用「未设置＝跟随未唱」表达（仓库已有 `OptionalValue<T>`），与「显式关闭」区分。
- 两态解析后完全相同、且没有羽化时只画一遍；不相同才画两遍。
- 需要改的地方：
  - 辉光由每行一次改为每态一次（裁剪到各自区域）。
  - 装饰外扩（`SkiaSceneGeometry.Decoration.cs` 的 `DecorationExtent`、`ElementPadding`、捕获外扩、O7 的对象墨迹范围）取两态的最大值。
  - 描边材质槽拆成未唱描边、已唱描边；`ObjectPlan` 的部件规划里描边部件像填充一样分两侧。
  - 判空（F3）按两态都未启用才算空。
- 属性清单借这次重排：去掉描边、辉光的重复粒度，按「态 × 部件」组织。粒度（D-P3-13）：

  | 部件 | 覆盖粒度 | 说明 |
  |---|---|---|
  | 阴影、辉光 | 分量级，未唱／已唱各一份 | 与现状一致（现有键如 `shadow.color`、`glow.blurRadiusPx`），只覆盖写了的分量，其余跟随上一级 |
  | 填充 | 整个材质 | 与现状一致（`unsungFill.material`、`sungFill.material`） |
  | 描边 | 整个列表 | 多层后按层覆盖在层数增减时对不上号；这是唯一比现状变粗的地方（现状可单独覆盖 `stroke.widthPx` 等） |
  | 字形与布局、整体、扫字样式 | 分量级 | 扫字样式为新增 |
- 工作量最大的是这一项的外围：覆盖、预设、模板、HTTP 合同 typed union、前端样式与模板编辑器。

## 5. 通用 Fill（D-P3-2、D-P3-4）

### 5.1 硬边分层
- **数据不必新增类型**：重合色标就是硬边，一个渐变里可以混用软硬边。脚本侧加一个按「颜色＋分界」构造的便捷工厂；前端加分层编辑方式（管理成对色标）。
- 渲染：取样改为按色标分段，重合处向 Skia 提交位置相同的两个色标（Skia 原生支持）。没有硬边的渐变保持现有取样，像素不变。
- 0°／90° 的边缘清晰；斜角度的硬边没有抗锯齿，有 1 px 台阶，可用约 1 设备像素宽的过渡代替，实现时定。

### 5.2 坐标参考
- **Glyph（按单字）**：每个 `ShapedDrawUnit` 用自己的字框（`X`、`Advance`、`Ascent`、`Descent`）建着色器，`DrawLine` 改为可按单元取着色器。
  描边渐变同理；相邻字的描边重叠处按绘制顺序覆盖。
- **Line（整行）**：保留，参考框改用字体度量（D-P3-8）。
- **Canvas**：保留。**Progress**：删除（D-P3-7）。「按元素」（一个带时间的音节或词）一档 P3 不做（§10.4），枚举可后加。
- 径向渐变沿用同一套参考框。

## 6. 多重描边（D-P3-3）

**结论：可行，渲染成本低。阴影／辉光的来源和包材质的范围已由用户确认（D-P3-9）。**

### 6.1 现状
- 描边只有一层、只有外描边（`StrokePosition` 只有 `Outside`）。画法（`TextPainting.cs` 的 `DrawStyledLine`）：开一张层，用两倍宽度的描边画整行，
  再用字形把内部减掉（DstOut），所以透明填充不会露出内半圈。
- 层序是 辉光 → 描边 → 阴影 → 填充（直绘与图场景的 `ColorPartOrder` 一致）。**阴影是填充字形的投影**（挂在填充那一笔的滤镜上），画在描边之上；
  **辉光的遮罩是字形本身加扩张**，不含描边宽度，画在描边之下。单层细描边时看不出问题；描边加厚后阴影会盖在描边上，辉光被描边遮住。
- 装饰外扩 `DecorationExtent` 的旧式部分（描边＋阴影＋2）有 64 px 上限。

### 6.2 做法
- 模型：每一态的描边由单个改为有序列表（由内向外），每层有自己的宽度、材质、拐角；第 i 层占据从内侧各层宽度之和起、自身宽度的一圈。
  上限 4 层。空列表即无描边。
- 渲染：仍只用一张描边层。由外向内依次画，画笔宽度取累计外半径的两倍，后画的内圈用替换混合（Src）覆盖外圈的内侧，
  这样半透明的内圈不会透出外圈颜色，边缘仍有抗锯齿；最后同样用字形减掉内部。每多一层只多一次整行描边绘制，不多开层。
  每层可用纯色或渐变（含硬边、按单字）。
- 外扩：各处取「累计描边宽度」；64 px 上限要重新评估（厚描边加阴影容易超）。
- 注音、文字标记、元素覆盖重画共用 `DrawStyledLine`，随之支持。

### 6.3 已确认的两点（D-P3-9）
1. **阴影与辉光改为以整体轮廓（字形加全部描边）为来源，并画在所有描边之下。**层序变为 辉光 → 阴影 → 描边（外到内）→ 填充。
   不这样做，多重描边的厚轮廓会把辉光遮掉、阴影压在描边上。代价：现有「描边＋阴影」「描边＋辉光」的样式观感会变
   （阴影变大并退到描边之下，辉光从描边外缘起算）。用户已接受。
2. **包材质的范围**：包材质只允许用在最内层描边；其余各层只支持纯色与渐变。
   原因：包材质按「部件」捕获区域并占材质槽，每层都放开会让槽位变成 态 × 层数。实现上给图场景加一个「外圈描边」部件（底字、注音各一），
   装下第 2 层起的各圈、原样捕获；没有外圈时由判空（F3）跳过。图输入 `StrokeCoverage` 定义为全部描边圈的并集。
- SDK：`StrokeValue` 不变，描边写入改为接受列表，并保留单层的便捷写法；签名见 §9（`SetStrokes`）。

## 7. 注音的样式

**结论：用户设想的「注音继承所属文字的样式」与现有实现一致，保持并补齐两处缺口。§7.2 已由用户确认（D-P3-10）。**

### 7.1 现状
- 注音挂在**元素**上（`RubyAnnotation.BaseTokenId`），不是挂在字簇上；一个元素可以含多个字簇（如「漢字」配「かんじ」）。
  样式的最细粒度也是元素：脚本的字簇目标只能写变换（`cluster.affine`），不能写外观。所以准确的说法是「注音继承所属元素的样式」。
- 注音取所属元素解析后的样式（行样式＋元素覆盖；脚本写入时取元素族样式），字号乘 `RelativeScale`（默认 0.5）。绘制在 `SkiaRenderPipeline.Ruby.cs`，布局在 `SkiaSceneGeometry.Ruby.cs`。
- 脚本目标分「元素族」（底字加注音）与「底字」两种，没有单独的注音目标：注音的外观就是元素族的外观。图场景里注音有自己的四个色彩部件（填充、描边、阴影、辉光）。
- 注音有自己的演唱进度：分段有时间就按分段时间，没有就跟随所属元素的进度（`LyricSampler.SampleRubySegments`）；已唱区域是每个分段各自的硬裁剪矩形。
- **缺口一：装饰不随注音缩放。** 只有字号乘了比例，描边宽度、阴影距离与模糊、辉光模糊与扩张仍是底字的像素值；半大的注音带着与底字一样粗的描边。
- **缺口二：注音的渐变参考框不对。** 宽度用注音自己的整形宽度，高度却用底字字号估算，上下方向的渐变在注音上只显示出一部分色标。

### 7.2 做法（已确认）
1. **继承**：注音继承所属元素的两态外观与扫字样式，不单设样式。以后若需要「注音与底字不同色」，再加一层可选的注音覆盖，不在 P3 做。
2. **装饰按比例缩放**：描边各层宽度、阴影距离与模糊、辉光模糊与扩张都乘 `RelativeScale`；羽化与高亮宽度按字号比例，自动随注音字号。
   现有带描边的注音观感会变（描边变细），用户已接受。
3. **渐变参考框**：
   - 按单字：注音每个字簇用自己的字框。
   - 整行：横向用**底字整行**的范围，注音的颜色与正下方的文字对齐；纵向用注音自己的字框，上下分色与上下渐变在注音上完整呈现。
   - 画布：不变。
4. **扫字**：进度来源不变（分段时间优先，否则跟随元素）；羽化带画在分段的演唱位置上，做法与底字相同。
5. 多重描边、硬边分层对注音同样生效（共用绘制函数）。

## 8. 扫字（D-P3-5）

### 8.1 羽化
- 做法：过渡带内未唱、已唱各画进一张小层，按遮罩相加，结果 = 未唱 ×（1−m）＋ 已唱 × m；过渡带外仍用硬裁剪。
  层只覆盖过渡带（带宽 × 行高，加装饰外扩），通常每行只有一个正在演唱的元素。两态形状不同也成立（是两张完整画面的交叉淡化）。
- 带宽按字号比例；为 0 时走原来的硬裁剪，像素逐字节不变。
- 进度映射：以演唱位置为中心。元素在自己的时间段之外按时间外推演唱位置（需要元素起止时间），使相邻元素衔接时过渡带连续进入、退出；
  相邻元素速度不同时边界处有短暂的小台阶（幅度与速度差成正比，交界那一刻为零）。未定时元素不染色。公式与测试点见 §10.1。
- 方向跟随 `TextProgressPath`（已含起止 X 的方向）。文字标记按整体已唱／未唱切换，不羽化。
- **成本最高的是图场景＋包材质**：两侧材质层现在按普通叠加组装，羽化后在过渡带重叠，必须先把两侧相加成一张再叠
  。定稿做法是两侧各按全覆盖执行后按 m 插值成一对再合成一次，见 §10.2。
- Vulkan 下过渡带与两侧硬裁剪区的边界同样要用整数设备像素区域，保证三块互补。

### 8.2 扫字线高亮与脚本
- 高亮：过渡带内再画一遍高亮色，强度沿扫字方向是以演唱位置为中心的峰；参数为颜色、宽度（字号比例）、不透明度、混合方式。作用范围（只填充还是含描边）未定（§10.3）。
- 模型：`WipeSpec { 羽化宽度, 高亮? }`，放在行样式上，可被预设与元素覆盖。
- 脚本：新增写入口（暂名 `SetWipe(target, WipeValue)`）与能力名；读取已有 `PlaybackCursor`。

## 9. SDK 表面（D-P3-12，用户已确认写法）

现状（`Scripting.Abstractions/V2/TextOutput.cs`）：`SetFill(target, FillState, material)` 分态，`SetStroke`／`SetShadow`／`SetGlow` 不分态。改为：

```csharp
text.SetFill(target, state, material);
text.SetStrokes(target, state, stroke1, stroke2);   // 由内向外，0～4 层；不传即无描边
text.SetShadow(target, state, shadow);              // null 为关闭
text.SetGlow(target, state, glow);                  // null 为关闭
text.SetWipe(target, wipe);                         // 新增：羽化宽度、高亮
```

- **状态参数**取 未唱、已唱、两态。传「两态」等于两态各写一次。枚举的代码名未定（现名 `FillState` 已不贴切，助手建议 `TextState { Unsung, Sung, Both }`），实现时定，不需再确认。
- **脚本只写未唱时已唱不跟着变。**「已唱跟随未唱」（D-P3-6）只在样式解析时生效：交给脚本的基线已经是两套完整外观，脚本写哪一态就只动哪一态。
  `StyleView`（脚本读到的基线）相应改为两态各一套外观。
- **新增**：`SetWipe` 与扫字样式值类型；渐变加按「颜色＋分界」构造分层的便捷工厂；渐变坐标加「按单字」、去掉「进度」。
- 可写目标与能力名沿用现有划分（元素族、底字、文字标记；`style.fill`／`style.stroke`／`style.shadow`／`style.glow`），新增扫字的能力名。
- 同一步还要做「B 第 2 层」（路线 §2 第 5 条；架构审查 §4.2 S3／S4、§8.1.3、S6）：能力名集中定义；`TextOutput` 等句柄的构造器、`Complete()`、`Invalidate()` 改为内部成员；`Evaluate` 的准备结果改泛型。
- 波及：宿主合成（`FrameEffectHost.Pure.cs`）、`StaticStyleProjection`、纯度分析器白名单、能力清单校验、样例与夹具里的 `SetStroke`／`SetShadow`／`SetGlow`、D8 能力台账。

## 10. 定稿细节（第 1 步，2026-11-12）

第 1、2、5、6 条已定稿，做法如下；第 3 条有观感取舍，第 6 步给出效果后请用户选；第 4 条不在 P3 做。

### 10.1 羽化的进度映射

**符号。** 一个有时间的元素 i（进度来源 `FromToken`）：
- 横向起止 L<sub>i</sub> = originX + shaped.XAt(utf16Start)，R<sub>i</sub> = originX + shaped.XAt(utf16End)（与 `BuildSungRegions` 相同，从右向左排版时 R < L），方向 d<sub>i</sub> = sign(R<sub>i</sub> − L<sub>i</sub>)；
- 时间段 [s<sub>i</sub>, e<sub>i</sub>)，即元素自己的时间（就是进度的来源）；帧时刻 t；
- 带宽 W<sub>i</sub> = 比例 × 行字号（逻辑像素），比例取该元素解析后的扫字样式（元素覆盖不能改字号，所以用行字号）。

1. **演唱位置**（不夹值）：x<sub>i</sub>(t) = L<sub>i</sub> + (R<sub>i</sub> − L<sub>i</sub>)·(t − s<sub>i</sub>)/(e<sub>i</sub> − s<sub>i</sub>)。
   t 在时间段内时，结果与 `TextProgressPath.XAt(L, R, progress)` 相同（同一单精度插值）。另写一个不夹值的版本；脚本可读的 `PlaybackCursor` 不变。
2. **遮罩**：m<sub>i</sub>(x) = clamp(0.5 + d<sub>i</sub>·(x<sub>i</sub>(t) − x)/W<sub>i</sub>, 0, 1)，只作用在元素自己的横向区间内。
   m = 1 是已唱，m = 0 是未唱；x = x<sub>i</sub>(t) 处 m = 0.5（D-P3-5）。
3. **区域**（每个元素各自划分）：
   - 已唱硬区 H<sub>i</sub> = {m<sub>i</sub> = 1}；
   - 过渡带 B<sub>i</sub> = {0 < m<sub>i</sub> < 1}；
   - 其余为未唱。

   整行的已唱硬区、过渡带是各元素的并集。未定时元素、`TokensMatchLineText` 为假的行不贡献区域（与现状相同，不染色）。
4. **生效时间**：B<sub>i</sub> 非空，当且仅当 t ∈ (s<sub>i</sub> − W/(2v<sub>i</sub>), e<sub>i</sub> + W/(2v<sub>i</sub>))，其中 v<sub>i</sub> = |R<sub>i</sub> − L<sub>i</sub>|/(e<sub>i</sub> − s<sub>i</sub>)。
   - 元素在开始前半个带宽（按自身速度换成时间）开始变色，结束后半个带宽完全变成已唱；
   - 此后 H<sub>i</sub> 是整个区间，与现状 `IsSung` 一致；
   - 零宽元素（L = R）或零长时间段没有过渡带，照现状硬切换。
5. **相邻元素衔接**：交界 x = R<sub>i</sub> = L<sub>i+1</sub>，左侧 m = clamp(0.5 + v<sub>i</sub>(t − e<sub>i</sub>)/W)，右侧 m = clamp(0.5 + v<sub>i+1</sub>(t − s<sub>i+1</sub>)/W)。
   - e<sub>i</sub> = s<sub>i+1</sub> 时，交界那一刻两侧都是 0.5；之后差值为 (v<sub>i</sub> − v<sub>i+1</sub>)(t − e<sub>i</sub>)/W，只在交界前后约半个带宽的时间内存在（§8.1 的小台阶）。
   - 时间有间隔或重叠时，各自独立计算，不做平滑。
6. **注音分段**：
   - 分段有自己的时间（`FromRubySegment`）时，用分段时间与分段横向区间；
   - 没有时间而跟随所属元素时（`SampleRubySegments` 的 Missing 分支），用元素的时间段与分段自己的横向区间，与现在「各分段按元素进度各自硬裁剪」一致；
   - W 用注音字号（行字号 × `RelativeScale`）。
7. **羽化为 0** 完全走现有的 `BuildSungRegions`／`ClipSungRegions`，像素逐字节不变。文字标记按整体切换，不羽化。
8. **数据**：`TokenSample`、`RubyTextSegmentSample` 增加时间段（只在有时间来源时提供），绘制处按帧时刻求 x<sub>i</sub>(t)；脚本可见的 `PerformanceSample` 不变。

**直绘合成**（`BuildSungRegions` 的 6 处调用共用一个区域构造与合成函数）：
- 硬区照旧：已唱硬区只画已唱一遍，未唱区只画未唱一遍，互补裁剪。
- 过渡带开一张层，范围是带宽 × 行高，加两态装饰外扩中较大的一个：
  - 未唱一遍画进去后，用 (1 − m) 做 DstIn；
  - 已唱一遍画进另一张，用 m 做 DstIn；
  - 两张以 Plus 相加，再 SrcOver 到画布。结果 = 未唱 × (1 − m) + 已唱 × m。
- **三块区域互补**（CPU 与 Vulkan 同一做法）：过渡带的设备路径先沿扫字方向向外各扩 1 个设备像素，再转成整数像素区域（`SKRegion`），从两块硬区里扣掉；
  m 在像素中心按解析式取值并夹到 0～1。多扣进来的像素 m 正好是 0 或 1，与相邻硬区连续，没有缝也没有重叠。
- 两态解析后完全相同（且没有高亮）时，整行只画一遍，与羽化无关。

**测试点**：
- 羽化为 0：直绘、图场景、注音、元素覆盖、Warp、分区的现有回归全部逐字节不变。
- 单元素：
  - t = s<sub>i</sub> 时，x = L 处 m = 0.5；
  - t = s − W/(2v) 时恰好全是未唱，t = e + W/(2v) 时恰好全是已唱；
  - 带内 m 线性变化，以 x<sub>i</sub>(t) 为中心；
  - 从右向左排版时方向反转。
- 相邻元素：同速时交界两侧 m 相等；不同速时交界处的台阶等于上式，交界那一刻为 0。
- 夹在中间的未定时元素不染色；注音分段有时间、无时间各一例。
- 互补：两态同色时关掉「相同只画一遍」，羽化前后像素差不超过 1/255；棋盘格背景、两态半透明时，带外像素与硬切换版本逐字节相同。
- 图场景与直绘逐像素对照（现有方式）；新回归做变异验证。

### 10.2 图场景与包材质下的层组装

**哪些部件受影响。** 原样部件经 `CapturePaint` 调用直绘函数，带内的交叉淡化在部件内部已经完成，层组装不变。

受影响的是**拆成两侧的部件**：
- 现状：填充在任一态用了包材质时，拆成未唱、已唱两层（`RequestedSungPass`）；
- 第 3 步以后：最内层描边也会按态拆开。

两侧现在各占互补的硬区，按层序依次合成（无历史的层用 SrcOver，有历史的层用 `CompositeChain`）。
羽化后两侧在带内重叠，依次合成不等于交叉淡化。例如两侧都不透明、各乘遮罩后依次叠加时，带内结果是 已唱·m + 未唱·(1 − m)² + 下层·m(1 − m)，下层会透出来。

**做法。** 本帧对象有非空过渡带时：
1. **每侧的区域**：本侧硬区 ∪ 过渡带，带内按全覆盖处理，不乘 m。
   - 材质看到的目标区域与宿主遮罩都用这个区域；
   - 原样一侧按同一区域裁剪捕获。
2. **两侧合并成一层**再进组装。同一部件的两侧在层序上相邻（现状如此），合并后占原来两层的位置。
   - 每侧先求出（颜色 c，覆盖 k）对，与组合导出的闭合对是同一种形式，复用 `CaptureCompositionPayload`：
     - 原样层：k = α；
     - SourceOver 材质：k = α；
     - ReplaceCoveredBackdrop 材质：k = 声明的覆盖与链上历史替换范围的并。
   - 合并后的一对为 (lerp(c<sub>U</sub>, c<sub>S</sub>, m), lerp(k<sub>U</sub>, k<sub>S</sub>, m))，按替换规则 out + below·(1 − k) 合成一次。
3. **为什么严格成立**：两种合成模式对下层都是仿射的，comp(below) = c + below·(1 − k)，所以上式恰好等于 lerp(comp<sub>U</sub>(below), comp<sub>S</sub>(below), m)，即两侧完整结果的逐像素交叉淡化。
   带外 m 为 0 或 1，退化为单侧，与现状相同。
4. **两条调度路径**：
   - 层组装：合并层是一种新的「有历史」层，在单遍组装处断开，用现有替换着色器（`sourceOver = 0`）合成；
   - 组合导出：合并后的一对直接作为闭合对导出。调度器本来就按替换规则合成闭合对，结果相同。
5. **扫字遮罩**：每个对象每帧一张覆盖表面，范围是对象工作框，按与部件捕获相同的行／族世界矩阵画出：
   - 已唱硬区为 1，过渡带内是解析渐变，其余为 0；
   - 图输入 `SungCoverage` 在羽化时取 字形覆盖 × m。羽化为 0 时 m 就是现在的硬区，结果不变。
6. **不变与成本**：
   - 本帧没有过渡带（羽化为 0，或没有元素处在过渡中）时，走现有路径，像素、表面数、预算与耗时都不变；
   - 两态解析后相同的部件不拆侧，不需要合并；
   - 有过渡带时，每个对象多一张遮罩，每个拆侧部件多两侧的闭合对与合并后的一对，约 +3～4 张表面，计入对象的层预算与峰值预检。

**测试点**：
- 两侧都是材质、一侧是材质，以及 SourceOver／ReplaceCoveredBackdrop 两种合成，合并结果与「两侧整片执行后按 m 插值」的参考逐像素一致；
- 两条调度路径结果相同；
- 羽化为 0 时表面数不变；
- 变异验证：改回依次合成，回归失败。

### 10.3 高亮的作用范围与混合方式（第 6 步，待用户选）

候选：
- 作用范围：只作用于填充，或填充加描边；
- 混合方式：普通、滤色、相加。

第 6 步做出样张后请用户选。模型先留 `blend` 字段（§10.5），取值以用户选定的为准。

### 10.4 「按元素」坐标

P3 不做。坐标枚举以后可以直接加一档，不影响本次格式。

### 10.5 格式字段与合同变化清单

**文件格式**：旧版本一律明确拒绝，不写迁移。
- 工程读取要求版本完全相同；
- 歌词迁移链到 17 为止，18 → 19 没有步骤；
- 两者都报 `UnsupportedSchemaVersion`，文件不改。

| 文件 | 版本 | 变化 |
|---|---|---|
| 工程 `project.json` | 30 → 31 | `lyricStyle` 改为新结构；文字标记外观（`sceneContent.appearance`／`appearanceOverride`）同样改为两态外观；`presetApplications.templateSchemaVersion` 随模板库变为 9 |
| 歌词修订 | 18 → 19 | 分支、行、元素的 `styleOverride` 改为新的覆盖结构 |
| 模板库 `index.json` | 8 → 9，目录 `templates-v9` | 模板的 `style` 与 `properties` 改为新结构与新属性清单；模板导出包与之同格式；旧目录保留，照现有的「旧库保留」提示 |
| TTML 工程档 | `lyrics-schema` 随歌词改为 19 | 样式覆盖本来不导出，只改版本号 |
| 私有快照（Server→Worker） | 外壳 `snapshot-v1` 不变 | 内含的工程、歌词、模板版本校验随之为 31／19／9；Server 与 Worker 同一发布 |
| 不变 | — | 效果合同 2、脚本库 2、包 v3 |

验收表 M13 行的「工程 30／模板库 8」在第 2 步改写。

**样式结构**（Core、持久化、HTTP 三处同形，持久化与 HTTP 字段名一致）：

```text
style = {
  typography,                                    // 不变
  unsung: { fill, strokes, shadow, glow },       // 完整外观
  sung:   { fill, strokes, shadow, glow },       // fill 必填；后三项可「跟随未唱」
  wipe:   { featherRatio, highlight },           // 新增
  verticalCenterRatio, horizontalAlignment, opacity, scale   // 不变
}
fill    = 材质（solid｜gradient｜package，与现在的 unsungFill.material 相同）
strokes = [ { material, widthPx, join } ]        // 由内向外，0～4 层；空表为无描边
shadow  = null｜{ color, opacity, angleDegrees, distancePx, blurRadiusPx }
glow    = null｜{ color, opacity, blurRadiusPx, spreadPx }
已唱一侧的 strokes／shadow／glow：{ "kind": "follow" }｜{ "kind": "set", "value": <上面的值，可为空表或 null> }
wipe.featherRatio：≥ 0，0 为硬边，上限 2（带宽至多两倍字号）
wipe.highlight：null｜{ color, widthRatio, opacity, blend }
```

规则：
- **描边**：删去 `position`，各层位置由层序决定。宽度须为正的有限数。包材质只允许用在第 0 层（最内层，D-P3-9），其余各层只能用纯色与渐变。
- **渐变**：`coordinateSpace` 删去 `progress`（D-P3-7），`glyph` 改为受支持；`oklch` 仍在枚举里但不受支持（不变）。重合色标本来就合法。
- **「跟随」**用显式的 `kind` 表示，不用「键缺失」，以免与 `null`（关闭）在客户端混淆。完整样式、覆盖、模板、HTTP 用同一种写法。
- **部分覆盖与跟随**：分量覆盖（如已唱阴影的颜色）叠在该态解析出的基线上；该态是「跟随」时，基线就是未唱解析值。
  部分覆盖落在关闭的基线上时，沿用现有规则，以透明默认值补齐其余分量。
- **文字标记外观**：字形 ＋ 待唱外观 ＋ 已过外观（已过一侧的描边、阴影、辉光同样可以跟随）＋ 不透明度，不含扫字样式。

**属性清单**（`LyricStyleProperty`、HTTP `styles.patch` 的属性路径、`styles.restore`、来源追踪、`TemplateProperty` 一一对应，共 43 个，替代现在的 26 个枚举与 29 条路径）：

| 组 | 路径 | 数量 |
|---|---|---|
| 字形 | `typography.fontFamilyName`、`fallbackFamilyNames`、`fontSizePx`、`fontWeight`、`isBold`、`isItalic`、`letterSpacingPx` | 7 |
| 填充 | `unsung.fill`、`sung.fill` | 2 |
| 描边 | `unsung.strokes`、`sung.strokes`（整表覆盖） | 2 |
| 阴影 | `{unsung,sung}.shadow`（整体，含关闭与跟随）及 `.color`、`.opacity`、`.angleDegrees`、`.distancePx`、`.blurRadiusPx` | 12 |
| 辉光 | `{unsung,sung}.glow`（整体，含关闭与跟随）及 `.color`、`.opacity`、`.blurRadiusPx`、`.spreadPx` | 10 |
| 扫字 | `wipe.featherRatio`、`wipe.highlight`（整体）及 `.color`、`.widthRatio`、`.opacity`、`.blend` | 6 |
| 整体与布局 | `verticalCenterRatio`、`horizontalAlignment`、`opacity`、`scale` | 4 |

去掉的旧路径：
- `unsungFill.material`、`sungFill.material` 改名为 `unsung.fill`、`sung.fill`；
- `stroke`、`stroke.material`、`stroke.widthPx`、`stroke.join`、`stroke.position` 由整表覆盖替代；
- `shadow.*`、`glow.*` 按态展开。

元素覆盖可设置的属性沿用 `TokenStyleSupport` 的划分，字形类仍不支持；扫字样式可由行、预设与元素覆盖设置（§8.2）。

**HTTP 合同**：1.49.2 → 1.50.0（次版本，按 P3 设计）。
- **性质**：样式相关形状不兼容地改变，合同正文写明。前端在同仓同版本升级，不支持旧前端。
- **协议号**：协议主版本（`BackendProtocol.Version` = 1）是否随之升级，第 7 步按合同变更流程（`backend/web/api-contract.md` §4）定。
- **受影响的 DTO**：
  - `CanonicalStyleDto`（新增外观与扫字类型）、`StyleChangeDto` 联合（上表 43 条）、`RestoreStylesCommandDto`；
  - `ProjectStyleResponse`、解析样式的来源路径；
  - `TemplateProperty`，以及模板定义、列表与传输；
  - 模板工坊；
  - 前导与文字标记的外观 DTO（`BackendLeadInContracts`、`BackendLeadInEditingContracts`）；
  - 预设应用。
- **渐变枚举**：去掉 `progress`。
- **合同工具**：`static_style_schema.py`、`glow_style_fixtures.py`、模板与预设的 schema／fixture、`validate.py` 历史计数；`freeze.py` 指纹清单随新增或改名的源文件更新。

**SDK**：表面见 §9。SDK 2.0 在 M13 冻结前修改，不另升版本。仓库内的样例包（`backend/samples/`）、夹具与分析器白名单同步改写。

**前端**：`protocol/style.ts`、`protocol/templates.ts`、`editor/model/style-overrides.ts`、`style-sources.ts`、`full-template.ts`、`editor/library/TemplateEditor.tsx`、`MaterialTab.tsx`、`draft-validation.ts`（路线旧稿的改动面清单），以及前导／文字标记外观编辑。

### 10.6 装饰外扩与 64 px 上限

现状：
- `DecorationExtent` = max(min(描边 + 阴影(偏移 + σ) + 2, 64), 辉光(扩张 + 3σ) + 2)，用于排版留白与元素框（TargetColor 捕获的互补裁剪）；
- 区域与原始覆盖捕获另有一套不设上限的外扩：O7 的 `RegionDecorationExtent`，以及 `CapturePadding` 的非 TargetColor 分支。

64 px 上限是既有约定：「TargetColor 保留……旧 64 px 矩形限制；新 raw 外扩不截 64 px」，见 `backend/docs/archive/decisions/decisions.md`。

定稿：
1. **累计描边宽度** S = 各层宽度之和，两态取大，注音按 `RelativeScale` 缩放后计。所有用到「描边宽度」的地方改用 S；单层时 S 就是原宽度。
2. **旧式部分保留上限**：min(S + 阴影 + 2, 64)，用于排版留白与元素框。现有内容逐字节不变。
3. **辉光项**：D-P3-9 之后辉光以整体轮廓为来源，元素框改用 S + 扩张 + 3σ + 2，否则元素覆盖的辉光会被元素框截掉；这一变化属于 D-P3-9 已接受的观感变化。
   排版留白（`SceneDecorationPadding`）保持旧的辉光项（扩张 + 3σ + 2，不加 S），已有工程的行距不变。代价是带描边的辉光在多行堆叠时可能多压到相邻行 S 像素，与现在阴影超出留白的情况同类。
4. **区域与原始捕获外扩不设上限，按整体轮廓计**：
   - 轮廓外扩 = 各层累计宽度 × 该层拐角系数（斜接 × 斜接上限，其余 × 1）的最大值；
   - 外扩 = 轮廓外扩 + max(阴影偏移 + 3σ, 辉光扩张 + 3σ, 0)；
   - 两态取大。

   带描边加辉光的对象，区域因此变大。CPU 像素不变（多出的范围里本来没有像素），Vulkan 模糊随表面变化，在容差内。
5. **已知限制**（照旧，ADR 已记）：旧式部分超过 64 px 时，元素框外圈仍由整行那一遍按行样式绘制，元素覆盖在超出部分不生效。

## 11. 开发方向（新会话按此顺序）

**已确认会改变现有观感的地方**（验收时不能当回归）：整行渐变的参考框（D-P3-8）；带描边时的阴影与辉光（D-P3-9）；带装饰的注音（D-P3-10）。
除此之外，不使用新能力的内容要求 CPU 像素逐字节不变、Vulkan 在既有容差内、表面数与耗时不变。测试包 18 组基线若用到上述三处，对应步骤后重取基准并写明原因。

每步一个或多个 PATCH，单独验收；像素回归覆盖直绘与图场景两条路径，新回归做变异验证。工作约定见路线 §10。

| 步 | 内容 | 要点 |
|---|---|---|
| 0 | **前置修复（0.112.73）**，已完成 | 路线 §2 第 12 条：独立对象路径的工作框并入声明的取样偏移。与 P3 设计无关，先做。实际只对声明了下层／背景输入的图与材质并入（两条路径一致），样例包中只有 GlassInk 受影响 |
| 1 | **定稿细节（只写文档）**，已完成 | §10 第 1、2、5、6 条已写入本文（§10.1、§10.2、§10.5、§10.6）；路线 §6 已改写为指向本文的摘要 |
| 2 | **Core 模型与格式** | 三层结构；两态外观与「跟随未唱」；描边列表；Fill（重合色标、按单字坐标、删进度坐标）；扫字样式；属性清单按 §4 的表重排；覆盖、解析、预设、模板；格式升版并拒绝旧版 |
| 3 | **渲染：外观分态** | 每遍读各自一态；辉光改每态一次；外扩取两态最大；描边材质槽拆两态；判空按两态 |
| 4 | **渲染：Fill、多重描边、注音** | 硬边取样；按单字着色器；参考框改字体度量；多重描边与阴影／辉光来源、层序（§6）；注音装饰缩放与参考框（§7） |
| 5 | **渲染：羽化** | 先直绘，再图场景与包材质；羽化为 0 的逐字节回归；Vulkan 三块区域互补 |
| 6 | **高亮与 SDK** | 扫字线高亮（作用范围与混合方式按 §10.3 出样张请用户选）；§9 的 SDK 表面与 B 第 2 层；宿主、分析器、样例、夹具、能力台账 |
| 7 | **合同与前端** | 合同次版本；前端协议、样式与模板编辑器（两态外观与跟随、分层编辑、坐标选项、多层描边、扫字样式）；缺背景的编辑时提示；导出诊断放开（路线 §2 第 11 条、§6.3） |
| 8 | **复测** | 测试包基线加分态、分层、多重描边、羽化几组（纯色、棋盘格）；其余组与 P1 基线（M12 记录 §6.3）比较 |

之后是 P4 文字优化（开工前与用户确认范围）、M13、M14，见路线 §7、§8。
