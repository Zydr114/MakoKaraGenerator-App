using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>元素级样式覆盖的领域入口与支持范围（roadmap P1「元素级覆盖的界面作用范围」）。</summary>
/// <remarks>
/// 模型与渲染早就支持元素级覆盖，缺的一直是"谁来写它"。这一层补上写入口，
/// 并把"哪些属性能用在元素级"固化成 <see cref="TokenStyleSupport" /> 这一份事实。
/// </remarks>
public sealed class TokenStyleOverrideTests
{
    [Fact]
    public void 设置元素覆盖只影响那个元素()
    {
        Project project = BuildProject();

        Project edited = LyricStyleEditor.SetTokenOverride(
            project,
            0,
            1,
            new LyricStyleOverride { Opacity = 0.5f });

        Assert.Equal(0.5f, edited.Lyrics.Lines[0].Tokens[1].StyleOverride!.Opacity);
        Assert.Null(edited.Lyrics.Lines[0].Tokens[0].StyleOverride);
        Assert.Null(edited.Lyrics.Lines[0].StyleOverride);

        // 其它行一个字节都不该变。
        Assert.Null(edited.Lyrics.Lines[1].Tokens[0].StyleOverride);
    }

    [Fact]
    public void 改写元素覆盖是叠加上去的()
    {
        Project project = LyricStyleEditor.SetTokenOverride(
            BuildProject(),
            0,
            1,
            new LyricStyleOverride { Opacity = 0.5f });

        Project edited = LyricStyleEditor.UpdateTokenOverride(
            project,
            0,
            1,
            overlay => (overlay ?? new LyricStyleOverride()) with { StrokeWidthPx = 3f });

        LyricStyleOverride result = edited.Lyrics.Lines[0].Tokens[1].StyleOverride!;

        Assert.Equal(0.5f, result.Opacity);
        Assert.Equal(3f, result.StrokeWidthPx);
    }

    [Fact]
    public void 没有实际变化时返回原实例()
    {
        // 与其它编辑入口同一条不变量（ADR-0025）：否则撤销栈里会多出空步骤，
        // 自动保存也会把工程误判成"有未保存改动"。
        Project project = LyricStyleEditor.SetTokenOverride(
            BuildProject(),
            0,
            1,
            new LyricStyleOverride { Opacity = 0.5f });

        Assert.Same(
            project,
            LyricStyleEditor.SetTokenOverride(project, 0, 1, new LyricStyleOverride { Opacity = 0.5f }));

        // 空覆盖与"没有覆盖"等价，都收敛成 null。
        Assert.Same(
            LyricStyleEditor.ClearTokenOverride(project, 0, 0),
            LyricStyleEditor.SetTokenOverride(project, 0, 0, null));
    }

    [Fact]
    public void 空覆盖写成没有覆盖()
    {
        Project project = LyricStyleEditor.SetTokenOverride(
            BuildProject(),
            0,
            0,
            new LyricStyleOverride { Opacity = 0.5f });

        Project edited = LyricStyleEditor.SetTokenOverride(project, 0, 0, new LyricStyleOverride());

        Assert.Null(edited.Lyrics.Lines[0].Tokens[0].StyleOverride);
    }

    [Fact]
    public void 清除元素覆盖()
    {
        Project project = LyricStyleEditor.SetTokenOverride(
            BuildProject(),
            0,
            0,
            new LyricStyleOverride { Opacity = 0.5f });

        Assert.Null(LyricStyleEditor.ClearTokenOverride(project, 0, 0).Lyrics.Lines[0].Tokens[0].StyleOverride);
    }

    [Fact]
    public void 下标越界时原样返回()
    {
        Project project = BuildProject();

        Assert.Same(project, LyricStyleEditor.SetTokenOverride(project, 9, 0, new LyricStyleOverride { Opacity = 0.5f }));
        Assert.Same(project, LyricStyleEditor.SetTokenOverride(project, 0, 9, new LyricStyleOverride { Opacity = 0.5f }));
        Assert.Same(project, LyricStyleEditor.SetTokenOverride(project, -1, 0, new LyricStyleOverride { Opacity = 0.5f }));

        // 没有拆分过的行没有元素：写入应当被拒（而不是凭空造一个元素出来）。
        Project untokenized = Project.Create(
            "行",
            lyrics: LyricsDocument.Create([LyricLine.Create("l1", "夜空", timingStatus: TimingStatus.Missing)]));

        Assert.Same(untokenized, LyricStyleEditor.ClearTokenOverride(untokenized, 0, 0));
    }

    [Fact]
    public void 支持范围与校验器一致()
    {
        // 界面与校验器必须看同一份事实：否则会出现"界面允许改、一保存就报错"的组合。
        // 这里用两个代表性属性把两边钉在一起（一个支持、一个不支持）。
        Project project = BuildProject();

        Project withOpacity = LyricStyleEditor.SetTokenOverride(
            project,
            0,
            0,
            new LyricStyleOverride { Opacity = 0.5f });

        Assert.True(TokenStyleSupport.IsSupported(LyricStyleProperty.Opacity));
        Assert.DoesNotContain(
            ProjectValidator.Validate(withOpacity, ValidationContext.Open).Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);

        Project withFontSize = LyricStyleEditor.SetTokenOverride(
            project,
            0,
            0,
            new LyricStyleOverride { FontSizePx = 120f });

        Assert.False(TokenStyleSupport.IsSupported(LyricStyleProperty.FontSizePx));
        Assert.Contains(
            ProjectValidator.Validate(withFontSize, ValidationContext.Open).Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);
    }

    [Fact]
    public void 不支持的元素级属性包括整行字体参数()
    {
        // 整行排版（字体/字号/纵向位置/对齐）与整体变换（缩放）不能套在单个元素上。
        Assert.Equal(
            [
                LyricStyleProperty.FontFamilyName,
                LyricStyleProperty.FallbackFamilyNames,
                LyricStyleProperty.FontSizePx,
                LyricStyleProperty.FontWeight,
                LyricStyleProperty.IsBold,
                LyricStyleProperty.IsItalic,
                LyricStyleProperty.LetterSpacingPx,
                LyricStyleProperty.VerticalCenterRatio,
                LyricStyleProperty.HorizontalAlignment,
                LyricStyleProperty.Scale,
            ],
            TokenStyleSupport.UnsupportedAtTokenLevel);
    }

    private static Project BuildProject() => Project.Create(
        "元素",
        lyrics: LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜空",
                new TimeRange(0, 2_000_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("t1", "夜", new TimeRange(0, 1_000_000), TimingStatus.Edited),
                    LyricToken.Create("t2", "空", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited),
                ]),
            LyricLine.Create(
                "l2",
                "星",
                new TimeRange(2_000_000, 3_000_000),
                TimingStatus.Edited,
                [LyricToken.Create("t3", "星", new TimeRange(2_000_000, 3_000_000), TimingStatus.Edited)]),
        ]));
}
