using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>模板库 HTTP 新意图预算；不反向限制旧库或完整服务端复制。</summary>
public static class TemplateLibraryLimits
{
    /// <summary>完整 command 的 canonical UTF-8 JSON 最大字节数，不含 metadata。</summary>
    public const int MaxIntentBytes = 256 * 1024;
    /// <summary>新增名称 UTF-16 code units。</summary>
    public const int MaxNameLength = 256;
    /// <summary>列表最大页大小。</summary>
    public const int MaxPageSize = 100;
}
/// <summary>逻辑库描述，不触发个人库读取。</summary>
public sealed record TemplateLibraryDescriptorDto(string LibraryId, string Name, bool ReadOnly);
/// <summary>Host/profile 的固定内置与个人逻辑库。</summary>
public sealed record TemplateLibrariesResponse(TemplateLibraryDescriptorDto[] Libraries, string ProtocolVersion = BackendProtocol.Version);
/// <summary>模板实际声明类型。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<TemplateKind>))]
public enum TemplateKind
{
    /// <summary>行模板。</summary>
    Line,
    /// <summary>元素模板。</summary>
    Token,
    /// <summary>组合模板。</summary>
    Composite,
}
/// <summary>有界模板摘要；能力检查覆盖完整未选样式。</summary>
/// <remarks>Properties 是与 styles.patch 相同的样式属性路径。</remarks>
public sealed record TemplateSummaryDto(string TemplateId, string Name, TemplateKind Kind, string TemplateRevision,
    string[] Properties, bool HasEffectRecipes, bool HasPackageMaterials, bool HasPackages);
/// <summary>完整静态定义；没有包、配方、运行身份或磁盘路径。</summary>
/// <remarks>Properties 是与 styles.patch 相同的样式属性路径。</remarks>
[JsonConverter(typeof(StaticTemplateDefinitionJsonConverter))]
public record StaticTemplateDefinitionDto(string Name, TemplateKind Kind, CanonicalStyleDto Style, string[] Properties);
/// <summary>完整可编辑定义；包内容由精确服务端引用固定，分享仍使用原mktpl格式。</summary>
[JsonConverter(typeof(FullTemplateDefinitionJsonConverter))]
public sealed record FullTemplateDefinitionDto(string Name, TemplateKind Kind, CanonicalStyleDto Style,
    string[] Properties, TemplateEffectRecipeDto[] EffectRecipes, EffectPackageReferenceDto[] Packages)
    : StaticTemplateDefinitionDto(Name, Kind, Style, Properties);
/// <summary>原模板配方的完整值；种子用十进制字符串保持UInt64精度。</summary>
[JsonConverter(typeof(TemplateEffectRecipeJsonConverter))]
public sealed record TemplateEffectRecipeDto(string RecipeId, string Name, TemplateEffectTargetDto Target,
    string PackageId, string Revision, string ExportId, string ParametersJson,
    TemplateEffectOverlayLayerDto OverlayLayer, bool Enabled, TemplateScriptReadScopeDto ReadScope,
    string? BranchInput, string? FixedSeed, string? ConfigurationId);
/// <summary>模板配方相对目标。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<TemplateEffectTargetDto>))]
public enum TemplateEffectTargetDto { Line, TokenFamily, Application }
/// <summary>图元层覆盖。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<TemplateEffectOverlayLayerDto>))]
public enum TemplateEffectOverlayLayerDto { Package, BehindText, AboveText }
/// <summary>配方读取范围，独立于输出目标。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<TemplateScriptReadScopeDto>))]
public enum TemplateScriptReadScopeDto { BoundSources, Project, BranchInput }
/// <summary>来自同一次库读取的摘要窗口。</summary>
public sealed record TemplateListResponse(string LibraryId, string Revision, int Offset, int TotalCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextOffset,
    TemplateSummaryDto[] Templates, string ProtocolVersion = BackendProtocol.Version);
/// <summary>明确区分完整静态定义与暂不开放的高级定义。</summary>
[JsonConverter(typeof(TemplateDetailResponseJsonConverter))]
public sealed record TemplateDetailResponse(string LibraryId, string Revision, TemplateSummaryDto Summary,
    string DefinitionAvailability, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StaticTemplateDefinitionDto? Definition,
    string ProtocolVersion = BackendProtocol.Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefinitionFormat = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EffectPackageInspectResponse[]? PackageClosureDetails = null);
/// <summary>独立模板库修改意图，唯一 type 与派生 DTO 绑定。</summary>
[JsonConverter(typeof(TemplateLibraryCommandJsonConverter))]
public abstract record TemplateLibraryCommandDto(string Type);
/// <summary>新建静态模板，身份由回执执行 owner 生成一次。</summary>
[JsonConverter(typeof(TemplateLibraryCommandJsonConverter))]
public sealed record CreateTemplateCommandDto(StaticTemplateDefinitionDto Definition, string? DefinitionFormat = null) : TemplateLibraryCommandDto("create");
/// <summary>整体替换既有静态定义；高级替换不在本能力内。</summary>
[JsonConverter(typeof(TemplateLibraryCommandJsonConverter))]
public sealed record ReplaceTemplateCommandDto(string TemplateId, StaticTemplateDefinitionDto Definition, string? DefinitionFormat = null) : TemplateLibraryCommandDto("replace");
/// <summary>服务器固定源快照与完整闭包后复制到个人库。</summary>
[JsonConverter(typeof(TemplateLibraryCommandJsonConverter))]
public sealed record CopyTemplateCommandDto(string SourceLibraryId, string SourceRevision, string TemplateId, string Name) : TemplateLibraryCommandDto("copy");
/// <summary>仅改名称，保完整高级字段和闭包。</summary>
[JsonConverter(typeof(TemplateLibraryCommandJsonConverter))]
public sealed record RenameTemplateCommandDto(string TemplateId, string Name) : TemplateLibraryCommandDto("rename");
/// <summary>删除个人模板，不自动清理历史包文件。</summary>
[JsonConverter(typeof(TemplateLibraryCommandJsonConverter))]
public sealed record DeleteTemplateCommandDto(string TemplateId) : TemplateLibraryCommandDto("delete");
/// <summary>严格 CAS 与回执元信息；三个成员均必填且非空 JSON 对象/字符串。</summary>
[JsonConverter(typeof(TemplateLibraryCommandRequestJsonConverter))]
public sealed record TemplateLibraryCommandRequest(string ExpectedRevision, RequestMetadataDto Metadata, TemplateLibraryCommandDto Command);
/// <summary>小型完整修改回执；删除仍返回被删身份，无工程历史或事件。</summary>
public sealed record TemplateLibraryCommandResponse(string LibraryId, string PreviousRevision, string Revision,
    bool Changed, string TemplateId, string ProtocolVersion = BackendProtocol.Version);
