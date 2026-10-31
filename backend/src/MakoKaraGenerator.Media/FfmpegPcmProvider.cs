using System.Buffers.Binary;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Structs;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 把 FFmpeg 解出的 PCM 管道直接喂给播放库的数据源。
/// </summary>
/// <remarks>
/// <para>
/// 播放库自带的 <c>StreamDataProvider</c> 要求输入流<b>可定位</b>（构造时会无条件把
/// Position 设回 0，播放开始时还会再来一次），而 FFmpeg 的 stdout 管道不可定位。
/// 因此这里实现库提供的扩展点 <see cref="ISoundDataProvider" />：
/// 顺序读取、不做定位，长度用媒体探测得到的时长换算。
/// </para>
/// <para>
/// 本项目的跳转是"重建解码会话"（用 <c>-ss</c> 重新开始解码），
/// 所以 <see cref="CanSeek" /> 恒为 <see langword="false" /> 是事实而不是限制。
/// </para>
/// </remarks>
internal sealed class FfmpegPcmProvider : ISoundDataProvider
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly byte[] _byteBuffer = new byte[64 * 1024];

    private int _positionSamples;
    private bool _endOfStreamRaised;
    private bool _disposed;

    /// <summary>创建一个数据源。</summary>
    /// <param name="stream">FFmpeg 的 stdout（f32le）。</param>
    /// <param name="format">采样格式（单声道 f32）。</param>
    /// <param name="totalSamples">预期总采样数；未知时用 0。</param>
    public FfmpegPcmProvider(Stream stream, AudioFormat format, long totalSamples)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _format = format;
        Length = totalSamples > int.MaxValue ? int.MaxValue : (int)Math.Max(0L, totalSamples);
        FormatInfo = new SoundFormatInfo
        {
            FormatName = "f32le",
            FormatIdentifier = "f32le",
            CodecName = "pcm_f32le",
            SampleRate = format.SampleRate,
            ChannelCount = format.Channels,
            BitsPerSample = 32,
            IsLossless = true,
        };
    }

    /// <inheritdoc/>
    public int Position => _positionSamples;

    /// <inheritdoc/>
    public int Length { get; }

    /// <inheritdoc/>
    public bool CanSeek => false;

    /// <inheritdoc/>
    public SampleFormat SampleFormat => SampleFormat.F32;

    /// <inheritdoc/>
    public int SampleRate => _format.SampleRate;

    /// <inheritdoc/>
    public bool IsDisposed => _disposed;

    /// <inheritdoc/>
    public SoundFormatInfo FormatInfo { get; }

    /// <inheritdoc/>
    public event EventHandler<EventArgs>? EndOfStreamReached;

    /// <inheritdoc/>
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    /// <inheritdoc/>
    public int ReadBytes(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        int wantedBytes = Math.Min(buffer.Length * sizeof(float), _byteBuffer.Length);
        int filledBytes = 0;

        // 管道一次读不满是常态，这里循环到填满或真的读完。
        while (filledBytes < wantedBytes)
        {
            int read = _stream.Read(_byteBuffer, filledBytes, wantedBytes - filledBytes);

            if (read <= 0)
            {
                break;
            }

            filledBytes += read;
        }

        int sampleCount = filledBytes / sizeof(float);

        for (int i = 0; i < sampleCount; i++)
        {
            buffer[i] = BinaryPrimitives.ReadSingleLittleEndian(
                _byteBuffer.AsSpan(i * sizeof(float), sizeof(float)));
        }

        if (sampleCount > 0)
        {
            _positionSamples += sampleCount;
            PositionChanged?.Invoke(this, new PositionChangedEventArgs(_positionSamples));
        }

        if (sampleCount == 0 && !_endOfStreamRaised)
        {
            _endOfStreamRaised = true;
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
        }

        return sampleCount;
    }

    /// <inheritdoc/>
    public void Seek(int offset) => throw new NotSupportedException(
        "本数据源不支持定位；播放跳转通过重建解码会话实现。");

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _stream.Dispose();
    }
}
