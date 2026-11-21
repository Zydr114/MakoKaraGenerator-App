using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>工程命令成功回执的公开有界通知预算。</summary>
public static class ProjectCommandLimits
{
    /// <summary>完整 changeSet 的默认 JSON 最多16KiB；超过时返回完整失效通知并清空全部目标ID。</summary>
    public const int MaxChangeSetBytes = 16 * 1024;
}

/// <summary>将精确播放头记为元素起点；媒体边界和估算终点沿共享 Application 规则。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record StampTokenStartCommandDto(LyricTimingTargetDto? Target, string? PlayheadUs)
    : ProjectCommandDto("lyrics.stampTokenStart", Target);

/// <summary>将精确播放头记为元素终点，并明确确认用户编辑状态。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record StampTokenEndCommandDto(LyricTimingTargetDto? Target, string? PlayheadUs)
    : ProjectCommandDto("lyrics.stampTokenEnd", Target);

/// <summary>清除一个稳定元素的时间及时间来源状态。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ClearTokenTimeCommandDto(LyricTimingTargetDto? Target)
    : ProjectCommandDto("lyrics.clearTokenTime", Target);

/// <summary>按当前工程有理帧率微调稳定元素；每个 HTTP 命令为一次独立历史。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record NudgeTokenTimeCommandDto(LyricTimingTargetDto? Target, int? FrameDelta)
    : ProjectCommandDto("lyrics.nudgeTokenTime", Target);

/// <summary>原子平移整行与其中已定时元素，精确微秒允许有符号十进制字符串。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ShiftLineCommandDto(LyricTimingTargetDto? Target, string? DeltaUs)
    : ProjectCommandDto("lyrics.shiftLine", Target);

/// <summary>元素批量时间条目；不接受行目标或部分端点。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TokenTimeChangeDto(LyricTimingTargetDto? Target, string? StartUs, string? EndUs);

/// <summary>一次原子时间批次；沿共享批次用例不额外应用媒体时长上界。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetTokenTimesCommandDto(TokenTimeChangeDto[]? Changes)
    : ProjectCommandDto("lyrics.setTokenTimes");

/// <summary>打点与连打操作的真实共享用例结果；其他命令不返回此字段。</summary>
public sealed record LyricTimingCommandResultDto(bool EstimatedPlaceholder,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? CloseGuardApplied = null);

[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOrchestrationDefaultContainerCommandDto(string? BranchId, string? ContainerId)
    : ProjectCommandDto("orchestration.setDefaultContainer");

public sealed record OrchestrationCommandResultDto(string BranchId, string ContainerId, bool Changed);

/// <summary>设置已有歌词容器的内部布局默认位置；显式 null 清除该位置。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOrchestrationLayoutDefaultPlacementCommandDto(string? ContainerId,
    ProjectOrchestrationPlacementDto? Placement)
    : ProjectCommandDto("orchestration.setLayoutDefaultPlacement");

/// <summary>内部布局默认位置命令结果。</summary>
public sealed record OrchestrationLayoutCommandResultDto(string ContainerId, bool Changed);

/// <summary>设置节点名称；稳定节点身份及其结构引用保持不变。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOrchestrationNodeCommandDto(string? NodeId, string? Name)
    : ProjectCommandDto("orchestration.setNode");

/// <summary>在保持画布外观的前提下重挂节点。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ReparentOrchestrationNodeCommandDto(string? NodeId, string? ParentId)
    : ProjectCommandDto("orchestration.reparent");

/// <summary>重排同一父节点下的全部直接子节点。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ReorderOrchestrationChildrenCommandDto(string? ParentId, string[]? OrderedIds)
    : ProjectCommandDto("orchestration.reorderChildren");

/// <summary>向槽位容器增加一个空槽位。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record AddOrchestrationSlotCommandDto(string? ContainerId, string? SlotId, string? Name)
    : ProjectCommandDto("orchestration.addSlot");

/// <summary>删除节点及其未被引用的子树。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record RemoveOrchestrationNodeCommandDto(string? NodeId)
    : ProjectCommandDto("orchestration.removeNode");

/// <summary>明确设置槽位容器的完整槽位顺序。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ReorderOrchestrationSlotsCommandDto(string? ContainerId, string[]? OrderedIds)
    : ProjectCommandDto("orchestration.reorderSlots");

/// <summary>结构编辑命令成功回执。</summary>
public sealed record OrchestrationStructureCommandResultDto(string Operation, string? NodeId,
    string? ParentId, string? ContainerId, string[]? OrderedIds, bool Changed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? CreatedNodeIds = null);

/// <summary>在指定来源中创建一个显式展示片段；片段身份由调用方提供以支持精确重试。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record CreateOrchestrationClipCommandDto(string? BranchId, string? ClipId, string? Name,
    string[]? LineIds, string? ContainerId) : ProjectCommandDto("orchestration.createClip");

/// <summary>修改显式展示片段名称；name 必须在 JSON 中出现，可用显式 null 清除名称。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record RenameOrchestrationClipCommandDto(string? BranchId, string? ClipId, string? Name)
    : ProjectCommandDto("orchestration.renameClip");

/// <summary>把显式展示片段换到当前来源下的另一个歌词容器。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetOrchestrationClipContainerCommandDto(string? BranchId, string? ClipId, string? ContainerId)
    : ProjectCommandDto("orchestration.setClipContainer");

/// <summary>删除显式展示片段；其成员由核心编辑器回到来源默认容器。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record DeleteOrchestrationClipCommandDto(string? BranchId, string? ClipId)
    : ProjectCommandDto("orchestration.deleteClip");

/// <summary>显式展示片段命令成功回执。</summary>
public sealed record OrchestrationClipCommandResultDto(string BranchId, string ClipId, string? Name,
    string[] LineIds, string ContainerId, bool Changed);

/// <summary>整来源快速布局的纯预演或一次历史应用；省略 SelectedLineIds 表示该来源全部歌词。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record BasicLayoutCommandDto(string? BranchId, string? Preset,
    string[]? SelectedLineIds, string? LayoutName, string[]? SlotNames,
    string? SelectedSegmentName, string? RetainedSegmentName, bool Preview)
    : ProjectCommandDto(Preview ? "orchestration.previewBasicLayout" : "orchestration.applyBasicLayout");

/// <summary>快速布局固定失败编号和预演／应用结果。</summary>
public sealed record BasicLayoutCommandResultDto(string BranchId, string Preset, string Scope,
    string? RecognizedPreset, int AffectedLines, int ExistingClips, int PreservedNodes, int SplitClips, bool Changed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ClipIds = null);
