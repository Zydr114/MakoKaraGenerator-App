using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// 文本元素与默认可忽略字符的判断测试。
/// </summary>
/// <remarks>
/// 这组测试是 docs/decisions.md ADR-0010 的回归防线：
/// 一旦有人把字体回退判定改回 <c>SKTypeface.ContainsGlyphs</c>，
/// <see cref="变体选择符会让库的整体判定误判为不覆盖" /> 会立刻失败。
/// </remarks>
public sealed class TextElementRulesTests
{
    [Theory]
    [InlineData(0x200D, true)]   // ZWJ
    [InlineData(0x200C, true)]   // ZWNJ
    [InlineData(0xFE0F, true)]   // 变体选择符 VS16（emoji 呈现）
    [InlineData(0xFE0E, true)]   // 变体选择符 VS15（文本呈现）
    [InlineData(0x200B, true)]   // 零宽空格
    [InlineData(0xFEFF, true)]   // BOM / 零宽不换行空格
    [InlineData(0x2060, true)]   // 词连接符
    [InlineData(0xE0100, true)]  // 补充变体选择符
    [InlineData(0x0030, false)]  // '0'
    [InlineData(0x0059, false)]  // 'Y'
    [InlineData(0x591C, false)]  // 夜
    [InlineData(0x1F308, false)] // 🌈
    [InlineData(0x0301, false)]  // 组合尖音符（需要字形，不能忽略）
    public void 默认可忽略字符的判定(int codepoint, bool expected)
    {
        Assert.Equal(expected, TextElementRules.IsDefaultIgnorable(codepoint));
    }

    [Fact]
    public void 变体选择符会让库的整体判定误判为不覆盖()
    {
        // 实测现象：彩虹旗里的 U+FE0F 在任何字体里都没有字形（glyph id 为 0），
        // 因此 ContainsGlyphs（"所有码点都要有字形"）会把真正能画它的
        // Noto Color Emoji 判成不覆盖，导致回退逻辑退到中文字体并画出豆腐块。
        string flag = OperatingSystem.IsWindows() ? "🏴\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F" : "🏳\uFE0F\u200D🌈";

        using SkiaSharp.SKTypeface emojiTypeface = SkiaSharp.SKTypeface.FromFamilyName(TestFonts.Emoji);

        Assert.NotNull(emojiTypeface);
        Assert.False(emojiTypeface.ContainsGlyphs(flag), "前提变了：库的判定行为已改变，请复核 ADR-0010。");
        Assert.True(
            SkiaTypefaceCoverage.IsElementCoveredBy(emojiTypeface, flag),
            "忽略默认可忽略字符后，Emoji 字体应当被判为覆盖含可忽略码点的序列。");
    }

    [Fact]
    public void 中文字体不覆盖_emoji_但覆盖汉字()
    {
        using SkiaSharp.SKTypeface cjk = SkiaSharp.SKTypeface.FromFamilyName(SkiaTextLayoutEngine.DefaultFamilyName);

        Assert.NotNull(cjk);
        Assert.True(SkiaTypefaceCoverage.IsElementCoveredBy(cjk, "夜空"));
        Assert.False(SkiaTypefaceCoverage.IsElementCoveredBy(cjk, "🌈"));
    }

    [Fact]
    public void 码点枚举能正确处理代理对()
    {
        Assert.Equal([0x1F3F3, 0xFE0F, 0x200D, 0x1F308], TextElementRules.EnumerateCodepoints("🏳\uFE0F\u200D🌈"));
        Assert.Equal([0x591C, 0x7A7A], TextElementRules.EnumerateCodepoints("夜空"));
        Assert.Empty(TextElementRules.EnumerateCodepoints(string.Empty));
    }

    [Fact]
    public void 空引用被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => TextElementRules.EnumerateCodepoints(null!));
        Assert.Throws<ArgumentNullException>(
            () => SkiaTypefaceCoverage.IsElementCoveredBy(null!, "夜"));
        Assert.Throws<ArgumentNullException>(
            () => SkiaTypefaceCoverage.IsElementCoveredBy(SkiaSharp.SKTypeface.Default, null!));
    }
}
