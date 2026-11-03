using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>已打开的固定 Runtime 会话上使用的私有 GPU 流消息，不是 HTTP 合同。</summary>
public static class GpuStreamWorkerContract
{
    /// <summary>建立同设备池和本机通道。</summary>
    public const string OpenKind = "gpu-stream-open-v1";
    /// <summary>池实际分配和进程身份。</summary>
    public const string OpenResultKind = "gpu-stream-open-result-v1";
    /// <summary>停止生产并排空消费者。</summary>
    public const string CloseKind = "gpu-stream-close-v1";
    /// <summary>已停止且池已排空。</summary>
    public const string CloseResultKind = "gpu-stream-close-result-v1";
    /// <summary>读取 Worker 的权威流状态。</summary>
    public const string ReadKind = "gpu-stream-read-v1";
    /// <summary>Worker 当前流状态。</summary>
    public const string ReadResultKind = "gpu-stream-read-result-v1";
    /// <summary>私有消息字节上限。</summary>
    public const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    /// <summary>编码有界私有 GPU 流消息。</summary>
    public static byte[] Encode<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length is 0 or > MaximumBytes) throw Invalid();
        return bytes;
    }

    /// <summary>解码有界私有 GPU 流消息；重复字段和未知字段均被拒绝。</summary>
    public static T Decode<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw Invalid();
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            ValidateUnique(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void ValidateUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                ValidateUnique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) ValidateUnique(item);
    }

    private static WorkerProtocolException Invalid() => new("GPU 流私有消息无效。", "WorkerGpuStreamProtocolInvalid");
}

/// <summary>固定 Runtime 会话打开后，由 Host 发送的 GPU 初始化请求。</summary>
/// <remarks>BrokerPath 和 Handshake 是私有数据，禁止日志或公开响应。</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuStreamWorkerOpen(string StreamId, string HostInstanceId, string InputGeneration,
    int Width, int Height, bool TransparentBackground, ulong MaximumAllocationBytes, int ExpectedMainPid,
    string BrokerPath, string Handshake);

/// <summary>只有真实分配成功后返回池身份和实际预算使用。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuStreamWorkerOpened(string StreamId, string ProducerEpoch, string PoolGeneration,
    int Width, int Height, int SlotCount, ulong AllocationBytes, int WorkerPid,
    uint VendorId, uint DeviceId, uint DrmRenderMajor, uint DrmRenderMinor, string DeviceUuid,
    GpuWorkerBindingProof? WarmupBinding = null);

/// <summary>关闭请求不暗示消费者已完成。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuStreamWorkerClose(string StreamId, string ProducerEpoch, string InputGeneration);

/// <summary>Worker 确认停止且租约已全部排空。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuStreamWorkerClosed(string StreamId, string ProducerEpoch, bool Drained);

/// <summary>读取绑定的 Worker 代次。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuStreamWorkerRead(string StreamId, string ProducerEpoch, string InputGeneration);

/// <summary>Worker 观察到的播放及排空状态。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuStreamWorkerState(string State, long? LastAppliedControlSeq, string TimelineEpoch,
    string TimelineResetId, string Mode, long? EffectiveMediaTimeUs, string? FailureCode, bool Drained,
    GpuStreamDiagnosticSummary? Diagnostics = null);

/// <summary>受限的渲染诊断摘要。</summary>
public sealed record GpuStreamDiagnosticSummary(int Total, int WarningCount, int ErrorCount, bool Truncated,
    GpuStreamDiagnosticItem[] Items);

/// <summary>单条受限渲染诊断。</summary>
public sealed record GpuStreamDiagnosticItem(string Severity, string Code, string Message, string? SourceId, string? Stage);
