using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>设置已有节点的外层几何；参考尺寸和位置均为相对父参考矩形的值。</summary>
public sealed record SetOrchestrationNodeGeometryCommandDto(string? NodeId,
    ProjectOrchestrationPlacementDto? Placement, ProjectOrchestrationReferenceRectDto? ReferenceRect)
    : ProjectCommandDto("orchestration.setNodeGeometry");

/// <summary>设置已有歌词布局的安全区与最大宽度，不更改布局类型和槽位身份。</summary>
public sealed record SetOrchestrationLayoutSettingsCommandDto(string? ContainerId,
    ProjectOrchestrationSafeAreaDto? SafeArea, double MaxWidthRatio)
    : ProjectCommandDto("orchestration.setLayoutSettings");

/// <summary>完整替换已有布局的槽位动画；null 仅能用于普通布局。</summary>
public sealed record SetOrchestrationSlotAnimationCommandDto(string? ContainerId,
    ProjectOrchestrationSlotAnimationDto? SlotAnimation) : ProjectCommandDto("orchestration.setSlotAnimation");

/// <summary>设置指定来源显示程序的命名动画策略；显式 null 恢复按来源角色解释。</summary>
public sealed record SetOrchestrationProgramAnimationPolicyCommandDto(string? BranchId,
    string? AnimationPolicy) : ProjectCommandDto("orchestration.setProgramAnimationPolicy");

/// <summary>动画通道；偏移单位为画布像素，旋转为度，缩放为正倍数。</summary>
public sealed record ProjectOrchestrationSceneTransformDto(double Opacity, double Scale,
    double OffsetXPx, double OffsetYPx, double RotationDegrees);

/// <summary>完整槽位调度和动画参数；微秒使用非负十进制字符串，不保存槽位位置。</summary>
public sealed record ProjectOrchestrationSlotAnimationDto(string RevealMode, double RevealProgress,
    string RevealDelayUs, string RevealLeadUs, int MaxVisibleLines, string ConflictPolicy, string RetentionPolicy,
    string EnterDurationUs, string ExitDurationUs, ProjectOrchestrationSceneTransformDto EnterFrom,
    ProjectOrchestrationSceneTransformDto Hold, ProjectOrchestrationSceneTransformDto ExitTo,
    string ExitEasing, string Easing);

/// <summary>基础编排属性写入结果；canonical 值通过精确修订编排读模型读取。</summary>
public sealed record OrchestrationPropertiesCommandResultDto(string Operation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NodeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BranchId, bool Changed);
