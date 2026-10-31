using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>行进入动画的求值与继承规则测试（roadmap `STYLE-04`）。</summary>
public sealed class LineAnimationTests
{
    [Fact]
    public void 没有动画时求值为空()
    {
        Assert.Null(LineAnimationEvaluator.Evaluate(animation: null, lineStartUs: 1_000_000, timeUs: 1_200_000));
    }

    [Theory]
    [InlineData(AnimationEasing.Linear, 0.5)]
    [InlineData(AnimationEasing.EaseIn, 0.25)]
    [InlineData(AnimationEasing.EaseOut, 0.75)]
    [InlineData(AnimationEasing.EaseInOut, 0.5)]
    public void 四种缓动在中点的取值固定(AnimationEasing easing, double expected)
    {
        Assert.Equal(expected, AnimationEasingFunctions.Evaluate(easing, 0.5d), 6);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void 缓动输入会被夹到零到一(double progress)
    {
        double value = AnimationEasingFunctions.Evaluate(AnimationEasing.Linear, progress);

        Assert.InRange(value, 0d, 1d);
    }

    [Fact]
    public void 行开始之前停在起始值()
    {
        LineAnimation animation = new() { DurationUs = 400_000, StartOpacity = 0f, StartScale = 1.5f, StartOffsetPx = 30f };

        LineAnimationValues? before = LineAnimationEvaluator.Evaluate(animation, lineStartUs: 1_000_000, timeUs: 900_000);

        Assert.Equal(0f, before!.Value.Opacity);
        Assert.Equal(1.5f, before.Value.Scale);
        Assert.Equal(30f, before.Value.VerticalOffsetPx);
    }

    [Fact]
    public void 时长走完后回到行自己的样式()
    {
        LineAnimation animation = new() { DurationUs = 400_000, StartOpacity = 0f };
        LineAnimationValues after = LineAnimationEvaluator.Evaluate(animation, elapsedUs: 400_000);

        Assert.Equal(LineAnimationValues.Neutral, after);
    }

    [Fact]
    public void 时长为零表示立即到位()
    {
        LineAnimation animation = new() { DurationUs = 0, StartOpacity = 0f, StartOffsetPx = 40f };

        Assert.Equal(LineAnimationValues.Neutral, LineAnimationEvaluator.Evaluate(animation, elapsedUs: 0));
    }

    [Fact]
    public void 起点按缓动插值()
    {
        // 缓出：走一半时已经完成 75%。
        LineAnimation animation = new()
        {
            DurationUs = 400_000,
            Easing = AnimationEasing.EaseOut,
            StartOpacity = 0f,
            StartScale = 2f,
            StartOffsetPx = 40f,
        };

        LineAnimationValues half = LineAnimationEvaluator.Evaluate(animation, elapsedUs: 200_000);

        Assert.Equal(0.75f, half.Opacity, 5);
        Assert.Equal(1.25f, half.Scale, 5);
        Assert.Equal(10f, half.VerticalOffsetPx, 5);
    }

    [Fact]
    public void 设置工程默认动画且无变化返回原实例()
    {
        Project project = Project.Create("工程");

        Project edited = LineAnimationEditor.SetProjectAnimation(project, LineAnimation.Default);

        Assert.NotNull(edited.EnterAnimation);
        Assert.Same(edited, LineAnimationEditor.SetProjectAnimation(edited, LineAnimation.Default));
        Assert.NotSame(project, edited);
    }

    [Fact]
    public void 行级覆盖优先于工程默认且能清回继承()
    {
        Project project = LineAnimationEditor.SetProjectAnimation(
            Project.Create(
                "工程",
                lyrics: LyricsDocument.Create(
                [
                    LyricLine.Create("l1", "第一行", TimeRange.Create(0, 1_000_000), TimingStatus.Edited),
                    LyricLine.Create("l2", "第二行", TimeRange.Create(1_000_000, 2_000_000), TimingStatus.Edited),
                ])),
            LineAnimation.Default);

        LineAnimation lineAnimation = new() { DurationUs = 900_000, Easing = AnimationEasing.Linear };
        Project edited = LineAnimationEditor.SetLineAnimation(project, 1, lineAnimation);

        Assert.Equal(LineAnimation.Default, LineAnimationEditor.Resolve(edited, edited.Lyrics.Lines[0]));
        Assert.Equal(lineAnimation, LineAnimationEditor.Resolve(edited, edited.Lyrics.Lines[1]));
        Assert.Same(edited, LineAnimationEditor.SetLineAnimation(edited, 1, lineAnimation));

        Project cleared = LineAnimationEditor.ClearLineAnimation(edited, 1);

        Assert.Equal(LineAnimation.Default, LineAnimationEditor.Resolve(cleared, cleared.Lyrics.Lines[1]));
        Assert.Same(cleared, LineAnimationEditor.ClearLineAnimation(cleared, 1));
    }

    [Fact]
    public void 关掉工程默认会让继承的行一起关掉()
    {
        Project project = LineAnimationEditor.SetProjectAnimation(
            Project.Create("工程", lyrics: LyricsDocument.Create([LyricLine.Create("l1", "行")])),
            LineAnimation.Default);

        Project edited = LineAnimationEditor.SetProjectAnimation(project, animation: null);

        Assert.Null(LineAnimationEditor.Resolve(edited, edited.Lyrics.Lines[0]));
    }

    [Fact]
    public void 行下标越界时原样返回()
    {
        Project project = Project.Create("工程", lyrics: LyricsDocument.Create([LyricLine.Create("l1", "行")]));

        Assert.Same(project, LineAnimationEditor.SetLineAnimation(project, 5, LineAnimation.Default));
        Assert.Same(project, LineAnimationEditor.SetLineAnimation(project, -1, LineAnimation.Default));
    }

    [Fact]
    public void 默认动画不是中性且缺省值有实际效果()
    {
        Assert.False(LineAnimation.Default.IsNeutral);
        Assert.Equal(AnimationEasing.EaseOut, LineAnimation.Default.Easing);
        Assert.Equal(400_000L, LineAnimation.Default.DurationUs);
        Assert.True(new LineAnimation { StartOpacity = 1f, StartScale = 1f, StartOffsetPx = 0f }.IsNeutral);
    }
}
