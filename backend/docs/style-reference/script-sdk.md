# 作者脚本 SDK 参考

本文原按 **2026-11-01、App 提交 `f3fdfdf210c69dcf1af15537e855d5559ec9ce09`、后端 `0.109.0`** 的源码整理；辉光按后续 `0.110.0` 工作树补充。命名空间是 `MakoKaraGenerator.Scripting`，作者引用程序集是 `MakoKaraGenerator.Scripting.Abstractions`；程序集名与命名空间不同。本文覆盖作者可读服务和全部 27 个写入方法重载；字段、构造器、枚举和按文件索引见 [SDK 类型与成员索引](script-sdk-types.md)。这是源码调查，不代表本次执行过编译、渲染或 HTTP 验收。

[版本源](../../Directory.Build.props)；[抽象层项目](../../src/MakoKaraGenerator.Scripting.Abstractions/MakoKaraGenerator.Scripting.Abstractions.csproj)；[包 profile 源](../../src/MakoKaraGenerator.Core/Effects/ProjectEffectsJson.cs)。当前包要求精确匹配 `packageVersion: 2`、`sdkVersion: "1.0.0"`、`shaderProfile: "skia-raster-3.119.4-v1"`。`sdkVersion` 是包协议声明，不等同产品版本，也不能据此推定所有公开类型均有生产执行路径。该 shader profile 也不是作者可借此指定 CPU/Vulkan 的渲染后端开关。

## 1. 入口与七阶段生命周期

入口是公共、非抽象、非开放泛型类，具有公共无参构造，实现 `IKaraokeEffect`；通常继承默认无操作的 `EffectBase`，只覆盖需要的方法。编译器使用分发的 .NET 10 参考集及 SDK、C# 14、Release、nullable、确定性 DLL/portable PDB；不自动解析作者 NuGet 或原生插件。编译缓存由源码、完整入口集合、编译选项和参考集内容身份决定。所有入口先验证；编译失败不能静默启用旧 DLL 或部分包资源。参考 [ScriptCompiler](../../src/MakoKaraGenerator.Scripting/Compilation/ScriptCompiler.cs)、[ScriptModule](../../src/MakoKaraGenerator.Scripting/Loading/ScriptModule.cs)、[CompiledEffectPackage](../../src/MakoKaraGenerator.Scripting/Compilation/CompiledEffectPackage.cs)。

| 完整回调签名 | 可做的事 | 不能混用的阶段 |
|---|---|---|
| `void Initialize(EffectInitializationContext context)` | 每实例初始化一次，读取固定目录、参数、资源、基本几何、随机服务 | 无帧输出；实例重建会重新调用 |
| `void OnEvent(TimelineEvent evt, EventContext context)` | 处理真实事件、更新共享状态，向 `context.Bursts` 登记当前事件 Burst | 不能指定任意出生时间或写文字外观 |
| `void Advance(SimulationStep simulationStep, StateContext context)` | 按 `[StartUs, EndUs)` 推进共享状态，输入和历史截至起点 | 不写外观、不修改源数据 |
| `void Evaluate(FrameContext frame, FrameWriter output)` | 纯求值文字、组、标记的变换、样式、图绑定、材质 uniform | 不推进共享状态，不读未来最终几何 |
| `void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)` | 最终文字几何确定后，给下一固定区间写连续发射描述 | 不读粒子、不推进状态、不改文字 |
| `void AdvanceVisuals(VisualSimulationStep simulationStep, VisualStateContext context)` | 当前文字确定后推进前一区间视觉状态 | 不回写共享状态和文字 |
| `void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)` | 纯呈现最终文字几何后的世界图元 | 不推进状态、不再改文字 |
| `void Dispose()` | 释放作者实例持有的资源；`EffectBase` 提供默认实现 | 不用于发布输出 |

时间单位为整数 `long` 微秒，区间为半开 `[startUs,endUs)`。`SimulationStep`/`VisualSimulationStep` 必须严格正长；`default` 无效。宿主按 120 Hz 整数网格 `floor(n*1_000_000/120)` 和精确事件边界共同推进，不能按显示帧计数。每个边界先处理事件，再 `Evaluate`、发射器、Burst 解析、粒子、`AdvanceVisuals`、视觉呈现；两边界间的请求只纯求值当前文字、解析粒子和视觉，不额外积分。回退请求、输出模式/目标改变或上次失败后的继续请求会重建会话。参考 [FrameSession](../../src/MakoKaraGenerator.Scripting/Execution/FrameSession.cs)、[网格](../../src/MakoKaraGenerator.Scripting/Execution/TimelineFrameSourceFactory.cs)。

所有 writer、`DiagnosticReporter`、`GeometryQueries` 都限定在所属回调内。不要保存后在下一帧使用；`Complete`/`Invalidate` 和 writer 构造器是**宿主接缝**，作者只使用传入实例。自行构造 writer 或返回中立输出记录不会被宿主采纳。纯求值是作者合同；这组 SDK 签名不表示对任意 C# 副作用建立了语言级沙箱。

## 2. 上下文与目录读取

下表列直接成员；继承字段只写一次。数据字段的类型与全部成员见 [类型索引](script-sdk-types.md#1-上下文与帧数据)。

| 上下文 | 作者可读成员 | 语义 |
|---|---|---|
| `ProjectContext` | `Content, Scene, CanvasSize, DurationUs, Audio` | 完整固定工程语义，不泄漏可写 Core 对象；未知时长为 `null` |
| `EffectInitializationContext` | `Project, Parameters, Resources, Random, GeometryQueries, Diagnostics, CancellationToken` | 初始化服务，无当前帧与输出绑定字段 |
| `SemanticContext`（`StateContext`、`EventContext` 的基类） | `Project, Parameters, Resources, Random, History, Binding, Audio, GeometryQueries, Diagnostics, CancellationToken` | 输出范围、历史与源事件订阅相互独立 |
| `StateContext` | `TimeUs` | 等于历史的 `LastBoundaryUs`，是当前推进区间起点 |
| `EventContext` | `Event, TimeUs, EventId, Bursts` | 当前真实事件；不能借历史对象派发新事件 |
| `FrameContext` | `TimeUs, Project, Parameters, Resources, Random, Audio, Baseline, Presentations, Geometry, LineGeometry, MarkGeometry, GroupGeometry, Binding, OutputMode, History, LastSimulationBoundaryUs, GeometryQueries, Diagnostics, CancellationToken` | 基础几何与各实例共同静态继承基线；不读前一个作者实例样式输出 |
| `VisualContext`（发射与视觉上下文基类） | `Project, Parameters, Resources, Random, Binding, Audio, OutputMode, Diagnostics, CancellationToken` | 没有 `GeometryQueries` 和文字 writer |
| `EmissionFrameContext` | `FinalGeometry, Playback, History, TimeUs` | 最终文字之后、粒子之前的固定边界 |
| `VisualStateContext` | `PreviousGeometry, CurrentGeometry, PreviousParticles, Particles, EmissionSchedule, PreviousHistory, History` | 两端已确定的几何、历史及本实例粒子视图 |
| `VisualFrameContext` | `FinalGeometry, Playback, BoundaryGeometry, History, Particles, EmissionSchedule, TimeUs, LastSimulationBoundaryUs, FractionalElapsedUs` | `BoundaryGeometry` 纯帧可为 `null`；分数帧时间差供解析呈现 |

纯帧工厂没有真实语义历史。读取 `FrameContext.History`、`EmissionFrameContext.History`、`VisualFrameContext.History/Particles/EmissionSchedule`（以及依赖它们的派生字段）会报 `TimelineSessionRequired`。需要事件、连续粒子或历史的效果必须使用时间线会话；纯帧 Load 发现作者覆盖 `OnEvent/Advance/AdvanceVisuals` 即报 `TimelineSessionRequired`，无历史时写非空连续发射也会明确失败。

### 完整目录与查询方法

| 服务 | 完整方法签名 | 作用与限制 |
|---|---|---|
| `ContentCatalog` | `SourceToken GetToken(TokenId id)`；`bool TryGetToken(TokenId id, out SourceToken? token)` | 完整源元素，不按当前帧绑定裁剪；缺失的 `Get` 抛 `KeyNotFoundException` |
| 同上 | `SourceLine GetLine(LineId id)`；`SourceBranch GetBranch(BranchId id)`；`SourceRuby GetRuby(RubyId id)` | 保留源顺序、真实注音、可缺失的源时间；注音不是假 Token |
| 同上 | `ImmutableArray<SourceToken> FindTokens(string text, StringComparison comparison = StringComparison.Ordinal)` | 文本全等，只接受 `Ordinal`/`OrdinalIgnoreCase`；不模糊搜索、不受系统区域影响 |
| `SceneCatalog` | `SourceNode GetNode(NodeId id)` | `Nodes, Programs, Clips, LeadIns` 是完整定义目录；实时槽位/附着关系看帧数据 |
| `GeometryQueries` | `BaseSceneGeometry GetBaseAt(long timeUs, SourceFilter filter)` | 非负任意歌曲时刻的**基本布局**；不执行作者效果、递归最终帧或推进未来历史 |
| `TimelineHistory` | `ImmutableArray<EventRecord> GetStarts(TimeRange range, SourceFilter filter)` | 仅已处理历史前缀内的真实 `TokenStarted`；不是全部事件或行起唱查询 |

`ContentCatalog` 的 `Branches/Lines/Tokens/Ruby` 与 `SceneCatalog` 的 `Nodes/Programs/Clips/LeadIns` 可直接枚举。`GeometryQueries` 的 `BaseSceneGeometry` 返回 `TimeUs, Lines, Tokens, LineGeometry, Marks, Groups, MarkGeometry, GroupGeometry`；无可见对象是空结果，不套用编辑试样。阶段结束报 `InvalidGeometryQueryStage`，未注入服务报 `GeometryQueriesUnavailable`。参考 [查询实现](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Queries.cs)。

## 3. 身份、输出绑定、订阅和真实事件

`TokenId/LineId/BranchId` 标识源内容，`BindingId` 标识一次展示装载；同一源可以有多个装载。`OccurrenceId` 是源演唱身份，各装载共用；`NodeId` 标识真实场景节点，不能拿 `LineId` 冒充。`CursorTrackId` 由分支和真实显示程序组成。目标应直接取当前帧的 `WithRuby`、`FrameLine.Target`、`FrameMark.Target` 或 `FrameGroup.Target`，避免拼接/缓存过期装载身份。

| 分类 | 可构造的封闭变体 | 作用 |
|---|---|---|
| `EffectBinding` | `Project()`；`Branch(BranchId Id)`；`Lines(ImmutableArray<LineId> Ids)`；`Tokens(ImmutableArray<TokenId> Ids)`；`Clips(ImmutableArray<ClipId> Ids)`；`Node(NodeId Id, bool IncludeDescendants)` | 实例**输出**范围 |
| `SourceSubscription` | `BoundSources()`；`Project()`；`Branch(BranchId id)` | `OnEvent` 的源订阅，默认随绑定源；扩大订阅不扩大写权限 |
| `SourceFilter` | `Project()`；`Branch(BranchId id)`；`Lines(ImmutableArray<LineId> ids)`；`Tokens(ImmutableArray<TokenId> ids)` | 基本几何/历史的明确查询筛选，无 Clip/Node 变体 |

`Presentations.Groups`、`GroupGeometry` 和最终几何的 `Groups` 是可读目录，读取并不授予组写权限。组仅 `Project` 或显式 `Node` 绑定授权；Branch/Lines/Tokens/Clips 不因存在祖先组就获得权限。`Tokens` 绑定可读所属 `LinesInBinding`，但不能写整行或标记；实际 writer 的允许集合另行筛选。Node 输出匹配当前运行父树，包括实际槽位/提示附着，不能只沿持久父树猜测。参考 [FrameBindingScope](../../src/MakoKaraGenerator.Scripting/Projection/FrameBindingScope.cs)、[组写权限](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Groups.cs)、[行写权限](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Lines.cs)、[源订阅解析](../../src/MakoKaraGenerator.Scripting/Projection/SourceSubscriptionResolver.cs)。

### 13 种事件

| 具体类型/`TimelineEventKind` | 专有可读成员 | 真实含义 |
|---|---|---|
| `TokenStarted`, `TokenCompleted` | 继承 `Source, TokenId, LineId, BranchId, OccurrenceId` | 定时源字起/止；缺逐字时间不制造事件 |
| `LinePerformanceStarted`, `LinePerformanceCompleted` | `Source, LineId, BranchId, PerformanceRange, ProgressSource` | 有定时字取首末字区间，否则取原行时间；不等于行显示区间 |
| `BindingEntered`, `BindingLeft` | `Binding` | 一次真实行装载进入/离开 |
| `ElementVisible`, `ElementHidden` | `Element` | `SceneElementIdentity.Lyric/Mark/Group` 的可见性变化；不等于起唱 |
| `LeadInStarted`, `LeadInStepChanged`, `LeadInCompleted` | `Target, ElapsedMarks, CueId` | 真实前奏及步骤，不制造演唱 Token |
| `CursorTransitionEvent`（kind 为 `CursorTransition`） | `TrackId, From, To, GapUs, TransitionReason` | 同通道真实后继转换；区分正空档、连续、重叠 |
| `AudioFeatureTick` | `AnalysisRevision, Point` | 固定分析 hop，不是设备音频回调 |

公共基类成员：`Kind, TimeUs, EventId, SourceOrder, ReplayReason, BoundaryPriority`；方法 `TimelineEvent WithReplayReason(ReplayReason reason)`；静态比较器 `TimelineEvent.Comparer`。排序先时刻、再结束/离场（0）、进入/前奏/音频（1）、起唱（2）、光标转换（3），再分支/行/元素源序和稳定 EventId。`ReplayReason` 为 `Initial, Seek, Loop, InputsChanged, Export, AnchorQuery`，改变它不改变事件身份；并非每种原因都由某个 UI 操作自动发出。`History.Records` 是截至边界的完整已处理前缀，不按当前实例订阅裁剪。参考 [事件](../../src/MakoKaraGenerator.Scripting.Abstractions/TimelineEvent.cs)、[显示事件](../../src/MakoKaraGenerator.Scripting.Abstractions/DisplayTimelineEvents.cs)、[TimelineProjection](../../src/MakoKaraGenerator.Scripting/Projection/TimelineProjection.cs)。

## 4. 参数、曲线、音频、随机

### 参数与曲线

所有参数 getter 接受 `string id`，返回类型和包声明严格对应；缺失报 `MissingParameter`，类型不符报 `ParameterTypeMismatch`，不会将整数/时长/颜色/文本相互转换。`ParameterValues.Ids` 给出 ordinal 顺序身份；`Empty` 是真实空集。清单声明、默认值和约束在包解析/参数解析阶段处理，不由脚本帧内解析。

| 包参数类型 | SDK 值 | `ParameterValues` 完整 getter |
|---|---|---|
| `number` | `ParameterValue.Number(double value)` | `double GetNumber(string id)` |
| `integer` | `ParameterValue.Whole(long Value)` | `long GetInteger(string id)` |
| `boolean` | `ParameterValue.Toggle(bool Value)` | `bool GetBoolean(string id)` |
| `enum` | `ParameterValue.Choice(string value)` | `string GetEnum(string id)` |
| `duration` | `ParameterValue.Duration(long value)` | `long GetDuration(string id)`，非负整数微秒 |
| `text` | `ParameterValue.Text(string value)` | `string GetText(string id)` |
| `color` | `ParameterValue.Color(ColorRgba value)` | `ColorRgba GetColor(string id)` |
| `curve` | `ParameterValue.Curve(CurveValue value)` | `CurveValue GetCurve(string id)` |
| `asset` | `ParameterValue.Asset(ImageHandle value)` | `ImageHandle GetAsset(string id)` |

参数类型字符串以 [EffectPackageParameterParser](../../src/MakoKaraGenerator.Core/Effects/EffectPackageParameterParser.cs) 为准；`void ValidateResources(ResourceCatalog resources)` 是资源校验辅助，不加载图片。构造 `ParameterValues(IEnumerable<KeyValuePair<string, ParameterValue>> values)` 会复制字典，拒绝空白/重复键和空值；用于图/材质参数时传**该次完整参数集**，不会自动继承上一绑定的参数。

曲线都通过 `double CurveValue.Evaluate(double progress)` 计算，输入必须为有限 `[0,1]`，不夹值。三种构造为 `new CurveValue.Easing(EasingKind kind)`、`new CurveValue.CubicBezier(Point2 first, Point2 second)`、`new CurveValue.ControlPoints(ImmutableArray<Point2> points)`。Easing 是 `Linear/EaseOut/EaseInOut`；贝塞尔 X 控制点在 `[0,1]`，Y 允许过冲；折线首尾为 `(0,0)/(1,1)`、X 严格递增、不自动排序。`Curves.LinearCurve/EaseOutCurve/EaseInOutCurve` 是固定实例，`double Curves.EaseOut(double progress)` 是直接三次 ease-out。过冲曲线用于粒子/样式时仍须满足实际输出的 alpha/尺寸限制。

### 音频

`AudioFeatures.GetAt(long timeUs)` 返回 `AudioFeatureSample`；`GetRange(TimeRange range)` 返回 `AudioFeatureRange`。已分析序列按**左端 hop 保持**，不预测节拍；范围查询只返回区间内真实分析点，不插入边界样本。`AudioFeatureValues` 只有 `BassLevel, Pulse, BeatPhase, BeatConfidence`，没有 FFT、频带数组或音频播放 API。前三种可用性 `NoAudio/NotAnalyzed/AnalysisFailed` 与 `Available/OutsideRange` 明确区分；不可用 `Values=null`，不是伪造零值静音。`BeatPhase` 为 `[0,1)` 或 `null`，无估计时 confidence 必须 0。可读服务元数据 `Availability, Revision, DurationUs`；`AudioFeatures.NotAnalyzed` 与静态 `Unavailable(AudioAvailability availability, string diagnosticCode)` 用于中立值/宿主输入。

控制器输出声明 `requires: ["AudioFeatures"]` 在 `FrameEffectHost.Load` 中调用 `RequireAudio(definition, 0)`，即初始化前检查时刻 0 的已分析音频，不可用报 `RequiredAudioUnavailable`；不是每个目标时刻重做该检查。之后 `GetAt` 越界仍正常返回 `OutsideRange`，作者应处理可用性；不声明时也可自行读取并分支处理。参考 [AudioFeatures](../../src/MakoKaraGenerator.Scripting.Abstractions/AudioFeatures.cs)、[RequireAudio](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Diagnostics.cs)。

### 确定性随机

`uint DeterministicRandom.GetUInt32(string streamKey, ulong index)`；`double GetUnitDouble(string streamKey, ulong index)`。算法属性 `Algorithm` 为 `PCG32-v1`，第二个结果为 `[0,1)`。宿主固定 seed/实例/流键，索引纯读取，不随调用顺序消耗游标或重绘推进。作者用传入 `Random`；公开构造器 `DeterministicRandom(ulong seed, EffectInstanceId instance)` 不会更改宿主实例的随机身份。

## 5. 几何、播放头与中立映射

基本文字几何 `GeometrySample` 的 `AdvanceBounds` 是排版框，`InkBounds` 可为空，`Baseline` 是逻辑基线，`BaseWorldMatrix` 已含父组/槽位/行/进入动画，只映射一次到画布。`DrawUnits` 是整行真实整形单元；`CanTransformIndependently` 决定源字是否能独立输出，不按 UTF-16 硬切共享字簇。`WorldInkCorners` 空墨迹时为空，不拿 Advance 冒充墨迹。

`TokenGeometrySample` 分 `BaseGeometry/FamilyGeometry/Ruby`，族缩放枢轴取**基础字 Advance 中心**。虽存在 `BaseOnly`/`BaseTokenTarget`，当前没有接收它的 writer；公开 Token 输出统一基础字及其注音族。`GroupGeometrySample.ReferenceBounds` 是组自己的参考框，不是后代墨迹联合框；`WorldPivot` 是同一解析器的画布枢轴。

| 类型/方法 | 完整签名 | 用法 |
|---|---|---|
| 点/向量/矩形 | `Point2(double x, double y)`；`Vector2(double x, double y)`；`Rect2(double left, double top, double right, double bottom)` | 有限逻辑坐标；矩形不交换边界，可空；向量 `Zero` |
| 仿射 | `Affine2(double a, double b, double c, double d, double tx, double ty)`；`Point2 Map(Point2 point)`；`Affine2 Compose(Affine2 child)`；`bool TryInverse(out Affine2 inverse)` | `Compose` 为父×子，`default` 是零矩阵，不是恒等 |
| 仿射工厂 | `static Affine2 Identity`；`static Affine2 Translation(double x, double y)`；`static Affine2 ScaleAt(double factor, Point2 pivot)` | 缩放 factor 有限且正；writer 另要求组合矩阵可逆 |
| 进度路径 | `TextProgressPath(float startX, float endX, double baselineY, double centerY)`；`static float XAt(float startX, float endX, double progress)`；`Point2 At(double progress, CursorAnchorMode mode)` | 使用真实裁切的单精度横向插值，纵向明确 `Baseline/AdvanceCenter` |
| 最终播放头 | `PlaybackCursor CursorGeometry.At(CursorAnchorMode mode)`；`ImmutableArray<PlaybackCursor> PlaybackFrame.GetCursors(CursorAnchorMode mode)` | 最终文字变换后，绑定内可有多个并发头；方向已单位化，零长为零 |
| 映射值 | `Point2 AnchorMap.Map(Point2 point)`；`Point2 Inverse(Point2 point)`；`Vector2 MapVectorAt(Point2 point, Vector2 vector)` | 有界中立数学值，域外报错；**没有生产 writer 绑定** |
| 映射构造 | `AnchorMap.Affine(Rect2 domain, Affine2 matrix)`；`AnchorMap.AxisWave(Rect2 domain, WaveDriveAxis driveAxis, double amplitudePx, double wavelengthPx, double phaseRadians, double originPx)` | 可逆仿射/正弦映射，不能单独改变正式文字、图像或光标 |

`FinalSceneGeometry` 由所有文字输出确定后提供，含 `TimeUs, Presentations, Tokens, Lines, Marks, Groups, Playback`，不含 overlay。`PlaybackFrame.IsAvailable=false` 与“当前无人演唱的合法空集合”不同；`Unavailable.GetCursors` 报错。`CursorEndpoint`/`CursorEndpointKind` 是宿主端点查询的中立结果，SDK 上下文没有作者可调用的最终未来端点查询服务；`GeometryQueries.GetBaseAt` 只查询基本布局，不能替代它。参考 [几何](../../src/MakoKaraGenerator.Scripting.Abstractions/GeometrySamples.cs)、[播放头](../../src/MakoKaraGenerator.Scripting.Abstractions/PlaybackGeometry.cs)、[AnchorMap](../../src/MakoKaraGenerator.Scripting.Abstractions/AnchorMap.cs)、[端点](../../src/MakoKaraGenerator.Scripting.Abstractions/CursorEndpoint.cs)。

## 6. 全部作者 writer 方法

下列均为 `void`，仅指定阶段有效。`FrameWriter` 没有 `Mappings`、任意画布绘制、字体、字号、排版、源时间或场景树写入 API。以下按**重载**计数：Transforms 4、Style 11、Materials 2、ImageEffects 4、Bursts 1、Emitters 1、Overlay 4，共 27。

### Evaluate：Transforms（4）

| 完整签名（`output.Transforms`） | 目标/能力 | 限制 |
|---|---|---|
| `void ScaleTokenFamily(TokenFamilyTarget target, double factor, Pivot pivot)` | 当前元素族；`Transforms` | factor 有限且 `>0`，pivot 当前只有 `BaseAdvanceCenter`；非恒等需独立字簇；只缩放，不重排邻字 |
| `void ApplyAffine(TextMarkTarget target, Affine2 matrix)` | 当前真实提示标记；`Transforms` | 有限可逆；不改源节点 |
| `void ApplyAffine(LineTarget target, Affine2 matrix)` | 当前完整真实行；`Transforms` | 注音/后代跟随；Tokens 绑定不授权整行 |
| `void ApplyAffine(GroupTarget target, Affine2 matrix)` | 真实组；`Transforms` | 仅工程/显式 Node 授权；隐藏组也不能绕过校验 |

同目标按调用顺序组合，恒等不发布无变化输出；组合溢出或不可逆报 `InvalidTransform`。元素族不独立时用真实 `DiagnosticCode` 或 `UnsupportedGlyphPartition` 拒绝。参考 [FrameWriter](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.cs)、[行](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Lines.cs)、[组](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Groups.cs)、[标记](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Marks.cs)。

### Evaluate：Style（11）

| 完整签名（`output.Style`） | 目标与效果 |
|---|---|
| `void SetUnsungMaterial(TokenFamilyTarget target, MaterialValue material)` | 元素族未唱材质 |
| `void SetSungMaterial(TokenFamilyTarget target, MaterialValue material)` | 元素族已唱材质 |
| `void SetStroke(TokenFamilyTarget target, StrokeValue? stroke)` | 元素族两态一起替换为单层描边，`null` 关闭 |
| `void SetShadow(TokenFamilyTarget target, ShadowValue? shadow)` | 元素族两态一起替换阴影，`null` 关闭 |
| `void MultiplyOpacity(TokenFamilyTarget target, double factor)` | 乘元素族静态样式 alpha |
| `void SetUnsungMaterial(TextMarkTarget target, MaterialValue material)` | 标记 Pending 材质 |
| `void SetSungMaterial(TextMarkTarget target, MaterialValue material)` | 标记 Elapsed 材质，不制造逐字演唱进度 |
| `void SetStroke(TextMarkTarget target, StrokeValue? stroke)` | 标记两态一起替换为单层描边，`null` 关闭 |
| `void SetShadow(TextMarkTarget target, ShadowValue? shadow)` | 标记两态一起替换阴影，`null` 关闭 |
| `void MultiplyOpacity(TextMarkTarget target, double factor)` | 乘标记静态 alpha，动态附着 alpha 单独应用一次 |
| `void MultiplyOpacity(GroupTarget target, double factor)` | 组孩子先合成，再统一乘组 alpha；不改每个孩子样式 |

全部要求 `Style` 能力和真实允许目标。元素族的**实际变化**受独立字簇限制；恒等/不变内容不强制拆分字簇。材质不能 `null`，关闭填充可用透明 Solid。Set 顺序覆盖，opacity 顺序相乘，乘数有限且非负，宿主最终校验合成结果 `[0,1]`；不要假设 `factor>1` 必定合法。跨实例保持工程实例顺序，各实例读取共同静态 `Baseline`。

材质值工厂：`MaterialValue.Solid(ColorRgba color)`、`Gradient(GradientValue gradient)`、`PackageExport(MaterialHandle handle, ParameterValues parameters)`。`ColorRgba(double red,double green,double blue,double alpha)` 为 `[0,1]` 直乘 sRGB；`MultiplyRgb(double factor)` 不改 alpha，结果不能超出颜色范围。描边 `StrokeValue(double widthPx, MaterialValue material, StrokeJoin join = StrokeJoin.Round)`（画在字形外侧）；阴影 `ShadowValue(ColorRgba color, double opacity = 1, double angleDegrees = 45, double distancePx = 4, double blurRadiusPx = 8)`。角度 0 向右、90 向下，长度单位逻辑像素。

两态外观（0.112.82 起，P3 第 6 步）：基线 `StyleView` 的 `Unsung`、`Sung` 各是一套完整的 `AppearanceView`（`Fill`、由内向外的 `Strokes`、`Shadow`、`Glow`），已唱跟随未唱时取同一值；`Material(MaterialSlot)` 按 `UnsungFill`／`SungFill`／`UnsungStroke`／`SungStroke` 取材质。v1 的 `SetStroke`／`SetShadow` 两态一起写（描边为单层），已唱随之跟随，与基线相同的写入视同未写；`SetUnsungMaterial`／`SetSungMaterial` 只写本态填充。不再报 `SungStrokeMaterialPending`。按态写描边、阴影、辉光与扫字样式只在 v2 `TextOutput` 提供，见 [v2 参考](../../../docs/style-reference/script-sdk-v2.md)。

`AppearanceView.Glow` 提供可空、只读的 `GlowValue` 静态基线；`GlowValue` 的颜色、透明度、模糊和外扩均只读。v1 writer 没有辉光写入，v2 用 `SetGlow`。静态编辑和继承见[辉光参考](glow.md)。

脚本生产样式接受 Linear/Radial、Canvas/Line/Glyph、LinearRgb/Hsv（重合色标即硬边；`Glyph` 为按单字，每个字簇以自己的字框为参考框）；Oklch 报 `UnsupportedEffectGradient`，不能按 Core 静态样式的历史回退说明推定脚本会降级。0.112.83 起坐标枚举删去 `Progress`，并新增 `GradientValue.Bands(colors, boundaries, …)` 按「颜色＋分界」构造硬边分层渐变，例如 `GradientValue.Bands([red, white, blue], [.4, .6])`。包材质还需固定包修订、fill/stroke 区域声明、参数、ColorOnly 和资源闭包校验。参考 [Style writer](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Style.cs)、[StyleUpdate](../../src/MakoKaraGenerator.Scripting.Abstractions/StyleUpdate.cs)、[实际样式转换](../../src/MakoKaraGenerator.Rendering.Abstractions/StaticStyleProjection.cs)、[GradientSupport](../../src/MakoKaraGenerator.Core/Projects/GradientSupport.cs)。

### Evaluate：Materials（2）

| 完整签名（`output.Materials`） | 目标/能力 |
|---|---|
| `void SetUniform(MaterialHandle handle, TokenFamilyTarget target, MaterialSlot slot, string nodeId, string name, UniformValue value)` | 元素族当时已安装材质；`Style` |
| `void SetUniform(MaterialHandle handle, TextMarkTarget target, MaterialSlot slot, string nodeId, string name, UniformValue value)` | 标记当时已安装材质；`Style` |

`slot` 为 `UnsungFill/SungFill/Stroke`。该调用**不安装材质**：选定部位必须已是同 handle 包材质，节点必须是真实 `RuntimeShader` 节点，uniform 名、类型、显式数组长度都匹配；所有中间写入均验证，后续 Set 不能掩盖先前无效 uniform。替换该部位材质会清除它先前覆盖。同名后写覆盖，其他节点/名字保持。

`UniformValue` 七种工厂是 `Float(double value)`、`Float2(double x,double y)`、`Float3(double x,double y,double z)`、`Float4(double x,double y,double z,double w)`、`Int(int value)`、`Color(ColorRgba value)`、`Matrix3x3(double m11,double m12,double m13,double m21,double m22,double m23,double m31,double m32,double m33)`；另有 `Array(IEnumerable<UniformValue> values)`，须同类型、非空、非嵌套，长度 1 也区别于非数组。Float4 不等于 Color；矩阵按行传入，可不可逆。SDK 值先要求 double 有限，实际 shader 再拒绝 float32 溢出或非零下溢。`UniformValues`/`GraphUniformValues` 是值集，不是另一条可写 native shader API。参考 [MaterialWriter](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Materials.cs)、[UniformValue](../../src/MakoKaraGenerator.Scripting.Abstractions/UniformValue.cs)、[材质验证](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Materials.cs)、[ABI 验证](../../src/MakoKaraGenerator.Rendering.Abstractions/MaterialUniformValidation.cs)。

### Evaluate：ImageEffects（4）

| 完整签名（`output.ImageEffects`） | 正式执行状态 |
|---|---|
| `void Bind(LineTarget target, GraphHandle graph, ParameterValues parameters)` | 可用；当前真实整行，Tokens 绑定不授权 |
| `void Bind(TokenFamilyTarget target, GraphHandle graph, ParameterValues parameters)` | 可用；必须独立字簇，注音捕获依包配方 |
| `void Bind(TextMarkTarget target, GraphHandle graph, ParameterValues parameters)` | 可用；当前真实标记 |
| `void Bind(GroupTarget target, GraphHandle graph, ParameterValues parameters)` | **v1 writer 可记录授权组，准备输出阶段拒绝** `GroupImageEffectUnsupported`；v2 组能力见新参考 |

全部要求 `ImageEffects` 能力和本实例固定修订的图资源。单实例同目标最后一次 Bind **整体替换**（含参数），跨实例按工程顺序串接。绑定的图导出自身也须允许实例 binding kind 和目标，当前目标只能有一个具名 ImageGraph 输出；非空依赖/额外需求、同类别多输出、旁路不一致均不猜测执行。只接受 `ColorOnly`，`MappedWarp/ImageOnly` 报 `PackageGraphMappingPending`。直接 graph 导出也先经独立门禁，组更早拒绝。参考 [writer](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.ImageEffects.cs)、[包输出验证](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.PackageOutputs.cs)、[直接图门禁](../../src/MakoKaraGenerator.Scripting/Compilation/DirectGraphEffectValidation.cs)、[Skia 正式画布](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.cs)、[执行器](../../src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageEffects.cs)。图 JSON、shader ABI 和捕获配方是包资源合同，不是 SDK 对任意图像/文件路径的访问接口。

### OnEvent：Bursts（1）与 EvaluateEmitters：EmitterWriter（1）

| 完整签名 | 作用域与替换规则 |
|---|---|
| `void BurstWriter.Queue(EmitterId emitterId, BurstDescriptor descriptor)` | 仅 `context.Bursts`，要求 `Overlay`；同真实事件同 emitter 最后登记生效；时间和 EventId 由当前事件固定 |
| `void EmitterWriter.Set(EmitterId id, CursorEmitterDescriptor descriptor)` | 仅 `EvaluateEmitters` 传入 writer，要求 `Overlay`；同边界同 id 最后描述生效，省略即下一固定区间不发射 |

`BurstDescriptor : ParticleDescription` 增加 required `Count`（非负 int，0 合法）与 `Anchor`。锚点为 `BurstAnchor.SourceToken(TokenId Id)`、`SourceLine(LineId Id)`、`TextMark(NodeId Id)`、`BindingCursors(CursorAnchorMode Mode)`。每个匹配装载独立得到 Count 个粒子；固定源锚点必须真实、在输出范围且事件边界存在最终几何，否则报 `UnknownBurstSource/BurstTargetOutsideBinding/BurstAnchorUnavailable`。绑定内播放头无人演唱可合法为空。扩大订阅不会扩大 Burst 范围。

连续发射 `CursorEmitterDescriptor` 使用绑定内全部真实头，每条头独立累计 `RatePerSecond`。必须指定 `AnchorMode`。`TransitionMode.Jump` 换点，旧粒子保留出生位置；`Bridge` 在允许正空档末段飞行，受 `BridgeDurationUs/BridgeGapLimitUs/BridgeCurve` 限制，不冒充 Current 演唱。`ImageOnlyPolicy.SemanticCursor` 可用；`IncludeInTargetCapture` 虽有枚举值，`Validate()` 直接报 `EmitterTargetCapturePending`。发射器不是每次 Set 即出生，出生由固定时间线配额控制。参考 [Burst 解析](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Bursts.cs)、[连续发射](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Emitters.cs)、[描述](../../src/MakoKaraGenerator.Scripting.Abstractions/CursorEmitterDescriptor.cs)、[桥接](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Bridges.cs)。

### EvaluateVisuals：Overlay（4）

| 完整签名（`output.Overlay`） | 一项的预算计数 |
|---|---|
| `void DrawDots(OutputId batchId, ImmutableArray<Dot> dots, OverlayLayer layer)` | 每 Dot 一项，`Dot` 半径 `>0` |
| `void DrawSegments(OutputId batchId, ImmutableArray<Segment> segments, OverlayLayer layer)` | 每 Segment 一项；平端、宽度 `>0`，零长合法但无面积 |
| `void DrawTrails(OutputId batchId, ImmutableArray<Trail> trails, OverlayLayer layer)` | 每 TrailPoint 一项；每 Trail 至少 2 点，点宽可为 0 |
| `void DrawSprites(OutputId batchId, ImmutableArray<Sprite> sprites, OverlayLayer layer)` | 每 Sprite 一项；固定包图片、可逆仿射、图内非空归一化裁片 |

全部要求 `Overlay`。坐标为最终画布逻辑空间，不能再乘文字矩阵。`BehindText` 在背景后/全部文字前；`AboveText` 在全部文字、注音和提示后。批次名在实例内**跨图元类型唯一**；同帧重复报 `DuplicateOverlayBatch`，与本实例仍存活粒子发射输出同名也拒绝。空数组合法，`default(ImmutableArray<...>)` 无效；透明项照样计预算。批次按工程实例序、再 ordinal ID 排序；宿主可按实例配置覆盖图元层。

构造器：`Dot(Point2 position, double radius, ColorRgba color)`、`Segment(Point2 start, Point2 end, double width, ColorRgba color)`、`TrailPoint(Point2 position, double width, ColorRgba color)`、`Trail(ImmutableArray<TrailPoint> points)`、`Sprite(ImageHandle image, Affine2 unitToWorld, ColorRgba tint, Rect2? source = null)`。Sprite 完图裁片默认 `(0,0,1,1)`，映射单位方形到世界，可旋转/镜像/非等比缩放；Tint 白保持原图，直乘 RGB/alpha 后由后端预乘一次。Trail 保留点序，重复位置取最后截面、严格反向断带。参考 [VisualWriter](../../src/MakoKaraGenerator.Scripting.Abstractions/VisualWriter.cs)、[矢量](../../src/MakoKaraGenerator.Scripting.Abstractions/VectorOverlays.cs)、[精灵](../../src/MakoKaraGenerator.Scripting.Abstractions/SpriteOverlays.cs)、[视觉宿主](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Visuals.cs)。

## 7. 粒子视图、出生记录与预算

`ParticleDescription` 的完整公共字段：required `LifetimeUs, SizePx, Color, SeedStreamKey`；可选 `Velocity, SpreadPx, TrailDurationUs, Rendering, Layer, Motion`。寿命严格正长微秒，尺寸是严格正**直径**，散布为非负半宽，速度逻辑像素/秒，拖尾窗口微秒。`ParticleMotion` 提供恒加速度（像素/秒²）、年龄透明度/尺寸 `ParticleAgeCurve`；`ParticleAgeCurve(double start,double end,CurveValue curve).Evaluate(double age)` 按年龄解析。默认 `ParticleMotion.ConstantVelocity`、曲线 `ConstantOne`。

`ParticleRenderMode.Dots` 自动绘制圆点/点状拖尾；`Custom` 只关闭自动绘制，宿主模拟与预算继续，作者从本实例 `Particles` 输出线段/带/精灵。粒子一旦出生不随文本重挂或移动；绑定离场后存活粒子仍保留。`ParticleSample` 有稳定 `Id`、真实 `Source`、`BirthUs/DeathUs`、`BirthPosition/Position`、实际曲线 `SizePx/Color`、`Layer`、固定采样 `Trail`；Burst 还有 `EventId`，连续出生为 null。`TrackId` 只适用于真实光标/桥接来源，不给固定文字伪造轨道。

`EmissionSchedule` 是最近完成区间的全部真实出生，含期末已死亡粒子；分数帧保持该记录，不是长期粒子历史。`ParticleBirthSample` 携带出生时描述、位置和来源。出生/存活/拖尾都是宿主时间线计算，不以作者 Evaluate 的次数累加。默认预算：`VisualOutputBudget(100_000)` 绘制项；`ParticleSimulationBudget(liveParticleLimit: 20_000, trailSampleLimit: 200_000, emissionRecordLimit: 100_000)`。正式预算由工程持久配置决定，跨实例合计，超限显式失败、不静默裁减。参考 [粒子描述](../../src/MakoKaraGenerator.Scripting.Abstractions/ParticleDescription.cs)、[粒子视图](../../src/MakoKaraGenerator.Scripting.Abstractions/ParticleSample.cs)、[出生记录](../../src/MakoKaraGenerator.Scripting.Abstractions/EmissionSchedule.cs)、[模拟](../../src/MakoKaraGenerator.Scripting/Particles/ContinuousParticleSimulation.cs)。

## 8. 资源、诊断与包声明

### 固定资源

`ResourceCatalog.GetMaterial(string exportId)`、`GetGraph(string exportId)`、`GetImage(string assetId)` 分别返回 `MaterialHandle/GraphHandle/ImageHandle`；三种 `bool Contains(... handle)` 重载验证真实包归属。`PackageId/Revision` 只读，空目录两者为 null。句柄的 `Identity` 包含 `PackageId, Revision, ExportId`，修订必须完整小写 `sha256:` + 64 hex。句柄构造器为 internal，作者通过目录/asset 参数取得，不用路径创建 native 图片或 shader。材质/图参数里的图片与 Sprite 都须属于本实例固定包修订；同名其他修订无效。资源缺失报 `MissingResource`，不按其他类型回退。参考 [ResourceCatalog](../../src/MakoKaraGenerator.Scripting.Abstractions/ResourceCatalog.cs)。

### 诊断

`void DiagnosticReporter.Report(DiagnosticSeverity severity, string code, string message, SourceLocation? location = null)`，severity 为 `Info/Warning/Error`。`SourceLocation(string path,int line,int column)` 使用非空路径和从 1 开始的行列。宿主附加 `RuntimeDiagnostic(InstanceId, ArtifactHash, Stage, TimeUs, Diagnostic)` 与可选 `PackageIdentity`。`Error` 诊断在回调结束时使该阶段失败；不是仅记录日志后继续。`OperationCanceledException` 保持取消，不转脚本失败。阶段结束后 reporter 失效；`EffectOutputException(string code,string message)` 供作者输出具名失败，宿主补实例/时刻/阶段。参考 [诊断类型](../../src/MakoKaraGenerator.Scripting.Abstractions/EffectDiagnostics.cs)、[执行包装](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Diagnostics.cs)。

### 声明门禁与实际支持矩阵

清单顶层包括 `packageVersion, packageId, name, version, sdkVersion, shaderProfile, files, parameters, exports`。文件闭包为每项声明 `path/role/sha256`。当前消费代码识别 `csharp/shader/graph/shaderProgram/image/font/svg/svgResource`；shader/graph/image 引用检查具体角色，`asset` 用于样例 README 等不作为图片资源的附属文件。`EffectPackageContents` 对 role 只要求非空，并非封闭枚举。SDK2 的 `svg` 正文与 `svgResource` 描述由固定闭包解析，资源目录提供 typed vector／atlas 句柄；作者包文本入口将两者纳入完整可编辑集合，真正 SVG 语义仍由编译前 validator 与正式运行资源准备校验。`font` 有真实宿主消费路径：[ProjectRenderSessionFactory](../../src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs) 收集该角色字节建立 `SkiaTextLayoutEngine` 的固定字体目录；作者 SDK 仍没有字体加载 writer 或字体句柄，不能由此在回调中切换字体。路径、闭包和 SHA-256 验证后，controller 用 `entryType`，graph/material 用固定图资源。导出限定参数 ID、bindingKinds 和有身份的 outputs；material 另声明 regions/fallback 等。完整格式以 [包读取](../../src/MakoKaraGenerator.Core/Effects/EffectPackageContents.cs)、[导出解析](../../src/MakoKaraGenerator.Core/Effects/EffectPackageDefinition.Parser.cs)、[样例清单](../../samples/ScriptEffects/WordEmphasis/manifest.json) 为准。

| 声明/类型 | 当前执行结果 |
|---|---|
| controller `VisualTransform` → `tokenFamily/line/group/textMark` | 对应真实 Transforms writer 可执行 |
| controller `StyleModulation` → `tokenFamily/textMark/group` | 元素/标记样式及组透明度可执行；`line` 无样式 writer，报 `PackageWriterPending` |
| controller `Overlay` → `tokenFamily/line/textMark` | 发射器/Burst/视觉批次可用，输出 ID 必须与清单 Overlay ID 匹配；`group` 组合未提供 |
| controller `ImageGraph` → `tokenFamily/line/textMark` | 真实 Bind + ColorOnly + 完整图/参数/配方门禁 |
| `ImageGraph/group` | v1 控制器/writer 描述在准备输出阶段拒绝；v2 双能力声明已开放，见 [组合同](../scripting-group-image-composition-contract.md) |
| `GeometryMapping`、`AnchorMap` | parser/中立类型存在，生产无 writer，`PackageWriterPending`；无 `Mappings` 属性 |
| `MappedWarp/ImageOnly` | 图数据可描述，实际包绑定/材质/正式图路径拒绝 `PackageGraphMappingPending` |
| `requires` | 控制器仅 `AudioFeatures`，其他需求报 `PackageRequirementUnsupported`；图/材质额外需求仍 `PackageOutputPlanPending` |
| `dependsOn`/同类别多个具名图输出 | parser 可校验拓扑，不等于跨输出执行；正式路径 `PackageOutputPlanPending` |
| `bypass` | parser 仅 ImageGraph、显式 identity mapping；graphInput/materialFallback 仍按实际图/材质可用性及匹配规则执行，不是通用跨输出旁路 |
| target capture / owned overlay | `IncludeInTargetCapture` 明确拒绝；画布 Overlay 不因此进入目标图捕获；v2 父组捕获/重组及直接 owner Below 排除见组合同 |

帧输出会再核对清单类别/目标；不能只用较宽 capability flag 越过导出声明。Overlay ID 会精确核对；图同目标后写替换不允许绕过图导出声明、资源修订或参数校验。参考 [CreateEffect](../../src/MakoKaraGenerator.Scripting/Compilation/CompiledEffectPackage.cs)、[帧输出校验](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.PackageOutputs.cs)、[材质门禁](../../src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Materials.cs)。

## 9. 可复制的最小脚本

下面类可放入包的 `csharp` 文件；清单 `entryType` 为 `Pulse`，output 声明 `VisualTransform/tokenFamily`，bindingKinds 包含所使用的范围。示例无参数、资源和历史需求；应将清单文件 SHA-256 更新为实际源码摘要。此处按真实签名核对，未在本次运行编译器。

```csharp
using MakoKaraGenerator.Scripting;

public sealed class Pulse : EffectBase
{
    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var p = token.Performance;
            if (p.ProgressSource != ProgressSource.Token || !p.IsCurrent
                || p.Progress is not { } progress)
                continue;
            output.Transforms.ScaleTokenFamily(
                token.WithRuby, 1 + .2 * Curves.EaseOut(progress),
                Pivot.BaseAdvanceCenter);
        }
    }
}
```

若要兼容共享字簇，先在 `frame.Geometry` 中按 `WithRuby` 找到对应 `BaseGeometry.CanTransformIndependently` 并选择整行效果或跳过，不能靠上述示例证明任意字体/语言均可逐字拆分。

自定义世界图元最小写法（清单 output ID 必须为 `marker`，kind `Overlay`、target 如 `line`；同一个实例不得同时用 `marker` 作存活发射器 ID）：

```csharp
using MakoKaraGenerator.Scripting;

public sealed class CursorMarker : EffectBase
{
    public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
    {
        if (!frame.Playback.IsAvailable) return;
        var cursors = frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter);
        var dots = System.Collections.Immutable.ImmutableArray.CreateBuilder<Dot>();
        foreach (var cursor in cursors)
            dots.Add(new Dot(cursor.Position, 3, new ColorRgba(1, .5, 0, 1)));
        output.Overlay.DrawDots(new OutputId("marker"), dots.ToImmutable(),
            OverlayLayer.AboveText);
    }
}
```

此简例收集全部并发头后一次 `DrawDots`；不可为每个头重复写同名批次。

## 10. 现有九个样例包

以下索引来自当前实际 manifest/源码；目录 README 的旧桌面入口和历史验收日期不构成当前 App 的操作要求或本次验证结果。

| 目录/导出 | 现有用途 | 关键限制/源码入口 |
|---|---|---|
| [WordEmphasis](../../samples/ScriptEffects/WordEmphasis/) / `word-emphasis` | 当前字放大与唱后回落 | `ScaleTokenFamily`，不改排版，受字簇边界限制 |
| [RepeatEmphasis](../../samples/ScriptEffects/RepeatEmphasis/) / `repeat-emphasis` | 源重复计数与样式强调 | `RequireSungSolidColor()` 要求已唱基线为 Solid；渐变/包材质报错 |
| [CursorTrail](../../samples/ScriptEffects/CursorTrail/) / `cursor-trail` | 连续光标圆点与固定拖尾 | Jump/Bridge，SemanticCursor，真实时间线会话 |
| [VectorTrail](../../samples/ScriptEffects/VectorTrail/) / `vector-trail` | 线段/带状粒子拖尾 | Custom 粒子视图与统一预算，不按 UI 帧累计 |
| [SpriteTrail](../../samples/ScriptEffects/SpriteTrail/) / `sprite-trail` | 固定图片精灵拖尾 | `assets/spark.png` 必须包含在固定闭包内 |
| [EventBurst](../../samples/ScriptEffects/EventBurst/) / `event-burst` | 起唱/标记事件 Burst | 真实锚点与事件边界；count 是包 number 后显式检查为非负 int |
| [CustomInk](../../samples/ScriptEffects/CustomInk/) / `custom-ink` | 纯资源波纹材质 | fill/stroke 区域，unmasked，不依赖背景，透明仍执行 |
| [LineGlitch](../../samples/ScriptEffects/LineGlitch/) / `line-glitch` | 直接整行图 | ColorOnly 图像位移，不改变文字几何或光标，不支持 Token/组映射 |
| [GlassInk](../../samples/ScriptEffects/GlassInk/) / `glass-media`, `glass-backdrop` | 媒体背景/真实下层材质 | 透明输出跳过背景依赖并使用包内 Solid fallback，保留诊断 |

进一步查询可从 [全部 SDK 文件索引](script-sdk-types.md#8-按源码文件的完整索引) 进入；[旧 SDK 合同](../scripting-sdk-contract.md) 仅作历史设计背景，支持状态以本文所链当前实现为准。
