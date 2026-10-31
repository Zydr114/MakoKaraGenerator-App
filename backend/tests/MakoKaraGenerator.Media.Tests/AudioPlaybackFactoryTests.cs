using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Media.Tests;

/// <summary>
/// <see cref="AudioPlaybackFactory" /> 与 SoundFlow 后端的测试。
/// </summary>
/// <remarks>
/// 本机（有音频设备）会真正跑一遍播放；没有设备或没有 FFmpeg 的环境会自动跳过，
/// 因为这些测试要证明的是"有设备时能出声、没设备时不炸"，而不是要求每台机器都有声卡。
/// </remarks>
public sealed class AudioPlaybackFactoryTests
{
    [Fact]
    public void 工厂永远返回可用的播放对象()
    {
        AudioPlaybackCreation creation = AudioPlaybackFactory.CreateDefault();

        Assert.NotNull(creation.Playback);

        if (creation.IsFallback)
        {
            Assert.False(creation.Playback.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(creation.FallbackReason));
        }
        else
        {
            Assert.True(creation.Playback.IsAvailable);
            Assert.Null(creation.FallbackReason);
        }

        creation.Playback.Dispose();
    }

    [Fact]
    public async Task SoundFlow后端能加载并播放真实音频()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "tone.wav");
        WavWriter.Write(path, WavWriter.BuildSegmentedTone(48_000, (1.0, 0.6f), (1.0, 0.2f)), 48_000);

        FfmpegMediaProbe probe = new();
        AudioStreamInfo? info = await probe.ProbeAsync(path);

        if (info is null)
        {
            return;     // 本机没有 FFmpeg：跳过
        }

        SoundFlowAudioPlayback? playback = SoundFlowAudioPlayback.TryCreate(
            sampleRate: 48_000,
            channels: 1,
            out string? reason);

        if (playback is null)
        {
            // 没有音频输出设备（CI / 容器）：静音回退另有测试覆盖
            Assert.False(string.IsNullOrWhiteSpace(reason));
            return;
        }

        using (playback)
        {
            Assert.True(playback.IsAvailable);
            Assert.Contains("SoundFlow", playback.BackendName, StringComparison.Ordinal);

            await playback.LoadAsync(path, info.DurationUs);

            Assert.Equal(AudioPlaybackState.Stopped, playback.State);
            Assert.Equal(0L, playback.PositionUs);

            playback.Play();
            Assert.Equal(AudioPlaybackState.Playing, playback.State);

            // 位置由播放器给出，给音频线程一点时间再读。
            await Task.Delay(400);
            long playingPosition = playback.PositionUs;
            Assert.True(playingPosition > 0L, $"播放中位置应推进，实际为 {playingPosition} 微秒。");

            playback.Pause();
            Assert.Equal(AudioPlaybackState.Paused, playback.State);

            await Task.Delay(120);
            long pausedPosition = playback.PositionUs;
            await Task.Delay(200);
            Assert.Equal(pausedPosition, playback.PositionUs);

            playback.Seek(1_500_000L);
            Assert.InRange(playback.PositionUs, 1_400_000L, 1_600_000L);

            playback.Stop();
            Assert.Equal(AudioPlaybackState.Stopped, playback.State);
            Assert.Equal(0L, playback.PositionUs);
        }
    }

    [Fact]
    public void 音量设置会传递到底层且被钳制()
    {
        SoundFlowAudioPlayback? playback = SoundFlowAudioPlayback.TryCreate(
            sampleRate: 48_000,
            channels: 1,
            out _);

        if (playback is null)
        {
            return;
        }

        using (playback)
        {
            playback.Volume = 0.25d;
            Assert.Equal(0.25d, playback.Volume);

            playback.Volume = 5d;
            Assert.Equal(1d, playback.Volume);
        }
    }
}
