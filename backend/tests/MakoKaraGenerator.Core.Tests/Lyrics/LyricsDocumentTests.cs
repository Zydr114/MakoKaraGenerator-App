using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary><see cref="LyricsDocument"/> 的行管理、计数与结构相等性测试。</summary>
public sealed class LyricsDocumentTests
{
    private static LyricLine LineWithTokens(string lineId, string text, string tokenPrefix, long startUs, long endUs) =>
        LyricLine.Create(
            lineId,
            text,
            new TimeRange(startUs, endUs),
            TimingStatus.Edited,
            [
                LyricToken.Create(tokenPrefix + "-001", text[..1], new TimeRange(startUs, startUs + 100_000), TimingStatus.Confirmed),
                LyricToken.Create(tokenPrefix + "-002", text[1..], new TimeRange(startUs + 100_000, endUs), TimingStatus.Estimated),
            ]);

    [Fact]
    public void 空文档是合法的初始状态()
    {
        LyricsDocument document = LyricsDocument.Create();

        Assert.Equal(LyricsDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Empty(document.Lines);
        Assert.Equal(0, document.TokenCount);
        Assert.Null(document.Language);
    }

    [Fact]
    public void 文档统计所有行的元素总数()
    {
        LyricLine[] expectedLines =
        [
            LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000),
            LineWithTokens("line-002", "世界", "token-b", 1_000_000, 2_000_000),
        ];

        LyricsDocument document = LyricsDocument.Create(expectedLines, language: "zh-CN");

        Assert.Equal(expectedLines, document.Lines);
        Assert.Equal(4, document.TokenCount);
        Assert.Equal("zh-CN", document.Language);
    }

    [Fact]
    public void 枚举元素时同时给出所属行()
    {
        LyricsDocument document = LyricsDocument.Create(
        [
            LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000),
        ]);

        var pairs = document.EnumerateTokens().ToArray();
        string[] expectedTokenIds = ["token-a-001", "token-a-002"];

        Assert.Equal(expectedTokenIds, pairs.Select(static p => p.Token.Id));
        Assert.All(pairs, static p => Assert.Equal("line-001", p.Line.Id));
    }

    [Fact]
    public void 行标识重复被拒绝()
    {
        ImmutableArray<LyricLine> duplicated =
        [
            LyricLine.Create("line-001", "你好"),
            LyricLine.Create("line-001", "世界"),
        ];

        Assert.Throws<ArgumentException>(() => LyricsDocument.Create(duplicated));
    }

    [Fact]
    public void 行列表默认值被规范化为空序列()
    {
        LyricsDocument document = LyricsDocument.Create(
        [
            LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000),
        ]);

        LyricsDocument normalized = document with { Lines = default };

        Assert.Empty(normalized.Lines);
        Assert.Equal(0, normalized.TokenCount);
    }

    [Fact]
    public void 结构相等性比较行内容而不是数组引用()
    {
        LyricsDocument first = LyricsDocument.Create(
            [LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000)],
            "zh-CN");

        LyricsDocument second = LyricsDocument.Create(
            [LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000)],
            "zh-CN");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void 语言或版本不同则不相等()
    {
        LyricsDocument first = LyricsDocument.Create(
            [LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000)],
            "zh-CN");

        Assert.NotEqual(first, first with { Language = "ja-JP" });
        Assert.NotEqual(first, first with { SchemaVersion = 99 });
    }

    [Fact]
    public void 行内容不同则不相等()
    {
        LyricsDocument first = LyricsDocument.Create(
            [LineWithTokens("line-001", "你好", "token-a", 0, 1_000_000)]);

        LyricsDocument second = LyricsDocument.Create(
            [LineWithTokens("line-001", "你好", "token-a", 0, 1_500_000)]);

        Assert.NotEqual(first, second);
    }
}
