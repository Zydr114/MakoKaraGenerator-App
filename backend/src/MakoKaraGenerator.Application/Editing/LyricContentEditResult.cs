using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>正文、注音或行操作的中立拒绝代码。</summary>
public enum LyricContentEditErrorCode
{
    /// <summary>输入所属工作区或代次已失效。</summary>
    WorkspaceStale,
    /// <summary>稳定目标不存在，或被编辑的原值已改变。</summary>
    TargetStale,
    /// <summary>操作要求行或元素目标，但请求形状不符。</summary>
    InvalidTarget,
    /// <summary>行时间规则、展示程序或领域构造校验未通过。</summary>
    DomainValidationFailed,
    /// <summary>沿既有编号样式无法再分配唯一标识。</summary>
    IdentifierExhausted,
    /// <summary>分段或计时注音只能通过显式片段命令修改。</summary>
    RubySegmentsRequireExplicitEdit,
}

/// <summary>中立错误及领域给出的具体原因；客户端负责提示文案。</summary>
/// <param name="Code">稳定拒绝代码。</param>
/// <param name="Detail">领域原因；身份门禁失败时可为空。</param>
public sealed record LyricContentEditError(LyricContentEditErrorCode Code, string? Detail = null);

/// <summary>共享歌词编辑结果；拒绝及无变化均保留原工程引用。</summary>
public sealed record LyricContentEditResult
{
    internal LyricContentEditResult(bool accepted, Project project, ProjectLyricsChangeSet changeSet,
        LyricContentEditError? error = null, int preservedCount = 0, int resetCount = 0,
        string? createdLineId = null, string? selectedLineId = null, int? insertedIndex = null, bool overlapsNext = false)
    {
        Accepted = accepted; Project = project; ChangeSet = changeSet; Error = error;
        PreservedCount = preservedCount; ResetCount = resetCount; CreatedLineId = createdLineId;
        SelectedLineId = selectedLineId; InsertedIndex = insertedIndex; OverlapsNext = overlapsNext;
    }

    /// <summary>是否通过全部门禁；无变化也属于接受成功。</summary>
    public bool Accepted { get; }
    /// <summary>用例完成后的当前工程。</summary>
    public Project Project { get; }
    /// <summary>是否产生实际变化。</summary>
    public bool Changed => ChangeSet.HasChanges;
    /// <summary>稳定行、结构来源与预览失效摘要。</summary>
    public ProjectLyricsChangeSet ChangeSet { get; }
    /// <summary>拒绝原因；接受成功时为空。</summary>
    public LyricContentEditError? Error { get; }
    /// <summary>正文重切保留的旧元素数量。</summary>
    public int PreservedCount { get; }
    /// <summary>正文重切新增的未定时元素数量。</summary>
    public int ResetCount { get; }
    /// <summary>新增或复制行的稳定标识。</summary>
    public string? CreatedLineId { get; }
    /// <summary>行操作之后建议选中的行标识；空来源时为空。</summary>
    public string? SelectedLineId { get; }
    /// <summary>新增或复制行在明确来源中的最终下标。</summary>
    public int? InsertedIndex { get; }
    /// <summary>复制行与其后一行存在真实时间重叠；用例不自动修正。</summary>
    public bool OverlapsNext { get; }
}
