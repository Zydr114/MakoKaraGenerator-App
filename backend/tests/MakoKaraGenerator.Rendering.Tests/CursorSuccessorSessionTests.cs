using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.TestSupport;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class CursorSuccessorSessionTests
{
    private const string Fingerprint = "R6-successor-shared-events-final-geometry";
    private static readonly Lazy<Task<(ScriptCompilationResult Artifact, EffectPackageDefinition Package)>> Artifact = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        const string source = """
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class SuccessorProbe : EffectBase
            {
                private int starts, transitions;
                private string reason = "none";
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (evt is TokenStarted) starts++;
                    if (evt is CursorTransitionEvent) transitions++;
                    reason = evt.ReplayReason.ToString();
                }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (starts != frame.History.GetStarts(new TimeRange(0, 80000), new SourceFilter.Project()).Length
                        || transitions != frame.History.Records.Count(record => record.Event is CursorTransitionEvent))
                        throw new System.Exception("Callbacks must equal the real processed history");
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "successor-count", starts + "/" + transitions + ":" + reason);
                    if (starts >= 2 && frame.Parameters.GetEnum("mode") == "error")
                        frame.Diagnostics.Report(DiagnosticSeverity.Error, "FutureSuccessorError", "future appearance failed");
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1 + starts * .1 + transitions * .3, Pivot.BaseAdvanceCenter);
                }
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
                    => frame.Diagnostics.Report(DiagnosticSeverity.Info, "emit-probe", "main only");
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                    => frame.Diagnostics.Report(DiagnosticSeverity.Info, "visual-probe", "main only");
            }
            public sealed class SecondTransform : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1.4, Pivot.BaseAdvanceCenter);
                }
            }
            """;
        var files = new Dictionary<string, byte[]> { ["src/SuccessorProbe.cs"] = Encoding.UTF8.GetBytes(source) };
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["parameters"] = new JsonArray(new JsonObject { ["id"] = "mode", ["type"] = "enum", ["default"] = "normal",
            ["label"] = "Test mode", ["values"] = new JsonArray(new JsonObject { ["value"] = "normal", ["label"] = "Normal" },
                new JsonObject { ["value"] = "error", ["label"] = "Error" }) });
        var declaration = manifest["exports"]![0]!;
        declaration["id"] = "state"; declaration["entryType"] = "SuccessorProbe";
        declaration["parameters"] = new JsonArray("mode"); declaration["bindingKinds"] = new JsonArray("project");
        var second = declaration.DeepClone(); second["id"] = "later"; second["entryType"] = "SecondTransform"; second["parameters"] = new JsonArray();
        manifest["exports"]!.AsArray().Add(second);
        var package = EffectPackageDefinition.Read(EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile));
        var result = await compiler.CompileAsync([new("src/SuccessorProbe.cs", source)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return (result, package);
    });
    private static ProjectSnapshot Snapshot(bool concurrent = false)
    {
        LyricLine Row(string id, long start, long end) => LyricLine.Create(id, "田", new(start, end), TimingStatus.Edited,
            [LyricToken.Create(id + "-token", "田", new(start, end), TimingStatus.Edited)]);
        var lyrics = LyricsDocument.Create(concurrent ? [Row("first", 10000, 20000), Row("second", 30000, 40000), Row("third", 30000, 45000)]
            : [Row("first", 10000, 20000), Row("second", 30000, 40000), Row("third", 50000, 60000)]);
        var project = Project.Create("最终后继", Composition.Create(640, 360, new(30, 1), outputDurationUs: 80000), lyrics,
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48, Stroke = null, Shadow = null });
        if (concurrent)
            project = PresentationEditor.SetBranch(project, new() { BranchId = "primary", SlotSequenceProfile = SlotSequenceAnimationProfile.Default with
                { EnterDurationUs = 0, ExitDurationUs = 0, RevealMode = SlotRevealMode.LeadBeforeNextStart } });
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    }
    private static async Task<ImmutableArray<CompiledFrameEffect>> Effects(string mode = "normal") =>
        [new(new("state"), (await Artifact.Value).Artifact, "SuccessorProbe", new(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode) }),
            new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms | Fx.EffectCapabilities.Overlay)
            { PackageDefinition = (await Artifact.Value).Package, PackageDeclaration = (await Artifact.Value).Package.GetController("state") },
         new(new("later-transform"), (await Artifact.Value).Artifact, "SecondTransform", new(new Dictionary<string, Fx.ParameterValue>()),
            new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms)
            { PackageDefinition = (await Artifact.Value).Package, PackageDeclaration = (await Artifact.Value).Package.GetController("later") }];
    private static TimelineFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects, Fx.ReplayReason reason = Fx.ReplayReason.Initial)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline(), reason);
    private static string Counter(PreparedRenderFrame frame) => frame.Diagnostics.Last(item => item.Diagnostic.Code == "successor-count").Diagnostic.Message;
    private static byte[] Pixels(PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer = new();
        using var result = renderer.Render(RenderRequest.At(frame.Snapshot, frame.TimeUs) with { PreparedFrame = frame, Target = frame.Target,
            TransparentBackground = frame.OutputMode == Fx.OutputMode.Transparent });
        return result.PixelMemory.ToArray();
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 后继真实callback与多实例最终缩放在独立查询导出和重绘中一致(int fps)
    {
        var snapshot = Snapshot(); snapshot = snapshot with { Composition = snapshot.Composition with { FrameRate = new(fps, 1) } };
        var effects = await Effects(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var before = await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target);
        var from = Assert.Single(before.Playback.Cursors).Identity;
        Assert.Equal("1/0:Initial", Counter(before)); var beforePixels = Pixels(before);
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var resolved = Assert.Single(await query.ResolveSuccessorsAsync(from, new Fx.EffectBinding.Project(), Fx.CursorAnchorMode.AdvanceCenter,
            Fx.OutputMode.Transparent, target));
        Assert.True(resolved.IsAvailable); Assert.Equal(19999, resolved.PreviousEnd.TimeUs); Assert.Equal(30000, resolved.NextStart.TimeUs);
        Assert.Equal(.9999, resolved.PreviousEnd.Cursor!.Progress); Assert.Equal(0, resolved.NextStart.Cursor!.Progress);
        Assert.Equal("2/1:AnchorQuery", resolved.NextStart.Diagnostics.Last(item => item.Diagnostic.Code == "successor-count").Diagnostic.Message);
        Assert.DoesNotContain(resolved.NextStart.Diagnostics, item => item.Diagnostic.Code is "emit-probe" or "visual-probe");
        Assert.Empty(await query.ResolveSuccessorsAsync(from, new Fx.EffectBinding.Lines([new("first"), new("third")]),
            Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target));
        var redraw = await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target);
        Assert.Equal("1/0:Initial", Counter(redraw)); Assert.Equal(beforePixels, Pixels(redraw));
        await using var export = await Factory(effects, Fx.ReplayReason.Export).CreateAsync(snapshot);
        var previous = await export.PrepareAsync(19999, Fx.OutputMode.Transparent, target);
        var future = await export.PrepareAsync(30000, Fx.OutputMode.Transparent, target);
        Assert.Equal(Assert.Single(previous.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)), resolved.PreviousEnd.Cursor);
        Assert.Equal(Assert.Single(future.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)), resolved.NextStart.Cursor);
        Assert.Equal("2/1:Export", Counter(future));
        var mainFuture = await main.PrepareAsync(30000, Fx.OutputMode.Transparent, target);
        Assert.Equal(Pixels(future), Pixels(mainFuture));
        var rewound = await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target);
        Assert.Equal("1/0:Seek", Counter(rewound)); Assert.Equal(beforePixels, Pixels(rewound));
        Assert.Contains(mainFuture.Diagnostics, item => item.Diagnostic.Code == "emit-probe");
    }
    [Fact]
    public async Task 同时起唱的后继集合和独立端点不选第一条且主历史保持原值()
    {
        var snapshot = Snapshot(true); var effects = await Effects(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var before = await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target);
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var resolved = await query.ResolveSuccessorsAsync(Assert.Single(before.Playback.Cursors).Identity, new Fx.EffectBinding.Project(),
            Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target);
        Assert.Equal(["second", "third"], resolved.Select(item => item.NextStart.Identity.LineId.Value));
        Assert.All(resolved, item => Assert.True(item.IsAvailable));
        var future = await main.PrepareAsync(30000, Fx.OutputMode.Transparent, target);
        Assert.Equal("3/2:Initial", Counter(future)); Assert.Equal(2, future.Playback.Cursors.Length);
        Assert.Equal(future.Playback.GetCursors(Fx.CursorAnchorMode.Baseline), resolved.Select(item => item.NextStart.Cursor));
        Assert.Equal("1/0:Seek", Counter(await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target)));
    }
    [Fact]
    public async Task 输出模式密度切换与后继查询均消费当前目标而不复用旧几何()
    {
        var snapshot = Snapshot(); var effects = await Effects(); var ordinary = RenderTarget.FromComposition(snapshot.Composition);
        var dense = new RenderTarget { PixelWidth = 1280, PixelHeight = 720 };
        var from = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial).CursorTransitions.First().From;
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        await using var main = await Factory(effects).CreateAsync(snapshot);
        foreach (var request in new[] { (Fx.OutputMode.Opaque, ordinary), (Fx.OutputMode.Transparent, dense), (Fx.OutputMode.Transparent, ordinary) })
        {
            var resolved = Assert.Single(await query.ResolveSuccessorsAsync(from, new Fx.EffectBinding.Project(), Fx.CursorAnchorMode.AdvanceCenter,
                request.Item1, request.Item2));
            var frame = await main.PrepareAsync(30000, request.Item1, request.Item2);
            Assert.Equal(request.Item1, frame.OutputMode); Assert.Equal(request.Item2, frame.Target);
            Assert.Equal(Assert.Single(frame.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)), resolved.NextStart.Cursor);
        }
    }
    [Fact]
    public async Task 未来作者Error与取消保留真实来源并不破坏主会话()
    {
        var snapshot = Snapshot(); var effects = await Effects("error"); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var before = await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target); var from = Assert.Single(before.Playback.Cursors).Identity;
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await query.ResolveSuccessorsAsync(from,
            new Fx.EffectBinding.Project(), Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target));
        Assert.Equal("FutureSuccessorError", failure.Code); Assert.Equal("Evaluate", failure.Stage); Assert.Equal(30000, failure.TimeUs);
        Assert.Equal(effects[0].Id, failure.InstanceId); Assert.Equal(effects[0].PackageIdentity, failure.PackageIdentity);
        Assert.Equal("1/0:Initial", Counter(await main.PrepareAsync(16000, Fx.OutputMode.Transparent, target)));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.ResolveSuccessorsAsync(from, new Fx.EffectBinding.Project(),
            Fx.CursorAnchorMode.Baseline, Fx.OutputMode.Transparent, target, canceled.Token));
        Assert.Equal("1/0:Initial", Counter(await main.PrepareAsync(16000, Fx.OutputMode.Transparent, target)));
    }
    [Theory]
    [InlineData("cancel")] [InlineData("error")]
    public async Task 未来查询中途失败释放两个独立真实后端且当前会话可继续(string mode)
    {
        var snapshot = Snapshot(); var effects = await Effects(mode == "error" ? "error" : "normal");
        var target = RenderTarget.FromComposition(snapshot.Composition); using CancellationTokenSource cancellation = new();
        await using var main = await Factory(effects).CreateAsync(snapshot);
        var before = await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target);
        List<TrackedGeometry> owned = [];
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, () =>
        {
            TrackedGeometry geometry = new(time => { if (mode == "cancel" && time >= 30000) cancellation.Cancel(); });
            owned.Add(geometry); return geometry;
        });
        async Task Resolve() => await query.ResolveSuccessorsAsync(Assert.Single(before.Playback.Cursors).Identity,
            new Fx.EffectBinding.Project(), Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target, cancellation.Token);
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Resolve);
        else Assert.Equal("FutureSuccessorError", (await Assert.ThrowsAsync<EffectFailureException>(Resolve)).Code);
        Assert.Equal(2, owned.Count); Assert.All(owned, geometry => Assert.True(geometry.Disposed));
        Assert.Equal(Pixels(before), Pixels(await main.PrepareAsync(15000, Fx.OutputMode.Transparent, target)));
        Assert.Equal("1/0:Initial", Counter(await main.PrepareAsync(16000, Fx.OutputMode.Transparent, target)));
    }
    private sealed class TrackedGeometry(Action<long> before) : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _renderer = new();
        public bool Disposed { get; private set; }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _renderer.GetLineGeometry(snapshot, lineId);
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { before(timeUs); return _renderer.GetSceneFrame(snapshot, timeUs, editingLineId); }
        public void Dispose() { if (Disposed) return; Disposed = true; _renderer.Dispose(); }
    }
}
