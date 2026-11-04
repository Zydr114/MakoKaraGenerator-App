using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2SourceIndexBudgetTests
{
    [Fact]
    public void 建立事件键和引用预算分别受限且重复输入仍计数()
    {
        var events = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial).Events;
        var token = events.OfType<TokenStarted>().First();
        Reject([token, token.WithReplayReason(ReplayReason.Seek)], new() { MaximumEvents = 1 });
        Reject([token], new() { MaximumKeys = 1 });
        Reject([token], new() { MaximumReferences = 1 });
        var index = new TimelineIndex([token, token.WithReplayReason(ReplayReason.Seek)], new() { MaximumEvents = 2 });
        Assert.Single(index.Events); Assert.Equal(1, index.Count(new(TimelineEventKind.TokenStarted)));
        static void Reject(IEnumerable<TimelineEvent> source, TimelineIndexBudget budget)
            => Assert.Equal("TimelineIndexBudgetExceeded", Assert.Throws<EffectOutputException>(() => new TimelineIndex(source, budget)).Code);
    }

    [Fact]
    public void 惰性输入中途取消且零事件入口均保持取消异常()
    {
        var token = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial).Events.OfType<TokenStarted>().First();
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => new TimelineIndex(Input(), cancellationToken: cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => new TimelineIndex([], cancellationToken: cancel.Token));
        IEnumerable<TimelineEvent> Input()
        { yield return token; cancel.Cancel(); yield return token.WithReplayReason(ReplayReason.Seek); }
    }

    [Fact]
    public void 非法预算拒绝且正常建立保留已有精确键查询()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineIndex([], new() { MaximumEvents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineIndex([], new() { MaximumKeys = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineIndex([], new() { MaximumReferences = 0 }));
        var events = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial).Events;
        var index = new TimelineIndex(events); var selector = new TimelineSelector(TimelineEventKind.TokenStarted, sourceText: "请");
        Assert.Equal(3, index.Count(selector)); Assert.Equal(1, index.Count(selector, new TimeRange(1000, 2000)));
        Assert.Equal("b", Assert.IsType<TokenStarted>(index.Next(selector, 1000)).TokenId.Value);
        Assert.Equal("a", Assert.IsType<TokenStarted>(index.Last(selector, 1999)).TokenId.Value);
        Assert.Equal(3, index.Ordinal(selector, Assert.IsType<TokenStarted>(index.Nth(selector, 2)).OccurrenceId));
    }
}
