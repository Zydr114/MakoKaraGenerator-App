using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>播放打点的中立结果；客户端负责播放头、选择和本地化提示。</summary>
public sealed record LyricTimingStampResult
{
    internal LyricTimingStampResult(bool accepted, Project project, ProjectTimingChangeSet changeSet,
        LyricTimingEditError? error = null, bool estimatedPlaceholder = false)
    {
        Accepted = accepted;
        Project = project;
        ChangeSet = changeSet;
        Error = error;
        EstimatedPlaceholder = estimatedPlaceholder;
    }

    /// <summary>是否通过稳定目标、时间边界和领域校验；无变化也属于接受。</summary>
    public bool Accepted { get; }

    /// <summary>是否实际写入了工程。</summary>
    public bool Changed => ChangeSet.HasChanges;

    /// <summary>用例完成后的工程；拒绝或无变化时保留原引用。</summary>
    public Project Project { get; }

    /// <summary>受影响的稳定元素及预览失效摘要。</summary>
    public ProjectTimingChangeSet ChangeSet { get; }

    /// <summary>拒绝原因；接受成功时为空。</summary>
    public LyricTimingEditError? Error { get; }

    /// <summary>起点打点是否使用了程序生成的一帧估算终点。</summary>
    public bool EstimatedPlaceholder { get; }
}
