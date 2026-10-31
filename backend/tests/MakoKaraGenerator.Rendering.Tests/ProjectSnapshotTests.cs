using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// 渲染快照的语义测试（roadmap RENDER-01）。
/// </summary>
/// <remarks>
/// 核心要证明的是"渲染读不到编辑状态"：快照一经建立就与项目数据脱钩，
/// 之后无论怎么改项目都不会影响已经交给渲染的那一份。
/// </remarks>
public sealed class ProjectSnapshotTests
{
    [Fact]
    public void 从项目创建快照会带上渲染需要的全部数据()
    {
        Project project = CreateProject();

        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);

        Assert.Equal(project.Name, snapshot.Name);
        Assert.Equal(project.SchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(project.Composition, snapshot.Composition);
        Assert.Equal(project.Lyrics, snapshot.Lyrics);
        Assert.Equal(2, snapshot.LineCount);
    }

    [Fact]
    public void 快照建立后修改项目不会改变快照()
    {
        Project project = CreateProject();
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);

        // 用 with 表达式模拟一次编辑：改画布尺寸、改歌词时间、加一行。
        Project edited = project with
        {
            Name = "改过名字的项目",
            Composition = project.Composition with { CanvasWidth = 1280, CanvasHeight = 720 },
            Lyrics = LyricsDocument.Create(
                [
                    .. project.Lyrics.Lines,
                    LyricLine.Create(
                        "l3",
                        "新加的一行",
                        new TimeRange(TimeUs.FromSeconds(9), TimeUs.FromSeconds(12)),
                        TimingStatus.Imported),
                ]),
        };

        Assert.Equal("测试项目", snapshot.Name);
        Assert.Equal(1920, snapshot.Composition.CanvasWidth);
        Assert.Equal(2, snapshot.LineCount);

        // 而新快照当然能看到改动，说明"脱钩"是快照行为，不是项目数据不更新。
        ProjectSnapshot next = ProjectSnapshot.FromProject(edited);
        Assert.Equal("改过名字的项目", next.Name);
        Assert.Equal(1280, next.Composition.CanvasWidth);
        Assert.Equal(3, next.LineCount);
    }

    [Fact]
    public void 内容相同的快照相等()
    {
        ProjectSnapshot first = ProjectSnapshot.FromProject(CreateProject());
        ProjectSnapshot second = ProjectSnapshot.FromProject(CreateProject());

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void 歌词不同的快照不等()
    {
        Project project = CreateProject();
        ProjectSnapshot first = ProjectSnapshot.FromProject(project);

        ProjectSnapshot second = ProjectSnapshot.FromProject(
            project with { Lyrics = LyricsDocument.Create([LyricLine.Create("l1", "只有一行")]) });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 空引用被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectSnapshot.FromProject(null!));
    }

    private static Project CreateProject()
    {
        LyricsDocument lyrics = LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜空中最亮的星",
                new TimeRange(TimeUs.FromSeconds(1), TimeUs.FromSeconds(4)),
                TimingStatus.Imported),
            LyricLine.Create(
                "l2",
                "请照亮我前行",
                new TimeRange(TimeUs.FromSeconds(4), TimeUs.FromSeconds(8)),
                TimingStatus.Imported),
        ]);

        return Project.Create("测试项目", Composition.Create(1920, 1080, FrameRate.Fps30), lyrics);
    }
}
