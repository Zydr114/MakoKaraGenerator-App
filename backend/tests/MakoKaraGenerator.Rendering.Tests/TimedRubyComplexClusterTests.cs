using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>Independent acceptance: a real ligature crossing segment boundaries belongs to its first segment.</summary>
public sealed class TimedRubyComplexClusterTests
{
    [Theory]
    [InlineData("DejaVu Sans", "f", "i", false)]
    [InlineData("DejaVu Sans", "f", "i", true)]
    [InlineData("DejaVu Sans", "ل", "ا", false)]
    [InlineData("DejaVu Sans", "ل", "ا", true)]
    public void Actual_cross_segment_ligatures_match_whole_ruby_pixels_and_geometry_during_forward_and_seek(
        string family, string first, string rest, bool transparent)
    {
        if (family == "DejaVu Sans")
            family = TestFonts.LigatureLatinAndArabic;
        const float rubySize = 24;
        using SkiaTextLayoutEngine shaper = new();
        var shaped = shaper.Shape(first + rest, new() { FamilyName = family, FontSizePx = rubySize });
        Assert.False(shaped.HasMissingGlyphs);
        var unit = Assert.Single(shaped.DrawUnits);
        Assert.Equal(0, unit.Utf16Start);
        Assert.Equal(2, unit.Utf16Length);
        Assert.Single(unit.Glyphs);

        var split = TimedRubyFixture.Create([
            RubyTextSegment.Create("first", first, new(100_000, 300_000), TimingStatus.Imported),
            RubyTextSegment.Create("rest", rest, new(700_000, 900_000), TimingStatus.Imported)]);
        split = split with { LyricStyle = split.LyricStyle with { FontFamilyName = family } };
        var whole = TimedRubyFixture.Create([
            RubyTextSegment.Create("whole", first + rest, new(100_000, 300_000), TimingStatus.Imported)]);
        whole = whole with { LyricStyle = whole.LyricStyle with { FontFamilyName = family } };
        using SkiaRenderPipeline pipeline = new();
        byte[]? firstVisit = null;
        foreach (long time in new long[] { 0, 150_000, 200_000, 500_000, 800_000, 200_000 })
        {
            var actualGeometry = Assert.Single(Assert.Single(pipeline.GetSceneFrame(split, time).TokenGeometries).Ruby).Geometry;
            var expectedGeometry = Assert.Single(Assert.Single(pipeline.GetSceneFrame(whole, time).TokenGeometries).Ruby).Geometry;
            Assert.Equal(expectedGeometry.DrawUnits.ToArray(), actualGeometry.DrawUnits.ToArray());
            Assert.Equal(expectedGeometry.WorldInkCorners.ToArray(), actualGeometry.WorldInkCorners.ToArray());
            using var actual = pipeline.Render(RenderRequest.At(split, time) with { TransparentBackground = transparent });
            using var expected = pipeline.Render(RenderRequest.At(whole, time) with { TransparentBackground = transparent });
            var pixels = actual.PixelMemory.ToArray();
            Assert.Equal(expected.PixelMemory.ToArray(), pixels);
            if (time != 200_000) continue;
            if (firstVisit is null) firstVisit = pixels;
            else Assert.Equal(firstVisit, pixels);
        }
    }
}
