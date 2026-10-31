using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>请求将固定工程包文件惰性投影为当前客户端的受控 asset。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectPackageResourceAssetRequest(
    string ExpectedRevision, string ResourceId, RequestMetadataDto? Metadata = null);

/// <summary>工程包内资源通过现有受授权 asset 读取时的固定投影。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectPackageResourceAssetResponse(
    string ProjectSessionId,
    string ProjectRevision,
    string ResourceId,
    string PackageId,
    string PackageRevision,
    string PackagePath,
    string PackageRole,
    AssetResponse Asset,
    string ContentUrl,
    string ProtocolVersion = BackendProtocol.Version);
