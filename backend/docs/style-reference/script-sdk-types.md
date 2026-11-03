# 脚本 SDK 类型与完整成员索引

与 [作者 SDK 主文](script-sdk.md) 使用同一基线：原始 App `f3fdfdf210c69dcf1af15537e855d5559ec9ce09`、后端 `0.109.0`，辉光按后续 `0.110.0` 工作树补充。本索引覆盖 `Scripting.Abstractions` 的 **69 个 `.cs` 文件、209 个不重复顶层公共类型**。表中嵌套类型均写所属类型；位置记录的主构造参数同时就是公开属性。集合均为不可变值；这里只列源码显式声明的 SDK 成员，不逐一重复 C# record/struct 自动合成的相等、打印、复制和解构成员。普通公共实例构造器不等于宿主会采纳该实例。

阅读标记：**输入**是宿主提供的上下文/目录/结果；**作者值**可供作者组合传入 writer；**宿主接缝**是公开但由宿主创建/收取/关闭的通道；**中立**表示存在可计算/描述类型，未因此获得正式输出通路。全部写入方法的完整重载、能力和执行限制见 [主文 §6](script-sdk.md#6-全部作者-writer-方法)。每个源文件的链接见 [§8](#8-按源码文件的完整索引)。

## 1. 上下文与帧数据

| 类型 | 显式公共成员（含继承关系） | 性质 |
|---|---|---|
| `IKaraokeEffect : IDisposable` / `EffectBase : IKaraokeEffect` | `Initialize(EffectInitializationContext)`、`OnEvent(TimelineEvent,EventContext)`、`Advance(SimulationStep,StateContext)`、`Evaluate(FrameContext,FrameWriter)`、`EvaluateEmitters(EmissionFrameContext,EmitterWriter)`、`AdvanceVisuals(VisualSimulationStep,VisualStateContext)`、`EvaluateVisuals(VisualFrameContext,VisualWriter)`、`Dispose()`，均返回 `void`；基类均 `virtual` 默认无操作 | 作者入口 |
| `ProjectContext` | `ContentCatalog Content`；`SceneCatalog Scene`；`Vector2 CanvasSize`；`long? DurationUs`；`AudioFeatures Audio` | 固定输入 |
| `EffectInitializationContext` | `GeometryQueries GeometryQueries`；`ProjectContext Project`；`ParameterValues Parameters`；`ResourceCatalog Resources`；`DeterministicRandom Random`；`CancellationToken CancellationToken`；`DiagnosticReporter Diagnostics` | 输入，初始化不写帧 |
| `SemanticContext` | `GeometryQueries GeometryQueries`；`ProjectContext Project`；`ParameterValues Parameters`；`ResourceCatalog Resources`；`DeterministicRandom Random`；`TimelineHistory History`；`EffectBinding Binding`；派生 `AudioFeatures Audio`；`DiagnosticReporter Diagnostics`；`CancellationToken CancellationToken` | 共享阶段输入基类 |
| `StateContext : SemanticContext` | `long TimeUs` | 从 `History.LastBoundaryUs` 派生 |
| `EventContext : SemanticContext` | `BurstWriter Bursts`；`TimelineEvent Event`；派生 `long TimeUs`、`EventId EventId` | 当前真实事件 |
| `FrameContext` | `GeometryQueries GeometryQueries`；`TimelineHistory History`；`long LastSimulationBoundaryUs`；`DiagnosticReporter Diagnostics`；`long TimeUs`；`ProjectContext Project`；`ParameterValues Parameters`；`ResourceCatalog Resources`；`DeterministicRandom Random`；`AudioFeatures Audio`；`BaselineStyles Baseline`；`FramePresentations Presentations`；`ImmutableArray<TokenGeometrySample> Geometry`；`ImmutableArray<LineGeometrySample> LineGeometry`；`ImmutableArray<TextMarkGeometrySample> MarkGeometry`；`ImmutableArray<GroupGeometrySample> GroupGeometry`；`EffectBinding Binding`；`OutputMode OutputMode`；`CancellationToken CancellationToken` | Evaluate 输入，历史可拒绝读取 |
| `FramePresentations` | `ImmutableArray<FrameToken> TokensInBinding`；`ImmutableArray<FrameLine> LinesInBinding`；`ImmutableArray<FrameMark> MarksInBinding`；`ImmutableArray<FrameGroup> Groups` | 当前真实装载；组可读不等于可写 |
| `FrameToken` | `TokenId TokenId`；`BranchId BranchId`；`LineId LineId`；`BindingId BindingId`；`OccurrenceId? OccurrenceId`；`string SourceText`；`PerformanceSample Performance`；`TokenFamilyTarget WithRuby`；`BaseTokenTarget BaseOnly` | 元素族输入，BaseOnly 当前无 writer |
| `FrameLine` | 主构造：`FrameLine(LineId LineId, BranchId BranchId, BindingId BindingId, NodeId NodeId, NodeId ParentId, ProgramId ProgramId, ClipId? ClipId, CueId? CueId, DisplayPhase DisplayPhase, double EnterProgress)`；派生 `LineTarget Target` | 行装载输入 |
| `FrameMark` | `FrameMark(TextMarkTarget Target, FrameLine TargetLine, NodeId ParentId, string Text, bool IsElapsed, double AnimationOpacity)` | 提示文字，无假 Token |
| `FrameGroup` | `FrameGroup(GroupTarget Target, NodeId? ParentId, bool IsVisible, double AnimationOpacity)` | 当前运行父关联 |
| `VisualContext` | `ProjectContext Project`；`ParameterValues Parameters`；`ResourceCatalog Resources`；`DeterministicRandom Random`；`EffectBinding Binding`；`AudioFeatures Audio`；`OutputMode OutputMode`；`DiagnosticReporter Diagnostics`；`CancellationToken CancellationToken` | 视觉输入基类，无基本几何查询通道 |
| `EmissionFrameContext : VisualContext` | `FinalSceneGeometry FinalGeometry`；`PlaybackFrame Playback`；`TimelineHistory History`；`long TimeUs` | 发射输入 |
| `VisualStateContext : VisualContext` | `FinalSceneGeometry PreviousGeometry, CurrentGeometry`；`ImmutableArray<ParticleSample> PreviousParticles, Particles`；`EmissionSchedule EmissionSchedule`；`TimelineHistory PreviousHistory, History` | 两端输入，本实例粒子 |
| `VisualFrameContext : VisualContext` | `FinalSceneGeometry FinalGeometry`；`PlaybackFrame Playback`；`FinalSceneGeometry? BoundaryGeometry`；`TimelineHistory History`；`ImmutableArray<ParticleSample> Particles`；`EmissionSchedule EmissionSchedule`；`long TimeUs, LastSimulationBoundaryUs, FractionalElapsedUs` | 最终视觉输入；纯帧无历史/粒子 |
| `FinalSceneGeometry` | `long TimeUs`；`FramePresentations Presentations`；`ImmutableArray<TokenGeometrySample> Tokens`；`ImmutableArray<LineGeometrySample> Lines`；`ImmutableArray<TextMarkGeometrySample> Marks`；`ImmutableArray<GroupGeometrySample> Groups`；`PlaybackFrame Playback` | 输入，不含 overlay |
| `BaseSceneGeometry` | `long TimeUs`；`ImmutableArray<FrameLine> Lines`；`ImmutableArray<TokenGeometrySample> Tokens`；`ImmutableArray<LineGeometrySample> LineGeometry`；`ImmutableArray<FrameMark> Marks`；`ImmutableArray<FrameGroup> Groups`；`ImmutableArray<TextMarkGeometrySample> MarkGeometry`；`ImmutableArray<GroupGeometrySample> GroupGeometry` | 基本布局查询结果，不执行作者效果 |
| `PerformanceSample` | `PerformanceSample(TimeRange? TimeRange, long? DurationUs, long? ElapsedUs, double? Progress, bool IsCurrent, bool IsSung, ProgressSource ProgressSource)` | `ElapsedUs` 可负，缺失为空 |
| `TimeRange` | `TimeRange(long startUs,long endUs)`；`long StartUs, EndUs, DurationUs`；`bool IsValid`；`bool Contains(long timeUs)` | 非负、严格正长半开区间 |
| `SimulationStep` / `VisualSimulationStep` | 各自 `(long startUs,long endUs)`；`long StartUs, EndUs, DurationUs`；`bool IsValid` | 推进区间，不按 UI 帧生成 |

相关枚举完整值：

| 枚举 | 值 |
|---|---|
| `OutputMode` | `Opaque, Transparent` |
| `DisplayPhase` | `Preparing, Performing, Retained` |
| `ProgressSource` | `Token, RubySegment, Line, Missing, Unavailable` |
| `SourceTimingStatus` | `Missing, Imported, Estimated, Edited, Confirmed, Invalid`；Invalid 不用于执行 |

## 2. 目录、绑定、目标和几何

| 类型 | 构造/公开属性 | 补充 |
|---|---|---|
| `SourceBranch` | `SourceBranch(BranchId Id,string Name,string Role,int SourceOrder,ImmutableArray<LineId> LineIds)` | 完整源序 |
| `SourceLine` | `SourceLine(LineId Id,BranchId BranchId,string Text,TimeRange? TimeRange,SourceTimingStatus TimingStatus,int SourceOrder,ImmutableArray<TokenId> TokenIds,ImmutableArray<RubyId> RubyIds)` | 行时间不代替逐字时间 |
| `SourceToken` | `SourceToken(TokenId Id,BranchId BranchId,LineId LineId,string Text,TimeRange? TimeRange,SourceTimingStatus TimingStatus,int SourceOrder)` | 源内容非帧状态 |
| `SourceRuby` | `SourceRuby(RubyId Id,LineId LineId,TokenId BaseTokenId,string Text,string? Language)`；`ImmutableArray<SourceRubySegment> Segments` | 无独立假 Token |
| `SourceRubySegment` | `SourceRubySegment(string Id,string Text,TimeRange? TimeRange,SourceTimingStatus TimingStatus)` | 注音片段源时间 |
| `SourceNode` | `SourceNode(NodeId Id,NodeId? ParentId,SceneNodeKind Kind,NodeLayoutKind Layout,Vector2 ReferenceRatio,BranchId? BranchId,LineId? LineId,RubyId? RubyId,int Order)` | 固定定义父树 |
| `SourceProgram` | `SourceProgram(ProgramId Id,BranchId BranchId,NodeId RootNodeId,NodeId DefaultNodeId)` | 显示程序 |
| `SourceClip` | `SourceClip(ClipId Id,ProgramId ProgramId,NodeId ContainerId,ImmutableArray<LineId> LineIds,TimeRange TimeRange)` | 显式展示片段 |
| `SourceCue` | `SourceCue(CueId Id,ProgramId ProgramId,LineId TargetLineId,NodeId ContainerId,bool Enabled,TimeRange? TimeRange)` | 真实前奏，未知区间可空 |
| `ContentCatalog` | `Branches, Lines, Tokens, Ruby` 对应上述不可变数组；方法 `GetToken/TryGetToken/GetLine/GetBranch/GetRuby/FindTokens` 见主文 §2 | 输入目录，构造器见 §6 |
| `SceneCatalog` | `Nodes, Programs, Clips, LeadIns` 对应上述不可变数组；`SourceNode GetNode(NodeId id)` | 输入目录 |
| `EffectBinding` | 嵌套 `Project()`、`Branch(BranchId Id)`、`Lines(ImmutableArray<LineId> Ids)`、`Tokens(ImmutableArray<TokenId> Ids)`、`Clips(ImmutableArray<ClipId> Ids)`、`Node(NodeId Id,bool IncludeDescendants)` | 输出范围 |
| `SourceSubscription` | 嵌套 `BoundSources()`、`Project()`、`Branch(BranchId id)`；Branch 属性 `BranchId Id` | 事件订阅，不授写权 |
| `SourceFilter` | 嵌套 `Project()`、`Branch(BranchId id)`、`Lines(ImmutableArray<LineId> ids)`、`Tokens(ImmutableArray<TokenId> ids)`；属性 `Id/Ids` | 查询筛选 |
| `TokenFamilyTarget` / `BaseTokenTarget` | 各自 `(TokenId TokenId,BindingId BindingId)` | 族/仅基础字中立身份，当前仅族有 writer |
| `LineTarget` | `LineTarget(LineId LineId,BindingId BindingId)` | 真实整行装载 |
| `GroupTarget` | `GroupTarget(NodeId NodeId)` | 真实组 |
| `TextMarkTarget` | `TextMarkTarget(NodeId NodeId,CueId CueId,BindingId BindingId)` | 真实提示标记装载 |
| `GeometrySample` | `Rect2 AdvanceBounds`；`Rect2? InkBounds`；`Point2 Baseline`；`Affine2 BaseWorldMatrix`；`ImmutableArray<DrawUnitId> DrawUnits`；`bool CanTransformIndependently`；`string? DiagnosticCode`；`ImmutableArray<Point2> WorldInkCorners` | 固定整形几何；世界矩阵只应用一次 |
| `TokenGeometrySample` | `BranchId BranchId`；`LineId LineId`；`TokenFamilyTarget WithRuby`；`BaseTokenTarget BaseOnly`；`GeometrySample BaseGeometry, FamilyGeometry`；`TextProgressPath? ProgressPath`；`ImmutableArray<RubyGeometrySample> Ruby` | 家族墨迹不改变基础 Advance 枢轴 |
| `RubyGeometrySample` | `RubyGeometrySample(RubyId RubyId,GeometrySample Geometry)` | 独立注音几何 |
| `LineGeometrySample` | `BranchId BranchId`；`LineTarget Target`；`GeometrySample BaseGeometry`；`TextProgressPath ProgressPath` | 无逐字行也有 |
| `TextMarkGeometrySample` | `TextMarkGeometrySample(TextMarkTarget Target,GeometrySample Geometry)` | 标记字形 |
| `GroupGeometrySample` | `GroupTarget Target`；`Rect2 ReferenceBounds`；`Affine2 BaseWorldMatrix`；`Point2 WorldPivot`；`ImmutableArray<Point2> WorldInkCorners` | 只读全部组，写权独立 |
| `GeometryQueries` | `BaseSceneGeometry GetBaseAt(long timeUs,SourceFilter filter)` | 阶段通道，宿主成员见 §6 |
| `Point2` | `Point2(double x,double y)`；`double X,Y` | 有限 |
| `Vector2` | `Vector2(double x,double y)`；`double X,Y`；`static Vector2 Zero` | 有限 |
| `Rect2` | `Rect2(double left,double top,double right,double bottom)`；`double Left,Top,Right,Bottom,Width,Height`；`Point2 Center` | 宽高非负，不排序 |
| `Affine2` | `Affine2(double a,double b,double c,double d,double tx,double ty)`；`double A,B,C,D,Tx,Ty`；`Identity, Map, Compose, TryInverse, Translation, ScaleAt` 完整签名见主文 §5 | default 为零矩阵 |
| `AnchorMap` | `Rect2 Domain`；`Point2 Map(Point2 point)`；`Point2 Inverse(Point2 point)`；`Vector2 MapVectorAt(Point2 point,Vector2 vector)` | 中立值，无输出 writer |
| `AnchorMap.Affine` | `Affine(Rect2 domain,Affine2 matrix)`；`Affine2 Matrix` | 有界可逆 |
| `AnchorMap.AxisWave` | `AxisWave(Rect2 domain,WaveDriveAxis driveAxis,double amplitudePx,double wavelengthPx,double phaseRadians,double originPx)`；`WaveDriveAxis DriveAxis`；`double AmplitudePx,WavelengthPx,PhaseRadians,OriginPx` | 波长正、相位弧度 |
| `TextProgressPath` | `TextProgressPath(float startX,float endX,double baselineY,double centerY)`；`float StartX,EndX`；`double BaselineY,CenterY`；`static float XAt(float startX,float endX,double progress)`；`Point2 At(double progress,CursorAnchorMode mode)` | 真实裁切进度路径 |
| `CursorGeometry` | `CursorTrackId TrackId`；`CursorIdentity Identity`；`long TimeUs`；`double Progress`；`TextProgressPath Path`；`Affine2 WorldMatrix`；`PlaybackCursor At(CursorAnchorMode mode)` | 最终文字几何 |
| `PlaybackCursor` | `PlaybackCursor(CursorTrackId TrackId,CursorIdentity Identity,long TimeUs,double Progress,Point2 Position,Vector2 Direction,CursorAnchorMode AnchorMode)`；派生 `TokenId? TokenId, LineId LineId, BindingId BindingId, OccurrenceId? OccurrenceId, ProgressSource ProgressSource` | 真实歌曲播放头 |
| `PlaybackFrame` | `ImmutableArray<CursorGeometry> Cursors`；`bool IsAvailable`；`static PlaybackFrame Unavailable`；`ImmutableArray<PlaybackCursor> GetCursors(CursorAnchorMode mode)` | 可并发/可合法空；构造器见 §6 |
| `CursorEndpoint` | `CursorEndpoint(CursorIdentity Identity,CursorEndpointKind Kind,long TimeUs,CursorAnchorMode AnchorMode,PlaybackCursor? Cursor,ImmutableArray<RuntimeDiagnostic> Diagnostics)`；`bool IsAvailable` | 宿主端点结果，作者无相应查询通道 |

| 枚举 | 完整值 |
|---|---|
| `SceneNodeKind` | `Group, Text` |
| `NodeLayoutKind` | `None, FreeLine, SlotSequence, TranslationFlow, Ruby` |
| `WaveDriveAxis` | `X, Y`（保留该轴，位移作用另一轴） |
| `CursorAnchorMode` | `Baseline, AdvanceCenter` |
| `CursorEndpointKind` | `PreviousEndLimit`（EndUs-1）、`NextStart`（StartUs） |

## 3. 时间线与音频的全部结果类型

除下面构造器外，事件均继承 `TimelineEvent` 的 `Kind,TimeUs,EventId,SourceOrder,ReplayReason,BoundaryPriority`。这些公开事件构造器用于中立事件值；自行 new 不会向宿主派发事件。抽象基类的构造器不向外部作者开放。

| 类型 | 公共构造与专有属性 |
|---|---|
| `EventSourceOrder` | `EventSourceOrder(int branch,int line = -1,int token = -1)`；`int Branch,Line,Token`；`static EventSourceOrder Global` |
| `TimelineEvent` | `TimelineEvent WithReplayReason(ReplayReason reason)`；`static IComparer<TimelineEvent> Comparer` |
| `TokenPerformanceEvent` | `SourceToken Source`；`TokenId TokenId`；`LineId LineId`；`BranchId BranchId`；`OccurrenceId OccurrenceId` |
| `TokenStarted` / `TokenCompleted` | 各自 `(SourceToken token,EventSourceOrder order,ReplayReason reason)` |
| `LinePerformanceEvent` | `SourceLine Source`；`LineId LineId`；`BranchId BranchId`；`TimeRange PerformanceRange`；`ProgressSource ProgressSource` |
| `LinePerformanceStarted` / `LinePerformanceCompleted` | 各自 `(SourceLine line,TimeRange range,ProgressSource source,EventSourceOrder order,ReplayReason reason)` |
| `BindingTimelineEvent` | `FrameLine Binding` |
| `BindingEntered` / `BindingLeft` | 各自 `(FrameLine binding,long timeUs,EventSourceOrder order,ReplayReason reason)` |
| `SceneElementIdentity` | `NodeId NodeId`；封闭 `Lyric/Mark/Group` |
| `SceneElementIdentity.Lyric` | `Lyric(NodeId nodeId,FrameLine binding,RubyId? rubyId = null)`；`FrameLine Binding`；`RubyId? RubyId` |
| `SceneElementIdentity.Mark` | `Mark(NodeId nodeId,CueId cueId,FrameLine target)`；`CueId CueId`；`FrameLine Target` |
| `SceneElementIdentity.Group` | `Group(NodeId nodeId,BranchId? branchId)`；`BranchId? BranchId` |
| `ElementTimelineEvent` | `SceneElementIdentity Element` |
| `ElementVisible` / `ElementHidden` | 各自 `(SceneElementIdentity element,long timeUs,EventSourceOrder order,ReplayReason reason)` |
| `LeadInEventTarget` | `LeadInEventTarget(SourceCue cue,FrameLine target,int markCount)`；`SourceCue Cue`；`FrameLine Target`；`int MarkCount` |
| `LeadInTimelineEvent` | `LeadInEventTarget Target`；`int ElapsedMarks`；`CueId CueId` |
| `LeadInStarted` / `LeadInCompleted` | 各自 `(LeadInEventTarget target,EventSourceOrder order,ReplayReason reason)` |
| `LeadInStepChanged` | `LeadInStepChanged(LeadInEventTarget target,long timeUs,int elapsedMarks,EventSourceOrder order,ReplayReason reason)` |
| `CursorIdentity` | `CursorIdentity(BranchId branchId,LineId lineId,BindingId bindingId,TimeRange range,ProgressSource source,TokenId? tokenId = null,OccurrenceId? occurrenceId = null)`；`BranchId BranchId`；`LineId LineId`；`BindingId BindingId`；`TimeRange TimeRange`；`ProgressSource ProgressSource`；`TokenId? TokenId`；`OccurrenceId? OccurrenceId` |
| `CursorTransitionEvent` | `CursorTransitionEvent(CursorTrackId trackId,CursorIdentity from,CursorIdentity to,long timeUs,CursorTransitionReason transitionReason,EventSourceOrder order,ReplayReason reason)`；`CursorTrackId TrackId`；`CursorIdentity From,To`；`long GapUs`；`CursorTransitionReason TransitionReason` |
| `AudioFeatureTick` | `AudioFeatureTick(string revision,AudioFeaturePoint point,ReplayReason reason)`；`string AnalysisRevision`；`AudioFeaturePoint Point` |
| `EventRecord` | `EventRecord(TimelineEvent timelineEvent)`；`TimelineEvent Event`；`EventId EventId`；`long TimeUs`；`TimelineEventKind Kind` |
| `TimelineHistory` | `long LastBoundaryUs`；`ImmutableArray<EventRecord> Records`；`ImmutableArray<EventRecord> GetStarts(TimeRange range,SourceFilter filter)`；构造器见 §6 |
| `AudioFeatureValues` | `AudioFeatureValues(double bassLevel,double pulse,double? beatPhase,double beatConfidence)`；`double BassLevel,Pulse,BeatConfidence`；`double? BeatPhase` |
| `AudioFeaturePoint` | `AudioFeaturePoint(long timeUs,AudioFeatureValues values)`；`long TimeUs`；`AudioFeatureValues Values` |
| `AudioFeatureSample` | `AudioFeatureSample(long TimeUs,AudioAvailability Availability,AudioFeatureValues? Values,string? DiagnosticCode)` |
| `AudioFeatureRange` | `AudioFeatureRange(TimeRange RequestedRange,TimeRange? AvailableRange,AudioAvailability Availability,ImmutableArray<AudioFeaturePoint> Points,string? DiagnosticCode)` |
| `AudioFeatures` | `AudioAvailability Availability`；`string? Revision`；`long? DurationUs`；`static AudioFeatures NotAnalyzed`；`static AudioFeatures Unavailable(AudioAvailability availability,string diagnosticCode)`；`AudioFeatureSample GetAt(long timeUs)`；`AudioFeatureRange GetRange(TimeRange range)`；已分析序列构造器见 §6 |

| 枚举 | 完整值 |
|---|---|
| `TimelineEventKind` | `TokenStarted, TokenCompleted, LinePerformanceStarted, LinePerformanceCompleted, BindingEntered, BindingLeft, ElementVisible, ElementHidden, LeadInStarted, LeadInStepChanged, LeadInCompleted, CursorTransition, AudioFeatureTick` |
| `ReplayReason` | `Initial, Seek, Loop, InputsChanged, Export, AnchorQuery` |
| `CursorTransitionReason` | `TokenChange, LineChange, BindingChange` |
| `AudioAvailability` | `NoAudio, NotAnalyzed, AnalysisFailed, Available, OutsideRange` |

## 4. 参数、曲线、样式、材质与 uniform

| 类型 | 公共构造/成员 | 作用 |
|---|---|---|
| `ParameterValue` | 封闭嵌套 `Number(double value)` / `Whole(long Value)` / `Toggle(bool Value)` / `Choice(string value)` / `Duration(long value)` / `Text(string value)` / `Color(ColorRgba value)` / `Curve(CurveValue value)` / `Asset(ImageHandle value)`；均公开对应类型的 `Value` | 作者参数值，不做隐式转换 |
| `ParameterValues` | `ParameterValues(IEnumerable<KeyValuePair<string,ParameterValue>> values)`；`static ParameterValues Empty`；`ImmutableArray<string> Ids`；9 个 getter 和 `void ValidateResources(ResourceCatalog resources)` 见主文 §4 | 完整不可变参数集 |
| `CurveValue` | `double Evaluate(double progress)`；封闭嵌套三型见下 | 输入 `[0,1]` 不夹值 |
| `CurveValue.Easing` | `Easing(EasingKind kind)`；`EasingKind Kind` | 规范缓动 |
| `CurveValue.CubicBezier` | `CubicBezier(Point2 first,Point2 second)`；`Point2 First,Second` | X 控制点 `[0,1]`，Y 可过冲 |
| `CurveValue.ControlPoints` | `ControlPoints(ImmutableArray<Point2> points)`；`ImmutableArray<Point2> Points` | 分段线性 |
| `Curves` | `static CurveValue LinearCurve,EaseOutCurve,EaseInOutCurve`；`static double EaseOut(double progress)` | 纯函数/固定值 |
| `ColorRgba` | `ColorRgba(double red,double green,double blue,double alpha)`；`double Red,Green,Blue,Alpha`；`ColorRgba MultiplyRgb(double factor)` | `[0,1]` 直乘 sRGB |
| `GradientStop` | `GradientStop(double offset,ColorRgba color)`；`double Offset`；`ColorRgba Color` | offset `[0,1]` |
| `GradientValue` | `GradientValue(ImmutableArray<GradientStop> stops,GradientKind kind = GradientKind.Linear,double angleDegrees = 0,GradientCoordinateSpace coordinateSpace = GradientCoordinateSpace.Line,GradientColorSpace colorSpace = GradientColorSpace.LinearRgb,GradientHuePath huePath = GradientHuePath.Shorter,double centerX = .5,double centerY = .5,double radiusX = .5,double radiusY = .5)` | 完整中立渐变；正式支持子集见主文 |
| 同上属性 | `ImmutableArray<GradientStop> Stops`；`GradientKind Kind`；`double AngleDegrees,CenterX,CenterY,RadiusX,RadiusY`；`GradientCoordinateSpace CoordinateSpace`；`GradientColorSpace ColorSpace`；`GradientHuePath HuePath` | 首尾 0/1、至少两点、非递减，可同位置硬边 |
| `MaterialValue` | `static MaterialValue Solid(ColorRgba color)`；`static MaterialValue Gradient(GradientValue gradient)`；`static MaterialValue PackageExport(MaterialHandle handle,ParameterValues parameters)` | 作者值工厂 |
| `MaterialValue.SolidMaterial` | `ColorRgba Color`；构造器 internal | 工厂结果 |
| `MaterialValue.GradientMaterial` | `GradientValue Value`；构造器 internal | 工厂结果 |
| `MaterialValue.PackageMaterial` | `MaterialHandle Handle`；`ParameterValues Parameters`；`GraphUniformValues Uniforms`（公开只读）；构造器和覆盖 helper 非 public | 固定包材质值 |
| `StrokeValue` | `StrokeValue(double widthPx,MaterialValue material,StrokeJoin join = StrokeJoin.Round,StrokePosition position = StrokePosition.Outside)`；`double WidthPx`；`MaterialValue Material`；`StrokeJoin Join`；`StrokePosition Position` | 外描边 |
| `ShadowValue` | `ShadowValue(ColorRgba color,double opacity = 1,double angleDegrees = 45,double distancePx = 4,double blurRadiusPx = 8)`；`ColorRgba Color`；`double Opacity,AngleDegrees,DistancePx,BlurRadiusPx` | 阴影 |
| `GlowValue` | `GlowValue(ColorRgba color,double opacity = 1,double blurRadiusPx = 8,double spreadPx = 0)`；`ColorRgba Color`；`double Opacity,BlurRadiusPx,SpreadPx` | 只读；opacity 为 [0,1]，模糊和外扩为非负有限值 |
| `StyleView` | `StyleView(MaterialValue unsungMaterial,MaterialValue sungMaterial,StrokeValue? stroke,ShadowValue? shadow,double opacity,GlowValue? glow = null)`；`MaterialValue UnsungMaterial,SungMaterial`；`StrokeValue? Stroke`；`ShadowValue? Shadow`；`double Opacity`；`GlowValue? Glow`；`ColorRgba RequireSungSolidColor()` | 基线输入/中立值；辉光只读，非 Solid 不猜基色 |
| `GroupStyleView` | `GroupStyleView(double opacity)`；`double Opacity` | 组无文字材质 |
| `BaselineStyles` | `static BaselineStyles Empty`；6 个 `Get`：`StyleView Get(TokenId id)`、`Get(TokenFamilyTarget target)`、`Get(LineId id)`、`Get(LineTarget target)`、`Get(TextMarkTarget target)`；`GroupStyleView Get(GroupTarget target)` | 所有实例共同静态基线；构造器见 §6 |
| `StrokeUpdate` / `ShadowUpdate` | `StrokeUpdate(StrokeValue? Value)` / `ShadowUpdate(ShadowValue? Value)` | null 包装表示明确关闭 |
| `StyleUpdate` | `StyleUpdate(MaterialValue? unsung = null,MaterialValue? sung = null,StrokeUpdate? stroke = null,ShadowUpdate? shadow = null,double opacityFactor = 1)`；`MaterialValue? UnsungMaterial,SungMaterial`；`StrokeUpdate? Stroke`；`ShadowUpdate? Shadow`；`double OpacityFactor`；`ImmutableArray<MaterialEdit> MaterialEdits` | 中立输出值，作者通过 writer 发布 |
| 同上辅助 | `StyleUpdate Then(StyleUpdate next)`；`StyleView Apply(StyleView baseline)`；`StyleView ApplyMaterials(StyleView baseline)` | 组合/验证值，不自行改变正式工程/帧 |
| `MaterialEdit` | 封闭 `ReplaceMaterial(MaterialSlot Slot,MaterialValue? Material)`；`Uniform(MaterialSlot Slot,MaterialHandle Handle,string NodeId,string Name,UniformValue Value)` | 有序中立操作日志 |
| `UniformValue` | `UniformValueKind Kind`；`int? ArrayLength`；`ImmutableArray<double> Numbers`；`ImmutableArray<int> Integers`；8 个静态工厂完整签名见主文 §6 Materials | 7 类型及数组；无 public 构造器 |
| `UniformValues` | `UniformValues(IEnumerable<KeyValuePair<string,UniformValue>> values)`；`static UniformValues Empty`；`ImmutableDictionary<string,UniformValue> Values` | 中立 shader 帧值集，无直接 writer |
| `GraphUniformValues` | `GraphUniformValues(IEnumerable<KeyValuePair<(string NodeId,string Name),UniformValue>> values)`；`static GraphUniformValues Empty`；`ImmutableDictionary<(string NodeId,string Name),UniformValue> Values` | 图节点覆盖集；Set 是 internal |

源码显式实现的内容相等 helper：`bool GradientValue.Equals(GradientValue? other)`、`bool StyleUpdate.Equals(StyleUpdate? other)`、`bool MaterialEdit.Uniform.Equals(Uniform? other)`、`bool GraphUniformValues.Equals(GraphUniformValues? other)`、`override bool GraphUniformValues.Equals(object? obj)`；上述四型均有 `override int GetHashCode()`。这些用于值比较，不是额外输出 API。

| 枚举 | 完整值 |
|---|---|
| `EasingKind` | `Linear, EaseOut, EaseInOut` |
| `GradientKind` | `Linear, Radial` |
| `GradientCoordinateSpace` | `Canvas, Line, Glyph, Progress`；后两者不是当前脚本生产支持 |
| `GradientColorSpace` | `LinearRgb, Hsv, Oklch`；Oklch 非当前生产支持 |
| `GradientHuePath` | `Shorter, Longer, Increasing, Decreasing` |
| `StrokeJoin` | `Miter, Round, Bevel` |
| `StrokePosition` | `Outside` |
| `MaterialSlot` | `UnsungFill, SungFill, Stroke` |
| `UniformValueKind` | `Float, Float2, Float3, Float4, Int, Color, Matrix3x3` |

## 5. 粒子、发射、临时图元与输出记录

| 类型 | 全部公开字段/构造或辅助方法 | 性质 |
|---|---|---|
| `ParticleDescription` | `long LifetimeUs`；`double SizePx`；`ColorRgba Color`；`Vector2 Velocity`；`double SpreadPx`；`long TrailDurationUs`；`ParticleRenderMode Rendering`；`OverlayLayer Layer`；`string SeedStreamKey`；`ParticleMotion Motion`；`virtual void Validate()` | 作者描述基类，只有 SDK 可继承 |
| `ParticleAgeCurve` | `ParticleAgeCurve(double start,double end,CurveValue curve)`；`double Start,End`；`CurveValue Curve`；`double Evaluate(double age)`；`static ParticleAgeCurve ConstantOne` | 作者值 |
| `ParticleMotion` | `Vector2 Acceleration`；`ParticleAgeCurve Opacity,Size`；`static ParticleMotion ConstantVelocity` | 默认构造可用 |
| `CursorEmitterDescriptor : ParticleDescription` | `CursorTrackFilter TrackFilter`；`CursorAnchorMode AnchorMode`；`double RatePerSecond`；`TransitionMode TransitionMode`；`long BridgeDurationUs,BridgeGapLimitUs`；`CurveValue BridgeCurve`；`ImageOnlyPolicy ImageOnlyPolicy`；`override void Validate()` | 作者值；捕获策略有关闭门禁 |
| `BurstAnchor` | 封闭 `SourceToken(TokenId Id)`；`SourceLine(LineId Id)`；`TextMark(NodeId Id)`；`BindingCursors(CursorAnchorMode Mode)` | 作者锚点 |
| `BurstDescriptor : ParticleDescription` | `int Count`；`BurstAnchor Anchor`；`override void Validate()` | 作者值，按真实事件出生 |
| `EmitterBinding` | `EmitterBinding(EffectInstanceId InstanceId,EmitterId Id,CursorEmitterDescriptor Descriptor)` | 宿主收取的中立记录 |
| `BurstIntent` | `BurstIntent(EffectInstanceId InstanceId,EmitterId EmitterId,EventId EventId,long TimeUs,BurstDescriptor Descriptor)` | 宿主收取的中立意图 |
| `ParticleId` | `ParticleId(EffectInstanceId InstanceId,EmitterId EmitterId,ulong BirthIndex)` | 稳定出生身份 |
| `ParticleTrailSample` | `ParticleTrailSample(long TimeUs,Point2 Position)` | 固定时间采样 |
| `ParticleSample` | `ParticleId Id`；`ParticleSource Source`；`CursorTrackId? TrackId`；`BindingId BindingId`；`EventId? EventId`；`long BirthUs,DeathUs`；`Point2 BirthPosition,Position`；`double SizePx`；`ColorRgba Color`；`OverlayLayer Layer`；`ImmutableArray<ParticleTrailSample> Trail` | 输入，本实例完整存活集合 |
| `ParticleSource` | `BindingId BindingId`；`CursorTrackId? TrackId`；封闭 Cursor/Bridge/Token/Line/TextMark | 不虚构固定文字轨道 |
| `ParticleSource.Cursor` | `Cursor(CursorTrackId trackId,CursorIdentity identity)`；`CursorTrackId Track`；`CursorIdentity Identity`；覆盖 `BindingId` | 出生来源 |
| `ParticleSource.Bridge` | `Bridge(CursorTransitionEvent transition,TimeRange flightRange)`；`CursorTrackId Track`；`CursorIdentity From,To`；`EventId TransitionId`；`TimeRange FlightRange`；覆盖 `BindingId` | 真实正空档末段 |
| `ParticleSource.Token` | `Token(TokenFamilyTarget target)`；`TokenFamilyTarget Target`；覆盖 `BindingId` | 固定元素来源 |
| `ParticleSource.Line` | `Line(LineTarget target)`；`LineTarget Target`；覆盖 `BindingId` | 固定行来源 |
| `ParticleSource.TextMark` | `TextMark(TextMarkTarget target)`；`TextMarkTarget Target`；覆盖 `BindingId` | 固定标记来源 |
| `ParticleBirthSample` | `ParticleBirthSample(ParticleId Id,ParticleSource Source,long BirthUs,long DeathUs,Point2 BirthPosition,ParticleDescription Descriptor,EventId? EventId = null)`；派生 `CursorTrackId? TrackId, BindingId BindingId` | 最近区间真实出生 |
| `EmissionSchedule` | `EmissionSchedule(long startUs,long endUs,ImmutableArray<ParticleBirthSample> births)`；`long StartUs,EndUs`；`ImmutableArray<ParticleBirthSample> Births`；`static EmissionSchedule Initial` | 输入记录；允许零长初始记录 |
| `VisualOutputBudget` | `VisualOutputBudget(int drawItemLimit = 100_000)`；`int DrawItemLimit`；`static VisualOutputBudget Default` | 宿主预算值 |
| `ParticleSimulationBudget` | `ParticleSimulationBudget(int liveParticleLimit = 20_000,int trailSampleLimit = 200_000,int emissionRecordLimit = 100_000)`；`int LiveParticleLimit,TrailSampleLimit,EmissionRecordLimit`；`static ParticleSimulationBudget Default` | 宿主预算值 |
| `Dot` | `Dot(Point2 position,double radius,ColorRgba color)`；`Point2 Position`；`double Radius`；`ColorRgba Color`；`bool IsValid` | 作者世界图元，半径正 |
| `Segment` | `Segment(Point2 start,Point2 end,double width,ColorRgba color)`；`Point2 Start,End`；`double Width`；`ColorRgba Color`；`bool IsValid` | 作者世界图元，宽正 |
| `TrailPoint` | `TrailPoint(Point2 position,double width,ColorRgba color)`；`Point2 Position`；`double Width`；`ColorRgba Color`；`bool IsValid` | 截面宽可 0 |
| `Trail` | `Trail(ImmutableArray<TrailPoint> points)`；`ImmutableArray<TrailPoint> Points` | 至少 2 点 |
| `Sprite` | `Sprite(ImageHandle image,Affine2 unitToWorld,ColorRgba tint,Rect2? source = null)`；`ImageHandle Image`；`Affine2 UnitToWorld`；`ColorRgba Tint`；`Rect2 Source`；`bool IsValid` | 作者固定图片图元 |
| `OverlayBatch` | `EffectInstanceId InstanceId`；`OutputId Id`；`OverlayLayer Layer`；抽象 `bool IsValid`、`long ItemCount` | 封闭中立批次 |
| `DotBatch` | `DotBatch(EffectInstanceId InstanceId,OutputId Id,OverlayLayer Layer,ImmutableArray<Dot> Dots)`；覆盖 `IsValid,ItemCount` | writer 收取结果 |
| `SegmentBatch` | `SegmentBatch(EffectInstanceId InstanceId,OutputId Id,OverlayLayer Layer,ImmutableArray<Segment> Segments)`；覆盖 `IsValid,ItemCount` | 同上 |
| `TrailBatch` | `TrailBatch(EffectInstanceId InstanceId,OutputId Id,OverlayLayer Layer,ImmutableArray<Trail> Trails)`；覆盖 `IsValid,ItemCount` | 每截面计项 |
| `SpriteBatch` | `SpriteBatch(EffectInstanceId InstanceId,OutputId Id,OverlayLayer Layer,ImmutableArray<Sprite> Sprites)`；覆盖 `IsValid,ItemCount` | 透明图片也计项 |
| `TokenFamilyTransform` | `TokenFamilyTransform(TokenFamilyTarget Target,Affine2 LocalMatrix)` | 中立帧记录 |
| `TextMarkTransform` | `TextMarkTransform(TextMarkTarget Target,Affine2 LocalMatrix)` | 同上 |
| `LineTransform` | `LineTransform(LineTarget Target,Affine2 LocalMatrix)` | 同上 |
| `GroupTransform` | `GroupTransform(GroupTarget Target,Affine2 LocalMatrix)` | 同上 |
| `GroupOpacity` | `GroupOpacity(GroupTarget Target,double Factor)` | 同上，最终 alpha 门禁 |
| `TokenFamilyStyle` | `TokenFamilyStyle(TokenFamilyTarget Target,StyleUpdate Update)` | 中立帧记录 |
| `TextMarkStyle` | `TextMarkStyle(TextMarkTarget Target,StyleUpdate Update)` | Pending/Elapsed 两态 |
| `ImageEffectTarget` | 封闭 `Line(LineTarget Target)`、`Token(TokenFamilyTarget Target)`、`Mark(TextMarkTarget Target)`、`Group(GroupTarget Target)` | Group 非正式图执行支持 |
| `ImageEffectBinding` | `ImageEffectBinding(ImageEffectTarget Target,GraphHandle Graph,ParameterValues Parameters)` | 单实例同目标最后绑定 |
| `FrameOutput` | `ImmutableArray<TokenFamilyTransform> TokenTransforms`；`ImmutableArray<TokenFamilyStyle> TokenStyles`；`ImmutableArray<TextMarkTransform> MarkTransforms`；`ImmutableArray<TextMarkStyle> MarkStyles`；`ImmutableArray<LineTransform> LineTransforms`；`ImmutableArray<GroupTransform> GroupTransforms`；`ImmutableArray<GroupOpacity> GroupOpacities`；`ImmutableArray<ImageEffectBinding> ImageEffects` | 宿主 Complete 的中立不可变结果，不由作者返回 |

| 枚举 | 完整值 |
|---|---|
| `ParticleRenderMode` | `Dots, Custom` |
| `CursorTrackFilter` | `BindingCursors` |
| `TransitionMode` | `Jump, Bridge` |
| `ImageOnlyPolicy` | `SemanticCursor, IncludeInTargetCapture`（后者 Validate 拒绝） |
| `OverlayLayer` | `BehindText, AboveText` |
| `Pivot` | `BaseAdvanceCenter` |
| `[Flags] EffectCapabilities` | `None=0, Transforms=1, Style=2, Overlay=4, ImageEffects=8` |

## 6. 公开但由宿主管理的通道与构造器

以下“公开”不表示作者需要在回调中调用。作者使用回调传入的 writer；自造通道和中立记录不会被宿主作为输出采纳。`Complete` 一般只能调用一次，宿主已经负责调用；作者提前关闭会导致宿主收取阶段失败。`Invalidate` 是宿主 `finally` 中的失效操作。

| 类型 | 完整公共接缝签名/成员 |
|---|---|
| `FrameWriter` 构造 1 | `FrameWriter(ImmutableArray<TokenGeometrySample> geometry,EffectCapabilities capabilities,BaselineStyles? baseline = null,ResourceCatalog? resources = null,ImmutableArray<TextMarkGeometrySample> markGeometry = default,ImmutableArray<LineGeometrySample> lineGeometry = default)` |
| `FrameWriter` 构造 2 | `FrameWriter(ImmutableArray<TokenGeometrySample> geometry,EffectCapabilities capabilities,ImmutableArray<GroupGeometrySample> groupGeometry,BaselineStyles? baseline = null,ResourceCatalog? resources = null,ImmutableArray<TextMarkGeometrySample> markGeometry = default,ImmutableArray<LineGeometrySample> lineGeometry = default)` |
| `FrameWriter` 通道 | `TransformWriter Transforms`；`StyleWriter Style`；`ImageEffectWriter ImageEffects`；`MaterialWriter Materials`；`FrameOutput Complete()`；`void Invalidate()` |
| `FrameWriter.TransformWriter` | 4 个作者方法；构造器 internal |
| `FrameWriter.StyleWriter` | 11 个作者方法；构造器 internal |
| `FrameWriter.MaterialWriter` | 2 个作者方法；构造器 internal |
| `FrameWriter.ImageEffectWriter` | 4 个作者方法；构造器 internal |
| `BurstWriter` | `BurstWriter(EffectInstanceId instance,EffectCapabilities capabilities,TimelineEvent currentEvent)`；作者 `void Queue(EmitterId emitterId,BurstDescriptor descriptor)`；宿主 `ImmutableArray<BurstIntent> Complete()`、`void Invalidate()`；`static BurstWriter Unavailable` |
| `EmitterWriter` | `EmitterWriter(EffectInstanceId instance,EffectCapabilities capabilities)`；作者 `void Set(EmitterId id,CursorEmitterDescriptor descriptor)`；宿主 `ImmutableArray<EmitterBinding> Complete()`、`void Invalidate()` |
| `VisualWriter` | `VisualWriter(EffectInstanceId instance,EffectCapabilities capabilities,VisualOutputBudget? budget = null)`；`OverlayWriter Overlay`；宿主 `ImmutableArray<DotBatch> Complete()`、`ImmutableArray<OverlayBatch> CompleteOverlay()`、`void Invalidate()`；旧 Complete 遇非 Dot 报 `UnsupportedLegacyOverlayOutput` |
| `VisualWriter.OverlayWriter` | 4 个作者 Draw 方法；构造器 internal |
| `GeometryQueries` | `GeometryQueries(Func<long,SourceFilter,BaseSceneGeometry> get)`；`void Invalidate()`；`static GeometryQueries Unavailable`；作者读取 `GetBaseAt` |
| `DiagnosticReporter` | `DiagnosticReporter(Action<EffectDiagnostic> report)`；`void Invalidate()`；`static DiagnosticReporter Unavailable`；作者 `void Report(DiagnosticSeverity severity,string code,string message,SourceLocation? location = null)` |
| `ContentCatalog` | `ContentCatalog(ImmutableArray<SourceBranch> branches,ImmutableArray<SourceLine> lines,ImmutableArray<SourceToken> tokens,ImmutableArray<SourceRuby> ruby)` |
| `SceneCatalog` | `SceneCatalog(ContentCatalog content,ImmutableArray<SourceNode> nodes,ImmutableArray<SourceProgram> programs,ImmutableArray<SourceClip> clips,ImmutableArray<SourceCue> cues)` |
| `TimelineHistory` | `TimelineHistory(ContentCatalog content,long lastBoundaryUs,ImmutableArray<EventRecord> records)`；检查真实源、顺序/去重/未来门禁 |
| `AudioFeatures` | `AudioFeatures(string revision,long durationUs,ImmutableArray<AudioFeaturePoint> points)`；序列须从 0 开始严格递增 |
| `PlaybackFrame` | `PlaybackFrame(ImmutableArray<CursorGeometry> cursors)`；验证真实演唱区间、路径和方向 |
| `BaselineStyles` 构造 1 | `BaselineStyles(IEnumerable<KeyValuePair<TokenId,StyleView>> tokens)` |
| `BaselineStyles` 构造 2 | `BaselineStyles(IEnumerable<KeyValuePair<TokenId,StyleView>> tokens,IEnumerable<KeyValuePair<LineId,StyleView>> lines,IEnumerable<KeyValuePair<NodeId,StyleView>> marks,IEnumerable<KeyValuePair<NodeId,GroupStyleView>> groups)` |
| `ResourceCatalog` | `ResourceCatalog(string packageId,string revision,ImmutableArray<string> materials,ImmutableArray<string> graphs,ImmutableArray<string> images)`；`static ResourceCatalog Empty`；作者 `GetMaterial/GetGraph/GetImage/Contains` |
| `DeterministicRandom` | `DeterministicRandom(ulong seed,EffectInstanceId instance)`；`static string Algorithm`；作者 `uint GetUInt32(string streamKey,ulong index)`、`double GetUnitDouble(string streamKey,ulong index)` |

## 7. 资源身份、诊断、稳定身份辅助

| 类型 | 全部公开成员 |
|---|---|
| `ResourceIdentity` | `ResourceIdentity(string packageId,string revision,string exportId)`；`string PackageId,Revision,ExportId` |
| `EffectPackageIdentity` | `EffectPackageIdentity(string packageId,string revision,string exportId)`；`string PackageId,Revision,ExportId` |
| `MaterialHandle` / `GraphHandle` / `ImageHandle` | 各自 `ResourceIdentity Identity`；构造器均 internal |
| `ResourceCatalog` 作者方法 | `MaterialHandle GetMaterial(string exportId)`；`GraphHandle GetGraph(string exportId)`；`ImageHandle GetImage(string assetId)`；`bool Contains(MaterialHandle handle)`；`bool Contains(GraphHandle handle)`；`bool Contains(ImageHandle handle)`；`string? PackageId,Revision` |
| `SourceLocation` | `SourceLocation(string path,int line,int column)`；`string Path`；`int Line,Column`；一基行列 |
| `EffectDiagnostic` | `EffectDiagnostic(DiagnosticSeverity severity,string code,string message,SourceLocation? location = null)`；`DiagnosticSeverity Severity`；`string Code,Message`；`SourceLocation? Location` |
| `RuntimeDiagnostic` | `RuntimeDiagnostic(EffectInstanceId InstanceId,string ArtifactHash,string Stage,long TimeUs,EffectDiagnostic Diagnostic)`；`EffectPackageIdentity? PackageIdentity` |
| `EffectOutputException : Exception` | `EffectOutputException(string code,string message)`；`string Code` |
| `DiagnosticSeverity` | `Info,Warning,Error`；Error 使阶段失败 |
| 身份结构体（见下） | 均 `(string value)`、`string Value`、`bool IsValid`、`override string ToString()`；默认结构体无效，不跨类别隐式转换 |

身份结构体完整集合：`BindingId, BranchId, ClipId, CueId, CursorTrackId, DrawUnitId, EffectInstanceId, EmitterId, EventId, LineId, NodeId, OccurrenceId, OutputId, ProgramId, RubyId, TokenId`。`ParticleId` 另为 §5 的三字段结构，不属于 string wrapper。

`StableIdentities` 的全部方法均 `static`：

| 完整签名 | 用途 |
|---|---|
| `CursorTrackId CursorTrack(BranchId branch,ProgramId program)` | 连续分支/显示程序通道 |
| `OccurrenceId Occurrence(BranchId branch,LineId line,TokenId token,TimeRange range)` | 定时源演唱身份 |
| `EventId Event(string kind,long timeUs,string owner,int subIndex = 0)` | 稳定事件身份计算，不派发事件 |
| `string LinePerformanceOwner(BranchId branch,LineId line,TimeRange range)` | 行演唱事件 owner |
| `string ElementOwner(NodeId node,BindingId? binding = null,CueId? cue = null)` | 真实节点可见事件 owner |
| `string CursorTransitionOwner(CursorTrackId track,BindingId from,OccurrenceId? fromOccurrence,BindingId to,OccurrenceId? toOccurrence)` | 真实后继边 owner |

## 8. 按源码文件的完整索引

下表逐一覆盖全部 69 个 `.cs` 文件；名称在表内按文件局部显示，嵌套类型的完整限定关系见上文。`VectorOverlayValidation` 和 `TimelineHistory.ValidatedEventTable` 是 internal 宿主实现，即使其内部有 public 修饰的成员也不是作者公共 SDK。`.csproj` 另见 [项目定义](../../src/MakoKaraGenerator.Scripting.Abstractions/MakoKaraGenerator.Scripting.Abstractions.csproj)。

| 源文件 | 公开类型（partial 同型合并阅读） |
|---|---|
| [AnchorMap.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/AnchorMap.cs) | `WaveDriveAxis`, `AnchorMap`, `Affine`, `AxisWave` |
| [AudioFeatures.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/AudioFeatures.cs) | `AudioAvailability`, `AudioFeatureValues`, `AudioFeaturePoint`, `AudioFeatureSample`, `AudioFeatureRange`, `AudioFeatures` |
| [BindingId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/BindingId.cs) | `BindingId` |
| [BranchId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/BranchId.cs) | `BranchId` |
| [BurstWriter.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/BurstWriter.cs) | `BurstAnchor`, `SourceToken`, `SourceLine`, `TextMark`, `BindingCursors`, `BurstDescriptor`, `BurstIntent`, `BurstWriter` |
| [ClipId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ClipId.cs) | `ClipId` |
| [ColorRgba.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ColorRgba.cs) | `ColorRgba` |
| [ContentCatalog.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ContentCatalog.cs) | `SourceTimingStatus`, `SourceBranch`, `SourceLine`, `SourceToken`, `SourceRuby`, `SourceRubySegment`, `ContentCatalog` |
| [CueId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/CueId.cs) | `CueId` |
| [CursorEmitterDescriptor.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/CursorEmitterDescriptor.cs) | `CursorTrackFilter`, `TransitionMode`, `ImageOnlyPolicy`, `ParticleAgeCurve`, `ParticleMotion`, `CursorEmitterDescriptor`, `EmitterBinding`, `ParticleSimulationBudget` |
| [CursorEndpoint.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/CursorEndpoint.cs) | `CursorEndpointKind`, `CursorEndpoint` |
| [CursorTrackId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/CursorTrackId.cs) | `CursorTrackId` |
| [CurveValue.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/CurveValue.cs) | `CurveValue`, `Easing`, `CubicBezier`, `ControlPoints`, `EasingKind` |
| [DeterministicRandom.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/DeterministicRandom.cs) | `DeterministicRandom` |
| [DisplayTimelineEvents.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/DisplayTimelineEvents.cs) | `BindingTimelineEvent`, `BindingEntered`, `BindingLeft`, `SceneElementIdentity`, `Lyric`, `Mark`, `Group`, `ElementTimelineEvent`, `ElementVisible`, `ElementHidden`, `LeadInEventTarget`, `LeadInTimelineEvent`, `LeadInStarted`, `LeadInStepChanged`, `LeadInCompleted`, `CursorIdentity`, `CursorTransitionReason`, `CursorTransitionEvent`, `AudioFeatureTick` |
| [DrawUnitId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/DrawUnitId.cs) | `DrawUnitId` |
| [EffectDiagnostics.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EffectDiagnostics.cs) | `DiagnosticSeverity`, `SourceLocation`, `EffectDiagnostic`, `DiagnosticReporter`, `RuntimeDiagnostic` |
| [EffectInstanceId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EffectInstanceId.cs) | `EffectInstanceId` |
| [EffectPackageIdentity.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EffectPackageIdentity.cs) | `EffectPackageIdentity` |
| [EmissionFrameContext.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EmissionFrameContext.cs) | `EmissionFrameContext` |
| [EmissionSchedule.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EmissionSchedule.cs) | `ParticleBirthSample`, `EmissionSchedule` |
| [EmitterId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EmitterId.cs) | `EmitterId` |
| [EmitterWriter.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EmitterWriter.cs) | `EmitterWriter` |
| [EventId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/EventId.cs) | `EventId` |
| [FrameContext.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameContext.cs) | `OutputMode`, `EffectBinding`, `Project`, `Branch`, `Lines`, `Tokens`, `Clips`, `Node`, `FramePresentations`, `DisplayPhase`, `FrameLine`, `ProjectContext`, `EffectInitializationContext`, `FrameContext`, `IKaraokeEffect`, `EffectBase` |
| [FrameToken.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameToken.cs) | `FrameToken` |
| [FrameWriter.Groups.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Groups.cs) | `GroupTransform`, `GroupOpacity`, `FrameWriter`, `StyleWriter` |
| [FrameWriter.ImageEffects.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.ImageEffects.cs) | `ImageEffectTarget`, `Line`, `Token`, `Mark`, `Group`, `ImageEffectBinding`, `FrameWriter`, `ImageEffectWriter` |
| [FrameWriter.Lines.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Lines.cs) | `LineTransform`, `FrameWriter` |
| [FrameWriter.Marks.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Marks.cs) | `FrameWriter`, `StyleWriter` |
| [FrameWriter.Materials.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Materials.cs) | `FrameWriter`, `MaterialWriter` |
| [FrameWriter.Style.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.Style.cs) | `FrameWriter`, `StyleWriter` |
| [FrameWriter.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/FrameWriter.cs) | `Pivot`, `EffectOutputException`, `EffectCapabilities`, `TokenFamilyTransform`, `FrameOutput`, `FrameWriter`, `TransformWriter` |
| [GeometryQueries.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/GeometryQueries.cs) | `BaseSceneGeometry`, `GeometryQueries` |
| [GeometrySamples.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/GeometrySamples.cs) | `TokenFamilyTarget`, `BaseTokenTarget`, `GeometrySample`, `RubyGeometrySample`, `TokenGeometrySample` |
| [GeometryValues.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/GeometryValues.cs) | `Point2`, `Vector2`, `Rect2`, `Affine2` |
| [GradientValue.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/GradientValue.cs) | `GradientKind`, `GradientCoordinateSpace`, `GradientColorSpace`, `GradientHuePath`, `GradientStop`, `GradientValue` |
| [LineId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/LineId.cs) | `LineId` |
| [MaterialUniforms.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/MaterialUniforms.cs) | `MaterialSlot`, `GraphUniformValues`, `MaterialEdit`, `ReplaceMaterial`, `Uniform` |
| [MaterialValue.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/MaterialValue.cs) | `MaterialValue`, `SolidMaterial`, `GradientMaterial`, `PackageMaterial` |
| [NodeId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/NodeId.cs) | `NodeId` |
| [OccurrenceId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/OccurrenceId.cs) | `OccurrenceId` |
| [OutputId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/OutputId.cs) | `OutputId` |
| [OverlayBatch.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/OverlayBatch.cs) | `OverlayBatch` |
| [ParameterValues.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ParameterValues.cs) | `ParameterValue`, `Number`, `Whole`, `Toggle`, `Choice`, `Duration`, `Text`, `Color`, `Curve`, `Asset`, `ParameterValues`, `Curves` |
| [ParticleDescription.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ParticleDescription.cs) | `ParticleRenderMode`, `ParticleDescription` |
| [ParticleSample.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ParticleSample.cs) | `ParticleId`, `ParticleTrailSample`, `ParticleSample` |
| [ParticleSource.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ParticleSource.cs) | `ParticleSource`, `Cursor`, `Bridge`, `Token`, `Line`, `TextMark` |
| [PerformanceSample.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/PerformanceSample.cs) | `ProgressSource`, `PerformanceSample` |
| [PlaybackGeometry.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/PlaybackGeometry.cs) | `CursorAnchorMode`, `TextProgressPath`, `LineTarget`, `LineGeometrySample`, `PlaybackCursor`, `CursorGeometry`, `PlaybackFrame` |
| [ProgramId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ProgramId.cs) | `ProgramId` |
| [ResourceCatalog.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/ResourceCatalog.cs) | `ResourceIdentity`, `MaterialHandle`, `GraphHandle`, `ImageHandle`, `ResourceCatalog` |
| [RubyId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/RubyId.cs) | `RubyId` |
| [SceneCatalog.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/SceneCatalog.cs) | `SceneNodeKind`, `NodeLayoutKind`, `SourceNode`, `SourceProgram`, `SourceClip`, `SourceCue`, `SceneCatalog` |
| [SceneTargets.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/SceneTargets.cs) | `GroupTarget`, `TextMarkTarget`, `FrameMark`, `FrameGroup`, `TextMarkGeometrySample`, `GroupGeometrySample`, `TextMarkTransform`, `TextMarkStyle`, `GroupStyleView` |
| [SourceSubscription.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/SourceSubscription.cs) | `SourceSubscription`, `BoundSources`, `Project`, `Branch`, `SourceFilter`, `Project`, `Branch`, `Lines`, `Tokens` |
| [SpriteOverlays.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/SpriteOverlays.cs) | `Sprite`, `SpriteBatch` |
| [StableIdentities.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/StableIdentities.cs) | `StableIdentities` |
| [StateContext.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/StateContext.cs) | `SimulationStep`, `SemanticContext`, `StateContext`, `EventContext` |
| [StyleUpdate.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/StyleUpdate.cs) | `StrokeUpdate`, `ShadowUpdate`, `StyleUpdate`, `TokenFamilyStyle` |
| [StyleView.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/StyleView.cs) | `StrokeJoin`, `StrokePosition`, `StrokeValue`, `ShadowValue`, `GlowValue`, `StyleView`, `BaselineStyles` |
| [TimeRange.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/TimeRange.cs) | `TimeRange` |
| [TimelineEvent.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/TimelineEvent.cs) | `TimelineEventKind`, `ReplayReason`, `EventSourceOrder`, `TimelineEvent`, `TokenPerformanceEvent`, `TokenStarted`, `TokenCompleted`, `LinePerformanceEvent`, `LinePerformanceStarted`, `LinePerformanceCompleted` |
| [TimelineHistory.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/TimelineHistory.cs) | `EventRecord`, `TimelineHistory` |
| [TokenId.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/TokenId.cs) | `TokenId` |
| [UniformValue.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/UniformValue.cs) | `UniformValueKind`, `UniformValue`, `UniformValues` |
| [VectorOverlays.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/VectorOverlays.cs) | `Segment`, `TrailPoint`, `Trail`, `SegmentBatch`, `TrailBatch` |
| [VisualContexts.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/VisualContexts.cs) | `FinalSceneGeometry`, `VisualSimulationStep`, `VisualContext`, `VisualStateContext`, `VisualFrameContext` |
| [VisualWriter.cs](../../src/MakoKaraGenerator.Scripting.Abstractions/VisualWriter.cs) | `OverlayLayer`, `VisualOutputBudget`, `Dot`, `DotBatch`, `VisualWriter`, `OverlayWriter` |
