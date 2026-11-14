using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VisualOverlayRenderingTests
{
    [Theory]
    [InlineData(320, 180)] [InlineData(640, 360)] [InlineData(640, 180)]
    public async Task 实际作者圆点与独立Skia绘制逐字节相同并随密度正确缩放(int width, int height)
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = new RenderTarget { PixelWidth = width, PixelHeight = height };
        var definition = await VisualTestFixture.Definition("above", "PureDots", x: 60, y: 25, radius: 8, color: new(1, .4, .2, .5));
        await using var session = await VisualTestFixture.Pure([definition]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        using SkiaRenderPipeline renderer = new();
        using var basis = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, frame.TimeUs) with { Target = target, TransparentBackground = true });
        var dot = Assert.Single(Assert.Single(frame.DotBatches).Dots);
        using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { Target = target, PreparedFrame = frame, TransparentBackground = true });
        Assert.Equal(OverlayGolden(basis, dot, target.ScaleXOf(snapshot.Composition), target.ScaleYOf(snapshot.Composition)), actual.PixelMemory.ToArray());
        Assert.True(actual.TryGetPixel((int)(60 * target.ScaleXOf(snapshot.Composition)), (int)(25 * target.ScaleYOf(snapshot.Composition)), out var color));
        Assert.Equal(128, color.Alpha); Assert.Equal(255, color.Red);
        Assert.Same(frame.Scene, frame.HierarchyScene);
        Assert.Same(snapshot.Lyrics, frame.Snapshot.Lyrics); Assert.Same(snapshot.LyricPresentation, frame.Snapshot.LyricPresentation);
    }
    [Fact]
    public async Task 前后图层只改变正确的遮挡次序且实例顺序保留半透明合成()
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = VisualTestFixture.Target(snapshot);
        using SkiaRenderPipeline renderer = new();
        using var basis = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, 18_001) with { TransparentBackground = true });
        int x = 0, y = 0; bool found = false;
        for (int row = 20; row < basis.Height - 20 && !found; row++)
            for (int column = 20; column < basis.Width - 20; column++)
                if (basis.TryGetPixel(column, row, out var pixel) && pixel.Alpha == 255)
                { x = column; y = row; found = true; break; }
        Assert.True(found);
        foreach (string mode in new[] { "behind", "above" })
        {
            var definition = await VisualTestFixture.Definition(mode, "PureDots", x: x, y: y, radius: 15, color: new(1, 0, 0, 1));
            await using var session = await VisualTestFixture.Pure([definition]).CreateAsync(snapshot);
            var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
            using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
            Assert.True(actual.TryGetPixel(x, y, out var pixel));
            Assert.Equal(mode == "behind" ? 255 : 0, pixel.Green); Assert.Equal(255, pixel.Red);
            if (mode == "above") Assert.Equal(OverlayGolden(basis, frame.DotBatches[0].Dots[0], 1, 1), actual.PixelMemory.ToArray());
        }
        var first = await VisualTestFixture.Definition("above", "PureDots", x: 30, y: 25, color: new(1, 0, 0, .5));
        var second = (await VisualTestFixture.Definition("above", "PureDots", x: 30, y: 25, color: new(0, 0, 1, .5))) with { Id = new("second") };
        await using var ordered = await VisualTestFixture.Pure([first, second]).CreateAsync(snapshot);
        var output = await ordered.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        Assert.Equal((string[])["visual", "second"], output.DotBatches.Select(batch => batch.InstanceId.Value));
        using var drawn = renderer.Render(RenderRequest.At(snapshot, output.TimeUs) with { PreparedFrame = output, TransparentBackground = true });
        byte[] expected = basis.PixelMemory.ToArray();
        foreach (var batch in output.DotBatches) expected = OverlayGolden(expected, basis.Width, basis.Height, batch.Dots[0], 1, 1);
        Assert.Equal(expected, drawn.PixelMemory.ToArray());
    }
    [Fact]
    public async Task 纯视觉的空输出保持基础帧()
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = VisualTestFixture.Target(snapshot);
        await using var none = await VisualTestFixture.Pure([await VisualTestFixture.Definition("none", "PureDots")]).CreateAsync(snapshot);
        var frame = await none.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        Assert.Empty(frame.DotBatches); Assert.Empty(frame.Diagnostics); Assert.Same(frame.Scene, frame.HierarchyScene);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 视觉圆点读取三槽前奏的最终附着或固定标记矩阵且不重复乘父组(bool fixedParent)
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var root = tree.FindNode(program.RootContainerId)!; var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.57, .44), rotationDegrees: 18, scale: .85) })
            .Replace(group, group with { Attachment = new() { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree }) with { EffectInputsFingerprint = VisualTestFixture.Fingerprint };
        await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition("mark", "PureDots")]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        var mark = frame.FinalMarkGeometry[0].Geometry; var dot = Assert.Single(Assert.Single(frame.DotBatches).Dots);
        Assert.Equal(mark.BaseWorldMatrix.Map(mark.AdvanceBounds.Center), dot.Position);
        using SkiaRenderPipeline renderer = new();
        using var text = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with
            { PreparedFrame = frame.WithVisualOutput([], frame.Diagnostics), TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        Assert.Equal(OverlayGolden(text, dot, 1, 1), actual.PixelMemory.ToArray());
    }
    [Fact]
    public async Task 全实例预算与声明能力明确失败不会静默削减()
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = VisualTestFixture.Target(snapshot);
        var first = await VisualTestFixture.Definition("above", "PureDots");
        await using var budget = await VisualTestFixture.Pure([first, first with { Id = new("second") }], new(1)).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await budget.PrepareAsync(18_001, Fx.OutputMode.Transparent, target));
        Assert.Equal("VisualOutputBudgetExceeded", error.Code); Assert.Equal(new Fx.EffectInstanceId("second"), error.InstanceId);
        var missingCapability = first with
        {
            PackageDefinition = MakoKaraGenerator.Core.Effects.EffectPackageDefinition.Read(V2RenderPackageHelper.UpgradeToV2(
                first.PackageDefinition!.Contents, [Fx.V2.CapabilityNames.TokenAffine, Fx.V2.CapabilityNames.LineAffine, Fx.V2.CapabilityNames.MarkAffine],
                manifest =>
                {
                    foreach (var export in manifest["exports"]!.AsArray())
                        foreach (var output in export!["outputs"]!.AsArray())
                            output!["requires"] = new System.Text.Json.Nodes.JsonArray(
                                ((System.Text.Json.Nodes.JsonArray)output["requires"]!)
                                .Where(r => r!.GetValue<string>() != Fx.V2.CapabilityNames.VisualsDots)
                                .Select(r => System.Text.Json.Nodes.JsonValue.Create(r!.GetValue<string>())).ToArray());
                })),
        };
        Assert.Equal("UndeclaredCapability", (await Assert.ThrowsAsync<EffectFailureException>(async () =>
        {
            await using var undeclared = await VisualTestFixture.Pure([missingCapability]).CreateAsync(snapshot);
            await undeclared.PrepareAsync(18_001, Fx.OutputMode.Transparent, target);
        })).Code);
    }
    [Theory]
    [InlineData("large")] [InlineData("tiny")]
    public async Task 后端坐标越限或半径下溢阻止绘制而不夹值(string mode)
    {
        var snapshot = VisualTestFixture.Snapshot();
        await using var session = await VisualTestFixture.Pure([await VisualTestFixture.Definition(mode, "PureDots")]).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot));
        using SkiaRenderPipeline renderer = new();
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(RenderRequest.At(snapshot, frame.TimeUs)
            with { PreparedFrame = frame, TransparentBackground = true }));
        Assert.Equal("UnsupportedNativeOverlay", error.Code);
        var owner = Assert.Single(frame.OverlayOwners);
        Assert.Equal(owner.InstanceId, error.InstanceId); Assert.Equal(owner.ArtifactHash, error.ArtifactHash);
        Assert.Equal("RenderOverlays", error.Stage); Assert.Equal(frame.TimeUs, error.TimeUs);
        Assert.Equal(error.Code, Assert.IsType<PreparedFrameException>(error.InnerException).Code);
    }
    [Fact]
    public void 准备视觉输出保留文字闭包并拒绝伪造重复批次与Error诊断()
    {
        var snapshot = VisualTestFixture.Snapshot(); using SkiaRenderPipeline renderer = new();
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        Fx.DotBatch batch = new(new("fx"), new("dot"), Fx.OverlayLayer.AboveText, [new(new(10, 20), 3, new(1, 0, 0, .5))]);
        var output = frame.WithVisualOutput([batch], []);
        Assert.Same(frame.Scene, output.Scene); Assert.Same(frame.HierarchyScene, output.HierarchyScene); Assert.Same(frame.Playback, output.Playback);
        Assert.Equal(frame.FinalGeometry, output.FinalGeometry); Assert.Equal(frame.FinalGroupGeometry, output.FinalGroupGeometry);
        Assert.Same(frame, frame.WithVisualOutput([], []));
        Assert.Equal("InvalidPreparedOverlay", Assert.Throws<PreparedFrameException>(() => frame.WithVisualOutput([batch, batch], [])).Code);
        Assert.Throws<PreparedFrameException>(() => frame.WithVisualOutput([batch with { Dots = [default] }], []));
        Fx.RuntimeDiagnostic fatal = new(new("fx"), "artifact", "EvaluateVisuals", frame.TimeUs, new(Fx.DiagnosticSeverity.Error, "Error", "failed"));
        Assert.Equal("InvalidPreparedDiagnostics", Assert.Throws<PreparedFrameException>(() => frame.WithVisualOutput([batch], [fatal])).Code);
        Assert.Throws<PreparedFrameException>(() => frame.WithVisualOutput([batch], [], new(1))
            .WithVisualOutput([batch with { Dots = [batch.Dots[0], batch.Dots[0]] }], [], new(1)));
    }
    private static byte[] OverlayGolden(RenderFrame basis, Fx.Dot dot, double sx, double sy)
        => OverlayGolden(basis.PixelMemory.ToArray(), basis.Width, basis.Height, dot, sx, sy);
    private static byte[] OverlayGolden(byte[] basis, int width, int height, Fx.Dot dot, double sx, double sy)
    {
        return SkiaTestDrawing.DrawOnSelectedBackend(basis, width, height, canvas =>
        {
            canvas.Scale((float)sx, (float)sy);
            using SKPaint paint = new() { IsAntialias = true, Color = new((byte)Math.Round(dot.Color.Red * 255), (byte)Math.Round(dot.Color.Green * 255),
                (byte)Math.Round(dot.Color.Blue * 255), (byte)Math.Round(dot.Color.Alpha * 255)) };
            canvas.DrawCircle((float)dot.Position.X, (float)dot.Position.Y, (float)dot.Radius, paint);
        });
    }
}
