using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class TtmlExportLossTests
{
    [Fact]
    public void Project_plan_preserves_branch_and_line_relationships()
    {
        LyricLine primaryLine = LyricLine.Create(
            "primary-line", "主", TimeRange.Create(0, 1_000_000), TimingStatus.Imported);
        LyricLine translationLine = LyricLine.Create(
            "translation-line", "译", TimeRange.Create(0, 1_000_000), TimingStatus.Imported) with
        {
            PrimaryLineId = primaryLine.Id,
        };
        LyricBranch translation = LyricBranch.Create(
            "translation", "翻译", LyricBranchRole.Translation, [translationLine], "zh-CN");
        LyricBranch primary = LyricBranch.Create("primary", "主歌词", LyricBranchRole.Primary, [primaryLine]);
        LyricsDocument document = LyricsDocument.Create([primaryLine]) with { Branches = [primary, translation] };

        LyricsExportPlan plan = new TtmlProjectExporter().Plan(document, LyricsExportOptions.Default);

        Assert.False(plan.HasLosses);
    }

    [Fact]
    public void Amll_reports_integer_milliseconds_outside_JavaScript_safe_range_without_rounding_XML()
    {
        const long start = 9_007_199_254_741_001_000;
        LyricToken token = LyricToken.Create("token", "字", new(start, start + 1_000), TimingStatus.Imported);
        LyricLine line = LyricLine.Create("line", "字", new(start, start + 2_000), TimingStatus.Imported, [token]);
        using MemoryStream output = new();
        LyricsExportReport report = new TtmlAmllExporter().Export(LyricsDocument.Create([line]), output, LyricsExportOptions.Default);
        Assert.Equal(1, Assert.Single(report.Plan.Losses, loss => loss.Feature == "AMLL消费端行时间毫秒精度").AffectedCount);
        Assert.Equal(1, Assert.Single(report.Plan.Losses, loss => loss.Feature == "AMLL消费端元素时间毫秒精度").AffectedCount);
        Assert.Contains("9007199254741.001s", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.Equal(0, report.RoundedTimeCount);
    }

    [Fact]
    public void Amll_reports_untimed_plain_token_consumer_word_loss_without_fabricating_time()
    {
        LyricToken missing = LyricToken.Create("missing", "A");
        LyricToken timed = LyricToken.Create("timed", "B", new(2_000_000, 3_000_000), TimingStatus.Imported);
        LyricLine line = LyricLine.Create("line", "AB", new(1_000_000, 3_000_000), TimingStatus.Imported, [missing, timed]);
        using MemoryStream output = new();
        LyricsExportReport report = new TtmlAmllExporter().Export(LyricsDocument.Create([line]), output, LyricsExportOptions.Default);
        LyricsExportLoss loss = Assert.Single(report.Plan.Losses, loss => loss.Feature == "AMLL消费端无时间普通元素");
        Assert.Equal(1, loss.AffectedCount);
        Assert.Contains("line=line/token=missing", loss.Message, StringComparison.Ordinal);
        Assert.Contains("<span>A</span>", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.Equal(0, report.DroppedTokenTimeCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Amll_reports_base_time_replacement_for_different_or_missing_ruby_ranges(bool timed, bool sameRange)
    {
        LyricToken token = LyricToken.Create("token", "字", new(1_000_000, 2_000_000), TimingStatus.Imported);
        RubyTextSegment segment = timed
            ? RubyTextSegment.Create("segment", "じ", sameRange ? new(1_000_000, 2_000_000) : new(1_100_000, 1_500_000), TimingStatus.Imported)
            : RubyTextSegment.Create("segment", "じ");
        LyricLine line = LyricLine.Create("line", "字", new(1_000_000, 3_000_000), TimingStatus.Imported, [token]) with
        { RubyAnnotations = [new RubyAnnotation { Id = "ruby", BaseTokenId = token.Id, Text = "じ", Segments = [segment] }] };
        LyricsExportPlan plan = new TtmlAmllExporter().Plan(LyricsDocument.Create([line]), LyricsExportOptions.Default);
        Assert.Equal(!sameRange, plan.Losses.Any(loss => loss.Feature == "AMLL消费端基础元素时间由注音范围替代"));
        if (!sameRange) Assert.Contains("token=token/ruby=ruby", Assert.Single(plan.Losses,
            loss => loss.Feature == "AMLL消费端基础元素时间由注音范围替代").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Amll_consumer_limitations_are_reported_without_changing_XML_precision_or_missing_times()
    {
        LyricToken token = LyricToken.Create("token", "字", new(1_000_001, 2_000_002), TimingStatus.Imported);
        LyricLine line = LyricLine.Create("line", "字", new(1_000_001, 3_000_003), TimingStatus.Imported, [token]) with
        {
            RubyAnnotations = [new RubyAnnotation { Id = "ruby", BaseTokenId = token.Id, Text = "じん", Segments =
                [RubyTextSegment.Create("timed", "じ", new(1_100_001, 1_500_001), TimingStatus.Imported), RubyTextSegment.Create("missing", "ん")] }],
        };
        using MemoryStream output = new();
        LyricsExportReport report = new TtmlAmllExporter().Export(LyricsDocument.Create([line]), output, LyricsExportOptions.Default);
        Assert.Equal(2, report.WrittenRubySegmentCount);
        Assert.Equal(0, report.DroppedRubySegmentTimeCount);
        Assert.Equal(0, report.RoundedTimeCount);
        foreach (string feature in new[] { "AMLL消费端基础元素时间由注音范围替代", "AMLL消费端无时间注音片段", "AMLL消费端行时间毫秒精度", "AMLL消费端元素时间毫秒精度", "AMLL消费端注音时间毫秒精度" })
            Assert.Equal(1, Assert.Single(report.Plan.Losses, loss => loss.Feature == feature).AffectedCount);
        string xml = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("1.100001s", xml, StringComparison.Ordinal);
        output.Position = 0;
        var imported = new MakoKaraGenerator.Core.Lyrics.Import.TtmlImporter().Import(output, new() { FileName = "consumer.ttml" });
        Assert.Equal(1_100_001, imported.Document.Lines[0].RubyAnnotations[0].Segments[0].Time!.Value.StartUs);
        Assert.Null(imported.Document.Lines[0].RubyAnnotations[0].Segments[1].Time);
    }
}
