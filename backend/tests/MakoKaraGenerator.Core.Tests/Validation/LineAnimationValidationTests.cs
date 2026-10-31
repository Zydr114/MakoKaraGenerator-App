using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Validation;

/// <summary>行进入动画的校验规则测试（roadmap `STYLE-04`）。</summary>
public sealed class LineAnimationValidationTests
{
    [Fact]
    public void 缺失或合法的动画都没有诊断()
    {
        Assert.Empty(AnimationIssues(Project.Create("工程")));
        Assert.Empty(AnimationIssues(WithAnimation(LineAnimation.Default)));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(10_000_001L)]
    public void 时长越界被报为错误(long durationUs)
    {
        Assert.NotEmpty(AnimationIssues(WithAnimation(new LineAnimation { DurationUs = durationUs })));
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void 起始不透明度越界被报为错误(float opacity)
    {
        Assert.NotEmpty(AnimationIssues(WithAnimation(new LineAnimation { StartOpacity = opacity })));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(5f)]
    public void 起始缩放越界被报为错误(float scale)
    {
        Assert.NotEmpty(AnimationIssues(WithAnimation(new LineAnimation { StartScale = scale })));
    }

    [Fact]
    public void 纵向偏移过大被报为错误()
    {
        Assert.NotEmpty(AnimationIssues(WithAnimation(new LineAnimation { StartOffsetPx = -20_000f })));
    }

    [Fact]
    public void 行级动画同样被校验()
    {
        Project project = Project.Create(
            "工程",
            lyrics: LyricsDocument.Create(
            [
                LyricLine.Create("l1", "行"),
            ])).WithLinePresentation("l1", animation: new LineAnimation { StartScale = 0f });

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Contains(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.InvalidLineAnimation
                && issue.Location is not null
                && issue.Location.Contains("enterAnimation", StringComparison.Ordinal));
    }

    private static Project WithAnimation(LineAnimation animation) =>
        Project.Create(
            "工程",
            lyrics: LyricsDocument.Create([LyricLine.Create("l1", "行")]),
            animation: animation);

    private static List<ProjectIssue> AnimationIssues(Project project) =>
        [.. ProjectValidator.Validate(project, ValidationContext.Open)
            .Issues
            .Where(static issue => issue.Code == DiagnosticCode.InvalidLineAnimation)];
}
