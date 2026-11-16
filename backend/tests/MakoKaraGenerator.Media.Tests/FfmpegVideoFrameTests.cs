using System.Diagnostics;
using System.Text.Json;
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

    [Fact]
    public void 无损透明视频缩放后保留颜色并在尺寸变化和跳转时重新取帧()
    {
        string path = Path.Combine(_directory.Path, "lossless.mkv");
        Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=321x181:rate=2:duration=2",
            "-vf", "format=rgba,geq=r='if(lt(X,160),220,20)':g='if(lt(Y,90),40,200)':b='30+N*30':a='128+N*20'",
            "-c:v", "ffv1", "-pix_fmt", "bgra", "-y", path);
        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(path, out _)!;
        Assert.Equal(321, source.Width);
        Assert.Equal(181, source.Height);

        Assert.True(source.TryGetFrame(0, 81, 46, out var first, CancellationToken.None));
        Assert.Equal(81, first!.Width);
        Assert.Equal(46, first.Height);
        byte[] original = [.. first.Pixels];
        Assert.InRange(original[0], 28, 32);
        Assert.InRange(original[1], 38, 42);
        Assert.InRange(original[2], 218, 222);
        Assert.InRange(original[3], 126, 130);
        Assert.True(source.TryGetFrame(0, 81, 46, out var repeated, CancellationToken.None));
        Assert.Equal(original, repeated!.Pixels);
        Assert.True(source.TryGetFrame(1_000_000, 81, 46, out var forward, CancellationToken.None));
        Assert.NotEqual(original, forward!.Pixels);
        Assert.True(source.TryGetFrame(0, 81, 46, out var backward, CancellationToken.None));
        Assert.Equal(original, backward!.Pixels);
        Assert.True(source.TryGetFrame(0, 161, 91, out var resized, CancellationToken.None));
        Assert.Equal(161, resized!.Width);
        Assert.Equal(91, resized.Height);
        Assert.InRange(resized.Pixels[3], 126, 130);
        Assert.True(source.TryGetFrame(0, 81, 46, out var restored, CancellationToken.None));
        Assert.Equal(original, restored!.Pixels);
        Assert.True(source.TryGetFrame(0, out var full));
        Assert.Equal(321, full!.Width);
        Assert.Equal(181, full.Height);
    }

    [Fact]
    public void 缺少缩放滤镜时回退原始帧并保持后续读取()
    {
        string path = CreateTestVideo();
        string executable;
        if (OperatingSystem.IsWindows())
        {
            string pyPath = Path.Combine(_directory.Path, "ffmpeg-without-zscale.py");
            File.WriteAllText(pyPath, $"""
                import subprocess, sys
                if any('zscale' in arg for arg in sys.argv[1:]):
                    sys.exit(1)
                sys.exit(subprocess.call([{System.Text.Json.JsonSerializer.Serialize(TestMediaToolPaths.Ffmpeg)}, *sys.argv[1:]]))
                """);
            executable = Path.Combine(_directory.Path, "ffmpeg-without-zscale.cmd");
            File.WriteAllText(executable, $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{pyPath}\" %*\r\n");
        }
        else
        {
            executable = Path.Combine(_directory.Path, "ffmpeg-without-zscale");
            File.WriteAllText(executable, "#!/bin/sh\nfor arg in \"$@\"; do case \"$arg\" in *zscale*) exit 1;; esac; done\nexec ffmpeg \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using FfmpegVideoFrameProvider provider = new(executable);
        using IVideoFrameSource source = provider.Open(path, out _)!;
        Assert.True(source.TryGetFrame(0, 16, 12, out var first, CancellationToken.None));
        Assert.Equal(Width, first!.Width);
        Assert.Equal(Height, first.Height);
        Assert.Null(source.LastError);
        Assert.True(source.TryGetFrame(200_000, 16, 12, out var later, CancellationToken.None));
        Assert.Equal(Width, later!.Width);
        Assert.Equal(Height, later.Height);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void 带显示矩阵的视频按原尺寸解码并与原路径一致(int rotation)
    {
        string original = Path.Combine(_directory.Path, "unrotated.mp4");
        string rotated = Path.Combine(_directory.Path, $"rotated-{rotation}.mp4");
        Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10:duration=2",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", "-y", original);
        Run("ffmpeg", "-v", "error", "-display_rotation:v:0", rotation.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", original, "-c", "copy", "-y", rotated);

        using JsonDocument probe = JsonDocument.Parse(CaptureOutput("ffprobe", "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height:stream_side_data=side_data_type,displaymatrix,rotation", "-of", "json", rotated));
        JsonElement stream = probe.RootElement.GetProperty("streams")[0];
        int width = stream.GetProperty("width").GetInt32();
        int height = stream.GetProperty("height").GetInt32();
        JsonElement matrix = stream.GetProperty("side_data_list").EnumerateArray()
            .Single(item => item.GetProperty("side_data_type").GetString() == "Display Matrix");
        Assert.False(string.IsNullOrWhiteSpace(matrix.GetProperty("displaymatrix").GetString()));
        Assert.Equal(90, Math.Abs(matrix.GetProperty("rotation").GetInt32()));
        Assert.Equal(0, width % 5);
        Assert.Equal(0, height % 5);

        using FfmpegVideoFrameProvider provider = new();
        using IVideoFrameSource source = provider.Open(rotated, out _)!;
        using IVideoFrameSource baseline = provider.Open(rotated, out _)!;
        Assert.Equal(width, source.Width);
        Assert.Equal(height, source.Height);
        foreach ((long time, int divisor) in new[] { (0L, 5), (0L, 5), (1_000_000L, 5), (200_000L, 2), (0L, 5) })
        {
            Assert.True(baseline.TryGetFrame(time, out var expected), baseline.LastError);
            byte[] expectedPixels = [.. expected!.Pixels];
            Assert.True(source.TryGetFrame(time, width / divisor, height / divisor, out var actual, CancellationToken.None), source.LastError);
            Assert.Equal(width, actual!.Width);
            Assert.Equal(height, actual.Height);
            Assert.Equal(expectedPixels, actual.Pixels);
            Assert.Null(source.LastError);
        }
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

    private static string CaptureOutput(string executable, params string[] arguments)
    {
        ProcessStartInfo info = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{executable} 失败：{stderr}");
        return output;
    }
}
