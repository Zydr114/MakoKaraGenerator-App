namespace MakoKaraGenerator.Media;

internal enum SelfContainedAudioFormat { Wave, Mp3, Flac, Aac, Mov, Ogg }

/// <summary>Backend preparation policy; independent of the narrower analysis and export profiles.</summary>
internal static class SelfContainedAudioInput
{
    internal const string FormatWhitelist = "wav,mp3,flac,aac,mov,ogg";

    internal static string Demuxer(SelfContainedAudioFormat format) => format switch
    {
        SelfContainedAudioFormat.Wave => "wav", SelfContainedAudioFormat.Mp3 => "mp3",
        SelfContainedAudioFormat.Flac => "flac", SelfContainedAudioFormat.Aac => "aac",
        SelfContainedAudioFormat.Mov => "mov", SelfContainedAudioFormat.Ogg => "ogg",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static SelfContainedAudioFormat DetectedFormat(string container) => container switch
    {
        "wav" => SelfContainedAudioFormat.Wave, "mp3" => SelfContainedAudioFormat.Mp3,
        "flac" => SelfContainedAudioFormat.Flac, "aac" => SelfContainedAudioFormat.Aac,
        "ogg" => SelfContainedAudioFormat.Ogg,
        _ when container.Length > 0 && container.Split(',').All(value => value is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2")
            => SelfContainedAudioFormat.Mov,
        _ => throw new IOException("音频准备不支持此容器。"),
    };

    internal static void AddPolicy(List<string> arguments)
        => arguments.AddRange(["-format_whitelist", FormatWhitelist, "-protocol_whitelist", "file,pipe"]);

    internal static void AddMovPolicy(List<string> arguments)
        => arguments.AddRange(["-enable_drefs", "0", "-use_absolute_path", "0"]);

    internal sealed class Probe(FfmpegMediaProbe inner) : IMediaProbe
    {
        public Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
            => inner.ProbeSelfContainedAsync(filePath, cancellationToken);
        public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
