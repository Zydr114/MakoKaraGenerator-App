using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// Material-color oracle for the finite AB/Ruby fixture. Public raw capture is shared geometry input,
/// never the material fallback/executor/compositor. All expected material BGRA and tint composition is CPU arithmetic.
/// Parent-only Ruby and original Glow remain independent non-Base-material references, not an independent font/blur oracle.
/// </summary>
internal static class BaseMaterialPixelOracle
{
    private static readonly MaterialValue Clear = MaterialValue.Solid(new(0, 0, 0, 0));
    private static readonly MaterialValue White = MaterialValue.Solid(new(1, 1, 1, 1));
    private static readonly SceneCaptureRecipe FillRecipe = new(false, false, false, false, SceneCaptureRegion.Fill, IncludeGlow: false);
    private static readonly SceneCaptureRecipe StrokeRecipe = new(false, true, false, false, SceneCaptureRegion.Stroke, IncludeGlow: false);

    internal static byte[] Expected(SkiaRenderPipeline renderer, ProjectSnapshot snapshot, PreparedRenderFrame frame,
        double unsungAmount, double sungAmount, double opacity, bool strokeOnly = false, params double[] postRgbFactors)
    {
        var token = Assert.Single(frame.FinalGeometry);
        // This oracle intentionally rejects fixtures with additional unmodelled object/group/common-alpha operations.
        Assert.Single(frame.HierarchyScene.Lines);
        Assert.Empty(frame.MarkStyles); Assert.Empty(frame.GroupOpacities); Assert.Empty(frame.OverlayBatches);
        Assert.Null(frame.GetBaseAppearance(token.BaseOnly).Unsung.Shadow);
        Assert.Equal(1f, snapshot.LyricStyle.Opacity);
        var parent = BaseMaterialPackageFixture.Parent(frame).WithImageEffects([]);
        if (parent.TokenStyles.Any(style => style.Target == token.WithRuby))
            Assert.Equal(1, parent.GetTokenAppearance(token.WithRuby).Opacity);
        Assert.Equal(opacity, frame.GetBaseAppearance(token.BaseOnly).Opacity);

        var glow = frame.GetBaseAppearance(token.BaseOnly).Unsung.Glow;
        var clear = TestStyle.Update(Clear, Clear, [], new(null), new(glow), opacity);
        PreparedRenderFrame decoration = WithoutBaseMaterial(clear);
        using var glowPart = renderer.CaptureSceneColorPart(BaseMaterialRuntimeTests.Request(snapshot, decoration),
            new SceneCaptureTarget.Base(token.BaseOnly), SceneColorPart.BaseGlow, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default);
        byte[] expected = SkiaTestReadback.Pixels(glowPart.Surface.Image);

        if (strokeOnly)
        {
            Assert.Equal(5, frame.GetBaseAppearance(token.BaseOnly).Unsung.InnermostStroke!.WidthPx);
            var strokeFrame = WithoutBaseMaterial(TestStyle.Update(Clear, Clear, [new StrokeValue(5, White)], new(null), new(null)));
            byte[] mask = Raw(strokeFrame, GraphInputSource.StrokeCoverage, StrokeRecipe);
            Over(expected, Tint(mask, unsungAmount, opacity));
            // A union mask is tinted once. Fully covered stroke alpha cannot exceed 160*.5=80.
            Assert.Equal(80, MaximumAlpha(Tint(mask, unsungAmount, .5)));
        }
        else
        {
            // Keep two independently captured complementary source passes, especially after real Warp resampling.
            // Subtracting SungCoverage from a full mask would hide overlap/rounding at a transformed cut.
            var unsung = WithoutBaseMaterial(TestStyle.Update(White, Clear, [], new(null), new(null)));
            var sung = WithoutBaseMaterial(TestStyle.Update(Clear, White, [], new(null), new(null)));
            Over(expected, Tint(Raw(unsung, GraphInputSource.TargetColor, FillRecipe), unsungAmount, opacity));
            Over(expected, Tint(Raw(sung, GraphInputSource.TargetColor, FillRecipe), sungAmount, opacity));
        }

        // An explicit transparent Base shadows parent material while preserving the parent's whole Ruby and Glow.
        var rubyOnly = WithoutBaseMaterial(TestStyle.Update(Clear, Clear, [], new(null), new(null)));
        using var ruby = BaseMaterialRuntimeTests.Render(renderer, snapshot, rubyOnly);
        Assert.Equal(ruby.Width * 4, ruby.Stride);
        Over(expected, ruby.PixelMemory.ToArray());
        foreach (double factor in postRgbFactors)
        {
            Assert.InRange(factor, 0, 1);
            for (int offset = 0; offset < expected.Length; offset += 4)
                for (int channel = 0; channel < 3; channel++) expected[offset + channel] = Quantize(expected[offset + channel] * factor);
        }
        return expected;

        PreparedRenderFrame WithoutBaseMaterial(StyleUpdate style)
        {
            var value = parent.WithTextPartitions(frame.BaseTransforms, [new(token.BaseOnly, style)], frame.ClusterTransforms)
                .WithTextWarps(frame.TextWarps);
            Assert.Empty(value.BaseMaterials);
            return value;
        }
        byte[] Raw(PreparedRenderFrame value, GraphInputSource channel, SceneCaptureRecipe recipe)
        {
            using var surface = renderer.CaptureSceneTarget(BaseMaterialRuntimeTests.Request(snapshot, value),
                new SceneCaptureTarget.Base(token.BaseOnly), recipe, channel, BaseMaterialPackageFixture.Bounds, GraphExecutionBudget.Default);
            Assert.Equal(640, surface.Image.Width); Assert.Equal(360, surface.Image.Height);
            return SkiaTestReadback.Pixels(surface.Image);
        }
    }

    // Fixed fixture fallback=#3380CCA0, author graph=half4(c.rgb*amount,c.a), local opacity once.
    // No material definition/evaluator output is used as the expected color source.
    internal static byte[] Tint(byte[] raw, double amount, double opacity)
    {
        Assert.InRange(amount, 0, 1); Assert.InRange(opacity, 0, 1);
        byte[] color = new byte[raw.Length];
        for (int offset = 0; offset < raw.Length; offset += 4)
        {
            double coverage = raw[offset + 3] / 255d;
            double alpha = coverage * 160;
            color[offset] = Quantize(alpha * (204 / 255d) * amount * opacity);
            color[offset + 1] = Quantize(alpha * (128 / 255d) * amount * opacity);
            color[offset + 2] = Quantize(alpha * (51 / 255d) * amount * opacity);
            color[offset + 3] = Quantize(alpha * opacity);
        }
        return color;
    }
    internal static void Over(byte[] below, byte[] above)
    {
        Assert.Equal(below.Length, above.Length);
        for (int offset = 0; offset < below.Length; offset += 4)
        {
            double inverse = 1 - above[offset + 3] / 255d;
            for (int channel = 0; channel < 4; channel++)
                below[offset + channel] = Quantize(above[offset + channel] + below[offset + channel] * inverse);
        }
    }
    internal static int MaximumAlpha(byte[] pixels)
    {
        int maximum = 0;
        for (int offset = 3; offset < pixels.Length; offset += 4) maximum = Math.Max(maximum, pixels[offset]);
        return maximum;
    }
    internal static void Compare(byte[] expected, RenderFrame actual, int tolerance = 2)
    {
        Assert.Equal(actual.Width * 4, actual.Stride); Assert.Equal(expected.Length, actual.PixelMemory.Length);
        int maximum = 0, worst = 0, different = 0;
        for (int offset = 0; offset < expected.Length; offset++)
        {
            int delta = Math.Abs(expected[offset] - actual.PixelMemory.Span[offset]);
            if (delta > tolerance) different++;
            if (delta > maximum) { maximum = delta; worst = offset; }
        }
        int pixel = worst / 4 * 4;
        Assert.True(maximum <= tolerance, $"raw-geometry/CPU-color oracle: max={maximum}, channels={different}, x={pixel % actual.Stride / 4}, y={pixel / actual.Stride}, expected BGRA={string.Join(',', expected.AsSpan(pixel, 4).ToArray())}, actual BGRA={string.Join(',', actual.PixelMemory.Span.Slice(pixel, 4).ToArray())}");
    }
    internal static void Compare(byte[] expected, byte[] actual, int width, int tolerance = 2)
    {
        Assert.Equal(expected.Length, actual.Length);
        int maximum = 0, worst = 0;
        for (int offset = 0; offset < expected.Length; offset++)
        {
            int delta = Math.Abs(expected[offset] - actual[offset]);
            if (delta > maximum) { maximum = delta; worst = offset; }
        }
        Assert.True(maximum <= tolerance, $"raw geometry max={maximum}, x={worst / 4 % width}, y={worst / 4 / width}");
    }
    private static byte Quantize(double value) => checked((byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255));
}
