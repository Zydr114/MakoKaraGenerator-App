using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using CliWrap;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 用 FFmpeg 把媒体文件解码为单声道 32 位浮点采样（docs/roadmap.md §8 的 <c>MEDIA-02</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 调用形态（已在本机 FFmpeg 9.0.2 上实测，5 秒 44.1 kHz 得到 882000 字节 = 5×44100×4）：
/// </para>
/// <code>
/// ffmpeg -v error -i &lt;file&gt; -vn -ac 1 -f f32le -acodec pcm_f32le -
/// </code>
/// <para>
/// 通过标准输出流式读取，并且在读取过程中就地转换，
/// 因此不会为了解码把整首歌留出两份内存（见 <see cref="WaveformPeakBuilder"/> 的说明）。
/// </para>
/// </remarks>
public sealed class FfmpegPcmReader : IResampledPcmSampleReader
{
    /// <summary>默认可执行文件名。</summary>
    public const string DefaultExecutableName = "ffmpeg";

    private const int ReadBufferBytes = 64 * 1024;
    private const int RetainedErrorCharacters = 32 * 1024;

    private readonly string _executablePath;

    /// <summary>创建一个解码器。</summary>
    /// <param name="executablePath">FFmpeg 可执行文件路径；省略时使用 PATH 中的 <c>ffmpeg</c>。</param>
    public FfmpegPcmReader(string executablePath = DefaultExecutableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 回调收到的 <see cref="ReadOnlyMemory{T}"/> 指向内部复用缓冲区，
    /// <b>仅在回调执行期间有效</b>；调用方如需保留必须自行复制。
    /// </remarks>
    public Task<long> ReadMonoSamplesAsync(
        string filePath,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples,
        CancellationToken cancellationToken = default)
        => ReadMonoCoreAsync(filePath, null, null, onSamples, cancellationToken);

    internal Task<long> ReadSelfContainedSamplesAsync(string filePath, SelfContainedAudioFormat format,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
        => ReadMonoCoreAsync(filePath, null, SelfContainedAudioInput.Demuxer(format), onSamples, cancellationToken, selfContained: true);

    /// <inheritdoc/>
    public Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return ReadMonoCoreAsync(filePath, sampleRate, null, onSamples, cancellationToken);
    }

    /// <summary>Strictly decode a trusted self-contained WAV, MP3, FLAC, AAC, MOV or Ogg with an explicit demuxer and file/pipe protocol policy.</summary>
    /// <remarks>The default interface retains container detection; sample budgets remain the managed task caller's responsibility.</remarks>
    public Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, string forcedInputFormat,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (forcedInputFormat is not ("wav" or "mp3" or "flac" or "aac" or "mov" or "ogg"))
            throw new ArgumentException("Restricted audio input format must be wav, mp3, flac, aac, mov or ogg.", nameof(forcedInputFormat));
        return ReadMonoCoreAsync(filePath, sampleRate, forcedInputFormat, onSamples, cancellationToken,
            selfContained: true, strictDecode: true);
    }

    private async Task<long> ReadMonoCoreAsync(string filePath, int? sampleRate, string? forcedInputFormat,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken, bool selfContained = false,
        bool strictDecode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(onSamples);

        byte[] byteBuffer = new byte[ReadBufferBytes];
        float[] floatBuffer = new float[ReadBufferBytes / sizeof(float)];
        string standardError = string.Empty;

        int bufferedBytes = 0;
        long totalSamples = 0;

        PipeTarget target = PipeTarget.Create(async (stream, token) =>
        {
            while (true)
            {
                int read = await stream.ReadAsync(byteBuffer.AsMemory(bufferedBytes), token).ConfigureAwait(false);

                if (read <= 0)
                {
                    if (bufferedBytes != 0) throw new IOException("PCM 流末尾存在不完整的 float 采样。");
                    return;
                }

                bufferedBytes += read;

                int wholeFloats = bufferedBytes / sizeof(float);

                if (wholeFloats == 0)
                {
                    continue;
                }

                for (int i = 0; i < wholeFloats; i++)
                {
                    floatBuffer[i] = BinaryPrimitives.ReadSingleLittleEndian(
                        byteBuffer.AsSpan(i * sizeof(float), sizeof(float)));
                }

                await onSamples(floatBuffer.AsMemory(0, wholeFloats)).ConfigureAwait(false);
                totalSamples += wholeFloats;

                // 把跨块残留的不足 4 字节挪到缓冲区开头，下一轮补齐。
                int consumed = wholeFloats * sizeof(float);
                int remaining = bufferedBytes - consumed;

                if (remaining > 0)
                {
                    Buffer.BlockCopy(byteBuffer, consumed, byteBuffer, 0, remaining);
                }

                bufferedBytes = remaining;
            }
        });

        CommandResult result;

        List<string> arguments = ["-v", "error"];
        if (strictDecode) arguments.AddRange(["-xerror", "-err_detect", "explode"]);
        if (selfContained)
        {
            SelfContainedAudioInput.AddPolicy(arguments);
            arguments.AddRange(["-f", forcedInputFormat!]);
            if (forcedInputFormat == "mov")
            {
                SelfContainedMovInputValidator.ValidateFile(filePath, required: true, cancellationToken);
                SelfContainedAudioInput.AddMovPolicy(arguments);
            }
        }
        else if (forcedInputFormat is not null)
            arguments.AddRange(["-protocol_whitelist", "file,pipe", "-f", forcedInputFormat]);
        arguments.AddRange(["-i", filePath]);
        if (forcedInputFormat is not null) arguments.AddRange(["-map", "0:a:0"]);
        arguments.AddRange(["-vn", "-ac", "1"]);
        if (sampleRate is { } rate) arguments.AddRange(["-ar", rate.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-f", "f32le", "-acodec", "pcm_f32le", "-"]);

        try
        {
            result = await Cli.Wrap(_executablePath)
                .WithArguments(arguments)
                .WithStandardOutputPipe(target)
                .WithStandardErrorPipe(PipeTarget.Create(async (stream, token) =>
                {
                    using StreamReader reader = new(stream, Encoding.UTF8);
                    standardError = await BoundedTextReader.ReadToEndAsync(reader, RetainedErrorCharacters,
                        rejectOverflow: false, token).ConfigureAwait(false);
                }))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            throw new IOException(
                $"无法启动 {_executablePath}：{exception.Message}。请确认 FFmpeg 已安装并位于 PATH 中。",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new IOException(string.Create(
                CultureInfo.InvariantCulture,
                $"{_executablePath} 解码失败（退出码 {result.ExitCode}）：{standardError.Trim()}"));
        }

        return totalSamples;
    }
}

/// <summary>
/// 一次加载完成的波形数据。
/// </summary>
/// <param name="Info">探测得到的音频流信息。</param>
/// <param name="Peaks">峰值金字塔。</param>
public sealed record LoadedWaveform(AudioStreamInfo Info, WaveformPeakPyramid Peaks);

/// <summary>
/// 把"探测 + 解码 + 峰值聚合"合成一次调用。
/// </summary>
/// <remarks>
/// 这是界面层唯一需要接触的入口：ViewModel 与控件都不应该出现
/// <c>ffmpeg</c> / <c>ffprobe</c> 这类字符串（docs/roadmap.md §8 的设计要求：
/// "波形服务可替换，不允许把 FFmpeg 命令写入时间轴 ViewModel"）。
/// </remarks>
public sealed class AudioWaveformLoader
{
    private readonly IMediaProbe _probe;
    private readonly IPcmSampleReader _reader;
    private readonly Func<string, AudioStreamInfo, Func<ReadOnlyMemory<float>, ValueTask>, CancellationToken, Task<long>>? _preparedReader;
    private readonly int? _maximumBaseBuckets;

    /// <summary>创建一个加载器。</summary>
    /// <param name="probe">媒体探测器。</param>
    /// <param name="reader">PCM 采样读取器。</param>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/> 时抛出。</exception>
    public AudioWaveformLoader(IMediaProbe probe, IPcmSampleReader reader)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(reader);

        _probe = probe;
        _reader = reader;
    }

    /// <summary>用 FFmpeg 的默认实现创建一个加载器。</summary>
    /// <returns>加载器。</returns>
    public static AudioWaveformLoader CreateDefault() =>
        new(new FfmpegMediaProbe(), new FfmpegPcmReader());

    /// <summary>Create the Backend self-contained file policy while retaining the original waveform algorithm and source sample rate.</summary>
    /// <remarks>
    /// Ordinary audio signatures are excluded from BMFF preflight; other plausible box sequences are checked before native probing,
    /// including unknown leading boxes. Detected MOV is checked again. This is a bounded reference gate, not full QuickTime or polyglot validation,
    /// and does not authorize a path or replace Host identity verification.
    /// </remarks>
    public static AudioWaveformLoader CreateForSelfContainedFiles(
        string ffmpegPath = FfmpegPcmReader.DefaultExecutableName, string ffprobePath = FfmpegMediaProbe.DefaultExecutableName)
    {
        FfmpegPcmReader reader = new(ffmpegPath);
        return new(new SelfContainedAudioInput.Probe(new FfmpegMediaProbe(ffprobePath)), reader,
            (path, info, callback, token) => reader.ReadSelfContainedSamplesAsync(path,
                SelfContainedAudioInput.DetectedFormat(info.ContainerName), callback, token));
    }

    /// <summary>Create the same self-contained loader with a managed waveform result capacity.</summary>
    /// <remarks>The capacity is checked before accepting a decoded block. Source rate, native stream selection and waveform precision are unchanged.</remarks>
    /// <param name="maximumBaseBuckets">Maximum number of base-resolution buckets, including the final partial bucket.</param>
    /// <param name="ffmpegPath">Trusted decoder executable.</param>
    /// <param name="ffprobePath">Trusted probe executable.</param>
    /// <returns>A loader that rejects oversized complete results instead of truncating them.</returns>
    public static AudioWaveformLoader CreateForSelfContainedFiles(int maximumBaseBuckets,
        string ffmpegPath = FfmpegPcmReader.DefaultExecutableName, string ffprobePath = FfmpegMediaProbe.DefaultExecutableName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBaseBuckets);
        FfmpegPcmReader reader = new(ffmpegPath);
        return new(new SelfContainedAudioInput.Probe(new FfmpegMediaProbe(ffprobePath)), reader,
            (path, info, callback, token) => reader.ReadSelfContainedSamplesAsync(path,
                SelfContainedAudioInput.DetectedFormat(info.ContainerName), callback, token), maximumBaseBuckets);
    }

    private AudioWaveformLoader(IMediaProbe probe, IPcmSampleReader reader,
        Func<string, AudioStreamInfo, Func<ReadOnlyMemory<float>, ValueTask>, CancellationToken, Task<long>> preparedReader,
        int? maximumBaseBuckets = null)
        : this(probe, reader) { _preparedReader = preparedReader; _maximumBaseBuckets = maximumBaseBuckets; }

    /// <summary>加载一个媒体文件的波形。</summary>
    /// <param name="filePath">媒体文件路径。</param>
    /// <param name="baseBucketSamples">峰值金字塔第 0 层的桶大小（采样数）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>加载结果；文件中没有音频流时为 <see langword="null"/>。</returns>
    /// <exception cref="IOException">探测或解码失败时抛出。</exception>
    public async Task<LoadedWaveform?> LoadAsync(
        string filePath,
        int baseBucketSamples = WaveformPeakPyramid.DefaultBaseBucketSamples,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        AudioStreamInfo? info = await _probe.ProbeAsync(filePath, cancellationToken).ConfigureAwait(false);

        if (info is null)
        {
            return null;
        }

        WaveformPeakBuilder builder = new(baseBucketSamples);

        ValueTask Append(ReadOnlyMemory<float> samples)
            {
                if (_maximumBaseBuckets is { } maximum)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (samples.Length > (long)maximum * baseBucketSamples - builder.SampleCount)
                        throw new WaveformPeakBudgetExceededException();
                }
                builder.Append(samples.Span);
                return ValueTask.CompletedTask;
            }
        long decoded = _preparedReader is null
            ? await _reader.ReadMonoSamplesAsync(filePath, Append, cancellationToken).ConfigureAwait(false)
            : await _preparedReader(filePath, info, Append, cancellationToken).ConfigureAwait(false);
        if (_preparedReader is not null && decoded <= 0) throw new IOException("音频准备没有实际解码样本。");

        // 探测得到的时长与解码出的采样数可能不一致（VBR、容器头部信息不准等），
        // 这里以"实际解码出的采样数"为准，因为波形是照着它画的。
        WaveformPeakPyramid peaks = builder.Build(info.SampleRate);

        return new LoadedWaveform(info, peaks);
    }
}

/// <summary>A complete waveform would exceed the caller's explicitly configured base-bucket capacity.</summary>
public sealed class WaveformPeakBudgetExceededException : IOException
{
    /// <summary>Create a capacity rejection without exposing an input path.</summary>
    public WaveformPeakBudgetExceededException() : base("完整波形超过准备结果容量。") { }
}
