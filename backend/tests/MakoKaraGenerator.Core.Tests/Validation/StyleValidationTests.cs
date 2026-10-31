using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Validation;

/// <summary>
/// 样式相关校验规则的测试（roadmap STYLE-01 / STYLE-02）。
/// </summary>
public sealed class StyleValidationTests
{
    [Fact]
    public void 默认样式与无覆盖的合法项目没有样式诊断()
    {
        ValidationReport report = ProjectValidator.Validate(BuildProject(null, null), ValidationContext.Open);

        Assert.DoesNotContain(report.Issues, static issue => issue.Code == DiagnosticCode.InvalidStyleValue);
        Assert.DoesNotContain(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);
    }

    [Fact]
    public void 非法基线样式是阻断性错误()
    {
        Project project = BuildProject(null, null) with
        {
            LyricStyle = new LyricStyle { FontSizePx = 0f, VerticalCenterRatio = 1.5d },
        };

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.HasErrors);
        Assert.Equal(2, report.Issues.Count(static issue => issue.Code == DiagnosticCode.InvalidStyleValue));
    }

    [Fact]
    public void 行级覆盖的字号与纵向位置被校验()
    {
        Project project = BuildProject(
            new LyricStyleOverride { FontSizePx = -3f, VerticalCenterRatio = -0.1d },
            null,
            applyLineOverride: true);

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.True(report.HasErrors);
        Assert.Equal(2, report.Issues.Count(static issue => issue.Code == DiagnosticCode.InvalidStyleValue));
    }

    [Fact]
    public void 行级颜色覆盖是合法的()
    {
        Project project = BuildProject(
            new LyricStyleOverride { SungColor = RgbaColor.Parse("#FF0000"), FontFamilyName = null },
            null,
            applyLineOverride: true);

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.DoesNotContain(report.Issues, static issue => issue.Code == DiagnosticCode.InvalidStyleValue);
        Assert.DoesNotContain(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);
    }

    [Fact]
    public void 元素级颜色覆盖不报警()
    {
        Project project = BuildProject(null, new LyricStyleOverride { SungColor = RgbaColor.Parse("#00FF00") });

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.DoesNotContain(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);
    }

    [Theory]
    [InlineData("font")]
    [InlineData("size")]
    [InlineData("ratio")]
    public void 元素级非颜色覆盖被明确报出来而不是静默忽略(string kind)
    {
        // 整行只整形一次，逐元素改字号/字体/位置会改变整行度量，
        // 因此这些覆盖目前不参与绘制——但必须说出来，不能让用户以为生效了。
        LyricStyleOverride tokenOverride = kind switch
        {
            "font" => new LyricStyleOverride { FontFamilyName = "Some Font" },
            "size" => new LyricStyleOverride { FontSizePx = 48f },
            _ => new LyricStyleOverride { VerticalCenterRatio = 0.2d },
        };

        Project project = BuildProject(null, tokenOverride);

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Equal(
            IssueSeverity.Warning,
            report.Issues.Single(static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride).Severity);
    }

    [Fact]
    public void 元素级覆盖成默认字体同样被报出来()
    {
        Project project = BuildProject(null, new LyricStyleOverride { FontFamilyName = null });

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Contains(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride
                && issue.Message.Contains("fontFamilyName", StringComparison.Ordinal));
    }

    /// <summary>
    /// 缩放属于“整体变换”，只能在行级与工程级用（ADR-0032）。
    /// </summary>
    /// <remarks>
    /// 它靠画布变换实现、作用范围是整行，元素级套上去只会误缩放整行；
    /// 若不说出来，用户会以为自己设的元素级缩放生效了。
    /// </remarks>
    [Fact]
    public void 元素级缩放覆盖被报出来()
    {
        Project project = BuildProject(null, new LyricStyleOverride { Scale = 1.5f });

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        ProjectIssue issue = report.Issues.Single(
            static item => item.Code == DiagnosticCode.UnsupportedTokenStyleOverride);

        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("scale", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>描边、阴影、不透明度是“描边类”属性，元素级能按元素裁切绘制，因此不报警。</summary>
    [Fact]
    public void 元素级描边阴影与不透明度覆盖不报警()
    {
        LyricStyleOverride tokenOverride = new()
        {
            StrokeColor = RgbaColor.Parse("#FFFFFF"),
            StrokeWidthPx = 4f,
            ShadowColor = RgbaColor.Parse("#00000080"),
            ShadowBlurPx = 6f,
            Opacity = 0.8f,
            SungGradientSpec = GradientSpec.Create([
                new GradientStop(0d, RgbaColor.Parse("#00E0FF")),
                new GradientStop(1d, RgbaColor.Parse("#7C4DFF")),
            ]),
        };

        Project project = BuildProject(null, tokenOverride);

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.DoesNotContain(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.UnsupportedTokenStyleOverride);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void 元素级不透明度越界是错误()
    {
        Project project = BuildProject(null, new LyricStyleOverride { Opacity = 1.4f });

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        Assert.Contains(
            report.Issues,
            static issue => issue.Code == DiagnosticCode.InvalidStyleValue
                && issue.Message.Contains("不透明度", StringComparison.Ordinal));
    }

    [Fact]
    public void 未接入的渐变组合被诊断而不是静默回退()
    {
        GradientSpec unsupported = GradientSpec.Create(
            [new GradientStop(0d, RgbaColor.Black), new GradientStop(1d, RgbaColor.White)],
            coordinateSpace: GradientCoordinateSpace.Glyph);
        Project project = BuildProject(
            new LyricStyleOverride { UnsungGradientSpec = unsupported },
            null,
            applyLineOverride: true);

        ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);

        ProjectIssue issue = Assert.Single(
            report.Issues.Where(static item => item.Code == DiagnosticCode.InvalidStyleValue));
        Assert.Contains("字形坐标", issue.Message, StringComparison.Ordinal);
        Assert.True(GradientSupport.IsSupported(GradientSpec.Create(
            [new GradientStop(0d, RgbaColor.Black), new GradientStop(1d, RgbaColor.White)])));
        Assert.False(GradientSupport.IsSupported(unsupported));
    }

    /// <summary>构造一个带样式覆盖的项目。</summary>
    private static Project BuildProject(
        LyricStyleOverride? lineOverride,
        LyricStyleOverride? tokenOverride,
        bool applyLineOverride = false)
    {
        LyricToken token = LyricToken.Create(
            "token-001",
            "夜",
            new TimeRange(1_200_000, 1_700_000),
            TimingStatus.Confirmed,
            tokenOverride);

        LyricLine line = LyricLine.Create(
            "line-001",
            "夜",
            new TimeRange(1_200_000, 3_400_000),
            TimingStatus.Confirmed,
            [token],
            applyLineOverride ? lineOverride : null);

        return Project.Create(
            "样式校验",
            Composition.Create(1920, 1080, FrameRate.Fps30, RgbaColor.Black, 5_000_000),
            LyricsDocument.Create([line]));
    }
}
