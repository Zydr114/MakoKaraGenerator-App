using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class LeadInRenderingTests
{
    [Fact]
    public void 三点实际逐步变色并在归零消失随机跳转像素一致()
    {
        var project = LeadInFixture.Create();
        using SkiaRenderPipeline pipeline = new();
        var snapshot = ProjectSnapshot.FromProject(project);
        var ready = pipeline.GetSceneFrame(snapshot, 9_500_000);
        Assert.Equal(3, ready.TextMarks.Length);
        Assert.All(ready.TextMarks, mark => Assert.False(mark.IsElapsed));
        using var first = pipeline.Render(RenderRequest.At(snapshot, 9_500_000) with { TransparentBackground = true });
        var next = pipeline.GetSceneFrame(snapshot, 10_000_000);
        using var second = pipeline.Render(RenderRequest.At(snapshot, 10_000_000) with { TransparentBackground = true });
        Assert.True(next.TextMarks[0].IsElapsed);
        Assert.All(next.TextMarks.Skip(1), mark => Assert.False(mark.IsElapsed));
        Assert.Equal(ready.TextMarks.Select(mark => mark.Geometry.WorldMatrix), next.TextMarks.Select(mark => mark.Geometry.WorldMatrix));
        Assert.True(CountIn(first, ready.TextMarks[0].Geometry, 255) > 20);
        Assert.True(CountIn(second, next.TextMarks[0].Geometry, 95) > 20);
        Assert.True(CountIn(second, next.TextMarks[1].Geometry, 255) > 20);
        Assert.Empty(pipeline.GetSceneFrame(snapshot, 12_000_000).TextMarks);
        using var singing = pipeline.Render(RenderRequest.At(snapshot, 12_000_000) with { TransparentBackground = true });
        Assert.Equal(0, CountIn(singing, next.TextMarks[0].Geometry, 95));
        _ = pipeline.GetSceneFrame(snapshot, 39_500_000);
        using var repeat = pipeline.Render(RenderRequest.At(snapshot, 10_000_000) with { TransparentBackground = true });
        Assert.True(second.PixelMemory.Span.SequenceEqual(repeat.PixelMemory.Span));
        Assert.All(ready.Definition.Elements.Where(element => element.Content is TextMarkContent), element =>
        { Assert.Equal(SceneElementKind.Text, element.Kind); Assert.Null(element.SourceBinding.LineId); });
        Assert.Same(project.Lyrics, snapshot.Lyrics);
    }

    [Fact]
    public void 跟随使用文字注音局部联合锚点并只乘一次父组槽位微调和动画()
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation;
        var program = tree.FindProgram("primary")!; var root = tree.FindNode(program.RootContainerId)!;
        var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.58,.43), rotationDegrees: 21, scale: .8) })
            .Replace(group, group with { Placement = group.Placement with { Position = new(-.02,.015) } }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", LocalTransform = new() { OffsetXRatio = .035, OffsetYRatio = -.018, RotationDegrees = 11, Scale = .9 } }) };
        group = tree.FindNode(group.Id)!;
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline pipeline = new(); var frame = pipeline.GetSceneFrame(snapshot, 10_000_000);
        var target = frame.Lines.Single(line => line.Branch.Id == "primary"); var glyph = target.Geometry!;
        var owner = frame.Elements.Single(element => element.Id == group.Id);
        var staticGeometry = SceneTreeGeometryResolver.Resolve(tree.Nodes,640,360);
        var reference = staticGeometry[group.ParentId!];
        var anchor = SceneMatrix.Translate(glyph.EffectiveBaseTopLeft.X, (glyph.EffectiveBaseTopLeft.Y + glyph.EffectiveBaseBottomRight.Y) / 2);
        var expected = glyph.WorldMatrix!.Value * anchor * SceneMatrix.Translate(group.Placement.Position.X * reference.Width, group.Placement.Position.Y * reference.Height)
            * SceneMatrix.RotateScale(group.Placement.RotationDegrees,group.Placement.Scale)
            * SceneMatrix.Translate(-640 * group.ReferenceRect.WidthRatio, -360 * group.ReferenceRect.HeightRatio/2);
        AssertMatrix(expected, owner.WorldMatrix);
        Assert.Equal(target.ElementId, owner.ParentId);
        Assert.All(frame.TextMarks, mark => Assert.Equal(1, mark.Opacity));
        var hit = SceneEditorGeometry.ForNode(snapshot,frame,group.Id)!;
        foreach (var mark in frame.TextMarks)
            Assert.True(hit.Contains(mark.Geometry.Transform(new((mark.Geometry.TopLeft.X + mark.Geometry.BottomRight.X)/2,(mark.Geometry.TopLeft.Y + mark.Geometry.BottomRight.Y)/2))));
        var rootGeometry = SceneEditorGeometry.ForNode(snapshot,frame,root.Id)!;
        Assert.All(frame.TextMarks.SelectMany(mark => mark.Geometry.Corners()), point => Assert.True(rootGeometry.Contains(point, .001)));
        var stable = pipeline.GetNodeGeometry(snapshot,group.Id,"first")!;
        Assert.Equal(hit.WorldMatrix, stable.WorldMatrix);
        Assert.Equal(3,pipeline.GetSceneFrame(snapshot,0,"first").TextMarks.Length);
    }

    [Fact]
    public void 固定父组不跟随换槽但继承共同父组变换()
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation;
        var program = tree.FindProgram("primary")!; var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        var fixedGroup = group with { Attachment = new() { Mode = SceneAttachmentMode.FixedParent }, Placement = ScenePlacement.Create(new(.3,.3)) };
        tree = tree with { Nodes = tree.Nodes.Replace(group,fixedGroup) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline pipeline = new();
        var first = pipeline.GetSceneFrame(snapshot,9_500_000); var chorus = pipeline.GetSceneFrame(snapshot,37_500_000);
        Assert.Equal(first.TextMarks.Select(mark => mark.Geometry.WorldMatrix),chorus.TextMarks.Select(mark => mark.Geometry.WorldMatrix));
        Assert.All(first.TextMarks, mark => Assert.Equal(1,mark.Opacity));
        var root = tree.FindNode(program.RootContainerId)!;
        var moved = snapshot with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(root,root with { Placement = root.Placement with { RotationDegrees = 18,Scale = .7 } }) } };
        Assert.NotEqual(first.TextMarks[0].Geometry.WorldMatrix,pipeline.GetSceneFrame(moved,9_500_000).TextMarks[0].Geometry.WorldMatrix);
    }

    [Fact]
    public void 透明度外观基线只应用一次标记覆盖解析独立并且无前奏保持像素()
    {
        var project = LeadInFixture.Create(); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var node = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!; var sequence = (CountdownSequenceContent)node.Content!;
        var translucent = node with { Content = sequence with { Appearance = sequence.Appearance with { Opacity = .5f } } };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(node,translucent) } });
        using SkiaRenderPipeline pipeline = new(); var scene = pipeline.GetSceneFrame(snapshot,10_000_000);
        using var frame = pipeline.Render(RenderRequest.At(snapshot,10_000_000) with { TransparentBackground = true });
        int maxAlpha = MaxAlphaIn(frame,scene.TextMarks[0].Geometry);
        Assert.InRange(maxAlpha,126,128);
        var without = snapshot with { LyricPresentation = tree with { Programs = tree.Programs.Replace(program,program with { LeadIns = [] }) } };
        var disabled = without with { LyricPresentation = tree with { Programs = tree.Programs.Replace(program,program with { LeadIns = [..program.LeadIns.Select(cue => cue with { Enabled = false })] }) } };
        using var a = pipeline.Render(RenderRequest.At(without,40_500_000)); using var b = pipeline.Render(RenderRequest.At(disabled,40_500_000));
        Assert.True(a.PixelMemory.Span.SequenceEqual(b.PixelMemory.Span));
    }

    [Fact]
    public void 标记字号渐变描边阴影真实绘制且装饰透明度只应用一次()
    {
        var project=TestFonts.WithCjkFont(LeadInFixture.Create()); var tree=project.LyricPresentation;
        var group=tree.FindNode(tree.FindProgram("primary")!.LeadIns[0].IndicatorContainerId)!;
        var sequence=(CountdownSequenceContent)group.Content!;
        var removed=sequence.MarkNodeIds.Skip(1).ToHashSet(StringComparer.Ordinal);
        var decorated=sequence.Appearance with
        {
            Typography=sequence.Appearance.Typography with { FontFamilyName=TestFonts.Cjk, FontSizePx=44 },
            PendingFill=new() { Color=RgbaColor.White,Gradient=GradientSpec.Create([
                new GradientStop(0,new(255,0,0)),new GradientStop(1,new(0,0,255))]) },
            Stroke=new() { Color=new(255,0,0),WidthPx=4 },
            Shadow=new() { Color=new(0,255,0),AngleDegrees=90,DistancePx=45 },
        };
        group=group with { Attachment=new() { Mode=SceneAttachmentMode.FixedParent },Placement=ScenePlacement.Create(new(.3,.2)),
            Content=sequence with { MarkNodeIds=[sequence.MarkNodeIds[0]],Appearance=decorated } };
        tree=tree with { Nodes=[..tree.Nodes.Where(node => !removed.Contains(node.Id)).Select(node => node.Id==group.Id ? group : node)] };
        var snapshot=ProjectSnapshot.FromProject(project with { LyricPresentation=tree });
        using SkiaRenderPipeline pipeline=new();
        using var opaque=pipeline.Render(RenderRequest.At(snapshot,9_500_000) with { TransparentBackground=true });
        var translucent=group with { Content=((CountdownSequenceContent)group.Content!) with { Appearance=decorated with { Opacity=.5f } } };
        using var half=pipeline.Render(RenderRequest.At(snapshot with { LyricPresentation=tree with { Nodes=tree.Nodes.Replace(group,translucent) } },9_500_000) with { TransparentBackground=true });
        int greenCount=0,maxGreenAlpha=0,maxOpaqueGreenAlpha=0,redCount=0,blueCount=0;
        for (int i=0;i<opaque.PixelMemory.Length;i+=4)
        {
            var pixel=opaque.PixelMemory.Span.Slice(i,4);
            if (pixel[1]>100 && pixel[1]>pixel[0]*2 && pixel[1]>pixel[2]*2)
            { greenCount++; maxOpaqueGreenAlpha=Math.Max(maxOpaqueGreenAlpha,pixel[3]); maxGreenAlpha=Math.Max(maxGreenAlpha,half.PixelMemory.Span[i+3]); }
            if (pixel[2]>pixel[0]*2 && pixel[2]>pixel[1]*2) redCount++;
            if (pixel[0]>pixel[2]*2 && pixel[0]>pixel[1]*2) blueCount++;
        }
        Assert.True(greenCount>100); Assert.True(redCount>100); Assert.True(blueCount>20);
        Assert.True(maxOpaqueGreenAlpha>230);
        Assert.InRange(maxGreenAlpha,maxOpaqueGreenAlpha/2-2,maxOpaqueGreenAlpha/2+2);
        var plain=group with { Content=((CountdownSequenceContent)group.Content!) with { Appearance=sequence.Appearance } };
        var plainScene=pipeline.GetSceneFrame(snapshot with { LyricPresentation=tree with { Nodes=tree.Nodes.Replace(group,plain) } },9_500_000);
        var larger=pipeline.GetSceneFrame(snapshot,9_500_000).TextMarks[0].Geometry;
        var smaller=plainScene.TextMarks[0].Geometry;
        Assert.True(larger.BottomRight.X-larger.TopLeft.X>smaller.BottomRight.X-smaller.TopLeft.X);
    }

    private static void AssertMatrix(SceneMatrix a, SceneMatrix b)
    { Assert.Equal(a.A,b.A,8); Assert.Equal(a.B,b.B,8); Assert.Equal(a.C,b.C,8); Assert.Equal(a.D,b.D,8); Assert.Equal(a.Tx,b.Tx,8); Assert.Equal(a.Ty,b.Ty,8); }
    private static int CountIn(RenderFrame frame, LineRenderGeometry geometry, byte color)
    {
        int count = 0; var pixels = frame.PixelMemory.Span;
        for (int y=0;y<frame.Height;y++) for (int x=0;x<frame.Width;x++)
        {
            int i=y*frame.Stride+x*4;
            if (pixels[i+3]>230 && pixels[i]==color && pixels[i+1]==color && pixels[i+2]==color && geometry.Contains(new(x+.5,y+.5))) count++;
        }
        return count;
    }
    private static int MaxAlphaIn(RenderFrame frame,LineRenderGeometry geometry)
    {
        int max = 0; var pixels = frame.PixelMemory.Span;
        for (int y=0;y<frame.Height;y++) for (int x=0;x<frame.Width;x++)
            if (geometry.Contains(new(x+.5,y+.5))) max = Math.Max(max,pixels[y*frame.Stride+x*4+3]);
        return max;
    }
}
