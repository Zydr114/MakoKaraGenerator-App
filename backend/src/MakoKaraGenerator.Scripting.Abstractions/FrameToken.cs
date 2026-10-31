namespace MakoKaraGenerator.Scripting;

/// <summary>本帧真实装载的源元素；可见与起唱分开，未定时元素无 OccurrenceId。</summary>
public sealed record FrameToken
{
    /// <summary>真实源元素。</summary>
    public required TokenId TokenId { get; init; }
    /// <summary>真实源分支。</summary>
    public required BranchId BranchId { get; init; }
    /// <summary>真实源行。</summary>
    public required LineId LineId { get; init; }
    /// <summary>实际装载；准备到起唱不改变身份。</summary>
    public required BindingId BindingId { get; init; }
    /// <summary>有有效逐字区间时的稳定源演唱身份；不从可见次数编号。</summary>
    public OccurrenceId? OccurrenceId { get; init; }
    /// <summary>原始源文本；不重新归一化。</summary>
    public required string SourceText { get; init; }
    /// <summary>共享采样器取得的半开进度；Missing 不用行进度替代。</summary>
    public required PerformanceSample Performance { get; init; }
    /// <summary>基础字与全部关联注音的合法目标。</summary>
    public required TokenFamilyTarget WithRuby { get; init; }
    /// <summary>显式只包含基础字的目标。</summary>
    public required BaseTokenTarget BaseOnly { get; init; }
}
