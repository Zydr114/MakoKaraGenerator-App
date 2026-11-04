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

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2MixedReplayTests
{
    private const string Fingerprint = "mixed-v2-fixed-source";
    private const string PureSource = """
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record ProbeData(int Count) : PreparedEffect;
        public sealed class WordEmphasis : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source)
            {
                var basis = source.Indexes.Layout.GetBaseAt(123_456, new SourceFilter.Project());
                return new ProbeData(basis.Tokens.Length);
            }
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                if (frame.TimeUs == 8_333) throw new InvalidOperationException("UnexpectedIntermediatePure");
                if (((ProbeData)data).Count != 1) throw new InvalidOperationException("MissingDeclareGeometry");
                foreach (var token in frame.Presentations.TokensInBinding)
                    output.AppendTransform(token.WithRuby, Affine2.Translation(11, 0));
            }
        }
        """;

    [Fact]
    public async Task 混合文字旧作者跳过纯作者回放网格且跳转和输出重置保留Declare()
    {
        var pure = await PackageEffect("WordEmphasis", PureSource);
        var legacy = await LegacyEffect("""
            using MakoKaraGenerator.Scripting;
            public sealed class Legacy : EffectBase
            {
                public override void Advance(SimulationStep step, StateContext context) { }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    foreach (var line in frame.Presentations.LinesInBinding)
                        output.Transforms.ApplyAffine(line.Target, Affine2.Translation(7, 0));
                }
            }
            """);
        var snapshot = Snapshot(); var measured = new CountingGeometry();
        var factory = Factory([pure, legacy], () => measured);
        await using var session = await factory.CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var first = await session.PrepareAsync(750_000, OutputMode.Transparent, target);
        var seek = await session.PrepareAsync(250_000, OutputMode.Transparent, target);
        var outputReset = await session.PrepareAsync(750_000, OutputMode.Opaque, target);
        var targetReset = await session.PrepareAsync(750_000, OutputMode.Transparent,
            new() { PixelWidth = 320, PixelHeight = 180 });
        Assert.Equal(11, Assert.Single(first.TokenTransforms).LocalMatrix.Tx);
        Assert.Equal(7, Assert.Single(first.LineTransforms).LocalMatrix.Tx);
        Assert.Equal(first.TokenTransforms.ToArray(), seek.TokenTransforms.ToArray());
        Assert.Equal(first.TokenTransforms.ToArray(), outputReset.TokenTransforms.ToArray());
        Assert.Equal(first.LineTransforms.ToArray(), targetReset.LineTransforms.ToArray());
        // Replay has evicted this time from the shared eight-frame cache. A second
        // Declare would need a fresh backend call instead of hiding behind its cache.
        Assert.True(measured.Calls.Count > 8);
        Assert.Equal(1, measured.Calls.Count(static time => time == 123_456));
        await using var reopened = await Factory([pure, legacy], static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var direct = await reopened.PrepareAsync(750_000, OutputMode.Transparent, target);
        Assert.Equal(first.TokenTransforms.ToArray(), direct.TokenTransforms.ToArray());
        Assert.Equal(first.LineTransforms.ToArray(), direct.LineTransforms.ToArray());
        Assert.Equal(Pixels(snapshot, first), Pixels(snapshot, direct));
    }

    [Fact]
    public async Task 旧视觉消费者保留纯作者边界求值及真实实例包失败归属()
    {
        var pure = await PackageEffect("WordEmphasis", PureSource);
        var legacy = await LegacyEffect("""
            using MakoKaraGenerator.Scripting;
            public sealed class Legacy : EffectBase
            {
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output) { }
            }
            """);
        var snapshot = Snapshot();
        await using var session = await Factory([pure, legacy], static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(100_000,
            OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal(pure.Id, error.InstanceId); Assert.Equal(pure.PackageIdentity, error.PackageIdentity);
        Assert.Equal("Evaluate", error.Stage); Assert.Equal(8_333, error.TimeUs);
        Assert.Equal("ScriptExecutionFailed", error.Code);
        Assert.Contains("UnexpectedIntermediatePure", error.InnerException!.Message);
    }

    [Fact]
    public async Task 旧发射和视觉阶段在回放边界观察纯作者最终文字变换()
    {
        var source = PureSource.Replace("if (frame.TimeUs == 8_333) throw new InvalidOperationException(\"UnexpectedIntermediatePure\");", "", StringComparison.Ordinal);
        var pure = await PackageEffect("WordEmphasis", source);
        var legacy = await LegacyEffect("""
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class Legacy : EffectBase
            {
                private Affine2 _expected;
                public override void Initialize(EffectInitializationContext context)
                {
                    var basis = context.GeometryQueries.GetBaseAt(0, new SourceFilter.Project());
                    _expected = basis.Tokens[0].BaseGeometry.BaseWorldMatrix.Compose(Affine2.Translation(11, 0));
                }
                private void Check(FinalSceneGeometry geometry)
                {
                    if (geometry.Tokens.Length != 1 || geometry.Tokens[0].BaseGeometry.BaseWorldMatrix != _expected)
                        throw new InvalidOperationException("PureBoundaryGeometryMissing");
                }
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output) => Check(frame.FinalGeometry);
                public override void AdvanceVisuals(VisualSimulationStep step, VisualStateContext context)
                { Check(context.PreviousGeometry); Check(context.CurrentGeometry); }
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                { Check(frame.FinalGeometry); if (frame.BoundaryGeometry is { } boundary) Check(boundary); }
            }
            """);
        var snapshot = Snapshot();
        await using var session = await Factory([pure, legacy], static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(100_001, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(11, Assert.Single(frame.TokenTransforms).LocalMatrix.Tx);
    }

    [Fact]
    public async Task 退出副本不进入旧文字发射视觉和视觉推进的呈现或组墨迹()
    {
        var pure = await PackageEffect("ClusterExit");
        var legacy = await LegacyEffect("""
            using System;
            using System.Collections.Immutable;
            using MakoKaraGenerator.Scripting;
            public sealed class Legacy : EffectBase
            {
                private static void CheckGroups(ImmutableArray<GroupGeometrySample> groups)
                {
                    foreach (var group in groups)
                        if (group.Target.NodeId.Value.StartsWith("exit:", StringComparison.Ordinal) || group.WorldInkCorners.Length > 4)
                            throw new InvalidOperationException("ExitGroupLeaked");
                }
                private static void Check(FinalSceneGeometry geometry)
                {
                    CheckGroups(geometry.Groups);
                    foreach (var group in geometry.Presentations.Groups)
                        if (group.Target.NodeId.Value.StartsWith("exit:", StringComparison.Ordinal))
                            throw new InvalidOperationException("ExitGroupPresentationLeaked");
                    if (geometry.TimeUs < 1_000_000) return;
                    if (geometry.Tokens.Length != 1 || geometry.Lines.Length != 1 || geometry.Presentations.TokensInBinding.Length != 1
                        || geometry.Presentations.LinesInBinding.Length != 1 || geometry.Tokens[0].LineId.Value != "line-1"
                        || geometry.Lines[0].Target.LineId.Value != "line-1")
                        throw new InvalidOperationException("ExitLoadLeaked");
                }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    CheckGroups(frame.GroupGeometry);
                    if (frame.TimeUs >= 1_000_000 && (frame.Geometry.Length != 1 || frame.Geometry[0].LineId.Value != "line-1"))
                        throw new InvalidOperationException("ExitTextInputLeaked");
                }
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output) => Check(frame.FinalGeometry);
                public override void AdvanceVisuals(VisualSimulationStep step, VisualStateContext context)
                { Check(context.PreviousGeometry); Check(context.CurrentGeometry); }
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                { Check(frame.FinalGeometry); if (frame.BoundaryGeometry is { } boundary) Check(boundary); }
            }
            """);
        var snapshot = Snapshot();
        await using var session = await Factory([pure, legacy], static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(1_250_000, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal("line-0", Assert.Single(frame.Scene.Lines.Where(static line => line.ExitReplica is not null)).Frame.Line!.Id);
        Assert.Contains(frame.FinalGroupGeometry, static group => !group.Target.NodeId.Value.StartsWith("exit:", StringComparison.Ordinal)
            && group.WorldInkCorners.Length > 4);
    }

    private static async Task<CompiledFrameEffect> PackageEffect(string sample, string? source = null)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", sample));
        if (source is not null)
        {
            const string path = "src/WordEmphasis.cs";
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
            manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == path)!["sha256"]
                = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            package = EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
                package.Files.Select(file => KeyValuePair.Create(file.Path,
                    (ReadOnlyMemory<byte>)(file.Path == path ? bytes : file.Content.ToArray()))));
        }
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(package), Compiler());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled.CreateEffect(compiled.Definition.Controllers.Single().Id, new("pure"), new EffectBinding.Project(), 0);
    }
    private static async Task<CompiledFrameEffect> LegacyEffect(string source)
    {
        var artifact = await Compiler().CompileAsync([new("src/Legacy.cs", source)]);
        Assert.True(artifact.Succeeded, string.Join('\n', artifact.Diagnostics));
        return new(new("legacy"), artifact, "Legacy", ParameterValues.Empty, new EffectBinding.Project(), EffectCapabilities.Transforms);
    }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static TimelineFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects, Func<ILineGeometryProvider> geometry)
        => new(effects, Fingerprint, geometry, ReplayReason.Initial);
    private static ProjectSnapshot Snapshot()
    {
        var lines = Enumerable.Range(0, 2).Select(index => LyricLine.Create("line-" + index, "AB", new(index * 1_000_000, (index + 1) * 1_000_000),
            TimingStatus.Edited, [LyricToken.Create("word-" + index, "AB", new(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Edited)]));
        var project = Project.Create("mixed pure legacy", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
            LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { EnterAnimation = null }) with { EffectInputsFingerprint = Fingerprint };
    }
    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer = new();
        using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        return pixels.PixelMemory.ToArray();
    }
    private sealed class CountingGeometry : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _renderer = new();
        internal List<long> Calls { get; } = [];
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { Calls.Add(timeUs); return _renderer.GetSceneFrame(snapshot, timeUs, editingLineId); }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _renderer.GetLineGeometry(snapshot, lineId);
        public void Dispose() => _renderer.Dispose();
    }
}
