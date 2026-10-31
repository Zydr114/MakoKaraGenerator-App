using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// 时间 → 画面状态的采样测试（roadmap RENDER-03：进度必须始终可预测）。
/// </summary>
public sealed class LyricSamplerTests
{
    [Fact]
    public void 首行之前没有可显示的行()
    {
        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(), Us(500));

        Assert.False(sample.HasLine);
        Assert.Equal(-1, sample.LineIndex);
        Assert.Equal(0d, sample.LineProgress);
        Assert.Equal(ProgressSource.Unavailable, sample.LineProgressSource);
    }

    [Fact]
    public void 行结束后立即离屏()
    {
        // 第二行 4s ~ 8s；终点是半开区间的排除边界。
        Assert.Equal(1, LyricSampler.Sample(CreateDocument(), Us(5_000_000)).LineIndex);
        Assert.Equal(-1, LyricSampler.Sample(CreateDocument(), Us(8_000_000)).LineIndex);
        Assert.Equal(-1, LyricSampler.Sample(CreateDocument(), Us(10_000_000)).LineIndex);
    }

    [Fact]
    public void 行内的行进度按时间区间线性推进并夹在范围内()
    {
        LyricLine line = CreateLine("l1", "夜", 2_000_000, 4_000_000);

        Assert.Equal(0d, LyricSampler.ComputeLineProgress(line, Us(2_000_000)).Progress, 1e-9);
        Assert.Equal(0.5d, LyricSampler.ComputeLineProgress(line, Us(3_000_000)).Progress, 1e-9);
        Assert.Equal(1d, LyricSampler.ComputeLineProgress(line, Us(9_999_999)).Progress, 1e-9);
        Assert.Equal(0d, LyricSampler.ComputeLineProgress(line, Us(1_000_000)).Progress, 1e-9);
        Assert.Equal(ProgressSource.FromLine, LyricSampler.ComputeLineProgress(line, Us(3_000_000)).Source);
    }

    [Fact]
    public void 没有时间的行进度为零且标为不可用()
    {
        LyricLine line = LyricLine.Create("l1", "夜");

        (double progress, ProgressSource source) = LyricSampler.ComputeLineProgress(line, Us(3_000_000));

        Assert.Equal(0d, progress);
        Assert.Equal(ProgressSource.Unavailable, source);
    }

    [Fact]
    public void 元素进度来自元素自己的时间区间()
    {
        LyricLine line = CreateLine(
            "l1",
            "夜空",
            0,
            4_000_000,
            CreateToken("t1", "夜", 0, 1_000_000),
            CreateToken("t2", "空", 1_000_000, 2_000_000));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(1_500_000));

        Assert.Equal(2, sample.Tokens.Length);
        Assert.Equal(ProgressSource.FromToken, sample.Tokens[0].Source);
        Assert.Equal(1d, sample.Tokens[0].Progress, 1e-9);
        Assert.Equal(0.5d, sample.Tokens[1].Progress, 1e-9);
        Assert.True(sample.Tokens[1].IsCurrent);
        Assert.Equal(1, sample.CurrentTokenIndex);
    }

    [Theory]
    [InlineData(1_500_000)]
    [InlineData(3_500_000)]
    [InlineData(4_750_000)]
    public void 行内空档显示整行但没有活动逐字动画(long timeUs)
    {
        LyricLine line = CreateLine("l1", "夜空", 1_000_000, 5_000_000,
            CreateToken("t1", "夜", 2_000_000, 3_000_000),
            CreateToken("t2", "空", 4_000_000, 4_500_000));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), timeUs);

        Assert.True(sample.HasLine);
        Assert.Equal(-1, sample.CurrentTokenIndex);
        Assert.All(sample.Tokens, static token => Assert.False(token.IsCurrent));
        Assert.False(LyricSampler.Sample(CreateDocument(line), 5_000_000).HasLine);
    }

    [Fact]
    public void 元素时间的端点分别对应零与一()
    {
        LyricLine line = CreateLine(
            "l1",
            "夜空",
            0,
            4_000_000,
            CreateToken("t1", "夜", 1_000_000, 2_000_000),
            CreateToken("t2", "空", 2_000_000, 3_000_000));

        LyricFrameSample atStart = LyricSampler.Sample(CreateDocument(line), Us(1_000_000));
        LyricFrameSample atEnd = LyricSampler.Sample(CreateDocument(line), Us(2_000_000));

        Assert.Equal(0d, atStart.Tokens[0].Progress, 1e-9);
        Assert.True(atStart.Tokens[0].IsCurrent);
        Assert.Equal(1d, atEnd.Tokens[0].Progress, 1e-9);
        Assert.Equal(0d, atEnd.Tokens[1].Progress, 1e-9);
        Assert.Equal(1, atEnd.CurrentTokenIndex);
    }

    [Fact]
    public void 没有时间的元素不会被推算逐字进度()
    {
        // 行 0s ~ 4s："夜" 0~1s，"星" 3~4s，中间"空"与"中"没有时间；
        // 这段空档不能再按字符数分摊给它们。
        LyricLine line = CreateLine(
            "l1",
            "夜空中星",
            0,
            4_000_000,
            CreateToken("t1", "夜", 0, 1_000_000),
            LyricToken.Create("t2", "空"),
            LyricToken.Create("t3", "中"),
            CreateToken("t4", "星", 3_000_000, 4_000_000));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(1_500_000));

        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[1].Source);
        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[2].Source);

        Assert.Equal(0d, sample.Tokens[1].Progress, 1e-9);
        Assert.Equal(0d, sample.Tokens[2].Progress, 1e-9);
        Assert.Equal(0d, sample.Tokens[3].Progress, 1e-9);
        Assert.Equal(-1, sample.CurrentTokenIndex);

        LyricFrameSample middle = LyricSampler.Sample(CreateDocument(line), Us(2_500_000));
        Assert.Equal(0d, middle.Tokens[1].Progress, 1e-9);
        Assert.Equal(0d, middle.Tokens[2].Progress, 1e-9);
        Assert.Equal(-1, middle.CurrentTokenIndex);
    }

    [Fact]
    public void 行首无时间元素保持不可用()
    {
        // 没有时间元素在行首：下界应当是行起点 0s；上界是下一个有时间元素的起点 2s。
        LyricLine line = CreateLine(
            "l1",
            "夜空",
            0,
            4_000_000,
            LyricToken.Create("t1", "夜"),
            CreateToken("t2", "空", 2_000_000, 4_000_000));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(1_000_000));

        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[0].Source);
        Assert.Equal(0d, sample.Tokens[0].Progress, 1e-9);
    }

    [Fact]
    public void 坏时间区间既不参与计算也不会被跨过外推()
    {
        // 中间元素终点早于起点（非法）：它前面的元素没有时间、后面也没有可用的上界，
        // 因此前后两段都应当保持"不可用"，而不是拿行区间把整段填满。
        LyricLine line = CreateLine(
            "l1",
            "夜空中",
            0,
            4_000_000,
            LyricToken.Create("t1", "夜"),
            LyricToken.Create("t2", "空", new TimeRange(3_000_000, 1_000_000), TimingStatus.Invalid),
            LyricToken.Create("t3", "中"));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(2_000_000));

        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[0].Source);
        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[1].Source);
        Assert.Equal(0d, sample.Tokens[1].Progress);
        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[2].Source);
    }

    [Fact]
    public void 零长度区间按无可用时间处理()
    {
        LyricLine line = CreateLine(
            "l1",
            "夜",
            0,
            4_000_000,
            LyricToken.Create("t1", "夜", new TimeRange(2_000_000, 2_000_000), TimingStatus.Invalid));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(2_000_000));

        Assert.Equal(ProgressSource.Unavailable, sample.Tokens[0].Source);
        Assert.Equal(0d, sample.Tokens[0].Progress);
    }

    [Fact]
    public void 没有任何时间的行仍然显示但进度不可用()
    {
        // 无时间行不参与选行（选行必须有起始时刻），因此这里用一首首行带时间的文档，
        // 第二行整行没有时间：它不会被选中，但一旦被选中（下面用带时间的行验证）
        // 进度也应当是 Unavailable。
        LyricLine line = LyricLine.Create("l1", "夜", null, TimingStatus.Missing, [CreateToken("t1", "夜", 0, 1_000_000)]);

        (double progress, ProgressSource source) = LyricSampler.ComputeLineProgress(line, Us(500_000));

        Assert.Equal(0d, progress);
        Assert.Equal(ProgressSource.Unavailable, source);
    }

    [Fact]
    public void 每个元素的进度随时间单调不减且始终落在_0_到_1_之间()
    {
        // 刻意用混合数据：未定时元素始终保持不可用。
        LyricLine line = CreateLine(
            "l1",
            "夜空中最亮的星",
            0,
            5_000_000,
            CreateToken("t1", "夜", 0, 500_000),
            CreateToken("t2", "空", 500_000, 1_000_000),
            LyricToken.Create("t3", "中"),
            LyricToken.Create("t4", "最亮"),
            CreateToken("t5", "的星", 3_000_000, 5_000_000));

        double[] previous = new double[line.Tokens.Length];
        Array.Fill(previous, -1d);

        for (long timeUs = 0; timeUs < 5_000_000; timeUs += 100_000)
        {
            LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), timeUs);

            Assert.Equal(line.Tokens.Length, sample.Tokens.Length);

            foreach (TokenSample token in sample.Tokens)
            {
                Assert.InRange(token.Progress, 0d, 1d);
                Assert.True(
                    token.Progress >= previous[token.Index],
                    $"{timeUs} µs 时第 {token.Index} 个元素的进度 {token.Progress} 比之前 {previous[token.Index]} 更小。");
                previous[token.Index] = token.Progress;
            }
        }
    }

    [Fact]
    public void 填充边界随时间不会倒退()
    {
        // 这是"高亮不跳字"的底层保证：随时间推进，"第一个还没唱完的元素"
        // 只会往后走，不会回到前面。注意单个元素的进度可以一前一后（真实数据里
        // 前一个元素唱完后一个元素才刚开始是常态），但视觉上的填充边界必须单调。
        LyricLine line = CreateLine(
            "l1",
            "夜空中最亮的星",
            0,
            5_000_000,
            CreateToken("t1", "夜", 0, 500_000),
            LyricToken.Create("t2", "空"),
            LyricToken.Create("t3", "中"),
            CreateToken("t4", "最亮", 2_000_000, 3_000_000),
            CreateToken("t5", "的星", 3_000_000, 5_000_000));

        int previousBoundary = -1;

        for (long timeUs = 0; timeUs < 5_000_000; timeUs += 50_000)
        {
            LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), timeUs);
            int boundary = 0;

            while (boundary < sample.Tokens.Length && sample.Tokens[boundary].IsSung)
            {
                boundary++;
            }

            Assert.True(
                boundary >= previousBoundary,
                $"{timeUs} µs 时填充边界退回到第 {boundary} 个元素（之前是 {previousBoundary}）。");
            previousBoundary = boundary;
        }

        Assert.Equal(1, previousBoundary);
    }

    [Fact]
    public void 元素文本拼不回行文本时会被标记出来()
    {
        // 行文本是"夜空"，但元素只有"夜"——数据被外部改坏过。
        LyricLine line = CreateLine("l1", "夜空", 0, 4_000_000, CreateToken("t1", "夜", 0, 4_000_000));

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(1_000_000));

        Assert.False(sample.TokensMatchLineText);
    }

    [Fact]
    public void 无元素的行仍然可以有行进度()
    {
        LyricLine line = CreateLine("l1", "夜空中最亮的星", 0, 4_000_000);

        LyricFrameSample sample = LyricSampler.Sample(CreateDocument(line), Us(2_000_000));

        Assert.True(sample.HasLine);
        Assert.False(sample.HasTokens);
        Assert.Equal(0.5d, sample.LineProgress, 1e-9);
        Assert.Equal(ProgressSource.FromLine, sample.LineProgressSource);
        Assert.Equal(-1, sample.CurrentTokenIndex);
    }

    [Fact]
    public void 乱序文档也能稳定选行()
    {
        LyricsDocument lyrics = LyricsDocument.Create(
        [
            CreateLine("l1", "本来在第一行", 4_000_000, 8_000_000),
            CreateLine("l2", "时间较早的行", 1_000_000, 3_000_000),
        ]);

        Assert.Equal(0, LyricSampler.FindLineIndexAt(lyrics, Us(5_000_000)));
        Assert.Equal(1, LyricSampler.FindLineIndexAt(lyrics, Us(2_000_000)));
        Assert.Equal(-1, LyricSampler.FindLineIndexAt(lyrics, Us(500_000)));
    }

    [Fact]
    public void 采样是纯函数且不修改文档()
    {
        LyricsDocument lyrics = CreateDocument();

        LyricFrameSample first = LyricSampler.Sample(lyrics, Us(2_000_000));
        LyricFrameSample second = LyricSampler.Sample(lyrics, Us(2_000_000));

        Assert.Equal(first, second);
        Assert.Equal("夜空中最亮的星", lyrics.Lines[0].Text);
        Assert.Null(lyrics.Lines[0].Tokens.FirstOrDefault()?.Time);
    }

    [Fact]
    public void 空引用被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => LyricSampler.Sample(null!, 0));
        Assert.Throws<ArgumentNullException>(() => LyricSampler.FindLineIndexAt(null!, 0));
        Assert.Throws<ArgumentNullException>(() => LyricSampler.ComputeLineProgress(null!, 0));
    }

    [Fact]
    public void 注音片段按自身绝对时间采样且不改变基础元素进度()
    {
        LyricLine line = CreateLine("line", "先", 0, 10_000, CreateToken("base", "先", 0, 10_000)) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby", "base", "せん") with
            { Segments = [RubyTextSegment.Create("se", "せ", new(1_000, 2_000), TimingStatus.Imported),
                RubyTextSegment.Create("n", "ん", new(3_000, 4_000), TimingStatus.Imported)] }] };
        LyricFrameSample sample = LyricSampler.SampleLine(line, 1_500);
        Assert.Equal(.15d, sample.Tokens[0].Progress);
        Assert.Equal(.5d, sample.RubySegments[0].Progress); Assert.True(sample.RubySegments[0].IsCurrent);
        Assert.Equal(ProgressSource.FromRubySegment, sample.RubySegments[0].Source);
        Assert.Equal(0d, sample.RubySegments[1].Progress); Assert.False(sample.RubySegments[1].IsCurrent);
        Assert.Equal(1, sample.RubySegments[1].Utf16Start);
        LyricFrameSample gap = LyricSampler.SampleLine(line, 2_500);
        Assert.True(gap.RubySegments[0].IsSung); Assert.False(gap.RubySegments[1].IsCurrent);
    }

    [Fact]
    public void 单段无时间注音继续跟随基础元素()
    {
        LyricLine line = CreateLine("line", "字", 0, 10_000, CreateToken("base", "字", 0, 10_000)) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "base", "じ")] };
        LyricFrameSample sample = LyricSampler.SampleLine(line, 5_000);
        Assert.Equal(sample.Tokens[0].Progress, Assert.Single(sample.RubySegments).Progress);
        Assert.Equal(ProgressSource.FromToken, sample.RubySegments[0].Source);
    }

    private static long Us(long microseconds) => microseconds;

    private static LyricsDocument CreateDocument(params LyricLine[] lines) =>
        LyricsDocument.Create(lines.Length == 0 ? [CreateLine("l1", "夜空中最亮的星", 1_000_000, 4_000_000), CreateLine("l2", "请照亮我前行", 4_000_000, 8_000_000)] : lines);

    private static LyricLine CreateLine(
        string id,
        string text,
        long? startUs = null,
        long? endUs = null,
        params LyricToken[] tokens) =>
        LyricLine.Create(
            id,
            text,
            startUs is { } start && endUs is { } end ? new TimeRange(start, end) : null,
            startUs is not null ? TimingStatus.Imported : TimingStatus.Missing,
            tokens.Length == 0 ? null : tokens);

    private static LyricToken CreateToken(string id, string text, long startUs, long endUs) =>
        LyricToken.Create(id, text, new TimeRange(startUs, endUs), TimingStatus.Imported);
}
