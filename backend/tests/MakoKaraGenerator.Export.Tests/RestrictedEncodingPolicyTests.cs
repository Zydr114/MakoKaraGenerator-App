using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Export.Tests;

public sealed class RestrictedEncodingPolicyTests
{
    [Fact]
    public void 强WAV输入和辅助字节限额不改变原AAC与视频编码策略()
    {
        VideoEncodingRequest request = new()
        {
            OutputPath = "output.mp4", Width = 320, Height = 180, FrameRate = FrameRate.Fps30,
            TotalFrames = 90, AudioPath = "input.wav", AudioInputFormat = "wav", MaximumOutputBytes = 64 * 1024 * 1024,
            AudioPlan = AudioEncodingPlan.Decide("pcm_s16le"),
        };
        IReadOnlyList<string> arguments = FfmpegCommandLine.BuildVideoEncoding(request);
        int input = arguments.ToList().IndexOf("input.wav");
        Assert.Equal("-i", arguments[input - 1]);
        Assert.Equal("wav", arguments[input - 2]);
        Assert.Contains("file,pipe", arguments);
        Assert.Contains("aac", arguments);
        Assert.Contains("libx264", arguments);
        Assert.DoesNotContain("-shortest", arguments);
        Assert.Contains("-t", arguments);
        int size = arguments.ToList().IndexOf("-fs"); Assert.Equal("67174400", arguments[size + 1]);
        string[] wave = ["-hide_banner", "-nostdin", "-loglevel", "warning", "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", "320x180", "-r", "30", "-i", "pipe:0", "-protocol_whitelist", "file,pipe", "-f", "wav", "-i", "input.wav",
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-preset", "medium",
            "-c:a", "aac", "-b:a", "192k", "-t", "3", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats",
            "-fs", "67174400", "-y", "output.mp4"];
        Assert.Equal(wave, arguments);
        IReadOnlyList<string> ordinary = FfmpegCommandLine.BuildVideoEncoding(request with { AudioInputFormat = null, MaximumOutputBytes = null });
        Assert.DoesNotContain("-protocol_whitelist", ordinary);
        Assert.DoesNotContain("-fs", ordinary);
        Assert.Contains("-shortest", ordinary);
        Assert.Equal(wave[..14].Concat(wave[18..36]).Concat(["-shortest"]).Concat(wave[36..43]).Concat(wave[45..]), ordinary);
    }

    [Theory]
    [InlineData("mp3", false)]
    [InlineData("flac", false)]
    [InlineData("mp3", true)]
    [InlineData("flac", true)]
    public void 强压缩输入严格解码并保留唯一选流有理裁切和完整视频策略(string format, bool transparent)
    {
        VideoEncodingRequest request = new()
        {
            OutputPath = transparent ? "output.mov" : "output.mp4", Width = 32, Height = 18,
            FrameRate = new(30_000, 1001), TotalFrames = 60, AudioPath = "owned.input", AudioInputFormat = format,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4,
            AudioPlan = new(false, "aac", "managed compressed audio"),
        };
        List<string> arguments = [.. FfmpegCommandLine.BuildVideoEncoding(request)];
        Assert.Equal("-xerror", arguments[0]);
        Assert.Equal(1, arguments.Count(value => value == "-xerror"));
        int policy = arguments.IndexOf("-err_detect");
        Assert.Equal(new[] { "-err_detect", "explode", "-protocol_whitelist", "file,pipe", "-f", format, "-i", "owned.input" },
            arguments.GetRange(policy, 8));
        Assert.Equal(transparent ? "pcm_s16le" : "aac", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.Equal("2.002", arguments[arguments.IndexOf("-t") + 1]);
        Assert.Contains("0:v:0", arguments); Assert.Contains("1:a:0", arguments);
        Assert.DoesNotContain("-shortest", arguments); Assert.DoesNotContain("copy", arguments);
    }

    [Fact]
    public async Task 未知受限音频格式在native启动前拒绝()
    {
        using TemporaryDirectory directory = new();
        VideoEncodingRequest request = new()
        {
            OutputPath = directory.FilePath("candidate.mp4"), Width = 32, Height = 18,
            FrameRate = FrameRate.Fps30, TotalFrames = 30, AudioInputFormat = "hls",
        };
        await Assert.ThrowsAsync<ArgumentException>(() => new FfmpegMediaEncoder(directory.FilePath("must-not-run"))
            .StartAsync(request));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task MOV受限音频格式通过编码器门禁()
    {
        using TemporaryDirectory directory = new();
        string audio = directory.FilePath("owned.input");
        await File.WriteAllBytesAsync(audio, [0]);
        VideoEncodingRequest request = new()
        {
            OutputPath = directory.FilePath("candidate.mp4"), Width = 32, Height = 18,
            FrameRate = FrameRate.Fps30, TotalFrames = 30, AudioPath = audio, AudioInputFormat = "mov",
        };

        // The missing executable is reached only after Validate accepts the restricted format.
        await Assert.ThrowsAsync<IOException>(() => new FfmpegMediaEncoder(directory.FilePath("must-not-run"))
            .StartAsync(request));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.encoding.mp4"));
    }
}
