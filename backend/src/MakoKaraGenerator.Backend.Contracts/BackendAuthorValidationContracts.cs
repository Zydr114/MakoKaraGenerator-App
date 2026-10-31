using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>作者草稿固定配置的正式运行试样；帧数由服务端的作者验证合同固定。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorValidationJobRequest(
    string ExpectedDraftRevision,
    string SampleId,
    string TargetKey,
    string? MaterialSlot = null,
    RequestMetadataDto? Metadata = null,
    string? RenderBackend = null);

/// <summary>作者验证任务捕获的全部身份；不依赖工程会话。</summary>
public sealed record AuthorValidationJobInputDto(
    string DraftId,
    string DraftRevision,
    string SourceRevision,
    string SampleId,
    string TargetKey,
    string? MaterialSlot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderBackend = null);
