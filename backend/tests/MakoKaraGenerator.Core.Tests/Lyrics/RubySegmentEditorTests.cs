using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class RubySegmentEditorTests
{
    [Fact]
    public void 完整替换同步正文保留属性允许重叠且同值复用原引用()
    {
        var ruby = RubyAnnotation.Create("ruby", "token", "せん", "ja", .7, 3, RubyAlignment.End);
        RubyTextSegment[] segments = [RubyTextSegment.Create("a", "せ", new(50, 300), TimingStatus.Imported), RubyTextSegment.Create("b", "ん", new(100, 500), TimingStatus.Edited)];
        Assert.True(RubySegmentEditor.TryReplaceSegments(ruby, [.. segments], out var changed));
        Assert.Equal("せん", changed.Text); Assert.Equal(ruby with { Segments = changed.Segments }, changed);
        Assert.True(RubySegmentEditor.TryReplaceSegments(changed, [.. segments], out var noOp)); Assert.Same(changed, noOp);
        Assert.True(RubySegmentEditor.TryReplaceSegments(changed, [segments[1], segments[0]], out var reordered)); Assert.Equal("んせ", reordered.Text);
        Assert.Same(segments[0], reordered.Segments[1]);
        Assert.False(RubySegmentEditor.TryReplaceSegments(changed, [], out var empty)); Assert.Same(changed, empty);
        Assert.False(RubySegmentEditor.TryReplaceSegments(changed, [segments[0], segments[0]], out var duplicate)); Assert.Same(changed, duplicate);
    }

    [Fact]
    public void 长注音身份派生可用于HTTP目标且稳定不改变原注音身份()
    {
        string id = new('字', 128); var ruby = RubyAnnotation.Create(id, "token", "せ");
        Assert.Equal(id, ruby.Id); Assert.InRange(ruby.Segments[0].Id.Length, 1, 128);
        Assert.Equal(ruby.Segments[0].Id, RubyAnnotation.Create(id, "other", "ん").Segments[0].Id);
        Assert.NotEqual(ruby.Segments[0].Id, RubyAnnotation.Create(id + "2", "token", "せ").Segments[0].Id);
        Assert.Equal("short-segment-1", RubyAnnotation.Create("short", "token", "せ").Segments[0].Id);
    }

    [Fact]
    public void 基础删除同步清理而附着组保留完整身份与原引用()
    {
        var keep = RubyAnnotation.Create("keep", "token", "せ"); var remove = RubyAnnotation.Create("remove", "deleted", "ん");
        var line = LyricLine.Create("line", "歌", tokens: [LyricToken.Create("token", "歌")]) with { RubyAnnotations = [keep, remove] };
        var clean = RubySegmentEditor.RemoveDetachedGroups(line);
        Assert.Same(keep, Assert.Single(clean.RubyAnnotations)); Assert.Same(clean, RubySegmentEditor.RemoveDetachedGroups(clean));
        var split = LyricLineTokenSplitter.Apply(line with { Text = "歌声", Tokens = [line.Tokens[0] with { Text = "歌声" }] }, ["歌", "声"], false, () => "new");
        Assert.All(split.Line.RubyAnnotations, ruby => Assert.Contains(split.Line.Tokens, token => token.Id == ruby.BaseTokenId));
    }

    [Theory]
    [InlineData(ValidationContext.Open)] [InlineData(ValidationContext.Import)] [InlineData(ValidationContext.Export)]
    public void 原始非法时间明确诊断且有效超父时间保留警告无时间片段不误报缺失(ValidationContext context)
    {
        var invalid = RubyTextSegment.Create("bad", "せ", new(-1, 0), TimingStatus.Invalid);
        var outside = RubyTextSegment.Create("outside", "ん", new(0, 500), TimingStatus.Imported);
        var fallback = RubyTextSegment.Create("missing", "っ");
        var ruby = RubyAnnotation.Create("ruby", "token", "せんっ") with { Segments = [invalid, outside, fallback] };
        var line = LyricLine.Create("line", "歌", new(100, 400), TimingStatus.Confirmed, [LyricToken.Create("token", "歌", new(100, 200), TimingStatus.Confirmed)]) with { RubyAnnotations = [ruby] };
        var project = Project.Create("validation", lyrics: LyricsDocument.Create([line]));
        var report = ProjectValidator.Validate(project, context);
        Assert.Contains(report.Issues, issue => issue.Code == DiagnosticCode.InvalidTimeRange && issue.Location!.EndsWith("segments[0]", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Code == DiagnosticCode.RubySegmentOutsideParent && issue.Severity == IssueSeverity.Warning && issue.Location!.EndsWith("segments[1]", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Issues, issue => issue.Code == DiagnosticCode.MissingTime && issue.Location!.Contains("segments", StringComparison.Ordinal));
        Assert.Same(invalid, project.Lyrics.Lines[0].RubyAnnotations[0].Segments[0]);
    }

    [Fact]
    public void 行平移同步有效注音时间并原子拒绝负时间和溢出保留原始非法数据()
    {
        var ruby = RubyAnnotation.Create("ruby", "token", "せんっ") with { Segments = [RubyTextSegment.Create("a", "せ", new(50, 600), TimingStatus.Imported), RubyTextSegment.Create("b", "ん", new(-2, -1), TimingStatus.Invalid), RubyTextSegment.Create("c", "っ")] };
        var line = LyricLine.Create("line", "歌", new(100, 400), TimingStatus.Imported, [LyricToken.Create("token", "歌", new(100, 200), TimingStatus.Imported)]) with { RubyAnnotations = [ruby] };
        var source = Project.Create("shift", lyrics: LyricsDocument.Create([line]));
        Assert.True(LyricTimingEditor.TryShiftLineAndTokens(source, "line", 20, out var shifted, out var reason), reason);
        var segments = shifted.Lyrics.Lines[0].RubyAnnotations[0].Segments;
        Assert.Equal(new TimeRange(70, 620), segments[0].Time); Assert.Equal(TimingStatus.Edited, segments[0].TimingStatus);
        Assert.Same(ruby.Segments[1], segments[1]); Assert.Same(ruby.Segments[2], segments[2]);
        Assert.False(LyricTimingEditor.TryShiftLineAndTokens(source, "line", -60, out var rejected, out _)); Assert.Same(source, rejected);
        var large = ruby with { Segments = ruby.Segments.SetItem(0, ruby.Segments[0] with { Time = new(50, long.MaxValue) }) };
        var overflow = source with { Lyrics = LyricsDocument.Create([line with { RubyAnnotations = [large] }]) };
        Assert.False(LyricTimingEditor.TryShiftLineAndTokens(overflow, "line", 1, out rejected, out _)); Assert.Same(overflow, rejected);
    }
}
