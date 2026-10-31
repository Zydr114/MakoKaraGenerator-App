using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>有界检查后使用原领域规范解析器；不重新实现分析或量化数值。</summary>
public static class AnalysisResultCodec
{
    public const string SourceProfile = BackendAudioInputProfiles.LegacyAnalysisSource;
    public const int MaximumResultBytes = BoundedAudioAnalysisCodec.MaximumBytes;
    public const long MaximumSamples = BoundedAudioAnalysisCodec.MaximumSamples;
    public const int MaximumPoints = BoundedAudioAnalysisCodec.MaximumPoints;
    public const long MaximumAudioBytes = BackendAudioInputProfiles.MaximumAudioBytes;
    public const long MaximumSourceDurationUs = 300_000_000;

    public static AudioAnalysisRevision Decode(ReadOnlyMemory<byte> bytes, string sourceSha256)
    {
        try { return BoundedAudioAnalysisCodec.Decode(bytes, sourceSha256); }
        catch (InvalidDataException) { throw Invalid(); }
    }
    private static WorkerProtocolException Invalid() => new("完整分析结果不满足固定来源、规范编码或预算。", "WorkerAnalysisResultInvalid");
}
