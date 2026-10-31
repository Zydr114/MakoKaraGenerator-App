using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享元素拆分、合并或重切结果；拒绝和无变化均保留原工程。</summary>
public sealed record LyricTokenEditResult
{
    internal LyricTokenEditResult(bool accepted, Project project, ProjectLyricsChangeSet changeSet,
        LyricContentEditError? error = null, string? selectedLineId = null, string? selectedTokenId = null,
        int changedLineCount = 0, int preservedCount = 0, int createdCount = 0, int estimatedCount = 0)
    {
        Accepted = accepted; Project = project; ChangeSet = changeSet; Error = error;
        SelectedLineId = selectedLineId; SelectedTokenId = selectedTokenId;
        ChangedLineCount = changedLineCount; PreservedCount = preservedCount;
        CreatedCount = createdCount; EstimatedCount = estimatedCount;
    }

    /// <summary>全部门禁是否通过，包含合法无变化。</summary>
    public bool Accepted { get; }
    /// <summary>执行后的权威工作区工程。</summary>
    public Project Project { get; }
    /// <summary>稳定行与预览失效范围。</summary>
    public ProjectLyricsChangeSet ChangeSet { get; }
    /// <summary>是否产生一次实际历史变化。</summary>
    public bool Changed => ChangeSet.HasChanges;
    /// <summary>中立拒绝原因，客户端负责本地化。</summary>
    public LyricContentEditError? Error { get; }
    /// <summary>单行操作的结果行稳定标识。</summary>
    public string? SelectedLineId { get; }
    /// <summary>拆分的右元素或合并后保留的元素稳定标识；重切不指定。</summary>
    public string? SelectedTokenId { get; }
    /// <summary>实际改变元素列表的行数。</summary>
    public int ChangedLineCount { get; }
    /// <summary>原领域重切保留的元素数。</summary>
    public int PreservedCount { get; }
    /// <summary>原领域重切创建的元素数。</summary>
    public int CreatedCount { get; }
    /// <summary>原领域重切估算的元素数。</summary>
    public int EstimatedCount { get; }
}
