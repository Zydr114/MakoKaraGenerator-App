using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>可选的有界准备观察器；桌面旧 API 默认不设宿主预算，观察器可抛错终止纯准备。</summary>
public sealed record LyricTokenPreparationObservers(Action<int>? OnStartingLine = null, Action? OnCreatingSegment = null,
    Action? OnCreatingToken = null, Action<int, LyricLine>? OnPreparedLine = null);

/// <summary>固定的元素结构候选与反馈；不拥有第二份工作区或历史。</summary>
public sealed class PreparedLyricTokenEdit
{
    internal PreparedLyricTokenEdit(ProjectWorkspace workspace, long generation, Project source, LyricTokenEditResult result, string commandName)
    { Workspace = workspace; Generation = generation; Source = source; Result = result; CommandName = commandName; }

    /// <summary>纯准备的候选与真实选择／计数。</summary>
    public LyricTokenEditResult Result { get; }
    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal Project Source { get; }
    internal string CommandName { get; }
}
