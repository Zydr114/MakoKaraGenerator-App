using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SdkProjectionTests
{
    [Fact]
    public void 注音只读投影保留片段身份时间状态并共享采样器()
    {
        ProjectSnapshot source = Source();
        var branch = source.Lyrics.Branches[0]; var line = branch.Lines[0];
        RubyAnnotation ruby = line.RubyAnnotations[0] with { Segments =
            [RubyTextSegment.Create("part", "qǐng", new(1_000, 2_000), TimingStatus.Imported)] };
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.SetItem(0,
            branch with { Lines = branch.Lines.SetItem(0, line with { RubyAnnotations = [ruby] }) }) } };
        SourceRuby projected = ContentProjection.Create(source).GetRuby(new("ruby1"));
        SourceRubySegment segment = Assert.Single(projected.Segments);
        Assert.Equal("part", segment.Id); Assert.Equal(new TimeRange(1_000, 2_000), segment.TimeRange);
        Assert.Equal(SourceTimingStatus.Imported, segment.TimingStatus);
        var sample = LyricSampler.SampleLine(source.Lyrics.Branches[0].Lines[0], 1_500).RubySegments[0];
        PerformanceSample performance = ContentProjection.RubySegmentPerformance(ruby.Segments[0], sample, 1_500);
        Assert.Equal(.5d, performance.Progress); Assert.True(performance.IsCurrent);
        Assert.Equal(ProgressSource.RubySegment, performance.ProgressSource);
    }

    [Fact]
    public void 源目录保留所有分支元素注音且不以可见集合计数()
    {
        ProjectSnapshot snapshot = Source();
        ContentCatalog catalog = ContentProjection.Create(snapshot);
        Assert.Equal(2, catalog.Branches.Length);
        Assert.Equal(2, catalog.Lines.Length);
        Assert.Equal(4, catalog.Tokens.Length);
        Assert.Equal([new TokenId("b1-a"), new TokenId("b1-b"), new TokenId("b2-a")], catalog.FindTokens("请").Select(token => token.Id));
        Assert.Equal(new TokenId("b1-a"), Assert.Single(catalog.Ruby).BaseTokenId);
        Assert.Equal("qǐng", catalog.GetRuby(new("ruby1")).Text);
        Assert.Equal(SourceTimingStatus.Missing, catalog.GetToken(new("b1-missing")).TimingStatus);
        Assert.Null(catalog.GetToken(new("b1-missing")).TimeRange);
        Assert.Throws<KeyNotFoundException>(() => catalog.GetToken(new("not-present")));
        Assert.False(catalog.TryGetToken(new("not-present"), out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.FindTokens("请", StringComparison.CurrentCulture));
        Assert.Equal(0, catalog.GetBranch(new("b1")).SourceOrder);
        Assert.Equal(1, catalog.GetToken(new("b1-missing")).SourceOrder);
        Assert.Equal("请问请", snapshot.Lyrics.Branches[0].Lines[0].Text);
    }

    [Theory]
    [InlineData(999, false, false, 0)]
    [InlineData(1000, true, false, 0)]
    [InlineData(1500, true, false, .5)]
    [InlineData(2000, false, true, 1)]
    public void 逐字投影复用共享采样器半开边界(long timeUs, bool current, bool sung, double progress)
    {
        LyricLine line = Source().Lyrics.Branches[0].Lines[0];
        LyricFrameSample source = LyricSampler.SampleLine(line, timeUs);
        PerformanceSample actual = ContentProjection.TokenPerformance(line.Tokens[0], source.Tokens[0], timeUs, source.TokensMatchLineText);
        Assert.Equal(current, actual.IsCurrent); Assert.Equal(sung, actual.IsSung);
        Assert.Equal(progress, actual.Progress); Assert.Equal(ProgressSource.Token, actual.ProgressSource);
        Assert.Equal(1000, actual.DurationUs); Assert.Equal(timeUs - 1000, actual.ElapsedUs);
        PerformanceSample missing = ContentProjection.TokenPerformance(line.Tokens[1], source.Tokens[1], timeUs, source.TokensMatchLineText);
        Assert.Equal(ProgressSource.Missing, missing.ProgressSource);
        Assert.Null(missing.Progress); Assert.Null(missing.DurationUs); Assert.False(missing.IsSung); Assert.False(missing.IsCurrent);
    }

    [Fact]
    public void 行依据与失效映射不会制造逐字进度()
    {
        LyricLine line = Source().Lyrics.Branches[0].Lines[0];
        PerformanceSample row = ContentProjection.LinePerformance(line, 3000);
        Assert.Equal(ProgressSource.Line, row.ProgressSource);
        Assert.True(row.IsCurrent);
        LyricFrameSample source = LyricSampler.SampleLine(line, 1500);
        PerformanceSample badMapping = ContentProjection.TokenPerformance(line.Tokens[0], source.Tokens[0], 1500, tokensMatchLineText: false);
        Assert.Equal(ProgressSource.Unavailable, badMapping.ProgressSource);
        Assert.Null(badMapping.Progress); Assert.False(badMapping.IsCurrent);
    }

    [Fact]
    public void 演唱身份不随装载或采样改变但随源时间改变()
    {
        var first = StableIdentities.Occurrence(new("分支:一"), new("行/二"), new("字😀"), new(1000, 2000));
        Assert.Equal(first, StableIdentities.Occurrence(new("分支:一"), new("行/二"), new("字😀"), new(1000, 2000)));
        Assert.NotEqual(first, StableIdentities.Occurrence(new("分支:一"), new("行/二"), new("字😀"), new(1001, 2000)));
        Assert.NotEqual(StableIdentities.Occurrence(new("a:b"), new("c"), new("d"), new(0, 1)),
            StableIdentities.Occurrence(new("a"), new("b:c"), new("d"), new(0, 1)));
        Assert.Equal(StableIdentities.Event("TokenStarted", 1000, first.Value), StableIdentities.Event("TokenStarted", 1000, first.Value));
        Assert.Throws<ArgumentException>(() => StableIdentities.Occurrence(default, new("l"), new("t"), new(0, 1)));
    }

    [Fact]
    public void 静态分支原画不逐字染色但SDK仍读取真实源演唱时间()
    {
        var scene = LyricSceneSampler.SampleFrame(Source(), 1500);
        var staticLine = scene.Lines.Single(item => item.Branch.Id == "b2");
        Assert.False(staticLine.Frame.Tokens[0].IsCurrent);
        Assert.Equal(Rendering.ProgressSource.Unavailable, staticLine.Frame.Tokens[0].Source);
        var token = FrameProjection.Tokens(scene).Single(item => item.TokenId.Value == "b2-a");
        Assert.True(token.Performance.IsCurrent);
        Assert.Equal(.5, token.Performance.Progress);
        Assert.Equal(ProgressSource.Token, token.Performance.ProgressSource);
        Assert.NotNull(token.OccurrenceId);
        Assert.NotNull(staticLine.BindingId);
    }

    [Fact]
    public void 中立SDK没有Core或图形引用且ID之间没有隐式转换()
    {
        var sdk = typeof(TokenId).Assembly;
        Assert.DoesNotContain(sdk.GetReferencedAssemblies(), assembly => assembly.Name!.StartsWith("MakoKaraGenerator", StringComparison.Ordinal)
            || assembly.Name.StartsWith("SkiaSharp", StringComparison.Ordinal) || assembly.Name.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.NotEqual(typeof(TokenId), typeof(NodeId));
        Assert.DoesNotContain(typeof(TokenId).GetMethods(), method => method.Name == "op_Implicit");
        Assert.False(default(TokenId).IsValid);
    }

    [Fact]
    public void 父子矩阵顺序求逆及非有限值拒绝符合合同()
    {
        Affine2 transform = Affine2.Translation(100, 200).Compose(Affine2.ScaleAt(2, new(5, 10)));
        Point2 actual = transform.Map(new(6, 12));
        Assert.Equal(new Point2(107, 214), actual);
        Assert.True(transform.TryInverse(out var inverse));
        Assert.Equal(new Point2(6, 12), inverse.Map(actual));
        Assert.False(default(Affine2).TryInverse(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point2(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Affine2.ScaleAt(0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(5, 5));
    }

    private static ProjectSnapshot Source()
    {
        var line = LyricLine.Create("l1", "请问请", new Core.Time.TimeRange(0, 6000), TimingStatus.Imported,
            [LyricToken.Create("b1-a", "请", new Core.Time.TimeRange(1000, 2000), TimingStatus.Imported),
                LyricToken.Create("b1-missing", "问"),
                LyricToken.Create("b1-b", "请", new Core.Time.TimeRange(4000, 5000), TimingStatus.Imported)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby1", "b1-a", "qǐng")] };
        var translated = LyricLine.Create("l2", "请", new Core.Time.TimeRange(0, 6000), TimingStatus.Imported,
            [LyricToken.Create("b2-a", "请", new Core.Time.TimeRange(1000, 2000), TimingStatus.Imported)]);
        return new() { Name = "source", Lyrics = new() { PrimaryBranchId = "b1", Branches = [LyricBranch.Create("b1", "主", LyricBranchRole.Primary, [line]),
            LyricBranch.Create("b2", "翻译", LyricBranchRole.Translation, [translated])] } };
    }
}
