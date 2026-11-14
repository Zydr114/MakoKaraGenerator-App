using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GeometryQuerySessionTests
{
    private const string Fingerprint = "R2-geometry-query-fixed-inputs";
    private static readonly Lazy<Task<CompiledEffectPackage>> Probe = new(() => V2RenderPackageHelper.CompileControllerAsync("""
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed record GeometryState(double FutureScale) : PreparedEffect;

        public sealed class GeometryProbe : IPureKaraokeEffect<GeometryState>
        {
            public GeometryState Declare(DeclareContext source)
            {
                string mode = source.Parameters.GetEnum("mode");
                var all = source.Indexes.Layout.GetBaseAt(35000, new SourceFilter.Project());
                if (mode == "query")
                {
                    var future = source.Indexes.Layout.GetBaseAt(35000, new SourceFilter.Tokens([new TokenId("second-token")]));
                    if (future.Lines.Length != 1 || future.Lines[0].LineId.Value != "second" || future.LineGeometry.Length != 1)
                        throw new InvalidOperationException("Query must preserve real source filtering outside output binding");
                    return new GeometryState(future.Tokens[0].BaseGeometry.BaseWorldMatrix.A);
                }
                return new GeometryState(all.Tokens.Length);
            }
            public void Evaluate(in TextFrame frame, GeometryState state, TextOutput output)
            {
                string mode = frame.Parameters.GetEnum("mode");
                if (mode == "invalid") frame.Indexes.Layout.GetBaseAt(35000, new SourceFilter.Lines([new LineId("does-not-exist")]));
                if (mode == "query")
                {
                    var future = frame.Indexes.Layout.GetBaseAt(35000, new SourceFilter.Tokens([new TokenId("second-token")]));
                    if (future.Lines.Length != 1 || future.Lines[0].LineId.Value != "second" || future.LineGeometry.Length != 1)
                        throw new InvalidOperationException("Query must preserve real source filtering outside output binding");
                    foreach (var token in frame.Presentations.TokensInBinding)
                        if (token.Performance.IsCurrent)
                            output.AppendTransform(token.WithRuby, new Affine2(state.FutureScale, 0, 0, 1.1, 0, 0));
                    return;
                }
                if (mode == "error" && frame.TimeUs >= 30_000)
                    throw new InvalidOperationException("Cannot resolve invalid future appearance");
                foreach (var token in frame.Presentations.TokensInBinding)
                    if (token.Performance.IsCurrent)
                        output.AppendTransform(token.WithRuby, new Affine2(1.1, 0, 0, 1.1, 0, 0));
            }
        }
        """,
        [Fx.V2.CapabilityNames.TokenAffine],
        entryType: "GeometryProbe",
        exportId: "geometry-probe",
        parameters: new System.Text.Json.Nodes.JsonArray(
            V2RenderPackageHelper.EnumParameter("mode", "normal", "normal", "query", "invalid", "error")),
        outputs: [V2RenderPackageHelper.Output("token", "VisualTransform", "tokenFamily", Fx.V2.CapabilityNames.TokenAffine)],
        packageId: "demo.geometry.query.v2"));
    private static ProjectSnapshot Snapshot()
    {
        var first = LyricLine.Create("first", "歌", new(10_000, 20_000), TimingStatus.Imported,
            [LyricToken.Create("first-token", "歌", new(10_000, 20_000), TimingStatus.Imported)]);
        var second = LyricLine.Create("second", "歌", new(30_000, 40_000), TimingStatus.Imported,
            [LyricToken.Create("second-token", "歌", new(30_000, 40_000), TimingStatus.Imported)]);
        return new() { Name = "独立未来查询", Lyrics = LyricsDocument.Create([first, second]), EffectInputsFingerprint = Fingerprint,
            Composition = new() { CanvasWidth = 640, CanvasHeight = 360 },
            LyricStyle = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48, Stroke = null, Shadow = null } };
    }
    private static async Task<ImmutableArray<CompiledFrameEffect>> Effects(string mode = "normal", Fx.EffectBinding? binding = null)
    {
        var package = await Probe.Value;
        return [package.CreateEffect("geometry-probe", new("probe"),
            binding ?? new Fx.EffectBinding.Project(), 0) with
        {
            Parameters = new Fx.ParameterValues(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode) }),
            Subscription = new Fx.SourceSubscription.Project(),
        }];
    }
    private static PureFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline());

    [Fact]
    public async Task 作者阶段查询未来基础布局不受输出绑定限制并与独立几何一致()
    {
        var snapshot = Snapshot(); var effects = await Effects("query", new Fx.EffectBinding.Tokens([new("first-token")]));
        await using var session = await Factory(effects).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var transform = Assert.Single(frame.TokenTransforms);
        Assert.Equal(1.1, transform.LocalMatrix.D, 12);
        using SkiaRenderPipeline geometry = new();
        var futureBasis = geometry.GetSceneFrame(snapshot, 35_000).TokenGeometries.Single().BaseGeometry.BaseWorldMatrix.A;
        Assert.Equal(futureBasis, transform.LocalMatrix.A, 12);
    }
    [Fact]
    public async Task 非法源查询明确失败()
    {
        var snapshot = Snapshot(); var factory = Factory(await Effects("invalid"));
        await using var session = await factory.CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(15_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("ScriptExecutionFailed", failure.Code); Assert.Equal("Evaluate", failure.Stage);
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 未来端点独立执行缩放不污染主会话且空档端点遵守半开时间(int fps)
    {
        var snapshot = Snapshot(); snapshot = snapshot with { Composition = snapshot.Composition with { FrameRate = new(fps, 1) } };
        var effects = await Effects(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var before = await main.PrepareAsync(15_000, Fx.OutputMode.Transparent, target);
        using SkiaRenderPipeline geometry = new();
        var future = PlaybackProjection.Create(snapshot, geometry.GetSceneFrame(snapshot, 30_000), geometry.GetSceneFrame(snapshot, 30_000).TokenGeometries);
        var identity = Assert.Single(future.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)).Identity;
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var endpoint = await query.ResolveAsync(identity, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
        Assert.True(endpoint.IsAvailable); Assert.Equal(30_000, endpoint.TimeUs); Assert.Equal(0, endpoint.Cursor!.Progress);
        await using var export = await Factory(effects).CreateAsync(snapshot);
        for (long time = 0; time < 30_000; time += 1_000_000 / fps) await export.PrepareAsync(time, Fx.OutputMode.Transparent, target);
        var reference = await export.PrepareAsync(30_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(Assert.Single(reference.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)), endpoint.Cursor);
        var after = await main.PrepareAsync(16_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(before.TokenTransforms.ToArray(), after.TokenTransforms.ToArray());
        var previous = await query.ResolveAsync(Assert.Single(before.Playback.Cursors).Identity, Fx.CursorEndpointKind.PreviousEndLimit,
            Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target);
        Assert.True(previous.IsAvailable); Assert.Equal(19_999, previous.TimeUs); Assert.Equal(.9999, previous.Cursor!.Progress);
        var missing = await query.ResolveAsync(new(identity.BranchId, identity.LineId, new("absent-binding"), identity.TimeRange,
            identity.ProgressSource, identity.TokenId, identity.OccurrenceId), Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target);
        Assert.False(missing.IsAvailable); Assert.Null(missing.Cursor);
    }
    [Fact]
    public async Task 未来异常取消和错误源均不回退为伪造端点且主状态仍可用()
    {
        var snapshot = Snapshot(); var effects = await Effects("error"); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var frame = await main.PrepareAsync(15_000, Fx.OutputMode.Transparent, target);
        var token = ContentProjection.Create(snapshot).GetToken(new("second-token")); var range = token.TimeRange!.Value;
        Fx.CursorIdentity identity = new(token.BranchId, token.LineId, new("binding"), range, Fx.ProgressSource.Token, token.Id,
            Fx.StableIdentities.Occurrence(token.BranchId, token.LineId, token.Id, range));
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        Assert.Equal("ScriptExecutionFailed", (await Assert.ThrowsAsync<EffectFailureException>(async () => await query.ResolveAsync(identity,
            Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target))).Code);
        Assert.Single((await main.PrepareAsync(16_000, Fx.OutputMode.Transparent, target)).TokenTransforms);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.ResolveAsync(identity, Fx.CursorEndpointKind.NextStart,
            Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target, canceled.Token));
        var wrong = new Fx.CursorIdentity(identity.BranchId, identity.LineId, identity.BindingId, new(30_001, 40_000), Fx.ProgressSource.Token,
            identity.TokenId, identity.OccurrenceId);
        Assert.Equal("InvalidCursorSource", (await Assert.ThrowsAsync<Fx.EffectOutputException>(async () => await query.ResolveAsync(wrong,
            Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target))).Code);
        Assert.Single(frame.Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
    }
}
