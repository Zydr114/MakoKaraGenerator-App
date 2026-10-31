using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享用户设置用例的中立拒绝代码，不改变 Core 模型表达非法数据的能力。</summary>
public enum ProjectSettingsEditErrorCode
{
    /// <summary>工作区实例或代次失效。</summary>
    WorkspaceStale,
    /// <summary>被编辑字段组的原值已经改变。</summary>
    TargetStale,
    /// <summary>画布宽度或高度不是正数。</summary>
    InvalidCanvasSize,
    /// <summary>有理帧率的分子或分母不是正数。</summary>
    InvalidFrameRate,
    /// <summary>输出时长已给出但不是正数。</summary>
    InvalidOutputDuration,
    /// <summary>调用方没有提供可用的已准备媒体长度。</summary>
    MediaDurationUnavailable,
    /// <summary>最终候选的展示校验未通过。</summary>
    DomainValidationFailed,
}

/// <summary>共享设置操作的拒绝原因，由客户端负责本地化。</summary>
/// <param name="Code">中立拒绝代码。</param>
/// <param name="Detail">原领域校验给出的具体原因。</param>
public sealed record ProjectSettingsEditError(ProjectSettingsEditErrorCode Code, string? Detail = null);

/// <summary>一个完整设置字段组的原子提交结果。</summary>
public sealed record ProjectSettingsEditResult
{
    internal ProjectSettingsEditResult(bool accepted, Project project, bool changed,
        ProjectSettingsEditError? error = null)
    {
        Accepted = accepted; Project = project; Changed = changed; Error = error;
    }

    /// <summary>是否通过原值与用户设置校验，无变化也属于接受。</summary>
    public bool Accepted { get; }
    /// <summary>是否产生实际工程变化。</summary>
    public bool Changed { get; }
    /// <summary>结束时的当前工程，拒绝或无变化保留原实例。</summary>
    public Project Project { get; }
    /// <summary>拒绝原因，接受成功时为空。</summary>
    public ProjectSettingsEditError? Error { get; }
}
