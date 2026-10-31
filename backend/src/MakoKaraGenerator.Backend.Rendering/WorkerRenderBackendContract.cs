using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>每个实际渲染 Worker 启动后、业务源工作前的对称设备核验。</summary>
public static class WorkerRenderBackendContract
{
    /// <summary>要求固定后端实际创建、绘制及读回的请求。</summary>
    public const string RequestKind = "render-backend-verify-v1";
    /// <summary>实际设备检查成功回执。</summary>
    public const string ResultKind = "render-backend-verified-v1";
    /// <summary>固定设备当前不能完成检查。</summary>
    public const string UnavailableCode = "RenderBackendUnavailable";
    /// <summary>Worker未提供本版设备检查能力。</summary>
    public const string UnsupportedCode = "RenderBackendUnsupported";
    /// <summary>Worker固定选择或实际设备与请求不一致。</summary>
    public const string MismatchCode = "RenderBackendMismatch";
    /// <summary>设备检查输入或回执不满足协议。</summary>
    public const string InvalidCode = "RenderBackendProtocolInvalid";
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    /// <summary>公开选择仅接受大小写精确的cpu或gpu。</summary>
    public static bool IsSupported(string? backend) => backend is "cpu" or "gpu";
    /// <summary>取得公开选择使用的底层API。</summary>
    public static string Api(string backend) => backend switch
    { "cpu" => "raster", "gpu" => "vulkan", _ => throw new ArgumentException("渲染后端必须为cpu或gpu。", nameof(backend)) };
    /// <summary>取得只写入子进程环境的部署后端值。</summary>
    public static string EnvironmentValue(string backend) => backend switch
    { "cpu" => "cpu", "gpu" => "vulkan", _ => throw new ArgumentException("渲染后端必须为cpu或gpu。", nameof(backend)) };

    /// <summary>编码一次固定选择的设备检查输入。</summary>
    public static byte[] EncodeRequest(string backend)
    {
        if (!IsSupported(backend)) throw Invalid();
        return JsonSerializer.SerializeToUtf8Bytes(new Request(1, backend), Options);
    }

    /// <summary>核对输入类型、完整SHA与严格有界内容。</summary>
    public static string DecodeRequest(WorkerMessage message)
    {
        if (message.Kind != RequestKind || message.InputSha256 != WorkerProtocol.ComputeSha256(message.Payload)
            || message.Payload.Length is <= 0 or > MaximumBytes) throw Invalid();
        Request value = Decode<Request>(message.Payload);
        if (value.Version != 1 || !IsSupported(value.RenderBackend)) throw Invalid();
        return value.RenderBackend;
    }

    /// <summary>编码已完成实际绘制读回的设备摘要。</summary>
    public static byte[] EncodeResult(string backend, RenderBackendDeviceInfoDto device)
    {
        ValidateDevice(device);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Result(1, backend, Api(backend), true, device), Options);
        if (bytes.Length > MaximumBytes) throw Invalid();
        return bytes;
    }

    /// <summary>对称核对本次请求关联、后端、API和有界设备摘要。</summary>
    public static RenderBackendDeviceInfoDto DecodeResult(WorkerMessage request, WorkerMessage response, string expectedBackend)
    {
        if (response.Kind != ResultKind)
            throw new WorkerProtocolException("Worker未提供渲染后端核验能力。", UnsupportedCode);
        if (response.RequestId != request.RequestId || response.InputSha256 != request.InputSha256
            || response.Cancelled || response.Error is not null || response.ErrorCode is not null
            || response.OutputSha256 != WorkerProtocol.ComputeSha256(response.Payload)
            || response.Payload.Length is <= 0 or > MaximumBytes) throw Invalid();
        Result result = Decode<Result>(response.Payload);
        if (result.Version != 1 || !result.ReadbackVerified) throw Invalid();
        if (result.RenderBackend != expectedBackend || result.Api != Api(expectedBackend))
            throw new WorkerProtocolException("实际Worker未使用请求固定的渲染后端。", MismatchCode);
        ValidateDevice(result.Device);
        return result.Device;
    }

    /// <summary>将Worker错误收敛到允许公开的稳定代码。</summary>
    public static string SafeFailureCode(string? code) => code is UnavailableCode or UnsupportedCode or MismatchCode or InvalidCode
        ? code : UnsupportedCode;

    private static T Decode<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            ValidateUniqueProperties(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!properties.Add(property.Name)) throw Invalid();
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) ValidateUniqueProperties(item);
    }

    private static void ValidateDevice(RenderBackendDeviceInfoDto? device)
    {
        if (device is null || string.IsNullOrWhiteSpace(device.Device)
            || new[] { device.Vendor, device.Device, device.Driver, device.ApiVersion }.Any(value => value is { } text
                && (text.Length > 256 || text.Any(char.IsControl)))) throw Invalid();
    }
    private static WorkerProtocolException Invalid() => new("Worker渲染后端核验回执无效。", InvalidCode);
    private sealed record Request(int Version, string RenderBackend);
    private sealed record Result(int Version, string RenderBackend, string Api, bool ReadbackVerified, RenderBackendDeviceInfoDto Device);
}
