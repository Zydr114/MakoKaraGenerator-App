using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>由真实工作区所有者同步捕获的不可变导入准备输入；后台准备不再读取实时工作区。</summary>
public sealed class LyricsImportEditSnapshot
{
    internal LyricsImportEditSnapshot(ProjectWorkspace workspace)
    { Workspace = workspace; Generation = workspace.Generation; Project = workspace.Project; }
    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    /// <summary>捕获时的不可变完整工程引用。</summary>
    public Project Project { get; }
}

/// <summary>固定导入或追加候选及唯一标识，提交前允许宿主完成所有读投影和回执预算。</summary>
public sealed class PreparedLyricsImportEdit
{
    internal PreparedLyricsImportEdit(LyricsImportEditSnapshot snapshot, LyricsImportEditResult result, string commandName, LyricsImportReport? importReport = null)
    { Snapshot = snapshot; Result = result; CommandName = commandName; ImportReport = importReport; }
    internal LyricsImportEditSnapshot Snapshot { get; }
    internal string CommandName { get; }
    /// <summary>尚未提交到历史的完整候选与选择反馈。</summary>
    public LyricsImportEditResult Result { get; }
    /// <summary>源选择、身份重分配与关联影响后的完整固定报告；纯文本追加为空。</summary>
    public LyricsImportReport? ImportReport { get; }
}
