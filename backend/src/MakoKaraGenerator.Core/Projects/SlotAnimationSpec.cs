namespace MakoKaraGenerator.Core.Projects;

/// <summary>独立于槽位节点的调度参数。</summary>
public sealed record SlotAnimationSpec
{
    /// <summary>提前显示触发方式。</summary>
    public SlotRevealMode RevealMode { get; init; } = SlotRevealMode.Progress;
    /// <summary>进度触发阈值。</summary>
    public double RevealProgress { get; init; } = .5;
    /// <summary>触发延迟微秒。</summary>
    public long RevealDelayUs { get; init; }
    /// <summary>提前显示微秒。</summary>
    public long RevealLeadUs { get; init; }
    /// <summary>同屏行数上限。</summary>
    public int MaxVisibleLines { get; init; } = 2;
    /// <summary>槽位冲突策略。</summary>
    public SlotConflictPolicy ConflictPolicy { get; init; } = SlotConflictPolicy.QueueUntilSlotFree;
    /// <summary>保留策略。</summary>
    public SlotRetentionPolicy RetentionPolicy { get; init; } = SlotRetentionPolicy.UntilLineEnd;
    /// <summary>从现有调度配置读取参数，不复制槽位位置。</summary>
    public static SlotAnimationSpec FromProfile(SlotSequenceAnimationProfile profile) => new()
    {
        RevealMode = profile.RevealMode,
        RevealProgress = profile.RevealProgress,
        RevealDelayUs = profile.RevealDelayUs,
        RevealLeadUs = profile.RevealLeadUs,
        MaxVisibleLines = profile.MaxVisibleLines,
        ConflictPolicy = profile.ConflictPolicy,
        RetentionPolicy = profile.RetentionPolicy,
    };
    /// <summary>为现有纯调度器建立临时配置；槽位的唯一位置来源是节点。</summary>
    public SlotSequenceAnimationProfile ToProfile(IEnumerable<SceneContainerDefinition> slots)
    {
        var nodes = slots.ToArray();
        var roles = nodes.Select(node => Uri.UnescapeDataString(node.Id[(node.Id.LastIndexOf('/') + 1)..])).ToArray();
        bool uniqueRoles = roles.Distinct(StringComparer.Ordinal).Count() == roles.Length;
        return new()
        {
            RevealMode = RevealMode,
            RevealProgress = RevealProgress,
            RevealDelayUs = RevealDelayUs,
            RevealLeadUs = RevealLeadUs,
            MaxVisibleLines = MaxVisibleLines,
            ConflictPolicy = ConflictPolicy,
            RetentionPolicy = RetentionPolicy,
            Slots = [.. nodes.Select((node, index) => new SlotSequenceSlot { Id = uniqueRoles ? roles[index] : node.Id, Placement = node.Placement })],
        };
    }
}
