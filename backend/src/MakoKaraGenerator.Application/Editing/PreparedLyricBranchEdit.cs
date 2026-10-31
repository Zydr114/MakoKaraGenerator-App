using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>来源管理的固定纯候选；宿主可先检查响应预算，再交唯一历史提交。</summary>
public sealed class PreparedLyricBranchEdit
{
    internal PreparedLyricBranchEdit(ProjectWorkspace workspace, long generation, Project source,
        LyricBranchEditResult result, string commandName)
    {
        Workspace = workspace; Generation = generation; Source = source; Result = result; CommandName = commandName;
    }

    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal Project Source { get; }
    internal string CommandName { get; }
    /// <summary>已完成领域检查的候选结果；准备不执行历史或修改工作区。</summary>
    public LyricBranchEditResult Result { get; }
}
