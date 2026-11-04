using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class RubySpacingRenderingTests
{
    [Fact]
    public void No_ruby_does_not_build_token_index()
    {
        var tokens = Enumerable.Range(0, 1000).Select(i => MakoKaraGenerator.Core.Lyrics.LyricToken.Create("token-" + i, "a")).ToArray();
        var line = MakoKaraGenerator.Core.Lyrics.LyricLine.Create("line", new string('a', tokens.Length), tokens: tokens);
        using var pipeline = new SkiaRenderPipeline();
        var method = typeof(SkiaRenderPipeline).GetMethod("MeasureRuby", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object?[] arguments = [line, MakoKaraGenerator.Core.Projects.LyricStyle.Default, null];
        method.Invoke(pipeline, arguments);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) method.Invoke(pipeline, arguments);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 50_000, $"Empty ruby allocated {allocated} bytes for 100 calls.");
    }

    [Fact]
    public void Shared_layouts_survive_renderer_disposal_and_ruby_styles_do_not_reuse_stale_geometry()
    {
        var source = RubySpacingFixture.Create();
        var changed = source with { LyricStyle = source.LyricStyle with { FontSizePx = 62 } };
        var cache = new SkiaRenderPipeline.LayoutCache();
        byte[] expected;
        using (var first = new SkiaRenderPipeline(null, null, null, null, false, null, true, cache))
        using (var frame = first.Render(RenderRequest.At(source, 250_000) with { TransparentBackground = true }))
            expected = frame.PixelMemory.ToArray();
        int initialCount = cache.Count;
        int initialRubyBuilds = cache.RubyBuildCount;
        Assert.True(initialCount > 0);
        Assert.True(initialRubyBuilds > 0);
        using var reused = new SkiaRenderPipeline(null, null, null, null, false, null, true, cache);
        using var actual = reused.Render(RenderRequest.At(source, 250_000) with { TransparentBackground = true });
        Assert.Equal(expected, actual.PixelMemory.ToArray());
        Assert.Equal(initialCount, cache.Count);
        Assert.Equal(initialRubyBuilds, cache.RubyBuildCount);
        var fallbackChanged = source with { LyricStyle = source.LyricStyle with { FallbackFamilyNames = ["Noto Color Emoji"] } };
        using var fallbackFrame = reused.Render(RenderRequest.At(fallbackChanged, 250_000) with { TransparentBackground = true });
        Assert.True(cache.Count > initialCount);
        var equivalentFallback = source with { LyricStyle = source.LyricStyle with { FallbackFamilyNames = System.Collections.Immutable.ImmutableArray.Create("Noto Color Emoji") } };
        using var equivalentFrame = reused.Render(RenderRequest.At(equivalentFallback, 250_000) with { TransparentBackground = true });
        Assert.Equal(fallbackFrame.PixelMemory.ToArray(), equivalentFrame.PixelMemory.ToArray());
        using var changedFrame = reused.Render(RenderRequest.At(changed, 250_000) with { TransparentBackground = true });
        using var independent = new SkiaRenderPipeline();
        using var reference = independent.Render(RenderRequest.At(changed, 250_000) with { TransparentBackground = true });
        Assert.Equal(reference.PixelMemory.ToArray(), changedFrame.PixelMemory.ToArray());
        Assert.False(expected.AsSpan().SequenceEqual(changedFrame.PixelMemory.Span));
        var endAligned = RubySpacingFixture.Create(alignment: RubyAlignment.End);
        using var changedRuby = reused.Render(RenderRequest.At(endAligned, 250_000) with { TransparentBackground = true });
        using var referenceRuby = independent.Render(RenderRequest.At(endAligned, 250_000) with { TransparentBackground = true });
        Assert.Equal(referenceRuby.PixelMemory.ToArray(), changedRuby.PixelMemory.ToArray());
    }

    [Fact]
    public void 共享注音解算淘汰后重算且动态回退不跨引擎复用()
    {
        var source = RubySpacingFixture.Create();
        var cache = new SkiaRenderPipeline.LayoutCache();
        using var first = new SkiaRenderPipeline(null, null, null, null, false, null, true, cache);
        using var second = new SkiaRenderPipeline(null, null, null, null, false, null, true, cache);
        using var initial = first.Render(RenderRequest.At(source, 250_000) with { TransparentBackground = true });
        int builds = cache.RubyBuildCount;
        for (int index = 0; index < 257; index++)
            first.GetSceneFrame(source with { LyricStyle = source.LyricStyle with { FontSizePx = 30 + index / 10f } }, 250_000);
        int evictedBuilds = cache.RubyBuildCount;
        Assert.True(evictedBuilds > builds);
        using var restored = second.Render(RenderRequest.At(source, 250_000) with { TransparentBackground = true });
        Assert.True(cache.RubyBuildCount > evictedBuilds);
        Assert.Equal(initial.PixelMemory.ToArray(), restored.PixelMemory.ToArray());

        var dynamicFallback = source with { LyricStyle = source.LyricStyle with { FontFamilyName = "DejaVu Sans", FallbackFamilyNames = [] } };
        using var firstFallback = first.Render(RenderRequest.At(dynamicFallback, 250_000) with { TransparentBackground = true });
        int firstBuilds = cache.RubyBuildCount;
        using var secondFallback = second.Render(RenderRequest.At(dynamicFallback, 250_000) with { TransparentBackground = true });
        Assert.True(cache.RubyBuildCount > firstBuilds);
        Assert.Equal(firstFallback.PixelMemory.ToArray(), secondFallback.PixelMemory.ToArray());
    }

    [Fact]
    public void Capturing_base_only_keeps_collision_spacing_and_family_translation()
    {
        var snapshot = RubySpacingFixture.Create();
        using SkiaRenderPipeline pipeline = new();
        var scene = pipeline.GetSceneFrame(snapshot, 200_000);
        var family = scene.TokenGeometries[1];
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [new(family.WithRuby, Fx.Affine2.Translation(10, 0))]);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        var recipe = new SceneCaptureRecipe(false, false, false, false, SceneCaptureRegion.FillAndStroke);
        using var before = pipeline.Render(request);
        using var captured = pipeline.CaptureSceneTarget(request, new SceneCaptureTarget.Token(family.WithRuby), recipe,
            GraphInputSource.GlyphCoverage, new(0, 0, 640, 240), GraphExecutionBudget.Default);
        using var after = pipeline.Render(request);
        Assert.Equal(before.PixelMemory.ToArray(), after.PixelMemory.ToArray());
        using var bitmap = SKBitmap.FromImage(captured.Image);
        var corners = family.BaseGeometry.WorldInkCorners;
        double left = corners.Min(p => p.X) + 10, right = corners.Max(p => p.X) + 10;
        double top = corners.Min(p => p.Y), bottom = corners.Max(p => p.Y);
        int visible = 0; var bytes = bitmap.Bytes;
        for (int y = 0; y < 240; y++) for (int x = 0; x < 640; x++)
        {
            if (bytes[(y * 640 + x) * 4 + 3] == 0) continue;
            visible++;
            Assert.InRange(x + .5, left - 1, right + 1);
            Assert.InRange(y + .5, top - 1, bottom + 1);
        }
        Assert.True(visible > 100);
    }

    [Theory]
    [InlineData(RubyAlignment.Start)]
    [InlineData(RubyAlignment.Center)]
    [InlineData(RubyAlignment.End)]
    public void Wide_ruby_has_a_local_pixel_gap_and_preserves_its_alignment_without_timing_gate(RubyAlignment alignment)
    {
        using SkiaRenderPipeline pipeline = new();
        var timed = RubySpacingFixture.Create(alignment: alignment);
        var untimed = RubySpacingFixture.Create(false, alignment);
        var scene = pipeline.GetSceneFrame(timed, 200_000);
        var legacy = pipeline.GetSceneFrame(untimed, 200_000);
        var geometry = Assert.Single(scene.Lines).Geometry!;
        var scale = Math.Sqrt(Math.Pow(geometry.WorldMatrix!.Value.A, 2) + Math.Pow(geometry.WorldMatrix.Value.B, 2));
        double? previousRight = null;
        foreach (var token in scene.TokenGeometries)
        {
            var ruby = Assert.Single(token.Ruby).Geometry;
            var ink = ruby.WorldInkCorners;
            double left = ink.Min(p => p.X), right = ink.Max(p => p.X);
            if (previousRight is { } previous) Assert.True(left - previous >= scale - .001, $"gap={left - previous}, required={scale}");
            previousRight = right;
            double actualAnchor = alignment switch { RubyAlignment.Start => left, RubyAlignment.End => right, _ => (left + right) / 2 };
            var natural = token.BaseGeometry.AdvanceBounds;
            double localAnchor = alignment switch { RubyAlignment.Start => natural.Left, RubyAlignment.End => natural.Right, _ => natural.Center.X };
            var worldAnchor = geometry.Transform(new(localAnchor, geometry.BaselineY));
            Assert.InRange(Math.Abs(actualAnchor - worldAnchor.X), 0, .001);
            var matching = legacy.TokenGeometries.Single(t => t.WithRuby.TokenId == token.WithRuby.TokenId);
            Assert.Equal(matching.BaseGeometry.WorldInkCorners.ToArray(), token.BaseGeometry.WorldInkCorners.ToArray());
            Assert.Equal(Assert.Single(matching.Ruby).Geometry.WorldInkCorners.ToArray(), ink.ToArray());
            Assert.True(geometry.Contains(new((left + right) / 2, ink.Average(p => p.Y))));
        }
        Assert.False(geometry.Contains(new(-1000, -1000)));
        Assert.Equal(timed.LyricStyle.LetterSpacingPx, untimed.LyricStyle.LetterSpacingPx);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wide_ruby_fit_and_seek_preserve_geometry_and_segment_source(bool transparent)
    {
        var source = RubySpacingFixture.Create(width: 180);
        var segments = source.Lyrics.Lines[0].RubyAnnotations.SelectMany(r => r.Segments).ToArray();
        using SkiaRenderPipeline pipeline = new();
        var initial = pipeline.GetSceneFrame(source, 150_000);
        var line = Assert.Single(initial.Lines).Geometry!;
        var corners = line.Corners();
        Assert.InRange(corners.Min(p => p.X), -.001, 180.001);
        Assert.InRange(corners.Max(p => p.X), -.001, 180.001);
        byte[]? first = null;
        foreach (long time in new long[] { 150_000, 250_000, 800_000, 150_000 })
        {
            var scene = pipeline.GetSceneFrame(source, time);
            for (int i = 0; i < scene.TokenGeometries.Length; i++)
                Assert.Equal(initial.TokenGeometries[i].FamilyGeometry.WorldInkCorners.ToArray(), scene.TokenGeometries[i].FamilyGeometry.WorldInkCorners.ToArray());
            using var rendered = pipeline.Render(RenderRequest.At(source, time) with { TransparentBackground = transparent });
            var bytes = rendered.PixelMemory.ToArray();
            if (time == 150_000) { if (first is null) first = bytes; else Assert.Equal(first, bytes); }
            else if (time == 250_000) Assert.False(first!.AsSpan().SequenceEqual(bytes));
        }
        Assert.Equal(segments, source.Lyrics.Lines[0].RubyAnnotations.SelectMany(r => r.Segments).ToArray());
    }
}
