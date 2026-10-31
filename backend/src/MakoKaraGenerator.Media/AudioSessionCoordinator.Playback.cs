using System.Collections.Concurrent;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Media;

public sealed partial class AudioSessionCoordinator
{
    private readonly ConcurrentQueue<PlaybackEnd> _playbackEnds = new();
    private EventHandler? _playbackEndedHandler;
    private long _playbackRun;
    private bool _atPlaybackEnd;

    /// <summary>轮询或播放命令已经在调用方上下文完成状态／位置变更。</summary>
    public event EventHandler? PlaybackChanged;
    /// <summary>协调后的真实播放状态。</summary>
    public AudioPlaybackState PlaybackState { get; private set; }
    /// <summary>协调后的播放头，单位为整数微秒。</summary>
    public long PositionUs { get; private set; }

    /// <summary>消费后台结束通知并轮询实际后端；有效循环到达出点后继续播放。</summary>
    /// <param name="loop">可选循环范围；运行时限制在当前音频内，不修改源选区。</param>
    public void PollPlayback(TimeRange? loop = null)
    {
        IAudioPlayback? playback = Playback;
        if (playback is null) return;
        if (playback is SilentAudioPlayback silent) silent.Tick();
        bool ended = false;
        while (_playbackEnds.TryDequeue(out PlaybackEnd completion))
            ended |= ReferenceEquals(completion.Playback, playback) && completion.Session == Generation
                && completion.Run == _playbackRun;
        AudioPlaybackState actual = playback.State;
        long duration = Waveform!.DurationUs;
        long position = Math.Clamp(playback.PositionUs, 0, duration);
        bool wasPlaying = PlaybackState == AudioPlaybackState.Playing;
        bool reachedEnd = position >= duration && duration > 0;
        bool naturalEnd = wasPlaying && (reachedEnd || ended && actual == AudioPlaybackState.Stopped);
        if (wasPlaying && TryLoop(loop, duration, out TimeRange region)
            && (naturalEnd || position >= region.EndUs))
        {
            Interlocked.Increment(ref _playbackRun);
            _atPlaybackEnd = false;
            playback.Seek(region.StartUs);
            if (playback.State != AudioPlaybackState.Playing) playback.Play();
            PublishPlayback(playback.State, region.StartUs);
            return;
        }
        if (naturalEnd)
        {
            // 有些后端在位置到末尾时还未发事件，仍须明确停止并保留末尾播放头。
            if (actual != AudioPlaybackState.Stopped) { playback.Stop(); playback.Seek(duration); }
            _atPlaybackEnd = true;
            PublishPlayback(AudioPlaybackState.Stopped, duration);
            return;
        }
        PublishPlayback(actual, _atPlaybackEnd && actual == AudioPlaybackState.Stopped ? duration : position);
    }

    /// <summary>开始或继续；EOF从头播放，有有效循环时从循环入点重播。</summary>
    /// <param name="positionUs">调用方的定位播放头。</param>
    /// <param name="loop">可选循环范围。</param>
    public void StartPlayback(long positionUs, TimeRange? loop = null)
    {
        bool wasPlaying = PlaybackState == AudioPlaybackState.Playing;
        PollPlayback(loop);
        if (Playback is not { } playback || PlaybackState == AudioPlaybackState.Playing) return;
        long duration = Waveform!.DurationUs;
        long target = wasPlaying && PositionUs >= duration ? duration : Math.Clamp(positionUs, 0, duration);
        if (TryLoop(loop, duration, out TimeRange region) && target >= region.EndUs) target = region.StartUs;
        else if (target >= duration) target = 0;
        Interlocked.Increment(ref _playbackRun);
        _atPlaybackEnd = false;
        if (PositionUs != target) playback.Seek(target);
        playback.Play();
        PublishPlayback(playback.State, target);
    }

    /// <summary>幂等暂停；停止或已暂停时不开始播放。</summary>
    public void PausePlayback()
    {
        PollPlayback();
        if (Playback is not { } playback || PlaybackState != AudioPlaybackState.Playing) return;
        Interlocked.Increment(ref _playbackRun);
        playback.Pause();
        PublishPlayback(playback.State, Math.Clamp(playback.PositionUs, 0, Waveform!.DurationUs));
    }

    /// <summary>停止并归零。</summary>
    public void StopPlayback()
    {
        Interlocked.Increment(ref _playbackRun);
        _atPlaybackEnd = false;
        Playback?.Stop();
        PublishPlayback(AudioPlaybackState.Stopped, 0);
    }

    /// <summary>定位并使旧播放段的排队结束通知失效；保持当前播放或暂停状态。</summary>
    public void SeekPlayback(long positionUs)
    {
        if (Playback is not { } playback) return;
        long target = Math.Clamp(positionUs, 0, Waveform!.DurationUs);
        Interlocked.Increment(ref _playbackRun);
        _atPlaybackEnd = false;
        playback.Seek(target);
        PublishPlayback(playback.State, target);
    }

    private void AttachPlayback(IAudioPlayback playback)
    {
        long generation = Generation;
        _atPlaybackEnd = false;
        PlaybackState = playback.State; PositionUs = 0;
        Interlocked.Increment(ref _playbackRun);
        _playbackEndedHandler = (_, _) => _playbackEnds.Enqueue(new(playback, generation, Volatile.Read(ref _playbackRun)));
        playback.PlaybackEnded += _playbackEndedHandler;
    }

    private void DetachPlayback(IAudioPlayback? playback)
    {
        if (playback is not null && _playbackEndedHandler is not null) playback.PlaybackEnded -= _playbackEndedHandler;
        _playbackEndedHandler = null;
        Interlocked.Increment(ref _playbackRun);
        _playbackEnds.Clear();
        _atPlaybackEnd = false;
        PlaybackState = AudioPlaybackState.Stopped; PositionUs = 0;
    }

    private void PublishPlayback(AudioPlaybackState state, long position)
    {
        if (PlaybackState == state && PositionUs == position) return;
        PlaybackState = state; PositionUs = position;
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryLoop(TimeRange? loop, long duration, out TimeRange region)
    {
        if (loop is { IsValid: true } requested && requested.StartUs >= 0 && requested.StartUs < duration)
        { region = new(requested.StartUs, Math.Min(requested.EndUs, duration)); return region.IsValid; }
        region = default;
        return false;
    }

    private readonly record struct PlaybackEnd(IAudioPlayback Playback, long Session, long Run);
}
