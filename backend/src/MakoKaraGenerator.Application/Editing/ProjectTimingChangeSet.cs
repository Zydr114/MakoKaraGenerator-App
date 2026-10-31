using System.Collections.Immutable;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>时间与时间来源状态的最小变更摘要；未知变化要求调用者完整刷新。</summary>
public sealed record ProjectTimingChangeSet
{
    internal ProjectTimingChangeSet(ImmutableArray<LyricTimingEditTarget> targets, bool fullRefreshRequired = false)
    {
        Targets = targets;
        FullRefreshRequired = fullRefreshRequired;
    }

    /// <summary>没有变化的摘要。</summary>
    public static ProjectTimingChangeSet None { get; } = new([]);

    /// <summary>无法仅用时间目标表达的变化；调用者必须完整刷新。</summary>
    public static ProjectTimingChangeSet FullRefresh { get; } = new([], fullRefreshRequired: true);

    /// <summary>发生时间或来源状态变化的稳定目标；行目标的元素标识为空。</summary>
    public ImmutableArray<LyricTimingEditTarget> Targets { get; }

    /// <summary>结构、未知字段或时间以外的数据变化要求完整刷新。</summary>
    public bool FullRefreshRequired { get; }

    /// <summary>是否存在实际变化。</summary>
    public bool HasChanges => FullRefreshRequired || !Targets.IsEmpty;

    /// <summary>是否使正式预览输入失效；脚本可能读取时间和来源状态，因此所有变化均失效。</summary>
    public bool InvalidatesPreview => HasChanges;
}
