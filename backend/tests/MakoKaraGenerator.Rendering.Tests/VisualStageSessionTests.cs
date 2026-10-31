using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VisualStageSessionTests
{
    [Fact]
    public async Task 视觉推进在文字求值之后每个固定区间一次且分数重绘不推进()
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = VisualTestFixture.Target(snapshot);
        await using var session = await VisualTestFixture.Timeline([await VisualTestFixture.Definition()]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        Assert.Equal((string[])["0:1001:1", "1001:8333:1", "8333:10000:2", "10000:16666:2"],
            frame.Diagnostics.Where(item => item.Diagnostic.Code == "V").Select(item => item.Diagnostic.Message));
        Assert.Equal("18001:16666:16666:2:1335:Initial", frame.Diagnostics.Last(item => item.Diagnostic.Code == "P").Diagnostic.Message);
        var dot = Assert.Single(Assert.Single(frame.DotBatches).Dots); Assert.Equal(38.001, dot.Position.X, 10);
        var stages = frame.Diagnostics.Where(item => item.TimeUs == 10_000).Select(item => item.Stage).ToArray();
        Assert.Equal((string[])["Evaluate", "AdvanceVisuals", "EvaluateVisuals", "Advance"], stages);
        var redraw = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        Assert.Equal(frame.Diagnostics.ToArray(), redraw.Diagnostics.ToArray()); Assert.Equal(frame.DotBatches[0].Dots.ToArray(), redraw.DotBatches[0].Dots.ToArray());
        var fraction = await session.PrepareAsync(18_500, Fx.OutputMode.Transparent, target);
        Assert.Equal(4, fraction.Diagnostics.Count(item => item.Diagnostic.Code == "V"));
        Assert.Equal(38.5, fraction.DotBatches[0].Dots[0].Position.X);
    }
    [Fact]
    public async Task 多帧率中段直接请求回退与独立会话得到同一视觉状态和像素()
    {
        var snapshot = VisualTestFixture.Snapshot(); var effects = await VisualTestFixture.Definition(); var target = VisualTestFixture.Target(snapshot);
        using SkiaRenderPipeline renderer = new(); byte[]? expected = null;
        foreach (int fps in new[] { 24, 30, 60 })
        {
            await using var session = await VisualTestFixture.Timeline([effects], Fx.ReplayReason.Export).CreateAsync(snapshot);
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
        await using var direct = await VisualTestFixture.Timeline([effects], Fx.ReplayReason.Export).CreateAsync(snapshot);
        var middle = await direct.PrepareAsync(75_001, Fx.OutputMode.Transparent, target);
        using var directPixels = renderer.Render(RenderRequest.At(snapshot, middle.TimeUs) with { PreparedFrame = middle, TransparentBackground = true });
        Assert.Equal(expected, directPixels.PixelMemory.ToArray());
    }
    [Fact]
    public async Task 视觉阶段读取最终族注音矩阵和绑定内全部真实播放头()
    {
        var snapshot = VisualTestFixture.Snapshot();
        await using var session = await VisualTestFixture.Timeline([await VisualTestFixture.Definition("geometry", binding: new Fx.EffectBinding.Tokens([new("a")]))]).CreateAsync(snapshot);
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
        await using var session = await VisualTestFixture.Timeline([await VisualTestFixture.Definition("geometry", binding: new Fx.EffectBinding.Lines([new("row")]))]).CreateAsync(snapshot);
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
        Assert.Equal("PlaybackGeometryUnavailable", Assert.Single(frame.Diagnostics).Diagnostic.Code);
        Assert.Equal("EvaluateVisuals", frame.Diagnostics[0].Stage);
    }
    private sealed class NoGlyphGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => LyricSceneSampler.SampleFrame(snapshot, timeUs);
    }
    [Theory]
    [InlineData("AdvanceVisuals")] [InlineData("EvaluateVisuals")]
    public async Task 视觉Error与异常失败且取消保留取消语义(string stage)
    {
        foreach (string prefix in new[] { "Error", "Throw", "Cancel" })
        {
            var snapshot = VisualTestFixture.Snapshot();
            await using var session = await VisualTestFixture.Timeline([await VisualTestFixture.Definition(prefix + stage)]).CreateAsync(snapshot);
            async Task Run() => await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
            if (prefix == "Cancel") await Assert.ThrowsAsync<OperationCanceledException>(Run);
            else
            {
                var error = await Assert.ThrowsAsync<EffectFailureException>(Run); Assert.Equal(stage, error.Stage);
                Assert.Equal(stage == "AdvanceVisuals" ? 1_001 : 0, error.TimeUs);
                if (prefix == "Error") { Assert.Equal("VisualError", error.Code); Assert.Equal(new Fx.SourceLocation("src/VisualProbe.cs", 20, 4), error.Diagnostic!.Diagnostic.Location); }
            }
        }
    }
    [Fact]
    public async Task 旧视觉writer不能复用且纯帧拒绝有状态视觉入口()
    {
        var snapshot = VisualTestFixture.Snapshot(); var definition = await VisualTestFixture.Definition("expired");
        await using var session = await VisualTestFixture.Timeline([definition]).CreateAsync(snapshot);
        Assert.Equal("InvalidVisualOutputStage", (await Assert.ThrowsAsync<EffectFailureException>(async () =>
            await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot)))).Code);
        Assert.Equal("TimelineSessionRequired", (await Assert.ThrowsAsync<EffectFailureException>(async () =>
            await VisualTestFixture.Pure([definition]).CreateAsync(snapshot))).Code);
    }
    [Fact]
    public async Task 独立未来端点只执行语义文字且跳过视觉Error与所有圆点()
    {
        var snapshot = VisualTestFixture.Snapshot(); var definition = await VisualTestFixture.Definition("ErrorEvaluateVisuals");
        await using var querySession = await VisualTestFixture.Timeline([definition], Fx.ReplayReason.AnchorQuery).CreateAsync(snapshot);
        var frame = await querySession.PrepareAsync(5_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        Assert.Empty(frame.DotBatches); Assert.DoesNotContain(frame.Diagnostics, item => item.Stage is "AdvanceVisuals" or "EvaluateVisuals");
        var identity = Assert.Single(frame.Playback.Cursors).Identity;
        SemanticAnchorQuery query = new(snapshot, [definition], VisualTestFixture.Fingerprint, static () => new SkiaRenderPipeline());
        var endpoint = await query.ResolveAsync(identity, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter,
            Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        Assert.True(endpoint.IsAvailable);
    }
    [Fact]
    public async Task 改变输出模式或密度会从零重建视觉状态并标明InputsChanged()
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = VisualTestFixture.Target(snapshot);
        await using var session = await VisualTestFixture.Timeline([await VisualTestFixture.Definition()]).CreateAsync(snapshot);
        await session.PrepareAsync(75_001, Fx.OutputMode.Transparent, target);
        foreach (var output in new[] { (Fx.OutputMode.Opaque, target), (Fx.OutputMode.Opaque, new RenderTarget { PixelWidth = 640, PixelHeight = 360 }) })
        {
            var frame = await session.PrepareAsync(75_001, output.Item1, output.Item2);
            Assert.EndsWith(":InputsChanged", frame.Diagnostics.Last(item => item.Diagnostic.Code == "P").Diagnostic.Message);
            Assert.Equal(75_000, frame.Diagnostics.Last(item => item.Diagnostic.Code == "V").TimeUs);
            Assert.Equal(95.001, Assert.Single(Assert.Single(frame.DotBatches).Dots).Position.X, 10);
        }
    }
}
