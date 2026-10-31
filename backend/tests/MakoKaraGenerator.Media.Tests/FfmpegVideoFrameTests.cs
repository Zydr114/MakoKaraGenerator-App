using System.Diagnostics;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>
/// 背景视频帧来源的真实 FFmpeg 集成测试（roadmap P1「背景视频完整支持」）。
/// </summary>
/// <remarks>
/// 测试视频由 FFmpeg 的 <c>testsrc</c> 现场生成（10 fps、64×48、2 秒、H.264）：
/// 每帧画面都不同，因此"不同时刻必须拿到不同画面"有确定的期望值，
/// 而不是靠"看起来像在动"。
/// </remarks>
public sealed class FfmpegVideoFrameTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private const double FrameRate = 10d;
    private const double DurationSeconds = 2d;

    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 打开视频后能探测宽高帧率与时长()
    {
        string path = CreateTestVideo();

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource? source = provider.Open(path, out string? failureReason);

        Assert.Null(failureReason);
        Assert.NotNull(source);
        Assert.Equal(Width, source.Width);
        Assert.Equal(Height, source.Height);
        Assert.Equal(FrameRate, source.FrameRate, 1);
        Assert.InRange(source.DurationUs, 1_800_000, 2_200_000);
    }

    [Fact]
    public void 取第一帧成功且尺寸与内容符合预期()
    {
        string path = CreateTestVideo();

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(path, out _)!;

        Assert.True(source.TryGetFrame(0, out VideoFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(Width, frame.Width);
        Assert.Equal(Height, frame.Height);
        Assert.Equal(Width * 4, frame.Stride);
        Assert.Null(source.LastError);

        // testsrc 的第一帧不是纯黑：至少要有一些非零像素，否则"取到了帧"没有意义。
        Assert.Contains(frame.Pixels, value => value != 0);
    }

    [Fact]
    public void 不同时刻取到不同画面()
    {
        string path = CreateTestVideo();

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(path, out _)!;

        Assert.True(source.TryGetFrame(0, out VideoFrame? first));
        byte[] firstCopy = [.. first!.Pixels];

        Assert.True(source.TryGetFrame(1_500_000, out VideoFrame? later));

        Assert.NotEqual(firstCopy, later!.Pixels);
    }

    [Fact]
    public void 往回跳会重新定位并给出那一时刻的画面()
    {
        // 预览里拖播放头就会往回跳：这条路径必须自己重启解码进程，否则会一直读到结尾。
        string path = CreateTestVideo();

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(path, out _)!;

        Assert.True(source.TryGetFrame(1_800_000, out _));
        Assert.True(source.TryGetFrame(200_000, out VideoFrame? backward));
        Assert.Null(source.LastError);

        // 与"直接取 200ms"应当一致：定位是确定性的，不依赖之前读过什么。
        using FfmpegVideoFrameProvider fresh = new();
        using IVideoFrameSource expected = fresh.Open(path, out _)!;
        Assert.True(expected.TryGetFrame(200_000, out VideoFrame? direct));

        Assert.Equal(direct!.Pixels, backward!.Pixels);
    }

    [Fact]
    public void 同一时刻重复取帧内容相同()
    {
        string path = CreateTestVideo();

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(path, out _)!;

        Assert.True(source.TryGetFrame(700_000, out VideoFrame? first));
        byte[] firstCopy = [.. first!.Pixels];

        Assert.True(source.TryGetFrame(700_000, out VideoFrame? second));

        Assert.Equal(firstCopy, second!.Pixels);
    }

    [Fact]
    public void 时长末尾的时刻也能取到帧()
    {
        // 冻结策略会把时间夹到"最后一微秒"，所以这个边界一定会被走到。
        string path = CreateTestVideo();

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(path, out _)!;

        Assert.True(source.TryGetFrame(source.DurationUs - 1, out VideoFrame? frame));
        Assert.NotNull(frame);
    }

    [Fact]
    public void 文件不存在时打不开并说明原因()
    {
        using FfmpegVideoFrameProvider provider = new();

        IVideoFrameSource? source = provider.Open(
            Path.Combine(_directory.Path, "没有这个文件.mp4"),
            out string? failureReason);

        Assert.Null(source);
        Assert.NotNull(failureReason);
        Assert.Contains("找不到", failureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 音频文件当视频打开会被拒绝()
    {
        string path = Path.Combine(_directory.Path, "只有声音.wav");

        Run(
            "ffmpeg",
            "-v", "error",
            "-f", "lavfi",
            "-i", "sine=frequency=440:duration=1",
            "-y",
            path);

        using FfmpegVideoFrameProvider provider = new();
        IVideoFrameSource? source = provider.Open(path, out string? failureReason);

        Assert.Null(source);
        Assert.NotNull(failureReason);
    }

    [Fact]
    public void 帧缓冲过小时构造被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrame(4, 4, 16, new byte[16]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoFrame(0, 4, 16, new byte[64]));
    }

    /// <summary>现场生成一段 <c>testsrc</c> 视频。</summary>
    private string CreateTestVideo()
    {
        string path = Path.Combine(_directory.Path, "背景.mp4");

        Run(
            "ffmpeg",
            "-v", "error",
            "-f", "lavfi",
            "-i", $"testsrc=size={Width}x{Height}:rate={FrameRate}:duration={DurationSeconds}",
            "-pix_fmt", "yuv420p",
            "-c:v", "libx264",
            "-y",
            path);

        return path;
    }

    private static void Run(string executable, params string[] arguments)
    {
        ProcessStartInfo info = new(executable) { RedirectStandardError = true, UseShellExecute = false };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        using var errors = process.StandardError;
        string stderr = errors.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{executable} 失败：{stderr}");
    }
}
