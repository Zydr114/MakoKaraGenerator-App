using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>Single-directory browsing and fixed LRC resource limits.</summary>
public static class FileLyricsLimits
{
    public const int MaxDirectoryEntries = 4096;
    public const int MaxDirectoryPageSize = 100;
    public const int MaxSourceLines = 10000;
    public const int MaxSourceTokens = 100000;
    public const int MaxSourceBytes = 4 * 1024 * 1024;
    public const int MaxContentBytes = 4 * 1024 * 1024;
    public const int MaxReportBytes = 64 * 1024;
    public const int MaxRetainedPerClient = 8;
    public const int MaxRetainedTotal = 32;
    public const int LifetimeSeconds = 300;
}

/// <summary>A direct child directory; IsProject only indicates a regular project entry file exists.</summary>
public sealed record ProjectDirectoryEntryDto(string Name, string RelativePath, bool IsProject);
/// <summary>Ordinally sorted, nonrecursive page. Separate requests do not share a filesystem snapshot.</summary>
public sealed record ProjectDirectoryEntriesResponse(string RootGrantId, string RelativePath, int Offset,
    int TotalCount, int? NextOffset, ProjectDirectoryEntryDto[] Entries, string ProtocolVersion = BackendProtocol.Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LyricsExportOptionsDto(string? Title = null, string? Artist = null, string? Album = null,
    string NewLine = "\n", bool WriteByteOrderMark = false, bool MillisecondPrecision = false);

[JsonConverter(typeof(LyricsExportPlanRequestJsonConverter))]
public sealed record LyricsExportPlanRequest(string ExpectedRevision, string? BranchId, string Format,
    LyricsExportOptionsDto Options, RequestMetadataDto? Metadata = null, string Scope = "branch");

/// <summary>Strict scope union: branch requires branchId; document forbids it and only accepts ttml.project.</summary>
public sealed class LyricsExportPlanRequestJsonConverter : JsonConverter<LyricsExportPlanRequest>
{
    public override LyricsExportPlanRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("导出计划必须为对象。");
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty field in root.EnumerateObject())
            if (field.Name is not ("expectedRevision" or "branchId" or "format" or "options" or "metadata" or "scope")
                || !fields.TryAdd(field.Name, field.Value)) throw new JsonException("导出计划含未知或重复字段。");
        string RequiredString(string name) => fields.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new JsonException("导出计划缺少必填字符串。");
        string scope = fields.ContainsKey("scope") ? RequiredString("scope") : "branch";
        string format = RequiredString("format");
        string? branch = scope switch
        {
            "branch" => RequiredString("branchId"),
            "document" when format == "ttml.project" && !fields.ContainsKey("branchId") => null,
            _ => throw new JsonException("歌词导出范围和来源字段组合无效。"),
        };
        if (!fields.TryGetValue("options", out JsonElement exportOptions) || exportOptions.ValueKind != JsonValueKind.Object)
            throw new JsonException("导出选项必须为对象。");
        RequestMetadataDto? metadata = fields.TryGetValue("metadata", out JsonElement value)
            ? value.Deserialize<RequestMetadataDto>(options) : null;
        return new(RequiredString("expectedRevision"), branch, format, exportOptions.Deserialize<LyricsExportOptionsDto>(options)!, metadata, scope);
    }
    public override void Write(Utf8JsonWriter writer, LyricsExportPlanRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("expectedRevision", value.ExpectedRevision);
        if (value.Scope != "document" || value.BranchId is not null) writer.WriteString("branchId", value.BranchId);
        writer.WriteString("format", value.Format); writer.WritePropertyName("options"); JsonSerializer.Serialize(writer, value.Options, options);
        writer.WriteString("scope", value.Scope);
        if (value.Metadata is not null) { writer.WritePropertyName("metadata"); JsonSerializer.Serialize(writer, value.Metadata, options); }
        writer.WriteEndObject();
    }
}

public sealed record LyricsExportLossDto(string Feature, int AffectedCount, string Message);
public sealed record LyricsExportIssueDto(string Kind, string LineId, string Message);
public sealed record LyricsExportAssessmentDto(LyricsExportLossDto[] Losses, int ExportableLineCount,
    int SkippedLineCount, LyricsExportIssueDto[] Issues, bool HasLosses);
/// <summary>The Core assessment follows Scope; BranchId is explicitly null for a complete document.</summary>
public sealed record LyricsExportPlanResponse(string PlanId, string ProjectSessionId, string ProjectRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BranchId, string Format, LyricsExportOptionsDto Options, string Encoding, DateTimeOffset ExpiresAtUtc,
    LyricsExportAssessmentDto Plan, LyricsExportLossDto[] AdditionalLosses, bool RequiresLossAcceptance,
    int OmittedBranchCount, string Scope = "branch", string ProtocolVersion = BackendProtocol.Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LyricsExportRequest([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string PlanId, [property: JsonRequired] bool AcceptLosses, RequestMetadataDto? Metadata = null);

/// <summary>Actual Core exporter counters and plan for the frozen branch or complete document.</summary>
public sealed record LyricsExportReportDto(string ExporterId, string ExporterName, int SourceLineCount,
    int ExportedLineCount, int SkippedLineCount, int DroppedTokenTimeCount, int RoundedTimeCount,
    long WrittenByteCount, int MetadataCount, LyricsExportAssessmentDto Plan,
    int WrittenRubySegmentCount = 0, int SkippedRubySegmentCount = 0, int DroppedRubySegmentTimeCount = 0);
public sealed record LyricsExportResponse(string ExportId, string PlanId, string ProjectSessionId,
    string ProjectRevision, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BranchId, string Format, string Encoding, long SizeBytes, string Sha256,
    string ContentUrl, DateTimeOffset ExpiresAtUtc, LyricsExportReportDto Report,
    LyricsExportLossDto[] AdditionalLosses, string ProtocolVersion = BackendProtocol.Version, string Scope = "branch");
