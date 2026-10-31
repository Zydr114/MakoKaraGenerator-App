using System.Text;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// 格式目录与三层识别的测试（ADR-0046）。
/// </summary>
public sealed class LyricsFormatDetectionTests
{
    private static readonly LyricsImporterRegistry Registry = LyricsImporterRegistry.CreateDefault();

    private static LyricsImportDetection? Detect(string content, string fileName)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        return Registry.Detect(fileName, bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
    }

    [Fact]
    public void 同一个_lrc_含逐字标签时选增强并说明还有别的候选()
    {
        LyricsImportDetection? detection = Detect("[00:01.00]<00:01.00>你<00:01.50>好\n", "song.lrc");

        Assert.NotNull(detection);
        Assert.Equal(EnhancedLrcImporter.IdValue, detection!.Importer.Id);
        Assert.True(detection.IsAmbiguous);
        Assert.Equal(2, detection.Matches.Length);
        Assert.Contains("<mm:ss.xx>", detection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 同一个_lrc_不含逐字标签时选普通且没有歧义()
    {
        LyricsImportDetection? detection = Detect("[00:01.00]你好\n", "song.lrc");

        Assert.NotNull(detection);
        Assert.Equal(PlainLrcImporter.IdValue, detection!.Importer.Id);
        Assert.False(detection.IsAmbiguous);
    }

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n你好\n", "song.srt", SrtImporter.IdValue)]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:01.000\n你好\n", "song.vtt", WebVttImporter.IdValue)]
    [InlineData("[Script Info]\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,试唱\n", "song.ass", AssImporter.IdValue)]
    [InlineData("#TITLE:x\n#BPM:30\n: 0 4 60 你\n", "song.txt", UltraStarImporter.IdValue)]
    [InlineData("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\" end=\"00:00:02.000\">你好</p></div></body></tt>", "song.ttml", TtmlImporter.IdValue)]
    public void 按扩展名与内容识别到对应导入器(string content, string fileName, string expectedId)
    {
        LyricsImportDetection? detection = Detect(content, fileName);

        Assert.NotNull(detection);
        Assert.Equal(expectedId, detection!.Importer.Id);
    }

    [Fact]
    public void 扩展名未登记时回退到内容嗅探()
    {
        LyricsImportDetection? detection = Detect("[00:01.00]你好\n", "song.weird");

        Assert.NotNull(detection);
        Assert.Equal(PlainLrcImporter.IdValue, detection!.Importer.Id);
    }

    [Fact]
    public void 无法识别时返回空并使导入抛出不支持异常()
    {
        byte[] bytes = [0x00, 0x01, 0x02, 0x7F, 0x00];

        Assert.Null(Registry.Detect("song.bin", bytes.AsSpan()));

        using MemoryStream stream = new(bytes);

        Assert.Throws<NotSupportedException>(() => Registry.Import(stream, new LyricsImportContext { FileName = "song.bin" }));
    }

    [Fact]
    public void 指定导入器时可以越过后自动识别()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n你好\n"));

        LyricsImportResult result = Registry.Import(
            stream,
            new LyricsImportContext { FileName = "song.srt" },
            new SrtImporter());

        Assert.Equal(SrtImporter.IdValue, result.Report.ImporterId);
        Assert.Contains("由用户指定", result.Report.DetectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 报告的识别信息写进了格式化文本()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("[00:01.00]<00:01.00>你<00:01.50>好\n"));

        LyricsImportResult result = Registry.Import(stream, new LyricsImportContext { FileName = "song.lrc" });
        string report = LyricsImportReportFormatter.Format(result.Report);

        Assert.Contains("识别：", report, StringComparison.Ordinal);
        Assert.Contains("候选：", report, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录列出的扩展名都能给出候选()
    {
        foreach (string extension in LyricsFormatCatalog.PickerExtensions)
        {
            Assert.NotEmpty(LyricsFormatCatalog.Default.CandidatesFor("song" + extension));
        }
    }
}
