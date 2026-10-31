using MakoKaraGenerator.Core.Time;
using System.Collections.Concurrent;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 基于 SoundFlow（miniaudio）的播放实现，数据来自本项目的 FFmpeg PCM 管线。
/// </summary>
/// <remarks>
/// <para>
/// 见 docs/decisions.md ADR-0019。要点：
/// </para>
/// <list type="bullet">
///   <item><description>
///     音频数据由我们自己的 FFmpeg 解码（单声道 f32le，与波形解码参数一致），
///     再通过自研的数据源喂给 SoundFlow，
///     因此"能播什么格式"由 FFmpeg 决定，不由播放库决定。
///   </description></item>
///   <item><description>
///     FFmpeg 的解码管道不可定位，所以<b>跳转 = 重建会话</b>：
///     用 <c>-ss</c> 重新启动解码进程并重建播放器。代价是几十到几百毫秒，
///     换来的是不必把整首歌的 PCM 缓存到磁盘。
///   </description></item>
///   <item><description>
///     位置 = 本会话的起点 + 播放器在本次流内的时间。
///   </description></item>
/// </list>
/// </remarks>
public sealed class SoundFlowAudioPlayback : IAudioPlayback
{
    /// <summary>默认的 FFmpeg 可执行文件名。</summary>
    public const string DefaultFfmpegExecutableName = "ffmpeg";

    private readonly string _ffmpegPath;
    private readonly MiniAudioEngine _engine;
    private readonly AudioPlaybackDevice _device;
    private readonly AudioFormat _format;

    private FfmpegPcmSession? _session;
    private FfmpegPcmProvider? _provider;
    private volatile SoundPlayer? _player;
    private readonly ConcurrentQueue<SoundPlayer> _endedPlayers = new();
    private string? _filePath;
    private long _durationUs;
    private long _sessionStartUs;
    private AudioPlaybackState _state = AudioPlaybackState.Stopped;
    private double _volume = 1d;
    private volatile bool _disposed;
    private bool _atEnd;

    private SoundFlowAudioPlayback(
        string ffmpegPath,
        MiniAudioEngine engine,
        AudioPlaybackDevice device,
        AudioFormat format)
    {
        _ffmpegPath = ffmpegPath;
        _engine = engine;
        _device = device;
        _format = format;
    }

    /// <inheritdoc/>
    public AudioPlaybackState State
    {
        get { ApplyPlaybackEnd(); return _state; }
    }

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public string BackendName => $"SoundFlow / miniaudio（{_engine.ActiveBackend}）";

    /// <inheritdoc/>
    public long DurationUs => _durationUs;

    /// <inheritdoc/>
    public long PositionUs
    {
        get
        {
            ApplyPlaybackEnd();
            if (_atEnd) return _durationUs;
            SoundPlayer? player = _player;

            if (player is null)
            {
                return _sessionStartUs;
            }

            long offsetUs = (long)(player.Time * TimeUs.PerSecond);
            long positionUs = _sessionStartUs + Math.Max(0L, offsetUs);

            return _durationUs > 0L ? Math.Min(positionUs, _durationUs) : positionUs;
        }
    }

    /// <inheritdoc/>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0d, 1d);

            if (_player is not null)
            {
                _player.Volume = (float)_volume;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler? PlaybackEnded;

    /// <summary>
    /// 尝试创建一个可用的播放后端。
    /// </summary>
    /// <param name="sampleRate">播放采样率（与解码保持一致）。</param>
    /// <param name="channels">声道数（本项目解码为单声道）。</param>
    /// <param name="ffmpegPath">FFmpeg 可执行文件。</param>
    /// <param name="reason">创建失败的原因；成功时为 <see langword="null" />。</param>
    /// <returns>播放后端；本机没有可用音频设备时为 <see langword="null" />。</returns>
    public static SoundFlowAudioPlayback? TryCreate(
        int sampleRate,
        int channels,
        out string? reason,
        string ffmpegPath = DefaultFfmpegExecutableName)
    {
        reason = null;

        MiniAudioEngine? engine = null;

        try
        {
            AudioFormat format = new()
            {
                Format = SampleFormat.F32,
                Channels = channels,
                SampleRate = sampleRate,
            };

            // 明确指定后端优先级：miniaudio 的默认探测在本机会选中 OSS，
            // 而现代 Linux 桌面（PipeWire）应该走 PulseAudio 兼容层或 ALSA。
            engine = new MiniAudioEngine(
            [
                MiniAudioBackend.PulseAudio,
                MiniAudioBackend.Alsa,
                MiniAudioBackend.Jack,
            ]);
            AudioPlaybackDevice device = engine.InitializePlaybackDevice(null, format, null);

            // 只是 Initialize 还不够：设备必须 Start 起来才会开始拉取数据，
            // 否则播放器状态是 Playing、位置却永远停在 0（本喵就踩过这个）。
            device.Start();

            return new SoundFlowAudioPlayback(ffmpegPath, engine, device, format);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException
            or DllNotFoundException
            or TypeInitializationException
            or ArgumentException)
        {
            engine?.Dispose();
            reason = exception.Message;
            return null;
        }
    }

    /// <inheritdoc/>
    public Task LoadAsync(string filePath, long durationUs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        _filePath = filePath;
        _durationUs = Math.Max(0L, durationUs);
        CloseSession();
        _sessionStartUs = 0L;
        _state = AudioPlaybackState.Stopped;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyPlaybackEnd();

        if (_filePath is null)
        {
            return;
        }

        if (!_device.IsRunning)
        {
            _device.Start();
        }

        if (_player is null || _atEnd)
        {
            // 完成段需从头新建；手动Stop已经释放不可定位的旧流。
            OpenSession(0);
        }

        _player?.Play();
        _state = AudioPlaybackState.Playing;
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyPlaybackEnd();

        if (_state != AudioPlaybackState.Playing)
        {
            return;
        }

        _player?.Pause();
        _state = AudioPlaybackState.Paused;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // FFmpeg流不可定位；SoundPlayer.Stop的Seek(0)无法复位该流。
        CloseSession();
        _state = AudioPlaybackState.Stopped;
        _sessionStartUs = 0L;
        _atEnd = false;
        _endedPlayers.Clear();
    }

    /// <inheritdoc/>
    public void Seek(long positionUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyPlaybackEnd();

        if (_filePath is null)
        {
            return;
        }

        long target = Math.Max(0L, positionUs);

        if (_durationUs > 0L)
        {
            target = Math.Min(target, _durationUs);
        }

        bool resume = _state == AudioPlaybackState.Playing;
        OpenSession(target);

        if (resume)
        {
            _player?.Play();
            _state = AudioPlaybackState.Playing;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseSession();
        _device.Dispose();
        _engine.Dispose();
    }

    /// <summary>重建解码会话与播放器。</summary>
    private void OpenSession(long startUs)
    {
        CloseSession();
        _atEnd = false;

        if (_filePath is null)
        {
            return;
        }

        _session = FfmpegPcmSession.Start(_ffmpegPath, _filePath, startUs);
        _sessionStartUs = startUs;

        // 库自带的 StreamDataProvider 要求可定位流，而解码管道不可定位，
        // 因此用库提供的扩展点直接喂 PCM（见 FfmpegPcmProvider）。
        _provider = new FfmpegPcmProvider(_session.Output, _format, ExpectedPcmSampleCount(startUs));
        _player = new SoundPlayer(_engine, _format, _provider)
        {
            Name = "MakoKaraGenerator 预览",
            Volume = (float)_volume,
        };

        _player.PlaybackEnded += OnPlaybackEnded;
        _device.MasterMixer.AddComponent(_player);
    }

    /// <summary>估算本次会话还能产出多少采样（用于让播放器知道总长度）。</summary>
    /// <param name="startUs">本次会话的起点。</param>
    /// <returns>采样数；时长未知时为 0。</returns>
    private long ExpectedPcmSampleCount(long startUs)
    {
        if (_durationUs <= 0L)
        {
            return 0L;
        }

        long remainingUs = Math.Max(0L, _durationUs - startUs);
        return remainingUs * _format.SampleRate / TimeUs.PerSecond;
    }

    /// <summary>关闭当前会话（播放器、数据源、解码进程）。</summary>
    private void CloseSession()
    {
        if (_player is not null)
        {
            SoundPlayer previous = _player;
            _player = null;
            previous.PlaybackEnded -= OnPlaybackEnded;
            previous.Stop();
            _device.MasterMixer.RemoveComponent(previous);
            previous.Dispose();
        }
        _endedPlayers.Clear();

        _provider?.Dispose();
        _provider = null;
        _session?.Dispose();
        _session = null;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        // 音频线程只发送完成信号；旧解码段绝不能改写新段的状态或起点。
        if (_disposed || sender is not SoundPlayer completed) return;
        _endedPlayers.Enqueue(completed);
        if (!ReferenceEquals(completed, _player)) return;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPlaybackEnd()
    {
        while (_endedPlayers.TryDequeue(out SoundPlayer? completed))
        {
            if (_disposed || !ReferenceEquals(completed, _player)
                || _state == AudioPlaybackState.Stopped) continue;
            _state = AudioPlaybackState.Stopped;
            _sessionStartUs = _durationUs;
            _atEnd = true;
        }
    }
}
