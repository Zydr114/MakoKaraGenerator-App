using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// <see cref="PlainLrcExporter" /> 的测试。
/// </summary>
public sealed class LyricsExporterTests
{
    private static LyricsDocument CreateDocument(params (string Text, long StartUs, long EndUs)[] lines)
    {
        List<LyricLine> result = [];
        LyricsIdAllocator ids = new();

        foreach ((string text, long startUs, long endUs) in lines)
        {
            List<LyricToken> tokens = [];

            foreach (TextElementRange element in GraphemeSplitter.Split(text))
            {
                tokens.Add(LyricToken.Create(ids.NextTokenId(), element.Text));
            }

            result.Add(LyricLine.Create(
                ids.NextLineId(),
                text,
                TimeRange.Create(startUs, endUs),
                TimingStatus.Imported,
                tokens));
        }

        return LyricsDocument.Create(result);
    }

    private static (string Text, LyricsExportReport Report) Export(
        LyricsDocument document,
        LyricsExportOptions? options = null)
    {
        using MemoryStream stream = new();
        PlainLrcExporter exporter = new();
        LyricsExportReport report = exporter.Export(document, stream, options ?? LyricsExportOptions.Default);

        return (Encoding.UTF8.GetString(stream.ToArray()), report);
    }

    [Fact]
    public void 导出普通LRC的基本格式()
    {
        LyricsDocument document = CreateDocument(("夜空", 1_000_000L, 5_000_000L), ("前行", 5_000_000L, 9_000_000L));

        (string text, LyricsExportReport report) = Export(document);

        Assert.Equal("[00:01.00]夜空\n[00:05.00]前行\n", text);
        Assert.Equal(2, report.ExportedLineCount);
        Assert.Equal(0, report.SkippedLineCount);
    }

    [Fact]
    public void 没有时间的行被跳过并计入报告()
    {
        List<LyricLine> lines =
        [
            LyricLine.Create("line-0001", "有时间", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported),
            LyricLine.Create("line-0002", "没时间"),
        ];

        (string text, LyricsExportReport report) = Export(LyricsDocument.Create(lines));

        Assert.Equal("[00:01.00]有时间\n", text);
        Assert.Equal(1, report.ExportedLineCount);
        Assert.Equal(1, report.SkippedLineCount);
        Assert.Contains(report.Plan.Issues, issue => issue.Kind == LyricsExportIssueKind.LineWithoutTime);
    }

    [Fact]
    public void 逐字时间会列入损失清单()
    {
        List<LyricLine> lines =
        [
            LyricLine.Create(
                "line-0001",
                "夜空",
                TimeRange.Create(1_000_000L, 3_000_000L),
                TimingStatus.Imported,
                [
                    LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported),
                    LyricToken.Create("token-0002", "空", TimeRange.Create(2_000_000L, 3_000_000L), TimingStatus.Confirmed),
                ]),
        ];

        LyricsExportPlan plan = new PlainLrcExporter().Plan(LyricsDocument.Create(lines), LyricsExportOptions.Default);

        Assert.True(plan.HasLosses);
        Assert.Contains(plan.Losses, loss => loss.Feature == "逐字时间" && loss.AffectedCount == 2);

        // 元素的"已确认"状态也表达不了
        Assert.Contains(plan.Losses, loss => loss.Feature == "时间来源状态");
    }

    [Fact]
    public void 百分秒精度不足时记入损失清单()
    {
        // 1.005 秒不是 10 毫秒整数倍
        LyricsDocument document = CreateDocument(("甲", 1_005_000L, 2_000_000L));

        LyricsExportPlan plan = new PlainLrcExporter().Plan(document, LyricsExportOptions.Default);

        Assert.Contains(plan.Losses, loss => loss.Feature == "时间精度");

        // 改成毫秒精度后就没有这项损失了
        LyricsExportPlan millisecondPlan = new PlainLrcExporter()
            .Plan(document, LyricsExportOptions.Default with { MillisecondPrecision = true });

        Assert.DoesNotContain(millisecondPlan.Losses, loss => loss.Feature == "时间精度");
    }

    [Fact]
    public void 毫秒精度导出保留毫秒()
    {
        LyricsDocument document = CreateDocument(("甲", 1_005_000L, 2_007_000L));

        (string text, LyricsExportReport report) = Export(
            document,
            LyricsExportOptions.Default with { MillisecondPrecision = true });

        Assert.Equal("[00:01.005]甲\n", text);
        Assert.Equal(0, report.RoundedTimeCount);
    }

    [Fact]
    public void 文本里的换行原样写出并记为提示()
    {
        LyricsDocument document = CreateDocument(("前句\n后句", 1_000_000L, 5_000_000L));

        LyricsExportPlan plan = new PlainLrcExporter().Plan(document, LyricsExportOptions.Default);

        Assert.Contains(plan.Issues, issue => issue.Kind == LyricsExportIssueKind.LineTextHasNewLine);

        (string text, _) = Export(document);

        // 原样写出：不静默把换行换成空格
        Assert.Equal("[00:01.00]前句\n后句\n", text);
    }

    [Fact]
    public void 元数据按选项写入()
    {
        LyricsDocument document = CreateDocument(("甲", 1_000_000L, 2_000_000L));

        (string text, LyricsExportReport report) = Export(
            document,
            LyricsExportOptions.Default with { Title = "测试曲", Artist = "某位", Album = "某碟" });

        Assert.StartsWith("[ti:测试曲]\n[ar:某位]\n[al:某碟]\n", text, StringComparison.Ordinal);
        Assert.Equal(3, report.MetadataCount);
    }

    [Fact]
    public void 换行符选项生效()
    {
        LyricsDocument document = CreateDocument(("甲", 1_000_000L, 2_000_000L));

        (string text, _) = Export(document, LyricsExportOptions.Default with { NewLine = "\r\n" });

        Assert.Equal("[00:01.00]甲\r\n", text);
    }

    [Fact]
    public void BOM选项生效()
    {
        LyricsDocument document = CreateDocument(("甲", 1_000_000L, 2_000_000L));

        using MemoryStream stream = new();
        new PlainLrcExporter().Export(document, stream, LyricsExportOptions.Default with { WriteByteOrderMark = true });

        byte[] bytes = stream.ToArray();

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("utf-8", LyricsTextDecoder.Decode(bytes).EncodingName);
    }

    [Fact]
    public void 导出再导入后行时间不变()
    {
        LyricsDocument original = CreateDocument(
            ("夜空中最亮的星", 1_000_000L, 7_000_000L),
            ("请照亮我前行", 8_500_000L, 13_800_000L),
            ("与 café 混排 🏳️‍🌈", 15_500_000L, 25_000_000L));

        (string text, _) = Export(original);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        LyricsImportResult imported = new LyricsImporterRegistry([new EnhancedLrcImporter(), new PlainLrcImporter()]).Import(
            stream,
            new LyricsImportContext { FileName = "roundtrip.lrc", MediaDurationUs = 30_000_000L });

        Assert.Equal(original.Lines.Length, imported.Document.Lines.Length);

        for (int i = 0; i < original.Lines.Length; i++)
        {
            LyricLine before = original.Lines[i];
            LyricLine after = imported.Document.Lines[i];

            Assert.Equal(before.Text, after.Text);
            Assert.Equal(before.Time!.Value.StartUs, after.Time!.Value.StartUs);

            // 逐字时间在 LRC 里不存在，导入回来应全部是"缺失"（而不是被估算）
            Assert.All(after.Tokens, token => Assert.Null(token.Time));
        }
    }

    [Fact]
    public void 损失提示排版包含格式名与不可恢复说明()
    {
        List<LyricLine> lines =
        [
            LyricLine.Create(
                "line-0001",
                "夜空",
                TimeRange.Create(1_000_000L, 3_000_000L),
                TimingStatus.Imported,
                [LyricToken.Create("token-0001", "夜", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported)]),
        ];

        LyricsExportPlan plan = new PlainLrcExporter().Plan(LyricsDocument.Create(lines), LyricsExportOptions.Default);
        string text = LyricsExportPlanFormatter.Format(plan, "普通 LRC");

        Assert.Contains("导出为 普通 LRC", text, StringComparison.Ordinal);
        Assert.Contains("损失提示", text, StringComparison.Ordinal);
        Assert.Contains("逐字时间", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有损失时明确说可以完整表达()
    {
        LyricsDocument document = CreateDocument(("甲", 1_000_000L, 2_000_000L));

        LyricsExportPlan plan = new PlainLrcExporter().Plan(document, LyricsExportOptions.Default);

        // 元素时间全缺失、状态全是 Imported，因此只剩"元素数"这类不影响表达的东西
        Assert.False(plan.HasLosses);
        Assert.Contains("损失提示：无", LyricsExportPlanFormatter.Format(plan, "普通 LRC"), StringComparison.Ordinal);
    }
}
