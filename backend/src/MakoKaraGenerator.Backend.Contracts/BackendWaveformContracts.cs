using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>保留峰值payload与单次读取的独立宿主预算，不是总托管堆上限。</summary>
public static class WaveformLimits
{
    /// <summary>整个Host已保留峰值payload最多64MiB；不含临时prepare与有界响应内存。</summary>
    public const long MaxRetainedBytes = 64 * 1024 * 1024;
    /// <summary>一次读取最多8192桶，每桶两个float32共8字节。</summary>
    public const int MaxWindowBuckets = 8192;
    /// <summary>每桶按little-endian Min,Max排列。</summary>
    public const string Format = "minmax-f32le-v1";
    /// <summary>绑定首音轨、FFmpeg单声道downmix与峰值位模式的身份域。</summary>
    public const string IdentityProfile = "first-audio-mono-peaks-v1";
}

/// <summary>完整真实层级；桶采样数保留整数字符串，避免移位或前端整数溢出。</summary>
public sealed record WaveformLevelDto(int Level, string BucketSamples, int BucketCount);

/// <summary>当前会话同一次音频准备的缓存状态；不可用时省略全部音频payload字段。</summary>
public sealed record ProjectWaveformResponse(
    string ProjectSessionId,
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    string ProtocolVersion = BackendProtocol.Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WaveformId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SampleRate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SampleCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DurationUs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SourceChannels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WaveformChannels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BaseBucketSamples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaveformLevelDto[]? Levels = null);
