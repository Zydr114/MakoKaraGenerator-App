using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>内部歌词布局；Auto 根据显示程序的动画策略选择普通或静态流式布局。</summary>
public enum LyricContainerLayoutKind
{
    /// <summary>普通或静态流式布局。</summary>
    Auto,
    /// <summary>按稳定槽位节点序列轮换。</summary>
    SlotSequence,
}

/// <summary>唯一持久化的通用容器节点；纯组可以没有内部歌词布局。</summary>
public sealed record SceneContainerDefinition
{
    /// <summary>稳定节点身份。</summary>
    public required string Id { get; init; }
    /// <summary>用户名称；未命名不改变稳定身份。</summary>
    public string? Name { get; init; }
    /// <summary>父容器；空值表示画布。</summary>
    public string? ParentId { get; init; }
    /// <summary>相对于父参考矩形的位置。</summary>
    public ScenePlacement Placement { get; init; } = ScenePlacement.Default;
    /// <summary>相对于父参考矩形的固定尺寸。</summary>
    public SceneReferenceRect ReferenceRect { get; init; } = SceneReferenceRect.Canvas;
    /// <summary>内部歌词布局；空值为纯组。</summary>
    public LyricContainerLayout? Layout { get; init; }
    /// <summary>节点自有内容；为空时仍是原有容器。</summary>
    public SceneContent? Content { get; init; }
    /// <summary>运行附着参考；位置只保存于 Placement。</summary>
    public SceneAttachment? Attachment { get; init; }
}

/// <summary>自动歌词布局配置；槽位位置仅存在于节点。</summary>
public sealed record LyricContainerLayout
{
    /// <summary>内部布局类型。</summary>
    public LyricContainerLayoutKind Kind { get; init; }
    /// <summary>普通行的默认局部位置。</summary>
    public ScenePlacement? DefaultPlacement { get; init; }
    /// <summary>流式布局安全区。</summary>
    public NormalizedSafeArea SafeArea { get; init; } = NormalizedSafeArea.Full;
    /// <summary>最大文本宽度比例。</summary>
    public double MaxWidthRatio { get; init; } = .9;
    /// <summary>按轮换次序保存的槽位节点 ID。</summary>
    public ImmutableArray<string> SlotIds { get; init; } = [];
    /// <summary>槽位调度及动画参数，不保存槽位位置。</summary>
    public SlotAnimationSpec? SlotAnimation { get; init; }
    /// <summary>配置内容相等。</summary>
    public bool Equals(LyricContainerLayout? other) => other is not null && Kind == other.Kind
        && DefaultPlacement == other.DefaultPlacement && SafeArea == other.SafeArea && MaxWidthRatio == other.MaxWidthRatio
        && SlotIds.SequenceEqual(other.SlotIds) && SlotAnimation == other.SlotAnimation;
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Kind); hash.Add(DefaultPlacement); hash.Add(SafeArea); hash.Add(MaxWidthRatio);
        hash.Add(SlotAnimation); foreach (var id in SlotIds) hash.Add(id); return hash.ToHashCode();
    }
}

/// <summary>通用显示片段；区间读取连续首末源成员的行时间。</summary>
public sealed record LyricDisplayClip
{
    /// <summary>稳定片段 ID。</summary>
    public required string Id { get; init; }
    /// <summary>用户片段名称。</summary>
    public string? Name { get; init; }
    /// <summary>按源顺序排列的连续行成员。</summary>
    public ImmutableArray<string> LineIds { get; init; } = [];
    /// <summary>显示该句的容器。</summary>
    public required string ContainerId { get; init; }
    /// <summary>拆分前的调度成员上下文；空值按当前成员独立调度，不保存时间或槽位副本。</summary>
    public LyricClipScheduleContext? ScheduleContext { get; init; }
    /// <summary>比较成员内容，避免数组引用导致无变化修改。</summary>
    public bool Equals(LyricDisplayClip? other) => other is not null && Id == other.Id && Name == other.Name
        && ContainerId == other.ContainerId && LineIds.SequenceEqual(other.LineIds) && ScheduleContext == other.ScheduleContext;
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Id); hash.Add(Name); hash.Add(ContainerId);
        foreach (string id in LineIds) hash.Add(id); hash.Add(ScheduleContext); return hash.ToHashCode();
    }
}

/// <summary>精确拆分保留的源成员序列；调度仍使用当前歌词时间和原容器规则。</summary>
public sealed record LyricClipScheduleContext
{
    /// <summary>原调度按源顺序读取的成员；可能包含未打轴行，不制造时间。</summary>
    public ImmutableArray<string> LineIds { get; init; } = [];
    /// <summary>是否来自默认段；默认段以相邻片段边界而不是成员首末时间裁切。</summary>
    public bool IsDefaultSegment { get; init; }
    /// <summary>默认段起点的前一片段末行；空值从零开始。</summary>
    public string? PreviousLineId { get; init; }
    /// <summary>默认段终点的后一片段首行；空值没有结束边界。</summary>
    public string? NextLineId { get; init; }
    /// <summary>按内容比较源成员。</summary>
    public bool Equals(LyricClipScheduleContext? other) => other is not null && LineIds.SequenceEqual(other.LineIds)
        && IsDefaultSegment == other.IsDefaultSegment && PreviousLineId == other.PreviousLineId && NextLineId == other.NextLineId;
    /// <inheritdoc/>
    public override int GetHashCode() { HashCode hash = new(); foreach (string id in LineIds) hash.Add(id); hash.Add(IsDefaultSegment); hash.Add(PreviousLineId); hash.Add(NextLineId); return hash.ToHashCode(); }
}

/// <summary>源歌词到通用容器树的显示程序。</summary>
public sealed record LyricDisplayProgram
{
    /// <summary>稳定程序 ID。</summary>
    public required string Id { get; init; }
    /// <summary>源歌词分支。</summary>
    public required string BranchId { get; init; }
    /// <summary>编辑该程序整体位置的共同根组。</summary>
    public required string RootContainerId { get; init; }
    /// <summary>未加入显式片段的歌词默认使用此容器。</summary>
    public required string DefaultContainerId { get; init; }
    /// <summary>动画策略；空值按源角色解释。</summary>
    public Lyrics.LyricBranchAnimationPolicy? AnimationPolicy { get; init; }
    /// <summary>显式展示片段；成员不再进入默认调度。</summary>
    public ImmutableArray<LyricDisplayClip> Clips { get; init; } = [];
    /// <summary>目标句的前奏准备条目。</summary>
    public ImmutableArray<LyricLeadIn> LeadIns { get; init; } = [];
    /// <summary>自动生成参数及明确排除项。</summary>
    public LeadInGenerationSettings? LeadInGeneration { get; init; }
    /// <summary>内容相等。</summary>
    public bool Equals(LyricDisplayProgram? other) => other is not null && Id == other.Id && BranchId == other.BranchId
        && RootContainerId == other.RootContainerId && DefaultContainerId == other.DefaultContainerId
        && AnimationPolicy == other.AnimationPolicy && Clips.SequenceEqual(other.Clips)
        && LeadIns.SequenceEqual(other.LeadIns) && LeadInGeneration == other.LeadInGeneration;
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Id); hash.Add(BranchId); hash.Add(RootContainerId); hash.Add(DefaultContainerId);
        hash.Add(AnimationPolicy); foreach (var clip in Clips) hash.Add(clip);
        foreach (var cue in LeadIns) hash.Add(cue); hash.Add(LeadInGeneration); return hash.ToHashCode();
    }
}

/// <summary>默认节点身份生成；源 ID 转义后不会与路径分隔符碰撞。</summary>
public static class LyricSceneIds
{
    /// <summary>默认展示根。</summary>
    public static string Root(string branchId) => "branch/" + Uri.EscapeDataString(branchId) + "/display";
    /// <summary>默认常规容器。</summary>
    public static string Regular(string branchId) => Root(branchId) + "/regular";
    /// <summary>默认槽位节点。</summary>
    public static string Slot(string branchId, string role) => Root(branchId) + "/slot/" + Uri.EscapeDataString(role);
}
