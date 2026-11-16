using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// 整行整形与布局测试（SPIKE-03）。
/// </summary>
/// <remarks>
/// <para>
/// 这些断言把"实测发现的 SkiaSharp API 陷阱"固化下来（docs/decisions.md ADR-0008）：
/// <c>SKShaper.Result.Codepoints</c> 装的是字形 ID，<c>Clusters</c> 是 UTF-8 字节偏移。
/// 如果日后有人"顺手"把实现改回按码点走位，这里的
/// <see cref="各类文本的元素边界都精确覆盖整行"/> 会立刻失败。
/// </para>
/// <para>
/// ⚠️ 测试依赖本机字体 <c>Noto Sans CJK SC</c> 与 <c>Noto Color Emoji</c>。
/// 与 docs/roadmap.md §15 的风险表一致：换到没有这些字体的机器上，布局会发生漂移。
/// 渲染金样测试（QA-02）必须在固定字体环境下运行。
/// </para>
/// </remarks>
public sealed class SkiaTextLayoutEngineTests
{
    private static readonly string PrimaryFamily = SkiaTextLayoutEngine.DefaultFamilyName;

    private const float DefaultFontSize = 72f;

    /// <summary>按给定族名与字号整形一行。</summary>
    private static ShapedLine Shape(
        SkiaTextLayoutEngine engine,
        string text,
        string? family = null,
        float fontSize = DefaultFontSize) =>
        engine.Shape(text, new TextLayoutOptions { FamilyName = family ?? PrimaryFamily, FontSizePx = fontSize });

    [Fact]
    public void 主字体能够被解析且覆盖简体中文()
    {
        using SkiaTextLayoutEngine shaper = new();

        Assert.Equal(PrimaryFamily, shaper.ResolveFamilyName(PrimaryFamily));
    }

    [Fact]
    public void 中文字号与字宽一一对应()
    {
        // 全角汉字在 72px 字号下的步进就是 72px，这是最容易手工验算的性质。
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "夜空中最亮的星");

        Assert.Equal(7, line.Elements.Count);
        Assert.Equal(504f, line.Width, 0.01f);

        for (int i = 0; i < line.Elements.Count; i++)
        {
            ShapedTextElement element = line.Elements[i];

            Assert.Equal(i * 72f, element.X, 0.01f);
            Assert.Equal(72f, element.Advance, 0.01f);
            Assert.Equal(i, element.Utf16Start);
        }
    }

    [Fact]
    public void 字距改变元素位置并保持整行终点一致()
    {
        using SkiaTextLayoutEngine shaper = new();
        TextLayoutOptions normal = new() { FamilyName = PrimaryFamily, FontSizePx = DefaultFontSize };
        ShapedLine plain = shaper.Shape("夜空中", normal);
        ShapedLine spaced = shaper.Shape("夜空中", normal with { LetterSpacingPx = 8f });

        Assert.Equal(plain.Width + 16f, spaced.Width, 0.01f);
        Assert.Equal(plain.Elements[0].X, spaced.Elements[0].X, 0.01f);
        Assert.Equal(plain.Elements[1].X + 8f, spaced.Elements[1].X, 0.01f);
        Assert.Equal(plain.Elements[2].X + 16f, spaced.Elements[2].X, 0.01f);
        Assert.Equal(spaced.Width, spaced.Elements[^1].XEnd, 0.01f);
    }

    [Fact]
    public void 字体诊断指出回退和缺字所在元素()
    {
        TextLayoutOptions options = new() { FamilyName = PrimaryFamily, FontSizePx = DefaultFontSize };
        FontInspection inspection = SkiaFontInspector.Inspect("夜👩\u200D🚀\uDBFF\uDFFF", options);

        Assert.Equal(PrimaryFamily, inspection.ResolvedFamily);
        Assert.Contains(inspection.Elements, entry => entry.ElementIndex == 1
            && entry.IsFallback && entry.ActualFamily.Contains("Emoji", StringComparison.Ordinal));
        Assert.Contains(inspection.Elements, entry => entry.ElementIndex == 2
            && entry.HasMissingGlyph && entry.Text == "\uDBFF\uDFFF");
        Assert.NotEmpty(inspection.AvailableWeights);
        FontInspection unavailable = SkiaFontInspector.Inspect("夜",
            options with { FamilyName = "This Font Does Not Exist 987654" });
        Assert.False(unavailable.RequestedFamilyAvailable);
        Assert.NotEmpty(unavailable.ResolvedFamily);
    }

    [Theory]
    [InlineData("夜空中最亮的星")]
    [InlineData("Hello, world!")]
    [InlineData("A中B文C")]
    [InlineData("👩‍🚀火箭🚀")]
    [InlineData("cafe\u0301 na\u0308ive")]
    [InlineData("한국어 かな カナ")]
    [InlineData("①Ⅷ♬※")]
    [InlineData("🏳️‍🌈 rainbow")]
    public void 各类文本的元素边界都精确覆盖整行(string text)
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, text);

        Assert.True(line.MappingIsExact, $"'{text}' 的元素边界没有精确覆盖整行。");

        int expected = 0;

        foreach (ShapedTextElement element in line.Elements)
        {
            Assert.Equal(expected, element.Utf16Start);
            Assert.True(element.Utf16Length > 0);
            Assert.Equal(text.Substring(element.Utf16Start, element.Utf16Length), element.Text);
            expected = element.Utf16End;
        }

        Assert.Equal(text.Length, expected);
    }

    [Theory]
    [InlineData("夜空中最亮的星")]
    [InlineData("Hello, world!")]
    [InlineData("👩‍🚀火箭🚀")]
    [InlineData("🏳️‍🌈 rainbow")]
    public void 元素的横向位置单调递增且与行宽一致(string text)
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, text);
        float previous = -1f;

        foreach (ShapedTextElement element in line.Elements)
        {
            Assert.True(element.X > previous, $"'{text}' 中元素 {element.Text} 的横向位置没有递增。");
            Assert.True(element.Advance > 0f);
            previous = element.X;
        }

        ShapedTextElement last = line.Elements[^1];
        Assert.Equal(line.Width, last.XEnd, 0.01f);
    }

    [Fact]
    public void 元素数量与文本元素切分结果一致()
    {
        // 这是"逐字"单位正确性的核心断言：
        // 元素数量必须等于 grapheme cluster 的数量，而不是 UTF-16 代码单元数量。
        using SkiaTextLayoutEngine shaper = new();

        const string text = "A中B👩‍🚀cafe\u0301한";

        ShapedLine line = Shape(shaper, text);
        IReadOnlyList<string> expectedElements = GraphemeSplitter.SplitToStrings(text);

        Assert.Equal(expectedElements.Count, line.Elements.Count);
        Assert.Equal(expectedElements, line.Elements.Select(static element => element.Text));
    }

    [Fact]
    public void emoji_会回退到彩色字体且仍算一个元素()
    {
        using SkiaTextLayoutEngine shaper = new();

        // 👩‍🚀 是 ZWJ 序列，5 个 UTF-16 代码单元。
        const string text = "👩\u200D🚀";

        ShapedLine line = Shape(shaper, text);

        ShapedTextElement element = Assert.Single(line.Elements);
        Assert.Equal(5, element.Utf16Length);
        Assert.Equal(text, element.Text);
        Assert.True(element.IsFallback, "emoji 应当由回退字体绘制。");
        Assert.Contains("Emoji", element.FamilyName, StringComparison.Ordinal);

        Assert.True(line.UsedFallbackFont);
    }

    [Fact]
    public void 显式字体回退链按声明的字体提供元素字体()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = shaper.Shape(
            "👩‍🚀",
            new TextLayoutOptions
            {
                FamilyName = PrimaryFamily,
                FallbackFamilyNames = ["Missing fallback 987654", TestFonts.Emoji],
                FontSizePx = DefaultFontSize,
            });

        ShapedTextElement element = Assert.Single(line.Elements);
        Assert.True(element.IsFallback);
        Assert.Equal(TestFonts.Emoji, element.FamilyName);
        Assert.False(line.HasMissingGlyphs);
    }

    [Fact]
    public void 回退字体的选择按文本元素而不是单码点()
    {
        // 同一行里汉字走主字体、emoji 走回退字体；若按"单个码点"决定字体，
        // ZWJ 序列会被拆到两个字体里，绘制结果就不是一个字形了。
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "夜👩\u200D🚀空");

        Assert.Equal(3, line.Elements.Count);
        Assert.False(line.Elements[0].IsFallback);
        Assert.False(line.Elements[2].IsFallback);
        Assert.True(line.Elements[1].IsFallback);
        Assert.Contains("Emoji", line.Elements[1].FamilyName, StringComparison.Ordinal);
        Assert.Equal(1, line.Elements[1].GlyphCount);
    }

    [Fact]
    public void 组合音标与基字符合成一个元素()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "cafe\u0301");

        Assert.Equal(4, line.Elements.Count);
        Assert.Equal("e\u0301", line.Elements[3].Text);
        Assert.Equal(2, line.Elements[3].Utf16Length);
        Assert.Equal(3, line.Elements[3].Utf16Start);
    }

    [Fact]
    public void 彩虹旗这类多码点序列只算一个元素()
    {
        using SkiaTextLayoutEngine shaper = new();

        // 🏳️‍🌈 = 🏳(2) + U+FE0F(1) + ZWJ(1) + 🌈(2) = 6 个 UTF-16 代码单元。
        const string text = "🏳️\u200D🌈";

        ShapedLine line = Shape(shaper, text);

        ShapedTextElement element = Assert.Single(line.Elements);
        Assert.Equal(6, element.Utf16Length);
        Assert.True(line.MappingIsExact);

        // HarfBuzz 会把整个 ZWJ 序列合成一个字形；若这里是 3 或 4，
        // 说明绘制与布局不一致（见 ADR-0010 里 DrawText 会重叠绘制的记录）。
        Assert.Equal(1, element.GlyphCount);
    }

    [Fact]
    public void 空行返回空布局()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, string.Empty);

        Assert.Empty(line.Elements);
        Assert.Equal(0f, line.Width);
        Assert.True(line.MappingIsExact);
        Assert.False(line.UsedFallbackFont);
    }

    [Fact]
    public void 可以按_UTF16_下标查询元素与位置()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "夜空中最亮");

        (int index, ShapedTextElement element) = line.ElementAt(2);

        Assert.Equal(2, index);
        Assert.Equal("中", element.Text);
        Assert.Equal(144f, line.XAt(2), 0.01f);

        // 元素内部插值：进度 0.5 时落在该元素中间。
        Assert.Equal(180f, line.XWithin(2, 0.5), 0.01f);
        Assert.Equal(144f, line.XWithin(2, -1), 0.01f);
        Assert.Equal(216f, line.XWithin(2, 2), 0.01f);
    }

    [Fact]
    public void 越界下标不会抛异常()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "夜空中最亮");

        Assert.Equal(-1, line.ElementAt(-1).Index);
        Assert.Equal(-1, line.ElementAt(99).Index);
        Assert.Equal(line.Width, line.XAt(99), 0.01f);
        Assert.Equal(line.Width, line.XWithin(99, 0.5), 0.01f);
    }

    [Fact]
    public void 字号会影响布局宽度()
    {
        using SkiaTextLayoutEngine engines = new();

        float smallWidth = Shape(engines, "夜空中最亮", fontSize: 24f).Width;
        float largeWidth = Shape(engines, "夜空中最亮", fontSize: 48f).Width;

        Assert.Equal(smallWidth * 2f, largeWidth, 0.05f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void 非正字号被拒绝(float fontSize)
    {
        using SkiaTextLayoutEngine engine = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => Shape(engine, "夜", fontSize: fontSize));
    }

    [Fact]
    public void 空引用被拒绝()
    {
        using SkiaTextLayoutEngine shaper = new();

        Assert.Throws<ArgumentNullException>(() => Shape(shaper, null!));
        Assert.Throws<ArgumentNullException>(() => shaper.Shape("夜", null!));
    }

    [Fact]
    public void 释放后不能再整形()
    {
        SkiaTextLayoutEngine shaper = new();
        shaper.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Shape(shaper, "夜"));

        // 重复释放应当是无害的。
        shaper.Dispose();
    }

    [Fact]
    public void 彩虹旗会回退到彩色字体并合成单个字形()
    {
        using SkiaTextLayoutEngine shaper = new();

        // 🏳️‍🌈 = 🏳(2) + VS16(1) + ZWJ(1) + 🌈(2)，必须由彩色 emoji 字体画成一个字形。
        const string text = "🏳\uFE0F\u200D🌈";

        ShapedLine line = Shape(shaper, text);

        ShapedTextElement element = Assert.Single(line.Elements);
        Assert.Equal(6, element.Utf16Length);
        Assert.True(element.IsFallback, "彩虹旗应由回退字体绘制。");
        Assert.Contains("Emoji", element.FamilyName, StringComparison.Ordinal);
        Assert.Equal(1, element.GlyphCount);
        Assert.Equal(0, line.MissingGlyphCount);
    }

    [Fact]
    public void 宇航员_ZWJ_序列合成单个字形()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "👩\u200D🚀");

        ShapedTextElement element = Assert.Single(line.Elements);
        Assert.True(element.IsFallback);
        Assert.Equal(1, element.GlyphCount);
        Assert.Equal(0, line.MissingGlyphCount);
    }

    [Theory]
    [InlineData("夜空中最亮的星")]
    [InlineData("Hello, world!")]
    [InlineData("🏳️‍🌈 emoji 与 cafe\u0301 混排")]
    [InlineData("👩\u200D🚀 发射")]
    [InlineData("한국어 かな カナ")]
    public void 常见文本不应出现缺字(string text)
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, text);

        Assert.False(line.HasMissingGlyphs, $"'{text}' 出现了 {line.MissingGlyphCount} 个缺字。");
    }

    [Fact]
    public void 缺字会被计数以便界面提示字体缺失()
    {
        using SkiaTextLayoutEngine shaper = new();

        // U+10FFFF 是 Unicode 允许的最大码点，永远不会被分配，
        // 因此永远不会有字体为它提供字形（比私用区更可靠：
        // 本机的 Nerd Font 就覆盖了大量私用区图标）。
        const string text = "夜\uDBFF\uDFFF空";

        ShapedLine line = Shape(shaper, text);

        Assert.True(line.HasMissingGlyphs, "U+10FFFF 应当被计为缺字。");
        Assert.True(line.MissingGlyphCount > 0);
    }

    [Fact]
    public void 未知字体族会回退到可用字体而不是失败()
    {
        using SkiaTextLayoutEngine shaper = new();

        ShapedLine line = Shape(shaper, "夜空中最亮", "这个字体族并不存在-Not-A-Real-Font");

        Assert.Equal(5, line.Elements.Count);
        Assert.True(line.MappingIsExact);
    }

    [Fact]
    public void 固定字体集合索引可跨平台枚举族名并完成整形()
    {
        byte[] bytes = MakoKaraGenerator.TestSupport.PackageFontFixture.FontBytes();
        IReadOnlyList<string> families = SkiaTextLayoutEngine.ReadFixedFontFamilies(bytes);
        Assert.Contains(MakoKaraGenerator.TestSupport.PackageFontFixture.Family, families);

        using SkiaTextLayoutEngine shaper = new([bytes]);
        ShapedLine line = shaper.Shape("歌词かし", new TextLayoutOptions
        {
            FamilyName = MakoKaraGenerator.TestSupport.PackageFontFixture.Family,
            FontSizePx = 40f,
        });
        Assert.Equal(4, line.Elements.Count);
        Assert.Equal(0, line.MissingGlyphCount);
        Assert.All(line.DrawUnits, unit => Assert.Equal(MakoKaraGenerator.TestSupport.PackageFontFixture.Family, unit.FamilyName));
    }
}
