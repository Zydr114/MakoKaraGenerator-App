using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Internal;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>与作者SDK隔离的一个真实分析hop；值为double，不量化颜色或时间。</summary>
public readonly record struct AudioAnalysisPoint
{
    /// <summary>创建规范值；无phase时confidence必须为零。</summary>
    public AudioAnalysisPoint(long timeUs, double bassLevel, double pulse, double? beatPhase, double beatConfidence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        if (!Unit(bassLevel) || !Unit(pulse) || !Unit(beatConfidence) || (beatPhase is { } phase ? !Unit(phase) || phase == 1 : beatConfidence != 0))
            throw new ArgumentException("音频特征必须有限并在规范范围内。");
        TimeUs = timeUs; BassLevel = bassLevel; Pulse = pulse; BeatPhase = beatPhase; BeatConfidence = beatConfidence;
    }
    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    /// <summary>真实hop起点。</summary>
    public long TimeUs { get; }
    /// <summary>低频包络。</summary>
    public double BassLevel { get; }
    /// <summary>脉冲强度。</summary>
    public double Pulse { get; }
    /// <summary>可空近似节拍相位。</summary>
    public double? BeatPhase { get; }
    /// <summary>估计可信度。</summary>
    public double BeatConfidence { get; }
}

/// <summary>分析元数据的唯一算法／采样／修订定义；Media复用同一身份计算。</summary>
public static class AudioAnalysisProfile
{
    /// <summary>固定基础分析算法。</summary>
    public const string AlgorithmVersion = "bass-pulse-rbj-v1";
    /// <summary>单声道采样率。</summary>
    public const int SampleRate = 48000;
    /// <summary>真实hop样本数。</summary>
    public const int HopSamples = 960;
    /// <summary>整数hop步长。</summary>
    public const long HopUs = 20000;
    /// <summary>沿用既有语义修订，不包括机器路径或UI帧。</summary>
    public static string Revision(string sourceHash, string pcmHash, long sampleCount)
    {
        MediaFileRevision.ValidateHash(sourceHash); MediaFileRevision.ValidateHash(pcmHash); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        string identity = string.Create(CultureInfo.InvariantCulture,
            $"{AlgorithmVersion}|48000|960|origin=0|samples={sampleCount}|source={sourceHash}|pcm={pcmHash}");
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    /// <summary>实际采样对应的向上取整微秒时长。</summary>
    public static long DurationUs(long sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        return checked(sampleCount / SampleRate * 1000000 + (sampleCount % SampleRate * 1000000 + SampleRate - 1) / SampleRate);
    }
}

/// <summary>完整真实分析内容；与语义修订分开的文件hash覆盖全部特征值。</summary>
public sealed class AudioAnalysisRevision : IEquatable<AudioAnalysisRevision>
{
    /// <summary>固定真实hop集合；必须完整覆盖48kHz／960样本网格。</summary>
    public AudioAnalysisRevision(string sourceHash, string pcmHash, long sampleCount, IEnumerable<AudioAnalysisPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points); SourceHash = sourceHash; PcmHash = pcmHash; SampleCount = sampleCount;
        Revision = AudioAnalysisProfile.Revision(sourceHash, pcmHash, sampleCount); DurationUs = AudioAnalysisProfile.DurationUs(sampleCount); Points = [.. points];
        long count = sampleCount / AudioAnalysisProfile.HopSamples + (sampleCount % AudioAnalysisProfile.HopSamples == 0 ? 0 : 1);
        if (Points.Length != count || Points.Where((point, index) => point.TimeUs != (long)index * AudioAnalysisProfile.HopUs || point.TimeUs >= DurationUs).Any())
            throw new ArgumentException("分析hop缺失、不在真实网格内或越出时长。", nameof(points));
        byte[] canonical = Persistence.AudioAnalysisJson.Encode(this);
        ContentByteLength = canonical.LongLength;
        ContentHash = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
    /// <summary>源文件完整哈希。</summary>
    public string SourceHash { get; }
    /// <summary>规范小端PCM哈希。</summary>
    public string PcmHash { get; }
    /// <summary>实际解码样本数。</summary>
    public long SampleCount { get; }
    /// <summary>真实半开时长。</summary>
    public long DurationUs { get; }
    /// <summary>算法和源内容定义的语义修订。</summary>
    public string Revision { get; }
    /// <summary>规范文件全部字节的摘要。</summary>
    public string ContentHash { get; }
    /// <summary>唯一规范完整分析文件的字节数；与内容摘要在同一次编码中确定。</summary>
    public long ContentByteLength { get; }
    /// <summary>不可变真实hop集合。</summary>
    public ImmutableArray<AudioAnalysisPoint> Points { get; }
    /// <summary>唯一受管文件。</summary>
    public string RelativePath => "data/analysis/" + ContentHash + ".json";
    /// <summary>完整内容相等。</summary>
    public bool Equals(AudioAnalysisRevision? other) => other is not null && SourceHash == other.SourceHash && PcmHash == other.PcmHash && SampleCount == other.SampleCount
        && SequenceEquality.ContentEquals(Points, other.Points);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AudioAnalysisRevision other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ContentHash);
}

/// <summary>工程已接受的分析结果；null工程属性表示尚未分析或无音频。</summary>
public sealed record ProjectAudioAnalysis
{
    private ProjectAudioAnalysis(AudioAnalysisRevision? data, string? code, string? message, bool missingStream = false)
    { Data = data; DiagnosticCode = code; DiagnosticMessage = message; MissingStream = missingStream; }
    /// <summary>完整成功修订；失败时为空。</summary>
    public AudioAnalysisRevision? Data { get; }
    /// <summary>实际失败原因。</summary>
    public string? DiagnosticCode { get; }
    /// <summary>实际失败说明。</summary>
    public string? DiagnosticMessage { get; }
    /// <summary>分析确认该媒体没有音频流；与尚未分析、解码失败分开。</summary>
    public bool MissingStream { get; }
    /// <summary>接受完整成功结果。</summary>
    public static ProjectAudioAnalysis Available(AudioAnalysisRevision data) { ArgumentNullException.ThrowIfNull(data); return new(data, null, null); }
    /// <summary>保留失败，不制造空序列。</summary>
    public static ProjectAudioAnalysis Failed(string code, string message)
    { ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(message); return new(null, code, message); }
    /// <summary>保留实际无音频流状态。</summary>
    public static ProjectAudioAnalysis NoStream(string code, string message)
    { ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(message); return new(null, code, message, true); }
}
