using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>有界渲染帧缓存的命中与快照失效测试。</summary>
public sealed class CachedRenderPipelineTests
{
    private static readonly RenderTarget Target = new() { PixelWidth = 1, PixelHeight = 1 };

    [Fact]
    public void 相同快照与帧输入会命中缓存()
    {
        CountingPipeline inner = new();
        CachedRenderPipeline pipeline = new(inner);
        ProjectSnapshot snapshot = Snapshot();
        RenderRequest request = Request(snapshot, timeUs: 1_000);

        using RenderFrame first = pipeline.Render(request);
        using RenderFrame second = pipeline.Render(request);

        Assert.Equal(1, inner.RenderCount);
        Assert.Equal(first.Pixels.ToArray(), second.Pixels.ToArray());
        Assert.False(second.IsPooled);
    }

    [Fact]
    public void 新快照会使旧帧缓存失效()
    {
        CountingPipeline inner = new();
        CachedRenderPipeline pipeline = new(inner);
        ProjectSnapshot firstSnapshot = Snapshot();
        ProjectSnapshot secondSnapshot = firstSnapshot with { Name = "另一个工程" };

        using RenderFrame first = pipeline.Render(Request(firstSnapshot, timeUs: 1_000));
        using RenderFrame second = pipeline.Render(Request(secondSnapshot, timeUs: 1_000));
        using RenderFrame third = pipeline.Render(Request(secondSnapshot, timeUs: 1_000));

        Assert.Equal(2, inner.RenderCount);
        Assert.False(first.Pixels.ToArray().SequenceEqual(second.Pixels.ToArray()));
        Assert.Equal(second.Pixels.ToArray(), third.Pixels.ToArray());
    }

    private static RenderRequest Request(ProjectSnapshot snapshot, long timeUs) => new()
    {
        Project = snapshot,
        TimeUs = timeUs,
        Target = Target,
        Quality = RenderQuality.Draft,
    };

    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(
        Project.Create("缓存测试", Composition.Create(1, 1, FrameRate.Fps30)));

    private sealed class CountingPipeline : IRenderPipeline
    {
        public int RenderCount { get; private set; }

        public string BackendName => "测试管线";

        public RenderFrame Render(RenderRequest request)
        {
            RenderCount++;
            return new RenderFrame(
                [(byte)RenderCount, 0, 0, 255],
                request.ResolveTarget(),
                PixelFormat.Bgra8888Premultiplied,
                request.TimeUs,
                pooledBuffer: false);
        }
    }
}
