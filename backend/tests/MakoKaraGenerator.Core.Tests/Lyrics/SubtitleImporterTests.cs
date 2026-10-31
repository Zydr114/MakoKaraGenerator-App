using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// SRT 与 WebVTT 导入器的测试（P0 批次）。
/// </summary>
public sealed class SubtitleImporterTests
{
    static SubtitleImporterTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static LyricsImportResult Import(ILyricsImporter importer, string content, string fileName)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));

        return importer.Import(
            stream,
            new LyricsImportContext { FileName = fileName, MediaDurationUs = 60_000_000L });
    }

    [Fact]
    public void SRT_导入两块行级时间与未定时元素()
    {
        LyricsImportResult result = Import(
            new SrtImporter(),
            "1\r\n00:00:01,000 --> 00:00:03,000\r\n你好\r\n\r\n2\r\n00:00:04,500 --> 00:00:06,000\r\n世界\r\n",
            "a.srt");

        Assert.Equal(2, result.Document.Lines.Length);
        Assert.Equal(1_000_000L, result.Document.Lines[0].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, result.Document.Lines[0].Time!.Value.EndUs);
        Assert.Equal(4_500_000L, result.Document.Lines[1].Time!.Value.StartUs);
        Assert.Equal(2, result.Document.Lines[0].Tokens.Length);
        Assert.All(result.Document.Lines[0].Tokens, token => Assert.Null(token.Time));
        Assert.Equal(0, result.Report.TimedTokenCount);
        Assert.Equal(4, result.Report.MissingTokenCount);    }

    [Fact]
    public void SRT_剥掉行内标签并记进丢弃项()
    {
        LyricsImportResult result = Import(
            new SrtImporter(),
            "1\n00:00:01,000 --> 00:00:02,000\n<i>你好</i>\n",
            "a.srt");

        Assert.Equal("你好", result.Document.Lines[0].Text);
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("SRT", StringComparison.Ordinal));
    }

    [Fact]
    public void SRT_坏块只报告不中断()
    {
        LyricsImportResult result = Import(
            new SrtImporter(),
            "1\n00:00:01,000 --> 00:00:02,000\n第一句\n\n这一块没有时间行\n\n2\n00:00:03,000 --> 00:00:04,000\n第二句\n",
            "a.srt");

        Assert.Equal(2, result.Document.Lines.Length);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.MalformedTag);
    }

    [Fact]
    public void SRT_GB18030_被自动识别()
    {
        Encoding gb18030 = Encoding.GetEncoding("GB18030");
        using MemoryStream stream = new(gb18030.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n夜空中最亮的星\n"));

        LyricsImportResult result = new SrtImporter().Import(stream, new LyricsImportContext { FileName = "a.srt" });

        Assert.Equal("夜空中最亮的星", result.Document.Lines[0].Text);
        Assert.Equal("gb18030", result.Report.EncodingName);
    }

    [Fact]
    public void SRT_强制编码会覆盖自动检测()
    {
        Encoding utf8 = Encoding.UTF8;
        Encoding gb18030 = Encoding.GetEncoding("GB18030");
        using MemoryStream stream = new(gb18030.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n你好\n"));

        LyricsImportResult result = new SrtImporter().Import(
            stream,
            new LyricsImportContext { FileName = "a.srt", EncodingOverride = utf8 });

        Assert.Equal("utf-8", result.Report.EncodingName);
        Assert.NotEqual("你好", result.Document.Lines[0].Text);
    }

    [Fact]
    public void WebVTT_导入行级时间()
    {
        LyricsImportResult result = Import(new WebVttImporter(), "WEBVTT\n\n00:00.000 --> 00:02.000\n你好世界\n", "a.vtt");

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(2_000_000L, line.Time!.Value.EndUs);
        Assert.Equal(4, line.Tokens.Length);
        Assert.Equal(0, result.Report.TimedTokenCount);
    }

    [Fact]
    public void WebVTT_逐字标签给出元素时间()
    {
        LyricsImportResult result = Import(
            new WebVttImporter(),
            "WEBVTT\n\n00:00.000 --> 00:03.000\n<00:00.500>Hello <00:01.000>world\n",
            "a.vtt");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(500_000L, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(1_000_000L, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(1_000_000L, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, line.Tokens[1].Time!.Value.EndUs);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(2, result.Report.TimedTokenCount);
        Assert.Equal(TimingStatus.Imported, line.Tokens[0].TimingStatus);
    }

    [Fact]
    public void WebVTT_行内标签被剥掉且实体被还原()
    {
        LyricsImportResult result = Import(
            new WebVttImporter(),
            "WEBVTT\n\n00:00.000 --> 00:02.000\n<c.yellow>A &amp; B</c>\n",
            "a.vtt");

        Assert.Equal("A & B", result.Document.Lines[0].Text);
        Assert.NotEmpty(result.Report.DiscardedFeatures);
    }

    [Fact]
    public void WebVTT_注释块与标识行被跳过()
    {
        LyricsImportResult result = Import(
            new WebVttImporter(),
            "WEBVTT\n\nNOTE 这是注释\n不该进歌词\n\ncue-1\n00:00.000 --> 00:02.000\n正片\n",
            "a.vtt");

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal("正片", line.Text);
    }
}
