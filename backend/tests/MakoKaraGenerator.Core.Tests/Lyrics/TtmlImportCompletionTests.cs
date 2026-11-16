using System.Text;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// 标准 TTML 与 AMLL/Apple 多歌唱者导入的独立预期。
/// 合成样本只复现统计形状，不冒充真实歌词。
/// </summary>
public sealed class TtmlImportCompletionTests
{
    private static LyricsImportResult Import(string xml, string name = "case.ttml")
    {
        using MemoryStream input = new(Encoding.UTF8.GetBytes(xml));
        return new TtmlImporter().Import(input, new LyricsImportContext { FileName = name });
    }

    [Fact]
    public void Synthetic_two_singer_sample_matches_independent_counts_text_and_times()
    {
        using Stream resource = typeof(TtmlImportCompletionTests).Assembly.GetManifestResourceStream(
            "MakoKaraGenerator.Core.Tests.Lyrics.Fixtures.synthetic-amll-two-singers.ttml")!;
        LyricsImportResult result = new TtmlImporter().Import(resource, new LyricsImportContext { FileName = "synthetic-amll-two-singers.ttml" });
        Assert.DoesNotContain(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.Contains(result.Report.Issues, issue => issue.Message.Contains("空命名空间", StringComparison.Ordinal));
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("agent", StringComparison.Ordinal));

        Assert.Equal(2, result.Document.Branches.Length);
        LyricBranch main = result.Document.Branches[0];
        LyricBranch harmony = result.Document.Branches[1];
        Assert.Equal(result.Document.PrimaryBranchId, main.Id);
        Assert.Equal("主唱", main.Name);
        Assert.Equal(LyricBranchRole.Primary, main.Role);
        Assert.Equal(LyricBranchTimingMode.Independent, main.TimingMode);
        Assert.Equal(53, main.Lines.Length);
        Assert.Equal("和声", harmony.Name);
        Assert.Equal(LyricBranchRole.Custom, harmony.Role);
        Assert.Equal(LyricBranchTimingMode.Independent, harmony.TimingMode);
        Assert.Equal(3, harmony.Lines.Length);
        Assert.Equal(56, result.Document.AllLines.Count());

        int groups = 0;
        int segments = 0;
        int global = 0;
        for (int i = 0; i < 53; i++)
        {
            int count = i < 7 ? 3 : 2;
            long lineStart = (i + 1) * 1_000_000L;
            LyricLine line = main.Lines[i];
            string text = "";
            var expected = new List<(string Text, long Start, long End, string Ruby, (string Text, long Start, long End)[] Segments)>();
            for (int local = 0; local < count; local++)
            {
                int segCount = global < 29 ? 3 : 2;
                string ch = char.ConvertFromUtf32(0x4E00 + global);
                text += ch;
                long groupStart = lineStart + local * 100_000L;
                var segs = new (string Text, long Start, long End)[segCount];
                for (int s = 0; s < segCount; s++)
                    segs[s] = (s.ToString(System.Globalization.CultureInfo.InvariantCulture), groupStart + s * 2_000L, groupStart + s * 2_000L + 1_000L);
                expected.Add((ch, groupStart, segs[^1].End, string.Concat(segs.Select(item => item.Text)), segs));
                global++;
            }
            Assert.Equal(text, line.Text);
            Assert.Equal(new TimeRange(lineStart, lineStart + 800_000L), line.Time);
            Assert.Equal(TimingStatus.Imported, line.TimingStatus);
            Assert.Equal(count, line.RubyAnnotations.Length);
            for (int rubyIndex = 0; rubyIndex < count; rubyIndex++)
            {
                RubyAnnotation ruby = line.RubyAnnotations[rubyIndex];
                LyricToken token = Assert.Single(line.Tokens, item => item.Id == ruby.BaseTokenId);
                var item = expected[rubyIndex];
                Assert.Equal(item.Text, token.Text);
                Assert.Equal(new TimeRange(item.Start, item.End), token.Time);
                Assert.Equal(TimingStatus.Estimated, token.TimingStatus);
                Assert.Equal(item.Ruby, ruby.Text);
                Assert.Equal(item.Segments.Length, ruby.Segments.Length);
                for (int s = 0; s < item.Segments.Length; s++)
                {
                    Assert.Equal(item.Segments[s].Text, ruby.Segments[s].Text);
                    Assert.Equal(new TimeRange(item.Segments[s].Start, item.Segments[s].End), ruby.Segments[s].Time);
                    Assert.Equal(TimingStatus.Imported, ruby.Segments[s].TimingStatus);
                }
            }
            groups += count;
            segments += line.RubyAnnotations.Sum(ruby => ruby.Segments.Length);
        }
        Assert.Equal(113, groups);
        Assert.Equal(113, global);
        Assert.Equal(255, segments);
        Assert.Equal(113, result.Document.AllLines.SelectMany(line => line.RubyAnnotations).Count());
        Assert.Equal(255, result.Document.AllLines.SelectMany(line => line.RubyAnnotations).SelectMany(ruby => ruby.Segments).Count(segment => segment.Time is not null));
        for (int i = 0; i < 3; i++)
        {
            LyricLine line = harmony.Lines[i];
            long start = (53 + i + 1) * 1_000_000L;
            Assert.Equal("和" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), line.Text);
            Assert.Equal(new TimeRange(start, start + 800_000L), line.Time);
            Assert.Equal(TimingStatus.Imported, Assert.Single(line.Tokens).TimingStatus);
            Assert.Empty(line.RubyAnnotations);
        }
    }

    [Fact]
    public void Unmarked_empty_namespace_and_project_profile_stay_strict()
    {
        LyricsImportResult plain = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml"><body xmlns=""><div><p begin="1s" end="2s">海</p></div></body></tt>
            """);
        Assert.Empty(plain.Document.Lines);
        Assert.Contains(plain.Report.Issues, issue => issue.Severity == IssueSeverity.Error);

        LyricsImportResult project = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:mkg="urn:makokara:ttml" mkg:profile="ttml.project" mkg:version="1">
              <body xmlns=""><div><p begin="1s" end="2s">海</p></div></body>
            </tt>
            """);
        Assert.Empty(project.Document.Lines);
        Assert.Contains(project.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(project.Report.Issues, issue => issue.Message.Contains("恢复", StringComparison.Ordinal));
    }

    [Fact]
    public void Agents_inherit_override_and_chorus_map_to_named_independent_branches()
    {
        const string xml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <head><metadata>
                <ttm:agent xml:id="v1"><ttm:name>主唱</ttm:name></ttm:agent>
                <ttm:agent xml:id="v2"><ttm:name>和声</ttm:name></ttm:agent>
              </metadata></head>
              <body>
                <div ttm:agent="v1">
                  <p begin="1s" end="2s">继承</p>
                  <p begin="2s" end="3s" ttm:agent="v2">覆盖</p>
                  <p begin="3s" end="4s" ttm:agent="v2 v1">合唱</p>
                  <p begin="4s" end="5s"><span begin="0s" end="0.5s">甲</span><span ttm:agent="v2" begin="0.5s" end="1s">乙</span></p>
                  <p begin="5s" end="6s" ttm:agent="missing">未定义</p>
                </div>
              </body>
            </tt>
            """;
        LyricsImportResult result = Import(xml);
        Assert.DoesNotContain(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        LyricBranch primary = result.Document.PrimaryBranch!;
        Assert.Equal("主唱", primary.Name);
        Assert.Equal(["继承", "甲"], primary.Lines.Select(line => line.Text));
        Assert.Equal(new TimeRange(4_000_000, 5_000_000), primary.Lines[1].Time);
        Assert.Equal(new TimeRange(4_000_000, 4_500_000), Assert.Single(primary.Lines[1].Tokens).Time);
        Assert.All(result.Document.Branches, branch => Assert.Equal(LyricBranchTimingMode.Independent, branch.TimingMode));

        LyricBranch harmony = result.Document.Branches.Single(branch => branch.Name == "和声");
        Assert.Equal(["覆盖", "乙"], harmony.Lines.Select(line => line.Text));
        Assert.Equal(new TimeRange(4_500_000, 5_000_000), harmony.Lines[1].Time);

        LyricBranch chorus = result.Document.Branches.Single(branch => branch.Name.Contains('、', StringComparison.Ordinal));
        Assert.Equal("合唱", Assert.Single(chorus.Lines).Text);
        Assert.Equal(new TimeRange(3_000_000, 4_000_000), chorus.Lines[0].Time);
        Assert.Contains("主唱", chorus.Name, StringComparison.Ordinal);
        Assert.Contains("和声", chorus.Name, StringComparison.Ordinal);

        LyricBranch undefined = result.Document.Branches.Single(branch => branch.Name.Contains("missing", StringComparison.Ordinal));
        Assert.Equal("未定义", Assert.Single(undefined.Lines).Text);
        Assert.Contains(result.Report.Issues, issue => issue.Severity == IssueSeverity.Warning && issue.Message.Contains("missing", StringComparison.Ordinal));
        Assert.Equal(6, result.Document.AllLines.Count());
    }

    [Fact]
    public void Sidecar_roles_keep_text_on_named_branches_instead_of_dropping_it()
    {
        const string xml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div>
                <p begin="1s" end="2s">正文<span ttm:role="x-bg" begin="1.5s" end="2s">啊</span></p>
                <p begin="2s" end="3s" ttm:role="x-translation">translation</p>
                <p begin="3s" end="4s" ttm:role="x-roman">ro-ma</p>
              </div></body>
            </tt>
            """;
        LyricsImportResult result = Import(xml);
        Assert.Equal("正文", Assert.Single(result.Document.Lines).Text);
        Assert.Equal("啊", Assert.Single(result.Document.Branches.Single(branch => branch.Name == "和声").Lines).Text);
        Assert.Equal(LyricBranchRole.Translation, result.Document.Branches.Single(branch => branch.Lines.Any(line => line.Text == "translation")).Role);
        Assert.Equal(LyricBranchRole.Annotation, result.Document.Branches.Single(branch => branch.Lines.Any(line => line.Text == "ro-ma")).Role);
        Assert.Equal(4, result.Document.AllLines.Count());
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("和声", StringComparison.Ordinal));
    }

    [Fact]
    public void Simple_ruby_style_reference_and_ambiguous_group_are_distinguished()
    {
        LyricsImportResult simple = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:tts="http://www.w3.org/ns/ttml#styling">
              <head><styling>
                <style xml:id="rb" tts:ruby="base"/>
                <style xml:id="rt" tts:ruby="text"/>
                <style xml:id="rc" tts:ruby="container" tts:rubyAlign="withBase"/>
              </styling></head>
              <body><div><p begin="10s" end="11s">
                <span style="rc"><span style="rb">漢</span><span style="rt" begin="0.2s" end="0.5s">かん</span></span>
              </p></div></body>
            </tt>
            """);
        Assert.DoesNotContain(simple.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        LyricLine line = Assert.Single(simple.Document.Lines);
        Assert.Equal("漢", line.Text);
        RubyAnnotation ruby = Assert.Single(line.RubyAnnotations);
        Assert.Equal("かん", ruby.Text);
        Assert.Equal(new TimeRange(10_200_000, 10_500_000), Assert.Single(ruby.Segments).Time);
        Assert.Equal(RubyAlignment.Start, ruby.Alignment);
        Assert.Null(line.Tokens[0].Time);

        LyricsImportResult grouped = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:tts="http://www.w3.org/ns/ttml#styling">
              <body><div><p begin="1s" end="2s"><span tts:ruby="container">
                <span tts:ruby="baseContainer"><span tts:ruby="base">東</span><span tts:ruby="base">京</span></span>
                <span tts:ruby="textContainer"><span tts:ruby="text">とうきょう</span></span>
              </span></p></div></body>
            </tt>
            """);
        Assert.Equal("東京", Assert.Single(grouped.Document.Lines).Text);
        Assert.Empty(grouped.Document.Lines[0].RubyAnnotations);
        Assert.Contains(grouped.Report.DiscardedFeatures, feature => feature.Contains("rubyAssociationAmbiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_amll_estimates_base_time_from_bounded_ruby_but_project_missing_roundtrips()
    {
        LyricsImportResult amll = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:tts="http://www.w3.org/ns/ttml#styling" itunes:timing="Word">
              <body><div><p begin="1s" end="2s"><span tts:ruby="container"><span tts:ruby="base">海</span>
                <span tts:ruby="textContainer">
                  <span tts:ruby="text" begin="1.1s" end="1.4s">う</span>
                  <span tts:ruby="text" begin="1.5s" end="1.8s">み</span>
                </span></span></p></div></body>
            </tt>
            """);
        LyricToken token = Assert.Single(amll.Document.Lines[0].Tokens);
        Assert.Equal(new TimeRange(1_100_000, 1_800_000), token.Time);
        Assert.Equal(TimingStatus.Estimated, token.TimingStatus);
        Assert.Contains(amll.Report.Issues, issue => issue.Message.Contains("Estimated", StringComparison.Ordinal));

        LyricToken missing = LyricToken.Create("token", "海");
        RubyAnnotation ruby = new()
        {
            Id = "ruby", BaseTokenId = "token", Text = "うみ",
            Segments =
            [
                RubyTextSegment.Create("s1", "う", new TimeRange(1_100_000, 1_400_000), TimingStatus.Imported),
                RubyTextSegment.Create("s2", "み", new TimeRange(1_500_000, 1_800_000), TimingStatus.Imported),
            ],
        };
        LyricsDocument source = LyricsDocument.Create([
            LyricLine.Create("line", "海", new TimeRange(1_000_000, 2_000_000), TimingStatus.Imported, [missing]) with { RubyAnnotations = [ruby] },
        ]);
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        output.Position = 0;
        LyricsImportResult round = new TtmlImporter().Import(output, new LyricsImportContext { FileName = "project.ttml" });
        Assert.Equal(TimingStatus.Missing, round.Document.Lines[0].Tokens[0].TimingStatus);
        Assert.Null(round.Document.Lines[0].Tokens[0].Time);
        Assert.Equal(new TimeRange(1_100_000, 1_400_000), round.Document.Lines[0].RubyAnnotations[0].Segments[0].Time);
    }

    [Fact]
    public void Smpte_nondrop_frame_time_is_deterministic_and_clock_stays_rejected()
    {
        LyricsImportResult smpte = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttp="http://www.w3.org/ns/ttml#parameter" ttp:timeBase="smpte" ttp:frameRate="25" ttp:dropMode="nonDrop">
              <body><div><p begin="00:00:01:00" end="00:00:01:10">帧</p></div></body>
            </tt>
            """);
        Assert.Equal(new TimeRange(1_000_000, 1_400_000), Assert.Single(smpte.Document.Lines).Time);
        Assert.Contains("ttml2-media", smpte.Report.SourceFormat, StringComparison.Ordinal);
        Assert.DoesNotContain("amll-absolute", smpte.Report.SourceFormat, StringComparison.Ordinal);

        LyricsImportResult clock = Import("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttp="http://www.w3.org/ns/ttml#parameter" ttp:timeBase="clock">
              <body><div><p begin="00:00:01" end="00:00:02">钟</p></div></body>
            </tt>
            """);
        Assert.Empty(clock.Document.Lines);
        Assert.Contains(clock.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
    }
}
