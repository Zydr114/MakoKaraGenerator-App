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
