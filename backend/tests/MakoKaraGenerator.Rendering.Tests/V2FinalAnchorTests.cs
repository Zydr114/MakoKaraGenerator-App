using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2FinalAnchorTests
{
    [Fact]
    public void 有限LRU按访问淘汰且重新补算保留同一节点真值()
    {
        V2FinalAnchorMemo memo = new(2, 3000); int evaluations = 0;
        var a = Key(10); var b = Key(20); var c = Key(30);
        Point2 Read(V2AnchorNodeKey key)
        {
            var transaction = memo.Begin(); V2AnchorQueryBudget budget = new(CancellationToken.None);
            var value = transaction.Read(key, () => { evaluations++; return new(key.TimeUs, 7); }, _ => { }, budget);
            transaction.Commit(CancellationToken.None); return value;
        }
        var original = Read(a); Read(b); Assert.Equal(original, Read(a));
        Assert.Equal(2, evaluations);
        Read(c); Assert.Equal(3, evaluations); Assert.Equal(2, memo.Count);
        Assert.Equal(new Point2(20, 7), Read(b)); Assert.Equal(4, evaluations);
        Assert.Equal(2, memo.Count); Assert.InRange(memo.Bytes, 1L, 3000L);
        memo.Clear(); Assert.Equal(0, memo.Count); Assert.Equal(0, memo.Bytes);
    }

    [Fact]
    public void 未完成或取消的请求不发布半份memo()
    {
        V2FinalAnchorMemo memo = new(); var key = Key(10);
        var failed = memo.Begin(); V2AnchorQueryBudget budget = new(CancellationToken.None);
        failed.Read(key, () => new(10, 1), _ => { }, budget);
        // The following author/visual stage failed, so this transaction is discarded.
        Assert.Equal(0, memo.Count);
        var cancelled = memo.Begin();
        cancelled.Read(key, () => new(10, 2), _ => { }, budget);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cancelled.Commit(cancellation.Token));
        Assert.Equal(0, memo.Count);
        var recovery = memo.Begin(); bool evaluated = false;
        Assert.Equal(new Point2(10, 3), recovery.Read(key, () => { evaluated = true; return new(10, 3); }, _ => { }, budget));
        recovery.Commit(CancellationToken.None); Assert.True(evaluated); Assert.Equal(1, memo.Count);
    }

    [Fact]
    public void 真实重载密度输出模式和区段都进入节点身份()
    {
        V2FinalAnchorMemo memo = new(); V2AnchorQueryBudget budget = new(CancellationToken.None);
        var original = Key(10);
        var reload = original with { Source = new ParticleSource.Token(new(new("token"), new("binding/reload/1"))) };
        var dense = original with { Target = new() { PixelWidth = 320, PixelHeight = 180 } };
        var opaque = original with { Mode = OutputMode.Opaque };
        var otherCell = original with { SegmentStartUs = 1 };
        var transaction = memo.Begin(); int count = 0;
        foreach (var key in new[] { original, reload, dense, opaque, otherCell })
            transaction.Read(key, () => new(++count, 0), _ => { }, budget);
        transaction.Commit(CancellationToken.None);
        Assert.Equal(5, count); Assert.Equal(5, memo.Count);
    }

    [Fact]
    public void 唯一时间文字调用和未来端点是独立硬门禁()
    {
        V2AnchorQueryBudget times = new(CancellationToken.None, timeLimit: 1);
        times.Time(10); times.Time(10);
        Assert.Equal("FinalAnchorTimeBudgetExceeded", Assert.Throws<EffectOutputException>(() => times.Time(11)).Code);
        V2AnchorQueryBudget calls = new(CancellationToken.None, textLimit: 1);
        calls.TextCall(); Assert.Equal("FinalAnchorTextCallBudgetExceeded", Assert.Throws<EffectOutputException>(calls.TextCall).Code);
        V2AnchorQueryBudget endpoints = new(CancellationToken.None, futureLimit: 1);
        var source = Key(0).Source;
        endpoints.Endpoint(source, 10, 10); endpoints.Endpoint(source, 20, 10); endpoints.Endpoint(source, 20, 10);
        Assert.Equal(1, endpoints.Statistics.FutureEndpoints);
        Assert.Equal("FinalAnchorFutureEndpointBudgetExceeded", Assert.Throws<EffectOutputException>(() => endpoints.Endpoint(source, 21, 10)).Code);
    }

    private static V2AnchorNodeKey Key(long time) => new(new ParticleSource.Token(new(new("token"), new("binding"))),
        CursorAnchorMode.AdvanceCenter, 0, 100, time, OutputMode.Transparent,
        new() { PixelWidth = 640, PixelHeight = 360 });
}
