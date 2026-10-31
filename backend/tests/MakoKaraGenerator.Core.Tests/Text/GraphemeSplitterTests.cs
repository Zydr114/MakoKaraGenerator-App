using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Tests.Text;

/// <summary>
/// 文本元素切分测试（docs/development.md §5.2：逐字按文本元素而不是 UTF-16 char）。
/// </summary>
public sealed class GraphemeSplitterTests
{
    [Fact]
    public void 空字符串没有元素()
    {
        Assert.Empty(GraphemeSplitter.Split(string.Empty));
        Assert.Empty(GraphemeSplitter.SplitToStrings(string.Empty));
    }

    [Fact]
    public void 汉字与拉丁字母各占一个元素()
    {
        IReadOnlyList<TextElementRange> cjk = GraphemeSplitter.Split("夜空中最亮");

        Assert.Equal(5, cjk.Count);
        Assert.All(cjk, static element => Assert.Equal(1, element.Length));

        IReadOnlyList<TextElementRange> latin = GraphemeSplitter.Split("Hello");

        Assert.Equal(5, latin.Count);
        Assert.All(latin, static element => Assert.Equal(1, element.Length));
    }

    [Fact]
    public void 星平面字符不会被拆成两个代理项()
    {
        // 🚀 是 U+1F680，UTF-16 里是 2 个代码单元。按 char 切分会把它拆坏。
        IReadOnlyList<TextElementRange> elements = GraphemeSplitter.Split("🚀");

        TextElementRange element = Assert.Single(elements);
        Assert.Equal(2, element.Length);
        Assert.Equal("🚀", element.Text);
    }

    [Fact]
    public void 组合音标与其基字符合为一个元素()
    {
        // "café" 用 e + U+0301 组合尖音符表示：字符长度是 2，但只有一个文本元素。
        const string text = "cafe\u0301";

        IReadOnlyList<TextElementRange> elements = GraphemeSplitter.Split(text);

        Assert.Equal(4, elements.Count);
        Assert.Equal("e\u0301", elements[3].Text);
        Assert.Equal(2, elements[3].Length);
        Assert.Equal(3, elements[3].Start);
    }

    [Fact]
    public void ZWJ_序列算一个元素()
    {
        // 👩‍🚀 = 👩(2) + ZWJ(1) + 🚀(2) = 5 个 UTF-16 代码单元。
        const string text = "👩\u200D🚀";

        TextElementRange element = Assert.Single(GraphemeSplitter.Split(text));
        Assert.Equal(5, element.Length);
        Assert.Equal(text, element.Text);
    }

    [Fact]
    public void 元素位置连续且覆盖整个字符串()
    {
        const string text = "A中B👩‍🚀cafe\u0301한";

        IReadOnlyList<TextElementRange> elements = GraphemeSplitter.Split(text);
        int expected = 0;

        foreach (TextElementRange element in elements)
        {
            Assert.Equal(expected, element.Start);
            Assert.Equal(text.Substring(element.Start, element.Length), element.Text);
            expected = element.End;
        }

        Assert.Equal(text.Length, expected);
    }

    [Theory]
    [InlineData("夜空中最亮", 5)]
    [InlineData("Hello, world!", 13)]
    [InlineData("👩‍🚀火箭🚀", 4)]
    [InlineData("cafe\u0301 na\u0308ive", 10)]
    [InlineData("한국어 かな カナ", 9)]
    [InlineData("①Ⅷ♬※", 4)]
    public void 各类脚本的元素数量符合预期(string text, int expectedCount)
    {
        Assert.Equal(expectedCount, GraphemeSplitter.Split(text).Count);
    }

    [Fact]
    public void 空引用被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => GraphemeSplitter.Split(null!));
        Assert.Throws<ArgumentNullException>(() => GraphemeSplitter.SplitToStrings(null!));
    }
}
