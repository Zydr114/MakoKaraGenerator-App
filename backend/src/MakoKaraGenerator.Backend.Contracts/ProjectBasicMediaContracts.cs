using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>将当前工程音轨替换为当前客户端已授权的固定资源。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ReplaceProjectAudioAssetCommandDto(string? AssetId) : ProjectCommandDto("audio.replaceAsset");
/// <summary>清除当前工程音轨、同源分析与波形。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ClearProjectAudioCommandDto() : ProjectCommandDto("audio.clear");
/// <summary>修改工程显示名称，不移动工程目录。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record RenameProjectCommandDto(string? Name) : ProjectCommandDto("project.rename");

/// <summary>同一精确修订下，工程实际引用的资源页。</summary>
public sealed record ProjectResourcesResponse(string ProjectSessionId, string ProjectRevision, int Offset,
    int TotalCount, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextOffset,
    ProjectResourceDto[] Resources, string ProtocolVersion = BackendProtocol.Version);

/// <summary>固定效果包的有限闭包摘要；不代表包已编译或可执行。</summary>
public sealed record ProjectPackageClosureSummaryDto(string PackageId, string PackageRevision,
    string ManifestSizeBytes, string ManifestSha256, string TotalSizeBytes, int FileCount,
    int ValidatedFileCount, int UnsupportedFileCount, string Status);

/// <summary>授权资源的公开身份；不包含主机路径或访问凭证。</summary>
public sealed record ProjectResourceDto(string ResourceId, string Kind, string[] Roles, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SizeBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AssetId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreparationStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WaveformStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackageId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackageRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackagePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackageRole = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectPackageClosureSummaryDto? PackageClosure = null);
