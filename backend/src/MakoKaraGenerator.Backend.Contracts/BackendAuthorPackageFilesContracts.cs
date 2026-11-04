using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定包文件描述；csharp/shader/shaderProgram/graph/svg/svgResource 文本 role 返回 editable=true 和 text。</summary>
public sealed record AuthorPackageFileDto(
    string Path,
    string Role,
    long SizeBytes,
    string Sha256,
    bool Editable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);

/// <summary>精确草稿修订下的完整清单和包文件描述。</summary>
public sealed record AuthorPackageFilesResponse(
    string DraftId,
    string DraftRevision,
    string SourceRevision,
    string PackageId,
    string PackageRevision,
    string ManifestJson,
    AuthorPackageFileDto[] Files,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>清单声明的可编辑文本文件；须完整提交全部可编辑文件。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorPackageTextFileDto(string Path, string Text);

/// <summary>包编辑时原子绑定新源码修订的完整作者配置；包身份和源码修订由服务派生。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorPackageConfigurationInputDto(
    string Name,
    string ExportId,
    string ParametersJson,
    TemplateEffectTargetDto RelativeTarget,
    TemplateScriptReadScopeDto ReadScope,
    string? BranchInput,
    string? FixedSeed,
    TemplateEffectOverlayLayerDto OverlayLayer);

/// <summary>按精确草稿修订保存清单与完整文本集合；配置可与包结构迁移原子提交。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorPackageFilesRequest(
    string ExpectedDraftRevision,
    string ManifestJson,
    AuthorPackageTextFileDto[] TextFiles,
    AuthorPackageConfigurationInputDto? Configuration = null,
    RequestMetadataDto? Metadata = null);
