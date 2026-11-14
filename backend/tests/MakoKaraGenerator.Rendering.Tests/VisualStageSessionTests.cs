using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VisualStageSessionTests
{
    [Fact]
    public async Task 纯视觉阶段按给定时刻求值且重绘与局部时刻完全确定()
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = VisualTestFixture.Target(snapshot);
        await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition()]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        var dot = Assert.Single(Assert.Single(frame.DotBatches).Dots); Assert.Equal(38.001, dot.Position.X, 10);
        var redraw = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        Assert.Equal(frame.DotBatches[0].Dots.ToArray(), redraw.DotBatches[0].Dots.ToArray());
        var fraction = await session.PrepareAsync(18_500, Fx.OutputMode.Transparent, target);
        Assert.Equal(38.5, fraction.DotBatches[0].Dots[0].Position.X);
    }
    [Fact]
    public async Task 多帧率中段直接请求回退与独立会话得到同一视觉状态和像素()
    {
        var snapshot = VisualTestFixture.Snapshot(); var effects = await VisualTestFixture.Definition(); var target = VisualTestFixture.Target(snapshot);
        using SkiaRenderPipeline renderer = new(); byte[]? expected = null;
        foreach (int fps in new[] { 24, 30, 60 })
        {
            await using var session = await VisualTestFixture.Pure([effects]).CreateAsync(snapshot);
            for (int index = 0; index * 1_000_000L / fps < 75_001; index++)
                await session.PrepareAsync(index * 1_000_000L / fps, Fx.OutputMode.Transparent, target);
            var frame = await session.PrepareAsync(75_001, Fx.OutputMode.Transparent, target);
            using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
            expected ??= pixels.PixelMemory.ToArray(); Assert.Equal(expected, pixels.PixelMemory.ToArray());
            await session.PrepareAsync(5_001, Fx.OutputMode.Transparent, target);
            var rewind = await session.PrepareAsync(75_001, Fx.OutputMode.Transparent, target);
            using var after = renderer.Render(RenderRequest.At(snapshot, rewind.TimeUs) with { PreparedFrame = rewind, TransparentBackground = true });
            Assert.Equal(expected, after.PixelMemory.ToArray());
        }
        await using var direct = await VisualTestFixture.Pure([effects]).CreateAsync(snapshot);
        var middle = await direct.PrepareAsync(75_001, Fx.OutputMode.Transparent, target);
        using var directPixels = renderer.Render(RenderRequest.At(snapshot, middle.TimeUs) with { PreparedFrame = middle, TransparentBackground = true });
        Assert.Equal(expected, directPixels.PixelMemory.ToArray());
    }
    [Fact]
    public async Task 视觉阶段读取最终族注音矩阵和绑定内全部真实播放头()
    {
        var snapshot = VisualTestFixture.Snapshot();
        await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition("geometry", binding: new Fx.EffectBinding.Tokens([new("a")]))]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(5_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        var cursor = Assert.Single(frame.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
        Assert.Equal(cursor.Position, Assert.Single(Assert.Single(frame.DotBatches).Dots).Position);
        var baseCursor = Assert.Single(PlaybackProjection.Create(snapshot, frame.Scene, frame.Scene.TokenGeometries).GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
        Assert.NotEqual(baseCursor.Position, cursor.Position);
        var outside = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        Assert.Empty(Assert.Single(outside.DotBatches).Dots); Assert.Single(outside.Playback.Cursors);
    }
    [Fact]
    public async Task 无逐字时间仍提供真实行依据视觉播放头而不制造源元素()
    {
        var snapshot = VisualTestFixture.Snapshot(); var branch = snapshot.Lyrics.PrimaryBranch!; var line = branch.Lines[0];
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line with { Tokens = [], RubyAnnotations = [] }) }) };
        await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition("geometry", binding: new Fx.EffectBinding.Lines([new("row")]))]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        var cursor = Assert.Single(frame.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
        Assert.Equal(Fx.ProgressSource.Line, cursor.ProgressSource); Assert.Null(cursor.TokenId); Assert.Empty(frame.FinalGeometry);
        Assert.Equal(cursor.Position, Assert.Single(Assert.Single(frame.DotBatches).Dots).Position);
    }
    [Fact]
    public async Task 未提供字形的后端在视觉阶段仍明确不可用而非空光标()
    {
        var snapshot = VisualTestFixture.Snapshot(); var definition = await VisualTestFixture.Definition(entry: "UnavailableProbe");
        PureFrameSourceFactory factory = new([definition], VisualTestFixture.Fingerprint, static () => new NoGlyphGeometry());
        await using var session = await factory.CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        Assert.False(frame.Playback.IsAvailable); Assert.Empty(frame.DotBatches);
        Assert.Equal("PlaybackGeometryUnavailable", Assert.Throws<Fx.EffectOutputException>(() => frame.Playback.GetCursors(Fx.CursorAnchorMode.Baseline)).Code);
        var failDef = await VisualTestFixture.Definition("call-cursors", entry: "UnavailableProbe");
        PureFrameSourceFactory failFactory = new([failDef], VisualTestFixture.Fingerprint, static () => new NoGlyphGeometry());
        await using var failSession = await failFactory.CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () =>
            await failSession.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot)));
        Assert.Equal("PlaybackGeometryUnavailable", error.Code);
        Assert.Equal("EvaluateVisuals", error.Stage);
    }
    private sealed class NoGlyphGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => LyricSceneSampler.SampleFrame(snapshot, timeUs);
    }
    [Theory]
    [InlineData("Evaluate")] [InlineData("EvaluateVisuals")]
    public async Task 纯视觉阶段异常失败且取消保留取消语义(string stage)
    {
        foreach (string prefix in new[] { "Throw", "Cancel" })
        {
            var snapshot = VisualTestFixture.Snapshot();
            if (prefix == "Cancel")
            {
                using CancellationTokenSource cts = new();
                cts.Cancel();
                await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition("normal")]).CreateAsync(snapshot);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), cts.Token));
            }
            else
            {
                await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition(prefix + stage)]).CreateAsync(snapshot);
                var error = await Assert.ThrowsAsync<EffectFailureException>(async () =>
                    await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot)));
                Assert.Equal(stage, error.Stage);
                Assert.Equal("ScriptExecutionFailed", error.Code);
            }
        }
    }
    [Fact]
    public async Task 独立纯会话在目标端点时刻返回最终几何与播放头()
    {
        var snapshot = VisualTestFixture.Snapshot(); var definition = await VisualTestFixture.Definition("normal");
        using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 5_001);
        var identity = Assert.Single(PlaybackProjection.Create(snapshot, scene, scene.TokenGeometries).Cursors).Identity;
        await using var session = await VisualTestFixture.Pure([definition]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(identity.TimeRange.StartUs, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        var cursor = Assert.Single(frame.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
        Assert.Equal(identity, cursor.Identity);
        Assert.Equal(0, cursor.Progress);
    }
}
