namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>创建由 Core 生成稳定身份的提示容器，标记数限制为1..64。</summary>
public sealed record CreateOrchestrationCountdownCommandDto(string? ParentId, string? Name, int? Count)
    : ProjectCommandDto("orchestration.createCountdown");
/// <summary>明确创建一条手工前奏。</summary>
public sealed record CreateOrchestrationLeadInCommandDto(string? BranchId, string? LeadInId, string? Name,
    string? TargetLineId, string? IndicatorContainerId, string? DurationUs, bool? Enabled)
    : ProjectCommandDto("orchestration.createLeadIn");
/// <summary>完整替换一条前奏的可编辑字段，实际修改转为手工来源。</summary>
public sealed record UpdateOrchestrationLeadInCommandDto(string? BranchId, string? LeadInId, string? Name,
    string? TargetLineId, string? IndicatorContainerId, string? DurationUs, bool? Enabled)
    : ProjectCommandDto("orchestration.updateLeadIn");
/// <summary>明确删除前奏，保留其提示容器和自动生成设置。</summary>
public sealed record DeleteOrchestrationLeadInCommandDto(string? BranchId, string? LeadInId)
    : ProjectCommandDto("orchestration.deleteLeadIn");
/// <summary>前奏完整配置与 Core 派生时间；不会开启自动生成。</summary>
public sealed record ProjectOrchestrationLeadInDto(string Id, string? Name, bool Enabled, string TargetLineId,
    string IndicatorContainerId, string DurationUs, string Origin, string? StartUs, string? EndUs);
/// <summary>提示组及标记的有限内容投影。</summary>
public sealed record ProjectOrchestrationContentDto(string[]? MarkNodeIds, string? Text, string? AttachmentMode,
    SceneTextAppearanceDto? Appearance = null, StyleChangeDto[]? AppearanceOverride = null);
/// <summary>前奏命令的稳定身份回执。</summary>
public sealed record OrchestrationLeadInCommandResultDto(string Operation, string? BranchId, string? LeadInId,
    string? IndicatorContainerId, string[]? MarkNodeIds, bool Changed);
