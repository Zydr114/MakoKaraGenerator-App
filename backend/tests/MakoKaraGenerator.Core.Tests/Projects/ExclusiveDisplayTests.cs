using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class ExclusiveDisplayTests
{
    [Fact]
    public void 通用树当前格式保存重开且没有旧分支结构或重复槽位位置()
    {
        using TemporaryDirectory directory = new();
        var project = ExclusiveDisplayFixture.Create();
        ProjectStore.Save(project, directory.Path);
        var loaded = ProjectStore.Load(directory.Path);
        Assert.False(loaded.Report.HasErrors, loaded.Report.ToSummary());
        Assert.Equal(project, loaded.Project);
        Assert.Equal(project.GetHashCode(), loaded.Project!.GetHashCode());
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, ProjectJson.ProjectFileName)))!;
        var tree = json["lyricPresentation"]!;
        Assert.Null(tree["branches"]);
        Assert.NotNull(tree["nodes"]); Assert.NotNull(tree["programs"]); Assert.NotNull(tree["lineOverrides"]);
        var layout = tree["nodes"]!.AsArray().Single(node => node!["layout"]?["kind"]?.GetValue<string>() == "SlotSequence")!["layout"]!;
        Assert.Equal(2, layout["slotIds"]!.AsArray().Count);
        Assert.Null(layout["slotAnimation"]!["slots"]);
        Assert.Null(tree["programs"]![0]!["clips"]![0]!["startUs"]);
        tree["branches"] = new JsonArray();
        File.WriteAllText(Path.Combine(directory.Path, ProjectJson.ProjectFileName), json.ToJsonString());
        Assert.True(ProjectStore.Load(directory.Path).Report.HasErrors);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing-parent")]
    [InlineData("duplicate-id")]
    [InlineData("missing-line")]
    [InlineData("missing-time")]
    [InlineData("wrong-container")]
    [InlineData("duplicate-slot")]
    public void 无效树预演拒绝且原工程不变(string defect)
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var root = tree.Nodes[0]; var program = tree.Programs[0];
        tree = defect switch
        {
            "cycle" => tree with { Nodes = tree.Nodes.SetItem(0, root with { ParentId = ExclusiveDisplayFixture.CenterId }) },
            "missing-parent" => tree with { Nodes = tree.Nodes.SetItem(0, root with { ParentId = "absent" }) },
            "duplicate-id" => tree with { Nodes = tree.Nodes.Add(root) },
            "missing-time" => tree,
            "missing-line" => tree with { Programs = tree.Programs.SetItem(0, program with { Clips = [program.Clips[0] with { LineIds = ["absent"] }] }) },
            "wrong-container" => tree with { Programs = tree.Programs.SetItem(0, program with { Clips = [program.Clips[0] with { ContainerId = LyricSceneIds.Root("translation") }] }) },
            _ => tree with { Nodes = tree.Nodes.SetItem(1, tree.Nodes[1] with { Layout = tree.Nodes[1].Layout! with { SlotIds = [tree.Nodes[1].Layout!.SlotIds[0], tree.Nodes[1].Layout!.SlotIds[0]] } }) },
        };
        if (defect == "missing-time")
            project = project with { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.SetItem(2, project.Lyrics.Lines[2] with { Time = null }) } };
        Assert.False(PresentationEditor.TrySetTree(project, tree, out var result, out var report));
        Assert.True(report.HasErrors); Assert.Same(project, result);
        Assert.Equal(5, project.Lyrics.Lines.Length);
    }

    [Theory]
    [InlineData(0, 4_000_000, true)]
    [InlineData(0, 4_000_001, false)]
    public void 演唱时间半开相邻合法而独占冲突明确拒绝(long start, long end, bool legal)
    {
        var project = ExclusiveDisplayFixture.Create();
        Assert.Equal(legal, LyricTimingEditor.TrySetLineTime(project, "line-0", start, end, out var changed, out var reason));
        if (legal) Assert.Equal(end, changed.Lyrics.Lines[0].Time!.Value.EndUs);
        else { Assert.Same(project, changed); Assert.Contains("重叠", reason, StringComparison.Ordinal); }
        Assert.Equal(2_000_000, project.Lyrics.Lines[0].Time!.Value.EndUs);
    }

    [Fact]
    public void 平移到独占区间拒绝并保留源行元素和配置引用()
    {
        var project = ExclusiveDisplayFixture.Create();
        Assert.False(LyricTimingEditor.TryShiftLineAndTokens(project, "line-0", 4_000_000, out var rejected, out _));
        Assert.Same(project, rejected);
        var invalid = project with { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.SetItem(0,
            project.Lyrics.Lines[0] with { Time = new(0, 4_000_001) }) } };
        Assert.Contains(PresentationValidator.Validate(invalid).Issues, issue => issue.Code == DiagnosticCode.PresentationTimeConflict);
        Assert.Throws<InvalidOperationException>(() => LyricDisplaySegmentResolver.Resolve(invalid.Lyrics.PrimaryBranch!, invalid.LyricPresentation.Programs[0], 5_000_000));
        using TemporaryDirectory directory = new();
        List<ProjectIssue> issues = []; ProjectStore.Save(invalid, directory.Path, issues);
        Assert.Contains(issues, issue => issue.Code == DiagnosticCode.PresentationTimeConflict);
        Assert.True(ProjectStore.Load(directory.Path).Report.HasErrors);
        Assert.Equal(4_000_001, invalid.Lyrics.Lines[0].Time!.Value.EndUs);
    }

    [Fact]
    public void 无变化预演不写工程且删除源行只清理关联()
    {
        var project = ExclusiveDisplayFixture.Create();
        Assert.True(PresentationEditor.TrySetTree(project, project.LyricPresentation with { Nodes = [.. project.LyricPresentation.Nodes] }, out var unchanged, out _));
        Assert.Same(project, unchanged);
        var lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.RemoveAt(2) };
        var pruned = project.LyricPresentation.RemoveDeletedSources(lyrics);
        Assert.Empty(pruned.Programs[0].Clips); Assert.Empty(pruned.LineOverrides);
        Assert.NotNull(pruned.FindNode(ExclusiveDisplayFixture.CenterId));
        Assert.Same(project.LyricPresentation, project.LyricPresentation.RemoveDeletedSources(project.Lyrics));
    }

    [Fact]
    public void 现有窗口适配器保留自定义容器和槽位身份及独占片段()
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var regular = tree.Nodes[1]; var slotIds = regular.Layout!.SlotIds;
        tree = tree with { Nodes = [.. tree.Nodes.Select(node => node.Id == slotIds[0] ? node with { Id = "custom/slot-a" } : node)],
            Programs = [.. tree.Programs], };
        tree = tree with { Nodes = tree.Nodes.SetItem(1, regular with { Layout = regular.Layout with { SlotIds = ["custom/slot-a", slotIds[1]] } }) };
        project = project with { LyricPresentation = tree };
        var changed = PresentationEditor.SetBranch(project, tree.Resolve("primary") with { Placement = ScenePlacement.Create(new(.6, .5)) });
        Assert.Equal("custom/slot-a", changed.LyricPresentation.FindNode(regular.Id)!.Layout!.SlotIds[0]);
        Assert.Single(changed.LyricPresentation.Programs[0].Clips);
        Assert.Same(project.Lyrics, changed.Lyrics);
    }

    [Fact]
    public void 片段解析按分支与程序复用_程序变化即重新解析_校验失败每次抛出()
    {
        var project = ExclusiveDisplayFixture.Create();
        var branch = project.Lyrics.PrimaryBranch!;
        var program = project.LyricPresentation.Programs[0];
        static string[] Ids(LyricDisplaySegment segment) => [.. segment.Lines.Select(static line => line.Id)];
        // 旧实现每次调用都按全曲重建跨度、校验并复制成员；同一（分支，程序）及值相同的程序副本必须复用同一段对象。
        var first = LyricDisplaySegmentResolver.Resolve(branch, program, 1_000_000);
        Assert.Same(first, LyricDisplaySegmentResolver.Resolve(branch, program, 3_000_000));
        Assert.Equal(["line-0", "line-1"], Ids(first));
        var clip = LyricDisplaySegmentResolver.Resolve(branch, program, 5_000_000);
        Assert.Equal(ExclusiveDisplayFixture.ClipId, clip.Clip?.Id);
        Assert.Same(clip, LyricDisplaySegmentResolver.Resolve(branch, program with { }, 4_500_000));
        Assert.Equal(["line-3", "line-4"], Ids(LyricDisplaySegmentResolver.Resolve(branch, program, 9_000_000)));

        // 同一分支换程序：新增片段占走 line-0，默认段随之改变；换回原程序也按原程序解析。
        var changed = program with { Clips = program.Clips.Add(new()
            { Id = "first-clip", LineIds = ["line-0"], ContainerId = ExclusiveDisplayFixture.CenterId }) };
        Assert.Equal("first-clip", LyricDisplaySegmentResolver.Resolve(branch, changed, 1_000_000).Clip?.Id);
        Assert.Equal(["line-1"], Ids(LyricDisplaySegmentResolver.Resolve(branch, changed, 3_000_000)));
        Assert.Equal(["line-0", "line-1"], Ids(LyricDisplaySegmentResolver.Resolve(branch, program, 1_000_000)));

        // 校验失败不记忆：同一源行重复分配的程序每次都抛出。
        var duplicated = program with { Clips = program.Clips.Add(new()
            { Id = "duplicate-clip", LineIds = ["line-2"], ContainerId = ExclusiveDisplayFixture.CenterId }) };
        for (int attempt = 0; attempt < 2; attempt++)
            Assert.Throws<InvalidOperationException>(() => LyricDisplaySegmentResolver.Resolve(branch, duplicated, 0));
    }
}
