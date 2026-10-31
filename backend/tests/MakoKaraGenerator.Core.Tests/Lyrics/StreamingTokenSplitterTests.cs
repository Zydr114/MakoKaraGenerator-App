using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Text;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class StreamingTokenSplitterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("漢字  e\u0301👨‍👩‍👧‍👦🇨🇳👩🏽‍🚀\t，。𠀀")]
    [InlineData("\r\n\u200d\0\ud800x\udc00")]
    public void 逐项文本元素范围与原数组API逐字节等价(string text)
    {
        Assert.Equal(GraphemeSplitter.Split(text), GraphemeSplitter.Enumerate(text));
        foreach (TokenSplitRule rule in Enum.GetValues<TokenSplitRule>())
        {
            int segments = 0;
            var observed = LyricTokenSplitter.Split(text, rule, () => segments++);
            Assert.Equal(LyricTokenSplitter.Split(text, rule).ToArray(), observed.ToArray());
            Assert.Equal(text, string.Concat(observed)); Assert.Equal(observed.Length, segments);
        }
    }

    [Fact]
    public void 真实段观察器在大量段建立前中止而大拉丁整词不按字符数误判()
    {
        int observed = 0;
        Assert.Throws<BudgetException>(() => LyricTokenSplitter.Split(new string('字', 100000), TokenSplitRule.PerTextElement,
            () => { if (++observed > 20) throw new BudgetException(); }));
        Assert.Equal(21, observed);
        observed = 0;
        string word = new('a', 100000);
        Assert.Equal(new[] { word }, LyricTokenSplitter.Split(word, TokenSplitRule.LatinWords, () => observed++).ToArray());
        Assert.Equal(1, observed);
    }

    private sealed class BudgetException : Exception;
}
