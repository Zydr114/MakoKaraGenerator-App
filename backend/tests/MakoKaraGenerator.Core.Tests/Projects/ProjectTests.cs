using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary><see cref="Project"/> 聚合根的组装与相等性测试。</summary>
public sealed class ProjectTests
{
    [Fact]
    public void 新建项目带有可用的默认值()
    {
        Project project = Project.Create("未命名项目");

        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal("未命名项目", project.Name);
        Assert.Equal(1920, project.Composition.CanvasWidth);
        Assert.Empty(project.Lyrics.Lines);
    }

    [Fact]
    public void 项目名称不能为空白()
    {
        Assert.Throws<ArgumentException>(() => Project.Create("   "));
        Assert.Throws<ArgumentNullException>(() => Project.Create(null!));
    }

    [Fact]
    public void 项目可以组装画布与歌词()
    {
        LyricsDocument lyrics = LyricsDocument.Create(
        [
            LyricLine.Create(
                "line-001",
                "你好吗",
                new TimeRange(1_200_000, 3_400_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("token-001", "你", new TimeRange(1_200_000, 1_700_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-002", "好", new TimeRange(1_700_000, 2_300_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-003", "吗", new TimeRange(2_300_000, 3_400_000), TimingStatus.Estimated),
                ]),
        ]);

        Project project = Project.Create(
            "测试项目",
            Composition.Create(1080, 1920, FrameRate.NtscVideo, RgbaColor.Transparent, 90_000_000),
            lyrics);

        Assert.Single(project.Lyrics.Lines);
        Assert.Equal(3, project.Lyrics.TokenCount);
        Assert.Equal(RgbaColor.Transparent, project.Composition.BackgroundColor);
    }

    [Fact]
    public void 相等性按内容比较且不受集合引用影响()
    {
        Project first = BuildSampleProject();
        Project second = BuildSampleProject();

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void 改动歌词后不再相等()
    {
        Project original = BuildSampleProject();

        Project edited = original with
        {
            Lyrics = original.Lyrics with
            {
                Lines = [original.Lyrics.Lines[0] with { TimingStatus = TimingStatus.Confirmed }],
            },
        };

        Assert.NotEqual(original, edited);
    }

    [Fact]
    public void 改动画布后不再相等()
    {
        Project original = BuildSampleProject();

        Assert.NotEqual(original, original with { Composition = original.Composition with { CanvasWidth = 1280 } });
    }

    [Fact]
    public void 编辑操作不修改原项目实例()
    {
        // 不可变模型是撤销/重做与渲染快照的基础（docs/development.md §10.2、roadmap CORE-07）。
        Project original = BuildSampleProject();
        Project renamed = original with { Name = "改名后" };

        Assert.Equal("测试项目", original.Name);
        Assert.Equal("改名后", renamed.Name);
        Assert.Equal(original.Lyrics, renamed.Lyrics);
    }

    private static Project BuildSampleProject() => Project.Create(
        "测试项目",
        Composition.Create(1920, 1080, FrameRate.Fps30, RgbaColor.Parse("#101010"), 5_000_000),
        LyricsDocument.Create(
        [
            LyricLine.Create(
                "line-001",
                "你好吗",
                new TimeRange(1_200_000, 3_400_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("token-001", "你", new TimeRange(1_200_000, 1_700_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-002", "好", new TimeRange(1_700_000, 2_300_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-003", "吗", new TimeRange(2_300_000, 3_400_000), TimingStatus.Estimated),
                ]),
            LyricLine.Create("line-002", "世界"),
        ],
        "zh-CN"));
}
