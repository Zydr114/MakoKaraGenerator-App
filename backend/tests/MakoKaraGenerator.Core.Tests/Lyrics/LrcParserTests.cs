using MakoKaraGenerator.Core.Lyrics.Lrc;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// <see cref="LrcParser" /> 与 <see cref="LrcTimeTagParser" /> 的测试。
/// </summary>
public sealed class LrcParserTests
{
    [Theory]
    [InlineData("00:00.00", 0L)]
    [InlineData("00:12.34", 12_340_000L)]
    [InlineData("00:12.3", 12_300_000L)]
    [InlineData("00:12.345", 12_345_000L)]
    [InlineData("01:02", 62_000_000L)]
    [InlineData("102:03.50", 6_123_500_000L)]
    [InlineData("01:02:50", 62_500_000L)]   // 末段冒号 = 百分秒（部分工具的输出）
    public void 时间标签按常见写法解析(string tag, long expected)
    {
        Assert.True(LrcTimeTagParser.TryParse(tag, out long actual), $"应能解析 '{tag}'");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("00:60.00")]    // 秒越界
    [InlineData("00:12.3456")]  // 小数超过三位
    [InlineData("00:12.")]      // 有点号但没有小数位
    [InlineData(":12")]         // 缺分钟
    [InlineData("00:")]         // 缺秒
    [InlineData("00:12:5")]     // 百分秒必须是两位
    [InlineData("abc")]
    [InlineData("-00:01")]
    [InlineData("00-12")]
    public void 无法识别的时间标签一律拒绝(string tag)
    {
        Assert.False(LrcTimeTagParser.TryParse(tag, out long actual));
        Assert.Equal(0L, actual);
    }

    [Fact]
    public void 解析普通歌词与元数据()
    {
        const string Text = """
            [ti:测试歌曲]
            [ar:某位歌手]
            [00:01.00]第一行
            [00:03.50]第二行
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(2, document.Entries.Count);
        Assert.Equal(1_000_000L, document.Entries[0].StartUs);
        Assert.Equal("第一行", document.Entries[0].Text);
        Assert.Equal(3_500_000L, document.Entries[1].StartUs);
        Assert.Equal("测试歌曲", document.GetMetadata("ti"));
        Assert.Equal("某位歌手", document.GetMetadata("ar"));
        Assert.Equal(4, document.SourceLineCount);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void 同一行多个时间标签会扩展成多条歌词()
    {
        const string Text = "[00:01.00][00:05.00][00:09.00]副歌";

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(3, document.Entries.Count);
        Assert.All(document.Entries, entry => Assert.Equal("副歌", entry.Text));
        Assert.All(document.Entries, entry => Assert.Equal(3, entry.TagCount));
        Assert.Equal([1_000_000L, 5_000_000L, 9_000_000L], document.Entries.Select(e => e.StartUs));
    }

    [Fact]
    public void 无时间标签的文本行接到上一条()
    {
        const string Text = """
            [00:01.00]第一句
            后半句
            [00:05.00]第二句
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(2, document.Entries.Count);
        Assert.Equal("第一句\n后半句", document.Entries[0].Text);
        Assert.True(document.Entries[0].IsContinuation);
    }

    [Fact]
    public void 关闭续行合并时无标签文本行记为诊断()
    {
        const string Text = """
            [00:01.00]第一句
            后半句
            """;

        LrcDocument document = LrcParser.Parse(Text, new LrcParserOptions { MergeContinuationLines = false });

        Assert.Single(document.Entries);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.TextWithoutTimestamp, diagnostic.Kind);
        Assert.Equal(2, diagnostic.SourceLineNumber);
        Assert.Contains("后半句", diagnostic.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void 只有时间标签没有文本的行被跳过并记诊断()
    {
        const string Text = """
            [00:01.00]
            [00:05.00]有词
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Single(document.Entries);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.EmptyText, diagnostic.Kind);
        Assert.Equal(1, diagnostic.SourceLineNumber);
    }

    [Fact]
    public void 无法识别的标签保留原文并记诊断()
    {
        const string Text = """
            [foobar]
            [00:01.00]有词
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Single(document.Entries);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.UnknownTag, diagnostic.Kind);
        Assert.Equal("[foobar]", diagnostic.RawText);
        Assert.Equal(1, diagnostic.SourceLineNumber);
    }

    [Fact]
    public void 未知标签重复出现时只记一条诊断()
    {
        const string Text = """
            [weird]一句
            [weird]二句
            [00:01.00]有词
            """;

        LrcDocument document = LrcParser.Parse(Text);

        // 两行都是未知标签 + 无时间标签的文本，诊断里既有 UnknownTag 也有 TextWithoutTimestamp，
        // 但 UnknownTag 对同一个标签只应出现一次。
        Assert.Single(document.Diagnostics, d => d.Kind == LrcDiagnosticKind.UnknownTag);
    }

    [Fact]
    public void 重复时间戳会被记录但仍全部保留()
    {
        const string Text = """
            [00:01.00]甲
            [00:01.00]乙
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(2, document.Entries.Count);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.DuplicateTimestamp, diagnostic.Kind);
        Assert.Equal(2, diagnostic.SourceLineNumber);
    }

    [Fact]
    public void 乱序时间会重排并记诊断()
    {
        const string Text = """
            [00:09.00]后
            [00:01.00]前
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal("前", document.Entries[0].Text);
        Assert.Equal("后", document.Entries[1].Text);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.OutOfOrder, diagnostic.Kind);
        Assert.Equal(2, diagnostic.SourceLineNumber);
    }

    [Fact]
    public void 关闭重排时保留原始顺序()
    {
        const string Text = """
            [00:09.00]后
            [00:01.00]前
            """;

        LrcDocument document = LrcParser.Parse(Text, new LrcParserOptions { SortByTime = false });

        Assert.Equal("后", document.Entries[0].Text);
        Assert.Equal("前", document.Entries[1].Text);
        Assert.Equal(LrcDiagnosticKind.OutOfOrder, Assert.Single(document.Diagnostics).Kind);
    }

    [Fact]
    public void 同一时间戳的多条歌词在重排后保持原有先后()
    {
        const string Text = """
            [00:05.00]乙
            [00:01.00]丙
            [00:05.00]甲
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(["丙", "乙", "甲"], document.Entries.Select(e => e.Text));
    }

    [Fact]
    public void offset标签会整体平移时间()
    {
        const string Text = """
            [offset:+500]
            [00:01.00]甲
            [00:02.00]乙
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(500_000L, document.OffsetUs);
        Assert.Equal(1_500_000L, document.Entries[0].StartUs);
        Assert.Equal(2_500_000L, document.Entries[1].StartUs);
    }

    [Fact]
    public void 负offset把时间推到负数时钳制到零并记诊断()
    {
        const string Text = """
            [offset:-2000]
            [00:01.00]甲
            [00:04.00]乙
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(0L, document.Entries[0].StartUs);
        Assert.Equal(2_000_000L, document.Entries[1].StartUs);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.NegativeTimeClamped, diagnostic.Kind);
    }

    [Fact]
    public void 关闭offset应用时时间保持文件原值()
    {
        const string Text = """
            [offset:+500]
            [00:01.00]甲
            """;

        LrcDocument document = LrcParser.Parse(Text, new LrcParserOptions { ApplyOffset = false });

        Assert.Equal(500_000L, document.OffsetUs);
        Assert.Equal(1_000_000L, document.Entries[0].StartUs);
    }

    [Fact]
    public void offset值无法解析时记诊断()
    {
        const string Text = """
            [offset:abc]
            [00:01.00]甲
            """;

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal(0L, document.OffsetUs);
        LrcDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(LrcDiagnosticKind.MalformedMetadata, diagnostic.Kind);
        Assert.Equal("[offset:abc]", diagnostic.RawText);
    }

    [Fact]
    public void 方括号没闭合时记为未知标签且不崩溃()
    {
        const string Text = "[00:01.00还没闭合";

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Empty(document.Entries);
        Assert.Equal(LrcDiagnosticKind.UnknownTag, Assert.Single(document.Diagnostics).Kind);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("a\nb\nc", 3)]
    [InlineData("a\rb\rc", 3)]
    [InlineData("a\r\nb\nc\rd", 4)]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    public void 换行符切分兼容三种写法(string text, int expectedLines)
    {
        Assert.Equal(expectedLines, LrcParser.SplitLines(text).Count);
    }

    [Fact]
    public void 空文件不会产生条目()
    {
        LrcDocument document = LrcParser.Parse(string.Empty);

        Assert.Empty(document.Entries);
        Assert.Empty(document.Diagnostics);
        Assert.Equal(0L, document.OffsetUs);
    }

    [Fact]
    public void 时间标签前后的空白会被忽略()
    {
        const string Text = "   [00:01.00]   甲   ";

        LrcDocument document = LrcParser.Parse(Text);

        Assert.Equal("甲", Assert.Single(document.Entries).Text);
    }
}
