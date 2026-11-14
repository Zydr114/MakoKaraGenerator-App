using System.Collections.Immutable;
using System.Reflection;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class FrameServicesTests
{
    private static string Revision(char digit = 'a') => "sha256:" + new string(digit, 64);
    private static ResourceCatalog Resources(char digit = 'a') => new("sample", Revision(digit), ["ink"], ["glitch"], ["spark"]);

    [Fact]
    public void 颜色是直乘且非有限越界和过亮结果拒绝()
    {
        var color = new ColorRgba(.8, .6, .4, .25);
        Assert.Equal(new ColorRgba(.4, .3, .2, .25), color.MultiplyRgb(.5));
        Assert.Equal(.8, color.Red);
        Assert.Equal(default, new ColorRgba(0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorRgba(double.NaN, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorRgba(0, 0, 0, 1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => color.MultiplyRgb(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => color.MultiplyRgb(2));
    }

    [Fact]
    public void 曲线参数保留控制点支持显式过冲但不排序或夹进度()
    {
        CurveValue linear = Curves.LinearCurve, easeOut = Curves.EaseOutCurve, smooth = Curves.EaseInOutCurve;
        Assert.Equal(.5, linear.Evaluate(.5)); Assert.Equal(.875, easeOut.Evaluate(.5));
        Assert.Equal(.15625, smooth.Evaluate(.25));
        var bezier = new CurveValue.CubicBezier(new(0, 0), new(1, 1));
        Assert.Equal(.25, bezier.Evaluate(.25), 14); Assert.Equal(0, bezier.Evaluate(0)); Assert.Equal(1, bezier.Evaluate(1));
        var points = new CurveValue.ControlPoints([new(0, 0), new(.5, 1.5), new(1, 1)]);
        Assert.Equal(1.5, points.Evaluate(.5)); Assert.Equal(.75, points.Evaluate(.25));
        var crossingX = new CurveValue.CubicBezier(new(1, 2), new(0, 2));
        Assert.True(crossingX.Evaluate(.5) > 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => smooth.Evaluate(1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => smooth.Evaluate(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurveValue.CubicBezier(new(-.1, 0), new(1, 1)));
        Assert.Throws<ArgumentException>(() => new CurveValue.ControlPoints([new(0, 0), new(.5, .2), new(.5, .4), new(1, 1)]));
        Assert.Throws<ArgumentException>(() => new CurveValue.ControlPoints([new(0, 0), new(.8, .4), new(.2, .4), new(1, 1)]));
        Assert.Throws<ArgumentException>(() => new CurveValue.ControlPoints(default));
    }

    [Fact]
    public void 所有参数类型严格读取资源固定修订而非路径或文本()
    {
        var resources = Resources(); var asset = resources.GetImage("spark");
        ParameterValues values = new(new Dictionary<string, ParameterValue>
        {
            ["color"] = new ParameterValue.Color(new(.8, .6, .4, .25)),
            ["curve"] = new ParameterValue.Curve(Curves.EaseInOutCurve), ["asset"] = new ParameterValue.Asset(asset),
            ["number"] = new ParameterValue.Number(1.35), ["whole"] = new ParameterValue.Whole(long.MaxValue),
            ["toggle"] = new ParameterValue.Toggle(true), ["choice"] = new ParameterValue.Choice("jump"),
            ["duration"] = new ParameterValue.Duration(long.MaxValue), ["text"] = new ParameterValue.Text(""),
        });
        Assert.Equal(.25, values.GetColor("color").Alpha); Assert.Same(Curves.EaseInOutCurve, values.GetCurve("curve"));
        Assert.Same(asset, values.GetAsset("asset")); Assert.Equal(1.35, values.GetNumber("number"));
        Assert.Equal(long.MaxValue, values.GetInteger("whole")); Assert.Equal(long.MaxValue, values.GetDuration("duration"));
        Assert.True(values.GetBoolean("toggle")); Assert.Equal("jump", values.GetEnum("choice")); Assert.Empty(values.GetText("text"));
        values.ValidateResources(resources);
        Assert.Equal("InvalidParameterResource", Assert.Throws<EffectOutputException>(() => values.ValidateResources(Resources('b'))).Code);
        Assert.Contains("asset", Assert.Throws<InvalidOperationException>(() => values.GetText("asset")).Message, StringComparison.Ordinal);
        Assert.Contains("color", Assert.Throws<InvalidOperationException>(() => values.GetNumber("color")).Message, StringComparison.Ordinal);
        Assert.Contains("absent", Assert.Throws<KeyNotFoundException>(() => values.GetCurve("absent")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new ParameterValue.Curve(null!));
        Assert.Throws<ArgumentNullException>(() => new ParameterValue.Asset(null!));
    }

    [Fact]
    public void 资源类型重复身份及不完整修订均拒绝不回退同名资源()
    {
        var resources = Resources();
        Assert.Equal(Revision(), resources.GetMaterial("ink").Identity.Revision);
        Assert.True(resources.Contains(resources.GetGraph("glitch")));
        Assert.False(resources.Contains(Resources('b').GetGraph("glitch")));
        Assert.Contains("image:ink", Assert.Throws<KeyNotFoundException>(() => resources.GetImage("ink")).Message, StringComparison.Ordinal);
        Assert.Null(ResourceCatalog.Empty.PackageId); Assert.Null(ResourceCatalog.Empty.Revision);
        Assert.Throws<KeyNotFoundException>(() => ResourceCatalog.Empty.GetImage("spark"));
        Assert.Throws<ArgumentException>(() => new ResourceCatalog("sample", Revision(), ["ink", "ink"], [], []));
        Assert.Throws<ArgumentException>(() => new ResourceCatalog("sample", Revision(), default, [], []));
        Assert.Throws<ArgumentException>(() => new ResourceCatalog("sample", "sha256:abc", [], [], []));
        Assert.Throws<ArgumentException>(() => new ResourceCatalog("sample", Revision().ToUpperInvariant(), [], [], []));
        Assert.Throws<ArgumentException>(() => new ResourceCatalog("sample", Revision(), [" "], [], []));
    }

    [Fact]
    public void PCG核心播种与官方六项已知向量一致()
    {
        // 官方 demo 固定 initstate=42、initseq=54；不把 SDK 哈希分流规则混入核心验证。
        MethodInfo sample = typeof(DeterministicRandom).GetMethod("Sample", BindingFlags.NonPublic | BindingFlags.Static)!;
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        for (ulong index = 0; index < (ulong)expected.Length; index++)
            Assert.Equal(expected[index], (uint)sample.Invoke(null, [42UL, 54UL, index])!);
    }

    [Fact]
    public void 随机分流字节合同含中文最大种子和全宽索引且不依赖调用顺序()
    {
        ulong[] indices = [0, 1, 2, 5, 1_000_000, ulong.MaxValue];
        var first = new DeterministicRandom(0, new("instance"));
        uint[] expected = [0x77b617a6, 0x5233620b, 0x0f4e08b6, 0xba7c00f5, 0xb17af4b2, 0x04a3a88c];
        Assert.Equal(expected, indices.Select(index => first.GetUInt32("stream", index)));
        Assert.Equal(expected.Reverse(), indices.Reverse().Select(index => first.GetUInt32("stream", index)));
        var unicode = new DeterministicRandom(ulong.MaxValue, new("实例😀"));
        uint[] unicodeExpected = [0x104464d6, 0x02987f62, 0xf93c19ab, 0xa6c070e9, 0xd7dc06ec, 0x57a06996];
        Assert.Equal(unicodeExpected, indices.Select(index => unicode.GetUInt32("发射/拖尾", index)));
        Assert.Equal(expected.Select(value => value / 4294967296d), indices.Select(index => first.GetUnitDouble("stream", index)));
        Assert.NotEqual(new DeterministicRandom(42, new("ab")).GetUInt32("c", 0), new DeterministicRandom(42, new("a")).GetUInt32("bc", 0));
        Assert.NotEqual(first.GetUInt32("stream", 0), first.GetUInt32("another", 0));
        Assert.NotEqual(first.GetUInt32("stream", 0), new DeterministicRandom(1, new("instance")).GetUInt32("stream", 0));
        Assert.NotEqual(first.GetUInt32("stream", 0), new DeterministicRandom(0, new("another")).GetUInt32("stream", 0));
        Assert.Throws<ArgumentException>(() => new DeterministicRandom(0, default));
        Assert.Throws<ArgumentException>(() => first.GetUInt32(" ", 0));
        Assert.Throws<System.Text.EncoderFallbackException>(() => first.GetUInt32("\ud800", 0));
    }

    private static AudioFeatures Audio() => new("analysis:fixture", 60_000,
        [new(0, new(0, 0, null, 0)), new(20_000, new(.8, .4, .25, .6)), new(40_000, new(.3, .1, .75, .2))]);

    [Theory]
    [InlineData(AudioAvailability.NoAudio)]
    [InlineData(AudioAvailability.NotAnalyzed)]
    [InlineData(AudioAvailability.AnalysisFailed)]
    public void 未完成分析不以假的零能量代替且保留具体原因(AudioAvailability state)
    {
        var audio = AudioFeatures.Unavailable(state, "fixture:" + state);
        var sample = audio.GetAt(long.MaxValue); Assert.Equal(state, sample.Availability); Assert.Null(sample.Values);
        Assert.Equal("fixture:" + state, sample.DiagnosticCode); Assert.Null(audio.DurationUs); Assert.Null(audio.Revision);
        var range = audio.GetRange(new(0, long.MaxValue)); Assert.Equal(state, range.Availability);
        Assert.Null(range.AvailableRange); Assert.Empty(range.Points); Assert.Equal(sample.DiagnosticCode, range.DiagnosticCode);
    }

    [Fact]
    public void 音频只在真实半开区间提供左端分析值静音与未知拍点分开()
    {
        var audio = Audio();
        var silent = audio.GetAt(0); Assert.Equal(AudioAvailability.Available, silent.Availability);
        Assert.Equal(0, silent.Values!.Value.BassLevel); Assert.Null(silent.Values.Value.BeatPhase);
        Assert.Equal(0, silent.Values.Value.BeatConfidence);
        Assert.Equal(0, audio.GetAt(19_999).Values!.Value.Pulse);
        Assert.Equal(.8, audio.GetAt(20_000).Values!.Value.BassLevel);
        Assert.Equal(.8, audio.GetAt(39_999).Values!.Value.BassLevel);
        Assert.Equal(.3, audio.GetAt(40_000).Values!.Value.BassLevel);
        Assert.Equal(.3, audio.GetAt(59_999).Values!.Value.BassLevel);
        var end = audio.GetAt(60_000); Assert.Equal(AudioAvailability.OutsideRange, end.Availability); Assert.Null(end.Values);
        Assert.Null(audio.GetAt(long.MaxValue).Values);
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.GetAt(-1));
        var range = audio.GetRange(new(20_000, 40_000)); Assert.Equal(AudioAvailability.Available, range.Availability);
        Assert.Equal(new TimeRange(20_000, 40_000), range.AvailableRange); Assert.Equal(20_000, Assert.Single(range.Points).TimeUs);
        var withinHop = audio.GetRange(new(20_001, 21_000)); Assert.Equal(AudioAvailability.Available, withinHop.Availability); Assert.Empty(withinHop.Points);
        var partial = audio.GetRange(new(40_000, 80_000)); Assert.Equal(AudioAvailability.OutsideRange, partial.Availability);
        Assert.Equal(new TimeRange(40_000, 60_000), partial.AvailableRange); Assert.Single(partial.Points);
        var outside = audio.GetRange(new(60_000, 80_000)); Assert.Null(outside.AvailableRange); Assert.Empty(outside.Points);
        Assert.Throws<ArgumentException>(() => audio.GetRange(default));
    }

    [Fact]
    public void 分析输入不排序不填空且非法节奏值拒绝()
    {
        Assert.Throws<ArgumentException>(() => new AudioFeatures("revision", 10, []));
        Assert.Throws<ArgumentException>(() => new AudioFeatures("revision", 10, [new(1, default)]));
        Assert.Throws<ArgumentException>(() => new AudioFeatures("revision", 10, [new(0, default), new(10, default)]));
        Assert.Throws<ArgumentException>(() => new AudioFeatures("revision", 10, [new(0, default), new(5, default), new(3, default)]));
        Assert.Throws<ArgumentException>(() => new AudioFeatures("revision", 10, [new(0, default), new(0, default)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioFeatures.Unavailable(AudioAvailability.Available, "wrong"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFeatureValues(1.1, 0, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFeatureValues(0, 0, 1, .1));
        Assert.Throws<ArgumentException>(() => new AudioFeatureValues(0, 0, null, .1));
    }

    [Fact]
    public async Task 真正编译入口在初始化和任意纯帧读取同一固定服务()
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var package = await CompiledEffectPackage.CompileAsync(V2PackageTestHelper.CreateControllerPackage("""
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class ServicesProbe : IPureKaraokeEffect
            {
                private sealed record ProbeData(ResourceCatalog Resources, AudioFeatures Audio) : PreparedEffect;
                public PreparedEffect Declare(DeclareContext source)
                {
                    if (source.Resources.GetMaterial("ink").Identity.PackageId != "sample"
                        || source.Resources.GetGraph("glitch").Identity.ExportId != "glitch"
                        || source.Parameters.GetAsset("asset") != source.Resources.GetImage("spark")
                        || source.Parameters.GetColor("color").Alpha != .25
                        || source.Parameters.GetCurve("curve").Evaluate(.5) != .5
                        || source.Random.GetUInt32("stream", 0) != 0x77b617a6)
                        throw new Exception("InitServiceMismatch");
                    return new ProbeData(source.Resources, source.Audio);
                }
                public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output)
                {
                    var data = (ProbeData)prepared;
                    if (!ReferenceEquals(data.Resources, frame.Resources)
                        || !ReferenceEquals(data.Audio, frame.Audio)
                        || frame.Random.GetUInt32("stream", 5) != 0xba7c00f5)
                        throw new Exception("FrameServiceMismatch");
                    var value = frame.Audio.GetAt(frame.TimeUs);
                    if (frame.TimeUs < 60000 && (value.Availability != AudioAvailability.Available || value.Values is null))
                        throw new Exception("AudioMismatch");
                    if (frame.TimeUs == 0 && value.Values!.Value.BeatPhase is not null)
                        throw new Exception("FakeBeat");
                    if (frame.TimeUs == 60000 && (value.Availability != AudioAvailability.OutsideRange || value.Values is not null))
                        throw new Exception("FakeEndAudio");
                }
            }
            """, "ServicesProbe", ["token.affine"], ["project"]), compiler);
        Assert.True(package.Artifact.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var resources = Resources();
        ParameterValues parameters = new(new Dictionary<string, ParameterValue>
        {
            ["asset"] = new ParameterValue.Asset(resources.GetImage("spark")), ["color"] = new ParameterValue.Color(new(.8, .6, .4, .25)),
            ["curve"] = new ParameterValue.Curve(Curves.EaseInOutCurve),
        });
        var effect = new CompiledFrameEffect(new("instance"), package.Artifact, "ServicesProbe", parameters, new EffectBinding.Project(), EffectCapabilities.None)
            { Resources = resources, Seed = 0, PackageDefinition = package.Definition, PackageDeclaration = package.Definition.GetExport("probe") };
        PureFrameSourceFactory factory = new([effect], "services-fixed", static () => new ActiveTokenGeometry(), Audio());
        var snapshot = ActiveSnapshot("services", "services-fixed");
        await using var source = await factory.CreateAsync(snapshot);
        foreach (long time in new long[] { 0, 20_000, 59_999, 60_000, 0 })
            Assert.Empty((await source.PrepareAsync(time, OutputMode.Transparent, new() { PixelWidth = 100, PixelHeight = 100 })).TokenTransforms);
        await using var other = await factory.CreateAsync(snapshot);
        Assert.Empty((await other.PrepareAsync(20_000, OutputMode.Opaque, new() { PixelWidth = 200, PixelHeight = 200 })).TokenTransforms);
        var badResources = new PureFrameSourceFactory([effect with { Resources = Resources('b') }], "services-fixed", static () => new ActiveTokenGeometry(), Audio());
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await badResources.CreateAsync(snapshot));
        Assert.Equal("ValidateInputs", error.Stage); Assert.Equal("InvalidParameterResource", error.Code); Assert.Equal(effect.Id, error.InstanceId);
    }

    [Fact]
    public async Task 没有注入分析时真实帧读取未分析而不是假静音()
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var package = await CompiledEffectPackage.CompileAsync(V2PackageTestHelper.CreateControllerPackage("""
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class NoAnalysis : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect prepared, TextOutput output)
                {
                    var sample = frame.Audio.GetAt(frame.TimeUs);
                    if (sample.Availability != AudioAvailability.NotAnalyzed || sample.Values is not null)
                        throw new Exception("FakeAnalyzedAudio");
                }
            }
            """, "NoAnalysis", ["token.affine"], ["project"]), compiler);
        Assert.True(package.Artifact.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var factory = new PureFrameSourceFactory([package.CreateEffect("probe", new("instance"), new EffectBinding.Project(), 0)],
            "unavailable", static () => new ActiveTokenGeometry());
        await using var source = await factory.CreateAsync(ActiveSnapshot("no-analysis", "unavailable"));
        Assert.Empty((await source.PrepareAsync(0, OutputMode.Opaque, new() { PixelWidth = 100, PixelHeight = 100 })).TokenTransforms);
    }

    private static ProjectSnapshot ActiveSnapshot(string name, string fingerprint)
        => ProjectSnapshot.FromProject(Core.Projects.Project.Create(name, lyrics: Core.Lyrics.LyricsDocument.Create([
            Core.Lyrics.LyricLine.Create("line", "甲", new(0, 100_000), Core.Lyrics.TimingStatus.Edited,
                [Core.Lyrics.LyricToken.Create("token", "甲", new(0, 100_000), Core.Lyrics.TimingStatus.Edited)])])))
            with { EffectInputsFingerprint = fingerprint };

    private sealed class ActiveTokenGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => LyricSceneSampler.SampleFrame(snapshot, timeUs);
    }
}
