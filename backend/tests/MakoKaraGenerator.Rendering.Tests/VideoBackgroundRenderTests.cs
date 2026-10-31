using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>视频背景的渲染测试（roadmap P1「背景视频完整支持」）。</summary>
/// <remarks>
/// 用假的帧提供者，因为在渲染这一层要验证的是"帧有没有被画上去、时间策略有没有被应用"，
/// 而不是 FFmpeg 能不能解码（那个在 Media 层有自己的真实集成测试）。
/// </remarks>
public sealed class VideoBackgroundRenderTests : IDisposable
{
    private static readonly RgbaColor Background = new(0x10, 0x10, 0x14);
    private static readonly RgbaColor VideoColor = new(0x00, 0xE0, 0x40);

    private readonly SkiaTextLayoutEngine _layoutEngine = new();

    /// <inheritdoc/>
    public void Dispose() => _layoutEngine.Dispose();

    [Fact]
    public void 视频画面会画到画布上()
    {
        FakeVideoSource source = new(VideoColor, durationUs: 5_000_000, width: 64, height: 48);

        using SkiaRenderPipeline pipeline = CreatePipeline(source);
        using RenderFrame frame = pipeline.Render(RenderRequest.At(CreateSnapshot(BackgroundFit.Cover), 1_000_000));

        // 纯视频背景铺满画布（Cover），中心必定是视频的颜色。
        Assert.True(frame.TryGetPixel(frame.Width / 2, frame.Height / 2, out RgbaColor center));
        Assert.Equal(VideoColor, center);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public void 循环策略把工程时间映射成视频内的时间()
    {
        FakeVideoSource source = new(VideoColor, durationUs: 1_000_000);

        using SkiaRenderPipeline pipeline = CreatePipeline(source);
        using RenderFrame _ = pipeline.Render(RenderRequest.At(CreateSnapshot(), 2_500_000));

        Assert.Equal(500_000, source.LastRequestedTimeUs);
    }

    [Fact]
    public void 冻结策略把工程时间夹到视频末尾()
    {
        FakeVideoSource source = new(VideoColor, durationUs: 1_000_000);

        using SkiaRenderPipeline pipeline = CreatePipeline(source);
        using RenderFrame _ = pipeline.Render(RenderRequest.At(
            CreateSnapshot(playback: BackgroundPlaybackMode.FreezeLastFrame),
            2_500_000));

        Assert.Equal(999_999, source.LastRequestedTimeUs);
    }

    [Fact]
    public void 视频打不开时只剩背景色()
    {
        // 文件缺失或解码失败：预览与导出都不该失败，画面退化成背景色。
        using SkiaRenderPipeline pipeline = CreatePipeline(source: null);
        using RenderFrame frame = pipeline.Render(RenderRequest.At(CreateSnapshot(), 1_000_000));

        Assert.True(frame.TryGetPixel(frame.Width / 2, frame.Height / 2, out RgbaColor center));
        Assert.Equal(Background, center);
    }

    [Fact]
    public void 视频比画布小时留白处仍是背景色()
    {
        FakeVideoSource source = new(VideoColor, durationUs: 5_000_000, width: 4, height: 4);

        using SkiaRenderPipeline pipeline = CreatePipeline(source);
        using RenderFrame frame = pipeline.Render(RenderRequest.At(CreateSnapshot(BackgroundFit.Contain), 1_000_000));

        // Contain：小视频居中，左边缘必然是背景色；视频本身画在中心附近。
        Assert.True(frame.TryGetPixel(0, frame.Height / 2, out RgbaColor left));
        Assert.Equal(Background, left);
        Assert.True(frame.TryGetPixel(frame.Width / 2, frame.Height / 2, out RgbaColor center));
        Assert.Equal(VideoColor, center);
    }

    private SkiaRenderPipeline CreatePipeline(IVideoFrameSource? source) =>
        new(_layoutEngine, new BackgroundFrameSourceResolver(source is null ? null : new FakeVideoProvider(source)));

    private static ProjectSnapshot CreateSnapshot(
        BackgroundFit fit = BackgroundFit.Contain,
        BackgroundPlaybackMode playback = BackgroundPlaybackMode.Loop)
    {
        Project project = Project.Create(
            "视频背景",
            Composition.Create(320, 180, FrameRate.Fps30, Background, outputDurationUs: 4_000_000),
            background: BackgroundSource.ForVideo("/tmp/背景.mp4", fit, playback));

        return ProjectSnapshot.FromProject(project);
    }

    /// <summary>假帧来源：任何时刻都给同一块纯色，并记下被问过的时间。</summary>
    private sealed class FakeVideoSource : IVideoFrameSource
    {
        private readonly byte[] _pixels;
        private readonly VideoFrame _frame;

        public FakeVideoSource(RgbaColor color, long durationUs, int width = 32, int height = 18)
        {
            DurationUs = durationUs;
            Width = width;
            Height = height;
            _pixels = new byte[width * 4 * height];

            for (int i = 0; i < _pixels.Length; i += 4)
            {
                // VideoFrame 是 BGRA：字节顺序与 RgbaColor 相反。
                _pixels[i] = color.Blue;
                _pixels[i + 1] = color.Green;
                _pixels[i + 2] = color.Red;
                _pixels[i + 3] = color.Alpha;
            }

            _frame = new VideoFrame(width, height, width * 4, _pixels);
        }

        public long DurationUs { get; }

        public double FrameRate => 30d;

        public int Width { get; }

        public int Height { get; }

        public string? LastError => null;

        public long LastRequestedTimeUs { get; private set; } = -1;

        public int CallCount { get; private set; }

        public bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame)
        {
            LastRequestedTimeUs = sourceTimeUs;
            CallCount++;
            frame = _frame;

            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>假提供者：无论路径如何都给出同一个帧来源。</summary>
    private sealed class FakeVideoProvider : IVideoFrameProvider
    {
        private readonly IVideoFrameSource _source;

        public FakeVideoProvider(IVideoFrameSource source)
        {
            _source = source;
        }

        public IVideoFrameSource? Open(string path, out string? failureReason)
        {
            failureReason = null;

            return _source;
        }

        public void Dispose()
        {
        }
    }
}
