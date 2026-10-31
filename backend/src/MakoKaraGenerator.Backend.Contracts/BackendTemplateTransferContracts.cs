using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>完整模板归档传输预算；解压内容独立于压缩输入计费。</summary>
public static class TemplateTransferLimits
{
    public const int MaxArchiveBytes = 256 * 1024 * 1024;
    public const int MaxExpandedBytes = 256 * 1024 * 1024;
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int MaxEntries = 1024;
    public const int MaxTemplates = 100;
    public const int MaxReportBytes = 64 * 1024;
    public const int MaxRetainedPerClient = 2;
    public const int MaxRetainedTotal = 8;
    public const long MaxRetainedBytes = 1024L * 1024 * 1024;
    public const int LifetimeSeconds = 300;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateImportPreflightRequest([property: JsonRequired] string AssetId, RequestMetadataDto? Metadata = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateImportRequest([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string PreflightId, RequestMetadataDto? Metadata = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateArchiveExportRequest([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string[] TemplateIds, RequestMetadataDto? Metadata = null);
/// <summary>高级模板完整保留，能力状态仅说明现有公开详情/执行范围。</summary>
public sealed record TemplateTransferSummaryDto(TemplateSummaryDto Summary, string DefinitionAvailability);
public sealed record TemplateImportPreflightResponse(string PreflightId, string AssetSha256, long ArchiveBytes,
    long ExpandedBytes, TemplateTransferSummaryDto[] Templates, DateTimeOffset ExpiresAtUtc,
    string IdentityPolicy = "generateNewIds", string ProtocolVersion = BackendProtocol.Version);
public sealed record TemplateImportIdentityDto(string SourceTemplateId, string TemplateId);
public sealed record TemplateImportResponse(string LibraryId, string PreviousRevision, string Revision,
    bool Changed, TemplateImportIdentityDto[] ImportedTemplates, string ProtocolVersion = BackendProtocol.Version);
public sealed record TemplateArchiveExportResponse(string ExportId, string LibraryId, string Revision,
    string[] TemplateIds, long SizeBytes, string Sha256, string ContentUrl, DateTimeOffset ExpiresAtUtc,
    string ProtocolVersion = BackendProtocol.Version);
