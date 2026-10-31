using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>有界完整分析数据的中立合同，复用原领域规范编码，不读取文件或执行分析。</summary>
public static class BoundedAudioAnalysisCodec
{
    /// <summary>完整规范分析字节上限；快照还须满足包含所有源字段的总预算。</summary>
    public const int MaximumBytes = 4 * 1024 * 1024;
    /// <summary>原48kHz分析的完整采样数上限。</summary>
    public const long MaximumSamples = 14_400_000;
    /// <summary>原960样本hop的完整点数上限。</summary>
    public const int MaximumPoints = 15_000;

    /// <summary>先检查完整数据预算，再编码原规范字节并核内容摘要。</summary>
    /// <param name="analysis">原领域完整成功修订。</param>
    /// <returns>保留全部double、整数时间和元数据的规范字节。</returns>
    /// <exception cref="InvalidDataException">完整数据超过预算或摘要不符。</exception>
    public static byte[] Encode(AudioAnalysisRevision analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.SampleCount is <= 0 or > MaximumSamples || analysis.Points.Length > MaximumPoints) throw Invalid();
        byte[] bytes = AudioAnalysisJson.Encode(analysis);
        if (bytes.Length > MaximumBytes || analysis.ContentHash != Convert.ToHexStringLower(SHA256.HashData(bytes))) throw Invalid();
        return bytes;
    }

    /// <summary>有界检查后由原领域解析器核完整规范、网格、语义修订和来源／内容摘要。</summary>
    /// <param name="bytes">完整规范分析字节。</param>
    /// <param name="sourceSha256">本次固定音频的完整源摘要。</param>
    /// <returns>完整不可变分析数据，不截断或重新量化。</returns>
    /// <exception cref="InvalidDataException">规范、预算或固定身份无效。</exception>
    public static AudioAnalysisRevision Decode(ReadOnlyMemory<byte> bytes, string sourceSha256)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw Invalid();
        try
        {
            MediaFileRevision.ValidateHash(sourceSha256);
            using (JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }))
            {
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("sampleCount", out JsonElement samples) || !samples.TryGetInt64(out long count)
                    || count is <= 0 or > MaximumSamples || !root.TryGetProperty("points", out JsonElement points)
                    || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() > MaximumPoints) throw Invalid();
            }
            AudioAnalysisRevision result = AudioAnalysisJson.Read(bytes);
            if (result.SourceHash != sourceSha256 || result.SampleCount > MaximumSamples || result.Points.Length > MaximumPoints
                || result.ContentHash != Convert.ToHexStringLower(SHA256.HashData(bytes.Span))) throw Invalid();
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or EffectPackageException)
        { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("完整分析数据不满足规范编码、固定身份或预算。");
}
