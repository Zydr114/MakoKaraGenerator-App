using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests;

public sealed class PlainTextImporterTests
{
    [Theory]
    [InlineData("星空\r\n\r\n歌声👩‍🚀", 2)]
    [InlineData("  保留留白  \n翻译", 2)]
    [InlineData(" \r\n", 0)]
    public void 纯文本导入保留文字Unicode与真实缺失时间(string text, int count)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var result = LyricsImporterRegistry.CreateDefault().Import(stream, new() { FileName = "lyrics.txt", MediaDurationUs = 12_000_000 });
        Assert.Equal("text.plain", result.Report.ImporterId);
        Assert.Equal(count, result.Document.Lines.Length);
        Assert.All(result.Document.Lines, line =>
        {
            Assert.Null(line.Time); Assert.Equal(TimingStatus.Missing, line.TimingStatus);
            Assert.Equal(line.Text, string.Concat(line.Tokens.Select(token => token.Text)));
            Assert.All(line.Tokens, token => { Assert.Null(token.Time); Assert.Equal(TimingStatus.Missing, token.TimingStatus); });
        });
        Assert.Equal(result.Report.TokenCount, result.Report.MissingTokenCount);
        Assert.Equal(0, result.Report.DerivedLineEndCount);
        Assert.Equal(0, result.Report.EstimatedTokenCount);
    }
    [Theory]
    [InlineData("[00:01.00]歌词", "lrc.plain")]
    [InlineData("[00:01.00]<00:01.00>歌<00:01.50>词", "lrc.enhanced")]
    [InlineData("#TITLE:Song\n#ARTIST:Test\n#BPM:120\n: 0 4 60 lyric\nE", "ustar")]
    public void 时间歌词和UltraStar保持原有格式优先级(string text, string importer)
    {
        var result = LyricsImporterRegistry.CreateDefault().Detect("lyrics.txt", Encoding.UTF8.GetBytes(text));
        Assert.Equal(importer, result!.Importer.Id);
    }
}
