using System.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class BackgroundResolverIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 同一解析器切换循环和冻结分别请求正确源时刻(bool freezeFirst)
    {
        using var temp = new TemporaryDirectory(); using var provider = new RecordingProvider();
        using var resolver = new BackgroundFrameSourceResolver(provider);
        var loop = BackgroundSource.ForVideo(temp.FilePath("video.media"));
        var freeze = loop with { Playback = BackgroundPlaybackMode.FreezeLastFrame };
        var first = resolver.Resolve(freezeFirst ? freeze : loop)!;
        _ = first.GetFrame(2_500_000);
        var second = resolver.Resolve(freezeFirst ? loop : freeze)!;
        _ = second.GetFrame(2_500_000);
        Assert.Equal(freezeFirst ? 500_000 : 999_999, provider.Sources[^1].LastTime);
        Assert.NotSame(first, second); Assert.Equal(2, provider.Sources.Count);
        Assert.Same(first, resolver.Resolve(freezeFirst ? freeze : loop));
        resolver.Dispose(); resolver.Dispose();
        Assert.All(provider.Sources, source => Assert.Equal(1, source.DisposeCount));
        Assert.False(provider.Disposed);
    }

    [Fact]
    public async Task 真实一秒视频复用解析器时循环及冻结像素各自对应正确帧()
    {
        using var temp = new TemporaryDirectory(); string path = temp.FilePath("one-second.mp4");
        var info = new ProcessStartInfo(RenderingTestEnvironment.FfmpegExecutable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10:duration=1",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", "-y", path }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        {
            using var errors = process.StandardError; string text = await errors.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, text);
        }
        using var provider = new FfmpegVideoFrameProvider(); using var resolver = new BackgroundFrameSourceResolver(provider);
        using var reference = provider.Open(path, out _)!;
        Assert.True(reference.TryGetFrame(500_000, out var loopFrame)); byte[] expectedLoop = [.. loopFrame!.Pixels];
        Assert.True(reference.TryGetFrame(999_999, out var last)); byte[] expectedFreeze = [.. last!.Pixels];
        Assert.NotEqual(expectedLoop, expectedFreeze);
        var background = BackgroundSource.ForVideo(path);
        Assert.Equal(expectedLoop, resolver.Resolve(background)!.GetFrame(2_500_000)!.Bytes);
        Assert.Equal(expectedFreeze, resolver.Resolve(background with { Playback = BackgroundPlaybackMode.FreezeLastFrame })!.GetFrame(2_500_000)!.Bytes);
        Assert.Equal(expectedLoop, resolver.Resolve(background)!.GetFrame(2_500_000)!.Bytes);
    }

    private sealed class RecordingProvider : IVideoFrameProvider
    {
        internal readonly List<RecordingSource> Sources = [];
        internal readonly List<string> Paths = [];
        internal Action? AfterCreate { get; set; }
        internal bool Disposed;
        public IVideoFrameSource? Open(string path, out string? failureReason) => Open(path, out failureReason, CancellationToken.None);
        public IVideoFrameSource? Open(string path, out string? failureReason, CancellationToken cancellationToken)
        {
            failureReason = null; var source = new RecordingSource();
            lock (Sources) { Sources.Add(source); Paths.Add(path); }
            AfterCreate?.Invoke(); return source;
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class RecordingSource : IVideoFrameSource
    {
        internal int DisposeCount;
        internal long LastTime;
        internal bool ThrowOnDispose { get; set; }
        public long DurationUs => 1_000_000;
        public double FrameRate => 30;
        public int Width => 1; public int Height => 1;
        public string? LastError => null;
        public bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame)
        { LastTime = sourceTimeUs; frame = new(1, 1, 4, new byte[] { 40, 50, 60, 255 }); return true; }
        public void Dispose() { DisposeCount++; if (ThrowOnDispose) throw new IOException("owned source release failed"); }
    }
}
