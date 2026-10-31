using System.Collections.Immutable;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>已知的歌词行变化类型，可同时包含多种变化。</summary>
[Flags]
public enum LyricLineChangeKind
{
    /// <summary>没有变化。</summary>
    None = 0,
    /// <summary>正文变化。</summary>
    Text = 1,
    /// <summary>注音序列或注音属性变化。</summary>
    Ruby = 2,
    /// <summary>元素身份、文字或切分结构变化。</summary>
    Tokens = 4,
    /// <summary>行或元素的时间、来源状态变化。</summary>
    Timing = 8,
}

/// <summary>发生已知变化的稳定歌词行。</summary>
/// <param name="BranchId">明确的歌词来源标识。</param>
/// <param name="LineId">发生变化的行标识。</param>
/// <param name="Kind">该行的变化类型。</param>
public sealed record LyricLineChange(string BranchId, string LineId, LyricLineChangeKind Kind);

/// <summary>正文、注音及行结构的保守变更摘要；不持有派生工程状态。</summary>
public sealed record ProjectLyricsChangeSet
{
    internal ProjectLyricsChangeSet(ImmutableArray<LyricLineChange> rows, ImmutableArray<string> structuralBranchIds,
        bool presentationChanged = false, bool fullRefreshRequired = false)
    {
        Rows = rows; StructuralBranchIds = structuralBranchIds;
        PresentationChanged = presentationChanged; FullRefreshRequired = fullRefreshRequired;
    }

    /// <summary>无实际变化。</summary>
    public static ProjectLyricsChangeSet None { get; } = new([], []);
    /// <summary>无法证明局部范围，要求调用方完整刷新。</summary>
    public static ProjectLyricsChangeSet FullRefresh { get; } = new([], [], fullRefreshRequired: true);
    /// <summary>已知字段发生变化的稳定行；插入或删除的范围由结构来源表达。</summary>
    public ImmutableArray<LyricLineChange> Rows { get; }
    /// <summary>行插入、删除或顺序改变的来源；该来源的行读模型须重建。</summary>
    public ImmutableArray<string> StructuralBranchIds { get; }
    /// <summary>展示引用或内部覆盖发生变化，应复用调用方的展示刷新出口。</summary>
    public bool PresentationChanged { get; }
    /// <summary>未知字段或来源结构变化要求完整刷新。</summary>
    public bool FullRefreshRequired { get; }
    /// <summary>是否存在工程变化。</summary>
    public bool HasChanges => FullRefreshRequired || PresentationChanged || !Rows.IsEmpty || !StructuralBranchIds.IsEmpty;
    /// <summary>是否使正式预览输入失效；脚本可读取所有歌词字段。</summary>
    public bool InvalidatesPreview => HasChanges;
}
