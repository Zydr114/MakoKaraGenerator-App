namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定授权音频的有限来源与明确解码器策略，不从路径或MIME推断。</summary>
public static class BackendAudioInputProfiles
{
    public const string Pcm16Wave = "pcm16-wave-self-contained";
    public const string Mp3 = "mp3-layer3-self-contained";
    public const string Flac = "native-flac-self-contained";
    public const string Aac = "aac-adts-self-contained";
    public const string Mov = "mov-audio-self-contained";
    public const string Ogg = "ogg-audio-self-contained";
    public const string LegacyAnalysisSource = "pcm16-wave-analysis-v1";
    public const string CompressedAnalysisSource = "fixed-compressed-audio-analysis-v1";
    public const long MaximumAudioBytes = 512 * 1024 * 1024;

    public static bool IsSupported(string? audioProfile) => audioProfile is Pcm16Wave or Mp3 or Flac or Aac or Mov or Ogg;
    public static bool IsAnalysisSourceSupported(string? sourceProfile) => sourceProfile is LegacyAnalysisSource or CompressedAnalysisSource;
    public static string GetInputFormat(string audioProfile) => audioProfile switch
    {
        Pcm16Wave => "wav", Mp3 => "mp3", Flac => "flac", Aac => "aac", Mov => "mov", Ogg => "ogg",
        _ => throw new ArgumentException("音频来源能力不受支持。", nameof(audioProfile)),
    };
    public static string GetAnalysisSourceProfile(string audioProfile) => audioProfile switch
    {
        Pcm16Wave => LegacyAnalysisSource, Mp3 or Flac or Aac or Mov or Ogg => CompressedAnalysisSource,
        _ => throw new ArgumentException("音频来源能力不受支持。", nameof(audioProfile)),
    };

    /// <summary>旧导出省略格式只表示原WAV，新压缩来源必须显式表达已知格式。</summary>
    public static bool TryResolveExportAudioProfile(string? audioProfile, out string resolved)
    {
        resolved = audioProfile ?? Pcm16Wave;
        if (IsSupported(resolved)) return true;
        resolved = string.Empty;
        return false;
    }

    /// <summary>旧账本省略格式时仅解释成原WAV；压缩来源必须明确表达受支持格式。</summary>
    public static bool TryResolveAnalysisAudioProfile(string sourceProfile, string? audioProfile, out string resolved)
    {
        resolved = string.Empty;
        if (sourceProfile == LegacyAnalysisSource && audioProfile is null or Pcm16Wave) { resolved = Pcm16Wave; return true; }
        if (sourceProfile == CompressedAnalysisSource && audioProfile is Mp3 or Flac or Aac or Mov or Ogg) { resolved = audioProfile; return true; }
        return false;
    }
}
