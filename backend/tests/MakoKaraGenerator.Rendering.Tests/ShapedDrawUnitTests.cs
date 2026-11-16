using System.Collections.Immutable;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ShapedDrawUnitTests
{
    [Theory]
    [InlineData("AVATAR")]
    [InlineData("office")]
    [InlineData("cafe\u0301")]
    [InlineData("سلام")]
    public void 固定字形绘制与上游整行结果像素一致(string text)
    {
        using SkiaTextLayoutEngine engine = new();
        TextLayoutOptions options = new() { FamilyName = TestFonts.LigatureLatinAndArabic, FontSizePx = 56 };
        ShapedLine line = engine.Shape(text, options);
        using SKBitmap actual = Draw(engine, line, options);
        using SKBitmap expected = NewBitmap();
        using SKCanvas canvas = new(expected);
        using SKTypeface face = SKTypeface.FromFamilyName(engine.ResolveFamilyName(options.FamilyName));
        using SKFont font = new(face, options.FontSizePx);
        using SKShaper shaper = new(face);
        using SKPaint paint = new() { Color = SKColors.White, IsAntialias = true };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawShapedText(shaper, text, 20, 95, SKTextAlign.Left, font, paint);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.True(line.MappingIsExact);
        Assert.Equal(text.Length, line.DrawUnits.Sum(unit => unit.Utf16Length));
        Assert.All(line.DrawUnits, unit => Assert.Equal(line.Elements.First(element => element.Utf16Start == unit.Utf16Start).GlyphCount, unit.Glyphs.Length));
    }

    [Fact]
    public void 完整合字可绑定一个元素但不能跨元素独立变化()
    {
        using SkiaTextLayoutEngine engine = new();
        ShapedLine line = engine.Shape("office", new() { FamilyName = TestFonts.LigatureFfi, FontSizePx = 56 });
        ShapedDrawUnit ligature = Assert.Single(line.DrawUnits.Where(unit => unit.Utf16Length > 1));
        Assert.Equal("ffi", line.Text.Substring(ligature.Utf16Start, ligature.Utf16Length));
        var split = GlyphPartitioner.Partition(line, Enumerable.Range(0, line.Text.Length).Select(index => new TextSpan(index, 1)).ToArray());
        Assert.All(split.Where(item => item.SourceIndex >= ligature.Utf16Start && item.SourceIndex < ligature.Utf16End), item =>
        {
            Assert.False(item.CanTransformIndependently);
            Assert.Equal("UnsupportedGlyphPartition", item.DiagnosticCode);
            Assert.Single(item.DrawUnitIndices);
        });
        var whole = GlyphPartitioner.Partition(line, [new(0, 1), new(1, 3), new(4, 1), new(5, 1)]);
        Assert.All(whole, item => Assert.True(item.CanTransformIndependently));
    }

    [Theory]
    [InlineData("请照亮👩‍🚀", 0)]
    [InlineData("请照亮👩‍🚀", 8)]
    [InlineData("cafe\u0301", 0)]
    public void 分组绘制单元之和与整行像素完全相同(string text, float spacing)
    {
        using SkiaTextLayoutEngine engine = new();
        TextLayoutOptions options = new() { FontSizePx = 56, LetterSpacingPx = spacing };
        ShapedLine line = engine.Shape(text, options);
        using SKBitmap original = Draw(engine, line, options);
        using SKBitmap separated = NewBitmap();
        using SKCanvas canvas = new(separated);
        canvas.Clear(SKColors.Transparent);
        for (int i = 0; i < line.DrawUnits.Length; i++)
            engine.DrawLine(canvas, line, options, 20, 95, SKColors.White, drawUnitIndices: [i]);
        Assert.Equal(original.Bytes, separated.Bytes);
        Assert.All(line.DrawUnits, unit => Assert.All(unit.Glyphs, glyph =>
        {
            if (glyph.InkBounds is { } box) Assert.True(box.Right > box.Left && box.Bottom > box.Top);
        }));
    }

    [Fact]
    public void 缓存字形不依赖可变子串再次整形()
    {
        using SkiaTextLayoutEngine engine = new();
        TextLayoutOptions options = new() { FontSizePx = 56 };
        ShapedLine original = engine.Shape("请照亮", options);
        // 改造一份只用于探针的文本／Elements，不改固定字形；若绘制再 Shape 子串会画成 XXX。
        ShapedLine diagnostic = new("XXX", original.Elements.Select(element => element with { Text = "X" }).ToArray(),
            original.Width, true, original.MissingGlyphCount, original.InkBounds, original.DrawUnits);
        using SKBitmap expected = Draw(engine, original, options);
        using SKBitmap actual = Draw(engine, diagnostic, options);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public void 整形绘制单元放大后相邻字形仍在原位置()
    {
        using SkiaTextLayoutEngine engine = new();
        TextLayoutOptions options = new() { FontSizePx = 48, LetterSpacingPx = 20 };
        ShapedLine line = engine.Shape("请照亮", options);
        using SKBitmap original = Draw(engine, line, options);
        using SKBitmap enlarged = NewBitmap();
        using SKCanvas canvas = new(enlarged);
        canvas.Clear(SKColors.Transparent);
        engine.DrawLine(canvas, line, options, 20, 95, SKColors.White, drawUnitIndices: [0, 2]);
        ShapedDrawUnit middle = line.DrawUnits[1];
        float pivotX = 20 + middle.X + middle.Advance / 2;
        canvas.Save(); canvas.Translate(pivotX, 71); canvas.Scale(1.3f); canvas.Translate(-pivotX, -71);
        engine.DrawLine(canvas, line, options, 20, 95, SKColors.White, drawUnitIndices: [1]);
        canvas.Restore();
        Assert.NotEqual(original.Bytes, enlarged.Bytes);
        for (int y = 0; y < original.Height; y++)
            for (int x = 0; x < original.Width; x++)
                if (x < 20 + middle.X - 12 || x >= 20 + middle.X + middle.Advance + 12)
                    Assert.Equal(original.GetPixel(x, y), enlarged.GetPixel(x, y));
    }

    [Fact]
    public void 不完整文本关联拒绝而不是修正源切分()
    {
        using SkiaTextLayoutEngine engine = new();
        ShapedLine line = engine.Shape("请照亮", new());
        Assert.Throws<ArgumentException>(() => GlyphPartitioner.Partition(line, [new(0, 1), new(2, 1)]));
    }

    private static SKBitmap Draw(SkiaTextLayoutEngine engine, ShapedLine line, TextLayoutOptions options)
    {
        SKBitmap bitmap = NewBitmap();
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Transparent);
        engine.DrawLine(canvas, line, options, 20, 95, SKColors.White);
        return bitmap;
    }
    private static SKBitmap NewBitmap() => new(new SKImageInfo(520, 150, SKColorType.Bgra8888, SKAlphaType.Premul));
}
