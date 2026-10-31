using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

/// <summary>工程会话（新建/打开/保存/另存为）的测试。</summary>
public sealed class ProjectWorkspaceTests
{
    [Fact]
    public void 新建的工程按默认值构造且不算有改动()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("新歌");

        Assert.Equal("新歌", workspace.Project.Name);
        Assert.Equal(1920, workspace.Project.Composition.CanvasWidth);
        Assert.Equal(1080, workspace.Project.Composition.CanvasHeight);
        Assert.Null(workspace.Project.Composition.OutputDurationUs);
        Assert.Empty(workspace.Project.Lyrics.Lines);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.HasPath);
    }

    [Fact]
    public void 新建工程直接用默认基线而不引用旧预设()
    {
        Project project = ProjectWorkspace.CreateDefaultProject("新歌");

        Assert.Null(project.LyricStylePresetId);
        Assert.Equal(LyricStyle.Default, project.LyricStyle);
    }

    [Fact]
    public void 重复写入相同工程基线不会把新工程变脏()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(ProjectWorkspace.CreateDefaultProject("新歌"));

        workspace.Replace(StyleBatchEditor.Apply(workspace.Project, StyleTargetLevel.Project,
            [], workspace.Project.LyricStyle, LyricStylePropertyExtensions.All));

        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void 编辑之后变脏保存之后干净()
    {
        using TemporaryDirectory directory = new();
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("新歌");
        string path = Path.Combine(directory.Path, "新歌.mkgproj");

        workspace.Replace(workspace.Project with { Name = "改过名" });

        Assert.True(workspace.IsDirty);

        workspace.SaveAs(path);

        Assert.False(workspace.IsDirty);
        Assert.Equal(Path.GetFullPath(path), workspace.DirectoryPath);
    }

    [Fact]
    public void 保存会真的落盘并能重新打开()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "春を連れて.mkgproj");

        ProjectWorkspace workspace = ProjectWorkspace.Adopt(BuildProject());
        workspace.SaveAs(path);

        Assert.True(File.Exists(Path.Combine(path, "project.json")));
        Assert.True(File.Exists(ProjectFileFixture.LyricsPath(path)));

        ProjectLoadResult result = ProjectWorkspace.CreateNew("别的东西").Open(path);

        Assert.True(result.Loaded);
        Assert.False(result.Report.HasErrors);
        Assert.Equal("春を連れて", result.Project!.Name);
        Assert.Equal("夜", result.Project.Lyrics.Lines[0].Text);
        Assert.Equal(TimeUs.FromMilliseconds(1000), result.Project.Lyrics.Lines[0].Time!.Value.StartUs);
    }

    [Fact]
    public void 重新打开后样式与行覆盖都还在()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "样式.mkgproj");

        Project project = BuildProject() with
        {
            LyricStyle = LyricStyle.Default with { FontSizePx = 96f },
            LyricStylePresetId = "karaoke-large",
        };

        LyricStyleOverride lineOverride = new()
        {
            FontSizePx = 48f,
            // "覆盖成后端默认字体"：这是三态存在的理由，最容易在存读之间丢掉。
            FontFamilyName = OptionalValue.Set<string?>(null),
        };

        project = project with
        {
            Lyrics = project.Lyrics with
            {
                Lines = [project.Lyrics.Lines[0] with { StyleOverride = lineOverride }],
            },
        };

        ProjectWorkspace.Adopt(project).SaveAs(path);

        ProjectLoadResult result = ProjectWorkspace.CreateNew("x").Open(path);
        Project loaded = result.Project!;

        Assert.Equal(96f, loaded.LyricStyle.FontSizePx);
        Assert.Equal("karaoke-large", loaded.LyricStylePresetId);
        Assert.Equal(48f, loaded.Lyrics.Lines[0].StyleOverride!.FontSizePx);
        Assert.True(loaded.Lyrics.Lines[0].StyleOverride!.FontFamilyName.IsSet);
        Assert.Null(loaded.Lyrics.Lines[0].StyleOverride!.FontFamilyName.Value);
    }

    [Fact]
    public void 保存到原有目录会覆盖而不是追加()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "覆盖.mkgproj");

        ProjectWorkspace.Adopt(BuildProject()).SaveAs(path);

        ProjectWorkspace second = ProjectWorkspace.Adopt(BuildProject() with { Name = "第二版" });
        second.SaveAs(path);

        string[] files = [.. Directory.GetFiles(path).Select(Path.GetFileName).OfType<string>().Order()];

        Assert.Equal(["project.json"], files);
        Assert.Single(Directory.GetFiles(Path.Combine(path, ProjectJson.LyricsDirectoryName)));

        ProjectLoadResult result = ProjectWorkspace.CreateNew("x").Open(path);

        Assert.Equal("第二版", result.Project!.Name);
    }

    [Fact]
    public void 没有落盘位置时保存会明确报错()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("新歌");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(workspace.Save);

        Assert.Contains("另存为", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 打开失败不动当前工程()
    {
        using TemporaryDirectory directory = new();
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("在手的工作");
        workspace.Replace(workspace.Project with { Name = "改了一半" });

        ProjectLoadResult result = workspace.Open(directory.Path);

        Assert.False(result.Loaded);
        Assert.True(result.Report.HasErrors);
        Assert.Equal("改了一半", workspace.Project.Name);
        Assert.True(workspace.IsDirty);
        Assert.False(workspace.HasPath);
    }

    [Fact]
    public void 带校验错误的工程照样能打开()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "坏样式.mkgproj");

        ProjectWorkspace.Adopt(BuildProject()).SaveAs(path);

        // 直接改 JSON：领域模型不允许构造非法值，但文件可能是手工改过或旧版本写的。
        string projectJson = File.ReadAllText(Path.Combine(path, "project.json"));
        File.WriteAllText(
            Path.Combine(path, "project.json"),
            projectJson.Replace("\"fontSizePx\": 72", "\"fontSizePx\": -5", StringComparison.Ordinal));

        ProjectLoadResult result = ProjectWorkspace.CreateNew("x").Open(path);

        Assert.True(result.Loaded);
        Assert.True(result.Report.HasErrors);
        Assert.False(result.CanExport);
    }

    [Fact]
    public void 先保存再改再保存会写回原目录()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "回写.mkgproj");

        ProjectWorkspace workspace = ProjectWorkspace.Adopt(BuildProject());
        workspace.SaveAs(path);

        workspace.Replace(workspace.Project with { Name = "改过的名字" });
        workspace.Save();

        ProjectLoadResult result = ProjectWorkspace.CreateNew("x").Open(path);

        Assert.Equal("改过的名字", result.Project!.Name);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void 接管演示工程时默认当作已保存()
    {
        // 启动时装的内置演示工程没有落盘位置，但不该一打开就提示"未保存"。
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(BuildProject());

        Assert.False(workspace.IsDirty);
        Assert.False(workspace.HasPath);

        ProjectWorkspace markedDirty = ProjectWorkspace.Adopt(BuildProject(), isSaved: false);

        Assert.True(markedDirty.IsDirty);
    }

    [Fact]
    public void 撤销回到未保存状态后不再算脏()
    {
        // 保存 → 再改 → 撤销：应当精确回到"已保存"的那个实例，因此重新变干净。
        // 这是"脏 = 与最后落盘实例不同一"这条判据自然带来的好处，不需要额外标记。
        using TemporaryDirectory directory = new();
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("新歌");
        workspace.SaveAs(Path.Combine(directory.Path, "新歌.mkgproj"));

        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "改过" }));

        Assert.True(workspace.IsDirty);
        Assert.Equal("新歌", workspace.Undo().Name);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void 撤销之后重做又变脏()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("新歌");

        Project edited = workspace.Execute(
            ProjectCommand.Create("rename", project => project with { Name = "改过" }));

        workspace.Undo();

        Assert.Same(edited, workspace.Redo());
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public void 打开工程会清空历史()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "工程.mkgproj");
        ProjectWorkspace.Adopt(BuildProject()).SaveAs(path);

        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("在手的工作");
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "改过" }));

        Assert.True(workspace.CanUndo);

        workspace.Open(path);

        Assert.False(workspace.CanUndo);
        Assert.False(workspace.CanRedo);
    }

    [Fact]
    public void 新建的工程没有历史()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("甲");
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "乙" }));

        Assert.True(workspace.CanUndo);

        ProjectWorkspace fresh = ProjectWorkspace.CreateNew("丙");

        Assert.False(fresh.CanUndo);
        Assert.False(fresh.CanRedo);
        Assert.Null(fresh.UndoName);
    }

    [Fact]
    public void 在当前会话中新建会保留会话实例并重置保存基线()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("甲");
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "改过" }));

        Project next = ProjectWorkspace.CreateDefaultProject(
            "乙",
            Composition.Create(1280, 720, FrameRate.Fps24));

        workspace.StartNew(next);

        Assert.Same(next, workspace.Project);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.HasPath);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.CanRedo);
        Assert.Equal(1280, workspace.Project.Composition.CanvasWidth);
        Assert.Equal(FrameRate.Fps24, workspace.Project.Composition.FrameRate);
    }

    [Fact]
    public void 启动初始化完成后可以建立干净基线并清除初始化历史()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("演示");
        workspace.Execute(ProjectCommand.Create(
            "media.import-audio",
            project => project with { AudioPath = "/tmp/demo.wav" }));

        Assert.True(workspace.IsDirty);
        Assert.True(workspace.CanUndo);

        workspace.AcceptCurrentAsSaved();

        Assert.False(workspace.IsDirty);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.CanRedo);
    }

    [Fact]
    public void 合并组把多次改写记成一步()
    {
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("新歌");

        using (workspace.BeginGroup("stamp"))
        {
            workspace.Execute(ProjectCommand.Create("time", project => project with { Name = "一" }));
            workspace.Execute(ProjectCommand.Create("estimate", project => project with { Name = "二" }));
        }

        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal("stamp", workspace.History.UndoName);
        Assert.Equal("新歌", workspace.Undo().Name);
    }

    private static Project BuildProject() => Project.Create(
        "春を連れて",
        Composition.Create(1920, 1080, FrameRate.Fps30),
        LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜",
                new TimeRange(TimeUs.FromMilliseconds(1000), TimeUs.FromMilliseconds(2000)),
                TimingStatus.Edited,
                [LyricToken.Create(
                    "t1",
                    "夜",
                    new TimeRange(TimeUs.FromMilliseconds(1000), TimeUs.FromMilliseconds(2000)),
                    TimingStatus.Edited)]),
        ]));
}
