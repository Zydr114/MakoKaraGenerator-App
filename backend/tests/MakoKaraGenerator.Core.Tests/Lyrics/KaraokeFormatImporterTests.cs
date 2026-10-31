using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// ASS / UltraStar / TTML 导入器的测试（P1 批次）。
/// </summary>
public sealed class KaraokeFormatImporterTests
{
    private const string AssHeader =
        "[Script Info]\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    private static LyricsImportResult Import(ILyricsImporter importer, string content, string fileName)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));

        return importer.Import(
            stream,
            new LyricsImportContext { FileName = fileName, MediaDurationUs = 60_000_000L });
    }

    [Fact]
    public void ASS_卡拉OK标签给出逐字时间()
    {
        LyricsImportResult result = Import(
            new AssImporter(),
            AssHeader + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\k50}你{\\k50}好\n",
            "a.ass");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(1_000_000L, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(1_500_000L, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(1_500_000L, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(2_000_000L, line.Tokens[1].Time!.Value.EndUs);
        Assert.Equal(TimingStatus.Imported, line.Tokens[0].TimingStatus);
        Assert.Equal("你好", line.Text);
        Assert.Equal(2, result.Report.TimedTokenCount);
    }

    [Fact]
    public void ASS_没有卡拉OK标签时按行级导入并说明()
    {
        LyricsImportResult result = Import(
            new AssImporter(),
            AssHeader + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,你好\n",
            "a.ass");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(0, result.Report.TimedTokenCount);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.TextWithoutTimestamp);
    }

    [Fact]
    public void ASS_kt_与行内样式标签被记进丢弃项()
    {
        LyricsImportResult result = Import(
            new AssImporter(),
            AssHeader + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\pos(1,2)\\kt100}你好\n",
            "a.ass");

        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("\\kt", StringComparison.Ordinal));
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("样式", StringComparison.Ordinal));
    }

    [Fact]
    public void UltraStar_按BPM与GAP换算成秒()
    {
        LyricsImportResult result = Import(
            new UltraStarImporter(),
            "#TITLE:x\n#ARTIST:y\n#BPM:30\n#GAP:1000\n: 0 4 60 你\n: 4 2 62 好\n",
            "a.txt");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Equal("你好", line.Text);
        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(1_000_000L, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(3_000_000L, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(4_000_000L, line.Tokens[1].Time!.Value.EndUs);
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("音高", StringComparison.Ordinal));
    }

    [Fact]
    public void UltraStar_缺少BPM时用兜底值并报告()
    {
        LyricsImportResult result = Import(
            new UltraStarImporter(),
            "#TITLE:x\n#ARTIST:y\n: 0 4 60 你\n",
            "a.txt");

        Assert.Contains(result.Report.Issues, issue => issue.Kind == LyricsImportIssueKind.MalformedTag);
        Assert.Single(result.Document.Lines);
    }

    [Fact]
    public void UltraStar_横线分隔多行()
    {
        LyricsImportResult result = Import(
            new UltraStarImporter(),
            "#TITLE:x\n#ARTIST:y\n#BPM:30\n: 0 2 60 你\n: 2 2 60 好\n- 0 0 0 \n: 8 2 60 再\n: 10 2 60 见\n",
            "a.txt");

        Assert.Equal(2, result.Document.Lines.Length);
        Assert.Equal("你好", result.Document.Lines[0].Text);
        Assert.Equal("再见", result.Document.Lines[1].Text);
        Assert.Equal(2_000_000L, result.Document.Lines[0].Time!.Value.EndUs);
    }

    [Fact]
    public void TTML_钟表时间与音节()
    {
        LyricsImportResult result = Import(
            new TtmlImporter(),
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" mkg:timing-profile=\"amll-absolute\"><body><div>"
            + "<p begin=\"00:00:01.000\" end=\"00:00:03.000\">"
            + "<span begin=\"00:00:01.000\" end=\"00:00:02.000\">你</span>"
            + "<span begin=\"00:00:02.000\" end=\"00:00:03.000\">好</span>"
            + "</p></div></body></tt>",
            "a.ttml");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Equal("你好", line.Text);
        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(1_000_000L, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(2_000_000L, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, line.Tokens[1].Time!.Value.EndUs);
    }

    [Fact]
    public void TTML_和声被报告且第二条div保留()
    {
        LyricsImportResult result = Import(
            new TtmlImporter(),
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" mkg:timing-profile=\"amll-absolute\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><div>"
            + "<p begin=\"00:00:01.000\" end=\"00:00:03.000\">"
            + "<span begin=\"00:00:01.000\" end=\"00:00:02.000\">你</span>"
            + "<span ttm:role=\"x-bg\" begin=\"00:00:02.000\" end=\"00:00:03.000\">啊</span>"
            + "</p></div><div><p begin=\"00:00:01.000\" end=\"00:00:03.000\">翻译轨</p></div></body></tt>",
            "a.ttml");

        Assert.Equal(2, result.Document.Lines.Length);
        LyricLine line = result.Document.Lines[0];

        Assert.Equal("你", line.Text);
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("和声", StringComparison.Ordinal));
        Assert.Equal("翻译轨", result.Document.Lines[1].Text);
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("div", StringComparison.Ordinal));
    }

    [Fact]
    public void TTML_帧式时间按帧率换算()
    {
        LyricsImportResult result = Import(
            new TtmlImporter(),
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" mkg:timing-profile=\"amll-absolute\" xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" ttp:frameRate=\"30\">"
            + "<body><div><p begin=\"00:00:01:00\" end=\"00:00:02:00\">"
            + "<span begin=\"00:00:01:15\" end=\"00:00:01:29\">你</span>"
            + "</p></div></body></tt>",
            "a.ttml");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Single(line.Tokens);
        Assert.Equal(1_500_000L, line.Tokens[0].Time!.Value.StartUs);
    }

    [Fact]
    public void TTML_整段没有音节时间时按行级导入()
    {
        LyricsImportResult result = Import(
            new TtmlImporter(),
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" mkg:timing-profile=\"amll-absolute\"><body><div>"
            + "<p begin=\"00:00:01.000\" end=\"00:00:03.000\">你好</p>"
            + "</div></body></tt>",
            "a.ttml");

        LyricLine line = Assert.Single(result.Document.Lines);

        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(0, result.Report.TimedTokenCount);
    }
}
