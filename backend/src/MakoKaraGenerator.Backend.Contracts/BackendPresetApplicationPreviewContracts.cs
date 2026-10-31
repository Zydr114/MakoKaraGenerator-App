using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>在精确工程修订上只读试看一次预设分配；成功后仍须调用正式 apply/update 命令。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresetApplicationPreviewRequest(
    string ExpectedProjectRevision,
    string? TemplateLibraryId,
    string? TemplateLibraryRevision,
    string? TemplateId,
    string? TemplateRevision,
    PresetApplicationTargetDto[] Targets,
    Dictionary<string, string>? BranchInputs,
    PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode = PreviewAlphaMode.Opaque,
    RequestMetadataDto? Metadata = null,
    TemplatePreviewSourceDto? Source = null,
    string? RenderBackend = null);
