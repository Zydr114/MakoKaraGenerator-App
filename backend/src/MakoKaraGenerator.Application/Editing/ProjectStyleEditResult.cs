using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享静态样式的稳定拒绝原因。</summary>
public enum ProjectStyleEditErrorCode
{
    /// <summary>工作区或代次已经变化。</summary>
    WorkspaceStale,
    /// <summary>捕获候选后的源工程已经变化。</summary>
    TargetStale,
    /// <summary>目标组合、数量或重复目标非法。</summary>
    InvalidTarget,
    /// <summary>选定属性为空、重复、未知或存在父子冲突。</summary>
    InvalidProperty,
    /// <summary>新成员值不是领域允许的规范静态值。</summary>
    InvalidValue,
    /// <summary>此层级不支持选定成员或恢复操作。</summary>
    UnsupportedProperty,
    /// <summary>候选不满足工程展示校验。</summary>
    DomainValidationFailed,
}

/// <summary>样式拒绝及可定位的输入位置。</summary>
/// <param name="Code">稳定错误代码。</param>
/// <param name="TargetIndex">存在问题的目标下标。</param>
/// <param name="Property">存在问题的规范成员。</param>
/// <param name="Detail">领域原因。</param>
public sealed record ProjectStyleEditError(ProjectStyleEditErrorCode Code, int? TargetIndex = null,
    StyleEditProperty? Property = null, string? Detail = null);

/// <summary>共享样式的纯候选或实际提交结果。</summary>
public sealed record ProjectStyleEditResult
{
    internal ProjectStyleEditResult(bool accepted, Project project, int changedTargetCount,
        ProjectStyleEditError? error = null)
    { Accepted = accepted; Project = project; ChangedTargetCount = changedTargetCount; Error = error; }

    /// <summary>请求是否合法；没有变化仍为接受。</summary>
    public bool Accepted { get; }
    /// <summary>发生实际变化的目标数。</summary>
    public int ChangedTargetCount { get; }
    /// <summary>是否存在实际源数据变化。</summary>
    public bool Changed => ChangedTargetCount != 0;
    /// <summary>完整候选或提交后的工程。</summary>
    public Project Project { get; }
    /// <summary>样式不冒充正文或时间局部变化；实际变化要求完整刷新。</summary>
    public ProjectLyricsChangeSet ChangeSet => Changed ? ProjectLyricsChangeSet.FullRefresh : ProjectLyricsChangeSet.None;
    /// <summary>拒绝原因；接受时为空。</summary>
    public ProjectStyleEditError? Error { get; }
}

/// <summary>绑定实际工作区、代次与源工程的静态样式纯候选。</summary>
public sealed class PreparedProjectStyleEdit
{
    internal PreparedProjectStyleEdit(ProjectWorkspace workspace, long generation, Project source,
        ProjectStyleEditResult result, string commandName)
    { Workspace = workspace; Generation = generation; Source = source; Result = result; CommandName = commandName; }

    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal Project Source { get; }
    /// <summary>实际历史命令名。</summary>
    public string CommandName { get; }
    /// <summary>未执行历史的固定结果。</summary>
    public ProjectStyleEditResult Result { get; }
}
