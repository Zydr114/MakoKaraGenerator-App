using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>无 GUI 脚本作者工作流的有限公共请求。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorDraftCreateRequest(
    string? PackageAssetId,
    string ExportId,
    string Name,
    RequestMetadataDto? Metadata = null,
    string? ConfigurationId = null,
    string? ExpectedConfigurationRevision = null);

/// <summary>提交一个完整包内 C# 源码集合，构造不可变源码修订。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorSourceRevisionRequest(
    string ExpectedDraftRevision,
    AuthorSourceFileDto[] Files,
    RequestMetadataDto? Metadata = null);

/// <summary>包内 C# 源码文件；路径必须来自当前固定包的 csharp role 文件。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorSourceFileDto(string Path, string Text);

/// <summary>固定指定保留源码修订并返回其编译诊断；省略源码修订时取最新修订。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorCompilationRequest(
    string ExpectedDraftRevision,
    RequestMetadataDto? Metadata = null,
    string? ExecutionMode = null,
    string? SourceRevision = null);

/// <summary>异步作者编译任务固定的草稿与源码修订。</summary>
public sealed record AuthorCompilationJobInputDto(
    string DraftId,
    string DraftRevision,
    string SourceRevision);

/// <summary>删除作者草稿并释放其固定包与客户端保护。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorDraftDeleteRequest(
    string ExpectedDraftRevision,
    RequestMetadataDto? Metadata = null);

/// <summary>显式采用当前草稿中的某个已固定源码修订。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorAdoptSourceRevisionRequest(
    string ExpectedDraftRevision,
    string SourceRevision,
    RequestMetadataDto? Metadata = null);

/// <summary>作者草稿配置写入；派生 kind/bindingKinds 不接受客户端覆盖。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorDraftConfigurationInputDto(
    string Name,
    string PackageId,
    string SourceRevision,
    string ExportId,
    string ParametersJson,
    TemplateEffectTargetDto RelativeTarget,
    TemplateScriptReadScopeDto ReadScope,
    string? BranchInput,
    string? FixedSeed,
    TemplateEffectOverlayLayerDto OverlayLayer);

/// <summary>精确草稿修订下保存完整作者配置。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorDraftConfigurationRequest(
    string ExpectedDraftRevision,
    AuthorDraftConfigurationInputDto Configuration,
    RequestMetadataDto? Metadata = null);

/// <summary>脚本作者草稿的持久状态摘要。</summary>
public sealed record AuthorDraftResponse(
    string DraftId,
    string DraftRevision,
    string Name,
    string PackageId,
    string BaseSourceRevision,
    string CurrentSourceRevision,
    string ExportId,
    string? AdoptedSourceRevision,
    bool CanCompile,
    bool CanAdopt,
    AuthorCompilationSummaryDto? LastCompilation = null,
    string ProtocolVersion = BackendProtocol.Version,
    AuthorDraftConfigurationDto? Configuration = null,
    AuthorDraftSampleOptionsDto? SampleOptions = null,
    int? PackageVersion = null,
    string? SdkVersion = null);

/// <summary>草稿列表；只包含当前客户端拥有的可恢复草稿。</summary>
public sealed record AuthorDraftListResponse(
    AuthorDraftResponse[] Drafts,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>作者草稿中固定源码修订的完整配置投影。</summary>
public sealed record AuthorDraftConfigurationDto(
    string Name,
    string PackageId,
    string PackageRevision,
    string SourceRevision,
    string ExportId,
    string ParametersJson,
    TemplateEffectTargetDto RelativeTarget,
    TemplateScriptReadScopeDto ReadScope,
    string? BranchInput,
    string? FixedSeed,
    TemplateEffectOverlayLayerDto OverlayLayer,
    string Kind,
    string[] BindingKinds);

/// <summary>作者配置可用的固定工坊样本、目标和材质槽位。</summary>
public sealed record AuthorDraftSampleOptionsDto(
    TemplateSampleOptionDto[] Samples,
    string[] MaterialSlots,
    string DefaultSampleId = TemplatePreviewLimits.SingleLineSampleId);

/// <summary>固定作者源码的完整 C# 文件集合和包身份。</summary>
public sealed record AuthorDraftSourcesResponse(
    string DraftId,
    string DraftRevision,
    string SourceRevision,
    string PackageId,
    string PackageRevision,
    AuthorSourceFileDto[] Files,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>源码修订提交结果；固定闭包已持久化，尚未自动采用。</summary>
public sealed record AuthorSourceRevisionResponse(
    string DraftId,
    string DraftRevision,
    string PackageId,
    string SourceRevision,
    string[] SourcePaths,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>编译任务结果；诊断绑定捕获的不可变源码修订。</summary>
public sealed record AuthorCompilationResponse(
    string DraftId,
    string DraftRevision,
    string SourceRevision,
    bool Succeeded,
    bool Stale,
    string ArtifactHash,
    AuthorCompilationDiagnosticDto[] Diagnostics,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>源码修订采用结果。</summary>
public sealed record AuthorAdoptSourceRevisionResponse(
    string DraftId,
    string DraftRevision,
    string PackageId,
    string PreviousSourceRevision,
    string AdoptedSourceRevision,
    string ExportId,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>编译结果摘要，供草稿读取使用。</summary>
public sealed record AuthorCompilationSummaryDto(
    string SourceRevision,
    bool Succeeded,
    bool Stale,
    string ArtifactHash,
    int DiagnosticCount,
    DateTimeOffset CompletedAtUtc);

/// <summary>草稿删除结果。</summary>
public sealed record AuthorDraftDeleteResponse(
    string DraftId,
    string DraftRevision,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>Roslyn 或包语义诊断，路径和行列均来自固定源码。</summary>
public sealed record AuthorCompilationDiagnosticDto(
    string Code,
    string Message,
    bool IsError,
    string? Path,
    int Line,
    int Column);
