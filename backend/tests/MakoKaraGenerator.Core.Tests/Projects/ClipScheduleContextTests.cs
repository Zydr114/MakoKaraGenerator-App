using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class ClipScheduleContextTests
{
    private static readonly string[] RetainedMembers = ["line-0", "line-2", "line-3", "line-4"];
    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    [InlineData("excludes-member")]
    [InlineData("wrong-boundary")]
    [InlineData("foreign-boundary")]
    [InlineData("time-order")]
    public void 非法调度上下文在编辑和读盘时报告且不修正(string defect)
    {
        var source = ExclusiveDisplayFixture.Create();
        var program = source.LyricPresentation.FindProgram("primary")!;
        var context = new LyricClipScheduleContext { LineIds = defect switch
        { "empty" => [], "missing" => ["line-2", "missing"], "duplicate" => ["line-2", "line-2"],
            "reversed" => ["line-3", "line-2"], "excludes-member" => ["line-0"], "time-order" => ["line-0", "line-1", "line-2"], _ => ["line-2"] } };
        if (defect == "wrong-boundary") context = context with { IsDefaultSegment = true, PreviousLineId = "line-3" };
        if (defect == "foreign-boundary") context = context with { NextLineId = "line-3" };
        if (defect == "time-order") source = source with { Lyrics = source.Lyrics with
            { Lines = source.Lyrics.Lines.SetItem(0, source.Lyrics.Lines[0] with { Time = new(3_000_000, 4_000_000) }) } };
        Assert.False(SceneTreeEditor.TrySetClip(source, "primary", program.Clips[0] with { ScheduleContext = context }, out var result, out var report));
        Assert.Same(source, result); Assert.True(report.HasErrors);
        using TemporaryDirectory directory = new();
        ProjectStore.Save(source, directory.Path);
        string path = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["lyricPresentation"]!["programs"]![0]!["clips"]![0]!["scheduleContext"] = new JsonObject
        {
            ["lineIds"] = new JsonArray(context.LineIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["isDefaultSegment"] = context.IsDefaultSegment, ["previousLineId"] = context.PreviousLineId, ["nextLineId"] = context.NextLineId,
        };
        File.WriteAllText(path, json.ToJsonString());
        var loaded = ProjectStore.Load(directory.Path);
        Assert.NotNull(loaded.Project); Assert.True(loaded.Report.HasErrors);
        Assert.Equal(context, loaded.Project.LyricPresentation.FindProgram("primary")!.Clips[0].ScheduleContext);
        Assert.True(ProjectValidator.Validate(loaded.Project, ValidationContext.Export).HasErrors);
    }

    [Fact]
    public void 上下文时间跟随源编辑且源删除显式清理无变化保留实例()
    {
        var source = ExclusiveDisplayFixture.Create();
        var program = source.LyricPresentation.FindProgram("primary")!;
        var context = new LyricClipScheduleContext { LineIds = ["line-0", "line-1", "line-2", "line-3", "line-4"] };
        Assert.True(SceneTreeEditor.TrySetClip(source, "primary", program.Clips[0] with { ScheduleContext = context }, out var project, out var report), report.ToSummary());
        Assert.Same(project.LyricPresentation, project.LyricPresentation.RemoveDeletedSources(project.Lyrics));
        Assert.True(LyricTimingEditor.TrySetLineTime(project, "line-4", 8_000_000, 11_000_000, out var timed, out var error), error);
        var clip = timed.LyricPresentation.FindProgram("primary")!.Clips[0];
        Assert.Same(context, clip.ScheduleContext);
        var segment = new LyricDisplaySegment(4_000_000, 6_000_000, clip, LyricDisplaySegmentResolver.Members(timed.Lyrics.PrimaryBranch!, clip));
        Assert.Equal(11_000_000, LyricDisplaySegmentResolver.ScheduleRange(timed.Lyrics.PrimaryBranch!, segment).EndUs);
        var lyrics = timed.Lyrics with { Lines = [.. timed.Lyrics.Lines.Where(line => line.Id != "line-1")] };
        var cleaned = timed.LyricPresentation.RemoveDeletedSources(lyrics);
        Assert.Equal(RetainedMembers, cleaned.FindProgram("primary")!.Clips[0].ScheduleContext!.LineIds);
        Assert.Same(cleaned, cleaned.RemoveDeletedSources(lyrics));
    }

    [Fact]
    public void 当前格式拒绝未知上下文字段而旧程序和格式门禁不改原文件()
    {
        using TemporaryDirectory directory = new();
        var source = ExclusiveDisplayFixture.Create();
        ProjectStore.Save(source, directory.Path);
        string path = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        string original = File.ReadAllText(path);
        foreach (string defect in new[] { "unknown", "application", "schema" })
        {
            var json = JsonNode.Parse(original)!;
            if (defect == "unknown") json["lyricPresentation"]!["programs"]![0]!["clips"]![0]!["scheduleContext"] = new JsonObject
                { ["lineIds"] = new JsonArray("line-2"), ["slotOffset"] = 1 };
            else if (defect == "application") json["applicationVersion"] = "0.42.2";
            else json["schemaVersion"] = Project.CurrentSchemaVersion - 1;
            string edited = json.ToJsonString(); File.WriteAllText(path, edited);
            var loaded = ProjectStore.Load(directory.Path);
            Assert.Null(loaded.Project);
            Assert.True(loaded.Report.HasErrors);
            Assert.Equal(edited, File.ReadAllText(path));
            if (defect == "unknown") Assert.True(loaded.Report.Contains(DiagnosticCode.MalformedJson));
        }
    }
}
