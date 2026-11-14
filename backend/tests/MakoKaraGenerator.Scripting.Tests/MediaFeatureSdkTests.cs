using System.Reflection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class MediaFeatureSdkTests
{
    private static readonly Vector2 Canvas = new(1920, 1080);
    private static readonly Rect2 UnitRect = new(0, 0, 1, 1);
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void 旧入口明确没有绑定且不能伪装NoMedia或继续创建绑定帧()
    {
        DeclareContext source = Source(); var frame = new TextFrame(source, Legacy(source));
        Assert.Null(source.Media.Input); Assert.Empty(source.Media.Profiles);
        Assert.Same(source.Media, frame.Media.Catalog);
        AssertCode("UndeclaredMediaQuery", () => source.Media.GetId("contrast"));
        AssertCode("MediaFeatureBindingRequired", () => frame.Media.Sample(default, UnitRect, 0));
        var bound = Source(Catalog(Input(MediaInputState.Pending)));
        AssertCode("MediaFeatureBindingRequired", () => new TextFrame(bound, Legacy(bound)));
    }

    [Fact]
    public void 同名查询仍限定独立目录且拒绝default与未声明名字()
    {
        var input = Input(MediaInputState.Pending); var first = Catalog(input); var second = Catalog(input);
        var media = Service(first);
        Assert.Equal("source", first.GetId("source").Value);
        AssertCode("UndeclaredMediaQuery", () => media.Sample(second.GetId("source"), UnitRect, 0));
        AssertCode("UndeclaredMediaQuery", () => media.Sample(default, UnitRect, 0));
        AssertCode("UndeclaredMediaQuery", () => first.GetId("canvasFromOtherExport"));
        Assert.Throws<ArgumentException>(() => new MediaQueryCatalog(input,
            [("same", MediaCoordinateSpace.SourceNormalized, 0), ("same", MediaCoordinateSpace.CanvasPixels, 0)]));
        Assert.Throws<ArgumentException>(() => new MediaQueryCatalog(input, [("bad", MediaCoordinateSpace.SourceNormalized, 2_000_001)]));
    }

    [Fact]
    public void 目录保留已接受文本而不新增Unicode归一化协议()
    {
        var catalog = new MediaQueryCatalog(Input(MediaInputState.Pending),
            [("e\u0301", MediaCoordinateSpace.SourceNormalized, 0), ("é", MediaCoordinateSpace.SourceNormalized, 0)]);
        Assert.Equal("e\u0301", catalog.GetId("e\u0301").Value);
        Assert.Equal("é", catalog.GetId("é").Value);
        Assert.Throws<ArgumentException>(() => new MediaQueryCatalog(Input(MediaInputState.Pending), [("bad\uFEFF", MediaCoordinateSpace.SourceNormalized, 0)]));
        Assert.Throws<ArgumentException>(() => new MediaQueryCatalog(Input(MediaInputState.Pending), [("\ud800", MediaCoordinateSpace.SourceNormalized, 0)]));
    }

    [Theory]
    [InlineData(MediaInputState.Pending)]
    [InlineData(MediaInputState.Unavailable)]
    [InlineData(MediaInputState.NoMedia)]
    public void 非Available保留完整profile并且两种数值均为空(MediaInputState state)
    {
        var input = Input(state); var catalog = Catalog(input); var media = Service(catalog);
        foreach (var profile in catalog.Profiles)
        {
            MediaFeatureSample sample = media.Sample(profile.Id, UnitRect, long.MaxValue);
            Assert.Same(input, sample.Input); Assert.Same(profile, sample.Query);
            Assert.Equal(Digest, sample.Input.ProfileDigest); Assert.Equal(state, sample.Input.State);
            Assert.Null(sample.SourceValues); Assert.Null(sample.CanvasValues);
            Assert.Equal(long.MaxValue, sample.ProjectTimeUs);
        }
        Assert.Equal(state == MediaInputState.NoMedia, input.SourceKey is null);
        Assert.Equal(state == MediaInputState.NoMedia, input.NoMediaReason is not null);
        AssertCode("InvalidMediaQuery", () => media.Sample(catalog.GetId("source"), default, 0));
    }

    [Fact]
    public void source与canvas返回独立数值分支且double细矩形和显式时刻原样传递()
    {
        var catalog = Catalog(Input(MediaInputState.Available)); var media = Service(catalog);
        double left = .5, right = Math.BitIncrement(left); Rect2 tiny = new(left, 0, right, 1);
        Assert.Equal((float)left, (float)right);
        var source = media.Sample(catalog.GetId("source"), tiny, 73);
        Assert.Equal(tiny, source.Rect); Assert.Equal(73, source.ProjectTimeUs);
        Assert.NotNull(source.SourceValues); Assert.Null(source.CanvasValues);
        Assert.Equal(.12, source.SourceValues.MeanAlphaTimesPremulY);
        Assert.Equal(.25, source.SourceValues.MeanSquaredAlpha);
        var canvas = media.Sample(catalog.GetId("canvas"), new(10, 20, 1920, 1080), 11);
        Assert.Null(canvas.SourceValues); Assert.NotNull(canvas.CanvasValues); Assert.True(canvas.CanvasValues.IsOpaque);
        Assert.Throws<ArgumentException>(() => new MediaFeatureSample(catalog.Input!, catalog.Profiles[0], UnitRect, 0));
    }

    [Fact]
    public void 调用边界先拒绝非法域和时刻而不交callback修补()
    {
        var catalog = Catalog(Input(MediaInputState.Pending)); int calls = 0;
        var media = new MediaFeatures(catalog, Canvas, (query, rect, time, _) => { calls++; return new(catalog.Input!, query, rect, time); });
        foreach (var rect in new Rect2[] { default, new(-1, 0, 1, 1), new(0, 0, 2, 1) })
            AssertCode("InvalidMediaQuery", () => media.Sample(catalog.GetId("source"), rect, 0));
        AssertCode("InvalidMediaQuery", () => media.Sample(catalog.GetId("source"), UnitRect, -1));
        AssertCode("InvalidMediaQuery", () => media.Sample(catalog.GetId("canvas"), new(0, 0, 1921, 1), 0));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Text与Visual句柄独立失效且保存的目录和快照不携带阶段生命周期()
    {
        var catalog = Catalog(Input(MediaInputState.Pending)); var source = Source(catalog);
        var textService = Service(catalog); var visualService = Service(catalog);
        var text = new TextFrame(source, Legacy(source), [], textService);
        var final = new FinalSceneGeometry { TimeUs = 100, Presentations = new() };
        var particles = new ParticleFrame(new("probe"), 100, [], new(new(0, 100), []));
        var visual = new VisualFrame(source, 100, new EffectBinding.Project(), OutputMode.Opaque, final, particles, visualService);
        var snapshot = text.Media.Sample(catalog.GetId("source"), UnitRect, 0); var savedCatalog = text.Media.Catalog;
        textService.Invalidate(); textService.Invalidate();
        AssertCode("InvalidMediaFeatureStage", () => text.Media.Catalog);
        AssertCode("InvalidMediaFeatureStage", () => text.Media.Sample(catalog.GetId("source"), UnitRect, 0));
        Assert.Same(savedCatalog.Input, snapshot.Input); Assert.Equal("source", savedCatalog.GetId("source").Value);
        Assert.Equal(MediaInputState.Pending, visual.Media.Sample(catalog.GetId("source"), UnitRect, 0).Input.State);
        visualService.Invalidate(); particles.Invalidate();
        AssertCode("InvalidMediaFeatureStage", () => visual.Media.Sample(catalog.GetId("source"), UnitRect, 0));
        AssertCode("MediaFeatureBindingMismatch", () => new TextFrame(source, Legacy(source), [], Service(Catalog(Input(MediaInputState.Pending)))));
        var wrongCanvas = new MediaFeatures(catalog, new(1, 1), (q, r, t, _) => new(catalog.Input!, q, r, t));
        AssertCode("MediaFeatureBindingMismatch", () => new TextFrame(source, Legacy(source), [], wrongCanvas));
    }

    [Fact]
    public void 取消原样传播且回调后的取消也不能交付样本()
    {
        var catalog = Catalog(Input(MediaInputState.Pending)); using CancellationTokenSource cancellation = new(); int calls = 0;
        var media = new MediaFeatures(catalog, Canvas, (query, rect, time, token) =>
        { calls++; Assert.Equal(cancellation.Token, token); cancellation.Cancel(); return new(catalog.Input!, query, rect, time); }, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => media.Sample(catalog.GetId("source"), UnitRect, 0));
        Assert.Equal(1, calls);
        Assert.Throws<OperationCanceledException>(() => media.Sample(catalog.GetId("source"), UnitRect, 0));
        Assert.Equal(1, calls); media.Invalidate();
        AssertCode("InvalidMediaFeatureStage", () => media.Catalog);
    }

    [Fact]
    public void callback异常原样保留且finally关闭后不能重用()
    {
        var catalog = Catalog(Input(MediaInputState.Pending)); var failure = new InvalidOperationException("sentinel"); int calls = 0;
        var media = new MediaFeatures(catalog, Canvas, (_, _, _, _) => { calls++; throw failure; });
        try { Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => media.Sample(catalog.GetId("source"), UnitRect, 0))); }
        finally { media.Invalidate(); }
        AssertCode("InvalidMediaFeatureStage", () => media.Sample(catalog.GetId("source"), UnitRect, 0));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void callback不能替换输入查询矩形或时刻()
    {
        var catalog = Catalog(Input(MediaInputState.Pending)); var other = Catalog(Input(MediaInputState.Pending));
        var wrongInput = new MediaFeatures(catalog, Canvas, (q, r, t, _) => new(other.Input!, q, r, t));
        AssertCode("MediaFeatureResultMismatch", () => wrongInput.Sample(catalog.GetId("source"), UnitRect, 0));
        var wrongQuery = new MediaFeatures(catalog, Canvas, (_, r, t, _) => new(catalog.Input!, other.Profiles[0], r, t));
        AssertCode("MediaFeatureResultMismatch", () => wrongQuery.Sample(catalog.GetId("source"), UnitRect, 0));
        var wrongRect = new MediaFeatures(catalog, Canvas, (q, _, t, _) => new(catalog.Input!, q, new(0, 0, .5, .5), t));
        AssertCode("MediaFeatureResultMismatch", () => wrongRect.Sample(catalog.GetId("source"), UnitRect, 0));
        var wrongTime = new MediaFeatures(catalog, Canvas, (q, r, t, _) => new(catalog.Input!, q, r, t + 1));
        AssertCode("MediaFeatureResultMismatch", () => wrongTime.Sample(catalog.GetId("source"), UnitRect, 0));
    }

    [Fact]
    public void 安全元数据拒绝非规范身份与路径诊断并且值不公开可写表面()
    {
        Assert.Throws<ArgumentException>(() => new MediaFeatureInputInfo(MediaInputState.Pending, Digest.ToUpperInvariant(), Digest, Digest, Digest));
        Assert.Throws<ArgumentException>(() => new MediaFeatureInputInfo(MediaInputState.Unavailable, Digest, Digest, Digest, Digest, diagnosticCode: "/private/path"));
        Assert.Throws<ArgumentException>(() => new MediaFeatureInputInfo(MediaInputState.NoMedia, Digest, Digest, Digest));
        Assert.Throws<ArgumentException>(() => new LinearPremulMean(1, 0, 0, .5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinearPremulMean(double.NaN, 0, 0, 1));
        Type[] values = [typeof(MediaQueryId), typeof(MediaQueryProfile), typeof(MediaFeatureInputInfo), typeof(MediaQueryCatalog),
            typeof(LinearPremulMean), typeof(SourceMediaFeatureValues), typeof(CanvasMediaFeatureValues), typeof(MediaFeatureSample)];
        foreach (Type type in values)
        {
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
            Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
        }
        Assert.Empty(typeof(MediaFeatures).GetConstructors());
        Assert.DoesNotContain(typeof(MediaQueryCatalog).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(MediaFeatures));
    }

    private static MediaFeatureInputInfo Input(MediaInputState state) => new(state, Digest, Digest, Digest,
        state == MediaInputState.NoMedia ? null : Digest,
        diagnosticCode: state == MediaInputState.Unavailable ? "DecodeFailed" : null,
        noMediaReason: state == MediaInputState.NoMedia ? "TransparentOutput" : null);
    private static MediaQueryCatalog Catalog(MediaFeatureInputInfo input) => new(input,
        [("source", MediaCoordinateSpace.SourceNormalized, 200_000), ("canvas", MediaCoordinateSpace.CanvasPixels, 0)]);
    private static MediaFeatures Service(MediaQueryCatalog catalog) => new(catalog, Canvas, (query, rect, time, _) =>
    {
        var input = catalog.Input!;
        if (input.State != MediaInputState.Available) return new(input, query, rect, time);
        return query.Space == MediaCoordinateSpace.SourceNormalized
            ? new(input, query, rect, time, new(new(.2, .2, .2, .5), .2, .08, .12, .25, .04))
            : new(input, query, rect, time, canvasValues: new(new(.5, .5, .5, 1), .5, .3, .05));
    });
    private static DeclareContext Source(MediaQueryCatalog? catalog = null)
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        return catalog is null ? new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, ResourceCatalog.Empty,
            1, TimelineIndex.Empty, Canvas, new("probe")) : new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed,
            ResourceCatalog.Empty, 1, TimelineIndex.Empty, Canvas, new("probe"), null, catalog);
    }
    private static TextFrameHostInputs Legacy(DeclareContext source) => new(
        100, BaselineStyles.Empty, new(), [], [], [], [], new EffectBinding.Project(), OutputMode.Opaque);
    private static void AssertCode<T>(string code, Func<T> call)
        => Assert.Equal(code, Assert.Throws<EffectOutputException>(() => { _ = call(); }).Code);
}
