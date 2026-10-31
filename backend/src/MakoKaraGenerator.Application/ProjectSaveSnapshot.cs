using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>由工作区所有者捕获的保存输入；后台不得再次读取可变工作区。</summary>
public sealed record ProjectSaveSnapshot(Project Project, string? DirectoryPath, long Generation, bool IsDirty, DateTimeOffset CapturedAtUtc);

/// <summary>一次写入实际使用的快照、发布结果或失败；完成不代表当前工程已保存。</summary>
public sealed record ProjectSaveResult(ProjectSaveSnapshot Snapshot, long Epoch, bool IsManual,
    string? DirectoryPath, IReadOnlyList<ProjectIssue> Issues, Exception? Error, Exception? RecoveryError = null)
{
    /// <summary>数据与本次所需恢复标记都成功发布。</summary>
    public bool Succeeded => Error is null;
}

/// <summary>同步的存储边界，由协调器在自己的唯一后台写入任务上调用。</summary>
public interface IProjectSaveStorage
{
    /// <summary>完整发布独立备份；不发布恢复标记。</summary>
    string WriteBackup(ProjectSaveSnapshot snapshot);
    /// <summary>原子保存指定的不可变工程。</summary>
    IReadOnlyList<ProjectIssue> WriteProject(ProjectSaveSnapshot snapshot, string directoryPath);
    /// <summary>数据成功后单独发布恢复标记。</summary>
    void PublishRecovery(AutoSaveSession session);
    /// <summary>仅清理本协调器此前发布的标记，不删除另一份恢复现场。</summary>
    void ClearRecovery(AutoSaveSession expected);
}

internal sealed class ProjectSaveStorage(string stateRoot) : IProjectSaveStorage
{
    public string WriteBackup(ProjectSaveSnapshot snapshot) => AutoSaveStore.Save(snapshot.Project,
        AutoSaveStore.ResolveRoot(snapshot.DirectoryPath, stateRoot), snapshot.CapturedAtUtc);
    public IReadOnlyList<ProjectIssue> WriteProject(ProjectSaveSnapshot snapshot, string directoryPath)
    {
        List<ProjectIssue> issues = [];
        ProjectStore.Save(snapshot.Project, directoryPath, issues);
        return issues.ToArray();
    }
    public void PublishRecovery(AutoSaveSession session) => AutoSaveSession.Write(stateRoot, session);
    public void ClearRecovery(AutoSaveSession expected)
    {
        if (AutoSaveSession.Read(stateRoot) == expected) AutoSaveSession.Clear(stateRoot);
    }
}
