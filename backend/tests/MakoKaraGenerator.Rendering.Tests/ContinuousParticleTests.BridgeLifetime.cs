using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ContinuousParticleTests
{
    [Theory]
    [InlineData("normal")] [InlineData("cancel")] [InlineData("error")]
    public async Task 桥接端点只缓存固定值独立后端关闭且失败或换目标重新解析(string mode)
    {
        var snapshot = Snapshot(twoRows: true); var effects = new[] { await BridgeState(mode == "error"), await Effect(transition: "bridge", bridgeUs: 45001) };
        using CancellationTokenSource cancellation = new(); bool cancelFuture = mode == "cancel";
        List<BridgeGeometry> owned = [];
        TimelineFrameSourceFactory factory = new([.. effects], Fingerprint, () =>
        {
            BridgeGeometry geometry = new(time => { if (cancelFuture && time >= 500000) cancellation.Cancel(); });
            owned.Add(geometry); return geometry;
        }, Fx.ReplayReason.Initial);
        await using (var source = await factory.CreateAsync(snapshot))
        {
            await Frame(source, snapshot, 400000); Assert.Single(owned);
            async Task Future() => await source.PrepareAsync(460000, Fx.OutputMode.Transparent,
                RenderTarget.FromComposition(snapshot.Composition), cancellation.Token);
            if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Future);
            else if (mode == "error") Assert.Equal("FutureBridgeError", (await Assert.ThrowsAsync<EffectFailureException>(Future)).Code);
            else await Future();
            Assert.Equal(3, owned.Count); Assert.False(owned[0].Disposed); Assert.All(owned.Skip(1), geometry => Assert.True(geometry.Disposed));
            if (mode == "normal")
            {
                var previous = await Frame(source, snapshot, 499999); Assert.Equal(3, owned.Count);
                var dense = await Frame(source, snapshot, 499999, target: new() { PixelWidth = 640, PixelHeight = 360 });
                Assert.Equal(5, owned.Count); Assert.Equal(Values(previous), Values(dense));
                await Frame(source, snapshot, 400000); await Frame(source, snapshot, 499999); Assert.Equal(7, owned.Count);
            }
            else
            {
                cancelFuture = false; await Frame(source, snapshot, 400000); Assert.Equal(3, owned.Count);
                if (mode == "cancel") { Assert.NotEmpty((await Frame(source, snapshot, 499999)).Particles); Assert.Equal(5, owned.Count); }
                else { await Assert.ThrowsAsync<EffectFailureException>(() => Frame(source, snapshot, 499999)); Assert.Equal(5, owned.Count); }
            }
        }
        Assert.All(owned, geometry => Assert.True(geometry.Disposed));
    }
    [Fact]
    public void 桥接来源拒绝默认区间越过旧端及错误转换时刻()
    {
        var edges = TimelineProjection.Create(Snapshot(twoRows: true), Fx.ReplayReason.Initial).CursorTransitions;
        var edge = edges.Single(item => item.GapUs > 0);
        var source = new Fx.ParticleSource.Bridge(edge, new(454999, 500000));
        Assert.Equal(edge.From.BindingId, source.BindingId); Assert.Equal(edge.TrackId, source.TrackId);
        Assert.Throws<ArgumentException>(() => new Fx.ParticleSource.Bridge(edge, default));
        Assert.Throws<ArgumentException>(() => new Fx.ParticleSource.Bridge(edge, new(399999, 500000)));
        Assert.Throws<ArgumentException>(() => new Fx.ParticleSource.Bridge(edge, new(400000, 500001)));
        var wrong = new Fx.CursorTransitionEvent(edge.TrackId, edge.From, edge.To, 499999, edge.TransitionReason, edge.SourceOrder, edge.ReplayReason);
        Assert.Throws<ArgumentException>(() => new Fx.ParticleSource.Bridge(wrong, new(400000, 500000)));
        Assert.Throws<ArgumentException>(() => new Fx.ParticleSource.Bridge(edges.First(item => item.GapUs == 0), new(1,2)));
    }
    private sealed class BridgeGeometry(Action<long> before) : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _renderer = new();
        public bool Disposed { get; private set; }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _renderer.GetLineGeometry(snapshot, lineId);
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { before(timeUs); return _renderer.GetSceneFrame(snapshot, timeUs, editingLineId); }
        public void Dispose() { if (Disposed) return; Disposed = true; _renderer.Dispose(); }
    }
    [Fact]
    public async Task 真实三槽和提前前奏桥接只用原装载并保持目标起唱时间()
    {
        var basis = Snapshot(twoRows: true);
        var profile = SlotSequenceAnimationProfile.Default with { MaxVisibleLines = 3, EnterDurationUs = 0, ExitDurationUs = 0,
            Slots = [.. SlotSequenceAnimationProfile.Default.Slots, SlotSequenceSlot.Create("third", ScenePlacement.Create(new(.5,.5)))] };
        var project = PresentationEditor.SetBranch(Project.Create("三槽前奏桥接", basis.Composition, basis.Lyrics, lyricStyle: basis.LyricStyle),
            new() { BranchId = "primary", SlotSequenceProfile = profile });
        var program = project.LyricPresentation.FindProgram("primary")!;
        Assert.True(LeadInEditor.TryCreateCountdown(project, program.RootContainerId, "提示", 3, out string indicator, out project, out var report), report.ToSummary());
        Assert.True(LeadInEditor.TrySetLeadIn(project, program.Id, new() { Id = "second-cue", TargetLineId = "second", IndicatorContainerId = indicator, DurationUs = 100000 },
            out project, out report), report.ToSummary());
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var effects = new[] { await Effect(transition: "bridge", bridgeUs: 45001) };
        await using var source = await Factory([.. effects]).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 499999); Assert.Empty(frame.Playback.Cursors);
        var edge = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial).CursorTransitions.Single(item => item.GapUs > 0);
        Assert.All(frame.Particles.Where(p => p.Source is Fx.ParticleSource.Bridge), particle =>
        { var bridge = (Fx.ParticleSource.Bridge)particle.Source; Assert.Equal(edge.To.BindingId, bridge.To.BindingId); Assert.Equal(500000, bridge.To.TimeRange.StartUs); });
        Assert.Equal(2, frame.Particles.Count(p => p.Source is Fx.ParticleSource.Bridge));
        var started = await Frame(source, snapshot, 500000);
        Assert.Contains(started.Playback.Cursors, cursor => cursor.Identity == edge.To); Assert.Equal(2, started.Particles.Count(p => p.Source is Fx.ParticleSource.Bridge));
    }
}
