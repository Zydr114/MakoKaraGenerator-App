using System.Collections.Immutable;
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
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/GeometryProbe.cs", """
            using System;
            using System.Linq;
            using System.Globalization;
            using MakoKaraGenerator.Scripting;
            public sealed class GeometryProbe : EffectBase
            {
                private int starts;
                private string reason = "none", mode = "";
                private GeometryQueries initialized = null!;
                public override void Initialize(EffectInitializationContext context)
                {
                    mode = context.Parameters.GetEnum("mode"); initialized = context.GeometryQueries;
                    context.GeometryQueries.GetBaseAt(35000, new SourceFilter.Project());
                }
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (evt is TokenStarted) { starts++; reason = evt.ReplayReason.ToString(); }
                    if (mode == "query") context.GeometryQueries.GetBaseAt(35000, new SourceFilter.Project());
                }
                public override void Advance(SimulationStep interval, StateContext context)
                {
                    if (mode == "query") context.GeometryQueries.GetBaseAt(35000, new SourceFilter.Tokens([new("second-token")]));
                }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (mode == "invalid") frame.GeometryQueries.GetBaseAt(35000, new SourceFilter.Lines([new("does-not-exist")]));
                    if (mode == "expired") initialized.GetBaseAt(35000, new SourceFilter.Project());
                    if (mode == "query")
                    {
                        var future = frame.GeometryQueries.GetBaseAt(35000, new SourceFilter.Tokens([new("second-token")]));
                        if (future.Lines.Length != 1 || future.Lines[0].LineId.Value != "second" || future.LineGeometry.Length != 1)
                            throw new Exception("Query must preserve real source filtering outside output binding");
                        frame.Diagnostics.Report(DiagnosticSeverity.Info, "base", future.Tokens.Single().BaseGeometry.BaseWorldMatrix.A.ToString(CultureInfo.InvariantCulture));
                    }
                    if (starts != frame.History.GetStarts(new TimeRange(0, 100000), new SourceFilter.Project()).Length)
                        throw new Exception("Base query must not dispatch future callbacks");
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "counter", starts + ":" + reason);
                    if (mode == "error" && starts >= 2) frame.Diagnostics.Report(DiagnosticSeverity.Error, "FutureError", "Cannot resolve invalid future appearance");
                    foreach (var token in frame.Presentations.TokensInBinding.Where(token => token.Performance.IsCurrent))
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1 + starts * .1, Pivot.BaseAdvanceCenter);
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
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
        => [new(new("probe"), await Probe.Value, "GeometryProbe",
            new(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode) }),
            binding ?? new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms) { Subscription = new Fx.SourceSubscription.Project() }];
    private static TimelineFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects, Fx.ReplayReason reason = Fx.ReplayReason.Initial)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), reason);
    private static string Counter(PreparedRenderFrame frame) => frame.Diagnostics.Last(item => item.Diagnostic.Code == "counter").Diagnostic.Message;

    [Fact]
    public async Task 作者四个阶段查询未来基础布局不会推进共享历史且不受输出绑定限制()
    {
        var snapshot = Snapshot(); var effects = await Effects("query", new Fx.EffectBinding.Tokens([new("first-token")]));
        await using var session = await Factory(effects).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal("1:Initial", Counter(frame));
        Assert.Equal(1.1, Assert.Single(frame.TokenTransforms).LocalMatrix.A, 12);
        using SkiaRenderPipeline geometry = new();
        var futureBasis = geometry.GetSceneFrame(snapshot, 35_000).TokenGeometries.Single().BaseGeometry.BaseWorldMatrix.A;
        Assert.Equal(futureBasis.ToString(System.Globalization.CultureInfo.InvariantCulture), frame.Diagnostics.Last(item => item.Diagnostic.Code == "base").Diagnostic.Message);
    }
    [Theory]
    [InlineData("expired", "InvalidGeometryQueryStage")]
    [InlineData("invalid", "ScriptExecutionFailed")]
    public async Task 查询阶段泄漏或非法源明确失败(string mode, string code)
    {
        var snapshot = Snapshot(); var factory = Factory(await Effects(mode));
        await using var session = await factory.CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(15_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal(code, failure.Code); Assert.Equal("Evaluate", failure.Stage);
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 未来端点独立执行真实callback和缩放不污染主会话且空档端点遵守半开时间(int fps)
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
        Assert.Equal("2:AnchorQuery", endpoint.Diagnostics.Last(item => item.Diagnostic.Code == "counter").Diagnostic.Message);
        await using var export = await Factory(effects, Fx.ReplayReason.Export).CreateAsync(snapshot);
        for (long time = 0; time < 30_000; time += 1_000_000 / fps) await export.PrepareAsync(time, Fx.OutputMode.Transparent, target);
        var reference = await export.PrepareAsync(30_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(Assert.Single(reference.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)), endpoint.Cursor);
        var after = await main.PrepareAsync(16_000, Fx.OutputMode.Transparent, target);
        Assert.Equal("1:Initial", Counter(after)); Assert.Equal(before.TokenTransforms.ToArray(), after.TokenTransforms.ToArray());
        var previous = await query.ResolveAsync(Assert.Single(before.Playback.Cursors).Identity, Fx.CursorEndpointKind.PreviousEndLimit,
            Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target);
        Assert.True(previous.IsAvailable); Assert.Equal(19_999, previous.TimeUs); Assert.Equal(.9999, previous.Cursor!.Progress);
        var missing = await query.ResolveAsync(new(identity.BranchId, identity.LineId, new("absent-binding"), identity.TimeRange,
            identity.ProgressSource, identity.TokenId, identity.OccurrenceId), Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target);
        Assert.False(missing.IsAvailable); Assert.Null(missing.Cursor);
    }
    [Fact]
    public async Task 未来Error取消和错误源均不回退为伪造端点且主状态仍可用()
    {
        var snapshot = Snapshot(); var effects = await Effects("error"); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var frame = await main.PrepareAsync(15_000, Fx.OutputMode.Transparent, target);
        var token = ContentProjection.Create(snapshot).GetToken(new("second-token")); var range = token.TimeRange!.Value;
        Fx.CursorIdentity identity = new(token.BranchId, token.LineId, new("binding"), range, Fx.ProgressSource.Token, token.Id,
            Fx.StableIdentities.Occurrence(token.BranchId, token.LineId, token.Id, range));
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        Assert.Equal("FutureError", (await Assert.ThrowsAsync<EffectFailureException>(async () => await query.ResolveAsync(identity,
            Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target))).Code);
        Assert.Equal("1:Initial", Counter(await main.PrepareAsync(16_000, Fx.OutputMode.Transparent, target)));
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
