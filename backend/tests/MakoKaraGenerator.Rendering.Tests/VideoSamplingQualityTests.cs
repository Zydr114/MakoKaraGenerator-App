using System.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VideoSamplingQualityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(BackgroundFit.Contain)]
    [InlineData(BackgroundFit.Cover)]
    [InlineData(BackgroundFit.Original)]
    public void 真实透明视频与原始分辨率路径满足逐通道门槛(BackgroundFit fit)
        => Compare(fit, 641, 361, "mod(X*3+Y,256)", "mod(Y*5,256)", "if(lt(mod(X,17),8),240,10)", "64+mod(X+Y,192)", false);

    [Theory]
    [InlineData("220", "40", "30", "255")]
    [InlineData("220", "40", "30", "128")]
    [InlineData("if(lt(X,320),220,20)", "if(lt(Y,180),40,200)", "30", "if(lt(X,320),64,192)")]
    [InlineData("X/4", "Y/4", "0", "255")]
    [InlineData("mod(X,256)", "mod(Y,256)", "0", "255")]
    [InlineData("if(eq(mod(X,4),0),255,0)", "if(eq(mod(X,4),1),255,0)", "if(eq(mod(X,4),2),255,0)", "255")]
    [InlineData("mod(X*3+Y,256)", "mod(Y*5,256)", "if(lt(mod(X,17),8),240,10)", "64+mod(X+Y,192)")]
    public void 整倍数最近邻缩小与旧路径逐像素一致(string red, string green, string blue, string alpha)
        => Compare(BackgroundFit.Contain, 640, 360, red, green, blue, alpha, true);

    [Theory]
    [InlineData(2, BackgroundFit.Original)]
    [InlineData(2, BackgroundFit.Contain)]
    [InlineData(3, BackgroundFit.Cover)]
    [InlineData(5, BackgroundFit.Contain)]
    public void 多种整倍数与模式的高频透明纹理满足门槛(int factor, BackgroundFit fit)
        => Compare(fit, 160 * factor, 90 * factor, "mod(X*3+Y,256)", "mod(Y*5,256)", "if(lt(mod(X,17),8),240,10)", "64+mod(X+Y,192)", true);

    [Fact]
    public void 常见YUV视频转换后保持颜色()
        => Compare(BackgroundFit.Cover, 640, 360, "mod(X*3+Y,256)", "mod(Y*5,256)", "if(lt(mod(X,17),8),240,10)", "255", true, true);

    [Theory]
    [InlineData(641, 360, 160, 90, BackgroundFit.Cover, false)]
    [InlineData(641, 361, 160, 90, BackgroundFit.Contain, false)]
    [InlineData(640, 360, 160, 100, BackgroundFit.Contain, false)]
    [InlineData(320, 180, 160, 90, BackgroundFit.Original, true)]
    public void 边界几何与捕获遵循同一门槛(int width, int height, int targetWidth, int targetHeight, BackgroundFit fit, bool reduced)
        => Compare(fit, width, height, "mod(X*3+Y,256)", "mod(Y*5,256)", "if(lt(mod(X,17),8),240,10)", "64+mod(X+Y,192)", reduced, false, targetWidth, targetHeight);

    private void Compare(BackgroundFit fit, int width, int height, string red, string green, string blue, string alpha, bool expectReduced, bool yuv = false, int targetWidth = 160, int targetHeight = 90)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "quality.mkv");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=2:duration=1",
            "-vf", $"format=rgba,geq=r='{red}':g='{green}':b='{blue}':a='{alpha}'",
            "-c:v", yuv ? "libx264" : "ffv1", "-pix_fmt", yuv ? "yuv420p" : "bgra", "-y", path }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info)!)
        {
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
        }
        var project = Project.Create("quality", Composition.Create(320, 180, FrameRate.Fps30, new RgbaColor(16, 16, 20)),
            background: BackgroundSource.ForVideo(path, fit, BackgroundPlaybackMode.Loop));
        var request = RenderRequest.At(ProjectSnapshot.FromProject(project), 0) with { Target = new RenderTarget { PixelWidth = targetWidth, PixelHeight = targetHeight } };
        using var engine = new SkiaTextLayoutEngine();
        var recorded = new RecordingProvider();
        using var actualPipeline = new SkiaRenderPipeline(engine, new BackgroundFrameSourceResolver(recorded));
        using var baselinePipeline = new SkiaRenderPipeline(engine, new BackgroundFrameSourceResolver(new FullSizeProvider()));
        using var actual = actualPipeline.Render(request);
        using var baseline = baselinePipeline.Render(request);
        Assert.Equal(expectReduced ? targetWidth : width, recorded.Width);
        Assert.Equal(expectReduced ? targetHeight : height, recorded.Height);
        byte[] a = actual.PixelMemory.ToArray(), b = baseline.PixelMemory.ToArray();
        int[] maximum = new int[4]; long[] sums = new long[4];
        for (int i = 0; i < a.Length; i++)
        {
            int difference = Math.Abs(a[i] - b[i]);
            maximum[i % 4] = Math.Max(maximum[i % 4], difference);
            sums[i % 4] += difference;
        }
        using var diagnosticProvider = new FfmpegVideoFrameProvider();
        using var diagnostic = diagnosticProvider.Open(path, out _)!;
        Assert.True(diagnostic.TryGetFrame(0, out var full));
        byte[] fullPixels = [.. full!.Pixels];
        Assert.True(diagnostic.TryGetFrame(0, targetWidth, targetHeight, out var small, CancellationToken.None));
        output.WriteLine($"source={width}x{height} full-buffer={fullPixels.Length} requested={targetWidth}x{targetHeight} returned={small!.Width}x{small.Height} decoded-buffer={small.Pixels.Length}");
        if (expectReduced)
        {
            int factor = width / targetWidth, offset = (factor - 1) / 2;
            Assert.Equal(targetWidth * targetHeight * 4, small!.Pixels.Length);
            Assert.Equal(width * height * 4, fullPixels.Length);
            for (int y = 0; y < targetHeight; y++)
            for (int x = 0; x < targetWidth; x++)
            for (int channel = 0; channel < 4; channel++)
                Assert.Equal(fullPixels[((y * factor + offset) * width + x * factor + offset) * 4 + channel],
                    small.Pixels[(y * targetWidth + x) * 4 + channel]);
        }
        string report = $"fit={fit} BGRA max={string.Join(',', maximum)} mean={string.Join(',', sums.Select(s => (s / (double)(a.Length / 4)).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)))} firstActual={string.Join(',', a.Take(4))} firstBaseline={string.Join(',', b.Take(4))} rawSmall={string.Join(',', small!.Pixels.Take(4))} rawFullRow1={string.Join(',', fullPixels.Skip(width * 4 + 4).Take(12))} rawFullRow2={string.Join(',', fullPixels.Skip(width * 8 + 4).Take(12))}";
        output.WriteLine(report);
        Assert.True(maximum.All(value => value <= 2), report);
        var bounds = new GraphRect(7.25, 4.25, 150.75, 100.75);
        using var actualMedia = actualPipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, bounds, GraphExecutionBudget.Default)!;
        using var expectedMedia = baselinePipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, bounds, GraphExecutionBudget.Default)!;
        Assert.Equal(SkiaTestReadback.Pixels(expectedMedia.Image), SkiaTestReadback.Pixels(actualMedia.Image));
        using var actualColor = actualPipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, bounds, GraphExecutionBudget.Default)!;
        using var expectedColor = baselinePipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, bounds, GraphExecutionBudget.Default)!;
        Assert.Equal(SkiaTestReadback.Pixels(expectedColor.Image), SkiaTestReadback.Pixels(actualColor.Image));
    }

    private sealed class RecordingProvider : IVideoFrameProvider
    {
        private readonly FfmpegVideoFrameProvider _inner = new();
        public int Width { get; private set; }
        public int Height { get; private set; }
        public IVideoFrameSource? Open(string path, out string? failureReason)
        {
            var source = _inner.Open(path, out failureReason);
            return source is null ? null : new RecordingSource(source, this);
        }
        public void Dispose() => _inner.Dispose();
        private sealed class RecordingSource(IVideoFrameSource inner, RecordingProvider owner) : IVideoFrameSource
        {
            public long DurationUs => inner.DurationUs;
            public double FrameRate => inner.FrameRate;
            public int Width => inner.Width;
            public int Height => inner.Height;
            public string? LastError => inner.LastError;
            public bool TryGetFrame(long time, out VideoFrame? frame) => inner.TryGetFrame(time, out frame);
            public bool TryGetFrame(long time, int width, int height, out VideoFrame? frame, CancellationToken token)
            {
                owner.Width = width; owner.Height = height;
                return inner.TryGetFrame(time, width, height, out frame, token);
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class FullSizeProvider : IVideoFrameProvider
    {
        private readonly FfmpegVideoFrameProvider _inner = new();
        public IVideoFrameSource? Open(string path, out string? failureReason)
        {
            var source = _inner.Open(path, out failureReason);
            return source is null ? null : new FullSizeSource(source);
        }
        public void Dispose() => _inner.Dispose();
    }

    private sealed class FullSizeSource(IVideoFrameSource inner) : IVideoFrameSource
    {
        public long DurationUs => inner.DurationUs;
        public double FrameRate => inner.FrameRate;
        public int Width => inner.Width;
        public int Height => inner.Height;
        public string? LastError => inner.LastError;
        public bool TryGetFrame(long time, out VideoFrame? frame) => inner.TryGetFrame(time, out frame);
        public bool TryGetFrame(long time, out VideoFrame? frame, CancellationToken token) => inner.TryGetFrame(time, out frame, token);
        public void Dispose() => inner.Dispose();
    }
}
