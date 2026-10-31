using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Timeline;

namespace MakoKaraGenerator.Timeline.Tests;

/// <summary>
/// 逐字块构造、命中测试与标尺刻度的测试。
/// </summary>
/// <remarks>
/// 这一组是"画在哪就能点到哪"的回归防线：绘制与命中都用
/// <see cref="TimelineLayout.Axis"/>，任何一方改用别的换算都会在这里暴露。
/// </remarks>
public sealed class TimelineInteractionTests
{
    private const double ViewportWidth = 1000d;
    private const double PixelsPerSecond = 100d;
    private const long ContentDurationUs = 30_000_000;

    private static TimelineLayout Layout(long startUs = 0, double pixelsPerSecond = PixelsPerSecond) =>
        TimelineLayout.Create(
            ViewportWidth,
            TimeAxis.Create(ViewportWidth, pixelsPerSecond, startUs),
            ContentDurationUs);

    private static TimelineTokenBlock Block(int lineIndex, int tokenIndex, string text, long startUs, long endUs) =>
        new(lineIndex, tokenIndex, $"line-{lineIndex + 1:D3}", $"token-{tokenIndex + 1:D3}", text,
            TimeRange.Create(startUs, endUs), tokenIndex, text.Length);

    private static IReadOnlyList<TimelineTokenBlock> SampleBlocks() =>
    [
        Block(0, 0, "夜", 1_000_000, 2_000_000),
        Block(0, 1, "空中", 2_000_000, 3_400_000),
        Block(0, 2, "最亮的", 3_400_000, 5_600_000),
        Block(0, 3, "星", 5_600_000, 7_000_000),
        Block(1, 0, "请", 8_500_000, 9_500_000),
    ];

    // ---------------- 逐字块构造 ----------------

    [Fact]
    public void 只有带合法时间的元素才会成为逐字块()
    {
        LyricsDocument lyrics = LyricsDocument.Create(
        [
            LyricLine.Create(
                "line-001",
                "你好吗",
                new TimeRange(0, 5_000_000),
                TimingStatus.Edited,
                [
                    LyricToken.Create("token-001", "你", new TimeRange(0, 1_000_000), TimingStatus.Confirmed),
                    LyricToken.Create("token-002", "好"),
                    LyricToken.Create("token-003", "吗", new TimeRange(2_000_000, 5_000_000), TimingStatus.Estimated),
                ]),
        ]);

        IReadOnlyList<TimelineTokenBlock> blocks = TimelineTokenBlocks.FromLyrics(lyrics);

        Assert.Equal(2, blocks.Count);

        // 序号必须保留原始元素下标，否则编辑时会改错元素。
        Assert.Equal(0, blocks[0].TokenIndex);
        Assert.Equal("你", blocks[0].Text);
        Assert.Equal(2, blocks[1].TokenIndex);
        Assert.Equal("吗", blocks[1].Text);
    }

    [Fact]
    public void 时间非法的元素也不会成为逐字块()
    {
        LyricLine line = new()
        {
            Id = "line-001",
            Text = "你好",
            Time = new TimeRange(0, 5_000_000),
            TimingStatus = TimingStatus.Invalid,
            Tokens =
            [
                new LyricToken
                {
                    Id = "token-001",
                    Text = "你",
                    Time = new TimeRange(2_000_000, 1_000_000),
                    TimingStatus = TimingStatus.Invalid,
                },
                LyricToken.Create("token-002", "好", new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed),
            ],
        };

        IReadOnlyList<TimelineTokenBlock> blocks = TimelineTokenBlocks.FromLyrics(
            new LyricsDocument { Lines = [line] });

        Assert.Single(blocks);
        Assert.Equal("好", blocks[0].Text);
    }

    [Fact]
    public void 行序号与元素序号都被保留()
    {
        LyricsDocument lyrics = LyricsDocument.Create(
        [
            LyricLine.Create("line-001", "你", tokens: [LyricToken.Create("t1", "你", new TimeRange(0, 1_000), TimingStatus.Confirmed)]),
            LyricLine.Create("line-002", "好", tokens: [LyricToken.Create("t2", "好", new TimeRange(2_000, 3_000), TimingStatus.Confirmed)]),
        ]);

        IReadOnlyList<TimelineTokenBlock> blocks = TimelineTokenBlocks.FromLyrics(lyrics);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(0, blocks[0].LineIndex);
        Assert.Equal(1, blocks[1].LineIndex);
        Assert.Equal("line-002", blocks[1].LineId);
    }

    // ---------------- 命中测试 ----------------

    [Fact]
    public void 行轨只给选中行的两端提供独立手柄()
    {
        TimelineLayout layout = Layout();
        LyricLine[] lines =
        [
            LyricLine.Create("a", "甲", TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Edited),
            LyricLine.Create("b", "乙", TimeRange.Create(4_000_000, 6_000_000), TimingStatus.Edited),
        ];
        double y = layout[TimelineLane.Line].Top + 4d;

        TimelineHit start = TimelineHitTester.HitTest(layout, [], 100d, y,
            lines: lines, selectedLineIndex: 0);
        TimelineHit end = TimelineHitTester.HitTest(layout, [], 300d, y,
            lines: lines, selectedLineIndex: 0);
        TimelineHit other = TimelineHitTester.HitTest(layout, [], 400d, y,
            lines: lines, selectedLineIndex: 0);

        Assert.Equal(TimelineHitKind.LineStartEdge, start.Kind);
        Assert.Equal(TimelineHitKind.LineEndEdge, end.Kind);
        Assert.Equal(0, start.BlockIndex);
        Assert.Equal(TimelineHitKind.Line, other.Kind);
    }

    [Fact]
    public void 命中逐字块主体()
    {
        TimelineLayout layout = Layout();
        IReadOnlyList<TimelineTokenBlock> blocks = SampleBlocks();

        // 「空中」占 2.0–3.4 s，即 x = 200–340，取中间。
        TimelineHit hit = TimelineHitTester.HitTest(layout, blocks, 270d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.TokenBody, hit.Kind);
        Assert.Equal(1, hit.BlockIndex);
    }

    [Fact]
    public void 命中逐字块起始边界()
    {
        TimelineLayout layout = Layout();

        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 200d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.TokenStartEdge, hit.Kind);
        Assert.Equal(1, hit.BlockIndex);
    }

    [Fact]
    public void 命中逐字块结束边界()
    {
        TimelineLayout layout = Layout();

        // 「星」的终点 7.0 s（x=700）后面是空隙，不是共享边界。
        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 700d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.TokenEndEdge, hit.Kind);
        Assert.Equal(3, hit.BlockIndex);
    }

    [Fact]
    public void 边界抓取优先于主体()
    {
        TimelineLayout layout = Layout();
        IReadOnlyList<TimelineTokenBlock> blocks = SampleBlocks();

        // 紧贴「空中」起点右侧 3 像素：应当判为起点边界而不是主体。
        TimelineHit hit = TimelineHitTester.HitTest(layout, blocks, 203d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.TokenStartEdge, hit.Kind);
    }

    [Fact]
    public void 超出抓取半径则算主体()
    {
        TimelineLayout layout = Layout();

        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 215d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.TokenBody, hit.Kind);
    }

    [Fact]
    public void 未选中时共享边界默认命中后一块的起点()
    {
        TimelineLayout layout = Layout();
        IReadOnlyList<TimelineTokenBlock> blocks = SampleBlocks();

        // 「夜」结束于 2.0 s，「空中」起始于 2.0 s，x=200。
        TimelineHit hit = TimelineHitTester.HitTest(layout, blocks, 200d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.TokenStartEdge, hit.Kind);
        Assert.Equal(1, hit.BlockIndex);
    }

    [Theory]
    [InlineData(0, TimelineHitKind.TokenEndEdge)]
    [InlineData(1, TimelineHitKind.TokenStartEdge)]
    public void 共享边界优先命中选中块的单侧边沿(int selectedIndex, TimelineHitKind expected)
    {
        TimelineLayout layout = Layout();
        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 200d,
            TokenLaneY(layout), selectedBlockIndex: selectedIndex);

        Assert.Equal(selectedIndex, hit.BlockIndex);
        Assert.Equal(expected, hit.Kind);
    }

    [Fact]
    public void 共享边界偏左时也报到同一条边界()
    {
        TimelineLayout layout = Layout();

        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 198d, TokenLaneY(layout));

        Assert.True(hit.IsEdge);

        // 不论抓到哪一侧，边界时刻都必须是 2.0 s。
        Assert.Equal(2_000_000, BoundaryTimeUs(SampleBlocks(), hit));
    }

    [Fact]
    public void 逐字块空白处没有命中()
    {
        TimelineLayout layout = Layout();

        // 7.0–8.5 s 之间是空隙，x = 700–850。
        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 780d, TokenLaneY(layout));

        Assert.Equal(TimelineHitKind.None, hit.Kind);
        Assert.False(hit.HasBlock);
    }

    [Theory]
    [InlineData(14d, TimelineHitKind.Overview)]
    [InlineData(40d, TimelineHitKind.Ruler)]
    [InlineData(80d, TimelineHitKind.Waveform)]
    [InlineData(120d, TimelineHitKind.Line)]
    [InlineData(150d, TimelineHitKind.TokenBody)]
    public void 按纵坐标分流到对应轨道(double y, TimelineHitKind expected)
    {
        TimelineLayout layout = Layout();

        TimelineHit hit = TimelineHitTester.HitTest(layout, SampleBlocks(), 150d, y);

        Assert.Equal(expected, hit.Kind);
    }

    [Fact]
    public void 轨道之外没有命中()
    {
        TimelineLayout layout = Layout();

        Assert.Equal(TimelineHitKind.None, TimelineHitTester.HitTest(layout, SampleBlocks(), 150d, -5d).Kind);
        Assert.Equal(
            TimelineHitKind.None,
            TimelineHitTester.HitTest(layout, SampleBlocks(), 150d, layout.TotalHeight + 5d).Kind);
    }

    [Fact]
    public void 命中位置与绘制位置使用同一个映射()
    {
        // 这是"对齐"最关键的一条：对每个块的每条边界，
        // 用绘制坐标去命中必须抓到<b>同一条边界</b>（同一个时刻），
        // 且命中位置与绘制位置一致。
        // 未选中时共享边界可先命中任一侧；实际编辑时已选元素的边沿优先。
        TimelineLayout layout = Layout(pixelsPerSecond: 250d, startUs: 1_000_000);
        IReadOnlyList<TimelineTokenBlock> blocks = SampleBlocks();

        for (int i = 0; i < blocks.Count; i++)
        {
            foreach (long boundaryUs in new[] { blocks[i].StartUs, blocks[i].EndUs })
            {
                double drawnX = layout.Axis.TimeToX(boundaryUs);
                TimelineHit hit = TimelineHitTester.HitTest(layout, blocks, drawnX, TokenLaneY(layout));

                Assert.True(hit.IsEdge, $"边界 {boundaryUs} us（x={drawnX}）命中了 {hit.Kind} 而不是边界。");
                Assert.Equal(drawnX, hit.X, 1e-9);
                Assert.Equal(boundaryUs, BoundaryTimeUs(blocks, hit));
            }
        }
    }

    [Fact]
    public void 缩略条用独立坐标系换算时间()
    {
        TimelineLayout layout = Layout(pixelsPerSecond: 1000d);

        TimelineHit overviewHit = new(TimelineHitKind.Overview, -1, 300d);
        TimelineHit waveformHit = new(TimelineHitKind.Waveform, -1, 300d);

        // 缩略条把 30 秒压进 1000 px：300 px 处是 9 秒。
        Assert.Equal(9_000_000, TimelineHitTester.TimeAt(layout, overviewHit));

        // 主视图 1000 px/s：300 px 处是 0.3 秒。
        Assert.Equal(300_000, TimelineHitTester.TimeAt(layout, waveformHit));
    }

    [Fact]
    public void 命中测试拒绝负的抓取半径()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimelineHitTester.HitTest(Layout(), SampleBlocks(), 0d, 0d, -1d));
    }

    // ---------------- 标尺刻度 ----------------

    [Theory]
    [InlineData(100d, 1_000_000L)]      // 100 px/s：1 秒 = 100 px，够 80 px
    [InlineData(1000d, 100_000L)]       // 1000 px/s：100 ms = 100 px
    [InlineData(10d, 10_000_000L)]      // 10 px/s：10 秒 = 100 px
    public void 刻度间隔随缩放变化且取整值(double pixelsPerSecond, long expected)
    {
        TimeAxis axis = TimeAxis.Create(ViewportWidth, pixelsPerSecond);

        Assert.Equal(expected, TimeRulerTicks.ChooseIntervalUs(axis));
    }

    [Fact]
    public void 刻度间隔不小于要求的像素间距()
    {
        foreach (double pixelsPerSecond in new[] { 1d, 7d, 33d, 100d, 480d, 3000d })
        {
            TimeAxis axis = TimeAxis.Create(ViewportWidth, pixelsPerSecond);
            long interval = TimeRulerTicks.ChooseIntervalUs(axis, minSpacingPx: 90d);

            Assert.True(
                axis.PixelsFromUs(interval) >= 90d,
                $"{pixelsPerSecond} px/s 下选了 {interval} us，只有 {axis.PixelsFromUs(interval):0.#} px 间距。");
        }
    }

    [Fact]
    public void 刻度落在整值上且覆盖视口()
    {
        TimeAxis axis = TimeAxis.Create(ViewportWidth, 250d, viewStartUs: 1_230_000);
        long interval = TimeRulerTicks.ChooseIntervalUs(axis);

        long[] ticks = [.. TimeRulerTicks.Enumerate(axis, interval)];

        Assert.NotEmpty(ticks);
        Assert.All(ticks, tick => Assert.Equal(0L, tick % interval));

        foreach (long tick in ticks)
        {
            Assert.InRange(tick, axis.ViewStartUs, axis.ViewEndUs);
        }

        // 第一个刻度应当在视口左端的一个间隔之内。
        Assert.True(ticks[0] - axis.ViewStartUs < interval);
    }

    [Fact]
    public void 刻度超出视口则不产生()
    {
        // 视口是 20–30 秒，却要求每分钟一个刻度：60 s 不在视口内，因此一个刻度都没有。
        TimeAxis axis = TimeAxis.Create(ViewportWidth, 100d, viewStartUs: 20_000_000);

        Assert.Empty(TimeRulerTicks.Enumerate(axis, 60_000_000));
    }

    [Fact]
    public void 视口包含零点时零点本身是刻度()
    {
        TimeAxis axis = TimeAxis.Create(ViewportWidth, 100d, viewStartUs: 0);

        Assert.Equal([0L], TimeRulerTicks.Enumerate(axis, 60_000_000));
    }

    [Fact]
    public void 刻度参数校验()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeRulerTicks.ChooseIntervalUs(TimeAxis.Create(100d, 100d), 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeRulerTicks.Enumerate(TimeAxis.Create(100d, 100d), 0).ToList());
    }

    private static double TokenLaneY(TimelineLayout layout) =>
        layout[TimelineLane.Token].Top + 10d;

    /// <summary>取命中结果所代表的边界时刻。</summary>
    private static long BoundaryTimeUs(IReadOnlyList<TimelineTokenBlock> blocks, TimelineHit hit) =>
        hit.Kind == TimelineHitKind.TokenStartEdge
            ? blocks[hit.BlockIndex].StartUs
            : blocks[hit.BlockIndex].EndUs;
}
