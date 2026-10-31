using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Media;

/// <summary>完整离线分析结果；失败没有部分序列或伪造的静音输入。</summary>
public sealed record AudioFeatureAnalysis
{
    internal AudioFeatureAnalysis(AudioFeatures features, string? sourceHash = null, string? pcmHash = null,
        long? sampleCount = null, string? diagnosticCode = null, string? diagnosticMessage = null)
    { Features = features; SourceHash = sourceHash; PcmHash = pcmHash; SampleCount = sampleCount;
        DiagnosticCode = diagnosticCode; DiagnosticMessage = diagnosticMessage; }
    /// <summary>可直接固定到脚本会话的只读时间序列。</summary>
    public AudioFeatures Features { get; }
    /// <summary>源文件完整小写 SHA-256；没有成功修订时为空。</summary>
    public string? SourceHash { get; }
    /// <summary>规范小端 float PCM 的完整 SHA-256；没有成功修订时为空。</summary>
    public string? PcmHash { get; }
    /// <summary>实际解码的 48 kHz 单声道样本数；失败时为空。</summary>
    public long? SampleCount { get; }
    /// <summary>不可用的稳定原因；成功时为空。</summary>
    public string? DiagnosticCode { get; }
    /// <summary>包含实际探测／解码／分析原因的信息。</summary>
    public string? DiagnosticMessage { get; }
}

/// <summary>不读播放器的固定离线低频／节奏分析；仅保留 hop，不保留整曲 PCM。</summary>
public sealed class AudioFeatureAnalyzer
{
    /// <summary>算法和时间映射的版本身份；不是精准节拍识别器。</summary>
    public const string AlgorithmVersion = AudioAnalysisProfile.AlgorithmVersion;
    /// <summary>已接受音频合同的固定单声道采样率。</summary>
    public const int SampleRate = AudioAnalysisProfile.SampleRate;
    /// <summary>20 ms 分析步长的样本数量。</summary>
    public const int HopSamples = AudioAnalysisProfile.HopSamples;
    private readonly IMediaProbe _probe;
    private readonly IResampledPcmSampleReader _reader;
    /// <summary>注入实际媒体探测与明确重采样读取器。</summary>
    public AudioFeatureAnalyzer(IMediaProbe probe, IResampledPcmSampleReader reader)
    { ArgumentNullException.ThrowIfNull(probe); ArgumentNullException.ThrowIfNull(reader); _probe = probe; _reader = reader; }
    /// <summary>使用已有媒体适配器创建离线分析服务。</summary>
    public static AudioFeatureAnalyzer CreateDefault() => new(new FfmpegMediaProbe(), new FfmpegPcmReader());
    /// <summary>分析完整音轨；无源／失败明确不可用，取消仍抛出，不发布部分数据。</summary>
    public async Task<AudioFeatureAnalysis> AnalyzeAsync(string? filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (filePath is null) return Unavailable(AudioAvailability.NoAudio, "AudioSourceMissing", "没有音频源。");
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            string sourceHash = await HashFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (await _probe.ProbeAsync(filePath, cancellationToken).ConfigureAwait(false) is null)
                return Unavailable(AudioAvailability.NoAudio, "AudioStreamMissing", "媒体没有音频流。");
            using AudioFeatureBuilder builder = new();
            long reported = await _reader.ReadMonoSamplesAtRateAsync(filePath, SampleRate, samples =>
            { builder.Append(samples.Span, cancellationToken); return ValueTask.CompletedTask; }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (reported != builder.SampleCount) throw new AudioAnalysisException("AudioSampleCountMismatch", "解码回报数量与实际采样数量不一致。");
            if (sourceHash != await HashFileAsync(filePath, cancellationToken).ConfigureAwait(false))
                throw new AudioAnalysisException("AudioSourceChanged", "音频文件在分析期间发生变化，请重新分析。");
            return builder.Complete(sourceHash, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Unavailable(AudioAvailability.AnalysisFailed, error is AudioAnalysisException analysis ? analysis.Code : "AudioAnalysisFailed", error.Message); }
    }
    private static AudioFeatureAnalysis Unavailable(AudioAvailability availability, string code, string message)
        => new(AudioFeatures.Unavailable(availability, code), diagnosticCode: code, diagnosticMessage: message);
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class AudioAnalysisException(string code, string message) : IOException(message)
{ internal string Code { get; } = code; }
