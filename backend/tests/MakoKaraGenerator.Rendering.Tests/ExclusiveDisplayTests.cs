using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ExclusiveDisplayTests
{
    [Theory]
    [InlineData(3_999_999, false)]
    [InlineData(4_000_000, true)]
    [InlineData(5_999_999, true)]
    [InlineData(6_000_000, false)]
    public void 独占严格使用源半开区间并保留关联翻译(long timeUs, bool exclusive)
    {
        var snapshot = ProjectSnapshot.FromProject(ExclusiveDisplayFixture.Create());
        using SkiaRenderPipeline pipeline = new();
        var frame = pipeline.GetSceneFrame(snapshot, timeUs);
        var primary = frame.Lines.Where(line => line.Branch.Id == "primary").ToArray();
        if (exclusive)
        {
            var line = Assert.Single(primary);
            Assert.Equal("line-2", line.Frame.Line!.Id); Assert.Null(line.SlotId);
            Assert.Equal(ExclusiveDisplayFixture.CenterId, line.ParentId);
            Assert.Equal(ExclusiveDisplayFixture.ClipId, line.DisplayClipId);
            var translation = Assert.Single(frame.Lines.Where(line => line.Branch.Id == "translation"));
            Assert.Equal("line-2", translation.Frame.Line!.PrimaryLineId);
            Assert.Equal(LyricSceneIds.Regular("translation"), translation.ParentId);
            Assert.Equal(new CanvasPoint(320, 360 * .86), translation.Geometry!.Pivot);
        }
        else
        {
            Assert.NotEmpty(primary);
            Assert.DoesNotContain(primary, line => line.Frame.Line!.Id == "line-2");
            Assert.DoesNotContain(frame.Elements, element => element.SourceBinding.RubyId == "center-ruby");
        }
        Assert.Equal(exclusive, frame.Elements.Single(element => element.Id == ExclusiveDisplayFixture.CenterId).IsVisible);
        Assert.Equal(!exclusive, frame.Elements.Single(element => element.Id == LyricSceneIds.Regular("primary")).IsVisible);
    }

    [Fact]
    public void 独占隔离前后预显示恢复槽位一且原始索引和时间保持不变()
    {
        var project = ExclusiveDisplayFixture.Create(); var snapshot = ProjectSnapshot.FromProject(project);
        var before = LyricSceneSampler.Sample(snapshot, 3_500_000).Where(line => line.Branch.Id == "primary").ToArray();
        Assert.All(before, line => Assert.True(line.Frame.LineIndex < 2));
        var restored = LyricSceneSampler.Sample(snapshot, 6_000_000).Single(line => line.Branch.Id == "primary");
        Assert.Equal("line-3", restored.Frame.Line!.Id); Assert.Equal(3, restored.Frame.LineIndex);
        Assert.Equal(LyricSceneIds.Slot("primary", "upper-left"), restored.ParentId);
        Assert.DoesNotContain(LyricSceneSampler.Sample(snapshot, 6_000_000), line => line.Frame.Line!.Id == "line-1");
        var next = LyricSceneSampler.Sample(snapshot, 7_500_000).Single(line => line.Frame.Line!.Id == "line-4");
        Assert.Equal(LyricSceneIds.Slot("primary", "lower-right"), next.ParentId);
        Assert.Same(project.Lyrics.Lines[3], restored.Frame.Line);
        var regular = snapshot.LyricPresentation.Resolve("primary");
        snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.SetBranch(regular with { SlotSequenceProfile = null }) };
        Assert.Equal(3, LyricSceneSampler.Sample(snapshot, 6_000_000).Single(line => line.Branch.Id == "primary").Frame.LineIndex);
    }

    [Fact]
    public void 居中保留自身进入动画源起点且共同父组变换不影响翻译()
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var root = tree.Nodes[0]; var placement = ScenePlacement.Create(new(.58, .45), rotationDegrees: 90, scale: .8);
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree with { Nodes = tree.Nodes.SetItem(0, root with { Placement = placement }) } });
        using SkiaRenderPipeline pipeline = new();
        var start = pipeline.GetSceneFrame(snapshot, 4_000_000);
        var halfway = pipeline.GetSceneFrame(snapshot, 4_200_000);
        var center = start.Lines.Single(line => line.Branch.Id == "primary");
        Assert.Equal(0, center.Transform.Opacity);
        Assert.InRange(halfway.Lines.Single(line => line.Branch.Id == "primary").Transform.Opacity, .1, .99);
        var rest = pipeline.GetSceneFrame(snapshot, 4_600_000);
        var rested = rest.Lines.Single(line => line.Branch.Id == "primary");
        Assert.Equal(1, rested.Transform.Opacity);
        Assert.Equal(640 * .58, rested.Geometry!.Pivot.X, 6); Assert.Equal(360 * .45, rested.Geometry.Pivot.Y, 6);
        Assert.Equal(new CanvasPoint(320, 360 * .86), rest.Lines.Single(line => line.Branch.Id == "translation").Geometry!.Pivot);
        var ruby = rest.Elements.Single(element => element.SourceBinding.RubyId == "center-ruby");
        Assert.Equal(rested.ElementId, ruby.ParentId); Assert.Equal(rested.Geometry.WorldMatrix, ruby.WorldMatrix);
        var startCorner = center.Geometry!.Corners()[0]; var restCorner = rested.Geometry.Corners()[0];
        Assert.Equal(-8, startCorner.X - restCorner.X, 6); Assert.Equal(0, startCorner.Y - restCorner.Y, 6);
    }

    [Fact]
    public void 任意纯组层级及参考尺寸按父子矩阵解析且与节点存储次序无关()
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var center = tree.FindNode(ExclusiveDisplayFixture.CenterId)!;
        var bridge = new SceneContainerDefinition { Id = "bridge", ParentId = center.ParentId,
            ReferenceRect = new(.5, .5), Placement = ScenePlacement.Create(new(.4, .4), rotationDegrees: 90, scale: .5) };
        tree = tree with { Nodes = [.. tree.Nodes.Select(node => node.Id == center.Id ? node with { ParentId = bridge.Id } : node).Reverse(), bridge] };
        project = project with { LyricPresentation = tree };
        Assert.False(PresentationValidator.Validate(project).HasErrors);
        using SkiaRenderPipeline pipeline = new(); var frame = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 5_000_000);
        var line = frame.Lines.Single(sample => sample.Branch.Id == "primary");
        Assert.Equal(new CanvasPoint(256, 144), line.Geometry!.Pivot);
        Assert.Equal(320, line.ParentReferenceWidth); Assert.Equal(180, line.ParentReferenceHeight);
        Assert.True(frame.Elements.Single(element => element.Id == "bridge").IsVisible);
        using var pixels = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 5_000_000) with { TransparentBackground = true });
        AssertPaintedInside(pixels, line.Geometry);
    }

    [Fact]
    public void 居中真实像素使用专属样式且重复跳转保存重开静态试样一致()
    {
        var project = ExclusiveDisplayFixture.Create(); var snapshot = ProjectSnapshot.FromProject(project);
        using SkiaRenderPipeline pipeline = new();
        using var first = pipeline.Render(RenderRequest.At(snapshot, 5_000_000) with { TransparentBackground = true });
        var geometry = pipeline.GetSceneFrame(snapshot, 5_000_000).Lines.Single(line => line.Branch.Id == "primary").Geometry!;
        AssertPaintedInside(first, geometry);
        int warm = 0;
        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
        {
            int offset = y * first.Stride + x * 4;
            if (first.PixelMemory.Span[offset + 3] > 180 && first.PixelMemory.Span[offset + 2] > first.PixelMemory.Span[offset] + 40) warm++;
        }
        Assert.True(warm > 100);
        _ = pipeline.GetSceneFrame(snapshot, 8_500_000);
        using var repeated = pipeline.Render(RenderRequest.At(snapshot, 5_000_000) with { TransparentBackground = true });
        Assert.True(first.PixelMemory.Span.SequenceEqual(repeated.PixelMemory.Span));
        using TemporaryDirectory directory = new(); Core.Persistence.ProjectStore.Save(project, directory.Path);
        var reloaded = Core.Persistence.ProjectStore.Load(directory.Path);
        Assert.False(reloaded.Report.HasErrors, reloaded.Report.ToSummary());
        using var disk = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(reloaded.Project!), 5_000_000) with { TransparentBackground = true });
        Assert.True(first.PixelMemory.Span.SequenceEqual(disk.PixelMemory.Span));
        var specimen = pipeline.GetSceneFrame(snapshot, 0, "line-2");
        Assert.Equal(ExclusiveDisplayFixture.CenterId, specimen.Lines.Single(line => line.Branch.Id == "primary").ParentId);
        Assert.DoesNotContain(specimen.Lines, line => line.Branch.Id == "primary" && line.SlotId is not null);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void 原生槽位序列按实际节点数量轮换而非固定双槽(int count)
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var regular = tree.Nodes[1]; var layout = regular.Layout!;
        string thirdId = "arbitrary-slot-three";
        tree = tree with { Programs = tree.Programs.SetItem(0, tree.Programs[0] with { Clips = [] }),
            Nodes = tree.Nodes.Add(new() { Id = thirdId, ParentId = regular.Id, Placement = ScenePlacement.Create(new(.5, .5)) }) };
        tree = tree with { Nodes = tree.Nodes.SetItem(1, regular with { Layout = layout with
            { SlotIds = count == 1 ? [layout.SlotIds[0]] : layout.SlotIds.Add(thirdId) } }) };
        Assert.False(PresentationValidator.Validate(project with { LyricPresentation = tree }).HasErrors);
        var frame = LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(project with { LyricPresentation = tree }), 4_500_000);
        var source = frame.Lines.Single(line => line.Frame.Line!.Id == "line-2");
        Assert.Equal(count == 1 ? layout.SlotIds[0] : thirdId, source.ParentId);
        Assert.Contains(Uri.EscapeDataString(source.ParentId), source.BindingId, StringComparison.Ordinal);
    }

    [Fact]
    public void 独立容器使用自身内部默认位置和宽度而不借用常规布局()
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        int index = Array.FindIndex(tree.Nodes.ToArray(), node => node.Id == ExclusiveDisplayFixture.CenterId);
        tree = tree with { Nodes = tree.Nodes.SetItem(index, tree.Nodes[index] with { Layout = new()
            { DefaultPlacement = ScenePlacement.Create(new(.7, .4)), MaxWidthRatio = .05 } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline pipeline = new();
        var line = pipeline.GetSceneFrame(snapshot, 5_000_000).Lines.Single(sample => sample.Branch.Id == "primary");
        Assert.Equal(new CanvasPoint(448, 144), line.Geometry!.Pivot);
        Assert.True(line.Geometry.Corners().Max(point => point.X) - line.Geometry.Corners().Min(point => point.X) <= 32.001);
        Assert.Equal(line.Geometry.Pivot, pipeline.GetSceneFrame(snapshot, 0, "line-2").Lines[0].Geometry!.Pivot);
        Assert.Equal(.9, tree.Resolve("primary").MaxWidthRatio);
    }

    [Fact]
    public void 多个独占片段不要求存储顺序且每次恢复都从槽位一开始()
    {
        var project = ExclusiveDisplayFixture.Create(); var tree = project.LyricPresentation;
        var program = tree.Programs[0];
        tree = tree with { Programs = tree.Programs.SetItem(0, program with { Clips = program.Clips.Add(new()
            { Id = "intro-clip", LineIds = ["line-0"], ContainerId = ExclusiveDisplayFixture.CenterId }) }) };
        Assert.False(PresentationValidator.Validate(project with { LyricPresentation = tree }).HasErrors);
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        foreach (long time in new[] { 2_000_000L, 6_000_000L })
            Assert.Equal(LyricSceneIds.Slot("primary", "upper-left"), LyricSceneSampler.Sample(snapshot, time).Single(line => line.Branch.Id == "primary").ParentId);
        var reversed = snapshot with { LyricPresentation = tree with { Programs = tree.Programs.SetItem(0, tree.Programs[0] with
            { Clips = [.. tree.Programs[0].Clips.Reverse()] }) } };
        foreach (long time in new[] { 0L, 2_000_000L, 5_000_000L, 6_000_000L })
            Assert.Equal(LyricSceneSampler.Sample(snapshot, time).Select(line => (line.ElementId, line.ParentId, line.BindingId)),
                LyricSceneSampler.Sample(reversed, time).Select(line => (line.ElementId, line.ParentId, line.BindingId)));
    }

    private static void AssertPaintedInside(RenderFrame frame, LineRenderGeometry geometry)
    {
        int count = 0;
        for (int y = 0; y < frame.Height; y++) for (int x = 0; x < frame.Width; x++)
            if (frame.PixelMemory.Span[y * frame.Stride + x * 4 + 3] > 150 && geometry.Contains(new(x + .5, y + .5))) count++;
        Assert.True(count > 20, "容器绑定没有实际绘制到对应世界几何内");
    }
}
