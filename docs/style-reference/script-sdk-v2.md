# 脚本 SDK 2.0 作者参考

源码核对：2026-11-13，产品 0.112.88，HTTP 合同 1.51.0。

本文是 SDK 2.0 的作者参考，覆盖以下内容：
- 包清单与两个纯函数入口；
- 文字输出（仿射、透明度、两态外观、扫字、Warp、图与材质）；
- 粒子与晚期视觉、包资源、媒体特征和源索引；
- 确定性与预算、作者工作流，以及样例索引。

**作者可见表面以快照为准。** 快照是 [sdk-v2-public-surface.txt](../../backend/tests/MakoKaraGenerator.Scripting.Tests/Snapshots/sdk-v2-public-surface.txt)，
范围是 `MakoKaraGenerator.Scripting.V2` 的公开类型，加上这些类型的公开签名能到达的公开类型。
本文与快照不一致时以快照为准。HTTP 行为的完整合同见 [后端 API](../../backend/docs/architecture/backend-api.md)
§21（作者）、[§26](../../backend/docs/architecture/backend-api.md#sdk2-m08-particles)（粒子）、
[§27](../../backend/docs/architecture/backend-api.md#sdk2-m09-svg-warp)（Warp 与 SVG）、
[§28](../../backend/docs/architecture/backend-api.md#sdk2-m09-graph-material-group)（图、材质与组）、
[§28.6](../../backend/docs/architecture/backend-api.md#sdk2-m10-media-features)（媒体特征）与
[§29](../../backend/docs/architecture/backend-api.md#preview-frame-inspection)（帧检查）。

SDK v1（[参考](../../backend/docs/style-reference/script-sdk.md)）在 M14 删除，新包只写 v2。

## 1. 包身份、设备与版本

| 项目 | SDK 2.0 |
| --- | --- |
| 包 profile | `packageVersion: 3`、`sdkVersion: "2.0.x"`、`shaderProfile: "skia-ganesh-3.119.4-v2"` |
| 版本准入 | 接受规范的 `2.0.x` 补丁号；`2.1.0` 及其他主、次版本或 shader profile 拒绝，不回退到 v1 |
| 作者入口 | 文字阶段 `IPureKaraokeEffect<TPrepared>`，晚期视觉阶段 `IPureVisualEffect<TPrepared>`（§3） |
| 无 DLL 包 | 只含图、shader、材质的包不写 C#，宿主直接执行（§8） |
| 执行后端 | 只支持 Vulkan。实际使用 v2 效果的工程在 CPU 上以 `PackageBackendUnsupported` 拒绝预览、验证与导出，不回落 |
| 与 v1 并存 | M14 之前 SDK `1.0.0` 包仍按旧路径执行；混合工程的成本见 §11 |

**设备准入**按实际启用的实例和正在使用的源材质判断。只把一个 v2 包放进工程目录，不影响无效果工程用 CPU；
禁用的实例也不算使用。预览、作者正式验证与导出都要经过后端准入，读取包成功不能代替设备可执行的证明。
包 profile、内部 shader 程序 profile、产品版本与 HTTP 合同版本是四个不同的身份。

**SDK 2.0 冻结。** 快照测试（[SdkV2PublicSurfaceSnapshotTests](../../backend/tests/MakoKaraGenerator.Scripting.Tests/SdkV2PublicSurfaceSnapshotTests.cs)）
逐项比对以下内容，任何变化都会让测试失败：
- 类型与继承；
- 构造、方法、属性（get／init／set）与字段；
- 参数修饰、默认值和可空注解。

快照首行的 SDK 主次版本须与包准入一致。要改作者可见表面，先升 SDK 版本，再用 `MAKO_UPDATE_SDK_SURFACE=1` 重写快照。
冻结点（版本与提交）登记在 M13 证据记录。

单一来源：
- [包 profile](../../backend/src/MakoKaraGenerator.Core/Effects/EffectPackageProfiles.cs)
- [设备准入](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/ScriptBackendRequirement.cs)
- [生产组合根](../../backend/src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs)

## 2. 包清单

包是一个目录，`manifest.json` 列出全部文件及其 SHA-256，内容按固定闭包使用。

| 字段 | 说明 |
| --- | --- |
| `packageId`、`name`、`version` | 包身份与显示名 |
| `files[]` | `path`、`role`、`sha256`。role 有 `csharp`、`asset`、`image`、`svg`、`svgResource`、`graph`、`shaderProgram`、`shader` |
| `parameters[]` | `id`、`type`、`label`、`default`，以及按类型取的 `min`／`max`／`unit` 或 `values`。类型见下表 |
| `exports[]` | 导出，见下文 |
| `requires[]` | 包需要的能力（§2.1），未知能力以 `PackageCapabilityUnsupported` 拒绝 |
| `lineExit` | `{ "durationUs": n }`，行隐藏后的退出寿命 0～3 000 000 µs，需能力 `line.exit`（§4） |
| `emitters[]` | 粒子发射器：`id`、`kind`（`cursor`／`burst`）、`maxLifetimeUs`（1～5 000 000）（§6） |
| `mediaQueries[]` | 媒体特征查询，需能力 `media.features`（§9） |

| 参数类型 | 读取方法 | 约束 |
| --- | --- | --- |
| `number` | `GetNumber` | `min`／`max` |
| `integer` | `GetInteger` | `min`／`max` |
| `duration` | `GetDuration`（整数微秒） | `min`／`max`，不得为负 |
| `enum` | `GetEnum` | `values[]`（`value`、`label`） |
| `boolean`、`text`、`color`、`curve` | `GetBoolean`、`GetText`、`GetColor`、`GetCurve` | 无 |
| `asset` | `GetAsset`（返回本包图片的 `ImageHandle`） | 无 |

**导出种类：**

- **`controller`**：C# 效果。字段有 `entryType`（实现入口的类型名）、`parameters`、`bindingKinds` 和 `outputs[]`。
  - `bindingKinds` 取 `project`、`branch`、`lines`、`tokens`、`clips`、`node`。
  - `outputs[]` 每项有 `id`、`kind`、`target`、`requires` 和 `dependsOn`。
- **`graph`**：无 DLL 图，见 §8。字段有 `graph`（图文件）、`parameters`、`bindingKinds`，`outputs` 只能是 `ImageGraph`。
- **`material`**：无 DLL 材质，见 §8。字段有 `graph`、`parameters`、`regions`（`fill`／`stroke`）、`fallback` 和 `coverageMode`（`masked`／`unmasked`），
  唯一输出为 `ImageGraph`／`region`。

| 输出 `kind` | 可用 `target` | 对应写入 |
| --- | --- | --- |
| `VisualTransform` | `tokenFamily`、`baseToken`、`line`、`group`、`textMark`、`cluster` | `AppendTransform`、`SetWarp` |
| `StyleModulation` | `tokenFamily`、`baseToken`、`group`、`textMark` | `MultiplyOpacity`、外观、扫字、材质 uniform |
| `ImageGraph` | `tokenFamily`、`line`、`group`、`textMark`；材质为 `region` | `BindGraph`；材质由宿主执行 |
| `Overlay` | `canvas`（只限 controller） | 粒子与晚期视觉图元 |

包导出的 `bindingKinds`，以及输出的 `kind`、`target`、`requires`，必须与入口的实际行为一致。
Declare 返回后，宿主会再核对一遍固定计划、寿命、能力，以及具名输出的种类和层。只靠清单通过检查、或者作者构造出了合法对象，都不能绕过运行门禁。

### 2.1 能力

能力名集中在 `CapabilityNames` 常量（`CapabilityNames.All` 为全集），SDK 写入检查与宿主准入共用这一份。清单 JSON 里仍写字符串本身。

| 类别 | 能力 |
| --- | --- |
| 仿射 | `token.affine`、`line.affine`、`group.affine`、`mark.affine`、`cluster.affine` |
| 透明度 | `token.opacity`、`group.opacity` |
| 外观 | `style.fill`、`style.stroke`、`style.shadow`、`style.glow`、`style.wipe`、`material.uniform` |
| Warp | `token.warp`、`cluster.warp`、`mark.warp` |
| 时间与退出 | `timeline.index`、`line.exit` |
| 粒子 | `particles.continuous`、`particles.burst` |
| 晚期视觉 | `visuals.dots`、`visuals.segments`、`visuals.trails`、`visuals.sprites`、`visuals.paths` |
| 资源 | `resources.svg.vector`、`resources.svg.atlas` |
| 图 | `image.graph`、`image.graph.group` |
| 媒体 | `media.features` |

`timeline.index` 只表示包声明依赖索引。查询没有逐方法的能力门禁，它也不扩大 `SourceSubscription`。

## 3. 两个入口：Declare、Evaluate、EvaluateVisuals

```csharp
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record Settings(double Scale) : PreparedEffect;

public sealed class Example : IPureKaraokeEffect<Settings>
{
    public Settings Declare(DeclareContext source)
        => new Settings(source.Parameters.GetNumber("scale"));

    public void Evaluate(in TextFrame frame, Settings settings, TextOutput output)
    {
        foreach (var geometry in frame.Geometry)
            if (geometry.BaseGeometry.CanTransformIndependently)
                output.AppendTransform(geometry.WithRuby,
                    Affine2.ScaleAt(settings.Scale, geometry.BaseGeometry.AdvanceBounds.Center));
    }
}
```

实际的包还要写好参数、输出、绑定，以及 `requires: ["token.affine"]`。

**阶段顺序**由宿主固定，每一帧依次是：
1. 固定准备（Declare）；
2. 各实例的文字阶段（`Evaluate`）；
3. 按实例顺序合成出最终文字几何；
4. 粒子出生锚点补算，并解析存活粒子；
5. 各实例的晚期视觉阶段（`EvaluateVisuals`）。

晚期阶段只能产生图元，不能回写文字或发射计划。文字阶段不读取任何实例（包括自己）的输出。

| 成员 | 作用 |
| --- | --- |
| `Declare(DeclareContext)` | 为固定输入准备递归只读的 `PreparedEffect`。固定参数、源、资源、媒体、画布或工程时长改变时重新准备；seek 不重新准备。无数据时返回 `PreparedEffect.Empty` |
| `Evaluate(in TextFrame, TPrepared, TextOutput)` | 直接描述目标时刻的文字输出，没有跨帧状态 |
| `EvaluateVisuals(in VisualFrame, TPrepared, VisualOutput)` | 晚期视觉（`IPureVisualEffect<TPrepared>`），读取最终几何与本实例粒子，绘制声明过的画布图元 |

**泛型入口。** `IPureKaraokeEffect<TPrepared>` 让 `Declare` 直接返回作者的准备记录，`Evaluate` 直接收到同一份记录，不用再强转。
宿主仍通过非泛型接口调用；非泛型成员由 SDK 默认实现转发，作者不必也不应自己实现。
`IPureVisualEffect<TPrepared>` 继承同一 `TPrepared` 的文字入口，所以两个阶段的准备记录类型一定相同。
非泛型写法（返回 `PreparedEffect`、自行强转）仍然可用，适合不读准备结果的效果，比如 CursorTrail、EventBurst。

**`PreparedEffect` 的宿主字段。** 下面三项都进入固定准备身份：

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `Particles` | `ParticleSchedule.Empty` | 连续与 Burst 发射计划（§6） |
| `VisualOutputs` | 空 | 本实例允许的晚期输出：`VisualOutputDeclaration(OutputId, VisualPrimitiveKind, OverlayLayer, VisualResourceKind)` |
| `GeometrySampling` | `GeometrySamplingPolicy.Exact` | 粒子锚点的最终几何采样策略（§6.2） |

**`DeclareContext`** 提供以下成员：
- 固定输入：`Content`、`Scene`、`Parameters`、`Audio`、`Resources`、`Media`、`Seed`、`InstanceId`、`Random`；
- 索引：`Timeline`、`Indexes`；
- 画布与时长：`CanvasSize`、`ProjectDurationUs`。

`ProjectDurationUs` 是工程已确定的成片时长（`long?`，整数微秒）。工程未设成片时长时为 `null`，宿主不会拿最后一行歌词或背景时长代填。
`Content` 和 `Scene` 是完整的固定源目录，包括 `SourceNode`、`SourceProgram`、`SourceClip`、`SourceCue`、根组与默认容器。
源索引另外受实例的 `SourceSubscription` 约束；读取目录不授予输出权限。

**`TextFrame`** 提供 `Source`（即 `DeclareContext`）、`TimeUs`，以及同名快捷属性 `Parameters`、`Audio`、`Resources`、`Media`、`Random`、`Timeline`、`Indexes`、`CanvasSize`、`ProjectDurationUs`。其余成员：

| 成员 | 语义 |
| --- | --- |
| `Presentations` | 当前实例绑定范围内的真实源目标及演唱状态 |
| `Geometry`、`LineGeometry`、`MarkGeometry`、`GroupGeometry` | 生产后端的基础几何；退场对象用冻结几何 |
| `Clusters` | 由同一整行整形结果投影出的基础字与 Ruby 绘制单元，按真实目标去重 |
| `LineLoads` | 正常装载与隐藏后的退出副本（§4） |
| `Baseline` | 静态继承的样式基线，不含任何作者实例的输出 |
| `Binding`、`OutputMode` | 本实例的输出范围；当前输出是 `Opaque` 还是 `Transparent` |

逐字的 `PerformanceSample` 中 `Progress`、`ElapsedUs` 等可能为空。`Missing` 不能当成零进度；元素演唱与准备、保留、退场三个相位要分开判断。
基础 `BaseWorldMatrix` 已经包含源父组、槽位、单句和进入动画，作者不要再乘一次父变换。

**`VisualFrame`** 有 `FinalGeometry`（`FinalSceneGeometry`：最终的 `Tokens`、`Lines`、`Marks`、`Groups`、`Presentations` 与播放头 `Playback`）、`Particles`（本实例的 `ParticleFrame`），以及与 `TextFrame` 相同的快捷属性。
`FinalGeometry` 已经合成了全部实例的仿射、Warp 与父变换。`Playback.Cursors` 中每个 `CursorGeometry` 可以用 `At(CursorAnchorMode)` 取最终位置，
改了 `Progress` 后再取，就能沿同一条进度路径回溯（StarSweep 的尾星用的就是这个方法）。

源码：
- [PureEffect.cs](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/PureEffect.cs)
- [TextFrame.cs](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextFrame.cs)
- [VisualFrame.cs](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/VisualFrame.cs)

### 3.1 准备结果与纯度边界

准备结果可以用以下几类：递归只读的作者 record、受支持的 SDK 值、不可变集合。
不能保存到准备结果、对象字段或逃逸闭包里的东西有：上下文、帧、输出、源查询句柄、媒体采样句柄、委托、可变集合和原生资源。
字段只读并不能让它引用的可变对象变成只读。

**分析器的检查范围**是真实入口，以及从入口可达的 helper、getter、构造器、初始化器和委托。下面这些非固定输入的能力都不允许：
- 墙钟、系统随机、环境变量；
- 文件、网络、线程；
- 反射、原生入口；
- `async`、`unsafe`、`dynamic`。

局部变量和本次调用内的局部集合可以改，跨调用的状态不能改。字符串比较与排序必须写明 ordinal 语义；`GetHashCode()` 不能用作稳定随机或身份的来源。

| 代码 | 含义 |
| --- | --- |
| `MKV2001`～`MKV2007` | 纯度或入口错误，编译失败 |
| `MKV2008`、`MKV2009` | 提示逐帧分配与 LINQ 的成本，不阻止编译 |

这些检查不是运行预算或终止性证明，纯度分析也不是安全沙箱。宿主另有运行预算、取消与 Worker 隔离。

**随机数**用宿主的 `Random.GetUInt32(streamKey, index)`／`GetUnitDouble(streamKey, index)`。
只要 seed、实例、流键与 index 相同，结果就相同；调用不消耗随机游标。

源码：
- [纯度分析器](../../backend/src/MakoKaraGenerator.Scripting.Compiler/ScriptV2PurityAnalyzer.cs)
- [调用白名单](../../backend/src/MakoKaraGenerator.Scripting.Compiler/ScriptV2PurityAnalyzer.Calls.cs)

## 4. 正常装载、退出副本与同源重入

`LineLoads` 的每项是 `TextLineLoad`，有 `Line`、`Target`、`Phase`、`HideTimeUs`、`ExitDurationUs`、`ExitProgress` 六个成员。
- **`Active`**：仍由源布局提供。
- **`ExitReplica`**：从真实隐藏时刻开始。几何、槽位、完整父链和静态源基线都取隐藏左极限 `hide - 1 µs` 的值。
  源演唱进度仍按查询时刻读取，不要把冻结的几何当成冻结的演唱时间。退出副本不参与源布局、起唱计数和基础播放头。

**寿命**由包的 `lineExit.durationUs` 声明，区间是 `[hide, hide + duration)`，终点时副本已不存在。新句照常按布局装载，不等旧句退完。
- 多个实例各自读自己的退出时长，进度夹到 `[0,1]`。
- 别的实例还需要同一副本时：寿命较短的实例过了自己的终点读到 1；声明为零的实例在副本阶段也读到 1。
- 窄绑定实例声明的长寿命不能延长范围外的装载。

**同源重入。** 同一源行被别的行遮挡后再次出现，是一次新的展示装载，但仍是同一个源演唱 occurrence。
生产采样器给重入装载生成独立的 `BindingId`，内部可能带 `/reload/{actualRevealUs}` 后缀。
**作者只传递宿主给的强类型身份，不要解析或拼接这个后缀，也不要从 `LineId` 推导 Binding。**
这样旧副本与同源的新装载可以同时存在、分别输出，且不会重复计入起唱次数。

源码：
- [装载身份](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/LyricSceneSampler.LoadIdentity.cs)
- [退出索引](../../backend/src/MakoKaraGenerator.Scripting/Projection/PresentationIndex.cs)
- [冻结生产几何](../../backend/src/MakoKaraGenerator.Scripting/Projection/FrozenExitGeometry.cs)

## 5. 文字输出

`TextTarget` 是封闭的目标类型，现有的目标类型可以隐式转换成它。请使用宿主给出的真实目标，不要自己编号。

| 目标（取得方式） | `AppendTransform` | `SetWarp` | `MultiplyOpacity` | 外观与扫字 |
| --- | --- | --- | --- | --- |
| `TokenFamilyTarget`（`geometry.WithRuby`） | `token.affine` | `token.warp` | `token.opacity` | `style.*` |
| `BaseTokenTarget`（`geometry.BaseOnly`） | `token.affine` | `token.warp` | `token.opacity` | `style.*`，仅基础字 |
| `LineTarget`（`load.Target`） | `line.affine` | 不支持 | 不支持 | 不支持 |
| `GroupTarget`（真实且可写的组） | `group.affine` | 不支持 | `group.opacity` | 不支持 |
| `TextMarkTarget`（真实提示标记） | `mark.affine` | `mark.warp` | 不支持 | `style.*`；`SetWipe` 不支持 |
| `TextClusterTarget`（`frame.Clusters[i].Target`） | `cluster.affine` | `cluster.warp` | 不支持 | 不支持 |

读取全局组几何不授予写权；组的写范围由真实运行父树与本实例的输出绑定共同决定。`EffectBinding.Tokens` 不授予整行目标。
常见拒绝：
- 写范围外的目标：`TargetOutsideBinding`；
- 缺能力：`UndeclaredCapability`；
- 属性与目标的组合不受支持：`PropertyTargetUnsupported`。

**事务与组合。** 输出是本次调用的事务，完整成功返回后才发布；抛异常时，这一阶段的输出全部丢弃。
`TextOutput` 的构造、`Complete()`、`Invalidate()` 只供宿主调用，作者看不到，也不能保存 `TextOutput`。
每个实例只读源基线与源几何，不读前一实例的输出；宿主按固定的实例顺序组合。

### 5.1 仿射、Warp 与透明度

- **`AppendTransform(target, matrix)`**：在目标局部逻辑空间追加。先写 A 再写 B，累计结果是 `B * A`。
  矩阵须有限、可逆，并且按后端 float 精度计算后仍然可逆。不同层级由宿主统一合成，不依赖作者的遍历顺序。
- **`SetWarp(target, GridWarp?)`**：整体替换目标的局部网格。传 `null` 是明确关闭，不写则继承。
  - 同一层总是先应用网格，再应用仿射。
  - `GridWarp(domain, columns, rows, destinationVertices)` 每轴 1～32 格，目标顶点按行优先排列，共 `(columns+1)×(rows+1)` 个。
  - 网格须有限，三角形须正向、不退化，外边界须简单。
  - `MapPoint` 越出定义域时报 `WarpOutsideDomain`。
- **Warp 的作用范围。** Warp 沿唯一的文字映射，同时作用于几何、最终播放头、粒子出生锚点和像素。
  共享字形不能单独设网格，报 `UnsupportedGlyphPartition` 或该几何的具体诊断。
- **`MultiplyOpacity`** 只接受 `[0,1]` 内的有限因子，多次写入累乘；组透明度走隔离合成。

### 5.2 两态外观与扫字

`SetFill`、`SetStrokes`、`SetShadow`、`SetGlow` 的第二个参数是 `TextState`，取 `Unsung`、`Sung` 或 `Both`；`Both` 等于两态各写一次。同一态的同一成员以后写为准。

| 写入 | 说明 |
| --- | --- |
| `SetFill(target, state, MaterialValue)` | 取值有 `MaterialValue.Solid(color)`、`Gradient(GradientValue)`、`PackageExport(MaterialHandle, ParameterValues)` |
| `SetStrokes(target, state, params StrokeValue[])` | 整表替换一态的描边，由内向外 0～4 层，不传参数即无描边。超过 4 层，或包材质不在最内层，报 `InvalidStrokeLayers` |
| `SetShadow`／`SetGlow(target, state, value?)` | 整体替换，传 `null` 为关闭，不写为继承 |
| `SetWipe(target, WipeValue)` | 整体替换扫字样式，两态共享，需要 `style.wipe`。`FeatherRatio` 为 0～2 倍字号；`Highlight` 是可选的扫字线高亮（`WipeHighlightValue`：颜色、宽度比 0～2、不透明度、混合方式） |

**只写一态时，另一态不会跟着变。** 源里已唱跟随未唱时，只写未唱会把已唱固定为源的未唱值；两态写同一个值，则保持跟随。

**渐变**的坐标取以下三种之一：
- `Canvas`：画布；
- `Line`：整行，纵向参考框用字体度量；
- `Glyph`：按单字，每个字簇以自己的字框为参考框。

`GradientValue.Bands(colors, boundaries, …)` 按“颜色＋分界”构造硬边分层渐变：n 层颜色配 n−1 个严格递增、落在 (0,1) 内的分界。例如：

```csharp
output.SetFill(target, TextState.Both, MaterialValue.Gradient(
    GradientValue.Bands([red, white, blue], [.4, .6], coordinateSpace: GradientCoordinateSpace.Glyph)));
```

**只读基线** `frame.Baseline.Get(target)` 返回 `StyleView`，包括：
- `Unsung`、`Sung` 两个 `AppearanceView`，各含 `Fill`、`Strokes`、`Shadow`、`Glow`。已唱跟随时，两态取同一个值。
- `Opacity` 与 `Wipe`。
- `Material(MaterialSlot)`：按 `UnsungFill`、`SungFill`、`UnsungStroke`、`SungStroke` 取材质，描边槽指最内层。

### 5.3 图绑定与材质 uniform

- **`BindGraph(target, GraphBindingValue)`**：每次整体替换，不与上一次合并。
  - 绑定值包括本实例包修订的 `GraphHandle`（`Resources.GetGraph`）、完整的 `ParameterValues`、逐节点 uniform，以及 Transform 覆盖。
  - 支持 Family、Line、Mark 的 `image.graph`。真实 Group 还要同时声明 `image.graph.group`，且只限 Project 或显式 Node 授予的真实父树。
  - controller 与实际的 graph 都要有唯一匹配的 `ImageGraph` 输出。
  - ColorOnly 图只改颜色，不改最终几何。Mapping 图以 `PackageGraphMappingPending` 拒绝；Base 与 Cluster 的图输出未开放。
  - 详见 [组图合同](../../backend/docs/scripting-group-image-composition-contract.md)。
- **`SetMaterialUniform(target, slot, material, nodeId, name, UniformValue)`**：需要 `material.uniform`。
  - 定位方式是：真实的 Family、Base 或 Mark，四个材质槽之一，固定的 `MaterialHandle`，再加上 `nodeId/name`。
  - 跨实例按“原始材质替换”与“uniform 调用”的顺序归约，完整替换会清除旧的覆盖。
  - 覆盖只属于本帧，不修改包、工程参数或 shader 源码。
  - 两态描边绑定同一份包材质时，按一个部件执行；材质不同时，按两态拆开，在过渡带内交叉淡化。

**Base 外观**从最终 Family 的中立外观继承，逐部位保留 Source 或 Script 的归属。写 Solid、Gradient 或 `Stroke=null` 会明确遮蔽父材质。
Base 捕获不含 Ruby，Ruby 保留父字族的外观。包材质必须属于当前实例的固定资源修订，未知资源报 `InvalidMaterialResource`。

### 5.4 基础字、Ruby 与字簇

- **Family 与 BaseOnly。** Family 同时影响基础字与关联的 Ruby；BaseOnly 只改基础字，Ruby 保留父字族的外观和变换。
- **Cluster** 是整行或整个 RubyRun 里真实、不可拆分的整形单元，`RubyId == null` 才表示基础字。
  `Utf16Start`／`Utf16Length` 只表达与来源的关联，不能用来切割字形。
- **跨 token 连字**保留 `OwnerTokens` 的完整所属范围，并且 `CanTransformIndependently == false`。
  对它做独立变换或外观会以 `UnsupportedGlyphPartition` 拒绝；可以退回到对整行 `LineTarget` 做变换，不能强拆共享连字。
- Cluster 目前只支持仿射与 Warp，不支持独立的填充、透明度、描边、阴影或辉光。

源码：
- [TextOutput](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextOutput.cs)
- [封闭目标和写入记录](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextOutputValues.cs)
- [真实字簇](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextClusters.cs)
- [分区准备](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/PreparedRenderFrame.TextPartitions.cs)

## 6. 粒子

粒子由宿主按 Declare 中的计划解析，作者不写逐帧积分。计划放在 `PreparedEffect.Particles`（`ParticleSchedule(Continuous, Bursts)`）。
每个计划的 `EmitterId` 须引用清单 `emitters[]` 中同名、同种类的发射器；输出要声明 `Overlay`／`canvas`，并写明 `particles.*` 能力。
自动绘制的圆点还需要 `visuals.dots`。

| 计划 | 构成 | 能力 |
| --- | --- | --- |
| `CursorEmissionPlan(Id, Rate, Particle, AnchorMode, Bridge)` | 沿真实播放头连续发射 | `particles.continuous` |
| `EventBurstPlan(Id, Selector, Rows)` | 按固定真实事件逐行爆发 | `particles.burst` |

**连续计划的三个参数：**

- **`RateTrack(Segments, EnabledRanges)`**：按歌曲时间的速率轨。每个 `RateSegment(Range, StartPerSecond, EndPerSecond)` 为常速或线性。
- **`AnchorMode`**：取 `Baseline` 或 `AdvanceCenter`。
- **`BridgePolicy`**：
  - `BridgePolicy.Jump`：在相邻播放头之间直接跳，不发射桥接粒子。
  - `Bridge`：在两段播放头之间生成一条桥接路径，按 `DurationUs`、`GapLimitUs` 与 `Curve` 发射。

宿主对速率做精确的有理数积分，逆推出生时刻。普通 head 在同轨道、同装载、首尾相接的 token 之间保留发射配额；
遇到真实空档、禁用或重载时清零。每个真实 Bridge 转换独立计算。

**Burst 计划**的 `BurstEventRow(EventId, Count, Anchor, Particle)` 要绑定真实固定事件，通常在 Declare 中由 `Timeline` 查询得到。
锚点有四种：`SourceToken`、`SourceLine`、`TextMark` 与 `BindingCursors(mode)`。不能只凭时间相同就冒充事件。

**`ParticleSpec`** 的字段：
- 外观与寿命：`LifetimeUs`（≤ 5 000 000）、`SizePx`、`Color`、`SpreadPx`、`TrailDurationUs`；
- 运动：`Velocity`、`Motion`（`Acceleration`，以及按年龄变化的 `Opacity`／`Size` 曲线）；
- 其他：`SeedStreamKey`、`Layer`（`BehindText`／`AboveText`）、`Rendering`。

`Rendering = Custom` 时宿主不自动画圆点，但保留粒子运动、完整出生窗与全部预算，由作者在晚期阶段自己绘制。

### 6.1 出生、存活与最终锚点

- **身份。** 粒子身份 `ParticleIdentity` 由实例、发射器和完整的出生来源（Continuous 或 Burst）组成，不是稠密序号。
- **`ParticleFrame`。** 存活条件为 `birthUs <= timeUs < deathUs`。`ParticleFrame` 有三个成员：
  - `Alive`：当前存活的粒子；
  - `Births`：出生窗 `[max(0, t−5 s), t]` 内的完整出生表，已死亡的粒子也在其中；
  - `GetTrail(id)`：按绝对歌曲 120 Hz 取的拖尾节点。

  不能从 `Alive` 反推完整的出生表。文字离场也不会提前杀死粒子。
- **出生锚点**取真实出生时刻的最终文字几何（含作者仿射与 Warp）。之后的散布、恒加速度、年龄外观和拖尾都按固定值解析，不逐帧积分。

### 6.2 锚点采样策略

默认的 `GeometrySamplingPolicy.Exact` 在每个出生时刻精确补算文字阶段。
`Continuous120Hz` 只在作者能证明文字输出连续的区段（`ContinuousRanges`，断点列在 `DiscontinuitiesUs`）内，按固定网格插值；
不在声明区段内的时刻仍走 Exact。宿主不会因为耗时、预算或 memo 命中情况自动降低精度。

补算只调用在该时刻绑定范围内有文字、行、标记或可写组的纯实例，成本随在场行数增长，与全曲行数无关。

源码：[ParticleSchedule](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/ParticleSchedule.cs)；
行为合同见 [后端 API §26](../../backend/docs/architecture/backend-api.md#sdk2-m08-particles)。

## 7. 晚期视觉与包资源

`VisualOutput` 按声明的 `OutputId` 绘制六类批次：

| 方法 | 图元 | 能力 |
| --- | --- | --- |
| `DrawDots` | `Dot(position, radius, color)` | `visuals.dots` |
| `DrawSegments` | `Segment(start, end, width, color)`，平端 | `visuals.segments` |
| `DrawTrails` | `Trail(points)`，每点 `TrailPoint(position, width, color)` | `visuals.trails` |
| `DrawSprites` | `Sprite(ImageHandle, unitToWorld, tint, source?)`，完整仿射 | `visuals.sprites` |
| `DrawPaths` | `VectorVisual(VectorHandle, unitToWorld, tint)`，SVG 矢量 | `visuals.paths` ＋ `resources.svg.vector` |
| `DrawAtlasSprites` | `AtlasSprite(SpriteHandle, unitToWorld, tint)`，SVG 图集 | `visuals.sprites` ＋ `resources.svg.atlas` |

每个批次的 `OutputId`、图元种类、层（`OverlayLayer`）和资源种类，都必须与 Declare 中的 `VisualOutputDeclaration` 一致。
不一致时报 `UndeclaredPackageOutput`；同一输出在一次调用里只能画一次，否则报 `DuplicateOverlayBatch`。调用结束或失败后，视图与 writer 都失效。

**资源句柄**由 `Resources` 取得，并固定在本实例的包修订上，不接受跨修订的句柄：
- 图片：`GetImage(assetId)`，或参数 `GetAsset`；
- 矢量：`GetVector(resourceId)`；
- 图集：`GetSprite(resourceId, variantId)`；
- 图与材质：`GetGraph`、`GetMaterial`。

**SVG 资源**由一个 `svgResource` 文件描述，例如：

```json
{"svgResourceVersion":1,"id":"star","source":"assets/star.svg","profile":"mako-svg-static-1",
 "mode":"vector","viewport":{"width":100,"height":100},"fonts":[],"images":[]}
```

`mode` 为 `vector` 或 `atlas`。SVG 的使用限制：
- 外部 URI、脚本、动画、文字或机器字体、未知语法、未声明的图片都会具名拒绝；
- `clipPath` 只允许直接形状。

正式编译会先校验全部 SVG；失败时返回资源错误码与包内路径，不产出 DLL。tint 的半透明作用在整个矢量结果上；图集使用独立的 padding 页和 mip 采样。

源码：[VisualOutput](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/VisualOutput.cs)；
行为合同见 [后端 API §27](../../backend/docs/architecture/backend-api.md#sdk2-m09-svg-warp)。

## 8. 无 DLL 的图与材质包

不需要 C# 的效果可以只由图、shader 程序和 SkSL 组成。这类包不编译、不创建作者对象，也不调用 Declare，由宿主直接执行。

- **`graph` 导出**按宿主授予的 Family、Line、Mark 或 Group 范围绑定，`bindingKinds` 与 controller 相同。token 绑定不能扩大成整行。
- **`material` 导出**作为填充或描边的材质，在模板、样式或脚本的 `MaterialValue.PackageExport` 中使用。
  - `regions` 写明适用部位；
  - `fallback` 是缺输入时的兜底外观；
  - `coverageMode` 为 `masked` 时，输出受区域覆盖遮罩约束。

**图文件**（`graphVersion: 1`）的组成：
- 节点：例如 `RuntimeShader`，引用 `shaderProgram` 文件；
- uniform 来源：`parameter:<id>`、`host:timeSeconds` 等；
- 其他：输入、边界外扩与取样偏移，以及合成方式。

**输入**可以读目标颜色与覆盖、包图片、`BackdropBelow`（真实下层）、`BackgroundColor` 和 `BackgroundMedia`（背景媒体，可标为可选）。缺输入时的规则：
- 透明输出，或不透明输出缺背景媒体时，必需的 `BackgroundMedia` 输入走旁路，材质退回 `fallback` 并给诊断 `BackgroundMediaUnavailable`；
- 可选的输入用工程底色代替。

预览与导出按同一规则兜底，导出报告列出兜底诊断。

格式细节见：
- [图合同](../../backend/docs/scripting-image-graph-contract.md)
- [shader 程序合同](../../backend/docs/scripting-shader-program-contract.md)
- [区域材质合同](../../backend/docs/scripting-region-material-contract.md)
- [后端 API §28](../../backend/docs/architecture/backend-api.md#sdk2-m09-graph-material-group)

## 9. 媒体特征

包在 `requires` 中声明 `media.features` 后，可以在清单根写 `mediaQueries`。每项包括：
- `id`；
- `space`：`sourceNormalized`（旋转后规范源的单位方形）或 `canvasPixels`（画布逻辑像素）；
- `window`：拖尾窗口，可以是字面量或同包的 `duration` 参数，取值 0～2 000 000 µs。

在 Declare 中用 `source.Media.GetId("behind")` 取 `MediaQueryId`，在文字或晚期阶段用 `frame.Media.Sample(id, rect, timeUs)` 读取固定结果。

`MediaFeatureSample` 的成员：
- `Input.State`：取值 `Available`、`Pending`、`Unavailable` 或 `NoMedia`。只有 `Available` 带数值，其他状态返回空值，作者应保持源样式。
- `CanvasValues`：线性预乘均值、`MeanPremulY`、`PremulYVariance` 等。
- `SourceValues`：未摆放、未合成底色的源统计。

**状态由宿主在准入时固定，一次运行内不变：**

| 运行类型 | 固定方式 |
| --- | --- |
| 工程预览 | 不等待分析，未完成按 `Pending` 处理 |
| 正式验证与导出 | 等待完整的分析结果 |
| 作者试样 | 最多等 20 秒 |
| 透明输出 | 一律为 `NoMedia`，原因是 `TransparentOutput` |

分析完成后，Server 发出 `project.mediaFeaturesChanged`，客户端用同一修订重新固定预览输入即可看到新结果。

源码：[MediaFeatures](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/MediaFeatures.cs)；
行为合同见 [后端 API §28.6](../../backend/docs/architecture/backend-api.md#sdk2-m10-media-features)。

**音频特征**（`Audio`）复用工程已有的固定分析结果：用 `GetAt(t)`／`GetRange(range)` 读取。`Availability` 取 `Available`、`NoAudio`、`NotAnalyzed`、`AnalysisFailed`、`OutsideRange`，
只有 `Available` 的采样带数值；工程未分析时为 `AudioFeatures.NotAnalyzed`。

## 10. 四类固定源索引

入口是 `source.Indexes` 或 `frame.Indexes`，包括 `Timeline`、`Presentations`、`Layout`、`Cursors`。
`source.Timeline`／`frame.Timeline` 与 `Indexes.Timeline` 是同一个固定事件索引。
这些查询只读取固定输入，可以看到未来的真实源信息；它们不回放作者历史，也不读取任何实例的最终输出。
查询句柄只在当前的 `Declare`／`Evaluate` 调用内使用，不要保存到 `PreparedEffect`。

### 10.1 固定 subscription 与每次 filter

- **订阅**：`SourceSubscription.BoundSources`、`.Project`、`.Branch(BranchId)` 在实例装载时固定。
  `BoundSources` 由输出绑定解析出关联的源；显式的 Project／Branch 读范围与输出绑定相互独立。
- **过滤**：查询用的 `SourceFilter.Project`、`.Branch(BranchId)`、`.Lines(…)`、`.Tokens(…)` 总是与固定订阅取交集，不能在帧内扩大订阅。
- **ID 规则**：ID 必须是真实的源 ID，源行 ID 与场景 NodeId 不能互换。纯组和前奏标记可以有自己的订阅范围，宿主不为它们制造 TokenId。

### 10.2 TimelineIndex

用 `new TimelineSelector(kind, scope = null, sourceText = null)` 创建有限键，scope 默认为 Project。
可用的 scope 有 `Project`、`Branch`、`Line`、`Token`、`Occurrence`、`Binding`、`Node`、`Cue`。
`sourceText` 只对带真实源文本的行或 token 演唱事件有效，按精确 ordinal 匹配，不做大小写、区域或 Unicode 归一化。

| API | 边界与返回 |
| --- | --- |
| `Events` | 全部真实事件，固定且稳定排序 |
| `Last(selector, timeUs)`／`Next(selector, timeUs)` | 最后一个 `TimeUs <= t`／第一个 `TimeUs > t` 的事件，没有则为 null |
| `Count(selector)`、`Count(selector, timeUs)`、`Count(selector, TimeRange)` | 键内全部事件数（含未来）、截至该时刻（含边界）的数量、半开范围内的数量 |
| `Nth(selector, index)` | 下标从 0 开始，越界为 null |
| `Ordinal(selector, EventId)`／`Ordinal(selector, OccurrenceId)` | 从 1 开始的事件序号或真实演唱 occurrence 序号。展示重入不增加次数 |
| `GetRange(selector, TimeRange)` | 半开范围内的事件，稳定有序 |

事件按稳定身份去重；同一身份带有不同语义时拒绝。准备显示、提示、Ruby 和退出副本都不会增加 `TokenStarted`。

### 10.3 SourcePresentationIndex

| API | 返回 |
| --- | --- |
| `Get(BindingId)` | 订阅内一次真实装载的 `SourcePresentationLoad?` |
| `GetAt(timeUs, SourceFilter)` | 该时刻可见的源装载，即 `SourcePresentationSample` 数组 |
| `GetRange(TimeRange, SourceFilter)` | 与半开范围相交的完整装载，稳定排序 |

`SourcePresentationLoad` 包含以下成员：
- `Binding`：揭示边界；
- `RevealTimeUs`、可空的 `HideTimeUs`、`LoadOrdinal`；
- 可空的 `PreparationRange` 与 `LayoutExitRange`。

没有真实的隐藏边界时，`HideTimeUs` 保持 null，不会用工程时长填补。
`LayoutExitRange` 是源在 hide 之前就有的布局退出时钟，与 hide 之后的 `TextLineLoad.ExitProgress` 不是一回事；索引不返回退出副本。

### 10.4 SourceLayoutIndex

`GetBaseAt(timeUs, filter)` 调用与生产相同的几何 provider，返回筛选后的 `Lines`、`Tokens`、`Marks`、`Groups` 及其几何。
这些都是源基础几何，不含任何作者的变换。时刻不合法，或基础帧里含作者的退出副本时，以 `InvalidSourceLayout` 拒绝。

### 10.5 SourceCursorIndex

| API | 返回 |
| --- | --- |
| `GetSegments(CursorTrackId, TimeRange)` | 一条真实轨道上，与查询窗相交的 `SourceCursorSegment` |
| `GetBaseAt(timeUs, SourceFilter, CursorAnchorMode)` | 该时刻全部真实的 Current `PlaybackCursor`，并发时保留多个 |

区段是原演唱时间与实际装载可见窗的交集；进度仍按完整的 `Identity.TimeRange` 计算，提前隐藏不会重新归一化进度。
所有区间都是半开的。缺路径时报 `BaseCursorGeometryUnavailable`。

源码：
- [SourceIndexes](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/SourceIndexes.cs)
- [TimelineIndex](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TimelineIndex.cs)
- [生产索引](../../backend/src/MakoKaraGenerator.Scripting/Projection/PureSourceIndexes.cs)
- [共享基础帧缓存](../../backend/src/MakoKaraGenerator.Scripting/Projection/FixedInputGeometryProvider.cs)

## 11. 任意时刻求值、确定性与预算

**任意时刻求值。** 纯 v2 会话只在请求的时刻执行作者代码；固定时间线只用于只读索引，不会因为目标在三分钟或十分钟处而推进作者状态。
seek、乱序与重复请求从同一固定输入得到同一结果；缓存只影响成本，不影响语义。
失败或取消不发布部分输出。作者运行错误会让该帧失败并带上准确的诊断：预览可以显示标注了修订的最近成功帧，导出则严格失败。

**确定性范围**限于以下输入都相同：
- 工程、源码与包资源修订；
- 参数与种子；
- 字体与布局、音频与媒体分析；
- 画布、输出模式和后端执行身份。

在这个范围之外，不承诺不同字体、驱动、设备或平台之间像素逐字节一致。
编译内容键包括源码、入口、编译器与参考集、SDK、引擎和分析规则身份；只改参数不需要重新编译。

**混合工程。** M14 之前，同时用 v1 与 v2 的工程仍保留 v1 的语义网格与有状态成本。
若 v1 覆盖了 `OnEvent`、`EvaluateEmitters`、`AdvanceVisuals` 或 `EvaluateVisuals`，并消费最终结果，纯 v2 的文字也要在旧边界上求值。
因此纯 v2 的 seek 成本结论不能推广到混合工程。

**预算**是固定的完整性门禁，超限时具名失败，不降质、不截断：

| 范围 | 上限 |
| --- | --- |
| `TimelineIndexBudget` 默认值 | 输入事件 200 000、有限键 1 000 000、键桶引用合计 4 000 000 |
| 源索引、装载计划、Cursor 区段、单次基础布局 | 各 200 000 项（计权源值） |
| 退出副本 | 同时 128 个；单个副本的父链 256、token 几何 4 096、绘制单元 65 536 |
| 粒子（`ParticleLimits`） | 发射器每实例 32；寿命 ≤ 5 s；出生 50 000；存活 20 000；拖尾节点 200 000；准备 256 MiB、帧 64 MiB |
| 最终锚点补算 | 唯一时刻 4 096；文字实例调用 32 768；寿命窗外端点 1 024 |
| 图与材质原始写入 | 256 次、65 536 个标量、8 MiB |
| 图与表面 | 每图 32 pass、每帧 64 个活动表面、表面每边 8 192 像素 |

`TimelineIndex` 的查询成本：键内 Last／Next／Count 为 O(log n)，Nth 与 ordinal 为 O(1)，范围查询为 O(log n + k)，不包括作者自己的全目录遍历。
建立和查询都支持宿主取消；会话释放后，查询报 `SourceIndexExpired`。

源码：
- [会话分派](../../backend/src/MakoKaraGenerator.Scripting/Execution/FrameSessionFactory.cs)
- [mixed 推进](../../backend/src/MakoKaraGenerator.Scripting/Execution/FrameSession.cs)
- [编译身份](../../backend/src/MakoKaraGenerator.Scripting/Compilation/ScriptCompilationIdentity.cs)

## 12. 作者工作流

v2 分别记录四种源码：最新源码、编译过的源码、已采用的源码，以及配置和预览实际在用的源码。
无论保存的源码正确与否，都只会产生一个不可变修订。**编译通过不会自动采用**，改配置也不能绕过采用门禁。

1. **导入或创建草稿。** 草稿保留 `packageVersion`、`sdkVersion`、当前 `draftRevision` 与源码身份。
2. **保存源码。** `POST /api/v1/drafts/{id}/source-revision-jobs` 保存全部 C# 文件；
   包内其他文本资源（清单、SVG、图、shader）走 `PUT /api/v1/drafts/{id}/package-files`。
3. **编译。** `POST /api/v1/drafts/{id}/compilation-jobs` 指定 `expectedDraftRevision` 与 `sourceRevision`。
   编译前先校验全部 SVG，再编译 C#。
4. **核对编译结果。** 任务终态后，从 `GET /api/v1/jobs/{id}/compilation-result` 核对成功与否、源码、产物、诊断和 `stale`。
5. **采用。** 用 `POST /api/v1/drafts/{id}/adopt-source-revision` 显式采用，可以同时带上完整配置。
   C# 包须有该精确修订的成功编译，否则报 `AuthorSourceCompilationRequired`；无 DLL 包不需要编译。
6. **试样与验证。** 试样预览（`previewKind=authorDraft`）在创建或 `PUT .../input` 时固定配置、来源与后端。
   正式验证固定为 640×360、30 fps、12 秒，不透明与透明各 360 帧，只用 GPU。
7. **导出与使用。** 可以下载包（`GET /api/v1/drafts/{id}/package`），把配置存进个人库，再作为模板配方应用到工程。

**试样的随机取值不随刷新变化。** 模板工坊与作者试样每次捕获都按配方与绑定次序分配固定的实例身份；
配方没有 `fixedSeed` 时用固定的试样种子，所以同一草稿反复刷新时粒子散布不变（0.112.88 起）。

**CAS 与回执。** 写入要带当前的 `expectedDraftRevision`，修订已过期的写入返回修订冲突，并保留原状态。
`202` 只表示请求已被接受。源码页、编译诊断和显示帧的来源要分别核对。
预览帧可以带 `inspect` 取检查投影（字框、字簇、播放头、粒子出生点与成本），见 [§29](../../backend/docs/architecture/backend-api.md#preview-frame-inspection)。

完整请求类型见 [BackendAuthorContracts](../../backend/src/MakoKaraGenerator.Backend.Contracts/BackendAuthorContracts.cs)，
通用工作流见 [HTTP 作者参考](../../backend/docs/style-reference/http-authoring.md)。

## 13. 样例索引

样例都在 [`backend/samples/ScriptEffectsV2/`](../../backend/samples/ScriptEffectsV2)，每个包都有 manifest、README，C# 包另有源码。

**九例**（逐项对应 v1 的九个样例）：

| 包 | 行为 | 用到的表面 |
| --- | --- | --- |
| [WordEmphasis](../../backend/samples/ScriptEffectsV2/WordEmphasis) | 当前字按真实进度放大，唱完回落；基础字与 Ruby 一起变 | `token.affine`；缺时间时保持源外观 |
| [RepeatEmphasis](../../backend/samples/ScriptEffectsV2/RepeatEmphasis) | 按同文本整行演唱事件的 ordinal 逐次放大并压暗 | `token.affine`、`style.fill`、`timeline.index`；不按重入装载计次 |
| [CursorTrail](../../backend/samples/ScriptEffectsV2/CursorTrail) | 沿播放头连续发射圆点粒子，可选 Bridge 与分段速率 | `particles.continuous`、`visuals.dots` |
| [VectorTrail](../../backend/samples/ScriptEffectsV2/VectorTrail) | 自定义粒子，晚期绘制圆点、线段、带状拖尾与精灵 | `particles.continuous`、`visuals.dots/segments/trails/sprites` |
| [SpriteTrail](../../backend/samples/ScriptEffectsV2/SpriteTrail) | 播放头粒子用包内图片绘制旋转星芒和淡出的拖尾 | `particles.continuous`、`visuals.sprites`、`Rendering=Custom` |
| [EventBurst](../../backend/samples/ScriptEffectsV2/EventBurst) | 按真实事件爆发，锚点可选 token／行／标记／播放头 | `particles.burst`、`visuals.dots`、Timeline 查询 |
| [CustomInk](../../backend/samples/ScriptEffectsV2/CustomInk) | 双色波纹材质，无 DLL | `material` 导出、`unmasked` |
| [LineGlitch](../../backend/samples/ScriptEffectsV2/LineGlitch) | 整行分片位移图，无 DLL | `graph` 导出、`image.graph` |
| [GlassInk](../../backend/samples/ScriptEffectsV2/GlassInk) | 玻璃材质：读取背景媒体或真实下层，两种导出 | `material` 导出、`masked`、`BackgroundMedia`／`BackdropBelow` |

**目标样例**（主计划的目标复杂样式拆出的四个）：

| 包 | 行为 | 用到的表面 |
| --- | --- | --- |
| [ClusterExit](../../backend/samples/ScriptEffectsV2/ClusterExit) | 隐藏后按基础字和 Ruby 单元错峰上移、淡出，寿命 600 ms；连字退回整行 | `cluster.affine`、`line.affine`、`token.opacity`、`line.exit` |
| [AdaptiveStroke](../../backend/samples/ScriptEffectsV2/AdaptiveStroke) | 按字后方背景的亮度与纹理切换描边颜色和宽度，分析未就绪时保持原描边 | `media.features`、`style.stroke`、两态 `SetStrokes` |
| [StarSweep](../../backend/samples/ScriptEffectsV2/StarSweep) | SVG 矢量星沿最终播放头扫过整句，后随渐隐小星 | `visuals.paths`、`resources.svg.vector`、`FinalGeometry.Playback` |
| [WaveWarp](../../backend/samples/ScriptEffectsV2/WaveWarp) | 正在唱的字族（含注音）按 6×1 正弦网格起伏 | `token.warp`、`GridWarp` |

**组合 T01**：同一工程叠加 ClusterExit、WaveWarp、StarSweep、CursorTrail、AdaptiveStroke 和 Ruby。
夹具还包括同句双实例、缺时间字、跨 token 连字和场景父子矩阵。回归见
[V2TargetComplexStyleTests](../../backend/tests/MakoKaraGenerator.Rendering.Tests/V2TargetComplexStyleTests.cs)（含边界前后 1 µs）；
HTTP 组合帧与导出见 `backend/tools/backend-script-render-upgrade/m11_complex_probe.py` 与 `m13_export_probe.py`。

## 14. 不在 SDK 2.0 中的能力与已知限制

| 项 | 现状 |
| --- | --- |
| 作者诊断通道（v1 的 `Diagnostics.Report`） | 不在 SDK 2.0，属于批准例外：逐帧求值的警告无法去重，会挤满诊断预算。作者只能抛异常，以后另升 SDK 版本再议 |
| 任意跨帧字段状态 | 不支持这种编程方式，改用源索引、准备结果、粒子计划或晚期视觉表达 |
| Cluster 的独立外观；Base 与 Cluster 的图输出；Mapping 图 | 未开放 |
| 扫字线高亮 | 不叠在包材质部件上 |
| 从右向左排版 | 元素区间错开一个字形（硬切换与羽化相同） |
| 源材质 | 只作用于有 token 的行 |
| 带 Mapping（Warp）且无材质的导出对象 | 工作框不收紧 |
| 缩放、倾斜与 Warp 的像素容差；分层光栅缓存 | 随 P4 处理，SDK 表面不变 |
