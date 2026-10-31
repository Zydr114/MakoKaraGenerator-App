using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class LinePlacementEditorTests
{
    [Theory]
    [InlineData(ScenePlacementAnchor.Center)]
    [InlineData(ScenePlacementAnchor.TopLeft)]
    [InlineData(ScenePlacementAnchor.TopCenter)]
    [InlineData(ScenePlacementAnchor.BottomRight)]
    [InlineData(ScenePlacementAnchor.LeftCenter)]
    [InlineData(ScenePlacementAnchor.RightCenter)]
    public void 独立位置创建保存重开结构相等且不丢属性(ScenePlacementAnchor anchor)
    {
        using TemporaryDirectory directory = new();
        ScenePlacement placement = ScenePlacement.Create(new(.27, .63), anchor, 17.5, 1.25);
        LyricLine line = LyricLine.Create("line", "甲", new TimeRange(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "甲", new TimeRange(0, 1_000_000), TimingStatus.Edited)]);
        Project project = Project.Create("位置", lyrics: LyricsDocument.Create([line])).WithLinePresentation("line", placement);
        ProjectStore.Save(project, directory.Path);
        ProjectLoadResult loaded = ProjectStore.Load(directory.Path);
        Assert.True(loaded.Loaded, loaded.Report.ToSummary());
        Assert.Equal(project, loaded.Project);
        Assert.Equal(project.LyricPresentation.FindLine("line")!.LocalTransform, loaded.Project!.LyricPresentation.FindLine("line")!.LocalTransform);
        Assert.Equal(anchor, loaded.Project.LyricPresentation.Resolve("primary").DefaultPlacement!.Anchor);
        Assert.Same(line, project.Lyrics.Lines[0]);
        Assert.Equal(line.GetHashCode(), loaded.Project.Lyrics.Lines[0].GetHashCode());
        JsonNode data = JsonNode.Parse(ProjectFileFixture.ReadLyrics(directory.Path))!;
        Assert.Equal(LyricsDocument.CurrentSchemaVersion, data["schemaVersion"]!.GetValue<int>());
        Assert.Null(data["branches"]![0]!["lines"]![0]!["placement"]);
        JsonNode presentation = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, ProjectJson.ProjectFileName)))!;
        Assert.Equal(anchor.ToString(), presentation["lyricPresentation"]!["nodes"]![1]!["layout"]!["defaultPlacement"]!["anchor"]!.GetValue<string>());
    }

    [Fact]
    public void 批量拒绝失效目标或非法位置并且重复应用返回原实例()
    {
        Project project = CreateProject();
        var valid = new Dictionary<string, LyricLocalTransform?> { ["a"] = LyricLocalTransform.Neutral };
        Assert.True(LinePlacementEditor.TryApply(project, valid, out Project changed));
        LyricLine copy = LyricLineDuplicator.Duplicate(changed.Lyrics.Lines[0], "copy", () => "copy-token");
        var copiedProject = changed with { Lyrics = changed.Lyrics with { Lines = changed.Lyrics.Lines.Add(copy) } };
        copiedProject = PresentationEditor.CopyLine(copiedProject, changed.Lyrics.PrimaryBranchId, "a", "copy");
        Assert.Equal(changed.LyricPresentation.FindLine("a")!.LocalTransform, copiedProject.LyricPresentation.FindLine("copy")!.LocalTransform);
        Assert.Equal(changed.Lyrics.Lines[0].StyleOverride, copy.StyleOverride);
        Assert.True(LinePlacementEditor.TryApply(changed, valid, out Project unchanged));
        Assert.Same(changed, unchanged);
        Assert.False(LinePlacementEditor.TryApply(project, new Dictionary<string, LyricLocalTransform?>
            { ["a"] = LyricLocalTransform.Neutral, ["missing"] = LyricLocalTransform.Neutral }, out Project missing));
        Assert.Same(project, missing);
        Assert.False(LinePlacementEditor.TryApply(project, new Dictionary<string, LyricLocalTransform?>
            { ["a"] = LyricLocalTransform.Neutral, ["b"] = LyricLocalTransform.Neutral with { Scale = double.NaN } }, out Project invalid));
        Assert.Same(project, invalid);
        Assert.Same(project.Lyrics.Lines[1], changed.Lyrics.Lines[1]);
        Assert.Equal(project.Lyrics.Lines[0].Time, changed.Lyrics.Lines[0].Time);
        Assert.Same(project.Lyrics.Lines[0].StyleOverride, changed.Lyrics.Lines[0].StyleOverride);
        Assert.True(LinePlacementEditor.TryApply(changed, new Dictionary<string, LyricLocalTransform?> { ["a"] = null }, out Project reset));
        Assert.Equal(project, reset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 平均分布按行序且保留另一个坐标锚点角度和缩放(bool vertical)
    {
        double[] coordinates = [.8, .2, .7, .4];
        ScenePlacement[] placements = coordinates.Select(value => ScenePlacement.Create(
            new(value, value), ScenePlacementAnchor.TopCenter, 23, 1.2)).ToArray();
        var distributed = LinePlacementEditor.Distribute(placements, vertical);
        double[] expected = [.2, .4, .6, .8];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], vertical ? distributed[i].Position.Y : distributed[i].Position.X, 10);
            Assert.Equal(placements[i].Position.X, vertical ? distributed[i].Position.X : distributed[i].Position.Y);
            Assert.Equal(placements[i].Anchor, distributed[i].Anchor);
            Assert.Equal(placements[i].RotationDegrees, distributed[i].RotationDegrees);
            Assert.Equal(placements[i].Scale, distributed[i].Scale);
        }
    }

    [Fact]
    public void 读取非法位置报告诊断且保留用户原始数值()
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(CreateProject().WithLinePresentation("a", ScenePlacement.Default), directory.Path);
        string path = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        json["lyricPresentation"]!["lineOverrides"]![0]!["localTransform"] = JsonNode.Parse("""{"offsetXRatio":1.7,"offsetYRatio":0.4,"rotationDegrees":15,"scale":-2}""");
        File.WriteAllText(path, json.ToJsonString());
        ProjectLoadResult loaded = ProjectStore.Load(directory.Path);
        Assert.Contains(loaded.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidLinePlacement);
        Assert.Equal(1.7, loaded.Project!.LyricPresentation.FindLine("a")!.LocalTransform!.OffsetXRatio);
        Assert.Equal(-2, loaded.Project.LyricPresentation.FindLine("a")!.LocalTransform!.Scale);
        Assert.Contains(ProjectValidator.Validate(loaded.Project, ValidationContext.Export).Issues,
            issue => issue.Code == DiagnosticCode.InvalidLinePlacement);
    }
    private static Project CreateProject() => Project.Create("位置", lyrics: LyricsDocument.Create([
        LyricLine.Create("a", "甲", new TimeRange(0, 1_000_000), TimingStatus.Edited,
            styleOverride: new LyricStyleOverride { UnsungColor = RgbaColor.White }),
        LyricLine.Create("b", "乙", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited),
    ]));
}
