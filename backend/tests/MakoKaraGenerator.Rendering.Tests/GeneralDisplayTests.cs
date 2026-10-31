using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GeneralDisplayTests
{
    [Theory]
    [InlineData(3_999_999,"generic-dual")]
    [InlineData(4_000_000,"generic-center")]
    [InlineData(5_999_999,"generic-center")]
    [InlineData(6_000_000,"generic-triple")]
    [InlineData(11_000_000,"generic-triple")]
    public void 通用片段边界只显示实际目标且三槽全参与调度(long time,string container)
    {
        var snapshot = ProjectSnapshot.FromProject(GeneralDisplayFixture.Create()); using SkiaRenderPipeline pipeline = new();
        var frame = pipeline.GetSceneFrame(snapshot,time);
        Assert.NotEmpty(frame.Lines.Where(l => l.Branch.Id == "primary"));
        Assert.All(frame.Lines.Where(l => l.Branch.Id == "primary"),l => Assert.Equal(container,l.ContainerId));
        if (time == 11_000_000)
        {
            Assert.Equal(3,frame.Lines.Count(l => l.Branch.Id == "primary"));
            Assert.Equal(3,frame.Lines.Where(l => l.Branch.Id == "primary").Select(l => l.ParentId).Distinct().Count());
        }
        Assert.Contains(frame.Lines,l => l.Branch.Id == "translation");
    }
    [Fact]
    public void 源区间更新不改变片段绑定身份而重排改变槽位装载身份()
    {
        var source = GeneralDisplayFixture.Create(); var before = LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(source),11_000_000);
        Assert.True(LyricTimingEditor.TrySetLineTime(source,"line-5",10_000_000,12_400_000,out var timed,out _));
        var after = LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(timed),11_000_000);
        Assert.Equal(before.Lines.Select(l => l.BindingId),after.Lines.Select(l => l.BindingId));
        var ids = source.LyricPresentation.FindNode(GeneralDisplayFixture.TripleId)!.Layout!.SlotIds.Reverse().ToArray();
        Assert.True(SceneTreeEditor.TryReorderSlots(source,GeneralDisplayFixture.TripleId,ids,out var reordered,out _));
        var frame = LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(reordered),11_000_000);
        Assert.Equal(ids[0],frame.Lines.First(l => l.Frame.Line!.Id == "line-3").ParentId);
        Assert.NotEqual(before.Lines.First(l => l.Frame.Line!.Id == "line-3").BindingId,frame.Lines.First(l => l.Frame.Line!.Id == "line-3").BindingId);
    }
    [Fact]
    public void 空槽位及未绑定纯组有固定几何并且与文字数量无关()
    {
        var source = GeneralDisplayFixture.Create(); string root = source.LyricPresentation.Programs[0].RootContainerId;
        Assert.True(SceneTreeEditor.TryAddNodes(source,new[] { new SceneContainerDefinition { Id = "empty",ParentId = root,ReferenceRect = new(.2,.3),Placement = ScenePlacement.Create(new(.8,.2),rotationDegrees:25) } },out var result,out _));
        using SkiaRenderPipeline pipeline = new(); var snapshot = ProjectSnapshot.FromProject(result);
        var geometry = pipeline.GetNodeGeometry(snapshot,"empty")!; Assert.Equal(new CanvasPoint(512,72),geometry.Pivot);
        Assert.Equal(4,geometry.Corners().Count); Assert.True(geometry.Contains(geometry.Pivot));
        var third = pipeline.GetNodeGeometry(snapshot,"generic-slot-4","line-0"); Assert.NotNull(third);
        var selected = pipeline.GetLocalTransformGeometry(snapshot,"line-0")!; Assert.Equal(new(0,0),selected.Placement.Position);
    }
}
