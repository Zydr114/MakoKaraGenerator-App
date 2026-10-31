using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class UnifiedSceneTests
{
    [Fact]
    public void 换歌词和切模式不改变容器槽位身份及固定枢轴()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var first = pipeline.GetSceneFrame(snapshot, 1_500_000);
        var later = pipeline.GetSceneFrame(snapshot, 4_500_000);
        string containerId = SceneDefinitionBuilder.ContainerId("primary");
        var groupA = first.Elements.Single(element => element.Id == containerId);
        var groupB = later.Elements.Single(element => element.Id == containerId);
        Assert.Equal(groupA.Pivot, groupB.Pivot);
        Assert.Equal(groupA.WorldMatrix, groupB.WorldMatrix);
        var slotsA = first.Definition.Elements.Where(element => element.Kind == SceneElementKind.Group).Select(element => element.Id);
        var slotsB = later.Definition.Elements.Where(element => element.Kind == SceneElementKind.Group).Select(element => element.Id);
        Assert.Equal(slotsA, slotsB);
        Assert.NotEqual(first.Lines[0].BindingId, later.Lines[0].BindingId);
        Assert.Equal(first.Lines[0].ContainerId, later.Lines[0].ContainerId);
        var ordinary = snapshot with { LyricPresentation = snapshot.LyricPresentation.SetBranch(
            snapshot.LyricPresentation.Resolve("primary") with { SlotSequenceProfile = null }) };
        var normalGroup = pipeline.GetSceneFrame(ordinary, 4_500_000).Elements.Single(element => element.Id == containerId);
        Assert.Equal(groupA.WorldMatrix, normalGroup.WorldMatrix);
        Assert.Equal(groupA.Pivot, normalGroup.Pivot);
        Assert.Equal(SceneElementKind.Group, normalGroup.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 动画在内部坐标解释并随外层旋转缩放(bool dual)
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var definition = snapshot.LyricPresentation.Resolve("primary") with
        { Placement = ScenePlacement.Create(new(.5, .5), rotationDegrees: 90, scale: .8), SlotSequenceProfile = dual ? SlotSequenceAnimationProfile.Default : null };
        definition = definition with { Lines = [new() { LineId = "a", EnterAnimation = new()
            { DurationUs = 500_000, StartOpacity = 1, StartScale = 1, StartOffsetPx = 20 } }] };
        snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.SetBranch(definition) };
        var animated = pipeline.GetSceneFrame(snapshot, 0).Lines.Single(line => line.Frame.Line!.Id == "a");
        var atRest = pipeline.GetSceneFrame(snapshot, 0, "a").Lines.Single(line => line.Frame.Line!.Id == "a");
        var animatedCorner = animated.Geometry!.Corners()[0]; var stillCorner = atRest.Geometry!.Corners()[0];
        Assert.Equal(-16, animatedCorner.X - stillCorner.X, 6);
        Assert.Equal(0, animatedCorner.Y - stillCorner.Y, 6);
        Assert.Equal(atRest.Geometry.Pivot, animated.Geometry.Pivot);
        Assert.Equal(definition.Placement, snapshot.LyricPresentation.Resolve("primary").Placement);
    }

    [Fact]
    public void 注音进入统一树并保持源关联和父世界矩阵()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var frame = pipeline.GetSceneFrame(snapshot, 1_500_000);
        var ruby = frame.Elements.Single(element => element.SourceBinding.RubyId == "ruby");
        var text = frame.Elements.Single(element => element.Id == ruby.ParentId);
        Assert.Equal("a-token", ruby.SourceBinding.TokenId);
        Assert.Equal("a", ruby.SourceBinding.LineId);
        Assert.Equal(SceneElementKind.Text, ruby.Kind);
        Assert.Equal(text.WorldMatrix, ruby.WorldMatrix);
        Assert.Equal(4, ruby.WorldCorners.Length);
        var sourceSample = frame.Lines.Single(line => line.Frame.Line!.Id == "a");
        Assert.Equal(sourceSample.ParentId, text.ParentId);
        Assert.Equal(sourceSample.BindingId, text.BindingId);
    }

    [Fact]
    public void 帧几何包住实际像素且容器命中涵盖双行静态试样()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var frame = pipeline.GetSceneFrame(snapshot, 1_500_000);
        using var pixels = pipeline.Render(RenderRequest.At(snapshot, 1_500_000) with { TransparentBackground = true });
        int count = 0;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
            if (pixels.PixelMemory.Span[y * pixels.Stride + x * 4 + 3] > 100)
            {
                count++;
                Assert.Contains(frame.Lines, sample => sample.Geometry!.Contains(new(x + .5, y + .5), 2));
            }
        Assert.True(count > 100);
        var group = pipeline.GetNodeGeometry(snapshot, snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId,"a")!;
        var staticFrame = pipeline.GetSceneFrame(snapshot, 0, "a");
        Assert.Equal(2, staticFrame.Lines.Length);
        foreach (var line in staticFrame.Lines)
        {
            var corners = line.Geometry!.Corners();
            Assert.True(group.Contains(new(corners.Average(point => point.X), corners.Average(point => point.Y))));
        }
        Assert.Equal(new CanvasPoint(640 * .58, 360 * .45), group.Pivot);
        var internalGeometry = pipeline.GetNodeGeometry(snapshot,snapshot.LyricPresentation.FindNode(snapshot.LyricPresentation.FindProgram("primary")!.DefaultContainerId)!.Layout!.SlotIds[0],"a")!;
        CanvasPoint delta = new(30, -12);
        var mapped = internalGeometry.EditCoordinateTransform.Map(delta);
        var origin = internalGeometry.EditCoordinateTransform.Map(new(0, 0));
        var local = internalGeometry.EditCoordinateTransform.Unmap(new(origin.X + mapped.X - origin.X, origin.Y + mapped.Y - origin.Y));
        Assert.Equal(delta.X, local.X, 8); Assert.Equal(delta.Y, local.Y, 8);
    }

    [Fact]
    public void 重复跳转得到相同绑定世界几何和帧像素()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var first = pipeline.GetSceneFrame(snapshot, 1_500_000);
        using var a = pipeline.Render(RenderRequest.At(snapshot, 1_500_000));
        _ = pipeline.GetSceneFrame(snapshot, 6_000_000);
        var second = pipeline.GetSceneFrame(snapshot, 1_500_000);
        using var b = pipeline.Render(RenderRequest.At(snapshot, 1_500_000));
        Assert.Equal(first.Lines.Select(line => (line.ElementId, line.ParentId, line.BindingId, line.Geometry!.WorldMatrix)),
            second.Lines.Select(line => (line.ElementId, line.ParentId, line.BindingId, line.Geometry!.WorldMatrix)));
        Assert.True(a.PixelMemory.Span.SequenceEqual(b.PixelMemory.Span));
    }

    [Fact]
    public void 有限画布外位置由裁切处理且源值不变()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var definition = snapshot.LyricPresentation.Resolve("primary") with
        { Placement = ScenePlacement.Create(new(1.5, -.5)), SlotSequenceProfile = null };
        snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.SetBranch(definition) };
        using var frame = pipeline.Render(RenderRequest.At(snapshot, 1_500_000) with { TransparentBackground = true });
        Assert.False(frame.PixelMemory.Span.ContainsAnyExcept((byte)0));
        Assert.Equal(new(1.5, -.5), snapshot.LyricPresentation.Resolve("primary").Placement.Position);
    }

    [Fact]
    public void 未定时静态试样不占用运行槽位且编辑双行不生成重叠第三句()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Lines = snapshot.Lyrics.Lines.Add(LyricLine.Create("missing", "未定时")) } };
        Assert.DoesNotContain(pipeline.GetSceneFrame(snapshot, 1_500_000).Lines, line => line.Frame.Line!.Id == "missing");
        var sample = pipeline.GetSceneFrame(snapshot, 0, "missing");
        Assert.Equal(2, sample.Lines.Length);
        Assert.Equal(2, sample.Lines.Select(line => line.SlotId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(sample.Lines, line => line.Frame.Line!.Id == "missing");
        Assert.Null(snapshot.Lyrics.Lines[^1].Time);
    }

    private static ProjectSnapshot Example()
    {
        var lyrics = LyricsDocument.Create(new[] { Timed("a", 0, 2_000_000), Timed("b", 2_000_000, 4_000_000), Timed("c", 4_000_000, 6_000_000) });
        lyrics = lyrics with { Lines = lyrics.Lines.SetItem(0, lyrics.Lines[0] with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "a-token", "zi")] }) };
        return ProjectSnapshot.FromProject(Project.Create("树", composition: new() { CanvasWidth = 640, CanvasHeight = 360 },
            lyrics: lyrics, lyricStyle: LyricStyle.Default with { FontSizePx = 28, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White }) with
        { LyricPresentation = LyricPresentationDefinition.FromBranches([new() { BranchId = "primary", Placement = ScenePlacement.Create(new(.58, .45), rotationDegrees: 8, scale: .8),
            SlotSequenceProfile = SlotSequenceAnimationProfile.Default with { EnterDurationUs = 0, ExitDurationUs = 0 } }]) });
    }
    private static LyricLine Timed(string id, long start, long end) => LyricLine.Create(id, id == "c" ? "很长的歌词内容" : "字",
        new TimeRange(start, end), TimingStatus.Edited,
        [LyricToken.Create(id + "-token", id == "c" ? "很长的歌词内容" : "字", new TimeRange(start, end), TimingStatus.Edited)]);
}
