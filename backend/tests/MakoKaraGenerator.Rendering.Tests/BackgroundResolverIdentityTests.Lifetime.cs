using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class BackgroundResolverIdentityTests
{
    [Fact]
    public void 缓存规范路径但不包含绘制适配及显示文件名()
    {
        using var temp = new TemporaryDirectory(); using var provider = new RecordingProvider(); using var resolver = new BackgroundFrameSourceResolver(provider);
        string path = temp.FilePath("video.media"); var background = BackgroundSource.ForVideo(path);
        var first = resolver.Resolve(background);
        Assert.Same(first, resolver.Resolve(background with { Path = Path.GetRelativePath(Environment.CurrentDirectory, path), Fit = BackgroundFit.Cover }));
        Assert.Same(first, resolver.Resolve(background with { Path = Path.Combine(temp.Path, "nested", "..", "video.media") }));
        Assert.Single(provider.Paths); Assert.Equal(Path.GetFullPath(path), provider.Paths[0]);
    }

    [Fact]
    public void 固定媒体摘要和长度参加身份而原名称不参加()
    {
        using var temp = new TemporaryDirectory(); using var provider = new RecordingProvider(); using var resolver = new BackgroundFrameSourceResolver(provider);
        var media = new MediaFileRevision(new string('a', 64), 3, "first.media");
        var background = BackgroundSource.ForVideo(temp.FilePath("managed.media")) with { Media = media };
        var first = resolver.Resolve(background);
        Assert.Same(first, resolver.Resolve(background with { Media = new(media.Hash, media.ByteLength, "renamed.media") }));
        Assert.NotSame(first, resolver.Resolve(background with { Media = new(new string('b', 64), media.ByteLength, "first.media") }));
        Assert.NotSame(first, resolver.Resolve(background with { Media = new(media.Hash, 4, "first.media") }));
        Assert.Equal(3, provider.Sources.Count);
    }

    [Fact]
    public void 同路径图片与视频不共享来源而图片忽略播放模式()
    {
        using var temp = new TemporaryDirectory(); string path = temp.FilePath("both.media");
        using var bitmap = new SKBitmap(1, 1); bitmap.SetPixel(0, 0, new SKColor(0, 0, 200));
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
        using var provider = new RecordingProvider(); using var resolver = new BackgroundFrameSourceResolver(provider);
        var video = resolver.Resolve(BackgroundSource.ForVideo(path))!;
        var description = BackgroundSource.ForImage(path); var still = resolver.Resolve(description)!;
        Assert.NotSame(video, still); Assert.Equal(40, video.GetFrame(0)!.GetPixel(0, 0).Blue); Assert.Equal(200, still.GetFrame(0)!.GetPixel(0, 0).Blue);
        Assert.Same(still, resolver.Resolve(description with { Playback = BackgroundPlaybackMode.FreezeLastFrame }));
    }

    [Fact]
    public void 两解析器不共享会话来源且分别释放自己的缓存()
    {
        using var temp = new TemporaryDirectory(); using var provider = new RecordingProvider(); using var first = new BackgroundFrameSourceResolver(provider);
        using var second = new BackgroundFrameSourceResolver(provider); var background = BackgroundSource.ForVideo(temp.FilePath("video.media"));
        Assert.NotSame(first.Resolve(background), second.Resolve(background));
        first.Dispose(); Assert.Equal(1, provider.Sources[0].DisposeCount); Assert.Equal(0, provider.Sources[1].DisposeCount);
        Assert.NotNull(second.Resolve(background)!.GetFrame(0)); second.Dispose(); Assert.Equal(1, provider.Sources[1].DisposeCount);
        Assert.False(provider.Disposed);
    }

    [Fact]
    public async Task 两并发候选只发布一个并明确释放未采用来源()
    {
        using var temp = new TemporaryDirectory(); using var barrier = new Barrier(2); using var provider = new RecordingProvider();
        provider.AfterCreate = () => { if (!barrier.SignalAndWait(TimeSpan.FromSeconds(2))) throw new TimeoutException(); };
        using var resolver = new BackgroundFrameSourceResolver(provider); var background = BackgroundSource.ForVideo(temp.FilePath("video.media"));
        var results = await Task.WhenAll(Task.Run(() => resolver.Resolve(background)), Task.Run(() => resolver.Resolve(background))).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(results[0], results[1]); Assert.Equal(2, provider.Sources.Count);
        Assert.Single(provider.Sources, source => source.DisposeCount == 1);
        resolver.Dispose(); Assert.All(provider.Sources, source => Assert.Equal(1, source.DisposeCount));
    }

    [Fact]
    public void 打开后迟到取消释放未发布来源且不污染下次缓存()
    {
        using var temp = new TemporaryDirectory(); using var cancellation = new CancellationTokenSource(); using var provider = new RecordingProvider();
        provider.AfterCreate = cancellation.Cancel; using var resolver = new BackgroundFrameSourceResolver(provider);
        var background = BackgroundSource.ForVideo(temp.FilePath("video.media"));
        Assert.ThrowsAny<OperationCanceledException>(() => resolver.Resolve(background, cancellation.Token));
        Assert.Equal(1, provider.Sources[0].DisposeCount); provider.AfterCreate = null;
        Assert.NotNull(resolver.Resolve(background)); Assert.Equal(2, provider.Sources.Count);
    }

    [Fact]
    public async Task 关闭不等待他人探测但释放迟到候选且不能重新发布()
    {
        using var temp = new TemporaryDirectory(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var provider = new RecordingProvider(); provider.AfterCreate = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); };
        using var resolver = new BackgroundFrameSourceResolver(provider); var background = BackgroundSource.ForVideo(temp.FilePath("video.media"));
        var pending = Task.Run(() => resolver.Resolve(background));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2))); resolver.Dispose(); release.Set();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, provider.Sources[0].DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => resolver.Resolve(background)); Assert.Single(provider.Sources);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void 缓存某来源释放失败仍释放其他来源并报告真实失败()
    {
        using var temp = new TemporaryDirectory(); using var provider = new RecordingProvider(); using var resolver = new BackgroundFrameSourceResolver(provider);
        var background = BackgroundSource.ForVideo(temp.FilePath("video.media")); _ = resolver.Resolve(background);
        _ = resolver.Resolve(background with { Playback = BackgroundPlaybackMode.FreezeLastFrame }); provider.Sources[0].ThrowOnDispose = true;
        var error = Assert.Throws<AggregateException>(resolver.Dispose); Assert.IsType<IOException>(Assert.Single(error.InnerExceptions));
        Assert.All(provider.Sources, source => Assert.Equal(1, source.DisposeCount)); resolver.Dispose();
    }
}
