using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class AudioReactiveFrameTests
{
    private const string Fingerprint = "fixed-actual-audio";
    private static readonly Lazy<Task<CompiledEffectPackage>> Package = new(() => V2RenderPackageHelper.CompileControllerAsync("""
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed record AudioState : PreparedEffect;

        public sealed class AudioProbe : IPureKaraokeEffect, IPureVisualEffect
        {
            public PreparedEffect Declare(DeclareContext source)
                => new AudioState { VisualOutputs = [new(new("bass-pulse"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var sample = frame.Audio.GetAt(frame.TimeUs);
                if (sample.Availability != AudioAvailability.Available)
                {
                    if (sample.Values != null) throw new InvalidOperationException("unavailable must not be fake silence");
                    return;
                }
                if (frame.Audio.GetRange(new(0, 2_000_000)).Points.Length != 100)
                    throw new InvalidOperationException("expected 100 real audio hop points");
                double shade = Math.Round(255 * sample.Values!.Value.BassLevel) / 255;
                foreach (var token in frame.Presentations.TokensInBinding)
                {
                    var target = new TextTarget.Family(token.WithRuby);
                    output.SetFill(target, TextState.Unsung, MaterialValue.Solid(new ColorRgba(0, shade, 0, 1)));
                    output.SetFill(target, TextState.Sung, MaterialValue.Solid(new ColorRgba(0, shade, 0, 1)));
                }
            }
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
            {
                var sample = frame.Audio.GetAt(frame.TimeUs);
                if (sample.Values is not { } values) return;
                output.DrawDots(new OutputId("bass-pulse"),
                    [new Dot(new Point2(20, 20), 4 + 8 * values.Pulse, new ColorRgba(1, 0, 0, .5))], OverlayLayer.AboveText);
            }
        }

        public sealed class PureAudioProbe : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var sample = frame.Audio.GetAt(frame.TimeUs);
                if (sample.Values != null && sample.Availability != AudioAvailability.Available)
                    throw new InvalidOperationException("unexpected values when unavailable");
            }
        }
        """,
        [Fx.V2.CapabilityNames.StyleFill, Fx.V2.CapabilityNames.VisualsDots],
        entryType: "AudioProbe",
        exportId: "AudioProbe",
        outputs:
        [
            V2RenderPackageHelper.Output("fill", "StyleModulation", "tokenFamily", Fx.V2.CapabilityNames.StyleFill),
            V2RenderPackageHelper.Output("bass-pulse", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots),
        ],
        packageId: "demo.audio.probe.v2",
        manifestChange: manifest =>
        {
            manifest["exports"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "PureAudioProbe",
                ["kind"] = "controller",
                ["name"] = "PureAudioProbe",
                ["entryType"] = "PureAudioProbe",
                ["parameters"] = new System.Text.Json.Nodes.JsonArray(),
                ["bindingKinds"] = new System.Text.Json.Nodes.JsonArray("project"),
                ["outputs"] = new System.Text.Json.Nodes.JsonArray(),
            });
        }));
    private static async Task<CompiledFrameEffect> Definition(bool pure = false)
    {
        var package = await Package.Value;
        return package.CreateEffect(pure ? "PureAudioProbe" : "AudioProbe", new("audio"),
            new Fx.EffectBinding.Project(), 0) with { Subscription = new Fx.SourceSubscription.Project() };
    }
    private static ProjectSnapshot Snapshot()
    {
        var line = LyricLine.Create("row", "田田", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "田", new(0, 1_000_000), TimingStatus.Edited), LyricToken.Create("b", "田", new(1_000_000, 2_000_000), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "た")] };
        var project = Project.Create("audio", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 2_000_000), LyricsDocument.Create([line]));
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    }
    private static async Task<Fx.AudioFeatures> Analyze(TemporaryDirectory directory)
    {
        string path = directory.FilePath("pulses.wav");
        WavWriter.Write(path, Enumerable.Range(0, 96_000).Select(index =>
        { double time = index / 48_000d; return time % .5 < .1 ? (float)(.7 * Math.Sin(2 * Math.PI * 80 * time)) : 0; }).ToArray(), 48_000);
        var result = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(path);
        Assert.Equal(Fx.AudioAvailability.Available, result.Features.Availability); return result.Features;
    }
    private static PureFrameSourceFactory Factory(CompiledFrameEffect effect, Fx.AudioFeatures audio)
        => new([effect], Fingerprint, static () => new SkiaRenderPipeline(), audio);

    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 真实音频脚本在不同帧率中段回退和独立导出得到相同画面和真实hop(int fps)
    {
        using var directory = new TemporaryDirectory(); var audio = await Analyze(directory); var definition = await Definition();
        var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition); const long time = 747_531;
        await using var sequential = await Factory(definition, audio).CreateAsync(snapshot);
        for (int index = 0; index * 1_000_000L / fps < time; index++) await sequential.PrepareAsync(index * 1_000_000L / fps, Fx.OutputMode.Transparent, target);
        var frame = await sequential.PrepareAsync(time, Fx.OutputMode.Transparent, target);
        using SkiaRenderPipeline renderer = new();
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = frame, TransparentBackground = true });
        await using var direct = await Factory(definition, audio).CreateAsync(snapshot);
        var other = await direct.PrepareAsync(time, Fx.OutputMode.Transparent, target);
        using var pixels = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = other, TransparentBackground = true });
        Assert.Equal(actual.PixelMemory.ToArray(), pixels.PixelMemory.ToArray());
        Assert.Equal(frame.TokenStyles.ToArray(), other.TokenStyles.ToArray());
        await sequential.PrepareAsync(240_000, Fx.OutputMode.Transparent, target);
        var replay = await sequential.PrepareAsync(time, Fx.OutputMode.Transparent, target);
        using var redrawn = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = replay, TransparentBackground = true });
        Assert.Equal(actual.PixelMemory.ToArray(), redrawn.PixelMemory.ToArray());
        Assert.Equal(100, audio.GetRange(new(0, 2_000_000)).Points.Length);
    }
    [Theory]
    [InlineData(20_000)] [InlineData(280_000)] [InlineData(520_000)]
    public async Task 实际分析驱动的文字注音和圆点像素等于静态外观加独立绘制(long time)
    {
        using var directory = new TemporaryDirectory(); var audio = await Analyze(directory); var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await Factory(await Definition(), audio).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(time, Fx.OutputMode.Transparent, target);
        byte green = (byte)Math.Round(audio.GetAt(time).Values!.Value.BassLevel * 255);
        var branch = snapshot.Lyrics.PrimaryBranch!; var row = branch.Lines[0];
        var expectedRow = row with { Tokens = [.. row.Tokens.Select(token => token with { StyleOverride = new() { UnsungColor = new(0, green, 0), SungColor = new(0, green, 0) } })] };
        var expectedSnapshot = snapshot with { EffectInputsFingerprint = null, Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = [expectedRow] }) };
        using SkiaRenderPipeline renderer = new();
        using var basis = renderer.Render(RenderRequest.At(expectedSnapshot, time) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = frame, TransparentBackground = true });
        var dot = Assert.Single(Assert.Single(frame.DotBatches).Dots);
        Assert.Equal(4 + 8 * audio.GetAt(time).Values!.Value.Pulse, dot.Radius);
        byte[] golden = SkiaTestDrawing.DrawOnSelectedBackend(basis.PixelMemory.ToArray(), basis.Width, basis.Height, canvas =>
        { using SKPaint paint = new() { IsAntialias = true, Color = new(255, 0, 0, 128) }; canvas.DrawCircle(20, 20, (float)dot.Radius, paint); });
        Assert.Equal(golden, actual.PixelMemory.ToArray()); Assert.Same(snapshot.Lyrics, frame.Snapshot.Lyrics);
    }
    [Theory]
    [InlineData(Fx.AudioAvailability.NoAudio)] [InlineData(Fx.AudioAvailability.NotAnalyzed)] [InlineData(Fx.AudioAvailability.AnalysisFailed)]
    public async Task 不可用音频状态下脚本读到真实不可用状态(Fx.AudioAvailability availability)
    {
        var audio = Fx.AudioFeatures.Unavailable(availability, "proof"); var snapshot = Snapshot(); var definition = await Definition();
        await using var optional = await Factory(definition, audio).CreateAsync(snapshot);
        var frame = await optional.PrepareAsync(20_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Empty(frame.TokenStyles); Assert.Empty(frame.DotBatches);
    }
}
