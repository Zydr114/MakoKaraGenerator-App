using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定包的受控来源；asset 和 library 字段按 kind 互斥，不含机器路径。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EffectPackageReferenceDto(string Kind, string PackageId, string PackageRevision,
    string? PackageAssetId = null, string? LibraryId = null, string? LibraryRevision = null,
    string? TemplateId = null, string? TemplateRevision = null, string? ConfigurationId = null,
    string? ConfigurationRevision = null);

/// <summary>只读包检查；授权 asset 或完整 reference 二选一。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EffectPackageInspectRequest(string? PackageAssetId = null,
    string? ExpectedPackageRevision = null, EffectPackageReferenceDto? Reference = null);

public sealed record EffectPackageFileSummaryDto(string Path, string Role, long Size, string Sha256);
public sealed record EffectPackageParameterOptionDto(string Value, string Label);
/// <summary>原包参数类型及精确 JSON 文本默认值/边界，浏览器读取时不丢失 Int64。</summary>
public sealed record EffectPackageParameterSchemaDto(string Id, string Type, string Label, string? Unit,
    string DefaultValueJson, string? MinimumJson, string? MaximumJson, EffectPackageParameterOptionDto[] Options);
/// <summary>保持完整原导出声明；bindingKinds 为派生的便捷查询。</summary>
public sealed record EffectPackageExportSchemaDto(string Id, string Kind, string Name, string[] ParameterIds,
    string[] BindingKinds, JsonElement Declaration);
/// <summary>图文件的原 inputs 声明及包内逻辑路径。</summary>
public sealed record EffectPackageInputsDto(string Path, JsonElement Inputs);
public sealed record EffectPackageStructureDiagnosticDto(string Code, string Path, string Message);
public sealed record EffectPackageInspectResponse(EffectPackageReferenceDto Reference,
    string PackageId, string PackageRevision, string Name, string Version, JsonElement Manifest, string ManifestJson,
    bool StructureValid, EffectPackageParameterSchemaDto[] Parameters, EffectPackageExportSchemaDto[] Exports,
    string[] BindingKinds, EffectPackageInputsDto[] Inputs, EffectPackageFileSummaryDto[] Files,
    EffectPackageStructureDiagnosticDto[] Diagnostics, string ProtocolVersion = BackendProtocol.Version);
