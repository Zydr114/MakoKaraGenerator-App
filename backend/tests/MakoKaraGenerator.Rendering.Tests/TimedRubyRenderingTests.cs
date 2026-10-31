using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class TimedRubyRenderingTests
{
    [Fact]
    public void 静态来源不应用注音高亮但保留其独立源时间()
    {
        var snapshot = TimedRubyFixture.Create();
        var source = snapshot.Lyrics.Lines[0];
        var line = source with { Id = "static-line", Tokens = [source.Tokens[0] with { Id = "static-token" }],
            RubyAnnotations = [source.RubyAnnotations[0] with { Id = "static-ruby", BaseTokenId = "static-token",
                Segments = [.. source.RubyAnnotations[0].Segments.Select(segment => segment with { Id = "static-" + segment.Id })] }] };
        var branch = LyricBranch.Create("static", "Static", LyricBranchRole.Translation, [line]);
        snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Branches = snapshot.Lyrics.Branches.Add(branch) } };
        var staticLine = LyricSceneSampler.SampleFrame(snapshot, 200_000).Lines.Single(entry => entry.Branch.Id == "static");
        Assert.All(staticLine.Frame.RubySegments, segment => {
            Assert.Equal(0d, segment.Progress); Assert.False(segment.IsCurrent); Assert.Equal(ProgressSource.Unavailable, segment.Source);
        });
        var actualSource = LyricSampler.SampleLine(line, 200_000);
        Assert.Equal(.5d, actualSource.RubySegments[0].Progress);
        Assert.Equal(ProgressSource.FromRubySegment, actualSource.RubySegments[0].Source);
        Assert.Equal(new Core.Time.TimeRange(100_000, 300_000), line.RubyAnnotations[0].Segments[0].Time);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 重叠计时和缺失混合注音中缺失段按基础字回退且与显式同时间像素相等(bool transparent)
    {
        var snapshot = TimedRubyFixture.Create([
            RubyTextSegment.Create("early", "せ", new(100_000, 300_000), TimingStatus.Imported),
            RubyTextSegment.Create("missing", "ん"),
            RubyTextSegment.Create("overlap", "あ", new(100_000, 500_000), TimingStatus.Edited)]);
        using SkiaRenderPipeline pipeline = new();
        var segments = snapshot.Lyrics.Lines[0].RubyAnnotations[0].Segments;
        var reference = TimedRubyFixture.Create(segments.SetItem(1, segments[1] with
            { Time = new(400_000, 600_000), TimingStatus = TimingStatus.Edited }));
        var sample = LyricSampler.SampleLine(snapshot.Lyrics.Lines[0], 500_000);
        Assert.Equal(.5d, sample.RubySegments[1].Progress);
        Assert.Equal(ProgressSource.FromToken, sample.RubySegments[1].Source);
        Assert.Equal(1d, sample.RubySegments[0].Progress);
        foreach (long time in new long[] { 250_000, 450_000, 500_000, 550_000, 800_000 })
        {
            using var actual = pipeline.Render(RenderRequest.At(snapshot, time) with { TransparentBackground = transparent });
            using var expected = pipeline.Render(RenderRequest.At(reference, time) with { TransparentBackground = transparent });
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 待唱前奏画面保持注音未唱而源采样保留提前的真实片段时间(bool slots)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(slots));
        var branch = snapshot.Lyrics.Branches[0];
        var line = branch.Lines[0];
        var ruby = line.RubyAnnotations[0] with { Segments =
            [RubyTextSegment.Create("before-line", "か", new(9_000_000, 10_000_000), TimingStatus.Imported)] };
        var changed = line with { RubyAnnotations = [ruby] };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Branches = snapshot.Lyrics.Branches.SetItem(0,
            branch with { Lines = branch.Lines.SetItem(0, changed) }) } };
        var scene = LyricSceneSampler.SampleFrame(snapshot, 9_500_000);
        var preparing = Assert.Single(scene.Lines, entry => entry.Frame.Line?.Id == line.Id);
        Assert.Equal(SceneDisplayPhase.Preparation, preparing.DisplayPhase);
        var actual = Assert.Single(preparing.Frame.RubySegments);
        Assert.Equal(0d, actual.Progress); Assert.False(actual.IsCurrent);
        Assert.Equal(ProgressSource.Unavailable, actual.Source);
        var source = Assert.Single(LyricSampler.SampleLine(changed, scene.TimeUs).RubySegments);
        Assert.Equal(.5d, source.Progress); Assert.True(source.IsCurrent);
        var active = LyricSceneSampler.SampleFrame(snapshot, 12_500_000).Lines.Single(entry => entry.Frame.Line?.Id == line.Id);
        Assert.Equal(SceneDisplayPhase.Performance, active.DisplayPhase);
        Assert.Equal(1d, Assert.Single(active.Frame.RubySegments).Progress);
        Assert.Equal(ProgressSource.FromRubySegment, Assert.Single(active.Frame.RubySegments).Source);
    }

    [Fact]
    public void 独立重叠与缺失片段保持各自时间且不延长行显示窗口()
    {
        var snapshot = TimedRubyFixture.Create([
            RubyTextSegment.Create("before", "せ", new(0, 500_000), TimingStatus.Imported),
            RubyTextSegment.Create("overlap", "ん", new(100_000, 700_000), TimingStatus.Edited),
            RubyTextSegment.Create("missing", "あ"),
            RubyTextSegment.Create("outside", "い", new(900_000, 1_500_000), TimingStatus.Confirmed)]);
        var sample = LyricSampler.Sample(snapshot.Lyrics, 250_000);
        Assert.Equal(0d, sample.Tokens[0].Progress);
        Assert.Equal(.5d, sample.RubySegments[0].Progress);
        Assert.Equal(.25d, sample.RubySegments[1].Progress);
        Assert.True(sample.RubySegments[0].IsCurrent && sample.RubySegments[1].IsCurrent);
        Assert.Equal(ProgressSource.FromToken, sample.RubySegments[2].Source);
        Assert.Equal(0d, sample.RubySegments[2].Progress);
        Assert.False(LyricSampler.Sample(snapshot.Lyrics, 1_100_000).HasLine);
        Assert.Empty(LyricSceneSampler.SampleFrame(snapshot, 1_100_000).Lines);
        var explicitLine = LyricSampler.SampleLine(snapshot.Lyrics.Lines[0], 1_100_000);
        Assert.Equal(1d / 3d, explicitLine.RubySegments[3].Progress, 10);
        Assert.True(explicitLine.RubySegments[3].IsCurrent);
    }

    [Fact]
    public void 损坏片段的Invalid未知或时间状态不一致不能按自身时间或基础字采样()
    {
        var snapshot = TimedRubyFixture.Create();
        var line = snapshot.Lyrics.Lines[0];
        var ruby = line.RubyAnnotations[0] with { Text = "あいうえお", Segments =
        [new() { Id = "invalid", Text = "あ", Time = new(100_000, 900_000), TimingStatus = TimingStatus.Invalid },
            new() { Id = "missing-with-time", Text = "い", Time = new(100_000, 900_000), TimingStatus = TimingStatus.Missing },
            new() { Id = "imported-without-time", Text = "う", TimingStatus = TimingStatus.Imported },
            new() { Id = "unknown", Text = "え", Time = new(100_000, 900_000), TimingStatus = (TimingStatus)999 },
            new() { Id = "invalid-without-time", Text = "お", TimingStatus = TimingStatus.Invalid }] };
        var changed = line with { RubyAnnotations = [ruby] };
        var sample = LyricSampler.SampleLine(changed, 500_000);
        Assert.Equal(.5d, sample.Tokens[0].Progress);
        Assert.All(sample.RubySegments, segment => {
            Assert.Equal(0d, segment.Progress); Assert.False(segment.IsCurrent); Assert.False(segment.IsSung);
            Assert.Equal(ProgressSource.Unavailable, segment.Source);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 损坏Invalid状态即使带合法区间也不在Skia发布注音像素(bool transparent)
    {
        var snapshot = TimedRubyFixture.Create();
        var branch = snapshot.Lyrics.Branches[0]; var line = branch.Lines[0];
        var invalidRuby = line.RubyAnnotations[0] with { Text = "せん", Segments =
            [new() { Id = "invalid", Text = "せん", Time = new(100_000, 900_000), TimingStatus = TimingStatus.Invalid }] };
        ProjectSnapshot WithRuby(ImmutableArray<RubyAnnotation> ruby) => snapshot with {
            Lyrics = snapshot.Lyrics with { Branches = snapshot.Lyrics.Branches.SetItem(0,
                branch with { Lines = branch.Lines.SetItem(0, line with { RubyAnnotations = ruby }) }) } };
        var invalid = WithRuby([invalidRuby]); var absent = WithRuby([]);
        using SkiaRenderPipeline pipeline = new();
        Assert.Empty(Assert.Single(pipeline.GetSceneFrame(invalid, 500_000).TokenGeometries).Ruby);
        using var actual = pipeline.Render(RenderRequest.At(invalid, 500_000) with { TransparentBackground = transparent });
        using var expected = pipeline.Render(RenderRequest.At(absent, 500_000) with { TransparentBackground = transparent });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 时间变化只改变注音颜色且聚合几何与基础像素稳定(bool transparent)
    {
        var snapshot = TimedRubyFixture.Create();
        using SkiaRenderPipeline pipeline = new();
        var first = pipeline.GetSceneFrame(snapshot, 150_000);
        var later = pipeline.GetSceneFrame(snapshot, 250_000);
        var firstGeometry = Assert.Single(first.TokenGeometries);
        var laterGeometry = Assert.Single(later.TokenGeometries);
        Assert.Equal(firstGeometry.BaseGeometry.AdvanceBounds, laterGeometry.BaseGeometry.AdvanceBounds);
        Assert.Equal(firstGeometry.FamilyGeometry.WorldInkCorners.ToArray(), laterGeometry.FamilyGeometry.WorldInkCorners.ToArray());
        Assert.Equal(Assert.Single(firstGeometry.Ruby).Geometry.DrawUnits.ToArray(), Assert.Single(laterGeometry.Ruby).Geometry.DrawUnits.ToArray());
        using var a = pipeline.Render(RenderRequest.At(snapshot, first.TimeUs) with { TransparentBackground = transparent });
        using var b = pipeline.Render(RenderRequest.At(snapshot, later.TimeUs) with { TransparentBackground = transparent });
        var ruby = Assert.Single(firstGeometry.Ruby).Geometry.WorldInkCorners;
        int differences = 0;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++)
        {
            int offset = y * a.Stride + x * 4;
            if (a.PixelMemory.Span.Slice(offset, 4).SequenceEqual(b.PixelMemory.Span.Slice(offset, 4))) continue;
            differences++;
            Assert.InRange(x + .5, ruby.Min(point => point.X) - 1, ruby.Max(point => point.X) + 1);
            Assert.InRange(y + .5, ruby.Min(point => point.Y) - 1, ruby.Max(point => point.Y) + 1);
        }
        Assert.True(differences > 10, "Independent ruby progress must contribute real pixels while the token is unchanged.");
    }

    [Theory]
    [InlineData("e", "\u0301")]
    [InlineData("👩", "\u200d💻")]
    public void 分段落在字簇内部时完整字簇归起始片段且不丢像素(string first, string rest)
        => AssertCluster(first, rest);

    [Fact]
    public void UTF16代理对内部的片段边界不拆字形或丢墨迹()
        => AssertCluster("\ud83d", "\udc69\u200d💻");

    private static void AssertCluster(string first, string rest)
    {
        var split = TimedRubyFixture.Create([
            RubyTextSegment.Create("first", first, new(100_000, 300_000), TimingStatus.Imported),
            RubyTextSegment.Create("rest", rest, new(700_000, 900_000), TimingStatus.Imported)]);
        var whole = TimedRubyFixture.Create([RubyTextSegment.Create("whole", first + rest, new(100_000, 300_000), TimingStatus.Imported)]);
        using SkiaRenderPipeline pipeline = new();
        foreach (long time in new long[] { 0, 200_000, 500_000, 800_000 })
        {
            using var actual = pipeline.Render(RenderRequest.At(split, time) with { TransparentBackground = true });
            using var expected = pipeline.Render(RenderRequest.At(whole, time) with { TransparentBackground = true });
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        }
    }

    [Fact]
    public void 只改片段时间的新快照使帧缓存失效且往返寻址可复现()
    {
        var source = TimedRubyFixture.Create();
        var changed = TimedRubyFixture.Create([
            RubyTextSegment.Create("early", "せ", new(600_000, 800_000), TimingStatus.Imported),
            RubyTextSegment.Create("late", "ん", new(700_000, 900_000), TimingStatus.Edited)]);
        using SkiaRenderPipeline skia = new();
        CachedRenderPipeline cache = new(skia);
        using var first = cache.Render(RenderRequest.At(source, 250_000));
        using var edited = cache.Render(RenderRequest.At(changed, 250_000));
        using var restored = cache.Render(RenderRequest.At(source, 250_000));
        Assert.False(first.PixelMemory.Span.SequenceEqual(edited.PixelMemory.Span));
        Assert.Equal(first.PixelMemory.ToArray(), restored.PixelMemory.ToArray());
        Assert.Equal(skia.GetSceneFrame(source, 250_000).TokenGeometries[0].FamilyGeometry.WorldInkCorners.ToArray(),
            skia.GetSceneFrame(changed, 250_000).TokenGeometries[0].FamilyGeometry.WorldInkCorners.ToArray());
    }
}
