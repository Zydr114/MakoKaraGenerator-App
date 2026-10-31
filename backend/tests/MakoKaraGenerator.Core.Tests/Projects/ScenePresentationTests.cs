using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class ScenePresentationTests
{
    [Fact]
    public void 展示仅保存到工程且普通双行切换保留外层及行内部配置()
    {
        using TemporaryDirectory directory = new();
        var source = Project.Create("场景", lyrics: LyricsDocument.Create([LyricLine.Create("line", "文字")]));
        var project = source.WithLinePresentation("line", ScenePlacement.Create(new(.25, .6)), LineAnimation.Default);
        var definition = project.LyricPresentation.Resolve("primary") with
        {
            Placement = ScenePlacement.Create(new(1.1, -.1), ScenePlacementAnchor.TopLeft, 30, .8),
            ReferenceRect = new(.8, .6), SlotSequenceProfile = SlotSequenceAnimationProfile.Default,
        };
        project = PresentationEditor.SetBranch(project, definition);
        Assert.Same(source.Lyrics, project.Lyrics);
        ProjectStore.Save(project, directory.Path);
        var reopened = ProjectStore.Load(directory.Path);
        Assert.False(reopened.Report.HasErrors, reopened.Report.ToSummary());
        Assert.Equal(project, reopened.Project);
        Assert.Equal(project.GetHashCode(), reopened.Project!.GetHashCode());
        var lyricJson = ProjectFileFixture.ReadLyrics(directory.Path);
        foreach (string key in new[] { "placement", "enterAnimation", "defaultPlacement", "dualLineProfile", "dualLineGroupTransform", "animationPolicy" })
            Assert.DoesNotContain('"' + key + '"', lyricJson, StringComparison.Ordinal);
        var ordinary = PresentationEditor.SetBranch(reopened.Project, definition with { SlotSequenceProfile = null });
        Assert.Equal(definition.Placement, ordinary.LyricPresentation.Resolve("primary").Placement);
        Assert.Equal(definition.Lines.ToArray(), ordinary.LyricPresentation.Resolve("primary").Lines.ToArray());
        Assert.Same(ordinary, PresentationEditor.SetBranch(ordinary, ordinary.LyricPresentation.Resolve("primary") with
            { Lines = [.. definition.Lines] }));
    }

    [Theory]
    [InlineData("line", "placement")]
    [InlineData("line", "enterAnimation")]
    [InlineData("branch", "defaultPlacement")]
    [InlineData("branch", "dualLineGroupTransform")]
    [InlineData("branch", "animationPolicy")]
    public void 当前版本歌词拒绝旧展示键(string scope, string key)
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(Project.Create("当前", lyrics: LyricsDocument.Create([LyricLine.Create("line", "文字")])), directory.Path);
        string path = ProjectFileFixture.LyricsPath(directory.Path);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var target = scope == "line" ? json["branches"]![0]!["lines"]![0]! : json["branches"]![0]!;
        target[key] = new JsonObject();
        ProjectFileFixture.PublishLyrics(directory.Path, json.ToJsonString());
        var rejected = ProjectStore.Load(directory.Path);
        Assert.Null(rejected.Project);
        Assert.True(rejected.Report.Contains(MakoKaraGenerator.Core.Diagnostics.DiagnosticCode.MalformedJson));
    }

    [Fact]
    public void 缺失展示定义或失效源引用均报告而不修正()
    {
        using TemporaryDirectory directory = new();
        var project = Project.Create("引用", lyrics: LyricsDocument.Create([LyricLine.Create("line", "文字")]));
        ProjectStore.Save(project, directory.Path);
        string path = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json.AsObject().Remove("lyricPresentation");
        File.WriteAllText(path, json.ToJsonString());
        var rejected = ProjectStore.Load(directory.Path);
        Assert.NotNull(rejected.Project);
        Assert.True(rejected.Report.Contains(MakoKaraGenerator.Core.Diagnostics.DiagnosticCode.InvalidDisplayProgram));
        var invalid = project with { LyricPresentation = LyricPresentationDefinition.FromBranches([new() { BranchId = "primary",
            Lines = [new() { LineId = "absent", LocalTransform = LyricLocalTransform.Neutral }] }]) };
        Assert.Contains(ProjectValidator.Validate(invalid, ValidationContext.Export).Issues,
            issue => issue.Code == DiagnosticCode.InvalidLinePlacement);
        Assert.Equal("absent", invalid.LyricPresentation.Branches[0].Lines[0].LineId);
    }

    [Fact]
    public void 槽位布局允许单句微调而槽位修改不改容器及另一槽位()
    {
        var project = Project.Create("目标", lyrics: LyricsDocument.Create([LyricLine.Create("line", "文字")]));
        project = PresentationEditor.SetBranch(project, new() { BranchId = "primary", SlotSequenceProfile = SlotSequenceAnimationProfile.Default });
        Assert.True(SceneTreeEditor.TrySetLocalTransform(project, "primary", "line", new() { OffsetXRatio = .1 }, out var adjusted, out _));
        Assert.Equal(.1, adjusted.LyricPresentation.FindLine("line")!.LocalTransform!.OffsetXRatio);
        Assert.Equal(project.LyricPresentation.Nodes.ToArray(), adjusted.LyricPresentation.Nodes.ToArray());
        var slot = project.LyricPresentation.FindNode(project.LyricPresentation.Nodes[1].Layout!.SlotIds[0])!;
        Assert.True(SceneTreeEditor.TrySetNode(project,slot with { Placement = ScenePlacement.Create(new(.1,.2)) },out var changed,out _));
        Assert.Equal(project.LyricPresentation.Branches[0].Placement, changed.LyricPresentation.Branches[0].Placement);
        Assert.Equal(project.LyricPresentation.Branches[0].SlotSequenceProfile!.Slots[1], changed.LyricPresentation.Branches[0].SlotSequenceProfile!.Slots[1]);
        Assert.Same(project.Lyrics, changed.Lyrics);
        Assert.True(SceneTreeEditor.TrySetNode(changed,changed.LyricPresentation.FindNode(slot.Id)!,out var unchanged,out _));
        Assert.Same(changed, unchanged);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void 无效参考矩形整次编辑被拒绝(double width, double height)
    {
        var project = Project.Create("参考矩形");
        Assert.Same(project, PresentationEditor.SetBranch(project, new() { BranchId = "primary", ReferenceRect = new(width, height) }));
    }
}
