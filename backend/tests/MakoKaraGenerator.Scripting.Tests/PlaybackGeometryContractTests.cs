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
    [Theory]
    [InlineData(WaveDriveAxis.X)] [InlineData(WaveDriveAxis.Y)]
    public void 波形正逆和切线对应同一有限映射(WaveDriveAxis axis)
    {
        AnchorMap map = new AnchorMap.AxisWave(new(-100, -100, 100, 100), axis, 8, 40, .3, -5);
        Point2 point = new(17, 23); Vector2 vector = new(2, -3);
        var mapped = map.Map(point); var inverse = map.Inverse(mapped);
        Assert.Equal(point.X, inverse.X, 12); Assert.Equal(point.Y, inverse.Y, 12);
        if (axis == WaveDriveAxis.X) Assert.Equal(point.X, mapped.X); else Assert.Equal(point.Y, mapped.Y);
        var tangent = map.MapVectorAt(point, vector);
        var near = map.Map(new(point.X + vector.X * 1e-6, point.Y + vector.Y * 1e-6));
        Assert.Equal(tangent.X, (near.X - mapped.X) / 1e-6, 5);
        Assert.Equal(tangent.Y, (near.Y - mapped.Y) / 1e-6, 5);
        Assert.Throws<EffectOutputException>(() => map.Map(new(101, 0)));
        Assert.Throws<EffectOutputException>(() => map.Inverse(new(101, 101)));
    }
    [Fact]
    public void 仿射与波形按明确顺序组合且不能接受零矩阵或溢出()
    {
        AnchorMap affine = new AnchorMap.Affine(new(-100, -100, 100, 100), new(0, 2, -2, 0, 10, -5));
        AnchorMap wave = new AnchorMap.AxisWave(new(-100, -100, 100, 100), WaveDriveAxis.X, 5, 20, 0, 0);
        Point2 source = new(3, 4); var transformed = wave.Map(affine.Map(source));
        var inverse = affine.Inverse(wave.Inverse(transformed));
        Assert.Equal(source.X, inverse.X, 12); Assert.Equal(source.Y, inverse.Y, 12);
        Assert.NotEqual(transformed, affine.Map(wave.Map(source)));
        Assert.Equal(new Vector2(-6, 4), affine.MapVectorAt(source, new(2, 3)));
        Assert.Throws<EffectOutputException>(() => new AnchorMap.Affine(new(0, 0, 10, 10), default));
        Assert.Throws<EffectOutputException>(() => new AnchorMap.Affine(new(0, 0, double.MaxValue, 10), new(2, 0, 0, 1, 0, 0)));
        Assert.Throws<EffectOutputException>(() => new AnchorMap.AxisWave(new(0, 0, 10, 10), WaveDriveAxis.X, 1, double.Epsilon, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnchorMap.AxisWave(new(0, 0, 10, 10), WaveDriveAxis.X, 1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new AnchorMap.Affine(new(0, 0, 0, 10), Affine2.Identity));
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
