using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>字体环境目录响应。</summary>
public sealed record FontListResponse(string[] Families, string Environment,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>字体实际整形检查请求。</summary>
[JsonConverter(typeof(FontInspectRequestJsonConverter))]
public sealed record FontInspectRequest(
    string Text,
    string? Family,
    float FontSizePx = 72,
    int FontWeight = 400,
    bool Italic = false,
    string[]? FallbackFamilies = null,
    string? SessionId = null,
    string? ExpectedRevision = null);

/// <summary>发生回退或缺字的文本元素。</summary>
public sealed record FontElementDiagnosticDto(int ElementIndex, int Utf16Start, string Text,
    string ActualFamily, bool IsFallback, bool HasMissingGlyph);

/// <summary>字体实际整形检查响应。</summary>
public sealed record FontInspectResponse(bool RequestedAvailable, string ResolvedFamily,
    int[] AvailableWeights, bool SupportsItalic, FontElementDiagnosticDto[] Elements,
    string Environment, string ProtocolVersion = BackendProtocol.Version);

/// <summary>字体 Worker 的私有输入和响应合同。</summary>
public static class FontWorkerContract
{
    /// <summary>目录消息 kind。</summary>
    public const string ListKind = "fonts-list-v1";
    /// <summary>整形检查消息 kind。</summary>
    public const string InspectKind = "fonts-inspect-v1";
    /// <summary>字体合同 profile。</summary>
    public const string Profile = "fonts-v1";
    /// <summary>实际字体执行环境标识。</summary>
    public const string Environment = "worker-skia";
    /// <summary>最大检查文本 UTF-16 单元数。</summary>
    public const int MaxText = 100_000;
    /// <summary>最大显式回退族数量。</summary>
    public const int MaxFallbackFamilies = 32;
    /// <summary>单一字体族名最大长度。</summary>
    public const int MaxFamilyLength = 256;

    /// <summary>严格拒绝未知属性的 JSON 选项。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>创建目录请求 payload。</summary>
    public static byte[] ListPayload() => Encoding.UTF8.GetBytes(Profile);

    /// <summary>验证并编码整形请求。</summary>
    public static byte[] EncodeInspect(FontInspectRequest value)
    {
        Validate(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    }

    /// <summary>严格解码并验证整形请求。</summary>
    public static FontInspectRequest DecodeInspect(byte[] payload)
    {
        if (payload.Length > WorkerProtocol.MaxPayloadBytes)
            throw new WorkerProtocolException("字体请求超限。", "FontWorkerBudgetExceeded");
        FontInspectRequest value = JsonSerializer.Deserialize<FontInspectRequest>(payload, JsonOptions)
            ?? throw new WorkerProtocolException("字体请求为空。", "FontWorkerInvalid");
        Validate(value);
        return value;
    }

    /// <summary>验证整形请求预算和数值域。</summary>
    public static void Validate(FontInspectRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Text.Length > MaxText || value.FontSizePx <= 0 || !float.IsFinite(value.FontSizePx)
            || value.FontWeight is < 100 or > 900 || value.Family?.Length > MaxFamilyLength
            || value.FallbackFamilies is { Length: > MaxFallbackFamilies }
            || value.FallbackFamilies?.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > MaxFamilyLength) == true)
            throw new WorkerProtocolException("字体检查参数超出预算。", "FontWorkerBudgetExceeded");
    }

    /// <summary>验证目录响应的环境、数组和预算。</summary>
    public static void ValidateListResponse(FontListResponse value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ProtocolVersion != BackendProtocol.Version || value.Environment != Environment
            || value.Families is null || value.Families.Any(string.IsNullOrWhiteSpace)
            || value.Families.Length != value.Families.Distinct(StringComparer.Ordinal).Count()
            || value.Families.Any(item => item.Length > MaxFamilyLength))
            throw new WorkerProtocolException("字体目录响应无效。", "FontWorkerInvalid");
    }

    /// <summary>验证整形响应的环境、数组和诊断身份。</summary>
    public static void ValidateInspectResponse(FontInspectResponse value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ProtocolVersion != BackendProtocol.Version || value.Environment != Environment
            || string.IsNullOrWhiteSpace(value.ResolvedFamily)
            || value.AvailableWeights is null
            || value.AvailableWeights.Any(weight => weight is < 100 or > 900)
            || value.AvailableWeights.Length != value.AvailableWeights.Distinct().Count()
            || value.Elements is null
            || value.Elements.Any(element => element is null || element.ElementIndex < 0 || element.Utf16Start < 0
                || string.IsNullOrWhiteSpace(element.Text) || string.IsNullOrWhiteSpace(element.ActualFamily))
            || value.Elements.Select(element => element.ElementIndex).Distinct().Count() != value.Elements.Length)
            throw new WorkerProtocolException("字体检查响应无效。", "FontWorkerInvalid");
    }
}

/// <summary>字体检查请求的严格 JSON 转换器。</summary>
public sealed class FontInspectRequestJsonConverter : JsonConverter<FontInspectRequest>
{
    private static readonly string[] Names = ["text", "family", "fontSizePx", "fontWeight", "italic",
        "fallbackFamilies", "sessionId", "expectedRevision"];

    /// <inheritdoc />
    public override FontInspectRequest Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        JsonProperty[] properties = root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().ToArray() : [];
        if (properties.Length != Names.Length
            || !properties.Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(Names))
            throw new JsonException("字体检查字段不完整、重复或包含未知字段。");
        return new(
            root.GetProperty("text").GetString() ?? throw new JsonException("text不能为空。"),
            root.GetProperty("family").ValueKind == JsonValueKind.Null ? null : root.GetProperty("family").GetString(),
            root.GetProperty("fontSizePx").GetSingle(), root.GetProperty("fontWeight").GetInt32(),
            root.GetProperty("italic").GetBoolean(),
            root.GetProperty("fallbackFamilies").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("fallbackFamilies").Deserialize<string[]>(options),
            root.GetProperty("sessionId").ValueKind == JsonValueKind.Null ? null : root.GetProperty("sessionId").GetString(),
            root.GetProperty("expectedRevision").ValueKind == JsonValueKind.Null ? null : root.GetProperty("expectedRevision").GetString());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FontInspectRequest value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("text", value.Text);
        writer.WriteString("family", value.Family);
        writer.WriteNumber("fontSizePx", value.FontSizePx);
        writer.WriteNumber("fontWeight", value.FontWeight);
        writer.WriteBoolean("italic", value.Italic);
        writer.WritePropertyName("fallbackFamilies");
        JsonSerializer.Serialize(writer, value.FallbackFamilies, options);
        writer.WriteString("sessionId", value.SessionId);
        writer.WriteString("expectedRevision", value.ExpectedRevision);
        writer.WriteEndObject();
    }
}
