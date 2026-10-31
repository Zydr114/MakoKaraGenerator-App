using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class SceneTreeEditorTests
{
    [Fact]
    public void 多句片段源时间更新保持身份且删除后回默认容器()
    {
        var project = ExclusiveDisplayFixture.Create();
        var last = project.Lyrics.Lines[3];
        project = project with { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.SetItem(3,last with
            { Tokens = last.Tokens.SetItem(0,last.Tokens[0] with { Time = new(6_000_000,7_000_000) }) }) } };
        var clip = project.LyricPresentation.Programs[0].Clips[0] with { LineIds = ["line-2","line-3"], Name = "副歌两句" };
        Assert.True(SceneTreeEditor.TrySetClip(project,"primary",clip,out var changed,out _));
        var range = LyricDisplaySegmentResolver.Range(changed.Lyrics.PrimaryBranch!,clip);
        Assert.Equal(4_000_000,range.StartUs); Assert.Equal(8_000_000,range.EndUs);
        Assert.True(LyricTimingEditor.TrySetLineTime(changed,"line-3",6_000_000,7_500_000,out var timed,out _));
        Assert.Equal(7_500_000,LyricDisplaySegmentResolver.Range(timed.Lyrics.PrimaryBranch!,clip).EndUs);
        Assert.Same(clip,timed.LyricPresentation.Programs[0].Clips[0]);
        Assert.True(SceneTreeEditor.TryDeleteClip(timed,"primary",clip.Id,out var removed,out _));
        var segment = LyricDisplaySegmentResolver.Resolve(removed.Lyrics.PrimaryBranch!,removed.LyricPresentation.Programs[0],5_000_000);
        Assert.Null(segment.Clip); Assert.Contains(segment.Lines,l => l.Id == "line-2");
        Assert.Same(timed.Lyrics,removed.Lyrics);
    }
    [Theory]
    [InlineData("gap")]
    [InlineData("reversed")]
    [InlineData("duplicate")]
    [InlineData("missing-time")]
    [InlineData("singing-conflict")]
    public void 不合法片段明确拒绝且不改变工程(string defect)
    {
        var project = ExclusiveDisplayFixture.Create();
        var clip = project.LyricPresentation.Programs[0].Clips[0] with { LineIds = defect switch
        { "gap" => ["line-2","line-4"],"reversed" => ["line-3","line-2"],"duplicate" => ["line-2","line-2"],_ => ["line-2","line-3"] } };
        if (defect == "missing-time") project = project with { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.SetItem(3,project.Lyrics.Lines[3] with { Time = null }) } };
        if (defect == "singing-conflict") project = project with { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.SetItem(4,project.Lyrics.Lines[4] with { Time = new(7_000_000,10_000_000) }) } };
        Assert.False(SceneTreeEditor.TrySetClip(project,"primary",clip,out var result,out var report));
        Assert.Same(project,result); Assert.True(report.HasErrors);
    }
    [Fact]
    public void 复制子树重建节点及槽位引用不复制任何绑定()
    {
        var project = ExclusiveDisplayFixture.Create(); var original = project.LyricPresentation.Programs[0].DefaultContainerId;
        Assert.True(SceneTreeEditor.TryCopySubtree(project,original,"布局副本",out string id,out var copy,out _));
        var node = copy.LyricPresentation.FindNode(id)!;
        Assert.NotEqual(original,id); Assert.Equal("布局副本",node.Name);
        Assert.Equal(2,node.Layout!.SlotIds.Length);
        foreach (string slot in node.Layout.SlotIds) { Assert.Equal(id,copy.LyricPresentation.FindNode(slot)!.ParentId); Assert.Null(project.LyricPresentation.FindNode(slot)); }
        Assert.Equal(project.LyricPresentation.Programs,copy.LyricPresentation.Programs);
        Assert.Same(project.Lyrics,copy.Lyrics);
        Assert.True(SceneTreeEditor.TryDeleteNode(copy,id,out var deleted,out _));
        Assert.Equal(project,deleted);
        Assert.False(SceneTreeEditor.TryDeleteNode(project,original,out var rejected,out _)); Assert.Same(project,rejected);
    }
    [Fact]
    public void 槽位新增重排删除与同级树顺序互相独立()
    {
        var project = ExclusiveDisplayFixture.Create(); string container = project.LyricPresentation.Programs[0].DefaultContainerId;
        var slot = new SceneContainerDefinition { Id = "slot-3", ParentId = container, Placement = ScenePlacement.Create(new(.5,.5)) };
        Assert.True(SceneTreeEditor.TryAddSlot(project,container,slot,out var added,out _));
        string[] reversed = added.LyricPresentation.FindNode(container)!.Layout!.SlotIds.Reverse().ToArray();
        Assert.True(SceneTreeEditor.TryReorderSlots(added,container,reversed,out var reordered,out _));
        Assert.Equal(added.LyricPresentation.Nodes.Select(n => n.Id),reordered.LyricPresentation.Nodes.Select(n => n.Id));
        Assert.True(SceneTreeEditor.TryReorderChildren(reordered,container,reversed,out var orderedTree,out _));
        Assert.Equal(reversed,orderedTree.LyricPresentation.FindNode(container)!.Layout!.SlotIds);
        Assert.True(SceneTreeEditor.TryDeleteNode(orderedTree,slot.Id,out var removed,out _));
        Assert.Equal(2,removed.LyricPresentation.FindNode(container)!.Layout!.SlotIds.Length);
        Assert.True(SceneTreeEditor.TryDeleteNode(removed,reversed[1],out var one,out _));
        Assert.False(SceneTreeEditor.TryDeleteNode(one,reversed[2],out var zero,out _)); Assert.Same(one,zero);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void 跨旋转缩放父组重挂保持所有子树世界坐标(int anchor)
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var first = new SceneContainerDefinition { Id = "parent-a", Placement = ScenePlacement.Create(new(.2,.6),rotationDegrees:30,scale:.8), ReferenceRect = new(.7,.6) };
        var second = new SceneContainerDefinition { Id = "parent-b", Placement = ScenePlacement.Create(new(.8,.3),rotationDegrees:-40,scale:1.2), ReferenceRect = new(.4,.9) };
        var child = new SceneContainerDefinition { Id = "child", ParentId = first.Id, ReferenceRect = new(.3,.7), Placement = ScenePlacement.Create(new(.7,.2),(ScenePlacementAnchor)anchor,17,.7) };
        var grand = new SceneContainerDefinition { Id = "grand", ParentId = child.Id, ReferenceRect = new(.5,.8), Placement = ScenePlacement.Create(new(.1,.9),rotationDegrees:11,scale:1.1) };
        project = project with { LyricPresentation = tree with { Nodes = tree.Nodes.AddRange(new[] { first,second,child,grand }) } };
        var before = SceneTreeGeometryResolver.Resolve(project.LyricPresentation.Nodes,640,360);
        Assert.True(SceneTreeEditor.TryReparent(project,child.Id,second.Id,out var moved,out var report),report.ToSummary());
        var after = SceneTreeGeometryResolver.Resolve(moved.LyricPresentation.Nodes,640,360);
        foreach (string id in new[] { child.Id,grand.Id })
        {
            Assert.Equal(before[id].Width,after[id].Width,8); Assert.Equal(before[id].Height,after[id].Height,8);
            foreach (var point in new[] { new CanvasPoint(0,0),new(100,50),new(30,90) })
            { var a = before[id].WorldMatrix.Map(point); var b = after[id].WorldMatrix.Map(point); Assert.Equal(a.X,b.X,8); Assert.Equal(a.Y,b.Y,8); }
        }
        Assert.False(SceneTreeEditor.TryReparent(moved,second.Id,grand.Id,out var cycle,out _)); Assert.Same(moved,cycle);
        Assert.True(SceneTreeEditor.TryReparent(moved,child.Id,second.Id,out var same,out _)); Assert.Same(moved,same);
    }
    [Theory]
    [InlineData("regularContainerId")]
    [InlineData("exclusiveClips")]
    [InlineData("placement")]
    public void 新工程格式拒绝旧展示键而当前局部微调往返(string key)
    {
        var project = ExclusiveDisplayFixture.Create();
        Assert.True(SceneTreeEditor.TrySetLocalTransform(project,"primary","line-3",new() { OffsetXRatio = .07,OffsetYRatio = -.08,RotationDegrees = 14,Scale = .8 },out project,out _));
        using TemporaryDirectory directory = new(); ProjectStore.Save(project,directory.Path);
        Assert.Equal(project,ProjectStore.Load(directory.Path).Project);
        string path = Path.Combine(directory.Path,ProjectJson.ProjectFileName); var json = JsonNode.Parse(File.ReadAllText(path))!;
        var presentation = json["lyricPresentation"]!;
        var target = key == "placement" ? presentation["lineOverrides"]![0]! : presentation["programs"]![0]!;
        target[key] = new JsonObject(); File.WriteAllText(path,json.ToJsonString());
        Assert.True(ProjectStore.Load(directory.Path).Report.HasErrors);
    }
}
