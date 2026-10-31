using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>来源管理的中立拒绝代码，由客户端负责本地化与草稿保留。</summary>
public enum LyricBranchEditErrorCode
{
    /// <summary>工作区或代次过期。</summary>
    WorkspaceStale,
    /// <summary>目标或原值已变。</summary>
    TargetStale,
    /// <summary>目标或请求字段组合不符。</summary>
    InvalidTarget,
    /// <summary>请求删除主来源、改变主角色或关联主来源自身行。</summary>
    PrimaryBranchProtected,
    /// <summary>元数据、时间策略或样式覆盖不合法。</summary>
    InvalidConfiguration,
    /// <summary>新关联的行不在当前真实主来源中。</summary>
    InvalidPrimaryLine,
    /// <summary>展示投影或动画策略不符合原展示合同。</summary>
    InvalidPresentation,
    /// <summary>无法分配全局唯一来源标识。</summary>
    IdentifierExhausted,
    /// <summary>最终候选的展示校验拒绝。</summary>
    DomainValidationFailed,
}

/// <summary>来源管理拒绝原因。</summary>
/// <param name="Code">中立代码。</param>
/// <param name="Detail">原领域校验的具体原因。</param>
public sealed record LyricBranchEditError(LyricBranchEditErrorCode Code, string? Detail = null);

/// <summary>来源管理原子结果，无变化和拒绝都保留当前工程实例。</summary>
public sealed record LyricBranchEditResult
{
    internal LyricBranchEditResult(bool accepted, Project project, ProjectLyricsChangeSet changeSet,
        LyricBranchEditError? error = null, string? selectedBranchId = null)
    {
        Accepted = accepted; Project = project; ChangeSet = changeSet; Error = error; SelectedBranchId = selectedBranchId;
    }

    /// <summary>请求是否接受；无变化仍为接受。</summary>
    public bool Accepted { get; }
    /// <summary>是否产生实际工程变化。</summary>
    public bool Changed => ChangeSet.HasChanges;
    /// <summary>提交结束时的当前工程。</summary>
    public Project Project { get; }
    /// <summary>供现有统一读模型采用出口消费的保守歌词摘要。</summary>
    public ProjectLyricsChangeSet ChangeSet { get; }
    /// <summary>拒绝原因；接受时为空。</summary>
    public LyricBranchEditError? Error { get; }
    /// <summary>新增建议选择新来源，删除建议安全回到主来源；配置与关联不改变客户端选择。</summary>
    public string? SelectedBranchId { get; }
}
