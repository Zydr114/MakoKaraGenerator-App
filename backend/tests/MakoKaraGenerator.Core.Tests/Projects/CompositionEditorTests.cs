using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>合成设置编辑规则的测试（roadmap §18.4 的「合成参数界面」）。</summary>
/// <remarks>
/// 这里守两条最容易写错的东西：一是"没有实际变化必须返回原实例"
/// （引用比较是"有没有未保存改动"的判据，凭空造新实例等于把工程弄脏），
/// 二是"按歌词末尾"不得给缺失时间的元素编造位置。
/// </remarks>
public sealed class CompositionEditorTests
{
    [Fact]
    public void 设置画布尺寸只换尺寸并保留其余字段()
    {
        Project project = BuildProject();

        Project edited = CompositionEditor.SetCanvasSize(project, 1280, 720);

        Assert.Equal(1280, edited.Composition.CanvasWidth);
        Assert.Equal(720, edited.Composition.CanvasHeight);
        Assert.Equal(project.Composition.FrameRate, edited.Composition.FrameRate);
        Assert.Equal(project.Composition.BackgroundColor, edited.Composition.BackgroundColor);
        Assert.Equal(project.Composition.OutputDurationUs, edited.Composition.OutputDurationUs);
    }

    [Fact]
    public void 尺寸没变时返回原实例()
    {
        Project project = BuildProject();

        Project edited = CompositionEditor.SetCanvasSize(
            project,
            project.Composition.CanvasWidth,
            project.Composition.CanvasHeight);

        Assert.Same(project, edited);
    }

    [Fact]
    public void 帧率没变时返回原实例()
    {
        Project project = BuildProject();

        Assert.Same(project, CompositionEditor.SetFrameRate(project, project.Composition.FrameRate));
    }

    [Fact]
    public void 底色没变时返回原实例()
    {
        Project project = BuildProject();

        Assert.Same(project, CompositionEditor.SetBackgroundColor(project, project.Composition.BackgroundColor));
    }

    [Fact]
    public void 时长没变时返回原实例()
    {
        Project withDuration = CompositionEditor.SetOutputDuration(BuildProject(), 5_000_000);

        Assert.Same(withDuration, CompositionEditor.SetOutputDuration(withDuration, 5_000_000));

        // 连续两次"未确定"也要是同一个实例，否则清空一次就会把工程弄脏。
        Project withoutDuration = CompositionEditor.SetOutputDuration(withDuration, null);

        Assert.Same(withoutDuration, CompositionEditor.SetOutputDuration(withoutDuration, null));
    }

    [Fact]
    public void 设置帧率底色与时长各自生效()
    {
        Project project = BuildProject();

        Project edited = CompositionEditor.SetFrameRate(project, FrameRate.Fps60);

        Assert.Equal(FrameRate.Fps60, edited.Composition.FrameRate);
        Assert.Equal(project.Composition.CanvasWidth, edited.Composition.CanvasWidth);

        edited = CompositionEditor.SetBackgroundColor(edited, RgbaColor.Parse("#204080"));

        Assert.Equal(RgbaColor.Parse("#204080"), edited.Composition.BackgroundColor);

        edited = CompositionEditor.SetOutputDuration(edited, 84_900_000);

        Assert.Equal(84_900_000, edited.Composition.OutputDurationUs);
    }

    [Fact]
    public void 输出时长可以清回未确定()
    {
        Project project = CompositionEditor.SetOutputDuration(BuildProject(), 1_000_000);

        Project cleared = CompositionEditor.SetOutputDuration(project, null);

        Assert.Null(cleared.Composition.OutputDurationUs);
        Assert.NotSame(project, cleared);
    }

    [Fact]
    public void 编辑不会修改传入的工程()
    {
        Project project = BuildProject();

        _ = CompositionEditor.SetCanvasSize(project, 640, 360);

        Assert.Equal(1920, project.Composition.CanvasWidth);
        Assert.Equal(1080, project.Composition.CanvasHeight);
    }

    [Fact]
    public void 非法尺寸可以写进模型由校验器报出来()
    {
        // ADR-0006：领域模型允许表示非法数据，逐项诊断归校验器。
        // 界面在提交前自己拦，这里必须"照写不误"，否则两处校验会互相打架。
        Project edited = CompositionEditor.SetCanvasSize(BuildProject(), 0, -5);

        Assert.Equal(0, edited.Composition.CanvasWidth);
        Assert.Equal(-5, edited.Composition.CanvasHeight);
        Assert.False(edited.Composition.IsValid);
    }

    [Fact]
    public void 按歌词末尾取行级时间的终点()
    {
        Project project = BuildProject();

        long? endUs = CompositionEditor.LyricsEndUs(project);

        Assert.Equal(3_000_000, endUs);
    }

    [Fact]
    public void 按歌词末尾取逐字时间里最靠后的终点()
    {
        Project project = BuildProject(
            LyricLine.Create(
                "l1",
                "夜",
                new TimeRange(0, 3_000_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("t1", "夜", new TimeRange(0, 1_200_000), TimingStatus.Edited),
                    LyricToken.Create("t2", "空", new TimeRange(1_200_000, 2_500_000), TimingStatus.Estimated),
                ]));

        Assert.Equal(3_000_000, CompositionEditor.LyricsEndUs(project));
    }

    [Fact]
    public void 按歌词末尾跳过缺失与非法时间的元素()
    {
        // 关键回归：不能让缺失时间的元素把"末尾"算到别处去，也不能因此报错。
        Project project = BuildProject(
            LyricLine.Create("l1", "夜", new TimeRange(0, 2_000_000), TimingStatus.Edited),
            LyricLine.Create(
                "l2",
                "空",
                timingStatus: TimingStatus.Missing,
                tokens: [LyricToken.Create("t1", "空", timingStatus: TimingStatus.Missing)]),
            LyricLine.Create("l3", "的", new TimeRange(5_000_000, 4_000_000), TimingStatus.Invalid));

        Assert.Equal(2_000_000, CompositionEditor.LyricsEndUs(project));
    }

    [Fact]
    public void 没有歌词时按歌词末尾没有结果()
    {
        Project project = Project.Create("空歌词");

        Assert.Null(CompositionEditor.LyricsEndUs(project));
    }

    [Theory]
    [InlineData("1920", 1920)]
    [InlineData(" 1080 ", 1080)]
    [InlineData("16", 16)]
    public void 画布边长接受正整数(string text, int expected)
    {
        Assert.True(CompositionEditor.TryParseCanvasDimension(text, out int dimension));
        Assert.Equal(expected, dimension);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1920")]
    [InlineData("1920.0")]
    [InlineData("1,920")]
    [InlineData("0x780")]
    [InlineData("1080p")]
    [InlineData("2147483648")]
    [InlineData(null)]
    public void 画布边长拒绝非正整数(string? text)
    {
        // 非法输入一律拒绝，绝不取整、绝不夹到范围（docs/development.md §19）。
        Assert.False(CompositionEditor.TryParseCanvasDimension(text, out _));
    }

    private static Project BuildProject(params LyricLine[] lines) => Project.Create(
        "合成",
        Composition.Create(1920, 1080, FrameRate.Fps30),
        LyricsDocument.Create(
            lines.Length > 0
                ? [.. lines]
                : [LyricLine.Create("l1", "夜", new TimeRange(0, 3_000_000), TimingStatus.Edited)]));
}
