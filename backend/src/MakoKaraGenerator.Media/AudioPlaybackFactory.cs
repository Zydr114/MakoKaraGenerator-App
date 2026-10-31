namespace MakoKaraGenerator.Media;

/// <summary>
/// 播放后端的创建结果。
/// </summary>
/// <param name="Playback">播放实现（永远是可用对象：拿不到真后端时是静音回退）。</param>
/// <param name="FallbackReason">为什么退回了静音实现；没有退回时为 <see langword="null" />。</param>
public sealed record AudioPlaybackCreation(IAudioPlayback Playback, string? FallbackReason)
{
    /// <summary>是否发生了回退（即当前不会出声）。</summary>
    public bool IsFallback => FallbackReason is not null;
}

/// <summary>
/// 创建播放后端。
/// </summary>
/// <remarks>
/// 界面只调用这里，不认识任何具体播放器（docs/roadmap.md §8 的 <c>MEDIA-04</c>）。
/// 拿不到音频设备时返回静音实现而不是抛异常：没有声音时编辑器依然要能用，
/// 因为打轴依赖的是"播放头在动"。
/// </remarks>
public static class AudioPlaybackFactory
{
    /// <summary>默认的播放采样率。</summary>
    public const int DefaultSampleRate = 48_000;

    /// <summary>默认的声道数（本项目的解码统一为单声道）。</summary>
    public const int DefaultChannels = 1;

    /// <summary>创建一个播放后端，失败时回退到静音实现。</summary>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="channels">声道数。</param>
    /// <returns>创建结果。</returns>
    public static AudioPlaybackCreation CreateDefault(
        int sampleRate = DefaultSampleRate,
        int channels = DefaultChannels)
    {
        if (sampleRate <= 0)
        {
            sampleRate = DefaultSampleRate;
        }

        if (channels <= 0)
        {
            channels = DefaultChannels;
        }

        SoundFlowAudioPlayback? playback = SoundFlowAudioPlayback.TryCreate(
            sampleRate,
            channels,
            out string? reason);

        return playback is not null
            ? new AudioPlaybackCreation(playback, null)
            : new AudioPlaybackCreation(
                new SilentAudioPlayback(),
                string.IsNullOrWhiteSpace(reason) ? "本机没有可用的音频输出设备。" : reason);
    }
}
