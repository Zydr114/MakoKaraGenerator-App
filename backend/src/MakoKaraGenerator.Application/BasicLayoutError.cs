using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Application;

/// <summary>布局变换的失败原因；本地化由调用界面负责。</summary>
public enum BasicLayoutErrorCode
{
    /// <summary>未知布局预设。</summary>
    InvalidPreset,
    /// <summary>来源分支或展示程序缺失。</summary>
    SourceMissing,
    /// <summary>当前来源的计时／播放策略不支持槽位轮换快速布局。</summary>
    UnsupportedSource,
    /// <summary>空、重复或缺失成员。</summary>
    InvalidRange,
    /// <summary>源顺序不连续。</summary>
    DiscontiguousRange,
    /// <summary>正式展示树校验未通过。</summary>
    PresentationInvalid,
    /// <summary>范围配方不能安全拆分。</summary>
    RecipeConflict,
    /// <summary>既有片段引用无法重映射。</summary>
    ReferenceConflict,
    /// <summary>拆分改变了未选成员调度。</summary>
    ScheduleConflict,
    /// <summary>调度求值失败，保留后端原因。</summary>
    ScheduleFailure,
}

/// <summary>完整校验或引用冲突上下文，不包含界面标签。</summary>
public sealed record BasicLayoutError(BasicLayoutErrorCode Code)
{
    /// <summary>完整展示树校验报告。</summary>
    public ValidationReport? Validation { get; init; }
    /// <summary>不能拆分的固定预设名称。</summary>
    public string? TemplateName { get; init; }
    /// <summary>后端失败消息，不拼接界面提示。</summary>
    public string? Detail { get; init; }
}

/// <summary>调用者已经确定的新节点和片段名称；不更改已有工程可见名称。</summary>
public sealed record BasicLayoutNames(string LayoutName, ImmutableArray<string> SlotNames,
    string SelectedSegmentName, string RetainedSegmentName);
