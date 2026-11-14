using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ParticleRuntimeTests
{
    private const string Fingerprint = "m08-runtime-fixed-input";

    [Fact]
    public async Task 默认Exact出生锚点等于真实出生时刻完整文字且热缓存不重复文字补算()
    {
        var effect = await Effect(continuous: false);
        var snapshot = Snapshot();
        await using var session = await Factory([effect]).CreateAsync(snapshot);
        var frame = await Read(session, snapshot, 30_000);
        Assert.Equal(6, frame.V2Particles.Length);
        Assert.Equal(6, frame.V2AnchorStatistics.UniqueTimes); Assert.Equal(6, frame.V2AnchorStatistics.TextCalls);
        var first = frame.V2Particles[0]; Assert.Equal(5_000, first.BirthUs);
        var exactText = await Read(session, snapshot, first.BirthUs);
        Assert.Equal(Assert.Single(exactText.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position, first.Birth.BirthPosition);
        var warm = await Read(session, snapshot, 30_000);
        Assert.Equal(frame.V2Particles.ToArray(), warm.V2Particles.ToArray());
        Assert.Equal(0, warm.V2AnchorStatistics.TextCalls); Assert.Equal(0, warm.V2AnchorStatistics.UniqueTimes);
        Assert.True(warm.V2AnchorStatistics.MemoHits >= 6);
        Assert.Empty(frame.Particles);
        var copied = frame.WithGroupOpacities([]).WithOverlayOutput(frame.OverlayBatches, []);
        Assert.Equal(frame.V2Particles.ToArray(), copied.V2Particles.ToArray());
        Assert.Same(frame.V2AnchorStatistics, copied.V2AnchorStatistics);
    }

    [Fact]
    public async Task 显式连续模式只在固定网格同区段插值且结果不同于非线性真值()
    {
        var effect = await Effect(continuous: true);
        var snapshot = Snapshot();
        await using var session = await Factory([effect]).CreateAsync(snapshot);
        var atZero = await Read(session, snapshot, 0);
        var atGrid = await Read(session, snapshot, 8_333);
        var frame = await Read(session, snapshot, 30_000);
        var birth = frame.V2Particles.Single(item => item.BirthUs == 5_000).Birth;
        var left = Assert.Single(atZero.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position;
        var right = Assert.Single(atGrid.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position;
        double amount = 5_000d / 8_333;
        Assert.Equal(left.X * (1 - amount) + right.X * amount, birth.BirthPosition.X, 8);
        Assert.Equal(left.Y * (1 - amount) + right.Y * amount, birth.BirthPosition.Y, 8);
        var exact = await Read(session, snapshot, 5_000);
        Assert.NotEqual(Assert.Single(exact.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position, birth.BirthPosition);
        Assert.InRange(frame.V2AnchorStatistics.UniqueTimes, 1, 4);
    }

    [Fact]
    public async Task 其他必要文字实例缺少连续证明时整条锚点走Exact并保留BaseOnly和Ruby分区()
    {
        var emitter = await Effect(continuous: true, partitions: true);
        var other = emitter with { Id = new("other") };
        var snapshot = Snapshot(ruby: true);
        await using var session = await Factory([other, emitter]).CreateAsync(snapshot);
        var frame = await Read(session, snapshot, 30_000);
        Assert.Equal(6, frame.V2AnchorStatistics.UniqueTimes); Assert.Equal(12, frame.V2AnchorStatistics.TextCalls);
        var birth = frame.V2Particles.First().Birth;
        var direct = await Read(session, snapshot, birth.BirthUs);
        Assert.Equal(Assert.Single(direct.Playback.GetCursors(CursorAnchorMode.AdvanceCenter)).Position, birth.BirthPosition);
        Assert.NotEmpty(direct.BaseTransforms); Assert.NotEmpty(direct.ClusterTransforms);
        var token = Assert.Single(direct.FinalGeometry);
        Assert.NotEqual(token.BaseGeometry.BaseWorldMatrix, Assert.Single(token.Ruby).Geometry.BaseWorldMatrix);
    }

    [Fact]
    public async Task 同绑定纯V2行变换组合不阻塞新锚点并传导偏移()
    {
        var emitter = await Effect(continuous: false);
        var lineTransform = await LineTransform();
        var snapshot = Snapshot(otherLine: true);
        await using var sameBinding = await Factory([lineTransform, emitter]).CreateAsync(snapshot);
        var shifted = await Read(sameBinding, snapshot, 30_000);
        Assert.NotEmpty(shifted.V2Particles);
        var separate = lineTransform with { Binding = new EffectBinding.Lines([new("other-line")]) };
        await using var otherBinding = await Factory([separate, emitter]).CreateAsync(snapshot);
        var baseline = await Read(otherBinding, snapshot, 30_000);
        Assert.NotEmpty(baseline.V2Particles);
        Assert.Equal(baseline.V2Particles[0].Birth.BirthPosition.X + 7f, shifted.V2Particles[0].Birth.BirthPosition.X, 2);
    }

    [Fact]
    public async Task 当前已无播放头仍保留全部寿命内粒子并在完整出生窗保留死粒子()
    {
        var emitter = await Effect(continuous: false);
        var snapshot = Snapshot();
        await using var session = await Factory([emitter]).CreateAsync(snapshot);
        var late = await Read(session, snapshot, 150_000);
        Assert.Empty(late.Playback.GetCursors(CursorAnchorMode.AdvanceCenter));
        Assert.Equal(19, late.V2Particles.Length); Assert.Single(late.OverlayBatches);
        var dead = await Read(session, snapshot, 300_000);
        Assert.Empty(dead.V2Particles); Assert.Empty(dead.OverlayBatches);
        Assert.Equal(19, Assert.Single(dead.V2ParticleSnapshots).Births.Births.Length);
    }

    private static async Task<CompiledFrameEffect> Effect(bool continuous, bool partitions = false)
    {
        string source = """
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed record ProbeData : PreparedEffect;
            public sealed class CursorTrail : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    if (source.InstanceId.Value == "other") return new ProbeData();
                    return new ProbeData
                    {
                        Particles = new([new(new("cursor"), new([new(new(0, 100_000), 200, 200)], [new(0, 100_000)]),
                            new ParticleSpec { LifetimeUs = 200_000, SizePx = 4, Color = new(1, 1, 1, 1), SeedStreamKey = "probe" },
                            CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump)], []),
                        SAMPLING
                    };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    foreach (var token in frame.Geometry)
                    {
                        if (frame.Source.InstanceId.Value == "other")
                        {
                            PARTITION
                        }
                        else output.AppendTransform(token.WithRuby, Affine2.Translation(frame.TimeUs * (double)frame.TimeUs / 100_000_000, 0));
                    }
                }
            }
            """;
        source = source.Replace("SAMPLING", continuous
                ? "GeometrySampling = new(GeometrySamplingMode.Continuous120Hz, [new(0, 100_000)], [])," : "", StringComparison.Ordinal)
            .Replace("PARTITION", partitions ? """
                output.AppendTransform(token.BaseOnly, Affine2.Translation(10, 3));
                foreach (var cluster in token.Clusters)
                    if (cluster.Target.RubyId is null) output.AppendTransform(new TextTarget.Cluster(cluster.Target), Affine2.Translation(2, 1));
                """ : "output.AppendTransform(token.WithRuby, Affine2.Translation(11, 0));", StringComparison.Ordinal);
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "CursorTrail"));
        const string path = "src/CursorTrail.cs"; byte[] bytes = Encoding.UTF8.GetBytes(source);
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!["sha256"]
            = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var requires = manifest["requires"]!.AsArray();
        foreach (string capability in new[] { "token.affine", "cluster.affine" }) requires.Add(capability);
        var outputs = manifest["exports"]![0]!["outputs"]!.AsArray();
        var cursorRequires = outputs[0]!["requires"]!.AsArray();
        if (!cursorRequires.Any(item => item!.GetValue<string>() == "particles.continuous")) cursorRequires.Add("particles.continuous");
        foreach (string target in new[] { "tokenFamily", "baseToken", "cluster" })
            outputs.Add(new JsonObject { ["id"] = target, ["kind"] = "VisualTransform", ["target"] = target,
                ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() });
        package = EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)(file.Path == path ? bytes : file.Content.ToArray()))));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(package), Compiler());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled.CreateEffect("cursor-trail", new("emitter"), new EffectBinding.Project(), 0);
    }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static async Task<CompiledFrameEffect> LineTransform()
    {
        var compiled = await V2RenderPackageHelper.CompileControllerAsync("""
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;

            public sealed class LineTransform : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    foreach (var line in frame.Presentations.LinesInBinding)
                        output.AppendTransform(new TextTarget.Line(line.Target), Affine2.Translation(7, 0));
                }
            }
            """,
            [CapabilityNames.LineAffine],
            entryType: "LineTransform",
            exportId: "line-transform",
            outputs: [V2RenderPackageHelper.Output("line", "VisualTransform", "line", CapabilityNames.LineAffine)],
            packageId: "demo.runtime.linetransform.v2");
        return compiled.CreateEffect("line-transform", new("line-transform"), new EffectBinding.Project(), 0);
    }
    private static PureFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline());
    private static ValueTask<PreparedRenderFrame> Read(IEvaluatedFrameSource session, ProjectSnapshot snapshot, long timeUs)
        => session.PrepareAsync(timeUs, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static ProjectSnapshot Snapshot(bool ruby = false, bool otherLine = false)
    {
        var line = LyricLine.Create("line", "AB", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("word", "AB", new(0, 100_000), TimingStatus.Edited)]);
        if (ruby) line = line with { RubyAnnotations = [RubyAnnotation.Create("ruby", "word", "xy")] };
        List<LyricLine> lines = [line];
        if (otherLine) lines.Add(LyricLine.Create("other-line", "C", new(200_000, 300_000), TimingStatus.Edited,
            [LyricToken.Create("other-word", "C", new(200_000, 300_000), TimingStatus.Edited)]));
        var project = Project.Create("M08 runtime", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { EnterAnimation = null }) with { EffectInputsFingerprint = Fingerprint };
    }
}
