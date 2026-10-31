using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SceneTargetGeometryTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 标记与组保留真实装载参考框运行父树和独立动画透明度(bool fixedParent)
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        tree = tree with { Nodes = tree.Nodes.Replace(group, group with { Attachment = new()
            { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText },
            Content = sequence with { Appearance = sequence.Appearance with { Opacity = .5f } } }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", EnterAnimation = new()
                { DurationUs = 2_000_000, StartOpacity = 0, Easing = AnimationEasing.Linear } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        var content = ContentProjection.Create(snapshot); var catalog = SceneProjection.Create(snapshot, content);
        var line = FrameProjection.Lines(scene, catalog).Single(item => item.LineId.Value == "first");
        var marks = FrameProjection.Marks(scene, catalog); var groups = FrameProjection.Groups(scene);
        var runtime = groups.Single(item => item.Target.NodeId.Value == group.Id);
        Assert.Equal(fixedParent ? group.ParentId : line.NodeId.Value, runtime.ParentId!.Value.Value);
        Assert.Equal(group.ParentId, catalog.GetNode(runtime.Target.NodeId).ParentId!.Value.Value);
        Assert.True(runtime.IsVisible); Assert.Equal(1, runtime.AnimationOpacity);
        Assert.All(marks, mark =>
        {
            Assert.Equal(line, mark.TargetLine); Assert.Equal("cue-first", mark.Target.CueId.Value);
            Assert.Equal(line.BindingId, mark.Target.BindingId); Assert.Equal(group.Id, mark.ParentId.Value);
            Assert.Equal(fixedParent ? 1 : .5, mark.AnimationOpacity, 7);
            Assert.Equal(.5, StaticStyleProjection.CreateBaseline(snapshot).Get(mark.Target).Opacity);
            Assert.Equal(scene.MarkGeometries.Single(item => item.Target == mark.Target).Geometry.BaseWorldMatrix,
                Matrix(scene.TextMarks.Single(item => item.NodeId == mark.Target.NodeId.Value).Geometry.WorldMatrix!.Value));
        });
        var geometry = scene.GroupGeometries.Single(item => item.Target == runtime.Target);
        var source = scene.Elements.Single(item => item.Id == group.Id);
        Assert.Equal(Matrix(source.WorldMatrix), geometry.BaseWorldMatrix);
        Assert.Equal(new Fx.Point2(source.Pivot.X, source.Pivot.Y), geometry.WorldPivot);
        Assert.Equal(120, geometry.ReferenceBounds.Width, 8); Assert.Equal(40, geometry.ReferenceBounds.Height, 8);
        Assert.Equal(scene.MarkGeometries.SelectMany(item => item.Geometry.WorldInkCorners), geometry.WorldInkCorners);
        Assert.Equal(scene.Definition.Elements.Count(item => item.Kind == SceneElementKind.Group), scene.GroupGeometries.Length);
    }
    [Fact]
    public void 空白标记有Advance无墨迹而组不以参考矩形伪造墨迹()
    {
        var project = LeadInFixture.Create(); var tree = project.LyricPresentation;
        var group = tree.FindNode(tree.FindProgram("primary")!.LeadIns[0].IndicatorContainerId)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        tree = tree with { Nodes = [.. tree.Nodes.Select(node => sequence.MarkNodeIds.Contains(node.Id)
            ? node with { Content = ((TextMarkContent)node.Content!) with { Text = " " } } : node)] };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        Assert.All(scene.MarkGeometries, mark =>
        { Assert.True(mark.Geometry.AdvanceBounds.Width > 0); Assert.Null(mark.Geometry.InkBounds); Assert.Empty(mark.Geometry.WorldInkCorners); });
        var geometry = scene.GroupGeometries.Single(item => item.Target.NodeId.Value == group.Id);
        Assert.True(geometry.ReferenceBounds.Width > 0); Assert.Empty(geometry.WorldInkCorners);
        Assert.True(scene.Elements.Single(item => item.Id == group.Id).IsVisible);
        var hidden = renderer.GetSceneFrame(snapshot, 40_000_000);
        Assert.Empty(hidden.GroupGeometries.Single(item => item.Target.NodeId.Value == group.Id).WorldInkCorners);
        Assert.False(hidden.Elements.Single(item => item.Id == group.Id).IsVisible);
    }
    [Fact]
    public void 父组墨迹包含整行固定字形注音和附着标记而不依赖Token切分()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true));
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        var first = scene.Lines.Single(item => item.Frame.Line!.Id == "first");
        var program = snapshot.LyricPresentation.FindProgram("primary")!;
        var root = scene.GroupGeometries.Single(item => item.Target.NodeId.Value == program.RootContainerId);
        var line = scene.LineGeometries.Single(item => item.Target.BindingId.Value == first.BindingId);
        var expected = line.BaseGeometry.WorldInkCorners.Concat(scene.TokenGeometries.Where(item => item.WithRuby.BindingId.Value == first.BindingId)
            .SelectMany(item => item.Ruby).SelectMany(item => item.Geometry.WorldInkCorners)).Concat(scene.MarkGeometries.SelectMany(item => item.Geometry.WorldInkCorners));
        Assert.Equal(expected, root.WorldInkCorners);
        Assert.True(root.WorldInkCorners.Length > line.BaseGeometry.WorldInkCorners.Length);
        Assert.All(scene.GroupGeometries.Where(item => !scene.Elements.Single(element => element.Id == item.Target.NodeId.Value).IsVisible),
            group => Assert.Empty(group.WorldInkCorners));
    }
    private static Fx.Affine2 Matrix(SceneMatrix value) => new(value.A, value.B, value.C, value.D, value.Tx, value.Ty);
}
