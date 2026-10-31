namespace MakoKaraGenerator.Media;

/// <summary>
/// 不发声的播放实现：位置按墙上时间推进。
/// </summary>
/// <remarks>
/// <para>
/// 用途有两个：机器上没有可用音频设备时界面照常可用；以及在没有音频输出的环境
/// （CI、容器）里跑测试与冒烟验证。
/// </para>
/// <para>
/// 刻意不复用"播放器不可用就直接禁用播放"的做法：打轴时"播放头在动"本身就是必需的，
/// 声音是补充而不是前提。
/// </para>
/// </remarks>
public sealed class SilentAudioPlayback : IAudioPlayback
{
    private readonly TimeProvider _timeProvider;

    private string? _filePath;
    private long _durationUs;
    private long _positionUs;
    private long _playStartedAtUs;
    private AudioPlaybackState _state = AudioPlaybackState.Stopped;
    private double _volume = 1d;
    private bool _disposed;

    /// <summary>创建一个静音播放实现。</summary>
    /// <param name="timeProvider">时间来源；为 <see langword="null" /> 时使用系统时钟。</param>
    public SilentAudioPlayback(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public AudioPlaybackState State => _state;

    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public string BackendName => "静音（无音频输出）";

    /// <inheritdoc/>
    public long DurationUs => _durationUs;

    /// <inheritdoc/>
    public long PositionUs => _state == AudioPlaybackState.Playing
        ? Math.Min(_durationUs, _positionUs + (NowUs() - _playStartedAtUs))
        : _positionUs;

    /// <inheritdoc/>
    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0d, 1d);
    }

    /// <inheritdoc/>
    public event EventHandler? PlaybackEnded;

    /// <inheritdoc/>
    public Task LoadAsync(string filePath, long durationUs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        _filePath = filePath;
        _durationUs = Math.Max(0L, durationUs);
        _positionUs = 0L;
        _state = AudioPlaybackState.Stopped;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_filePath is null || _state == AudioPlaybackState.Playing)
        {
            return;
        }

        // 播到末尾再按播放：从头开始，符合大多数播放器的习惯。
        if (_durationUs > 0 && _positionUs >= _durationUs)
        {
            _positionUs = 0L;
        }

        _playStartedAtUs = NowUs();
        _state = AudioPlaybackState.Playing;
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != AudioPlaybackState.Playing)
        {
            return;
        }

        _positionUs = PositionUs;
        _state = AudioPlaybackState.Paused;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _positionUs = 0L;
        _state = AudioPlaybackState.Stopped;
    }

    /// <inheritdoc/>
    public void Seek(long positionUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = Math.Max(0L, positionUs);

        if (_durationUs > 0)
        {
            target = Math.Min(target, _durationUs);
        }

        _positionUs = target;

        if (_state == AudioPlaybackState.Playing)
        {
            _playStartedAtUs = NowUs();
        }
    }

    /// <summary>按墙上时间推进位置，并在到达末尾时报告播放结束。</summary>
    /// <remarks>由界面定时器调用；静音实现没有自己的播放线程。</remarks>
    public void Tick()
    {
        if (_state != AudioPlaybackState.Playing)
        {
            return;
        }

        long positionUs = PositionUs;

        if (_durationUs > 0 && positionUs >= _durationUs)
        {
            _positionUs = _durationUs;
            _state = AudioPlaybackState.Stopped;
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _state = AudioPlaybackState.Stopped;
    }

    /// <summary>取当前时刻的微秒数。</summary>
    private long NowUs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1_000L;
}
