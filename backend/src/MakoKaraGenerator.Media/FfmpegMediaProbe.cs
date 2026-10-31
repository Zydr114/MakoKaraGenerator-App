using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CliWrap;
using MakoKaraGenerator.Processes;
using System.Text;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 用 FFprobe 探测媒体文件（docs/roadmap.md §8 的 <c>MEDIA-01</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 调用形态（已在本机 FFprobe 9.0.2 上实测）：
/// </para>
/// <code>
/// ffprobe -v error -select_streams a:0
///         -show_entries stream=codec_name,sample_rate,channels,duration
///         -show_entries format=duration,format_name
///         -of json &lt;file&gt;
/// </code>
/// <para>
/// 注意 FFprobe 输出的 JSON 里数值常常是<b>字符串</b>（例如 <c>"sample_rate": "44100"</c>），
/// 因此解析时两种形态都要接受，不能直接反序列化成整数属性。
/// </para>
/// </remarks>
public sealed class FfmpegMediaProbe : IMediaProbe
{
    /// <summary>默认可执行文件名。</summary>
    public const string DefaultExecutableName = "ffprobe";

    private readonly string _executablePath;

    /// <summary>创建一个探测器。</summary>
    /// <param name="executablePath">FFprobe 可执行文件路径；省略时使用 PATH 中的 <c>ffprobe</c>。</param>
    public FfmpegMediaProbe(string executablePath = DefaultExecutableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <inheritdoc/>
    public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        => ProbeAudioCoreAsync(filePath, null, cancellationToken);

    internal Task<AudioStreamInfo?> ProbeSelfContainedAsync(string filePath, CancellationToken cancellationToken)
        => ProbeAudioCoreAsync(filePath, null, cancellationToken, selfContained: true);

    /// <summary>Probe a restricted WAV, MP3, FLAC, AAC, MOV or Ogg input without container or network protocol autodetection.</summary>
    public Task<AudioStreamInfo?> ProbeAsync(string filePath, string forcedInputFormat, CancellationToken cancellationToken = default)
    {
        if (forcedInputFormat is not ("wav" or "mp3" or "flac" or "aac" or "mov" or "ogg"))
            throw new ArgumentException("Restricted audio format must be wav, mp3, flac, aac, mov or ogg.", nameof(forcedInputFormat));
        return ProbeAudioCoreAsync(filePath, forcedInputFormat, cancellationToken, selfContained: true);
    }

    private async Task<AudioStreamInfo?> ProbeAudioCoreAsync(string filePath, string? forcedInputFormat, CancellationToken token,
        bool selfContained = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        List<string> arguments = ["-v", "error", "-select_streams", "a:0", "-show_entries",
            "stream=codec_name,sample_rate,channels,duration", "-show_entries", "format=duration,format_name", "-of", "json"];
        if (selfContained)
        {
            SelfContainedMovInputValidator.ValidateFile(filePath, required: false, token);
            SelfContainedAudioInput.AddPolicy(arguments);
            SelfContainedAudioInput.AddMovPolicy(arguments);
            if (forcedInputFormat is not null) arguments.AddRange(["-f", forcedInputFormat]);
        }
        else AddInputPolicy(arguments, forcedInputFormat);
        arguments.Add(filePath);
        AudioStreamInfo? info = Parse(await ExecuteProbeAsync(arguments, token).ConfigureAwait(false));
        if (selfContained && info is not null && SelfContainedAudioInput.DetectedFormat(info.ContainerName) == SelfContainedAudioFormat.Mov)
            SelfContainedMovInputValidator.ValidateFile(filePath, required: true, token);
        return info;
    }

    /// <summary>解析 FFprobe 的 JSON 输出。</summary>
    /// <param name="json">FFprobe 输出。</param>
    /// <returns>音频流信息；没有音频流时为 <see langword="null"/>。</returns>
    /// <exception cref="IOException">JSON 无法解析时抛出。</exception>
    internal static AudioStreamInfo? Parse(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new IOException($"FFprobe 输出不是合法 JSON：{exception.Message}", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("streams", out JsonElement streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement stream = streams[0];
            int sampleRate = ReadInt(stream, "sample_rate");
            int channels = ReadInt(stream, "channels");
            string codec = ReadString(stream, "codec_name") ?? "unknown";

            string container = "unknown";
            long? formatDurationUs = null;

            if (root.TryGetProperty("format", out JsonElement format))
            {
                container = ReadString(format, "format_name") ?? container;
                formatDurationUs = ReadDurationUs(format, "duration");
            }

            long durationUs = ReadDurationUs(stream, "duration") ?? formatDurationUs ?? 0;

            return new AudioStreamInfo(durationUs, sampleRate, channels, codec, container);
        }
    }

    /// <inheritdoc/>
    public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default)
        => ProbeFileAsync(filePath, false, null, cancellationToken);

    /// <summary>Explicitly decode-count frames and optionally force the managed-task WAV or MOV demuxer.</summary>
    public async Task<MediaFileInfo?> ProbeFileAsync(string filePath, bool countFrames, string? forcedInputFormat,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (forcedInputFormat is not (null or "wav" or "mov"))
            throw new ArgumentException("Restricted format must be wav or mov.", nameof(forcedInputFormat));
        List<string> arguments = ["-v", "error", "-show_entries",
            countFrames ? "stream=codec_type,codec_name,width,height,r_frame_rate,duration,pix_fmt,nb_read_frames"
                : "stream=codec_type,codec_name,width,height,r_frame_rate,duration,pix_fmt",
            "-show_entries", "format=duration,format_name", "-of", "json"];
        if (countFrames) arguments.Add("-count_frames");
        AddInputPolicy(arguments, forcedInputFormat);
        arguments.Add(filePath);
        return ParseFile(await ExecuteProbeAsync(arguments, cancellationToken).ConfigureAwait(false));
    }

    private static void AddInputPolicy(List<string> arguments, string? forcedInputFormat)
    {
        if (forcedInputFormat is null) return;
        arguments.Add("-protocol_whitelist"); arguments.Add("file,pipe");
        arguments.Add("-f"); arguments.Add(forcedInputFormat);
    }

    private async Task<string> ExecuteProbeAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        string output = string.Empty, errors = string.Empty;
        CommandResult result;
        try
        {
            result = await Cli.Wrap(_executablePath).WithArguments(arguments).WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.Create(async (stream, cancellation) =>
                {
                    using StreamReader reader = new(stream, Encoding.UTF8);
                    output = await BoundedTextReader.ReadToEndAsync(reader, 1_048_576, true, cancellation).ConfigureAwait(false);
                }))
                .WithStandardErrorPipe(PipeTarget.Create(async (stream, cancellation) =>
                {
                    using StreamReader reader = new(stream, Encoding.UTF8);
                    errors = await BoundedTextReader.ReadToEndAsync(reader, 32_768, false, cancellation).ConfigureAwait(false);
                })).ExecuteAsync(token).ConfigureAwait(false);
        }
        catch (Win32Exception error)
        {
            throw new IOException($"无法启动 {_executablePath}：{error.Message}。请确认 FFmpeg 已安装并位于 PATH 中。", error);
        }
        if (result.ExitCode != 0)
            throw new IOException(string.Create(CultureInfo.InvariantCulture,
                $"{_executablePath} 探测失败（退出码 {result.ExitCode}）：{errors.Trim()}"));
        return output;
    }

    /// <summary>解析“全部流”形态的 FFprobe 输出。</summary>
    /// <param name="json">FFprobe 输出。</param>
    /// <returns>文件信息；解析不出任何流时为 <see langword="null"/>。</returns>
    /// <exception cref="IOException">JSON 无法解析时抛出。</exception>
    internal static MediaFileInfo? ParseFile(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new IOException($"FFprobe 输出不是合法 JSON：{exception.Message}", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("streams", out JsonElement streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0)
            {
                return null;
            }

            string container = "unknown";
            long? formatDurationUs = null;

            if (root.TryGetProperty("format", out JsonElement format))
            {
                container = ReadString(format, "format_name") ?? container;
                formatDurationUs = ReadDurationUs(format, "duration");
            }

            List<MediaStreamInfo> parsed = [];

            foreach (JsonElement stream in streams.EnumerateArray())
            {
                parsed.Add(new MediaStreamInfo(
                    ReadString(stream, "codec_type") ?? "unknown",
                    ReadString(stream, "codec_name") ?? "unknown",
                    ReadInt(stream, "width"),
                    ReadInt(stream, "height"),
                    ReadFrameRate(stream),
                    ReadDurationUs(stream, "duration") ?? 0)
                {
                    // 像素格式是透明输出（EXPORT-07）的验收依据：报告要说"产物确实是 yuva…"，
                    // 而不是"文件名叫 .mov"。
                    PixelFormat = ReadString(stream, "pix_fmt"),
                    DecodedFrameCount = ReadLong(stream, "nb_read_frames"),
                    FrameRateNumerator = ReadRationalPart(stream, false),
                    FrameRateDenominator = ReadRationalPart(stream, true),
                });
            }

            return new MediaFileInfo(container, formatDurationUs ?? 0, [.. parsed]);
        }
    }

    /// <summary>读有理数形式的帧率（例如 <c>30000/1001</c>）。</summary>
    private static double ReadFrameRate(JsonElement element)
    {
        string? text = ReadString(element, "r_frame_rate");

        if (string.IsNullOrWhiteSpace(text))
        {
            return 0d;
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double single)
                ? single
                : 0d;
        }

        bool numeratorOk = int.TryParse(
            text.AsSpan(0, slash),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int numerator);

        bool denominatorOk = int.TryParse(
            text.AsSpan(slash + 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int denominator);

        return numeratorOk && denominatorOk && denominator > 0
            ? (double)numerator / denominator
            : 0d;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        string? text = ReadString(element, name);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value >= 0 ? value : null;
    }

    private static int ReadRationalPart(JsonElement element, bool denominator)
    {
        string? text = ReadString(element, "r_frame_rate");
        if (text is null) return 0;
        int separator = text.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0) return 0;
        ReadOnlySpan<char> part = denominator ? text.AsSpan(separator + 1) : text.AsSpan(0, separator);
        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : 0;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private static long? ReadDurationUs(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        double seconds = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed) ? parsed : double.NaN,
            _ => double.NaN,
        };

        return double.IsFinite(seconds) && seconds >= 0d
            ? (long)Math.Round(seconds * 1_000_000d)
            : null;
    }
}
