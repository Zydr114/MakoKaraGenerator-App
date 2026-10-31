using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>一个不可变、尚未提交的歌词内容候选；不会拥有第二份工作区或历史。</summary>
public sealed class PreparedLyricContentEdit
{
    internal PreparedLyricContentEdit(ProjectWorkspace workspace, long generation, Project source,
        LyricContentEditResult result, string commandName, bool mergeable, ProjectEditMergeIdentity? identity)
    {
        Workspace = workspace; Generation = generation; Source = source; Result = result;
        CommandName = commandName; Mergeable = mergeable; Identity = identity;
    }

    /// <summary>准备阶段的固定候选与真实反馈；读取不会提交任何状态。</summary>
    public LyricContentEditResult Result { get; }

    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal Project Source { get; }
    internal string CommandName { get; }
    internal bool Mergeable { get; }
    internal ProjectEditMergeIdentity? Identity { get; }
}
