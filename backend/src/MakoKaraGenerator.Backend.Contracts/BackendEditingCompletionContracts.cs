using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>在已有歌词容器上明确切换内部布局；旧节点始终保留。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOrchestrationLayoutKindCommandDto(string? ContainerId, string? Kind,
    string[]? SlotIds, int? NewSlotCount) : ProjectCommandDto("orchestration.setLayoutKind");

/// <summary>已有三通道行进入动画；时长为微秒，纵向偏移为画布像素。</summary>
public sealed record ProjectLineEnterAnimationDto(string DurationUs, string Easing,
    float StartOpacity, float StartScale, float StartOffsetPx);

/// <summary>设置本句进入动画；显式 null 恢复继承工程默认。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetLineEnterAnimationCommandDto(string? BranchId, string? LineId,
    ProjectLineEnterAnimationDto? Animation) : ProjectCommandDto("orchestration.setLineEnterAnimation");

/// <summary>设置工程默认进入动画；显式 null 关闭工程默认。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetProjectEnterAnimationCommandDto(ProjectLineEnterAnimationDto? Animation)
    : ProjectCommandDto("orchestration.setProjectEnterAnimation");

/// <summary>精确工程修订下的本句局部展示与进入动画继承；不是最终帧几何。</summary>
public sealed record ProjectLinePresentationResponse(string ProjectSessionId, string ProjectRevision,
    string BranchId, string LineId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineLocalTransformDto? LocalTransform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineEnterAnimationDto? LocalEnterAnimation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineEnterAnimationDto? EffectiveEnterAnimation,
    string AnimationSource, string ProtocolVersion = BackendProtocol.Version);
