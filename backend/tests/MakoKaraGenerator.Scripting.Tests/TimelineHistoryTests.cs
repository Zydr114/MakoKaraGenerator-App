using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class TimelineHistoryTests
{
    [Fact]
    public void 历史只含已处理前缀且半开查询不能读未来()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        var before = timeline.HistoryAt(999); var at = timeline.HistoryAt(2000);
        Assert.Empty(before.GetStarts(new(0, 100_000), new SourceFilter.Project()));
        Assert.Equal(2000, at.LastBoundaryUs); Assert.All(at.Records, record => Assert.True(record.TimeUs <= 2000));
        Assert.Equal(["a", "b"], at.GetStarts(new(0, 100_000), new SourceFilter.Project()).Select(record => Assert.IsType<TokenStarted>(record.Event).TokenId.Value));
        Assert.Single(at.GetStarts(new(1000, 2000), new SourceFilter.Project()));
        Assert.Equal("b", Assert.IsType<TokenStarted>(Assert.Single(at.GetStarts(new(2000, 3000), new SourceFilter.Project())).Event).TokenId.Value);
        Assert.Empty(at.GetStarts(new(3000, 5000), new SourceFilter.Project()));
        Assert.Empty(timeline.HistoryAt(7000).GetStarts(new(6000, 7000), new SourceFilter.Project()));
    }

    [Fact]
    public void 筛选使用真实源身份并保留每次起唱记录()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Export);
        var history = timeline.HistoryAt(7000); TimeRange range = new(0, 7001);
        Assert.Equal(3, history.GetStarts(range, new SourceFilter.Project()).Length);
        Assert.Equal(3, history.GetStarts(range, new SourceFilter.Branch(timeline.Content.Branches[0].Id)).Length);
        Assert.Equal(3, history.GetStarts(range, new SourceFilter.Lines([new("first")])).Length);
        Assert.Equal(2, history.GetStarts(range, new SourceFilter.Tokens([new("a"), new("c")])).Length);
        Assert.Empty(history.GetStarts(range, new SourceFilter.Tokens([new("missing")])));
        Assert.Empty(history.GetStarts(range, new SourceFilter.Lines([new("fallback")])));
        var occurrences = history.GetStarts(range, new SourceFilter.Project()).Select(record => Assert.IsType<TokenStarted>(record.Event).OccurrenceId);
        Assert.Equal(3, occurrences.Distinct().Count());
        Assert.Throws<KeyNotFoundException>(() => history.GetStarts(range, new SourceFilter.Tokens([new("unknown")])));
        Assert.Throws<KeyNotFoundException>(() => history.GetStarts(range, new SourceFilter.Branch(new("unknown"))));
    }

    [Fact]
    public void 历史拒绝未来乱序重复及假源事件而不是排序或修正()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        var starts = timeline.Events.OfType<TokenStarted>().ToArray();
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 999, [new(starts[0])]));
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 5000, [new(starts[1]), new(starts[0])]));
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 5000, [new(starts[0]), new(starts[0])]));
        var forged = new TokenStarted(starts[0].Source with { Text = "伪造" }, starts[0].SourceOrder, ReplayReason.Initial);
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 5000, [new(forged)]));
        var wrongOrder = new TokenStarted(starts[0].Source, new(1, 0, 0), ReplayReason.Initial);
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 5000, [new(wrongOrder)]));
        var source = timeline.Content.GetLine(new("first"));
        var wrongRange = new LinePerformanceStarted(source, new(0, 6000), ProgressSource.Line, new(0, 0), ReplayReason.Initial);
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 5000, [new(wrongRange)]));
    }

    [Fact]
    public void 源筛选拒绝空列表重复和无效身份()
    {
        Assert.Throws<ArgumentException>(() => new SourceFilter.Tokens([]));
        Assert.Throws<ArgumentException>(() => new SourceFilter.Tokens([new("a"), new("a")]));
        Assert.Throws<ArgumentException>(() => new SourceFilter.Tokens([default]));
        Assert.Throws<ArgumentException>(() => new SourceFilter.Lines(default));
        Assert.Throws<ArgumentException>(() => new SourceFilter.Lines([new("first"), new("first")]));
        Assert.Throws<ArgumentException>(() => new SourceFilter.Branch(default));
        Assert.Throws<ArgumentException>(() => new SourceSubscription.Branch(default));
    }
}
