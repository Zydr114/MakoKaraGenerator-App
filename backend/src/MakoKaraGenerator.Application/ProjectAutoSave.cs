using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>
/// 自动保存与崩溃恢复的策略（roadmap `EDITOR-10`，ADR-0035 第三节）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层只做决定，不碰界面：什么时候写、写到哪、启动时有没有现场可恢复。
/// 界面负责"每 30 秒来问一次"和"弹对话框问用户"。
/// </para>
/// <para>
/// <b>只在有改动时写</b>：判据是"工程实例不是上一次备份时那个实例"。之所以可靠，
/// 是因为编辑函数在没有实际变化时必须返回原实例（ADR-0025），所以引用相等真的等价于
/// "自那以后没改过"。这也顺带避免了每 30 秒重写一份内容完全相同的备份。
/// </para>
/// </remarks>
public sealed class ProjectAutoSave
{
    private readonly string _stateRoot;
    private Project? _lastBackedUp;

    /// <summary>创建一个自动保存策略。</summary>
    /// <param name="stateRoot">应用状态目录（新工程的备份与现场标记都放这里）。</param>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> 为空白时抛出。</exception>
    public ProjectAutoSave(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        _stateRoot = Path.GetFullPath(stateRoot);
    }

    /// <summary>应用状态目录。</summary>
    public string StateRoot => _stateRoot;

    /// <summary>按需写一份备份。</summary>
    /// <param name="workspace">工程会话。</param>
    /// <param name="now">当前时间（由调用方给，便于测试）。</param>
    /// <returns>写出的备份目录；没有改动而跳过时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> 为 <see langword="null"/> 时抛出。</exception>
    public string? Save(ProjectWorkspace workspace, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        ProjectSaveSnapshot snapshot = workspace.CaptureSave(now);
        if (!snapshot.IsDirty || ReferenceEquals(snapshot.Project, _lastBackedUp))
        {
            return null;
        }

        string root = AutoSaveStore.ResolveRoot(snapshot.DirectoryPath, _stateRoot);
        string backupDirectory = AutoSaveStore.Save(snapshot.Project, root, now);

        AutoSaveSession.Write(
            _stateRoot,
            new AutoSaveSession(backupDirectory, snapshot.DirectoryPath, snapshot.Project.Name, now));

        _lastBackedUp = snapshot.Project;

        return backupDirectory;
    }

    /// <summary>启动时查找可恢复的未保存现场。</summary>
    /// <returns>可恢复的现场；没有（或盘上的工程其实更新）时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 只有"备份确实比工程文件新"才算现场：正常保存过之后备份就是旧的，
    /// 那时再问一遍"要恢复吗"只会让人误以为丢了东西。
    /// </remarks>
    public AutoSaveRecovery? FindRecovery()
    {
        AutoSaveInspection? inspection = InspectRecovery();
        return inspection?.Project is { } project
            ? new AutoSaveRecovery(inspection.Session, project, inspection.Report)
            : null;
    }

    /// <summary>检查恢复现场并保留版本不匹配等加载诊断，供界面明确提示。</summary>
    public AutoSaveInspection? InspectRecovery()
    {
        if (AutoSaveSession.Read(_stateRoot) is not { } session
            || !Directory.Exists(session.BackupDirectoryPath))
        {
            return null;
        }

        if (session.ProjectDirectoryPath is { } projectDirectory
            && !AutoSaveStore.IsNewerThanProject(session.BackupDirectoryPath, projectDirectory))
        {
            return null;
        }

        ProjectLoadResult result = ProjectStore.Load(session.BackupDirectoryPath);

        return new AutoSaveInspection(session, result.Project, result.Report);
    }

    /// <summary>用户已经处理过这份现场（恢复或丢弃），清掉标记。</summary>
    public void ClearSession() => AutoSaveSession.Clear(_stateRoot);
}

/// <summary>一份可恢复的未保存现场。</summary>
/// <param name="Session">现场标记。</param>
/// <param name="Project">从备份里读出来的工程数据。</param>
/// <param name="Report">读取那份备份时的校验报告。</param>
public sealed record AutoSaveRecovery(AutoSaveSession Session, Project Project, ValidationReport Report);

/// <summary>备份的读取结果；工程不可读时仍保留诊断与原始会话。</summary>
/// <param name="Session">原始备份会话。</param>
/// <param name="Project">版本与结构检查均通过时的工程。</param>
/// <param name="Report">版本或数据诊断。</param>
public sealed record AutoSaveInspection(AutoSaveSession Session, Project? Project, ValidationReport Report);
