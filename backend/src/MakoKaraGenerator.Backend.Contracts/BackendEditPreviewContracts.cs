using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定基础工程修订的纯数据编辑；数组顺序就是编辑顺序。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ApplyProjectEditsCommandDto(ProjectCommandDto[]? Edits) : ProjectCommandDto("project.applyEdits");

/// <summary>创建或替换同一预览会话中的固定编辑候选。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectEditPreviewRequest(string ExpectedRevision, ProjectCommandDto[] Edits, PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode = PreviewAlphaMode.Opaque, string? PreviewId = null,
    string? ExpectedPreviewGeneration = null, RequestMetadataDto? Metadata = null, string? RenderBackend = null);

/// <summary>保留各编辑原有的目标、选择及领域反馈；不把候选内部历史当工程历史。</summary>
public sealed record ProjectEditResultDto(int Index, string Type, bool Changed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricTimingCommandResultDto? TimingResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricContentCommandFeedbackDto? Feedback = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricBranchCommandResultDto? BranchResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricTokenCommandResultDto? TokenResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LyricsImportCommandResultDto? ImportResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StyleCommandResultDto? StyleResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresetApplicationCommandResultDto? PresetApplicationResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationCommandResultDto? OrchestrationResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationLayoutCommandResultDto? LayoutResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationStructureCommandResultDto? StructureResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BasicLayoutCommandResultDto? BasicLayoutResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationClipCommandResultDto? ClipResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationLeadInCommandResultDto? LeadInResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LineLocalTransformCommandResultDto? LineLocalTransformResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationPropertiesCommandResultDto? PropertiesResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LeadInGenerationCommandResultDto? LeadInGenerationResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrchestrationClipMembersCommandResultDto? ClipMembersResult = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RubySegmentCommandResultDto? RubyResult = null);

/// <summary>固定候选的身份和原编辑反馈；sourceRevision 与 candidateRevision 相同。</summary>
public sealed record ProjectEditCandidateDto(string ProjectSessionId, string BaseProjectRevision, string CandidateRevision,
    ProjectChangeSetDto ChangeSet, ProjectEditResultDto[] EditResults);
