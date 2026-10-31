using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>帧的背景输出语义；不与预览质量混合。</summary>
public enum OutputMode
{
    /// <summary>含工程背景的画布。</summary>
    Opaque,
    /// <summary>移除工程背景的透明画布。</summary>
    Transparent,
}

/// <summary>源对象的输出范围；源事件订阅另外配置，不能由范围推断计数历史。</summary>
public abstract record EffectBinding
{
    private EffectBinding() { }
    /// <summary>全工程。</summary>
    public sealed record Project : EffectBinding;
    /// <summary>单分支。</summary><param name="Id">源分支。</param>
    public sealed record Branch(BranchId Id) : EffectBinding;
    /// <summary>指定源行。</summary><param name="Ids">源行身份，不是节点身份。</param>
    public sealed record Lines(ImmutableArray<LineId> Ids) : EffectBinding;
    /// <summary>指定源元素及其注音族。</summary><param name="Ids">源元素身份。</param>
    public sealed record Tokens(ImmutableArray<TokenId> Ids) : EffectBinding;
    /// <summary>指定展示片段。</summary><param name="Ids">片段身份。</param>
    public sealed record Clips(ImmutableArray<ClipId> Ids) : EffectBinding;
    /// <summary>指定节点，可显式包括后代。</summary><param name="Id">节点。</param><param name="IncludeDescendants">包括后代。</param>
    public sealed record Node(NodeId Id, bool IncludeDescendants) : EffectBinding;
}

/// <summary>当前帧的合法输出集合；与完整源目录不同。</summary>
public sealed record FramePresentations
{
    /// <summary>已按输出绑定筛选的真实元素，不含提示标记。</summary>
    public ImmutableArray<FrameToken> TokensInBinding { get; init; } = [];
    /// <summary>已按输出绑定筛选的真实行装载。</summary>
    public ImmutableArray<FrameLine> LinesInBinding { get; init; } = [];
    /// <summary>已按输出范围筛选的真实前奏文字标记，没有假 Token。</summary>
    public ImmutableArray<FrameMark> MarksInBinding { get; init; } = [];
    /// <summary>本帧真实组的只读目录；组写权限仍须由对应 writer 独立校验。</summary>
    public ImmutableArray<FrameGroup> Groups { get; init; } = [];
}

/// <summary>目标句当前的可见阶段，不用于伪造演唱时间。</summary>
public enum DisplayPhase
{
    /// <summary>前奏期间准备显示。</summary>
    Preparing,
    /// <summary>源演唱／常规显示阶段。</summary>
    Performing,
    /// <summary>演唱后由容器策略保留。</summary>
    Retained,
}

/// <summary>真实源行装载；在前奏／起唱之间保持 BindingId。</summary>
/// <param name="LineId">源行。</param><param name="BranchId">源分支。</param><param name="BindingId">装载。</param>
/// <param name="NodeId">源文本节点。</param><param name="ParentId">实际槽位或容器父节点。</param><param name="ProgramId">程序。</param>
/// <param name="ClipId">显式片段。</param><param name="CueId">关联前奏。</param><param name="DisplayPhase">阶段。</param><param name="EnterProgress">入场进度。</param>
public sealed record FrameLine(LineId LineId, BranchId BranchId, BindingId BindingId, NodeId NodeId, NodeId ParentId, ProgramId ProgramId,
    ClipId? ClipId, CueId? CueId, DisplayPhase DisplayPhase, double EnterProgress)
{
    /// <summary>真实行装载目标；当前行输出权限须由相应 writer 验证。</summary>
    public LineTarget Target => new(LineId, BindingId);
}

/// <summary>只读工程语义投影，不向脚本泄漏可写领域对象。</summary>
public sealed record ProjectContext
{
    /// <summary>完整源目录。</summary>
    public required ContentCatalog Content { get; init; }
    /// <summary>完整真实场景目录。</summary>
    public required SceneCatalog Scene { get; init; }
    /// <summary>画布逻辑宽高。</summary>
    public required Vector2 CanvasSize { get; init; }
    /// <summary>已确定的成片时长；未知保持 null，不用最后一行猜测。</summary>
    public long? DurationUs { get; init; }
    /// <summary>固定分析修订；未提供分析服务时明确为未分析，不伪造静音。</summary>
    public AudioFeatures Audio { get; init; } = AudioFeatures.NotAnalyzed;
}

/// <summary>效果实例初始化的固定输入；不返回领域对象或原生资源。</summary>
public sealed record EffectInitializationContext
{
    /// <summary>本阶段有效的只读基本布局查询，不执行作者效果。</summary>
    public GeometryQueries GeometryQueries { get; init; } = GeometryQueries.Unavailable;
    /// <summary>固定源工程。</summary>
    public required ProjectContext Project { get; init; }
    /// <summary>固定类型化参数。</summary>
    public required ParameterValues Parameters { get; init; }
    /// <summary>本实例固定包的资源目录；无资源时为空。</summary>
    public ResourceCatalog Resources { get; init; } = ResourceCatalog.Empty;
    /// <summary>按宿主种子／实例身份分流的纯索引随机服务。</summary>
    public required DeterministicRandom Random { get; init; }
    /// <summary>取消令牌，不转换为脚本失败。</summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>仅初始化阶段有效的诊断。</summary>
    public DiagnosticReporter Diagnostics { get; init; } = DiagnosticReporter.Unavailable;
}

/// <summary>同一时刻的只读外观输入；共享状态由固定边界推进。</summary>
public sealed record FrameContext
{
    /// <summary>本阶段有效的只读基本布局查询；不能读取尚未完成的最终光标。</summary>
    public GeometryQueries GeometryQueries { get; init; } = GeometryQueries.Unavailable;
    private readonly TimelineHistory? _history;
    /// <summary>最近已处理的真实历史；纯帧工厂不提供历史，读取时报 TimelineSessionRequired。</summary>
    public TimelineHistory History
    {
        get => _history ?? throw new EffectOutputException("TimelineSessionRequired", "历史需要语义会话，纯帧工厂不执行源事件。");
        init => _history = value;
    }
    /// <summary>共享状态最后边界，不等同于任意分数帧时刻。</summary>
    public long LastSimulationBoundaryUs => History.LastBoundaryUs;
    /// <summary>仅求值阶段有效的诊断。</summary>
    public DiagnosticReporter Diagnostics { get; init; } = DiagnosticReporter.Unavailable;
    /// <summary>目标整数微秒时刻。</summary>
    public required long TimeUs { get; init; }
    /// <summary>固定工程语义。</summary>
    public required ProjectContext Project { get; init; }
    /// <summary>固定类型化参数。</summary>
    public required ParameterValues Parameters { get; init; }
    /// <summary>与 Initialize 相同的固定资源目录。</summary>
    public ResourceCatalog Resources { get; init; } = ResourceCatalog.Empty;
    /// <summary>与 Initialize 相同的纯索引随机服务，不随重绘推进。</summary>
    public required DeterministicRandom Random { get; init; }
    /// <summary>工程固定音频分析；不存在另一个帧级分析真值。</summary>
    public AudioFeatures Audio => Project.Audio;
    /// <summary>全实例共用的静态继承基线，不含前一个脚本的输出。</summary>
    public BaselineStyles Baseline { get; init; } = BaselineStyles.Empty;
    /// <summary>合法输出对象。</summary>
    public required FramePresentations Presentations { get; init; }
    /// <summary>同一帧、同一后端的固定文字基础几何。</summary>
    public ImmutableArray<TokenGeometrySample> Geometry { get; init; } = [];
    /// <summary>匹配本帧输出行的完整基础几何，含无逐字元素行。</summary>
    public ImmutableArray<LineGeometrySample> LineGeometry { get; init; } = [];
    /// <summary>本帧合法文字标记的固定字形几何。</summary>
    public ImmutableArray<TextMarkGeometrySample> MarkGeometry { get; init; } = [];
    /// <summary>全部真实组的只读几何，读取不授予写权限。</summary>
    public ImmutableArray<GroupGeometrySample> GroupGeometry { get; init; } = [];
    /// <summary>实例输出绑定，不代表源事件订阅。</summary>
    public required EffectBinding Binding { get; init; }
    /// <summary>背景输出语义。</summary>
    public OutputMode OutputMode { get; init; }
    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>共享和视觉状态分区的七阶段生命周期；粒子服务继续按已接受合同实施。</summary>
public interface IKaraokeEffect : IDisposable
{
    /// <summary>每个实例一次初始化；不能写帧输出。</summary>
    void Initialize(EffectInitializationContext context);
    /// <summary>精确边界的真实源事件，不按 UI 帧重复派发。</summary>
    void OnEvent(TimelineEvent evt, EventContext context);
    /// <summary>推进共享状态，输入和历史截至 step.StartUs；不得写外观或修改源数据。</summary>
    void Advance(SimulationStep simulationStep, StateContext context);
    /// <summary>纯求值，不推进时间或修改源工程；writer 仅在此次调用内有效。</summary>
    void Evaluate(FrameContext frame, FrameWriter output);
    /// <summary>纯求值当前边界的连续发射描述；不得读取粒子或修改文字／状态。</summary>
    void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output);
    /// <summary>文字输出已确定后推进前一区间视觉状态；不得回写共享或文字状态。</summary>
    void AdvanceVisuals(VisualSimulationStep simulationStep, VisualStateContext context);
    /// <summary>纯呈现最终文字几何后的临时图元，不推进状态或修改文字。</summary>
    void EvaluateVisuals(VisualFrameContext frame, VisualWriter output);
}

/// <summary>共享／视觉七阶段作者基类；默认不写输出或推进状态。</summary>
public abstract class EffectBase : IKaraokeEffect
{
    /// <inheritdoc/>
    public virtual void Initialize(EffectInitializationContext context) { }
    /// <inheritdoc/>
    public virtual void OnEvent(TimelineEvent evt, EventContext context) { }
    /// <inheritdoc/>
    public virtual void Advance(SimulationStep simulationStep, StateContext context) { }
    /// <inheritdoc/>
    public virtual void Evaluate(FrameContext frame, FrameWriter output) { }
    /// <inheritdoc/>
    public virtual void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output) { }
    /// <inheritdoc/>
    public virtual void AdvanceVisuals(VisualSimulationStep simulationStep, VisualStateContext context) { }
    /// <inheritdoc/>
    public virtual void EvaluateVisuals(VisualFrameContext frame, VisualWriter output) { }
    /// <inheritdoc/>
    public virtual void Dispose() { GC.SuppressFinalize(this); }
}
