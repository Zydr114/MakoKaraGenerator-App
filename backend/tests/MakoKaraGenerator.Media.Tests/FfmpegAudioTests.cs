using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>
/// 与真实 FFmpeg 的集成测试：自己写 WAV → 探测 → 解码 → 峰值聚合。
/// </summary>
/// <remarks>
/// <para>
/// 本机 FFmpeg / FFprobe 9.0.2 属于既定环境（docs/development.md §22.1），
/// 因此这里直接要求它们存在：缺失时测试会失败并指出原因，
/// 而不是悄悄跳过——波形是这个功能的核心，不能"没测也当通过"。
/// </para>
/// <para>
/// 测试音频由 <see cref="WavWriter"/> 直接生成，振幅分段已知（0.9 / 0.30 / 0.70 / 0.05），
/// 所以"读回来的峰值对不对"有确定的期望值。
/// </para>
/// </remarks>
public sealed class FfmpegAudioTests : IDisposable
{
    private const int SampleRate = 44_100;
    private const double DurationSeconds = 5d;

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task 探测_WAV_得到采样率声道与时长()
    {
        string path = CreateTestWav();
        FfmpegMediaProbe probe = new();

        AudioStreamInfo? info = await probe.ProbeAsync(path);

        Assert.NotNull(info);
        Assert.Equal(SampleRate, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal("pcm_s16le", info.CodecName);
        Assert.Contains("wav", info.ContainerName, StringComparison.Ordinal);
        Assert.InRange(info.DurationUs, 4_990_000, 5_010_000);
    }

    [Fact]
    public async Task 探测不存在的文件会给出可读错误()
    {
        FfmpegMediaProbe probe = new();
        string missing = _directory.FilePath("并不存在.wav");

        IOException exception = await Assert.ThrowsAsync<IOException>(() => probe.ProbeAsync(missing));

        Assert.Contains("ffprobe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解码出的采样数与时长一致()
    {
        string path = CreateTestWav();
        FfmpegPcmReader reader = new();

        List<float> collected = [];

        long total = await reader.ReadMonoSamplesAsync(
            path,
            samples =>
            {
                collected.AddRange(samples.ToArray());
                return ValueTask.CompletedTask;
            });

        Assert.Equal((long)(DurationSeconds * SampleRate), total);
        Assert.Equal(total, collected.Count);
    }

    [Fact]
    public async Task 解码后的采样携带原始振幅()
    {
        string path = CreateTestWav();
        FfmpegPcmReader reader = new();

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;

        await reader.ReadMonoSamplesAsync(
            path,
            samples =>
            {
                foreach (float sample in samples.Span)
                {
                    min = Math.Min(min, sample);
                    max = Math.Max(max, sample);
                }

                return ValueTask.CompletedTask;
            });

        // 最大段振幅 0.9，最小段 0.05。
        Assert.InRange(max, 0.85f, 0.95f);
        Assert.InRange(min, -0.95f, -0.85f);
    }

    [Fact]
    public async Task 端到端加载波形并还原每段振幅()
    {
        string path = CreateTestWav();

        LoadedWaveform? loaded = await AudioWaveformLoader.CreateDefault().LoadAsync(path);

        Assert.NotNull(loaded);

        WaveformPeakPyramid peaks = loaded.Peaks;

        Assert.Equal(SampleRate, peaks.SampleRate);
        Assert.Equal((long)(DurationSeconds * SampleRate), peaks.SampleCount);

        // 0–1 s 振幅 0.9；1–2 s 振幅 0.30；2–4 s 振幅 0.70；4–5 s 振幅 0.05。
        AssertSegmentAmplitude(peaks, 0.5d, 0.9f);
        AssertSegmentAmplitude(peaks, 1.5d, 0.30f);
        AssertSegmentAmplitude(peaks, 3.0d, 0.70f);
        AssertSegmentAmplitude(peaks, 4.5d, 0.05f);
    }

    [Fact]
    public async Task 峰值金字塔在任意缩放下都不需要读原始采样()
    {
        string path = CreateTestWav();

        LoadedWaveform? loaded = await AudioWaveformLoader.CreateDefault().LoadAsync(path);

        Assert.NotNull(loaded);

        WaveformPeakPyramid peaks = loaded.Peaks;

        Assert.True(peaks.LevelCount > 1, "至少要有两个层级，否则缩放到整曲时会退化成逐桶遍历。");

        // 整曲铺满 1000 像素：每像素的采样跨度约 220，应当选到第 1 层以上。
        long samplesPerPixel = peaks.SampleCount / 1000;

        Assert.True(
            peaks.SelectLevel(samplesPerPixel) >= 1,
            $"每像素 {samplesPerPixel} 个采样时选到了第 {peaks.SelectLevel(samplesPerPixel)} 层，缩放会变慢。");
    }

    private static void AssertSegmentAmplitude(WaveformPeakPyramid peaks, double seconds, float expected)
    {
        long center = (long)(seconds * peaks.SampleRate);
        long half = peaks.SampleRate / 40; // ±25 ms

        PeakRange range = peaks.GetPeak(center - half, center + half);

        Assert.InRange(range.Amplitude, expected - 0.05f, expected + 0.05f);
    }

    private string CreateTestWav()
    {
        string path = _directory.FilePath("test-tone.wav");

        float[] samples = WavWriter.BuildSegmentedTone(
            SampleRate,
            (1d, 0.9f),
            (1d, 0.30f),
            (2d, 0.70f),
            (1d, 0.05f));

        WavWriter.Write(path, samples, SampleRate);

        return path;
    }
}
