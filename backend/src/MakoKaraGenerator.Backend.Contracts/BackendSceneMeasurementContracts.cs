using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>有界只读同帧测量，既不创建任务，也不修改工程历史。</summary>
[JsonConverter(typeof(SceneMeasurementRequestJsonConverter))]
public sealed record SceneMeasurementRequest(string ExpectedRevision, string Mode, string? TimeUs,
    int Width, int Height, string AlphaMode, string? PreviewId = null,
    string? ExpectedPreviewGeneration = null, string? CandidateRevision = null, string? RenderBackend = null);

public sealed record SceneMeasurementElementDto(string Id, string? ParentId, string Kind,
    string? BranchId, string? LineId, string? TokenId, string? RubyId, string? BindingId, string? SlotId,
    bool Representative, bool Visible, double Opacity, string GeometryStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectGeometryMatrixDto? WorldMatrix,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectGeometryPointDto? Pivot,
    ProjectGeometryPointDto[] WorldCorners,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectGeometryReferenceDto? FixedReference,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectGeometryReferenceDto? EditReference);

public sealed record SceneMeasurementDataDto(string SourceSha256, string SampleInputFingerprint, string SourceProfile,
    string SamplingProfile, string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TimeUs, int CanvasWidth, int CanvasHeight, int Width, int Height,
    string AlphaMode, SceneMeasurementElementDto[] Elements, string PngSha256, byte[] Png,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderBackend = null,
    BackendDiagnosticDto[] FallbackDiagnostics = null!);

public sealed record SceneMeasurementResponse(string ProjectSessionId, string ProjectRevision,
    SceneMeasurementDataDto Measurement, string ProtocolVersion = BackendProtocol.Version);

public sealed class SceneMeasurementRequestJsonConverter : JsonConverter<SceneMeasurementRequest>
{
    public override SceneMeasurementRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        string[] required = ["expectedRevision", "mode", "timeUs", "width", "height", "alphaMode"];
        string[] candidate = ["previewId", "expectedPreviewGeneration", "candidateRevision"];
        var properties = root.EnumerateObject().ToArray();
        var names = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != properties.Length) throw new JsonException("测量字段不得重复。");
        names.Remove("renderBackend");
        bool hasCandidateFields = names.SetEquals(required.Concat(candidate));
        if (!(names.SetEquals(required) || hasCandidateFields))
            throw new JsonException();
        if (hasCandidateFields && candidate.Any(name => root.GetProperty(name).ValueKind == JsonValueKind.Null))
            throw new JsonException("候选预览字段不能显式为null。");
        try
        {
            string? ReadOptional(string name) => root.TryGetProperty(name, out var property)
                ? property.GetString()
                : null;
            var request = new SceneMeasurementRequest(root.GetProperty("expectedRevision").GetString()!, root.GetProperty("mode").GetString()!,
                root.GetProperty("timeUs").ValueKind == JsonValueKind.Null ? null : root.GetProperty("timeUs").GetString(),
                root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(), root.GetProperty("alphaMode").GetString()!,
                ReadOptional("previewId"), ReadOptional("expectedPreviewGeneration"), ReadOptional("candidateRevision"), ReadOptional("renderBackend"));
            SceneMeasurementContract.ValidateRequest(request);
            return request;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new JsonException("测量输入字段或union无效。", error); }
    }
    public override void Write(Utf8JsonWriter writer, SceneMeasurementRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("expectedRevision", value.ExpectedRevision); writer.WriteString("mode", value.Mode);
        writer.WriteString("timeUs", value.TimeUs); writer.WriteNumber("width", value.Width); writer.WriteNumber("height", value.Height);
        writer.WriteString("alphaMode", value.AlphaMode);
        if (value.PreviewId is not null) writer.WriteString("previewId", value.PreviewId);
        if (value.ExpectedPreviewGeneration is not null) writer.WriteString("expectedPreviewGeneration", value.ExpectedPreviewGeneration);
        if (value.CandidateRevision is not null) writer.WriteString("candidateRevision", value.CandidateRevision);
        if (value.RenderBackend is not null) writer.WriteString("renderBackend", value.RenderBackend);
        writer.WriteEndObject();
    }
}

/// <summary>完整响应、展开与像素预算；任何超限整批失败。</summary>
public static class SceneMeasurementContract
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int MaximumElements = 4096;
    public const int MaximumCorners = 20000;
    public const string Profile = "scene-measurement-v1";
    public const string LayoutProfile = "layout-reference-v1";
    public const string TimedProfile = "time-v1";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64 };

    public static void ValidateRequest(SceneMeasurementRequest request)
    {
        if (request.RenderBackend is not (null or "cpu" or "gpu")
            || !long.TryParse(request.ExpectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long revision)
            || revision < 0 || revision.ToString(CultureInfo.InvariantCulture) != request.ExpectedRevision
            || request.Mode is not ("time" or "layout") || request.AlphaMode is not ("opaque" or "transparent")
            || request.Width is < 1 or > 1280 || request.Height is < 1 or > 720 || (long)request.Width * request.Height * 4 > 4 * 1024 * 1024
            || request.Mode == "layout" && request.TimeUs is not null
            || request.Mode == "time" && (!long.TryParse(request.TimeUs, NumberStyles.None, CultureInfo.InvariantCulture, out long time)
                || time < 0 || time.ToString(CultureInfo.InvariantCulture) != request.TimeUs))
            throw new ArgumentException("测量请求无效。");
        bool hasPreview = request.PreviewId is not null || request.ExpectedPreviewGeneration is not null || request.CandidateRevision is not null;
        if (hasPreview != (request.PreviewId is not null && request.ExpectedPreviewGeneration is not null && request.CandidateRevision is not null)
            || hasPreview && (string.IsNullOrWhiteSpace(request.PreviewId) || string.IsNullOrWhiteSpace(request.CandidateRevision)
                || request.PreviewId.Length > 256 || request.CandidateRevision.Length > 256
                || !long.TryParse(request.ExpectedPreviewGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
                || generation < 0 || generation.ToString(CultureInfo.InvariantCulture) != request.ExpectedPreviewGeneration))
            throw new ArgumentException("候选预览必须同时提供previewId、expectedPreviewGeneration与candidateRevision。");
    }

    public static bool IsValidPng(ReadOnlySpan<byte> png, int width, int height) => PreviewPngValidation.IsValid(png, width, height);

    public static byte[] EncodeBounded<T>(T value)
    {
        using var stream = new BudgetStream(MaximumResponseBytes);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        return stream.ToArray();
    }

    private sealed class BudgetStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count) { if (Position + count > maximum) throw new WorkerProtocolException("完整测量响应超过2MiB预算。", "SceneMeasurementBudgetExceeded"); }
    }
}
