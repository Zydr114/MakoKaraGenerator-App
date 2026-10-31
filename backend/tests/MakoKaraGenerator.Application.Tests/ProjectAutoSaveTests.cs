using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Diagnostics;
using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Application.Tests;

/// <summary>自动保存策略的测试（roadmap `EDITOR-10`，ADR-0035 第三节）。</summary>
public sealed class ProjectAutoSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mkg-autosave-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void 没有改动时不写备份()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);

        Assert.Null(autoSave.Save(workspace, Now));
        Assert.False(Directory.Exists(Path.Combine(StateRoot, "autosave")));
    }

    [Fact]
    public void 有改动时写备份并记下现场()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);

        Rename(workspace, "改过的");

        string? backupDirectory = autoSave.Save(workspace, Now);

        Assert.NotNull(backupDirectory);
        Assert.True(Directory.Exists(backupDirectory));

        ProjectLoadResult reloaded = ProjectStore.Load(backupDirectory);

        Assert.NotNull(reloaded.Project);
        Assert.Equal("改过的", reloaded.Project.Name);

        // 现场标记要指向这份备份，否则下次启动根本找不到它。
        AutoSaveSession? session = AutoSaveSession.Read(StateRoot);

        Assert.NotNull(session);
        Assert.Equal(backupDirectory, session.BackupDirectoryPath);
        Assert.Null(session.ProjectDirectoryPath);
    }

    [Fact]
    public void 同一个工程实例不会重复写()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);

        Rename(workspace, "改过的");

        Assert.NotNull(autoSave.Save(workspace, Now));
        Assert.Null(autoSave.Save(workspace, Now.AddSeconds(30)));

        // 再改一次就又该写了。
        Rename(workspace, "又改了");

        Assert.NotNull(autoSave.Save(workspace, Now.AddSeconds(60)));
    }

    [Fact]
    public void 失败备份保留旧恢复现场且同一编辑实例可重试()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);
        Rename(workspace, "上次备份");
        string previous = autoSave.Save(workspace, Now)!;
        byte[] marker = File.ReadAllBytes(Path.Combine(StateRoot, AutoSaveSession.FileName));
        string missing = Path.Combine(_root, "source.wav");
        Project next = workspace.Project with { Name = "等待重试", AudioPath = missing };
        workspace.Replace(next);

        Assert.Throws<FileNotFoundException>(() => autoSave.Save(workspace, Now));
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(StateRoot, AutoSaveSession.FileName)));
        Assert.Equal("上次备份", autoSave.FindRecovery()!.Project.Name);
        Assert.Equal([previous], AutoSaveStore.List(Path.GetDirectoryName(previous)!));
        Assert.Same(next, workspace.Project);
        Assert.True(workspace.IsDirty);

        File.WriteAllBytes(missing, [1, 2, 3, 4]);
        string retried = autoSave.Save(workspace, Now)!;
        Assert.NotEqual(previous, retried);
        Assert.Equal("等待重试", autoSave.FindRecovery()!.Project.Name);
        Assert.Equal("上次备份", ProjectStore.Load(previous).Project!.Name);
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public void 已有更新备份时找得到现场()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        string projectDirectory = Path.Combine(_root, "作品");

        ProjectStore.Save(BuildProject("盘上的"), projectDirectory);

        // 把盘上那份的时间拨到过去：模拟"改完没保存就崩了"。
        File.SetLastWriteTimeUtc(
            Path.Combine(projectDirectory, ProjectJson.ProjectFileName),
            DateTime.UtcNow.AddMinutes(-5));

        ProjectWorkspace workspace = Workspace(projectDirectory);
        Rename(workspace, "改过的");
        autoSave.Save(workspace, Now);

        AutoSaveRecovery? recovery = autoSave.FindRecovery();

        Assert.NotNull(recovery);
        Assert.Equal("改过的", recovery.Project.Name);
        Assert.Equal(Path.GetFullPath(projectDirectory), recovery.Session.ProjectDirectoryPath);
        Assert.False(recovery.Report.HasErrors);
    }

    [Fact]
    public void 异版备份保留诊断且不能恢复()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);
        Rename(workspace, "改过的");
        string backupDirectory = autoSave.Save(workspace, Now)!;
        string projectPath = Path.Combine(backupDirectory, ProjectJson.ProjectFileName);
        JsonNode project = JsonNode.Parse(File.ReadAllText(projectPath))!;
        project["applicationVersion"] = "0.1.0";
        File.WriteAllText(projectPath, project.ToJsonString());

        AutoSaveInspection inspection = Assert.IsType<AutoSaveInspection>(autoSave.InspectRecovery());
        Assert.Null(inspection.Project);
        Assert.True(inspection.Report.Contains(DiagnosticCode.ApplicationVersionMismatch));
        Assert.Null(autoSave.FindRecovery());
        Assert.NotNull(AutoSaveSession.Read(StateRoot));
    }

    [Fact]
    public void 盘上的工程更新时没有现场()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        string projectDirectory = Path.Combine(_root, "作品");

        ProjectStore.Save(BuildProject("盘上的"), projectDirectory);

        ProjectWorkspace workspace = Workspace(projectDirectory);
        Rename(workspace, "改过的");
        autoSave.Save(workspace, Now);

        // 之后正常保存过：备份就是旧的了，不该再问"要恢复吗"。
        ProjectStore.Save(workspace.Project, projectDirectory);
        File.SetLastWriteTimeUtc(
            Path.Combine(projectDirectory, ProjectJson.ProjectFileName),
            DateTime.UtcNow.AddMinutes(5));

        Assert.Null(autoSave.FindRecovery());
    }

    [Fact]
    public void 清掉标记后不再报告现场()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);

        Rename(workspace, "改过的");
        autoSave.Save(workspace, Now);

        Assert.NotNull(autoSave.FindRecovery());

        autoSave.ClearSession();

        Assert.Null(autoSave.FindRecovery());
    }

    [Fact]
    public void 备份被删掉后不算现场()
    {
        ProjectAutoSave autoSave = new(StateRoot);
        ProjectWorkspace workspace = Workspace(directoryPath: null);

        Rename(workspace, "改过的");
        string backupDirectory = autoSave.Save(workspace, Now)!;

        Directory.Delete(backupDirectory, recursive: true);

        Assert.Null(autoSave.FindRecovery());
    }

    [Fact]
    public void 恢复出来的工程算有未保存改动()
    {
        // 关键：恢复的内容与盘上的文件不同，必须算"有改动"——
        // 否则用户一关窗口就真的丢了，而且界面上还显示"已保存"。
        string projectDirectory = Path.Combine(_root, "作品");
        ProjectWorkspace workspace = Workspace(projectDirectory);

        workspace.Recover(BuildProject("恢复的"), projectDirectory);

        Assert.True(workspace.IsDirty);
        Assert.Equal(Path.GetFullPath(projectDirectory), workspace.DirectoryPath);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void 参数不合法时直接报错()
    {
        Assert.Throws<ArgumentException>(() => new ProjectAutoSave("  "));

        ProjectAutoSave autoSave = new(StateRoot);

        Assert.Throws<ArgumentNullException>(() => autoSave.Save(null!, Now));
    }

    private static DateTimeOffset Now => new(2026, 9, 25, 18, 40, 12, TimeSpan.Zero);

    private string StateRoot => Path.Combine(_root, "state");

    private static ProjectWorkspace Workspace(string? directoryPath)
    {
        if (directoryPath is not null)
        {
            ProjectStore.Save(BuildProject("盘上的"), directoryPath);
        }

        return ProjectWorkspace.Adopt(BuildProject("盘上的"), directoryPath, isSaved: true);
    }

    private static void Rename(ProjectWorkspace workspace, string name) => workspace.Execute(
        ProjectCommand.Create("test.rename", project => project with { Name = name }));

    private static Project BuildProject(string name) => Project.Create(
        name,
        Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 2_000_000),
        LyricsDocument.Create(
        [
            LyricLine.Create("l1", "夜", new TimeRange(0, 1_000_000), TimingStatus.Edited),
        ]));
}
