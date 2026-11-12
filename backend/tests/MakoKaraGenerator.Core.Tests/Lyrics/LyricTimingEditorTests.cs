using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class LyricTimingEditorTests
{
    [Fact]
    public void 显式整行平移保持相对时间且失败原样返回()
    {
        Project original = Sample();
        Assert.True(LyricTimingEditor.TryShiftLineAndTokens(original, "line", 500_000,
            out Project shifted, out string reason), reason);
        LyricLine line = shifted.Lyrics.Lines[0];
        Assert.Equal(1_500_000, line.Time!.Value.StartUs);
        Assert.Equal(1_500_000, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(2_500_000, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(TimingStatus.Edited, line.Tokens[0].TimingStatus);

        Assert.False(LyricTimingEditor.TryShiftLineAndTokens(original, "line", -2_000_000,
            out Project rejected, out reason));
        Assert.NotEmpty(reason);
        Assert.Same(original, rejected);
        Assert.True(LyricTimingEditor.TryShiftLineAndTokens(original, "line", 0,
            out Project unchanged, out reason), reason);
        Assert.Same(original, unchanged);
    }

    [Fact]
    public void 整行结构替换前可统一检查越行与重叠()
    {
        Project project = Sample();
        LyricLine line = project.Lyrics.Lines[0];
        Assert.True(LyricTimingEditor.TryValidateLine(line, out _));
        LyricLine overlap = line with
        {
            Tokens = line.Tokens.SetItem(1,
                line.Tokens[1] with { Time = TimeRange.Create(1_500_000, 3_000_000) }),
        };
        Assert.False(LyricTimingEditor.TryValidateLine(overlap, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void 相邻元素的单边编辑只改变选中元素并允许空档()
    {
        Project original = Sample();

        bool accepted = LyricTimingEditor.TrySetTokenTime(
            original, "line", "b", 2_400_000, 3_000_000,
            out Project result, out string reason);

        Assert.True(accepted, reason);
        LyricLine line = Assert.Single(result.Lyrics.Lines);
        Assert.Same(original.Lyrics.Lines[0].Tokens[0], line.Tokens[0]);
        Assert.Equal(2_000_000, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(2_400_000, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(original.Lyrics.Lines[0].Time, line.Time);
    }

    [Theory]
    [InlineData(1_900_000, 3_000_000)]
    [InlineData(2_000_000, 5_100_000)]
    public void 元素重叠或越行时原工程引用不变(long startUs, long endUs)
    {
        Project original = Sample();

        bool accepted = LyricTimingEditor.TrySetTokenTime(
            original, "line", "b", startUs, endUs,
            out Project result, out string reason);

        Assert.False(accepted);
        Assert.NotEmpty(reason);
        Assert.Same(original, result);
    }

    [Fact]
    public void 行边界缩入元素时拒绝且不改元素()
    {
        Project original = Sample();

        bool accepted = LyricTimingEditor.TrySetLineTime(
            original, "line", 2_100_000, 5_000_000,
            out Project result, out string reason);

        Assert.False(accepted);
        Assert.NotEmpty(reason);
        Assert.Same(original, result);
    }

    [Fact]
    public void 相同时间重复提交不产生新实例()
    {
        Project original = Sample();

        Assert.True(LyricTimingEditor.TrySetTokenTime(
            original, "line", "b", 2_000_000, 3_000_000,
            out Project result, out _));
        Assert.Same(original, result);
    }

    [Fact]
    public void 清除单个元素时间只使该元素回到缺失状态()
    {
        Project original = Sample();

        Assert.True(LyricTimingEditor.TryClearTokenTime(original, "line", "a",
            out Project cleared, out string reason), reason);
        LyricLine line = Assert.Single(cleared.Lyrics.Lines);
        Assert.Null(line.Tokens[0].Time);
        Assert.Equal(TimingStatus.Missing, line.Tokens[0].TimingStatus);
        Assert.True(line.Tokens[0].IsTimeStatusConsistent);
        Assert.Same(original.Lyrics.Lines[0].Tokens[1], line.Tokens[1]);
        Assert.Equal(original.Lyrics.Lines[0].Time, line.Time);
        Assert.True(LyricTimingEditor.TryValidateLine(line, out reason), reason);

        Assert.True(LyricTimingEditor.TryClearTokenTime(cleared, "line", "a",
            out Project unchanged, out reason), reason);
        Assert.Same(cleared, unchanged);
        Assert.False(LyricTimingEditor.TryClearTokenTime(cleared, "line", "unknown",
            out Project rejected, out reason));
        Assert.Same(cleared, rejected);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void 已定时元素跨位默认拒绝确认后重分配时间槽()
    {
        Project original = Sample();
        Assert.False(LyricTokenReorderEditor.TryReorder(original, "line", "b", 0,
            reassignTimeSlots: false, out Project rejected, out _));
        Assert.Same(original, rejected);

        Assert.True(LyricTokenReorderEditor.TryReorder(original, "line", "b", 0,
            reassignTimeSlots: true, out Project accepted, out string reason), reason);
        LyricLine line = accepted.Lyrics.Lines[0];
        Assert.Equal("乙甲", line.Text);
        Assert.Equal(["b", "a"], line.Tokens.Select(static token => token.Id));
        Assert.Equal(1_000_000, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(2_000_000, line.Tokens[1].Time!.Value.StartUs);
        Assert.All(line.Tokens, static token => Assert.Equal(TimingStatus.Edited, token.TimingStatus));
    }

    [Fact]
    public void 行与元素文本不一致时重排不会静默改写行文本()
    {
        Project original = Sample();
        LyricLine line = original.Lyrics.Lines[0] with { Text = "甲乙！" };
        original = original with { Lyrics = original.Lyrics with
        {
            Lines = original.Lyrics.Lines.SetItem(0, line),
        } };

        Assert.False(LyricTokenReorderEditor.TryReorder(original, "line", "b", 0,
            reassignTimeSlots: true, out Project rejected, out string reason));
        Assert.NotEmpty(reason);
        Assert.Same(original, rejected);
    }

    [Fact]
    public void 模板只写入指定行或元素的选定属性()
    {
        Project original = Sample();
        LyricStyle style = original.LyricStyle with { FontSizePx = 96f };
        StyleTemplate row = StyleTemplate.Create("r", "行", StyleTemplateKind.Line, style,
            [LyricStyleProperty.FontSizePx]);
        Assert.True(StyleTemplateApplicator.TryApply(original, row, "line", null,
            out Project withRow, out string rowReason), rowReason);
        Assert.Equal(96f, withRow.Lyrics.Lines[0].StyleOverride!.FontSizePx);
        Assert.Null(withRow.Lyrics.Lines[0].Tokens[0].StyleOverride);

        LyricStyle tokenStyle = style with { SungColor = new RgbaColor(1, 2, 3) };
        StyleTemplate token = StyleTemplate.Create("t", "元素", StyleTemplateKind.Token, tokenStyle,
            [LyricStyleProperty.SungFill]);
        Assert.True(StyleTemplateApplicator.TryApply(withRow, token, "line", "b",
            out Project withToken, out string tokenReason), tokenReason);
        Assert.Equal(new RgbaColor(1, 2, 3), withToken.Lyrics.Lines[0].Tokens[1].StyleOverride!.SungColor);
        Assert.Null(withToken.Lyrics.Lines[0].Tokens[0].StyleOverride);
        Assert.Equal(96f, withToken.Lyrics.Lines[0].StyleOverride!.FontSizePx);
    }

    private static Project Sample() => Project.Create("test", lyrics: LyricsDocument.Create(
    [
        LyricLine.Create("line", "甲乙", TimeRange.Create(1_000_000, 5_000_000), TimingStatus.Confirmed,
        [
            LyricToken.Create("a", "甲", TimeRange.Create(1_000_000, 2_000_000), TimingStatus.Confirmed),
            LyricToken.Create("b", "乙", TimeRange.Create(2_000_000, 3_000_000), TimingStatus.Edited),
        ]),
    ]));
}
