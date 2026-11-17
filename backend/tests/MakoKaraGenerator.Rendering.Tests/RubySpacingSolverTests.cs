using System.Collections.Immutable;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class RubySpacingSolverTests
{
    [Fact]
    public void Natural_edge_bearings_and_letter_spacing_set_the_required_seam()
    {
        // A ends at ink x=15 with 3 px right bearing; B starts at ink x=16
        // with 2 px left bearing. One 4 px interval gives a 9 px seam,
        // hence B and all later groups must move by 8 px.
        var actual = RubySpacingSolver.Solve(Basis(3), [new(0, 1), new(1, 1), new(2, 1)],
        [
            new(0, new(-5, -20, 15, -10), LeadingSpace: 1, TrailingSpace: 3, LetterSpacing: 4),
            new(1, new(16, -20, 25, -10), LeadingSpace: 2, TrailingSpace: 1),
        ]);
        Assert.Equal(RubySpacingStatus.Adjusted, actual.Status);
        Assert.Equal(new float[] { 0, 8 }, actual.RubyOffsets.ToArray());
        Assert.Equal(18, actual.Basis.DrawUnits[1].X);
        Assert.Equal(28, actual.Basis.DrawUnits[2].X);
    }

    [Fact]
    public void Natural_seam_without_added_letter_spacing_and_negative_bearings()
    {
        var tokens = new TextSpan[] { new(0, 1), new(1, 1) };
        var basis = Basis(2);
        var natural = RubySpacingSolver.Solve(basis, tokens,
            [new(0, new(0, -20, 15, -10), TrailingSpace: 3), new(1, new(16, -20, 25, -10), LeadingSpace: 2)]);
        Assert.Equal(4, natural.RubyOffsets[1]); // natural gap 5, original gap 1
        var negative = RubySpacingSolver.Solve(basis, tokens,
            [new(0, new(0, -20, 15, -10), TrailingSpace: -3), new(1, new(15, -20, 25, -10), LeadingSpace: 1)]);
        Assert.Equal(1, negative.RubyOffsets[1]); // negative bearings still keep the 1 px floor
    }

    [Theory]
    [InlineData(.99f, true)]
    [InlineData(1f, false)]
    [InlineData(1.01f, false)]
    public void Minimum_gap_threshold_and_nonadjacent_vertical_constraint(float gap, bool changes)
    {
        var basis = Basis(3);
        var actual = RubySpacingSolver.Solve(basis, [new(0, 1), new(1, 1), new(2, 1)],
            [new(0, new(0, -20, 20, -10)), new(1, new(10, -40, 30, -30)), new(2, new(20 + gap, -20, 40 + gap, -10))]);
        Assert.Equal(0, actual.RubyOffsets[1]);
        Assert.Equal(changes ? 1 - gap : 0, actual.RubyOffsets[2], 4);
        if (!changes) Assert.Same(basis, actual.Basis);
    }

    [Theory]
    [InlineData(-5, 15, 5, 25)] // Center
    [InlineData(0, 20, 10, 30)] // Start
    [InlineData(-10, 10, 0, 20)] // End
    public void Hand_computed_anchors_add_eleven_pixels_without_stretching_natural_advance(float a, float b, float c, float d)
    {
        var original = Basis(2);
        var actual = RubySpacingSolver.Solve(original, [new(0, 1), new(1, 1)], [new(0, new(a, -20, b, -10)), new(1, new(c, -20, d, -10))]);
        Assert.Equal(RubySpacingStatus.Adjusted, actual.Status);
        Assert.Equal(new float[] { 0, 11 }, actual.RubyOffsets.ToArray());
        Assert.Equal(31, actual.Basis.Width);
        Assert.Equal(21, actual.Basis.DrawUnits[1].X);
        Assert.All(actual.Basis.DrawUnits, unit => Assert.Equal(10, unit.Advance));
        Assert.All(actual.Basis.Elements, unit => Assert.Equal(10, unit.Advance));
        Assert.Equal(21, actual.Basis.XAt(1)); // preceding progress traverses inserted gap
        Assert.Equal(original.DrawUnits.SelectMany(u => u.Glyphs.Select(g => g.GlyphId)), actual.Basis.DrawUnits.SelectMany(u => u.Glyphs.Select(g => g.GlyphId)));
    }

    [Fact]
    public void Three_groups_propagate_cumulatively_and_keep_input_annotation_order()
    {
        var actual = RubySpacingSolver.Solve(Basis(3), [new(0, 1), new(1, 1), new(2, 1)],
            [new(2, new(15, -20, 35, -10)), new(0, new(-5, -20, 15, -10)), new(1, new(5, -20, 25, -10))]);
        Assert.Equal(new float[] { 22, 0, 11 }, actual.RubyOffsets.ToArray());
        Assert.Equal(52, actual.Basis.Width);
    }

    [Fact]
    public void A_long_earlier_annotation_constrains_across_unannotated_tokens()
    {
        var actual = RubySpacingSolver.Solve(Basis(4), [new(0, 1), new(1, 1), new(2, 1), new(3, 1)],
            [new(0, new(0, -20, 60, -10)), new(3, new(30, -20, 40, -10))]);
        Assert.Equal(new float[] { 0, 31 }, actual.RubyOffsets.ToArray());
        Assert.Equal(71, actual.Basis.Width);
    }

    [Theory]
    [InlineData(-10, 0)] // touching vertical boundary has no positive intersection
    [InlineData(0, 10)]
    public void Different_vertical_bands_need_no_spacing(float top, float bottom)
    {
        var original = Basis(2);
        var actual = RubySpacingSolver.Solve(original, [new(0, 1), new(1, 1)],
            [new(0, new(0, -20, 30, -10)), new(1, new(10, top, 40, bottom))]);
        Assert.Same(original, actual.Basis);
        Assert.Equal(RubySpacingStatus.Unchanged, actual.Status);
    }

    [Fact]
    public void Shared_cluster_is_not_split_but_safe_later_boundary_can_move()
    {
        var original = Basis(3, shared: true);
        var actual = RubySpacingSolver.Solve(original, [new(0, 1), new(1, 1), new(2, 1)],
            [new(0, new(-5, -20, 15, -10)), new(1, new(5, -20, 25, -10)), new(2, new(15, -20, 35, -10))]);
        Assert.True(actual.Status.HasFlag(RubySpacingStatus.SharedClusterConflict));
        Assert.True(actual.Status.HasFlag(RubySpacingStatus.Adjusted));
        Assert.Equal(new float[] { 0, 0, 11 }, actual.RubyOffsets.ToArray());
        Assert.Equal(2, actual.Basis.DrawUnits[0].Utf16Length);
        Assert.Equal(original.DrawUnits[0].X, actual.Basis.DrawUnits[0].X);
        Assert.Equal(original.DrawUnits[0].Advance, actual.Basis.DrawUnits[0].Advance);
        Assert.Equal(original.DrawUnits[0].Glyphs.ToArray(), actual.Basis.DrawUnits[0].Glyphs.ToArray());
    }

    [Fact]
    public void Sufficient_gap_returns_original_shape()
    {
        var basis = Basis(2);
        var actual = RubySpacingSolver.Solve(basis, [new(0, 1), new(1, 1)],
            [new(0, new(0, -20, 9, -10)), new(1, new(10, -20, 19, -10))]);
        Assert.Same(basis, actual.Basis);
        Assert.Equal(RubySpacingStatus.Unchanged, actual.Status);
    }

    [Fact]
    public void Invalid_mapping_nonfinite_and_visual_RTL_are_conservatively_preserved()
    {
        var basis = Basis(2);
        RubySpacingItem[] ruby = [new(0, new(0, -20, 30, -10)), new(1, new(10, -20, 40, -10))];
        var invalid = RubySpacingSolver.Solve(basis, [new(0, 1), new(0, 1)], ruby);
        Assert.Same(basis, invalid.Basis); Assert.Equal(RubySpacingStatus.InvalidInput, invalid.Status);
        var approximate = Basis(2, exact: false);
        Assert.Same(approximate, RubySpacingSolver.Solve(approximate, [new(0, 1), new(1, 1)], ruby).Basis);
        var nonfinite = RubySpacingSolver.Solve(basis, [new(0, 1), new(1, 1)], [ruby[0], new(1, new(float.NaN, -20, 40, -10))]);
        Assert.Same(basis, nonfinite.Basis); Assert.Equal(RubySpacingStatus.UnsupportedCoordinates, nonfinite.Status);
        var rtl = Basis(2, rtl: true);
        var actual = RubySpacingSolver.Solve(rtl, [new(0, 1), new(1, 1)], ruby);
        Assert.Same(rtl, actual.Basis); Assert.Equal(RubySpacingStatus.UnsupportedMapping, actual.Status);
    }

    private static ShapedLine Basis(int count, bool shared = false, bool exact = true, bool rtl = false)
    {
        var elements = Enumerable.Range(0, count).Select(i => new ShapedTextElement(i, 1, "a", rtl ? (count - 1 - i) * 10 : i * 10,
            10, "fixture", false, 1)).ToArray();
        var units = Enumerable.Range(0, count).Where(i => !shared || i != 1).Select(i => {
            float x = rtl ? (count - 1 - i) * 10 : i * 10;
            int length = shared && i == 0 ? 2 : 1;
            return new ShapedDrawUnit(i, length, "fixture", x, length * 10,
                [new((ushort)(i + 1), x, 0, new(x, -8, x + length * 10, 2))], -8, 2);
        }).ToImmutableArray();
        return new(new string('a', count), elements, count * 10, exact, 0, new(0, -8, count * 10, 2), units);
    }
}
