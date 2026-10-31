using System.Text;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// <see cref="LyricsImporterRegistry" /> 与各导入器的测试。
/// </summary>
public sealed class LyricsImporterTests
{
    private static LyricsImportResult Import(string content, string fileName = "song.lrc", long? mediaDurationUs = null)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));

        return new LyricsImporterRegistry([new EnhancedLrcImporter(), new PlainLrcImporter()]).Import(
            stream,
            new LyricsImportContext { FileName = fileName, MediaDurationUs = mediaDurationUs });
    }

    [Fact]
    public void 普通LRC只给行级时间且元素时间为缺失()
    {
        const string Content = """
            [ti:测试]
            [00:01.00]夜空
            [00:05.00]前行
            """;

        LyricsImportResult result = Import(Content, mediaDurationUs: 8_000_000L);

        Assert.Equal(2, result.Document.Lines.Length);
        Assert.Equal("夜空", result.Document.Lines[0].Text);
        Assert.Equal(1_000_000L, result.Document.Lines[0].Time!.Value.StartUs);
        Assert.Equal(5_000_000L, result.Document.Lines[0].Time!.Value.EndUs);   // 由下一行起点推导
        Assert.Equal(TimingStatus.Imported, result.Document.Lines[0].TimingStatus);

        // 元素：按文本元素切分，时间全部缺失
        Assert.Equal(["夜", "空"], result.Document.Lines[0].Tokens.Select(t => t.Text));
        Assert.All(result.Document.Lines[0].Tokens, token =>
        {
            Assert.Null(token.Time);
            Assert.Equal(TimingStatus.Missing, token.TimingStatus);
        });

        // 最后一行用音频时长收尾
        Assert.Equal(8_000_000L, result.Document.Lines[1].Time!.Value.EndUs);

        Assert.Equal(2, result.Report.LineCount);
        Assert.Equal(4, result.Report.TokenCount);
        Assert.Equal(0, result.Report.TimedTokenCount);
        Assert.Equal(4, result.Report.MissingTokenCount);
        Assert.Equal(0, result.Report.EstimatedTokenCount);
        Assert.Equal(1, result.Report.DerivedLineEndCount);
        Assert.Equal(0, result.Report.FallbackLineEndCount);
    }

    [Fact]
    public void 元素文本拼接严格等于行文本()
    {
        const string Content = "[00:01.00]夜 空 中 café 🏳️‍🌈";

        LyricsImportResult result = Import(Content);

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(line.Text, string.Concat(line.Tokens.Select(token => token.Text)));
    }

    [Fact]
    public void 最后一行没有音频时长时使用兜底时长并记诊断()
    {
        const string Content = "[00:01.00]只有一行";

        LyricsImportResult result = Import(Content);

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(1_000_000L, line.Time!.Value.StartUs);
        Assert.Equal(5_000_000L, line.Time!.Value.EndUs);   // 兜底 4 秒
        Assert.Equal(1, result.Report.FallbackLineEndCount);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.LineEndFallback);
    }

    [Fact]
    public void 空文本行被跳过并计入报告()
    {
        const string Content = """
            [00:01.00]
            [00:05.00]有词
            """;

        LyricsImportResult result = Import(Content);

        Assert.Single(result.Document.Lines);
        Assert.Equal(1, result.Report.SkippedLineCount);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.EmptyText);
    }

    [Fact]
    public void 未知标签进入诊断且保留原文()
    {
        const string Content = """
            [unknowntag]
            [00:01.00]有词
            """;

        LyricsImportResult result = Import(Content);

        LyricsImportIssue issue = Assert.Single(result.Report.Issues, i => i.Kind == LyricsImportIssueKind.UnknownTag);
        Assert.Equal("[unknowntag]", issue.RawText);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void 增强LRC识别逐字标签并去掉标签文本()
    {
        const string Content =
            "[00:01.00]<00:01.00>夜<00:01.60>空中<00:02.40>最亮的<00:04.00>星";

        LyricsImportResult result = Import(Content, mediaDurationUs: 6_000_000L);

        LyricLine line = Assert.Single(result.Document.Lines);

        // 行文本必须不含逐字标签
        Assert.DoesNotContain('<', line.Text);
        Assert.Equal("夜空中最亮的星", line.Text);

        Assert.Equal(["夜", "空中", "最亮的", "星"], line.Tokens.Select(t => t.Text));
        Assert.Equal(1_000_000L, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(1_600_000L, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(1_600_000L, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(2_400_000L, line.Tokens[1].Time!.Value.EndUs);
        Assert.Equal(2_400_000L, line.Tokens[2].Time!.Value.StartUs);
        Assert.Equal(4_000_000L, line.Tokens[3].Time!.Value.StartUs);
        Assert.Equal(6_000_000L, line.Tokens[3].Time!.Value.EndUs);   // 最后一个元素接到行终点
        Assert.All(line.Tokens, token =>
        {
            Assert.Equal(TimingStatus.Imported, token.TimingStatus);
            Assert.NotNull(token.Time);
        });

        Assert.Equal(4, result.Report.TimedTokenCount);
        Assert.Equal(0, result.Report.MissingTokenCount);
    }

    [Fact]
    public void 增强LRC里的空白单独成元素且不带时间()
    {
        const string Content = "[00:01.00]<00:01.00>夜 <00:02.00>空中";

        LyricsImportResult result = Import(Content, mediaDurationUs: 4_000_000L);

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(["夜", " ", "空中"], line.Tokens.Select(t => t.Text));
        Assert.Null(line.Tokens[1].Time);
        Assert.Equal(TimingStatus.Missing, line.Tokens[1].TimingStatus);
        Assert.Equal(line.Text, string.Concat(line.Tokens.Select(t => t.Text)));
        Assert.Equal(2, result.Report.TimedTokenCount);
    }

    [Fact]
    public void 增强LRC关闭空白分离时空白并入前一个元素()
    {
        const string Content = "[00:01.00]<00:01.00>夜 <00:02.00>空中";

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Content));
        LyricsImportResult result = new LyricsImporterRegistry(
            [new EnhancedLrcImporter(wordOptions: new EnhancedLrcOptions { WhitespaceIsUntimed = false })]).Import(
            stream,
            new LyricsImportContext { FileName = "song.lrc", MediaDurationUs = 4_000_000L });

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(["夜 ", "空中"], line.Tokens.Select(t => t.Text));
        Assert.Equal(line.Text, string.Concat(line.Tokens.Select(t => t.Text)));
    }

    [Fact]
    public void 增强LRC的逐字时间越出所属行时被钳制并记诊断()
    {
        // 行起点 00:05.00，但逐字标签写 00:01.00（比行起点早）
        const string Content = "[00:05.00]<00:01.00>夜<00:06.00>空[00:09.00]下一行";

        LyricsImportResult result = Import(Content, mediaDurationUs: 12_000_000L);

        LyricLine line = result.Document.Lines[0];
        Assert.Equal(5_000_000L, line.Tokens[0].Time!.Value.StartUs);   // 钳制到行起点
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.WordTimeOutsideLine);
    }

    [Fact]
    public void 增强LRC的逐字时间不递增时顺延并记诊断()
    {
        const string Content = "[00:01.00]<00:03.00>夜<00:02.00>空<00:04.00>星";

        LyricsImportResult result = Import(Content, mediaDurationUs: 6_000_000L);

        LyricLine line = result.Document.Lines[0];

        // 夜 保留 3.0 秒起；被顺延后与它起点相同的 空 无法得到非空区间，退化为"时间缺失"
        Assert.Equal(3_000_000L, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(4_000_000L, line.Tokens[0].Time!.Value.EndUs);
        Assert.Null(line.Tokens[1].Time);
        Assert.Equal(4_000_000L, line.Tokens[2].Time!.Value.StartUs);

        // 两处问题都要被记录：顺延一次、退化为缺失一次
        Assert.Equal(2, result.Report.Issues.Count(issue => issue.Kind == LyricsImportIssueKind.WordTimesNotIncreasing));
    }

    [Fact]
    public void 增强LRC的逐字时间越过行终点时退化为缺失()
    {
        // 行 [01.00, 02.00)，第二个元素写在 05.00（已在行外）
        const string Content = "[00:01.00]<00:01.00>夜<00:05.00>空[00:02.00]下一行";

        LyricsImportResult result = Import(Content);

        LyricLine line = result.Document.Lines[0];
        Assert.NotNull(line.Tokens[0].Time);
        Assert.Null(line.Tokens[1].Time);
        Assert.Equal(TimingStatus.Missing, line.Tokens[1].TimingStatus);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.WordTimeOutsideLine);
    }

    [Fact]
    public void 增强LRC里无法识别的标签保留原文并记诊断()
    {
        const string Content = "[00:01.00]<abc>夜<00:02.00>空[00:03.00]下一行";

        LyricsImportResult result = Import(Content);

        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.MalformedTag);
        Assert.Contains("<abc>", result.Document.Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有逐字标签的增强文件退化成纯行级()
    {
        const string Content = "[00:01.00]夜空[00:05.00]前行";

        LyricsImportResult result = Import(Content);

        Assert.All(result.Document.Lines.SelectMany(line => line.Tokens), token => Assert.Null(token.Time));
        Assert.Equal(0, result.Report.TimedTokenCount);
    }

    [Fact]
    public void 注册表按文件头选择增强导入器()
    {
        const string Content = "[00:01.00]<00:01.00>夜<00:02.00>空";

        LyricsImportResult result = Import(Content, "song.lrc");

        Assert.Equal(EnhancedLrcImporter.IdValue, result.Report.ImporterId);
    }

    [Fact]
    public void 注册表对纯行级文件选择普通导入器()
    {
        const string Content = "[00:01.00]夜空";

        LyricsImportResult result = Import(Content, "song.lrc");

        Assert.Equal(PlainLrcImporter.IdValue, result.Report.ImporterId);
    }

    [Fact]
    public void 注册表无法识别时抛出明确异常()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("这不是歌词"));

        // .srt 既不是 .lrc/.txt，内容里也没有 LRC 时间标签
        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            new LyricsImporterRegistry([new EnhancedLrcImporter(), new PlainLrcImporter()]).Import(
                stream,
                new LyricsImportContext { FileName = "movie.srt" }));

        Assert.Contains("movie.srt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 导入的行顺序与时间顺序一致()
    {
        const string Content = """
            [00:09.00]后
            [00:01.00]前
            [00:05.00]中
            """;

        LyricsImportResult result = Import(Content);

        Assert.Equal(["前", "中", "后"], result.Document.Lines.Select(line => line.Text));
        Assert.Equal(LyricsImportIssueKind.OutOfOrder, Assert.Single(
            result.Report.Issues,
            issue => issue.Kind == LyricsImportIssueKind.OutOfOrder).Kind);
    }

    [Fact]
    public void 标识在行与元素之间不重复()
    {
        const string Content = """
            [00:01.00]甲乙
            [00:02.00]丙丁
            """;

        LyricsImportResult result = Import(Content);

        List<string> ids = [.. result.Document.Lines.Select(line => line.Id)];
        ids.AddRange(result.Document.Lines.SelectMany(line => line.Tokens).Select(token => token.Id));

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 导入结果可通过项目校验且缺失时间只是提示()
    {
        const string Content = """
            [00:01.00]夜空
            [00:05.00]前行
            """;

        LyricsImportResult result = Import(Content, mediaDurationUs: 8_000_000L);

        // 时间缺失在"打开"语境下是提示，不是错误：导入后立刻可用。
        Assert.All(result.Document.Lines.SelectMany(line => line.Tokens), token => Assert.Null(token.Time));
        Assert.NotNull(result.Document.Lines[0].Time);
    }

    [Fact]
    public void 报告排版包含关键信息()
    {
        const string Content = """
            [00:01.00]<00:01.00>夜
            [00:05.00]前行
            """;

        LyricsImportResult result = Import(Content, mediaDurationUs: 8_000_000L);
        string text = LyricsImportReportFormatter.Format(result.Report);

        Assert.Contains("增强 LRC", text, StringComparison.Ordinal);
        Assert.Contains("歌词 2 行", text, StringComparison.Ordinal);
        Assert.Contains("逐字时间", text, StringComparison.Ordinal);
        Assert.Contains("编码：utf-8", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 报告排版在问题很多时截断()
    {
        StringBuilder builder = new();

        for (int i = 0; i < 30; i++)
        {
            builder.Append("[weird").Append(i).Append("]\n");
        }

        builder.Append("[00:01.00]有词");

        LyricsImportResult result = Import(builder.ToString());
        string text = LyricsImportReportFormatter.Format(result.Report, maxIssues: 3);

        Assert.Contains("另有", text, StringComparison.Ordinal);
    }
}
