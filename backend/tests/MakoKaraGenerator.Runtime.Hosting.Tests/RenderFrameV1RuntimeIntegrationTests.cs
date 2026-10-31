using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class RenderFrameV1RuntimeIntegrationTests
{
    [Fact]
    public async Task 同进程适配器使用真实RuntimeHosting会话返回有界像素帧()
    {
        using TemporaryDirectory temp = new();
        ProjectSnapshot snapshot = Snapshot(64, 36);
        var runtime = new ProjectRenderSessionFactory(
            Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache"), new FfmpegVideoFrameProvider(), ReplayReason.Initial);
        var adapter = new RenderFrameV1SessionFactory(runtime, new(64, 36, 64L * 36 * 4));

        await using IRenderFrameSession session = await adapter.CreateAsync(snapshot);
        using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, 0));

        Assert.Equal(64, frame.Width);
        Assert.Equal(36, frame.Height);
        Assert.Equal(64 * 36 * 4, frame.PixelMemory.Length);
        Assert.Equal(PixelFormat.Bgra8888Premultiplied, frame.Format);
    }

    private static ProjectSnapshot Snapshot(int width, int height) =>
        ProjectSnapshot.FromProject(Project.Create(
            "render-frame-v1",
            Composition.Create(width, height, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([])));
}
