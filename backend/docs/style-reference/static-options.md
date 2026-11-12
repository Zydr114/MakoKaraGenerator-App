# 静态样式选项参考

本文原以 App 提交 `f3fdfdf`、后端 `0.109.0` 为基线；辉光按后续 `0.110.0` 工作树源码补充。它是源码核对结果，未执行构建、HTTP 或像素验收。场景位置和动画查[动画、场景与预设](animation-scene-presets.md)，运行时输出查[脚本 SDK](script-sdk.md)，请求封装查[HTTP 编辑与作者入口](http-authoring.md)。

> 0.112.74 起 Core 样式改为未唱／已唱两态外观、多层描边（无 position）与扫字样式，渐变删去 `progress` 坐标，属性来源由 26 项增至 43 项，工程／歌词／模板 schema 为 31／19／9。本文关于 Core 的单层描边、`StrokeSpecOverride` 与来源表的描述已过时，HTTP 部分仍是过渡期的现行视图（见[行为合同 §6.11](../architecture/backend-api.md#611-四层静态样式读写0830110-新增外辉光)）；本文随 P3 第 7 步合同改版整体改写。

必须区分三层能力：**Core 可表达**表示领域模型能保存该值；**HTTP 可编辑**表示当前 `styles.patch`/`styles.restore` 接受该意图；**渲染支持**表示实际消费者实现该语义。前两项成立不自动意味着第三项成立，渐变的 `glyph`、`progress`、`oklch` 就是现成反例。

## 1. 对象、作用域与继承

### 1.1 静态对象

`LyricStyle` 有 10 个规范顶层成员；其平面属性是直接访问嵌套对象的 C# 外观，没有第二份独立值。

| Core 成员 / HTTP canonical 字段 | 默认值 | 内容及边界 |
| --- | --- | --- |
| `Typography` / `typography` | `TypographySpec.Default` | 7 个排版成员，见下一节 |
| `UnsungFill` / `unsungFill` | solid `#4A5060FF` | 未唱材质；HTTP canonical 是 `{material: ...}` |
| `SungFill` / `sungFill` | solid `#4C8DFFFF` | 已唱材质；只有未唱/已唱两态，没有第三个“当前字颜色”字段 |
| `Stroke` / `stroke` | `null` | 整体描边为空表示关闭 |
| `Shadow` / `shadow` | `null` | 整体阴影为空表示关闭 |
| `Glow` / `glow` | `null` | 整体辉光为空表示关闭 |
| `VerticalCenterRatio` / `verticalCenterRatio` | `0.5` | 有限数，`[0,1]`；默认排版纵向中心 |
| `HorizontalAlignment` / `horizontalAlignment` | `center` | `left / center / right` |
| `Opacity` / `opacity` | `1` | 有限数，`[0,1]`；绘制透明度 |
| `Scale` / `scale` | `1` | 正的有限数，无模型上限；基础几何缩放 |

`LyricStyleOverride` 对应同一组规范成员：`Typography` 为可空部分对象，填充/描边/阴影/辉光使用 `OptionalValue<T?>`，另外四项为 nullable 数值或枚举。空覆盖归一化为 `null`；覆盖仅修改本层明确设置的成员。

源码：[LyricStyle](../../src/MakoKaraGenerator.Core/Projects/LyricStyle.cs)、[LyricStyleOverride](../../src/MakoKaraGenerator.Core/Projects/LyricStyleOverride.cs)、[LyricStyleEditor](../../src/MakoKaraGenerator.Core/Projects/LyricStyleEditor.cs)、[HTTP canonical DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendStyleContracts.cs)。

### 1.2 作用域与顺序

| 目标 | 本层数据 | 静态 HTTP 目标形状 | 可写范围 |
| --- | --- | --- | --- |
| 工程 | `Project.LyricStyle` 完整基线 | `{kind:"project"}` | 全部 29 个规范编辑成员；不能 restore |
| 分支 | `LyricBranch.StyleOverride` | `{kind:"branch",branchId}` | 全部 29 个规范编辑成员 |
| 行 | `LyricLine.StyleOverride` | `{kind:"line",branchId,lineId}` | 全部 29 个规范编辑成员 |
| token | `LyricToken.StyleOverride` | `{kind:"token",branchId,lineId,tokenId}` | 颜色/材质、描边、阴影、辉光、透明度；禁止排版和基础缩放 |
| Ruby 注音组 | `LyricLine.RubyAnnotations[]` | 不属于 `StyleTargetDto` | 自身有比例/间距/对齐；没有独立 `LyricStyleOverride` |

`LyricStyleResolver.Resolve` 的顺序是：传入默认样式（省略才用 `LyricStyle.Default`）→ 可选旧式 `LyricStylePreset` **整份替换** → 分支覆盖 → 行覆盖 → token 覆盖。每层之后校验；`LyricStyleSource` 的枚举数值不代表应用次序。工程里的 `LyricStylePresetId` 是基线来源标识，`LyricStyleEditor.ApplyPreset` 将完整样式写入基线，手改基线会清除此 ID。

HTTP 静态读取只执行**工程基线 → 分支 → 行 → token**，返回 `baseline`、本层 `local`、`staticHierarchy`、`hasPresetApplications`，`resolution` 固定为 `staticHierarchy`。它不解析实际节点/clip/preset 应用，不能当成某一帧的最终外观。

实际持久预设应用由 `PresetStyleResolver` 另行交织，歌词顺序为：工程基线 → 工程预设应用 → 分支本地覆盖 → 分支预设应用 → 实际节点根到当前对象路径上的预设应用 → 当前 clip 应用 → 行本地覆盖 → 行应用 → token 本地覆盖 → token 应用。节点祖先应用还受 `includeDescendants` 控制；不可见源没有真实装载节点/clip 时不猜测未来槽位。脚本在准备帧中再处理运行时结果。

源码：[基础解析器](../../src/MakoKaraGenerator.Core/Projects/LyricStyleResolver.cs)、[预设来源解析器](../../src/MakoKaraGenerator.Core/Projects/PresetStyleResolver.cs)、[实际场景投影](../../src/MakoKaraGenerator.Rendering.Abstractions/PresetSceneStyleProjection.cs)、[静态 HTTP 读取](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.StyleReadModels.cs)。

## 2. 排版 TypographySpec

| Core 字段 / HTTP 编辑属性 | 默认值 | 模型规则、清除语义 | 实际渲染 / token |
| --- | --- | --- | --- |
| `FontFamilyName` / `typography.fontFamilyName` | `null` | `string?`；Core 允许空串/空白并保持原值；显式 `null` 选后端默认字体；恢复继承必须 restore | 字体族由后端字体系统解析，不能保证任意机器有同一字体；token 禁止 |
| `FallbackFamilyNames` / `typography.fallbackFamilyNames` | `[]` | 有序、非 default 的不可变数组；每项非空白，Ordinal 不重复；`[]` 清空用户链，使用系统回退；restore 继承上层链 | 字体缺字时按链尝试后再走后端回退；token 禁止 |
| `FontSizePx` / `typography.fontSizePx` | `72` | 正的有限 float，单位画布像素，无模型上限 | 整行整形字号；token 禁止 |
| `FontWeight` / `typography.fontWeight` | `400` | 整数 `[100,900]`，不要求 100 的倍数 | 请求字重；实际字形依赖字体；token 禁止 |
| `IsBold` / `typography.isBold` | `false` | bool；`false` 是明确覆盖 | 为真时请求字重 `max(700,fontWeight)`；token 禁止 |
| `IsItalic` / `typography.isItalic` | `false` | bool；`false` 是明确覆盖 | 请求 Italic 字形；token 禁止 |
| `LetterSpacingPx` / `typography.letterSpacingPx` | `0` | 任意有限 float，可为负，无模型上限 | 在共享整形布局中调整文本元素间距；token 禁止 |

`TypographySpecOverride` 只用 `FontFamilyName` 的 `OptionalValue<string?>` 区分“未设/显式 null/字符串”；其余 nullable 字段的 `null` 表示继承，回退链的空数组表示明确清空。没有单独的行高、字宽、字体轴、文本方向、自动换行、下划线或删除线静态字段。

整行及 Ruby 均通过 `LayoutOptionsFor` 使用这些字段；整形结果用于高亮、token 分区与绘制，不通过重新整形子串实现逐字样式。字号单位是设计画布像素，预览尺寸缩放由管线处理。

源码：[TypographySpec/Override](../../src/MakoKaraGenerator.Core/Projects/TypographySpec.cs)、[SkiaTextLayoutEngine](../../src/MakoKaraGenerator.Rendering.Skia/SkiaTextLayoutEngine.cs)、[布局参数投影](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Geometry.cs)、[文本布局合同](../../src/MakoKaraGenerator.Text/ITextLayoutEngine.cs)。

## 3. 填充、颜色与材质

`FillSpec` 只有一个真实字段 `Material`。`Color` 与 `Gradient` 是 C# 访问外观，不是可以叠乘的两份材质；`FillSpec` 裸构造默认透明 solid，歌词的未唱/已唱默认值才是上表两色。

| MaterialSpec 类型 / HTTP `kind` | 完整数据 | Core 可表达 | 静态 HTTP 编辑 | 渲染边界 |
| --- | --- | --- | --- | --- |
| `Solid` / `solid` | `color` | 是 | 是，整体替换材质 | RGBA 纯色 |
| `Gradient` / `gradient` | `gradient` 完整描述 | 是 | 是，整体替换材质 | 只支持下节矩阵；接受输入不代表可绘制 |
| `PackageMaterial` / `package` | `packageId`、固定 `revision`（`sha256:` 加 64 位小写十六进制）、`exportId`、`parameters` | 是；构造器检查非空身份、合法包修订格式；真正资源校验另行完成 | `styles.patch` 禁止新写入；读取只给身份，不给参数。完整模板资源路径另有协议 | 需要会话前固定包资源、完整源引用与参数、实际材质协调器；不能把包伪装为纯色，缺资源报错 |

HTTP 纯色示例为 `{"kind":"solid","color":{"red":255,"green":128,"blue":0,"alpha":255}}`。四通道均是整数 byte `[0,255]`，`alpha=0` 透明，`255` 不透明；HTTP 样式命令不使用十六进制颜色字符串。Core `RgbaColor.Parse` 可接受 `#RRGGBB`/`#RRGGBBAA`（也可无 `#`），`ToHex` 固定输出含 alpha 的 `#RRGGBBAA`。工程持久化 DTO、HTTP canonical DTO、C# 访问外观不能互换：例如持久化的 `unsungFill` 直接写材质判别对象，HTTP canonical 的 `unsungFill` 则包含 `material`；持久化包材质判别名是 `packageMaterial`，HTTP 是 `package`；持久化描边把宽度/连接/位置放在 `geometry` 对象，HTTP 完整描边则平铺这三个成员。

静态访问外观的行为：

- `Material.RepresentativeColor`：solid 返回本色，gradient 返回首色标；package 抛 `SourceMaterialColorUnavailable`。
- 完整 `FillSpec.Color` 或 `StrokeSpec.Color` 写入：solid 改本色，gradient 改首色标，不删除渐变；package 抛 `SourceMaterialReplacementRequired`。
- 完整对象 `Gradient = null`：用当前首色标建立 solid；package 不允许该兼容编辑。不存在“关闭渐变并恢复之前另存的基色”。
- `FillSpecOverride.Color`/`StrokeSpecOverride.Color` 写入是**完整 solid 覆盖**，与完整对象的 `Color` setter 不同；覆盖中的 `Gradient=null` 若没有明确当前材质基准会抛异常。HTTP 应直接提交完整 solid 以关闭渐变。
- 填充对象和材质的显式 `null` 均非法；恢复继承需要删除本层成员。描边的材质不能 null，但整个描边可以 null。

源码：[FillSpec](../../src/MakoKaraGenerator.Core/Projects/FillSpec.cs)、[MaterialSpec](../../src/MakoKaraGenerator.Core/Projects/MaterialSpec.cs)、[RgbaColor](../../src/MakoKaraGenerator.Core/Projects/RgbaColor.cs)、[材质持久化](../../src/MakoKaraGenerator.Core/Persistence/ProjectDtoMapper.Materials.cs)、[HTTP converter](../../src/MakoKaraGenerator.Backend.Contracts/StyleCommandJsonConverters.cs)、[固定源材质](../../src/MakoKaraGenerator.Rendering.Abstractions/PreparedSourceMaterials.cs)、[绘制投影](../../src/MakoKaraGenerator.Rendering.Abstractions/StaticStyleProjection.cs)。

## 4. 渐变 GradientSpec

下面列出完整的 10 个渐变字段；HTTP `gradient` 对象必须完整提供全部字段，省略“该类型不使用的字段”也会被 converter 拒绝。

| 字段 | Core 默认值 | Core 有效范围/枚举 | 当前渲染 |
| --- | --- | --- | --- |
| `kind` | `linear` | `linear / radial` | 均实现 |
| `angleDegrees` | `0` | 任意有限 double，不要求 `[0,360]` | 线性方向，0° 向右、90° 向下；径向不用于距离计算 |
| `centerX` | `0.5` | 有限数；radial 时 `[0,1]` | 径向中心横坐标；linear 忽略 |
| `centerY` | `0.5` | 同上 | 径向中心纵坐标；linear 忽略 |
| `radiusX` | `0.5` | 有限数；radial 时 `>0`，没有 `<=1` 限制 | 径向横向归一化半径，可形成椭圆；linear 忽略 |
| `radiusY` | `0.5` | 同上 | 径向纵向归一化半径；linear 忽略 |
| `coordinateSpace` | `line` | `canvas / line / glyph / progress` | 仅 `canvas / line` 实现；token 的 line 渐变仍依据共享整行布局，不自动改为 token 边框 |
| `colorSpace` | `linearRgb` | `linearRgb / hsv / oklch` | 仅 `linearRgb / hsv` 实现 |
| `huePath` | `shorter` | `shorter / longer / increasing / decreasing` | HSV 插值使用；linearRgb 不用色相路径 |
| `stops` | `[]`，裸默认描述无效 | 至少 2 个；首 offset 必须恰为 `0`、末为 `1`；按 offset 非递减，允许相同位置；每个 offset 有限且 `[0,1]`，每个 color 为 RGBA | 核心取色再采样为 65 个 shader 色标，画布之外 clamp 到端色 |

Core 的线性渐变只要求 center/radius 有限，即使中心超出 `[0,1]` 或半径负数仍可表达，因为线性不使用它们；不要给这类未使用字段臆造统一限制。`GradientSpec.IsValid` 与 `GradientSupport.IsSupported` 是两种判断。

| 能力组合 | Core / HTTP 结构 | 工程校验与渲染 |
| --- | --- | --- |
| linear 或 radial × canvas 或 line × linearRgb 或 hsv | 可表达、可写 | 支持 |
| glyph / progress 坐标 | 可表达、可写 | `GradientSupport` 不支持；`ProjectValidator` 报 Error |
| oklch 插值 | 可表达、可写 | `GradientSupport` 不支持；`ProjectValidator` 报 Error；核心采样器不实现 |
| 未知枚举、非有限数、无效 stops、无效径向参数 | 无效 | HTTP/领域拒绝，不排序或修补 |

这里“可写”有意保留实际入口差异：`styles.patch` 检查完整结构与 `IsValid`，准备阶段执行 `PresentationValidator`，没有调用完整 `ProjectValidator` 的渐变支持门禁；现有后端测试明确保留了 `progress + oklch` 的读写往返。因此不能声称它们会在 HTTP 输入时统一被拒绝。

实际降级/失败也须按路径区分：普通填充 shader 创建失败或不支持时可以按首色标纯色绘制；描边渐变遇到不支持组合会抛 `UnsupportedEffectGradient`，shader 无法建立抛 `GradientShaderUnavailable`；脚本样式投影与包材质 fallback 路径要求能力矩阵成立，不能静默改色。涉及实际径向尺寸的受检路径还要求 native float 可表示、像素半径不小于 `1e-4`，有限的 Core double 仍不保证可在 Skia 绘制。

源码：[GradientSpec](../../src/MakoKaraGenerator.Core/Projects/GradientSpec.cs)、[能力矩阵](../../src/MakoKaraGenerator.Core/Projects/GradientSupport.cs)、[取色实现](../../src/MakoKaraGenerator.Core/Projects/GradientSampler.cs)、[工程校验](../../src/MakoKaraGenerator.Core/Validation/ProjectValidator.cs)、[HTTP 编辑准备](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.Values.cs)、[HTTP 往返回归源码](../../tests/MakoKaraGenerator.Backend.Hosting.Tests/BackendStyleCommandsTests.cs)、[Skia shader](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs)、[绘制与 native 检查](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.TextPainting.cs)、[包 fallback 检查](../../src/MakoKaraGenerator.Rendering.Skia/PackageMaterialValidator.cs)。

## 5. 描边、阴影与辉光

### 5.1 StrokeSpec 的 4 个规范字段

`LyricStyle.Stroke` 默认 null；下表是**建立一个 StrokeSpec 对象**的构造默认值，不能当成歌词默认开启描边。

| 字段 / HTTP 属性 | 对象默认值 | 范围和语义 | token |
| --- | --- | --- | --- |
| `Material` / `stroke.material` | solid 白色 `#FFFFFFFF` | 同材质联合；静态编辑只写 solid/gradient；非 null | 支持 |
| `WidthPx` / `stroke.widthPx` | `2` | 非负有限 float，画布像素；`0` 关闭可见描边，保留对象 | 支持 |
| `Join` / `stroke.join` | `round` | `miter / round / bevel` | 支持 |
| `Position` / `stroke.position` | `outside` | 唯一值 `outside`；没有 inside/center 选项 | 支持 |

`StrokeSpec.IsEnabled` 要求有效、宽度 `>0`、材质不是 alpha=0 的 solid。实际外描边先画 `widthPx*2` 轮廓，再减去不透明字形遮罩，使填充透明时也不会露出内半边描边。渐变不叠乘另存的纯色 alpha；整个效果仍受样式 opacity 影响。没有可编辑 miter limit、虚线或端帽字段。

`StrokeSpecOverride` 可独立覆盖 material/width/join/position；`IsComplete` 要求四项齐全。上层没有描边时，部分覆盖用透明 solid、宽 `0`、round、outside 为合并起点，而不是采用 `new StrokeSpec()` 的白色宽 2。因此只设置宽度通常不会自动得到可见描边，需明确材质与宽度。

### 5.2 ShadowSpec 的 5 个规范字段

`LyricStyle.Shadow` 默认 null；下面同样是显式建立阴影对象的默认值。

| 字段 / HTTP 属性 | 对象默认值 | 范围和语义 | token |
| --- | --- | --- | --- |
| `Color` / `shadow.color` | `#000000A0` | RGBA；不支持渐变或 package 阴影材质 | 支持 |
| `Opacity` / `shadow.opacity` | `1` | 有限 float `[0,1]`；独立于整体 opacity 和 color alpha | 支持 |
| `AngleDegrees` / `shadow.angleDegrees` | `45` | 任意有限 double；0° 向右，90° 向下 | 支持 |
| `DistancePx` / `shadow.distancePx` | `4` | 非负有限 float；画布像素 | 支持 |
| `BlurRadiusPx` / `shadow.blurRadiusPx` | `8` | 非负有限 float；画布像素 | 支持 |

`OffsetXPx=cos(angle)*distance`、`OffsetYPx=sin(angle)*distance` 是只读派生值，不是另两个编辑选项。`ShadowSpec.IsEnabled` 要求有效且 opacity、blurRadius、color alpha 都 `>0`：**blur=0 的硬边阴影当前不画**。实际阴影 alpha 由 `color.alpha × shadow.opacity × style.opacity` 形成，并再受场景/动画外层合成影响。Skia 使用 DropShadow；没有独立 spread、内阴影、多层阴影或混合模式静态字段。

`ShadowSpecOverride` 5 个 nullable 叶字段均可单独覆盖；全齐才 `IsComplete`。上层无阴影时合并起点为透明色、opacity 1、angle 0、distance 0、blur 0。只改颜色也不会自动产生模糊阴影。

源码：[StrokeSpec](../../src/MakoKaraGenerator.Core/Projects/StrokeSpec.cs)、[ShadowSpec](../../src/MakoKaraGenerator.Core/Projects/ShadowSpec.cs)、[部分覆盖及合并默认](../../src/MakoKaraGenerator.Core/Projects/EffectSpecOverride.cs)、[实际绘制](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.TextPainting.cs)。

### 5.3 “关闭”“清除”“恢复继承”不是同一操作

| 意图 | Core 覆盖表达 | HTTP 表达 | 结果 |
| --- | --- | --- | --- |
| 不设置本层成员 | `OptionalValue` 未设 / nullable 叶为 null | 不包含该 change | 不改当前源覆盖；并非删除已有覆盖 |
| 恢复某个叶字段继承 | 移除该叶字段；空部分对象归一化 | `styles.restore` 的 `properties` | 仅移除本层该字段，保留其他叶字段 |
| 明确关闭描边/阴影 | `Stroke/Shadow = Set(null)` | patch `stroke/shadow` 的 `value:null` | 屏蔽上层效果 |
| 恢复整个效果继承 | `Stroke/Shadow` 未设 | restore `stroke/shadow` | 删除本层整个效果覆盖 |
| 不指定主字体 | `FontFamilyName=Set(null)` | patch `typography.fontFamilyName` 为 null | 明确选后端默认字体，不继承上层字体 |
| 清空回退链 | `FallbackFamilyNames=[]` | patch 空数组 | 明确回到系统回退，不继承上层用户链 |
| 关闭渐变 | 明确 solid 材质 | patch `unsungFill.material` 等为 solid | 固定新纯色，不是 restore |
| 完全透明 | color alpha=0 或 opacity=0 | 正常 patch 数值 | 保留本层对象与覆盖身份 |

对显式 `stroke:null` 再 restore `stroke.widthPx` 不会删除关闭状态；需要 restore `stroke`。同理适用于 shadow。对关闭状态再 patch 某个叶成员会新建部分对象，随后与上层效果合并，不能理解为关闭对象内存着一套旧参数。

Core 还有 `LyricStyleEffects` 四种便捷开关：`UnsungGradient / SungGradient / Stroke / Shadow`。这些不是额外 HTTP 字段。启用渐变会以当前首色和各 RGB 通道加 48（clamp 255）的颜色建立两色标；启用描边在宽度不足时补 2；启用阴影无偏移时以 `(4,4)` 得到距离约 `5.657`，并补 blur 8。便捷 Enable 的初始值与 `new ShadowSpec()` 的 distance 4 不完全相同；Disable 清除效果，渐变关闭则转为首色标 solid。

源码：[应用层 set/restore](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.Values.cs)、[规范覆盖](../../src/MakoKaraGenerator.Core/Projects/LyricStyleOverride.cs)、[效果开关](../../src/MakoKaraGenerator.Core/Projects/LyricStyleEffects.cs)。

### 5.4 静态辉光

完整 `GlowSpec` 默认关闭（`null`）；新建对象的默认字段如下。完整 HTTP 对象必须提供全部字段，颜色使用 `{red,green,blue,alpha}` 字节对象；持久模型及绘制细节见[辉光参考](glow.md)。

| Core 字段 / HTTP 属性 | 对象默认值 | 约束 |
| --- | --- | --- |
| `Color` / `glow.color` | 白色 `#FFFFFFFF` | RGBA |
| `Opacity` / `glow.opacity` | 1 | 有限数 [0,1] |
| `BlurRadiusPx` / `glow.blurRadiusPx` | 8 | 非负有限数，高斯 sigma，画布像素 |
| `SpreadPx` / `glow.spreadPx` | 0 | 非负有限数，圆角轮廓向外扩张的画布像素 |

四层均支持辉光，包括 token。完整 `glow:null` 明确关闭；`styles.restore` 恢复本层完整对象或指定叶字段的继承。上层无辉光时部分覆盖从透明色、opacity 1、零 sigma 和零外扩合并；这不同于新建完整对象的默认值。颜色 alpha、opacity 任一为零或模糊和外扩均为零时不绘制。

## 6. LyricStyleProperty 全量清单（26 项）

此枚举用于属性来源、模板选择和 token 能力判断，**不是** HTTP 的 29 项编辑字段枚举。填充现已整体是材质，仍保留颜色/渐变名称用于兼容的属性选择与来源追踪。

| Core 枚举 / JSON 名称 | 指向或含义 | token | HTTP 对应编辑入口 |
| --- | --- | --- | --- |
| `FontFamilyName` / `fontFamilyName` | typography 主字体 | 禁止 | `typography.fontFamilyName` |
| `FallbackFamilyNames` / `fallbackFamilyNames` | typography 回退链 | 禁止 | `typography.fallbackFamilyNames` |
| `FontSizePx` / `fontSizePx` | typography 字号 | 禁止 | `typography.fontSizePx` |
| `FontWeight` / `fontWeight` | typography 字重 | 禁止 | `typography.fontWeight` |
| `IsBold` / `isBold` | typography 粗体 | 禁止 | `typography.isBold` |
| `IsItalic` / `isItalic` | typography 斜体 | 禁止 | `typography.isItalic` |
| `LetterSpacingPx` / `letterSpacingPx` | typography 字距 | 禁止 | `typography.letterSpacingPx` |
| `VerticalCenterRatio` / `verticalCenterRatio` | 默认纵向中心 | 禁止 | `verticalCenterRatio` |
| `HorizontalAlignment` / `horizontalAlignment` | 默认水平对齐 | 禁止 | `horizontalAlignment` |
| `UnsungColor` / `unsungColor` | 未唱材质兼容颜色访问/来源 | 支持 | `unsungFill.material` |
| `SungColor` / `sungColor` | 已唱材质兼容颜色访问/来源 | 支持 | `sungFill.material` |
| `UnsungGradientSpec` / `unsungGradientSpec` | 未唱材质兼容渐变访问/来源 | 支持 | `unsungFill.material` |
| `SungGradientSpec` / `sungGradientSpec` | 已唱材质兼容渐变访问/来源 | 支持 | `sungFill.material` |
| `StrokeColor` / `strokeColor` | 描边材质兼容颜色访问/来源 | 支持 | `stroke.material` |
| `StrokeWidthPx` / `strokeWidthPx` | 描边宽度 | 支持 | `stroke.widthPx` |
| `StrokeSpec` / `strokeSpec` | 整体描边以及 join/position 来源 | 支持 | `stroke`、`stroke.join`、`stroke.position` |
| `ShadowColor` / `shadowColor` | 阴影颜色 | 支持 | `shadow.color` |
| `ShadowBlurPx` / `shadowBlurPx` | 阴影模糊 | 支持 | `shadow.blurRadiusPx` |
| `ShadowSpec` / `shadowSpec` | 整体阴影及方向/距离等 | 支持 | `shadow`、`shadow.opacity`、`shadow.angleDegrees`、`shadow.distancePx` |
| `GlowSpec` / `glowSpec` | 整体辉光及关闭状态 | 支持 | `glow` |
| `GlowColor` / `glowColor` | 辉光颜色 | 支持 | `glow.color` |
| `GlowOpacity` / `glowOpacity` | 辉光透明度 | 支持 | `glow.opacity` |
| `GlowBlurRadiusPx` / `glowBlurRadiusPx` | 辉光模糊 | 支持 | `glow.blurRadiusPx` |
| `GlowSpreadPx` / `glowSpreadPx` | 辉光外扩 | 支持 | `glow.spreadPx` |
| `Opacity` / `opacity` | 整体绘制透明度 | 支持 | `opacity` |
| `Scale` / `scale` | 基础几何缩放 | 禁止 | `scale` |

共 **10 项 token 禁止、16 项 token 支持**。HTTP 对应为 29 个规范成员中 **19 个可用于 token、10 个禁止**。不能把 `TokenStyleSupport.IsSupported` 当作未知枚举合法性检查：调用者还须先验证枚举已定义。

来源表也有表达粒度限制：写入完整填充材质时会同时标记 color 和 gradient 来源；描边 join/position 共用 `StrokeSpec`；阴影没有独立的 opacity 来源键，当前 `MarkShadowSources` 对仅 `Opacity` 的部分覆盖不单独更新来源键。因此来源枚举不是全部 canonical 叶字段的无损审计表，HTTP `local` 保留叶意图更适合编辑。

模板中的 `Properties` 使用本节 26 项名称；模板种类、属性范围和预设应用入口详见[动画、场景与预设](animation-scene-presets.md)。不要用本节 JSON 名称代替下一节 HTTP canonical 属性路径。

源码：[全部属性和 JSON 名称](../../src/MakoKaraGenerator.Core/Projects/LyricStyleProperty.cs)、[TokenStyleSupport](../../src/MakoKaraGenerator.Core/Projects/TokenStyleSupport.cs)、[HTTP 到 Core 映射](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.Values.cs)、[来源标记](../../src/MakoKaraGenerator.Core/Projects/LyricStyleResolver.cs)、[模板拷贝语义](../../src/MakoKaraGenerator.Core/Projects/StyleBatchEditor.cs)。

## 7. HTTP 静态编辑清单（29 项）

每项均采用 `{property:"准确名称",value:...}`；并非向 `LyricStyle` 发任意 JSON Merge Patch。以下列出所有入口，详细值范围见前表。

| 属性 | value 类型 | null 是否合法 | token |
| --- | --- | --- | --- |
| `typography.fontFamilyName` | string/null | 合法，后端默认字体 | 否 |
| `typography.fallbackFamilyNames` | string[] | 否 | 否 |
| `typography.fontSizePx` | float | 否 | 否 |
| `typography.fontWeight` | int32 | 否 | 否 |
| `typography.isBold` | bool | 否 | 否 |
| `typography.isItalic` | bool | 否 | 否 |
| `typography.letterSpacingPx` | float | 否 | 否 |
| `unsungFill.material` | 完整 solid/gradient 联合 | 否 | 是 |
| `sungFill.material` | 完整 solid/gradient 联合 | 否 | 是 |
| `stroke` | 完整 Stroke 或 null | 合法，关闭 | 是 |
| `stroke.material` | 完整 solid/gradient 联合 | 否 | 是 |
| `stroke.widthPx` | float | 否 | 是 |
| `stroke.join` | `miter/round/bevel` | 否 | 是 |
| `stroke.position` | `outside` | 否 | 是 |
| `shadow` | 完整 Shadow 或 null | 合法，关闭 | 是 |
| `shadow.color` | 完整 RGBA | 否 | 是 |
| `shadow.opacity` | float | 否 | 是 |
| `shadow.angleDegrees` | double | 否 | 是 |
| `shadow.distancePx` | float | 否 | 是 |
| `shadow.blurRadiusPx` | float | 否 | 是 |
| `glow` | 完整 Glow 或 null | 合法，关闭 | 是 |
| `glow.color` | 完整 RGBA | 否 | 是 |
| `glow.opacity` | float | 否 | 是 |
| `glow.blurRadiusPx` | float | 否 | 是 |
| `glow.spreadPx` | float | 否 | 是 |
| `verticalCenterRatio` | double | 否 | 否 |
| `horizontalAlignment` | `left/center/right` | 否 | 否 |
| `opacity` | float | 否 | 是 |
| `scale` | float | 否 | 否 |

`styles.patch` 需 `type/targets/changes`；`styles.restore` 需 `type/targets/properties`，后者的 properties 使用上表相同字符串。所有目标必须同层、身份完整且不重复；工程目标只允许一个。属性不能重复，也不能在同批同时写 `stroke` 与 `stroke.*`、`shadow` 与 `shadow.*` 或 `glow` 与 `glow.*`。字段名、枚举准确 camelCase，拒绝大小写变体、整数枚举、未知/重复/缺失字段，`value` 即使为 null 也必须显式存在。

当前新输入预算：目标 `1..1000`；成员 `1..29`；完整命令意图最多 `64 KiB`；改变目标的新完整投影累计及新值展开最多 `16 MiB`；新主字体/各回退名称最长 `256` UTF-16 单元；新回退链最多 `32` 项；新渐变最多 `256` stops。这些是新编辑输入预算，不把旧工程值自动截断。HTTP 命令还经过已有修订/CAS、只读、关闭状态与完整回执预算门禁。

修改和恢复都先准备候选，任一目标失败整批不采纳。真实变化形成一个历史命令；同值、同内容数组等 no-op 不改变工程修订/历史和 redo。restore 只删除本层成员，不从解析结果复制完整样式，工程基线没有上层可恢复。

源码：[HTTP DTO/预算](../../src/MakoKaraGenerator.Backend.Contracts/BackendStyleContracts.cs)、[严格 converter](../../src/MakoKaraGenerator.Backend.Contracts/StyleCommandJsonConverters.cs)、[Application 原子编辑](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.cs)、[值与 restore](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.Values.cs)、[Hosting 提交与回执预算](../../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.StyleCommands.cs)、[读取查询校验](../../src/MakoKaraGenerator.Backend.Server/ServerEndpoints.Style.cs)。

## 8. Ruby 注音

Ruby 是附着到基础 token 的独立组，不是第三套歌词样式。完整组有 **8 个**持久数据成员（`EffectiveSegments` 是派生视图，不是额外用户字段）。

| 字段 | 默认/要求 | 可表达和渲染语义 | 当前 HTTP 编辑边界 |
| --- | --- | --- | --- |
| `id` | 必填非空白 | 组稳定 ID | 供目标引用，不是 styles 属性 |
| `baseTokenId` | 必填非空白 | 关联本行基础 token；删除基础 token 清理脱离组 | 不能通过 styles 更换 |
| `text` | 必填非空 | 与 segments 文本拼接必须完全一致 | `lyrics.setRubyText` 可改；兼容 setter 会重置为一个无时间片段 |
| `segments` | 至少一个合法片段 | 按文本顺序整体整形，各片段用独立进度裁切；字段见下表 | 有专门片段增删、顺序、文本、计时命令 |
| `language` | `null` | 可存语言标签；当前 Ruby Skia 测量未将它作为语言整形选项传入 | 无对应 styles 修改属性 |
| `relativeScale` | `0.5` | 正的有限 double，无模型上限；Ruby 字号=`行字号×比例` | 读取有该值；没有对应 HTTP 静态样式编辑属性 |
| `gapPx` | `2` | 非负有限 double，无模型上限；相对基础行墨迹上沿的画布像素间距 | 同上 |
| `alignment` | `center` | `center/start/end`，依基础 token 边界对齐 | 同上 |

| RubyTextSegment 字段 | 默认/规则 |
| --- | --- |
| `id` | 必填非空白，整份歌词文档内唯一 |
| `text` | 非空；相邻片段拼接是组正文 |
| `time` | 可空，绝对微秒半开区间；允许独立于行/基础 token，不要求包含、相邻或无重叠 |
| `timingStatus` | `missing / imported / estimated / edited / confirmed / invalid`，默认 `missing`；missing 要求无 time，invalid 要求有无效区间，其他状态要求有效区间 |

渲染先用**行级 Typography**建立整组注音布局，只对字号乘 `relativeScale`；`start/end` 指基础 token 的左右边界，注音放在基础行上方。整行 token 拼接不等于 `line.Text` 时 Ruby 测量不生成布局；无效注音或找不到基础 token 也不画。相邻注音会通过 `RubySpacingSolver` 调整共享几何以处理重叠，不能假定永不影响基础字布局。

绘制颜色、渐变、描边、阴影、透明度跟随其基础 token 的生效绘制样式；Ruby 没有独立字体族/颜色/描边/阴影覆盖字段。分段仍共享整组整形；片段边界落进字素/共享字簇时归属于包含该簇起始代码单元的片段，不重新整形子串。独立计时影响 Ruby 自己的未唱/已唱裁切；无时间兼容片段沿原规则跟随基础 token。场景/脚本的 token family 包含其 Ruby，不能据此宣称 Ruby 有一套独立静态目标 API。

源码：[RubyAnnotation](../../src/MakoKaraGenerator.Core/Lyrics/RubyAnnotation.cs)、[RubyTextSegment](../../src/MakoKaraGenerator.Core/Lyrics/RubyTextSegment.cs)、[时间状态](../../src/MakoKaraGenerator.Core/Lyrics/TimingStatus.cs)、[片段编辑](../../src/MakoKaraGenerator.Core/Lyrics/RubySegmentEditor.cs)、[Skia Ruby 测量与绘制](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Ruby.cs)、[间距求解](../../src/MakoKaraGenerator.Text/RubySpacingSolver.cs)、[HTTP Ruby 读取 DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendProjectReadContracts.cs)、[组文本命令](../../src/MakoKaraGenerator.Backend.Contracts/BackendProjectContentCommandContracts.cs)、[片段命令](../../src/MakoKaraGenerator.Backend.Contracts/BackendRubySegmentContracts.cs)。

## 9. 使用时必须保留的渲染限制

- **位置字段是默认值。** `verticalCenterRatio/horizontalAlignment` 由 `LinePlacementEditor.ResolveDefault` 转成位置/锚点；实际场景容器的 placement/layout 可以提供自己的默认位置。看到 canonical 值改变，不代表已有明确场景位置必定移动。基础 scale 还会与场景 scale、本地变换相乘，并受可用宽度和 maxWidthRatio 约束。
- **token 不重排整行。** 可用静态字段是绘制属性。HTTP 会以 `UnsupportedProperty` 拒绝禁止项；Core 对象仍能构造这些覆盖，`ProjectValidator` 当前报告 `UnsupportedTokenStyleOverride` 是 Warning，消息说明绘制忽略整行排版项，不能把模型可构造说成 HTTP 可写。实际绘制保持整行布局；共享字簇的静态覆盖可以走区域裁切，准备帧中要独立改变共享字簇的外观/变换则明确报 `UnsupportedGlyphPartition`。
- **不把字体选择说成字体嵌入。** 后端请求族名、字重和字形，结果受字体资源、回退与缺字影响；字体族 API 的静态字符串本身不能证明渲染机器已安装字体。
- **有限数不等于无成本无限大。** Core 多数像素值没有业务上限；native float 转换、shader、实际径向尺寸、字形分区、资源闭包和表面预算仍可能拒绝合法结构。本文没有测定极值性能或承诺任意尺寸都成功。
- **透明度分层。** fill/stroke 颜色 alpha 与 style opacity 共同影响绘制，shadow 还有自身 opacity；场景/动画图层透明度独立存在。不能只用颜色 alpha 解释最后一帧 alpha。

源码：[默认排版转换](../../src/MakoKaraGenerator.Core/Projects/LinePlacementEditor.cs)、[实际几何](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Geometry.cs)、[静态 token/整行绘制](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs)、[字簇分区绘制](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.FamilyTransforms.cs)、[准备帧约束](../../src/MakoKaraGenerator.Rendering.Abstractions/PreparedRenderFrame.cs)、[完整校验器](../../src/MakoKaraGenerator.Core/Validation/ProjectValidator.cs)。

## 10. 回归源码核对索引

下表说明源码盘点时查阅的测试意图；原文档盘点未执行测试。后续辉光实施的实际回归和HTTP验收单列于[辉光参考](glow.md)，不将未执行的其他测试推定为通过。

| 测试源 | 用于交叉核对的规则 |
| --- | --- |
| [ProjectStyleEditValidationTests](../../tests/MakoKaraGenerator.Application.Tests/ProjectStyleEditValidationTests.cs) | 全部 29 成员、token 10 禁止/19 允许、非法值、重复色标 |
| [ProjectStyleEditUseCaseTests](../../tests/MakoKaraGenerator.Application.Tests/ProjectStyleEditUseCaseTests.cs) | 四层局部编辑、显式关闭、只恢复叶与恢复 whole 的差异、无上层效果的透明零宽合并、no-op |
| [BackendStyleCommandsTests](../../tests/MakoKaraGenerator.Backend.Hosting.Tests/BackendStyleCommandsTests.cs) | 空白字体合法、progress/oklch 完整往返、包身份只读和参数保留、历史 |
| [StyleCommandsTransportTests](../../tests/MakoKaraGenerator.Backend.Server.Tests/StyleCommandsTransportTests.cs) | 29 属性真实 wire 构造、strict JSON、准确 camelCase、输入预算 |
| [TokenStyleSupportTests](../../tests/MakoKaraGenerator.Core.Tests/Projects/TokenStyleSupportTests.cs) | 26 项枚举覆盖、允许与禁止互斥、绘制覆盖识别 |
| [LyricStyleResolverTests](../../tests/MakoKaraGenerator.Core.Tests/Projects/LyricStyleResolverTests.cs) | 完整预设替换、三态 null、属性来源和层级错误归因 |
| [GradientOpacityTests](../../tests/MakoKaraGenerator.Rendering.Tests/GradientOpacityTests.cs) | 渐变色标与样式透明度各应用一次，包含描边和 Ruby |
| [TimedRubyRenderingTests](../../tests/MakoKaraGenerator.Rendering.Tests/TimedRubyRenderingTests.cs) | 无时间回退、独立时间不延长显示、字簇内边界、时间变化保持聚合几何 |
| [RubySpacingRenderingTests](../../tests/MakoKaraGenerator.Rendering.Tests/RubySpacingRenderingTests.cs) | 宽 Ruby 间距、对齐与整体几何 |
