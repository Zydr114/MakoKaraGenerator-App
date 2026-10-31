using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectRevisionWorkspaceTests
{
    [Fact]
    public void 另存为和移动工程不依赖原歌词目录()
    {
        using TemporaryDirectory directory = new();
        string source = directory.FilePath("source.mkgproj");
        string copy = directory.FilePath("copy.mkgproj");
        string moved = directory.FilePath("moved.mkgproj");
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source());
        workspace.SaveAs(source);
        byte[] bytes = File.ReadAllBytes(ProjectFileFixture.LyricsPath(source));
        workspace.SaveAs(copy);
        Directory.Move(copy, moved);
        Directory.Move(source, directory.FilePath("source-unavailable.mkgproj"));
        Assert.Equal(Source(), ProjectStore.Load(moved).Project);
        Assert.Equal(bytes, File.ReadAllBytes(ProjectFileFixture.LyricsPath(moved)));
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void 自动备份拥有自己的修订并可独立恢复()
    {
        using TemporaryDirectory directory = new();
        string source = directory.FilePath("source.mkgproj");
        string detached = directory.FilePath("backup.mkgproj");
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source());
        workspace.SaveAs(source);
        Project edited = Source("改后歌词");
        workspace.Replace(edited);
        ProjectAutoSave autoSave = new(directory.FilePath("state"));
        string backup = autoSave.Save(workspace, DateTimeOffset.UtcNow)!;
        Assert.True(workspace.IsDirty);
        Assert.Equal(edited, ProjectStore.Load(backup).Project);
        Assert.True(File.Exists(ProjectFileFixture.LyricsPath(backup)));
        Assert.Equal(Source(), ProjectStore.Load(source).Project);
        Directory.Move(backup, detached);
        Directory.Move(source, directory.FilePath("source-unavailable.mkgproj"));
        ProjectWorkspace recovered = ProjectWorkspace.CreateNew("恢复");
        Assert.True(recovered.Open(detached).Loaded);
        Assert.Equal(edited, recovered.Project);
        Assert.False(recovered.IsDirty);
    }

    [Fact]
    public void Linux入口发布失败不更新saved引用路径或当前编辑()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using TemporaryDirectory directory = new();
        string original = directory.FilePath("original.mkgproj");
        string target = directory.FilePath("target.mkgproj");
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source());
        workspace.SaveAs(original);
        ProjectStore.Save(Source(), target);
        Project edited = Source("未保存的歌词");
        workspace.Replace(edited);
        UnixFileMode mode = File.GetUnixFileMode(target);
        try
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Throws<UnauthorizedAccessException>(() => workspace.SaveAs(target));
            Assert.Same(edited, workspace.Project);
            Assert.Equal(original, workspace.DirectoryPath);
            Assert.True(workspace.IsDirty);
            Assert.Equal(Source(), ProjectStore.Load(target).Project);
        }
        finally { File.SetUnixFileMode(target, mode); }
        workspace.Save();
        Assert.False(workspace.IsDirty);
        Assert.Equal(edited, ProjectStore.Load(original).Project);
    }

    [Fact]
    public void 修订损坏不能替换当前工作区或造成空歌词保存()
    {
        using TemporaryDirectory directory = new();
        string target = directory.FilePath("damaged.mkgproj");
        ProjectStore.Save(Source(), target);
        File.AppendAllText(ProjectFileFixture.LyricsPath(target), " ");
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Source());
        Project edited = Source("在手的工作");
        workspace.Replace(edited);
        ProjectLoadResult result = workspace.Open(target);
        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.LyricsContentMismatch));
        Assert.Same(edited, workspace.Project);
        Assert.True(workspace.IsDirty);
        Assert.False(workspace.HasPath);
    }

    private static Project Source(string text = "你好") => Project.Create("独立工程", lyrics: LyricsDocument.Create([
        LyricLine.Create("line", text, new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("token", text, new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed)]),
    ]));
}
