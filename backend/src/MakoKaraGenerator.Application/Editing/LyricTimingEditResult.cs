using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>时间组提交的拒绝原因；客户端根据代码显示本地化提示。</summary>
public enum LyricTimingEditErrorCode
{
    /// <summary>输入所属工作区代次已失效。</summary>
    WorkspaceStale,

    /// <summary>稳定目标不存在，或其时间已经不同于输入捕获时的时间。</summary>
    TargetStale,

    /// <summary>端点未提供且原时间中也没有可保留的端点。</summary>
    MissingEndpoint,

    /// <summary>终点超过已经准备的媒体时长。</summary>
    BeyondContentDuration,

    /// <summary>时间包含性、顺序或正式展示程序校验未通过。</summary>
    DomainValidationFailed,

    /// <summary>元素起点为负；调用方可保留原输入解析提示。</summary>
    InvalidStart,

    /// <summary>元素终点不晚于起点。</summary>
    InvalidRange,

    /// <summary>微调或拖动要求目标已有有效时间。</summary>
    Untimed,

    /// <summary>微调会越出非负或媒体边界，或整数微秒运算溢出。</summary>
    NudgeOutOfBounds,

    /// <summary>此操作只接受元素目标。</summary>
    TokenRequired,

    /// <summary>手势已经结束，或历史连续性／工程基线已经失效。</summary>
    GestureStale,
}

/// <summary>中立错误及领域规则给出的具体原因，不拼接客户端提示。</summary>
/// <param name="Code">稳定错误代码。</param>
/// <param name="Detail">可供显示的领域校验原因；客户端门禁错误可为空。</param>
public sealed record LyricTimingEditError(LyricTimingEditErrorCode Code, string? Detail = null);

/// <summary>时间组提交的结果；拒绝或无实际变化时保留原工程引用。</summary>
public sealed record LyricTimingEditResult
{
    internal LyricTimingEditResult(
        bool accepted, Project project, ProjectTimingChangeSet changeSet, LyricTimingEditError? error = null)
    {
        Accepted = accepted;
        Project = project;
        ChangeSet = changeSet;
        Error = error;
    }

    /// <summary>输入是否通过全部门禁；无变化也属于接受成功。</summary>
    public bool Accepted { get; }

    /// <summary>用例完成后的当前工程。</summary>
    public Project Project { get; }

    /// <summary>是否产生实际工程变化。</summary>
    public bool Changed => ChangeSet.HasChanges;

    /// <summary>受影响的稳定目标及预览失效范围。</summary>
    public ProjectTimingChangeSet ChangeSet { get; }

    /// <summary>拒绝原因；接受成功时为空。</summary>
    public LyricTimingEditError? Error { get; }
}
