using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

public sealed record LeadInGenerationSettingsDto(bool IncludeOpening, bool IncludeLongGaps, string GapThresholdUs,
    string DurationUs, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IndicatorContainerId, string[] ExcludedLineIds);
public sealed record SetLeadInGenerationCommandDto(string? BranchId, LeadInGenerationSettingsDto? Settings)
    : ProjectCommandDto("orchestration.setLeadInGeneration");
public sealed record PreviewLeadInGenerationCommandDto(string? BranchId, LeadInGenerationSettingsDto? Settings)
    : ProjectCommandDto("orchestration.previewLeadInGeneration");
public sealed record ApplyLeadInGenerationCommandDto(string? BranchId, LeadInGenerationSettingsDto? Settings, string[]? SelectedLineIds)
    : ProjectCommandDto("orchestration.applyLeadInGeneration");
public sealed record DeleteLeadInAndExcludeCommandDto(string? BranchId, string? LeadInId)
    : ProjectCommandDto("orchestration.deleteLeadInAndExclude");
public sealed record SetLeadInAttachmentCommandDto(string? BranchId, string? LeadInId, string? ContainerId, string? Mode)
    : ProjectCommandDto("orchestration.setLeadInAttachment");
public sealed record AddOrchestrationMarkCommandDto(string? ContainerId, string? Text) : ProjectCommandDto("orchestration.addMark");
public sealed record SetOrchestrationMarkTextCommandDto(string? NodeId, string? Text) : ProjectCommandDto("orchestration.setMarkText");
public sealed record ReorderOrchestrationMarksCommandDto(string? ContainerId, string[]? OrderedIds) : ProjectCommandDto("orchestration.reorderMarks");
public sealed record SpaceOrchestrationMarksCommandDto(string? ContainerId) : ProjectCommandDto("orchestration.spaceMarks");
public sealed record SceneTextAppearanceDto(StyleTypographyDto Typography, StyleFillDto PendingFill, StyleFillDto ElapsedFill,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleStrokeDto? Stroke,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleShadowDto? Shadow, float Opacity);
public sealed record SetCountdownAppearanceCommandDto(string? ContainerId, SceneTextAppearanceDto? Appearance)
    : ProjectCommandDto("orchestration.setCountdownAppearance");
public sealed record SetMarkAppearanceCommandDto(string? NodeId, StyleChangeDto[]? Changes)
    : ProjectCommandDto("orchestration.setMarkAppearance");
public sealed record LeadInGenerationAnchorDto(string TimeUs, string TimingStatus, bool UsesLineTime, bool HasMissingPrefix);
public sealed record LeadInGenerationCandidateDto(string TargetLineId, string Status, string Reason,
    ProjectOrchestrationLeadInDto? Existing, ProjectOrchestrationLeadInDto? Proposed, string? StartUs, string? EndUs,
    LeadInGenerationAnchorDto? Anchor);
public sealed record LeadInGenerationDiagnosticDto(string Code, string Severity, string Message, string? Location);
public sealed record LeadInGenerationCommandResultDto(string Operation, string BranchId, LeadInGenerationSettingsDto? Settings,
    LeadInGenerationCandidateDto[] Candidates, LeadInGenerationDiagnosticDto[] Diagnostics, string[] SelectedLineIds, bool Changed);
