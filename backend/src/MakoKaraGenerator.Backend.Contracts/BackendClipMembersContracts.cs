namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>替换现有片段的完整连续成员；保持身份、名称、容器和原调度上下文。</summary>
public sealed record SetOrchestrationClipMembersCommandDto(string? BranchId, string? ClipId, string[]? LineIds)
    : ProjectCommandDto("orchestration.setClipMembers");
/// <summary>在现有成员前拆分；旧身份保留左段，显式新身份用于右段。</summary>
public sealed record SplitOrchestrationClipCommandDto(string? BranchId, string? ClipId, string? SplitBeforeLineId, string? NewClipId)
    : ProjectCommandDto("orchestration.splitClip");
/// <summary>合并同容器、同调度上下文且相邻的完整片段；保留指定成员身份。</summary>
public sealed record MergeOrchestrationClipsCommandDto(string? BranchId, string[]? ClipIds, string? TargetClipId)
    : ProjectCommandDto("orchestration.mergeClips");

/// <summary>本次事务涉及的完整新片段和已删除身份；拆分新身份由调用者显式指定。</summary>
public sealed record OrchestrationClipMembersCommandResultDto(string Operation, string BranchId,
    ProjectOrchestrationClipDto[] Clips, string[] RemovedClipIds, bool Changed,
    string[] CreatedApplicationIds, string[] RemovedApplicationIds);
