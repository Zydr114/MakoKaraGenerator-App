using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>Phase 0：空格双语义、独立空白/标点分词、PhraseEndHint 持久化与 TTML 无损往返测试。</summary>
public sealed class LyricTokenSemanticsTests
{
    private static Func<string> Ids()
    {
        int next = 0;
        return () => $"tok-{++next:D4}";
    }

    [Fact]
    public void 混合规则将连续空白和标点切分为独立无时间元素且保持行文完整()
    {
        string text = "「Hello, world」  今日　明日！";
        var parts = LyricTokenSplitter.Split(text, TokenSplitRule.Mixed);

        Assert.Equal(
            ["「", "Hello", ",", " ", "world", "」", "  ", "今", "日", "　", "明", "日", "！"],
            parts.ToArray());
        Assert.Equal(text, string.Concat(parts));

        LyricLine line = LyricLine.Create("l1", text, TimeRange.Create(0, 4_000_000), TimingStatus.Imported);
        LyricTokenSplitResult estimated = LyricLineTokenSplitter.Apply(line, TokenSplitRule.Mixed, estimateTimes: true, Ids());

        Assert.Equal(text, string.Concat(estimated.Line.Tokens.Select(t => t.Text)));
        foreach (LyricToken token in estimated.Line.Tokens)
        {
            LyricTokenKind kind = LyricTokenSemantics.Classify(token.Text);
            if (kind is LyricTokenKind.Whitespace or LyricTokenKind.Punctuation)
            {
                Assert.Null(token.Time);
                Assert.Equal(TimingStatus.Missing, token.TimingStatus);
            }
            else
            {
                Assert.NotNull(token.Time);
                Assert.Equal(TimingStatus.Estimated, token.TimingStatus);
            }
        }

        // 首尾实义元素时间严格对齐整行包络。
        LyricToken firstContent = estimated.Line.Tokens.First(t => LyricTokenSemantics.Classify(t.Text) == LyricTokenKind.Content);
        LyricToken lastContent = estimated.Line.Tokens.Last(t => LyricTokenSemantics.Classify(t.Text) == LyricTokenKind.Content);
        Assert.Equal(0L, firstContent.Time!.Value.StartUs);
        Assert.Equal(4_000_000L, lastContent.Time!.Value.EndUs);
    }

    [Fact]
    public void 长音符与叠字符归为实义元素而标点与全角波浪号归为标点()
    {
        Assert.Equal(LyricTokenKind.Content, LyricTokenSemantics.Classify("ー"));
        Assert.Equal(LyricTokenKind.Content, LyricTokenSemantics.Classify("々"));
        Assert.Equal(LyricTokenKind.Content, LyricTokenSemantics.Classify("🎵"));
        Assert.Equal(LyricTokenKind.Whitespace, LyricTokenSemantics.Classify(" "));
        Assert.Equal(LyricTokenKind.Whitespace, LyricTokenSemantics.Classify("　"));
        Assert.Equal(LyricTokenKind.Whitespace, LyricTokenSemantics.Classify("   "));
        Assert.Equal(LyricTokenKind.Punctuation, LyricTokenSemantics.Classify("「"));
        Assert.Equal(LyricTokenKind.Punctuation, LyricTokenSemantics.Classify("、"));
        Assert.Equal(LyricTokenKind.Punctuation, LyricTokenSemantics.Classify("！"));
        Assert.Equal(LyricTokenKind.Punctuation, LyricTokenSemantics.Classify("～"));
        Assert.Equal(["カ", "ー", "人", "々"], LyricTokenSplitter.Split("カー人々", TokenSplitRule.Mixed).ToArray());
    }

    [Fact]
    public void 拆分带前导标点的已定时元素时首个实义段继承原时间而非标点()
    {
        TimeRange range = TimeRange.Create(1_000_000, 2_500_000);
        LyricLine line = LyricLine.Create(
            "l1", "「パネ」", range, TimingStatus.Confirmed,
            [LyricToken.Create("t1", "「パネ」", range, TimingStatus.Confirmed)]);

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.Mixed, estimateTimes: false, Ids());

        Assert.Equal(["「", "パ", "ネ", "」"], result.Line.Tokens.Select(t => t.Text).ToArray());
        Assert.Null(result.Line.Tokens[0].Time);
        Assert.Equal(TimingStatus.Missing, result.Line.Tokens[0].TimingStatus);
        Assert.Equal(range, result.Line.Tokens[1].Time);
        Assert.Equal(TimingStatus.Confirmed, result.Line.Tokens[1].TimingStatus);
        Assert.Null(result.Line.Tokens[2].Time);
        Assert.Null(result.Line.Tokens[3].Time);
    }

    [Fact]
    public void 英日空格邻接语境自动区分切分空格与停顿空格且支持显式覆写()
    {
        // 英文单词间单个半角空格 → 切分空格（false）
        var english = SplitTokens("Hello world");
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(english, 0)); // Hello
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(english, 1)); // " "
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(english, 2)); // world

        // 连续多空格 → 停顿空格（true）
        var multiSpace = SplitTokens("Hello  world");
        Assert.True(LyricTokenSemantics.HasPhraseEndHint(multiSpace, 0));

        // 全角空格 U+3000 → 停顿空格（true）
        var fullwidth = SplitTokens("Hello　world");
        Assert.True(LyricTokenSemantics.HasPhraseEndHint(fullwidth, 0));

        // 与中日韩字符相邻的半角空格 → 停顿空格（true）
        var cjk = SplitTokens("今日 明日");
        Assert.True(LyricTokenSemantics.HasPhraseEndHint(cjk, 1)); // 日 before " "

        var mixedLeft = SplitTokens("愛 you");
        Assert.True(LyricTokenSemantics.HasPhraseEndHint(mixedLeft, 0)); // 愛

        var mixedRight = SplitTokens("love 君");
        Assert.True(LyricTokenSemantics.HasPhraseEndHint(mixedRight, 0)); // love

        // 行首/行尾空白 → false
        var leadingTrailing = SplitTokens("  Hello world  ");
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(leadingTrailing, 0)); // leading "  "
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(leadingTrailing, 1)); // Hello -> single space -> world
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(leadingTrailing, 3)); // world -> trailing "  "

        // 手动覆写优先于语境推导
        var forcedTrue = english.SetItem(0, english[0] with { PhraseEndHint = true });
        Assert.True(LyricTokenSemantics.HasPhraseEndHint(forcedTrue, 0));

        var forcedFalse = cjk.SetItem(1, cjk[1] with { PhraseEndHint = false });
        Assert.False(LyricTokenSemantics.HasPhraseEndHint(forcedFalse, 1));
    }

    [Fact]
    public void PhraseEndHint持久化读写无损且null省略写出()
    {
        using TemporaryDirectory temp = new();
        LyricLine line = LyricLine.Create(
            "l1", "Hello world", TimeRange.Create(0, 2_000_000), TimingStatus.Confirmed,
            [
                LyricToken.Create("t1", "Hello", TimeRange.Create(0, 1_000_000), TimingStatus.Confirmed, phraseEndHint: true),
                LyricToken.Create("t2", " "),
                LyricToken.Create("t3", "world", TimeRange.Create(1_000_000, 2_000_000), TimingStatus.Confirmed, phraseEndHint: false),
            ]);
        Project project = Project.Create("phrase-end", lyrics: LyricsDocument.Create([line]));

        ProjectStore.Save(project, temp.Path);

        string lyricsJson = ProjectFileFixture.ReadLyrics(temp.Path);
        JsonArray tokensJson = JsonNode.Parse(lyricsJson)!["branches"]![0]!["lines"]![0]!["tokens"]!.AsArray();
        Assert.True(tokensJson[0]!["phraseEndHint"]!.GetValue<bool>());
        Assert.Null(tokensJson[1]!["phraseEndHint"]); // null 时省略写出
        Assert.False(tokensJson[2]!["phraseEndHint"]!.GetValue<bool>());

        ProjectLoadResult loaded = ProjectStore.Load(temp.Path);
        Assert.NotNull(loaded.Project);
        Assert.False(loaded.Report.HasErrors, loaded.Report.ToSummary());
        Assert.Equal(true, loaded.Project!.Lyrics.Lines[0].Tokens[0].PhraseEndHint);
        Assert.Null(loaded.Project.Lyrics.Lines[0].Tokens[1].PhraseEndHint);
        Assert.Equal(false, loaded.Project.Lyrics.Lines[0].Tokens[2].PhraseEndHint);
    }

    [Fact]
    public void Amll导出将无时间空格写为裸文本节点零告警且重新导入可恢复独立空白元素()
    {
        LyricLine line = LyricLine.Create(
            "l1", "Hello world", TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Confirmed,
            [
                LyricToken.Create("t1", "Hello", TimeRange.Create(1_000_000, 2_000_000), TimingStatus.Confirmed),
                LyricToken.Create("t2", " "),
                LyricToken.Create("t3", "world", TimeRange.Create(2_000_000, 3_000_000), TimingStatus.Confirmed),
            ]);
        LyricsDocument document = LyricsDocument.Create([line]);

        using MemoryStream stream = new();
        LyricsExportReport report = new TtmlAmllExporter().Export(document, stream, LyricsExportOptions.Default);

        Assert.DoesNotContain(report.Plan.Losses, loss => loss.Feature == "AMLL消费端无时间普通元素");
        string xml = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("Hello</span> <span", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<span> </span>", xml, StringComparison.Ordinal);

        stream.Position = 0;
        LyricsImportResult reimported = new TtmlImporter().Import(stream, new() { FileName = "amll-space.ttml" });
        Assert.DoesNotContain(reimported.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        LyricLine importedLine = Assert.Single(reimported.Document.Lines);
        Assert.Equal("Hello world", importedLine.Text);
        Assert.Equal(["Hello", " ", "world"], importedLine.Tokens.Select(t => t.Text).ToArray());
        Assert.Null(importedLine.Tokens[1].Time);
        Assert.Equal(TimingStatus.Missing, importedLine.Tokens[1].TimingStatus);
        Assert.Null(importedLine.Tokens[0].PhraseEndHint);
    }

    [Fact]
    public void ProjectProfile精确往返mkgPhraseEnd含注音元素且无未映射属性告警()
    {
        LyricToken baseToken = LyricToken.Create("t1", "今日", TimeRange.Create(0, 1_000_000), TimingStatus.Confirmed, phraseEndHint: true);
        LyricToken spaceToken = LyricToken.Create("t2", " ");
        LyricToken nextToken = LyricToken.Create("t3", "明日", TimeRange.Create(1_200_000, 2_000_000), TimingStatus.Confirmed, phraseEndHint: false);
        LyricLine line = LyricLine.Create("l1", "今日 明日", TimeRange.Create(0, 2_000_000), TimingStatus.Confirmed, [baseToken, spaceToken, nextToken]) with
        {
            RubyAnnotations =
            [
                RubyAnnotation.Create("r1", "t1", "きょう") with
                {
                    Segments =
                    [
                        RubyTextSegment.Create("s1", "きょ", TimeRange.Create(0, 400_000), TimingStatus.Confirmed),
                        RubyTextSegment.Create("s2", "う", TimeRange.Create(400_000, 1_000_000), TimingStatus.Confirmed),
                    ],
                },
            ],
        };
        LyricsDocument document = LyricsDocument.Create([line]);

        using MemoryStream stream = new();
        LyricsExportReport report = new TtmlProjectExporter().Export(document, stream, LyricsExportOptions.Default);
        Assert.False(report.Plan.HasLosses);

        string xml = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("mkg:phrase-end=\"true\"", xml, StringComparison.Ordinal);
        Assert.Contains("mkg:phrase-end=\"false\"", xml, StringComparison.Ordinal);

        stream.Position = 0;
        LyricsImportResult imported = new TtmlImporter().Import(stream, new() { FileName = "project-phrase-end.ttml" });
        Assert.DoesNotContain(imported.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(imported.Report.DiscardedFeatures, feature => feature.Contains("未映射", StringComparison.Ordinal));
        Assert.Equal(document, imported.Document);
    }

    private static System.Collections.Immutable.ImmutableArray<LyricToken> SplitTokens(string text)
    {
        int i = 0;
        return [.. LyricTokenSplitter.Split(text, TokenSplitRule.Mixed).Select(part => LyricToken.Create($"t{++i}", part))];
    }
}
