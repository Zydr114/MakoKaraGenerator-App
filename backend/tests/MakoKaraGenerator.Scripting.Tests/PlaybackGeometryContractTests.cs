using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class PlaybackGeometryContractTests
{
    [Fact]
    public void 播放头使用实际半开区间允许极长区间的进度浮点舍入()
    {
        var range = new TimeRange(0, long.MaxValue); TokenId token = new("token");
        CursorIdentity identity = new(new("branch"), new("line"), new("binding"), range, ProgressSource.Token, token,
            StableIdentities.Occurrence(new("branch"), new("line"), token, range));
        CursorGeometry geometry = new() { TrackId = new("track"), Identity = identity, TimeUs = long.MaxValue - 1,
            Progress = (double)(long.MaxValue - 1) / long.MaxValue, Path = new(10, 30, 40, 35), WorldMatrix = Affine2.Identity };
        Assert.Equal(1, geometry.Progress);
        var cursor = Assert.Single(new PlaybackFrame([geometry]).GetCursors(CursorAnchorMode.Baseline));
        Assert.Equal(new Point2(30, 40), cursor.Position);
        Assert.Throws<EffectOutputException>(() => new PlaybackFrame([geometry with { TimeUs = long.MaxValue }]));
        Assert.Throws<EffectOutputException>(() => new PlaybackFrame([geometry with { Progress = double.NaN }]));
    }
    [Fact]
    public void 空集合也验证锚点模式且后端不可用不冒充无演唱()
    {
        Assert.Empty(new PlaybackFrame([]).GetCursors(CursorAnchorMode.AdvanceCenter));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackFrame([]).GetCursors((CursorAnchorMode)999));
        Assert.False(PlaybackFrame.Unavailable.IsAvailable);
        Assert.Equal("PlaybackGeometryUnavailable", Assert.Throws<EffectOutputException>(() => PlaybackFrame.Unavailable.GetCursors(CursorAnchorMode.Baseline)).Code);
    }
    [Fact]
    public void 查询通道取消阶段引用不能继续使用()
    {
        GeometryQueries queries = new(static (time, _) => new() { TimeUs = time });
        Assert.Equal(1, queries.GetBaseAt(1, new SourceFilter.Project()).TimeUs);
        queries.Invalidate();
        Assert.Equal("InvalidGeometryQueryStage", Assert.Throws<EffectOutputException>(() => queries.GetBaseAt(2, new SourceFilter.Project())).Code);
        Assert.Equal("GeometryQueriesUnavailable", Assert.Throws<EffectOutputException>(() => GeometryQueries.Unavailable.GetBaseAt(2, new SourceFilter.Project())).Code);
    }
}
