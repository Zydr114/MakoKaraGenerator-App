using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>来源替换或追加的中立拒绝代码。</summary>
public enum LyricsImportEditErrorCode
{
    /// <summary>工作区实例或代次已改变。</summary>
    WorkspaceStale,
    /// <summary>已有目标或新来源的原歌词已改变。</summary>
    TargetStale,
    /// <summary>目标、原记录或新来源配置不符。</summary>
    InvalidTarget,
    /// <summary>文件解析没有产生可导入的行。</summary>
    EmptyImport,
    /// <summary>多来源文档必须显式选择单来源导入的源分支。</summary>
    SourceBranchRequired,
    /// <summary>所选源分支不存在。</summary>
    SourceBranchNotFound,
    /// <summary>粘贴文字没有非空行。</summary>
    EmptyText,
    /// <summary>候选无法分配唯一歌词标识。</summary>
    IdentifierExhausted,
    /// <summary>最终候选的展示或领域构造校验未通过。</summary>
    DomainValidationFailed,
}

/// <summary>共享导入操作的拒绝原因，由客户端映射提示。</summary>
/// <param name="Code">中立错误代码。</param>
/// <param name="Detail">原领域校验给出的具体原因。</param>
public sealed record LyricsImportEditError(LyricsImportEditErrorCode Code, string? Detail = null);

/// <summary>一次来源替换或追加的工程与读模型结果，拒绝保留原工程。</summary>
public sealed record LyricsImportEditResult
{
    internal LyricsImportEditResult(bool accepted, Project project, ProjectLyricsChangeSet changeSet,
        LyricsImportEditError? error = null, int lineCount = 0, string? selectedBranchId = null, string? selectedLineId = null)
    {
        Accepted = accepted; Project = project; ChangeSet = changeSet; Error = error;
        LineCount = lineCount; SelectedBranchId = selectedBranchId; SelectedLineId = selectedLineId;
    }

    /// <summary>是否通过原值及候选校验。</summary>
    public bool Accepted { get; }
    /// <summary>是否产生实际工程变化。</summary>
    public bool Changed => ChangeSet.HasChanges;
    /// <summary>操作结束后的当前工程。</summary>
    public Project Project { get; }
    /// <summary>供统一刷新与历史恢复使用的保守歌词摘要。</summary>
    public ProjectLyricsChangeSet ChangeSet { get; }
    /// <summary>拒绝原因；接受成功时为空。</summary>
    public LyricsImportEditError? Error { get; }
    /// <summary>本次替换或追加的行数，拒绝时为零。</summary>
    public int LineCount { get; }
    /// <summary>成功之后建议选择的明确来源标识。</summary>
    public string? SelectedBranchId { get; }
    /// <summary>成功之后建议选择的首个新行标识。</summary>
    public string? SelectedLineId { get; }
}
