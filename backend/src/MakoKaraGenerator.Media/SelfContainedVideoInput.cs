using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>
/// A deliberately narrow video adapter for private package resources. It
/// accepts only a self-contained ISO-BMFF file and decodes one BGRA frame.
/// This is separate from the user-facing background provider because that
/// provider intentionally supports ordinary FFmpeg input discovery.
/// </summary>
public static class SelfContainedVideoInput
{
    /// <summary>Container profile identifier for self-contained video inputs.</summary>
    public const string ContainerProfile = "self-contained-mov-mp4-v1";

    /// <summary>Inspects a self-contained video synchronously for legacy callers.</summary>
    public static SelfContainedVideoInspection Inspect(string path, CancellationToken cancellationToken = default)
        => InspectAsync(path, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Inspects a self-contained video and decodes its first BGRA frame.</summary>
    public static async Task<SelfContainedVideoInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            SelfContainedMovInputValidator.ValidateFile(path, required: true, cancellationToken);
            using MediaOperationDeadline deadline = new(TimeSpan.FromSeconds(20), cancellationToken);
            using OwnedMediaProcess process = OwnedMediaProcess.Start(FfmpegMediaProbe.DefaultExecutableName, ["-v", "error", "-f", "mov",
                "-protocol_whitelist", "file,pipe", "-enable_drefs", "0", "-use_absolute_path", "0",
                "-show_entries", "stream=codec_type,codec_name,width,height,duration:format=duration,format_name", "-of", "json", path]);
            using CancellationTokenRegistration registration = deadline.Token.Register(process.Terminate);
            string json = await process.ReadMetadataAsync(deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();
            ParsedMetadata metadata = ParseMetadata(json);
            if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.DurationUs <= 0)
                throw Invalid();
            int frameBytes = checked(metadata.Width * metadata.Height * 4);
            if (frameBytes <= 0 || frameBytes > 16 * 1024 * 1024) throw Budget();

            using OwnedMediaProcess decoder = OwnedMediaProcess.Start(FfmpegVideoFrameProvider.DefaultExecutableName,
                ["-v", "error", "-f", "mov", "-protocol_whitelist", "file,pipe", "-enable_drefs", "0", "-use_absolute_path", "0",
                    "-i", path, "-an", "-sn", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-"]);
            using CancellationTokenRegistration decodeRegistration = deadline.Token.Register(decoder.Terminate);
            byte[] pixels = new byte[frameBytes];
            int offset = 0;
            while (offset < pixels.Length)
            {
                int read = await decoder.Output.ReadAsync(pixels.AsMemory(offset), deadline.Token).ConfigureAwait(false);
                if (read == 0) throw Invalid();
                offset += read;
                deadline.ThrowIfExpired();
            }
            return new(metadata.DurationUs, metadata.Width, metadata.Height, metadata.Codec, metadata.HasAudio,
                pixels, 0, ContainerProfile);
        }
        catch (OperationCanceledException) { throw; }
        catch (WorkerResourceMediaException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or OverflowException or FormatException)
        { throw Invalid(error); }
    }

    private static ParsedMetadata ParseMetadata(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
                throw Invalid();
            JsonElement? video = null; bool audio = false;
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string? kind = stream.TryGetProperty("codec_type", out JsonElement type) ? type.GetString() : null;
                if (kind == "audio") audio = true;
                if (kind == "video" && video is null) video = stream;
            }
            if (video is null) throw Invalid();
            int width = ReadInt(video.Value, "width"), height = ReadInt(video.Value, "height");
            string codec = ReadString(video.Value, "codec_name") ?? "unknown";
            long duration = ReadDuration(video.Value, root, "duration");
            return new(duration, width, height, codec, audio);
        }
        catch (JsonException error) { throw new InvalidDataException("视频元数据不是合法JSON。", error); }
    }

    private static int ReadInt(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement field) && field.TryGetInt32(out int result) ? result : 0;

    private static string? ReadString(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement field) ? field.GetString() : null;

    private static long ReadDuration(JsonElement stream, JsonElement root, string name)
    {
        string? text = ReadString(stream, name);
        if (text is null && root.TryGetProperty("format", out JsonElement format)) text = ReadString(format, name);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
            ? checked((long)Math.Round(seconds * 1_000_000d)) : 0;
    }

    private static WorkerResourceMediaException Invalid(Exception? inner = null)
        => new("自包含视频资源无法完成受限容器检查或首帧解码。", "WorkerResourceVideoInvalid", inner);

    private static WorkerResourceMediaException Budget()
        => new("自包含视频资源超过固定首帧预算。", "WorkerResourceBudgetExceeded");

    private readonly record struct ParsedMetadata(long DurationUs, int Width, int Height, string Codec, bool HasAudio);
}

/// <summary>Validated metadata and first frame for a self-contained video.</summary>
public sealed record SelfContainedVideoInspection(long DurationUs, int Width, int Height, string Codec,
    bool HasAudio, byte[] FramePixels, long FrameTimeUs, string ContainerProfile);

/// <summary>Media validation failure reported by the package resource worker.</summary>
public sealed class WorkerResourceMediaException(string message, string code, Exception? inner = null)
    : IOException(message, inner)
{
    /// <summary>Stable worker error code.</summary>
    public string Code { get; } = code;
}
