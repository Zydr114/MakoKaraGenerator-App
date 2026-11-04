# 脚本 SDK v2 开发参考（M07）

源码核对日期：2026-11-04。本文描述 App 当前工作树已经接通的 M07 文字纯求值接口。
**M07 尚待统一验收**；源码、样例及定向测试的存在不表示全量后端、匹配发布、HTTP、浏览器、桌面或性能验收已经通过。
进度以[阶段一执行记录](../plans/script-sdk-v2-phase-one-progress.md)和[验收清单](../plans/script-sdk-v2-render-acceptance.md)为准。

旧入口继续见 [SDK v1 参考](../../backend/docs/style-reference/script-sdk.md)及[类型索引](../../backend/docs/style-reference/script-sdk-types.md)。
主计划中的晚期视觉、粒子、最终锚点 memo、Warp、SVG 和媒体特征轨属于后续节点；本文不把这些候选设计列为 M07 API。

## 1. 包身份、设备与开发并存

| 项目 | M07 当前实现 |
| --- | --- |
| 包 profile | `packageVersion: 3`、`sdkVersion: "2.0.0"`、`shaderProfile: "skia-ganesh-3.119.4-v2"` |
| SDK 补丁号 | 当前解析器接受规范的 `2.0.x`；未知主版本、次版本和其他 shader profile 拒绝，不回退到 v1 |
| 作者入口 | `MakoKaraGenerator.Scripting.V2.IPureKaraokeEffect` |
| 共用值类型 | `MakoKaraGenerator.Scripting` 中的身份、参数、曲线、音频、材质、矩阵和几何 |
| 执行后端 | 实际使用 v2 的工程要求 Vulkan；CPU 返回 `PackageBackendUnsupported`，调用方须明确选择 GPU |
| v1 开发兼容 | 包 v2／SDK `1.0.0`／`skia-raster-3.119.4-v1` 仍显式分派到 `IKaraokeEffect`／`EffectBase` 路径；尚未执行 M14 删除 |

设备判断依据实际启用实例和使用中的源材质引用。仅把一个未使用的 v2 包放入工程目录，不会剥夺无效果工程的 CPU 能力；
禁用实例也不等于使用。预览、作者正式验证、导出都需过后端准入，不能用读取包成功代替设备可执行证明。
包 profile、内部 shader 程序 profile、产品版本和 HTTP 合同版本是不同身份。

单一来源：[包 profile](../../backend/src/MakoKaraGenerator.Core/Effects/EffectPackageProfiles.cs)、
[设备准入](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/ScriptBackendRequirement.cs)、
[生产组合根](../../backend/src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs)。

## 2. Declare 与 Evaluate

```csharp
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record Settings(double Scale) : PreparedEffect;

public sealed class Example : IPureKaraokeEffect
{
    public PreparedEffect Declare(DeclareContext source)
        => new Settings(source.Parameters.GetNumber("scale"));

    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
    {
        var settings = (Settings)data;
        foreach (var geometry in frame.Geometry)
            if (geometry.BaseGeometry.CanTransformIndependently)
                output.AppendTransform(geometry.WithRuby,
                    Affine2.ScaleAt(settings.Scale, geometry.BaseGeometry.AdvanceBounds.Center));
    }
}
```

示例只展示作者接口；实际包还须提供匹配的参数、输出、绑定和 `requires: ["token.affine"]` 声明。

`Declare(DeclareContext)` 为固定输入准备递归只读的 `PreparedEffect`；无数据时返回 `PreparedEffect.Empty`。
固定参数改变后重新准备，seek 不通过修改字段推进状态。`Evaluate(in TextFrame, PreparedEffect, TextOutput)` 直接描述目标时刻的文字输出。
M07 没有另一个已接通的 `IPureVisualEffect`、`VisualFrame` 或 `VisualOutput`；分析器中为后续入口保留的元数据识别不等于运行能力。

`DeclareContext` 提供 `Content`、`Scene`、`Parameters`、`Audio`、`Resources`、`Seed`、`InstanceId`、`Random`、`Timeline`、`Indexes` 和 `CanvasSize`。
`Content`／`Scene` 是完整固定源目录；源索引另受实例 `SourceSubscription` 约束。读取目录不会授予输出权限。

`TextFrame` 提供 `Source`、`TimeUs`，以及 `Parameters`、`Audio`、`Resources`、`Random`、`Timeline`、`Indexes`、`CanvasSize` 快捷属性：

| 成员 | 语义 |
| --- | --- |
| `Presentations` | 当前实例绑定内的真实源目标及演唱状态 |
| `Geometry`、`LineGeometry`、`MarkGeometry`、`GroupGeometry` | 生产后端的基础几何；退场对象使用冻结几何 |
| `Clusters` | 由同一整行整形结果投影、按真实目标去重的基础字和 Ruby 绘制单元 |
| `LineLoads` | 正常装载与隐藏后退出副本，见下一节 |
| `Baseline` | 静态继承基线；不含任一作者实例输出 |
| `Binding`、`OutputMode` | 本实例输出范围及当前输出背景语义 |

逐字 `PerformanceSample` 的 `Progress`、`ElapsedUs` 等可为空；`Missing` 不能当成零进度，元素演唱与准备／保留／退场相位分别判断。
基础 `BaseWorldMatrix` 已包含源父组、槽位、单句和进入动画，作者不要再次乘父变换。

接口源码：[PureEffect.cs](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/PureEffect.cs)、
[TextFrame.cs](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextFrame.cs)。

### 准备结果和纯度边界

准备结果可使用递归只读作者 record、受支持的 SDK 值和不可变集合。
禁止把上下文、帧、输出、源查询句柄、委托、可变集合或原生资源保存到准备结果、对象字段或逃逸闭包。
只读字段本身不能把可变内部对象变为只读。

分析器检查真实入口及可达 helper、getter、构造器、初始化器和委托；不允许墙钟、系统随机、环境变量、文件／网络／线程、反射、原生入口、`async`、`unsafe`、`dynamic` 等非固定输入能力。
局部变量和本次调用内局部集合可以变化；不能修改跨调用状态。字符串比较和排序须使用受支持的显式 ordinal 语义。
`GetHashCode()` 不能作为稳定随机或身份来源。

`MKV2001`–`MKV2007` 是纯度／入口错误；`MKV2008`、`MKV2009` 提示逐帧分配和 LINQ 成本。
这些提示不是已实施完整运行预算或任意程序终止性证明。纯度分析也不是安全沙箱。
用宿主 `Random.GetUInt32(streamKey, index)`／`GetUnitDouble(streamKey, index)`；同 seed、实例、流键和 index 的结果不消耗随机游标。

源码：[纯度分析器](../../backend/src/MakoKaraGenerator.Scripting.Compiler/ScriptV2PurityAnalyzer.cs)、
[调用白名单](../../backend/src/MakoKaraGenerator.Scripting.Compiler/ScriptV2PurityAnalyzer.Calls.cs)。

## 3. 正常装载、退出副本和同源重入

`LineLoads` 的每项是 `TextLineLoad`：`Line`、`Target`、`Phase`、`HideTimeUs`、`ExitDurationUs`、`ExitProgress`。
`TextLinePhase.Active` 仍由源布局提供；`ExitReplica` 从真实隐藏时刻开始，几何、槽位、完整父链和静态源基线取隐藏左极限 `hide - 1 µs`。
源演唱进度继续按查询时刻读取，不把冻结几何当成冻结演唱时间。退出副本不参与源布局、起唱计数或基础播放头。

包用顶层 `lineExit: { "durationUs": ... }` 和必须能力 `line.exit` 声明隐藏后寿命，范围为 `0` 到 `3_000_000 µs`。
新句仍按原布局装载，不等待旧句退完。寿命区间为 `[hide, hide + duration)`；终点不再存在。
多个实例各读自己的退出时长及夹到 `[0,1]` 的进度；别的实例若仍需要同一副本，短实例在自身终点后读到 1，零声明实例在副本阶段也读到 1。
窄输出绑定的长声明不能延长范围外装载。

同一个源行被其他行遮挡后再次出现，是新的展示装载，仍是同一个源演唱 occurrence。
当前启用 v2 的生产采样器为重入装载生成独立 `BindingId`；内部可能包含 `/reload/{actualRevealUs}` 后缀。
**作者只传递宿主给出的强类型身份，不解析或拼接该后缀，也不从 `LineId` 推导 Binding。**
这样旧副本与同源新装载才能同时存在，分别输出且不重复起唱计数。

源码：[装载身份](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/LyricSceneSampler.LoadIdentity.cs)、
[退出索引](../../backend/src/MakoKaraGenerator.Scripting/Projection/PresentationIndex.cs)、
[冻结生产几何](../../backend/src/MakoKaraGenerator.Scripting/Projection/FrozenExitGeometry.cs)。

## 4. 能力、目标和输出顺序

顶层 `requires` 是有限能力列表；未知值读取时以 `PackageCapabilityUnsupported` 拒绝。
当前白名单是：`token.affine`、`line.affine`、`group.affine`、`mark.affine`、`cluster.affine`、
`token.opacity`、`group.opacity`、`style.fill`、`style.stroke`、`style.shadow`、`style.glow`、`timeline.index`、`line.exit`。
`timeline.index` 表示包声明依赖索引；当前查询没有逐方法独立能力门禁，不能把它解释为扩大 `SourceSubscription`。

`TextTarget` 是封闭目标类型，现有目标可隐式转换；用真实当前目标，不自己制造编号。

| 目标／取得方式 | `AppendTransform` 必须能力 | `MultiplyOpacity` | `SetFill`／`SetStroke`／`SetShadow`／`SetGlow` |
| --- | --- | --- | --- |
| `TokenFamilyTarget`，`geometry.WithRuby` | `token.affine` | `token.opacity` | 对应 `style.*` |
| `BaseTokenTarget`，`geometry.BaseOnly` | `token.affine` | `token.opacity` | 对应 `style.*`，仅基础字 |
| `LineTarget`，`load.Target` | `line.affine` | 不支持 | 不支持 |
| `GroupTarget`，真实且可写的组 | `group.affine` | `group.opacity` | 不支持 |
| `TextMarkTarget`，真实提示标记 | `mark.affine` | 不支持 | 对应 `style.*` |
| `TextClusterTarget`，`frame.Clusters[i].Target` | `cluster.affine` | 不支持 | 不支持 |

包导出的 `bindingKinds`、输出 `kind`／`target`／`requires` 也必须与入口行为匹配。
读取全局组几何不授予写权；组写范围由真实运行父树和本实例输出绑定决定。`EffectBinding.Tokens` 不授予整行目标。
范围外目标报 `TargetOutsideBinding`，缺能力报 `UndeclaredCapability`，不支持的属性／目标组合报 `PropertyTargetUnsupported`。

`AppendTransform(target, matrix)` 在目标局部逻辑空间追加，先写 A 再写 B，累计为 `B * A`。
矩阵须有限、可逆，并在后端 float 精度下仍然可逆。不同层级由宿主统一合成，不依赖作者遍历顺序推导父链。
`MultiplyOpacity` 只接受有限 `[0,1]` 因子并累乘；组透明度走隔离合成。
`SetFill` 显式选择 `FillState.Unsung`／`Sung`；后写覆盖同一叶属性。
描边、阴影和辉光整体替换，传 `null` 为关闭，未写为继承。

每个实例只读源基线与源几何，不读取前一实例输出；宿主按固定实例顺序组合。
输出是本次调用事务，成功返回后才发布；作者不能调用宿主 `Complete()`／`Invalidate()`，也不能保存 `TextOutput`。

### 基础字、Ruby、字簇和材质限制

Family 同时影响基础字及关联 Ruby；BaseOnly 只改基础字，Ruby 保留父字族外观和变换。
Cluster 是完整行或完整 RubyRun 的真实不可拆整形单元，`RubyId == null` 才表示基础字。
`Utf16Start`／`Utf16Length` 只表达来源关联，不能用来切割字形。
跨 token 连字保留 `OwnerTokens` 的完整所属范围，`CanTransformIndependently == false`；独立变换或外观以
`UnsupportedGlyphPartition`（或该几何的具体诊断）拒绝。可退回完整 `LineTarget` 变换，不能强拆共享连字。

BaseOnly 外观目前只接通可直接绘制的纯色、渐变等现有描述；当独立外观更新、基础来源或绑定材质使用包材质时，
准备帧以 `UnsupportedPartitionMaterial` 拒绝，避免未经实现的分区协调器。
此门禁针对 BaseOnly 外观，不代表所有 BaseOnly 仿射都拒绝包材质。
Cluster 当前只提供真实独立字簇仿射，不支持独立填充、透明度、描边、阴影或辉光；本轮验证范围为纯色／渐变。
Cluster 仿射没有统一包材质拒绝门禁，与包材质协调器、捕获区域和部件绘制叠加的完整支持尚未验收，不作承诺。
包材质仍须属于当前实例固定资源修订；未知资源报 `InvalidMaterialResource`。

源码：[TextOutput](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextOutput.cs)、
[封闭目标和写入记录](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextOutputValues.cs)、
[真实字簇](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextClusters.cs)、
[分区准备](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/PreparedRenderFrame.TextPartitions.cs)。

## 5. 四类固定源索引

入口是 `source.Indexes` 或 `frame.Indexes`，包含 `Timeline`、`Presentations`、`Layout`、`Cursors`。
`source.Timeline`／`frame.Timeline` 与 `Indexes.Timeline` 是同一固定事件索引。
这些查询读取固定输入，允许查看未来真实源信息；不会执行作者历史回放或读取任何实例的最终输出。
不要把查询句柄保存到 `PreparedEffect`，在当前 `Declare`／`Evaluate` 调用内使用。

### 5.1 固定 subscription 与每次 filter

`SourceSubscription.BoundSources`、`.Project`、`.Branch(BranchId)` 在实例装载时固定。
`BoundSources` 由输出绑定解析关联源；显式 Project／Branch 读范围与输出绑定独立。
查询的 `SourceFilter.Project`、`.Branch(BranchId)`、`.Lines(ImmutableArray<LineId>)`、`.Tokens(ImmutableArray<TokenId>)`
始终与固定订阅相交，不能在帧内扩大订阅。ID 必须是真实源 ID，源行与场景 NodeId 不可互换。
纯组、前奏标记可有自己的订阅范围，不为它们制造 TokenId。
全工程音频 hop 是只读输入，不因此变成某行的演唱事件。

### 5.2 TimelineIndex

用 `new TimelineSelector(TimelineEventKind kind, TimelineScope? scope = null, string? sourceText = null)` 创建有限键，
默认 scope 为 Project；支持 `Project`、`Branch`、`Line`、`Token`、`Occurrence`、`Binding`、`Node`、`Cue`。
文本只对有真实源文本的行／token 演唱事件有效，使用精确 ordinal 匹配，不归一化大小写、区域或 Unicode。

| API | 边界／返回 |
| --- | --- |
| `Events` | 固定、稳定排序的真实事件 |
| `Last(selector, timeUs)` | 最后一个 `TimeUs <= timeUs` 的事件，无则 null |
| `Next(selector, timeUs)` | 第一个 `TimeUs > timeUs` 的事件，无则 null |
| `Count(selector)` | 键内全部事件数，包含未来 |
| `Count(selector, timeUs)` | 截至该时刻、包含边界的数量 |
| `Count(selector, TimeRange)` | 半开范围内数量 |
| `Nth(selector, zeroBasedIndex)` | 从零开始，越界 null |
| `Ordinal(selector, EventId)` | 键内从一开始的事件序号，范围外 null |
| `Ordinal(selector, OccurrenceId)` | 真实 token 演唱 occurrence 序号，展示重入不增加次数 |
| `GetRange(selector, TimeRange)` | 半开范围内稳定有序事件 |

固定事件按稳定身份去重；同身份不同语义拒绝。重放原因归一为 Initial，不随 seek 改变事件身份。
真实时间与 Missing 由源决定；准备显示、提示、Ruby 和退出副本不增加 TokenStarted。

### 5.3 SourcePresentationIndex

| API | 返回 |
| --- | --- |
| `Get(BindingId)` | 订阅内一次真实装载的 `SourcePresentationLoad?`，不存在／范围外 null |
| `GetAt(long timeUs, SourceFilter)` | 该时刻可见源装载的 `ImmutableArray<SourcePresentationSample>` |
| `GetRange(TimeRange, SourceFilter)` | 与半开范围相交的完整装载，按揭示边界和装载序号稳定排序 |

`SourcePresentationLoad` 包含揭示边界的 `Binding`、`RevealTimeUs`、可空 `HideTimeUs`、`LoadOrdinal`、
可空 `PreparationRange` 和 `LayoutExitRange`；`Contains(t)` 使用实际半开可见窗。
没有真实隐藏边界时保留 null，不用工程时长填补。`Binding.EnterProgress` 是揭示时的源快照，不是任意 t 的当前进度。
`SourcePresentationSample` 包含 `Load`、`TimeUs`、`DisplayPhase` 和 `LayoutExitProgress`。
准备、演唱／正常显示、源布局保留与 token 的 Current／Sung 分别表达。
`LayoutExitRange` 是源 hide 前已有布局退出时钟，与 hide 后 `TextLineLoad.ExitProgress` 不同；索引不返回退出副本。

### 5.4 SourceLayoutIndex

`BaseSceneGeometry GetBaseAt(long timeUs, SourceFilter filter)` 调用同一生产几何 provider，禁用编辑试样 override。
返回筛选后的 `Lines`、`Tokens`、`LineGeometry`、`Marks`、`MarkGeometry`、`Groups`、`GroupGeometry` 和准确 `TimeUs`。
组包含匹配对象的真实祖先；Project 查询也保留固定 scope 内的空组目录。
无可见源可返回空几何；错误时刻或含作者退出副本的基础帧以 `InvalidSourceLayout` 拒绝。
这些是源基础几何，未叠加任何作者的文字变换或最终播放头。

### 5.5 SourceCursorIndex

| API | 返回 |
| --- | --- |
| `GetSegments(CursorTrackId, TimeRange)` | 单个真实轨道与查询窗相交的 `SourceCursorSegment`，不存在轨道为空 |
| `GetBaseAt(long timeUs, SourceFilter, CursorAnchorMode)` | 该时刻的全部真实 Current `PlaybackCursor`，并发保留多个 |

`SourceCursorSegment` 包含 `TrackId`、完整 `CursorIdentity` 和 `ActiveRange`。
区段是原演唱时间与实际装载可见窗的交集，进度仍按原完整 `Identity.TimeRange` 计算；提前隐藏不能重归一化进度。
逐字缺时间不造区段；整行没有任何 timed token 时可按真实行时间回退，文本映射不匹配时不造逐字路径。
轨道来自真实分支／显示程序；区段保留 gap、并发和每次 Binding，不跨身份插值。
所有区间半开，即使终点是 `long.MaxValue` 也不包含该终点。
锚点只有 `Baseline`／`AdvanceCenter`，使用生产进度路径和基础世界矩阵；缺路径报 `BaseCursorGeometryUnavailable`。

### 5.6 建立、查询成本和预算

这些是当前数值门禁，不是已测得的性能保证：

| 范围 | 当前上限／行为 |
| --- | --- |
| `TimelineIndexBudget` 默认 | 输入事件 200,000（重复输入也计数）、有限键 1,000,000、键桶引用合计 4,000,000；超限 `TimelineIndexBudgetExceeded` |
| 生产源索引预检 | 固定目录与事件合计 200,000 个源值；超限 `SourceIndexBudgetExceeded` |
| 装载计划／实际装载／Cursor 区段 | 各最多 200,000 项 |
| 单次基础布局 | 最多 200,000 个计权源值；含几何、字簇及绘制单元等 |
| 基础场景缓存 | 固定输入会话共享最多 8 个时刻／400,000 个计权值；索引查询也有同量级有界缓存，淘汰后按同一输入重算 |
| 装载身份／退出索引 | 最多 200,000 个边界或装载 |
| 同时退出副本／冻结缓存 | 最多 128 项；缓存淘汰不改变结果 |
| 单个退出副本 | 父链节点最多 256、token 几何最多 4,096、计权绘制单元最多 65,536；超限 `ExitReplicaBudgetExceeded` |

`TimelineIndex` 建立包含输入枚举、排序和有限键桶；键内 Last／Next／Count 定位为 O(log n)，Nth／ordinal 为 O(1)，
范围返回为 O(log n + k)。这不包括作者自己的全目录遍历或 LINQ。
装载／Cursor 区间表通过排序和前缀边界减少扫描；大量重叠区间时仍可能检查较多候选，不承诺任意形状均为 O(log n + k)。

布局查询按真实 t 采样，仍有场景构建、时变布局和几何成本；共享同一 provider、CPU shaping／Ruby 缓存与同刻基础帧，
不等于每次均命中。冷加载还包含时间线、装载代次及显示计划建立；其中候选边界可能逐次采样完整源。
M07 未以固定数量上限证明大型工程加载复杂度或时延达标。没有单独 IPC 查询，也没有从 0 到目标 t 的 v2 回调重放。
建立和查询支持宿主取消；索引随固定会话释放，过期查询报 `SourceIndexExpired`，未注入服务的手工上下文报 `SourceIndexUnavailable`。

源码：[SourceIndexes](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/SourceIndexes.cs)、
[TimelineIndex](../../backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TimelineIndex.cs)、
[生产索引](../../backend/src/MakoKaraGenerator.Scripting/Projection/PureSourceIndexes.cs)、
[共享基础帧缓存](../../backend/src/MakoKaraGenerator.Scripting/Projection/FixedInputGeometryProvider.cs)。

## 6. 任意时间求值与 mixed 会话

纯 v2 会话只在请求时刻执行作者文字求值；固定时间线用于只读索引，不因目标是三分钟或十分钟而推进 120 Hz 作者状态。
seek、乱序和重复请求从固定输入得到同一结构化结果；缓存只影响成本，不能影响语义。
失败或取消不发布部分输出，恢复时按固定输入重建必要运行时。

开发期 mixed v1／v2 工程仍保留 v1 语义网格和有状态成本。
v1 只读文字路径不观察别的作者最终几何时，宿主可跳过网格上的 pure 调用，在最终请求时刻求值一次；
若 v1 覆盖 `OnEvent`、`EvaluateEmitters`、`AdvanceVisuals` 或 `EvaluateVisuals` 并消费最终结果，pure 文字也需在旧边界求值。
因此不能把 pure-only 的 seek 成本结论推广到混合粒子／晚期视觉工程。

确定性范围是相同固定工程／源码及包资源修订、参数／种子、字体／布局、音频分析、画布、输出模式及对应后端执行身份。
不承诺不同字体、驱动、设备、平台间像素逐字节一致；结构化输出、匹配后端像素、真实 GPU、HTTP 和浏览器显示须分别验收。
编译内容键包含源码、入口、编译器／参考集、SDK／引擎和分析规则身份；参数改变不要求重新编译相同源码。
持久编译缓存命中时编译计数可能是零，不能用“必须等于一”证明没有重复编译。

源码：[会话分派](../../backend/src/MakoKaraGenerator.Scripting/Execution/FrameSessionFactory.cs)、
[mixed 推进](../../backend/src/MakoKaraGenerator.Scripting/Execution/FrameSession.cs)、
[编译身份](../../backend/src/MakoKaraGenerator.Scripting/Compilation/ScriptCompilationIdentity.cs)。

## 7. 三个真实 v2 样例

| 包 | 行为 | 当前依赖／限制 |
| --- | --- | --- |
| [WordEmphasis](../../backend/samples/ScriptEffectsV2/WordEmphasis) | 当前字按真实进度放大，唱完按参数回落；基础字和 Ruby 一起变化 | `token.affine`；缺逐字时间保持源外观；共享连字仍遵守独立性门禁 |
| [RepeatEmphasis](../../backend/samples/ScriptEffectsV2/RepeatEmphasis) | 用同文本真实整行演唱事件 ordinal，在分支或工程范围内逐次放大并压暗 | `token.affine`、`style.fill`、`timeline.index`；不以重入装载／副本计次，基线已唱填充须为纯色 |
| [ClusterExit](../../backend/samples/ScriptEffectsV2/ClusterExit) | hide 后按真实基础字／Ruby 单元错峰上移并淡出，寿命 600,000 µs | `cluster.affine`、`line.affine`、`token.opacity`、`line.exit`；跨 token 连字退回完整行移出，保留共享字形 |

每包都有 manifest、C# 与 README，所有源码和资源均按固定包闭包使用。
它们提供当前可编译／运行路径与验收样本，不表示 M11 九样例已重写或全作者链已经通过。

## 8. 作者保存、编译、显式采用

v2 区分最新源码、编译的源码、已采用源码和配置／预览实际在用源码。
保存正确或错误源码都只创建不可变修订；**编译通过不会自动采用**，错误来源也不能通过改配置绕过采用门禁。

1. 导入包／创建草稿，保留 `packageVersion`、`sdkVersion`、当前 `draftRevision` 和源码身份。
2. `POST /api/v1/drafts/{id}/source-revision-jobs` 保存完整 C# 文件集合；包文本资源另走完整包文件入口。
3. `POST /api/v1/drafts/{id}/compilation-jobs`，指定 `expectedDraftRevision` 和需要编译的 `sourceRevision`；可编译保留历史修订。
4. 等任务终态，并从 `GET /api/v1/jobs/{id}/compilation-result` 核对成功、源码、产物、诊断与 `stale`。
5. `POST /api/v1/drafts/{id}/adopt-source-revision` 显式采用已编译通过的来源；未通过以 `AuthorSourceCompilationRequired` 拒绝。
6. 预览捕获固定配置／来源／后端；显式更新预览输入才切到新输入。撤销／重做采用同样选择保留修订，并重新核验当前草稿 CAS。

写入使用当前 `expectedDraftRevision`，陈旧写入返回修订冲突并保留原状态。
`202` 只代表接受，不代表编译、正式验证或渲染完成；旧图仍能显示也不代表新修订已经出图。
源码页、编译诊断和显示帧的来源要分别核对，真实浏览器还要等图片加载／解码并关联帧身份。

当前作者 HTTP 探针和待执行 UI 范围见 [M07 作者验收说明](../../backend/tools/backend-script-render-upgrade/M07_AUTHOR_ACCEPTANCE.md)。
完整请求类型见 [BackendAuthorContracts](../../backend/src/MakoKaraGenerator.Backend.Contracts/BackendAuthorContracts.cs)，
既有通用工作流见 [HTTP 作者参考](../../backend/docs/style-reference/http-authoring.md)。
M07 最终结论由统一构建、回归、匹配发布、真实作者链和加载／seek 测量共同登记；本文不提前放行 M07 或后续节点。
