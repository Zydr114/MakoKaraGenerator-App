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

/// <summary>完整作者配置；包身份和源码修订由服务派生。采用命令按目标源码校验，SDK2 package-files 按当前在用源码校验。</summary>
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

/// <summary>整份替换基准修订里一个图片或字体文件：path 不变，字节取自本客户端上传的资源。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorPackageFileReplacementDto(string Path, string AssetId);

/// <summary>
/// 按精确草稿修订保存清单与完整文本集合；SDK2 只保存候选源码，可选配置仍按当前在用源码校验。迁移使用 adopt 的完整配置。
/// 不可编辑文件取自 <see cref="BaseSourceRevision" />（省略时为最新修订），<see cref="ReplacedFiles" /> 列出的图片或字体换成新字节。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorPackageFilesRequest(
    string ExpectedDraftRevision,
    string ManifestJson,
    AuthorPackageTextFileDto[] TextFiles,
    AuthorPackageConfigurationInputDto? Configuration = null,
    RequestMetadataDto? Metadata = null,
    AuthorPackageFileReplacementDto[]? ReplacedFiles = null,
    string? BaseSourceRevision = null);

/// <summary>只读检查：草稿某个修订里的图片或字体换成一个已上传资源会怎样；不改变草稿。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorPackageFileReplacementInspectionRequest(
    string ExpectedDraftRevision,
    string SourceRevision,
    string Path,
    string AssetId);

/// <summary>替换检查里一份资源的信息；图片带宽高，字体带家族名。</summary>
public sealed record AuthorPackageResourceInfoDto(
    string Format,
    long SizeBytes,
    string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Width = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Height = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? FamilyNames = null);

/// <summary>替换提醒：imageSizeChanged 或 fontFamilyChanged；不阻止保存。</summary>
public sealed record AuthorPackageReplacementWarningDto(string Code, string Message);

/// <summary>替换检查结果：原文件、新文件和提醒。</summary>
public sealed record AuthorPackageFileReplacementInspectionResponse(
    string DraftId,
    string DraftRevision,
    string SourceRevision,
    string Path,
    string Role,
    AuthorPackageResourceInfoDto Current,
    AuthorPackageResourceInfoDto Replacement,
    AuthorPackageReplacementWarningDto[] Warnings,
    string ProtocolVersion = BackendProtocol.Version);
