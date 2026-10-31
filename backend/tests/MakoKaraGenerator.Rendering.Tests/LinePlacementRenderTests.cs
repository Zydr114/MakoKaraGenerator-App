using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class LinePlacementRenderTests
{
    [Fact]
    public void 移动位置改变真实像素且缩小预览与完整输出保持相对位置()
    {
        using SkiaRenderPipeline pipeline = new();
        Project first = CreateProject(ScenePlacement.Create(new(.3, .3)));
        Project second = CreateProject(ScenePlacement.Create(new(.6, .7)));
        using RenderFrame a = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(first), 500_000) with { TransparentBackground = true });
        using RenderFrame b = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(second), 500_000) with { TransparentBackground = true });
        var boxA = Bounds(a); var boxB = Bounds(b);
        Assert.InRange(boxB.Left - boxA.Left, 191, 193);
        Assert.InRange(boxB.Top - boxA.Top, 143, 145);
        using RenderFrame small = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(second), 500_000) with
            { TransparentBackground = true, Target = new RenderTarget { PixelWidth = 320, PixelHeight = 180 } });
        var smallBox = Bounds(small);
        Assert.InRange(Math.Abs(smallBox.Left * 2 - boxB.Left), 0, 2);
        Assert.InRange(Math.Abs(smallBox.Top * 2 - boxB.Top), 0, 2);
        Assert.Equal(first.Lyrics.Lines[0].Time, second.Lyrics.Lines[0].Time);
    }

    [Theory]
    [InlineData(ScenePlacementAnchor.Center)]
    [InlineData(ScenePlacementAnchor.TopLeft)]
    [InlineData(ScenePlacementAnchor.TopCenter)]
    [InlineData(ScenePlacementAnchor.BottomRight)]
    [InlineData(ScenePlacementAnchor.LeftCenter)]
    [InlineData(ScenePlacementAnchor.RightCenter)]
    public void 实际字形锚点几何包围渲染像素且旋转命中正确(ScenePlacementAnchor anchor)
    {
        using SkiaRenderPipeline pipeline = new();
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(CreateProject(ScenePlacement.Create(new(.5, .5), anchor, 32, 1.2)));
        var geometry = pipeline.GetLineGeometry(snapshot, "line")!;
        Assert.Equal(new CanvasPoint(320, 180), geometry.Pivot);
        Assert.True(geometry.Contains(geometry.Pivot));
        Assert.False(geometry.Contains(new(0, 0)));
        using RenderFrame frame = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = true });
        int painted = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.PixelMemory.Span[y * frame.Stride + x * 4 + 3] > 100)
                {
                    painted++;
                    Assert.True(geometry.Contains(new(x + .5, y + .5), 1.5), $"像素 {x},{y} 未在行几何中");
                }
        Assert.True(painted > 100);
    }

    [Fact]
    public void 旋转九十度和缩放确实改变真实像素几何()
    {
        using SkiaRenderPipeline pipeline = new();
        Project project = CreateProject(ScenePlacement.Default);
        using RenderFrame normal = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true });
        project = project.WithLinePresentation("line", ScenePlacement.Create(new(.5, .5), rotationDegrees: 90, scale: 1.5));
        using RenderFrame rotated = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true });
        var a = Bounds(normal); var b = Bounds(rotated);
        Assert.InRange((double)(b.Bottom - b.Top) / (a.Right - a.Left), 1.45, 1.55);
        Assert.InRange((double)(b.Right - b.Left) / (a.Bottom - a.Top), 1.4, 1.6);
    }

    [Theory]
    [InlineData(LyricHorizontalAlignment.Left)]
    [InlineData(LyricHorizontalAlignment.Center)]
    [InlineData(LyricHorizontalAlignment.Right)]
    public void 第一次拖动默认位置转换为独立几何位置没有跳跃(LyricHorizontalAlignment alignment)
    {
        using SkiaRenderPipeline pipeline = new();
        Project project = CreateProject(null) with { LyricStyle = LyricStyle.Default with
            { FontSizePx = 32, HorizontalAlignment = alignment, Scale = 1.25f, LetterSpacingPx = 3 } };
        var before = pipeline.GetLineGeometry(ProjectSnapshot.FromProject(project), "line")!;
        Assert.True(before.Placement.IsValid);
        project = project.WithLinePresentation("line", before.Placement);
        var after = pipeline.GetLineGeometry(ProjectSnapshot.FromProject(project), "line")!;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(before.Corners()[i].X, after.Corners()[i].X, 4);
            Assert.Equal(before.Corners()[i].Y, after.Corners()[i].Y, 4);
        }
    }

    [Fact]
    public void 未定时行的静态布局试样可见且不污染正常渲染缓存或源时间()
    {
        using SkiaRenderPipeline inner = new();
        CachedRenderPipeline pipeline = new(inner);
        Project project = CreateProject(ScenePlacement.Default);
        project = project with { Lyrics = project.Lyrics with { Lines = [LyricLine.Create("line", "甲乙丙")] } };
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
        RenderRequest request = RenderRequest.At(snapshot, 500_000) with { TransparentBackground = true };
        using RenderFrame normal = pipeline.Render(request);
        using RenderFrame staticFrame = pipeline.Render(request with { LayoutLineId = "line" });
        using RenderFrame normalAgain = pipeline.Render(request);
        Assert.All(normal.PixelMemory.ToArray(), value => Assert.Equal(0, value));
        Assert.True(staticFrame.PixelMemory.Span.ContainsAnyExcept((byte)0));
        Assert.True(normal.PixelMemory.Span.SequenceEqual(normalAgain.PixelMemory.Span));
        Assert.Null(project.Lyrics.Lines[0].Time);
        Assert.NotNull(pipeline.GetLineGeometry(snapshot, "line"));
    }

    private static Project CreateProject(ScenePlacement? placement) => Project.Create("位置",
        composition: new Composition { CanvasWidth = 640, CanvasHeight = 360 },
        lyricStyle: LyricStyle.Default with { FontSizePx = 32, UnsungColor = RgbaColor.White },
        lyrics: LyricsDocument.Create([LyricLine.Create("line", "甲乙丙", new TimeRange(0, 1_000_000), TimingStatus.Edited)])).WithLinePresentation("line", placement);
    private static (int Left, int Top, int Right, int Bottom) Bounds(RenderFrame frame)
    {
        int left = frame.Width, top = frame.Height, right = -1, bottom = -1;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.PixelMemory.Span[y * frame.Stride + x * 4 + 3] > 100)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        Assert.True(right >= left);
        return (left, top, right, bottom);
    }
}
