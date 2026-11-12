using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class StyleTemplateApplicatorTests
{
    [Fact]
    public void 行模板预演全部包含未定时行且重复套用无变化()
    {
        Project project = CreateProject();
        StyleTemplate template = StyleTemplate.Create("line-blue", "蓝色行", StyleTemplateKind.Line,
            project.LyricStyle with { SungColor = new RgbaColor(5, 60, 170) },
            [LyricStyleProperty.SungFill]);

        Assert.True(StyleTemplateApplicator.TryPreview(project, template, null, null, true,
            out StyleTemplateApplyPreview preview, out string reason), reason);
        Assert.Equal(2, preview.TargetCount);
        Assert.Equal(2, preview.ChangedCount);
        Assert.Null(project.Lyrics.Lines[0].StyleOverride);
        Assert.All(preview.Result.Lyrics.Lines,
            line => Assert.Equal(new RgbaColor(5, 60, 170), line.StyleOverride?.SungColor));
        Assert.True(StyleTemplateApplicator.TryPreview(preview.Result, template, null, null, true,
            out StyleTemplateApplyPreview repeated, out reason), reason);
        Assert.Same(preview.Result, repeated.Result);
        Assert.Equal(0, repeated.ChangedCount);
    }

    [Fact]
    public void 元素模板预演全部包含未定时元素但不改变行覆盖()
    {
        Project project = CreateProject();
        StyleTemplate template = StyleTemplate.Create("token-blue", "蓝色元素", StyleTemplateKind.Token,
            project.LyricStyle with { UnsungColor = new RgbaColor(6, 70, 180) },
            [LyricStyleProperty.UnsungFill]);

        Assert.True(StyleTemplateApplicator.TryPreview(project, template, null, null, true,
            out StyleTemplateApplyPreview preview, out string reason), reason);
        Assert.Equal(3, preview.TargetCount);
        Assert.Equal(3, preview.ChangedCount);
        Assert.All(preview.Result.Lyrics.Lines, line => Assert.Null(line.StyleOverride));
        Assert.Equal(new RgbaColor(6, 70, 180),
            preview.Result.Lyrics.Lines[1].Tokens[0].StyleOverride?.UnsungColor);
    }

    [Fact]
    public void 无效属性导致全部预演失败且原工程不变()
    {
        Project project = CreateProject();
        StyleTemplate template = StyleTemplate.Create("token-blue", "蓝色元素", StyleTemplateKind.Token,
            project.LyricStyle, [LyricStyleProperty.UnsungFill]) with
        {
            Properties = ImmutableArray.Create(LyricStyleProperty.FontSizePx),
        };

        Assert.False(StyleTemplateApplicator.TryPreview(project, template, null, null, true,
            out StyleTemplateApplyPreview preview, out string reason));
        Assert.NotEmpty(reason);
        Assert.Same(project, preview.Result);
        Assert.Equal(0, preview.ChangedCount);
        Assert.All(project.Lyrics.Lines, line => Assert.All(line.Tokens,
            token => Assert.Null(token.StyleOverride)));
    }

    [Fact]
    public void 全部行只写模板所选属性并保留元素优先覆盖()
    {
        Project project = LyricStyleEditor.SetLineOverride(CreateProject(), 0,
            new LyricStyleOverride { UnsungColor = new RgbaColor(20, 30, 40) });
        project = LyricStyleEditor.SetTokenOverride(project, 0, 0,
            new LyricStyleOverride { SungColor = new RgbaColor(120, 30, 40) });
        StyleTemplate template = StyleTemplate.Create("line-selected", "行选项",
            StyleTemplateKind.Line,
            project.LyricStyle with { SungColor = new RgbaColor(5, 60, 170) },
            [LyricStyleProperty.SungFill]);

        Assert.True(StyleTemplateApplicator.TryPreview(project, template, null, null, true,
            out StyleTemplateApplyPreview preview, out string reason), reason);

        LyricLine changed = preview.Result.Lyrics.Lines[0];
        Assert.Equal(new RgbaColor(20, 30, 40), changed.StyleOverride?.UnsungColor);
        Assert.Equal(new RgbaColor(5, 60, 170), changed.StyleOverride?.SungColor);
        Assert.Equal(new RgbaColor(120, 30, 40), changed.Tokens[0].StyleOverride?.SungColor);
        Assert.Same(project.Lyrics.Lines[0].Tokens[0], changed.Tokens[0]);
    }

    private static Project CreateProject() => Project.Create("template-batch", lyrics:
        LyricsDocument.Create(
        [
            LyricLine.Create("line-a", "甲乙", TimeRange.Create(1_000_000, 3_000_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("a", "甲", TimeRange.Create(1_000_000, 2_000_000),
                        TimingStatus.Edited),
                    LyricToken.Create("b", "乙", TimeRange.Create(2_000_000, 3_000_000),
                        TimingStatus.Edited),
                ]),
            LyricLine.Create("line-b", "丙", null, TimingStatus.Missing,
                [LyricToken.Create("c", "丙", null, TimingStatus.Missing)]),
        ]));
}
