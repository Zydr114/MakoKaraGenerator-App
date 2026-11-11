using System.Text.Json;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class TextFallbackCacheTests
{
    private static readonly TextLayoutOptions Options = new() { FamilyName = "DejaVu Sans", FontSizePx = 32f };

    [Fact]
    public void 共享布局缓存不跨引擎复用动态回退字体且几何像素一致()
    {
        const string text = "A夜星";
        var cache = new SkiaSceneGeometry.LayoutCache();
        var key = new SkiaSceneGeometry.LayoutKey(text, Options.FamilyName, Options.FontSizePx, Options.FontWeight,
            Options.IsBold, Options.IsItalic, Options.LetterSpacingPx, Options.FallbackFamilyNames);
        using var first = new SkiaTextLayoutEngine();
        using var second = new SkiaTextLayoutEngine();
        using var reference = new SkiaTextLayoutEngine();
        var firstLine = cache.GetOrAdd(key, first, text, Options);
        Assert.True(firstLine.UsedFallbackFont);
        var actual = cache.GetOrAdd(key, second, text, Options);
        var expected = reference.Shape(text, Options);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(Draw(reference, expected, Options), Draw(second, actual, Options));
        Assert.Same(actual, cache.GetOrAdd(key, second, text, Options));
    }

    [Fact]
    public void 显式回退字体布局仍可跨引擎共享并绘制一致()
    {
        const string text = "A夜星";
        using var discover = new SkiaTextLayoutEngine();
        var dynamicLine = discover.Shape(text, Options);
        Assert.True(dynamicLine.UsedFallbackFont);
        string family = dynamicLine.Elements.First(element => element.IsFallback).FamilyName;
        var options = Options with { FallbackFamilyNames = [family] };
        var key = new SkiaSceneGeometry.LayoutKey(text, options.FamilyName, options.FontSizePx, options.FontWeight,
            options.IsBold, options.IsItalic, options.LetterSpacingPx, options.FallbackFamilyNames);
        var cache = new SkiaSceneGeometry.LayoutCache();
        using var first = new SkiaTextLayoutEngine();
        using var second = new SkiaTextLayoutEngine();
        using var reference = new SkiaTextLayoutEngine();
        var shaped = cache.GetOrAdd(key, first, text, options);
        var actual = cache.GetOrAdd(key, second, text, options);
        var expected = reference.Shape(text, options);
        Assert.True(shaped.UsedFallbackFont);
        Assert.Same(shaped, actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(Draw(reference, expected, options), Draw(second, actual, options));
    }

    [Fact]
    public void 重复回退与缺字不重复调用系统查找且字形诊断像素不变()
    {
        var lookups = new NativeLookups();
        using var engine = lookups.CreateEngine();
        const string text = "夜👩‍🚀\uDBFF\uDFFF星";
        ShapedLine first = engine.Shape(text, Options);
        Assert.True(first.UsedFallbackFont);
        Assert.True(first.HasMissingGlyphs);
        int matches = lookups.Matches, opens = lookups.Opens;
        Assert.True(matches > 0);
        Assert.True(opens > 0);
        byte[] pixels = Draw(engine, first, Options);
        for (int index = 0; index < 10; index++)
        {
            ShapedLine repeated = engine.Shape(text, Options);
            Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
            Assert.Equal(pixels, Draw(engine, repeated, Options));
        }
        Assert.Equal(matches, lookups.Matches);
        Assert.Equal(opens, lookups.Opens);
        using var fresh = new SkiaTextLayoutEngine();
        Assert.Equal(pixels, Draw(fresh, fresh.Shape(text, Options), Options));
    }

    [Fact]
    public void 新元素匹配同一回退族时不重复打开已持有字体()
    {
        var lookups = new NativeLookups();
        using var engine = lookups.CreateEngine();
        ShapedLine first = engine.Shape("夜", Options);
        int matches = lookups.Matches, opens = lookups.Opens;
        Assert.True(first.UsedFallbackFont);
        Assert.True(opens > 0);
        ShapedLine second = engine.Shape("星", Options);
        Assert.True(second.UsedFallbackFont);
        Assert.Equal(first.Elements[0].FamilyName, second.Elements[0].FamilyName);
        Assert.True(lookups.Matches > matches);
        Assert.Equal(opens, lookups.Opens);
    }

    [Fact]
    public void 字号字重斜体及显式回退链独立并与新引擎结果相同()
    {
        var lookups = new NativeLookups();
        using var engine = lookups.CreateEngine();
        TextLayoutOptions[] variants = [Options, Options with { FontSizePx = 48f }, Options with { FontWeight = 700 },
            Options with { IsBold = true }, Options with { IsItalic = true },
            Options with { FallbackFamilyNames = ["Noto Sans CJK SC", "Noto Color Emoji"] }];
        foreach (TextLayoutOptions options in variants)
        {
            int opens = lookups.Opens;
            ShapedLine actual = engine.Shape("夜👩‍🚀", options);
            Assert.True(lookups.Opens > opens);
            using var fresh = new SkiaTextLayoutEngine();
            ShapedLine expected = fresh.Shape("夜👩‍🚀", options);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            Assert.Equal(Draw(fresh, expected, options), Draw(engine, actual, options));
        }
        int matchesAfterWarmup = lookups.Matches, opensAfterWarmup = lookups.Opens;
        foreach (TextLayoutOptions options in variants.Reverse()) engine.Shape("夜👩‍🚀", options);
        Assert.Equal(matchesAfterWarmup, lookups.Matches);
        Assert.Equal(opensAfterWarmup, lookups.Opens);
    }

    [Fact]
    public void 不同引擎不共享回退且释放后拒绝整形()
    {
        var lookups = new NativeLookups();
        using (var first = lookups.CreateEngine()) first.Shape("夜", Options);
        int before = lookups.Matches;
        var second = lookups.CreateEngine();
        second.Shape("夜", Options);
        Assert.True(lookups.Matches > before);
        second.Dispose();
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => second.Shape("夜", Options));
    }

    [Fact]
    public void 多种缺字超过缓存边界后重新匹配且仍报告缺字()
    {
        var lookups = new NativeLookups();
        using var engine = lookups.CreateEngine();
        string oldest = char.ConvertFromUtf32(0xF0000);
        ShapedLine expected = engine.Shape(oldest, Options);
        Assert.True(expected.HasMissingGlyphs);
        for (int index = 1; index <= 1040; index++) engine.Shape(char.ConvertFromUtf32(0xF0000 + index), Options);
        int matches = lookups.Matches;
        ShapedLine actual = engine.Shape(oldest, Options);
        Assert.True(lookups.Matches > matches);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        int after = lookups.Matches;
        engine.Shape(oldest, Options);
        Assert.Equal(after, lookups.Matches);
    }

    [Fact]
    public void 超长文本元素不常驻缓存且缺字结果一致()
    {
        var lookups = new NativeLookups();
        using var engine = lookups.CreateEngine();
        string text = char.ConvertFromUtf32(0xF0000) + new string('\u034F', 70);
        ShapedLine expected = engine.Shape(text, Options);
        Assert.Single(expected.Elements);
        int matches = lookups.Matches;
        ShapedLine actual = engine.Shape(text, Options);
        Assert.True(lookups.Matches > matches);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    [Fact]
    public void 缺失主字体通过可读取的真实默认字体整形并保持字形像素()
    {
        const string missing = "Font-I-O-Unavailable";
        using var engine = new SkiaTextLayoutEngine(
            (family, codepoint) => SKFontManager.Default.MatchCharacter(family, codepoint),
            (family, style) => family == missing ? null : SKFontManager.Default.MatchFamily(family, style));
        using var expected = new SkiaTextLayoutEngine();
        TextLayoutOptions requested = Options with { FamilyName = missing };
        TextLayoutOptions system = Options with { FamilyName = null };
        const string text = "朝の光👩‍🚀e\u0301";
        var actual = engine.Shape(text, requested);
        var baseline = expected.Shape(text, system);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(actual));
        Assert.Equal(Draw(expected, baseline, system), Draw(engine, actual, requested));
    }

    [Fact]
    public void 全部字体不可用时报告可重试错误而不把空字体交给整形器()
    {
        using var engine = new SkiaTextLayoutEngine((_, _) => null, (_, _) => null);
        var error = Assert.Throws<InvalidOperationException>(() => engine.Shape("朝の光", Options));
        Assert.Contains("无法读取字体数据", error.Message);
    }

    private static byte[] Draw(SkiaTextLayoutEngine engine, ShapedLine line, TextLayoutOptions options)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(400, 120, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        engine.DrawLine(canvas, line, options, 10, 80, SKColors.White);
        return bitmap.Bytes;
    }

    private sealed class NativeLookups
    {
        public int Matches { get; private set; }
        public int Opens { get; private set; }
        public SkiaTextLayoutEngine CreateEngine() => new(
            (family, codepoint) => { Matches++; return SKFontManager.Default.MatchCharacter(family, codepoint); },
            (family, style) => { Opens++; return SKTypeface.FromFamilyName(family, style); });
    }
}
