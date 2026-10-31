using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// <see cref="EnhancedLrcExporter" /> 的测试。
/// </summary>
public sealed class EnhancedLrcExporterTests
{
    private static LyricsDocument CreateDocument(params LyricLine[] lines) => LyricsDocument.Create(lines);

    private static (string Text, LyricsExportReport Report) Export(
        LyricsDocument document,
        LyricsExportOptions? options = null)
    {
        using MemoryStream stream = new();
        LyricsExportReport report = new EnhancedLrcExporter()
            .Export(document, stream, options ?? LyricsExportOptions.Default);

        return (Encoding.UTF8.GetString(stream.ToArray()), report);
    }

    [Fact]
    public void 带逐字时间的行写成标签加文本()
    {
        // 与用户给的格式一致：[行标签] 之后每个带时间的元素前面放一个逐字标签
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜空最亮的星",
            TimeRange.Create(1_000_000L, 7_000_000L),
            TimingStatus.Imported,
            [
                LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_600_000L), TimingStatus.Imported),
                LyricToken.Create("token-0002", "空", TimeRange.Create(1_600_000L, 2_400_000L), TimingStatus.Imported),
                LyricToken.Create("token-0003", "最亮的星", TimeRange.Create(2_400_000L, 5_000_000L), TimingStatus.Imported),
            ]);

        (string text, LyricsExportReport report) = Export(CreateDocument(line));

        Assert.Equal("[00:01.00]<00:01.00>夜<00:01.60>空<00:02.40>最亮的星\n", text);
        Assert.Equal(1, report.ExportedLineCount);
        Assert.Equal(3, report.DroppedTokenTimeCount == 0 ? 3 : 0);
        Assert.Equal(0, report.RoundedTimeCount);
    }

    [Fact]
    public void 没有逐字时间的行退化成普通行级LRC()
    {
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜空",
            TimeRange.Create(1_000_000L, 5_000_000L),
            TimingStatus.Imported,
            [
                LyricToken.Create("token-0001", "夜"),
                LyricToken.Create("token-0002", "空"),
            ]);

        (string text, LyricsExportReport report) = Export(CreateDocument(line));

        Assert.Equal("[00:01.00]夜空\n", text);

        // 整行都没有逐字时间：什么都没丢，不该报"逐字时间损失"
        Assert.False(report.Plan.HasLosses);
    }

    [Fact]
    public void 未定时的元素列入损失清单()
    {
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜空中",
            TimeRange.Create(1_000_000L, 3_000_000L),
            TimingStatus.Imported,
            [
                LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_500_000L), TimingStatus.Imported),
                LyricToken.Create("token-0002", "空"),
                LyricToken.Create("token-0003", "中"),
            ]);

        LyricsExportPlan plan = new EnhancedLrcExporter().Plan(CreateDocument(line), LyricsExportOptions.Default);

        LyricsExportLoss loss = Assert.Single(plan.Losses, l => l.Feature == "未定时的元素");
        Assert.Equal(2, loss.AffectedCount);
    }

    [Fact]
    public void 逐字时间在导出报告里不算被丢弃()
    {
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜",
            TimeRange.Create(1_000_000L, 2_000_000L),
            TimingStatus.Imported,
            [LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_500_000L), TimingStatus.Imported)]);

        LyricsExportPlan plan = new EnhancedLrcExporter().Plan(CreateDocument(line), LyricsExportOptions.Default);

        Assert.DoesNotContain(plan.Losses, l => l.Feature == "逐字时间");
    }

    [Fact]
    public void 毫秒精度选项生效()
    {
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜",
            TimeRange.Create(1_005_000L, 2_007_000L),
            TimingStatus.Imported,
            [LyricToken.Create("token-0001", "夜", TimeRange.Create(1_006_400L, 2_000_000L), TimingStatus.Imported)]);

        (string text, LyricsExportReport report) = Export(
            CreateDocument(line),
            LyricsExportOptions.Default with { MillisecondPrecision = true });

        Assert.Equal("[00:01.005]<00:01.006>夜\n", text);
        Assert.Equal(0, report.RoundedTimeCount);
    }

    [Fact]
    public void 百分秒精度不足时列入损失清单()
    {
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜",
            TimeRange.Create(1_005_000L, 2_000_000L),
            TimingStatus.Imported,
            [LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_500_000L), TimingStatus.Imported)]);

        LyricsExportPlan plan = new EnhancedLrcExporter().Plan(CreateDocument(line), LyricsExportOptions.Default);

        Assert.Contains(plan.Losses, l => l.Feature == "时间精度");
    }

    [Fact]
    public void 元素时间状态列入损失清单()
    {
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜",
            TimeRange.Create(1_000_000L, 2_000_000L),
            TimingStatus.Confirmed,
            [LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_500_000L), TimingStatus.Edited)]);

        LyricsExportPlan plan = new EnhancedLrcExporter().Plan(CreateDocument(line), LyricsExportOptions.Default);

        Assert.Contains(plan.Losses, l => l.Feature == "时间来源状态");
    }

    [Fact]
    public void 没有时间的行被跳过()
    {
        LyricsDocument document = CreateDocument(
            LyricLine.Create("line-0001", "有时间", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported),
            LyricLine.Create("line-0002", "没时间"));

        (string text, LyricsExportReport report) = Export(document);

        Assert.Equal("[00:01.00]有时间\n", text);
        Assert.Equal(1, report.SkippedLineCount);
    }

    [Fact]
    public void 元数据与换行符按选项写入()
    {
        LyricsDocument document = CreateDocument(
            LyricLine.Create("line-0001", "夜", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported));

        (string text, LyricsExportReport report) = Export(
            document,
            LyricsExportOptions.Default with { Title = "测试曲", Artist = "某位", NewLine = "\r\n" });

        Assert.Equal("[ti:测试曲]\r\n[ar:某位]\r\n[00:01.00]夜\r\n", text);
        Assert.Equal(2, report.MetadataCount);
    }

    [Fact]
    public void 导出再导入保留行时间与逐字时间()
    {
        // 这是本导出器存在的理由：逐字时间必须能原样带出去
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜空中最亮的星",
            TimeRange.Create(1_000_000L, 7_000_000L),
            TimingStatus.Imported,
            [
                LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_600_000L), TimingStatus.Imported),
                LyricToken.Create("token-0002", "空中", TimeRange.Create(1_600_000L, 2_400_000L), TimingStatus.Imported),
                LyricToken.Create("token-0003", "最亮的", TimeRange.Create(2_400_000L, 4_000_000L), TimingStatus.Imported),
                LyricToken.Create("token-0004", "星", TimeRange.Create(4_000_000L, 7_000_000L), TimingStatus.Imported),
            ]);

        LyricsDocument original = CreateDocument(line);
        (string text, _) = Export(original);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        LyricsImportResult imported = LyricsImporterRegistry.CreateDefault().Import(
            stream,
            new LyricsImportContext { FileName = "roundtrip.lrc", MediaDurationUs = 10_000_000L });

        LyricLine roundTripped = Assert.Single(imported.Document.Lines);

        Assert.Equal(1_000_000L, roundTripped.Time!.Value.StartUs);
        Assert.Equal("夜空中最亮的星", roundTripped.Text);

        Assert.Equal(
            line.Tokens.Select(t => (t.Text, t.Time!.Value.StartUs)),
            roundTripped.Tokens.Select(t => (t.Text, t.Time!.Value.StartUs)));
    }

    [Fact]
    public void 元素文本与行文本不一致时按行文本整行导出()
    {
        // 故意构造"元素拼不回行文本"的非法数据：不能让导出的文件丢字
        LyricLine line = LyricLine.Create(
            "line-0001",
            "夜空中",
            TimeRange.Create(1_000_000L, 3_000_000L),
            TimingStatus.Imported,
            [LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 1_500_000L), TimingStatus.Imported)]);

        (string text, _) = Export(CreateDocument(line));

        Assert.Equal("[00:01.00]夜空中\n", text);
    }

    [Fact]
    public void 词间空白原样保留且不会被额外插入空格()
    {
        // When + 空格（无时间） + the（有时间）
        LyricLine line = LyricLine.Create(
            "line-0001",
            "When the",
            TimeRange.Create(1_000_000L, 3_000_000L),
            TimingStatus.Imported,
            [
                LyricToken.Create("token-0001", "When", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported),
                LyricToken.Create("token-0002", " "),
                LyricToken.Create("token-0003", "the", TimeRange.Create(2_000_000L, 3_000_000L), TimingStatus.Imported),
            ]);

        (string text, _) = Export(CreateDocument(line));

        // 文本与工程里的文本逐字一致（只有标签被插进元素之间，不额外补空格）
        Assert.Equal("[00:01.00]<00:01.00>When <00:02.00>the\n", text);
    }
}
