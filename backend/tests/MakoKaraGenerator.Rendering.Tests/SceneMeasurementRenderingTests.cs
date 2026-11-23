using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SceneMeasurementRenderingTests
{
    [Fact]
    public void 部分token准备帧测量保留整行未分词正文墨迹且像素相同()
    {
        var line = LyricLine.Create("sample", "Template sample", new(0, 2_500_000), TimingStatus.Imported,
            [LyricToken.Create("sample-token", "Template", new(100_000, 1_200_000), TimingStatus.Imported)]);
        var snapshot = ProjectSnapshot.FromProject(Project.Create("template-sample",
            Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000), LyricsDocument.Create([line])));
        using SkiaRenderPipeline pipeline = new();
        var scene = pipeline.GetSceneFrame(snapshot, 500_000);
        var fullLine = Assert.Single(scene.LineGeometries);
        var tokenUnits = scene.TokenGeometries.SelectMany(token => token.BaseGeometry.DrawUnits).ToHashSet();
        Assert.Contains(fullLine.BaseGeometry.DrawUnits, unit => !tokenUnits.Contains(unit));
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, MakoKaraGenerator.Scripting.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, []);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared };
        using var measured = pipeline.MeasureAndRender(request, false);
        using var rendered = pipeline.Render(request);
        Assert.Equal(rendered.PixelMemory.ToArray(), measured.Frame.PixelMemory.ToArray());
        var element = Assert.Single(measured.Scene.Elements, element => element.SourceBinding.LineId == line.Id);
        Assert.Equal(fullLine.BaseGeometry.WorldInkCorners.Select(point => new CanvasPoint(point.X, point.Y)), element.WorldCorners);
    }

    [Fact]
    public void 同帧时间测量使用原绘制场景且普通透明几何一致()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true));
        using SkiaRenderPipeline pipeline = new();
        using var measured = pipeline.MeasureAndRender(RenderRequest.At(snapshot, 10_000_000) with { TransparentBackground = true }, false);
        using var ordinary = pipeline.Render(RenderRequest.At(snapshot, 10_000_000) with { TransparentBackground = true });
        Assert.True(measured.Frame.PixelMemory.Span.SequenceEqual(ordinary.PixelMemory.Span));
        using var opaque = pipeline.MeasureAndRender(RenderRequest.At(snapshot, 10_000_000), false);
        Assert.Equal(measured.Scene.Lines.Select(line => line.Geometry!.WorldMatrix), opaque.Scene.Lines.Select(line => line.Geometry!.WorldMatrix));
        Assert.Equal(measured.Scene.Elements.SelectMany(element => element.WorldCorners), opaque.Scene.Elements.SelectMany(element => element.WorldCorners));
        Assert.NotEmpty(measured.Scene.TextMarks);
    }

    [Fact]
    public void 布局试样填充全部容器与槽位标记且跳转不改变像素或源工程()
    {
        var project = LeadInFixture.Create(true);
        var tree = project.LyricPresentation;
        string root = tree.FindProgram("primary")!.RootContainerId;
        tree = tree with { Nodes = tree.Nodes.Add(new() { Id = "unused-auto", ParentId = root, Layout = new(),
            Placement = ScenePlacement.Create(new(.25,.25)), ReferenceRect = new(.5,.5) }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline pipeline = new();
        using var first = pipeline.MeasureAndRender(RenderRequest.At(snapshot, 0) with { TransparentBackground = true }, true);
        using var last = pipeline.MeasureAndRender(RenderRequest.At(snapshot, 999_000_000) with { TransparentBackground = true }, true);
        Assert.True(first.Frame.PixelMemory.Span.SequenceEqual(last.Frame.PixelMemory.Span));
        foreach (var node in tree.Nodes.Where(node => node.Layout is not null))
            foreach (string parent in node.Layout!.Kind == LyricContainerLayoutKind.SlotSequence ? node.Layout.SlotIds : [node.Id])
                Assert.Contains(first.Scene.Lines, line => line.ParentId == parent && line.Geometry is not null);
        Assert.All(first.Scene.Lines, line => Assert.Equal(LayoutReferenceScene.Text, line.Frame.Line!.Text));
        Assert.Equal(tree.Nodes.Count(node => node.Content is TextMarkContent), first.Scene.TextMarks.Length);
        Assert.Same(snapshot.Lyrics, project.Lyrics);
        int count = 0;
        for (int y = 0; y < first.Frame.Height; y++) for (int x = 0; x < first.Frame.Width; x++)
            if (first.Frame.PixelMemory.Span[y * first.Frame.Stride + x * 4 + 3] > 100)
            {
                count++;
                Assert.True(first.Scene.Lines.Any(line => line.Geometry!.Contains(new(x + .5,y + .5),3))
                    || first.Scene.TextMarks.Any(mark => mark.Geometry.Contains(new(x + .5,y + .5),3)));
            }
        Assert.True(count > 100);
    }

    [Fact]
    public void 真实参考切换附着模式保持固定矩形世界外观不使用文字ink尺寸()
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation;
        var program = tree.FindProgram("primary")!; var cue = program.LeadIns[0];
        var snapshot = ProjectSnapshot.FromProject(project);
        using SkiaRenderPipeline pipeline = new();
        using var measured = pipeline.MeasureAndRender(RenderRequest.At(snapshot, 0), false, cue.TargetLineId, cue.Id);
        var node = tree.FindNode(cue.IndicatorContainerId)!;
        var previous = measured.Nodes[node.Id];
        var fixedNodes = SceneTreeGeometryResolver.Resolve(tree.Nodes,640,360);
        var reference = fixedNodes[node.ParentId!];
        Assert.True(LeadInEditor.TrySetAttachment(project,node.Id,SceneAttachmentMode.FixedParent,previous,reference,out var changed,out _));
        using var updated = pipeline.MeasureAndRender(RenderRequest.At(ProjectSnapshot.FromProject(changed),0),false,cue.TargetLineId,cue.Id);
        var actual = updated.Nodes[node.Id];
        Assert.Equal(previous.Width,actual.Width,8); Assert.Equal(previous.Height,actual.Height,8);
        Assert.Equal(previous.WorldMatrix.A,actual.WorldMatrix.A,8); Assert.Equal(previous.WorldMatrix.B,actual.WorldMatrix.B,8);
        Assert.Equal(previous.WorldMatrix.Tx,actual.WorldMatrix.Tx,8); Assert.Equal(previous.WorldMatrix.Ty,actual.WorldMatrix.Ty,8);
    }
}
