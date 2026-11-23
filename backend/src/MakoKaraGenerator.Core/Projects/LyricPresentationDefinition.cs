using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>固定容器参考矩形，相对于父参考尺寸；根节点相对于画布。</summary>
/// <param name="WidthRatio">宽度比例。</param>
/// <param name="HeightRatio">高度比例。</param>
public readonly record struct SceneReferenceRect(double WidthRatio, double HeightRatio)
{
    /// <summary>完整画布。</summary>
    public static SceneReferenceRect Canvas => new(1, 1);
    /// <summary>有限且为正的尺寸。</summary>
    public bool IsValid => double.IsFinite(WidthRatio) && double.IsFinite(HeightRatio)
        && WidthRatio > 0 && HeightRatio > 0;
}

/// <summary>按稳定源行 ID 保存的内部展示覆盖。</summary>
public sealed record LyricLinePresentation
{
    /// <summary>源分支；现有分支视图写入时补齐。</summary>
    public string BranchId { get; init; } = string.Empty;
    /// <summary>源行 ID。</summary>
    public required string LineId { get; init; }
    /// <summary>当前容器或槽位基础布局上的单句微调；空值表示不微调。</summary>
    public LyricLocalTransform? LocalTransform { get; init; }
}

/// <summary>现有分支设置的临时适配视图；不作为磁盘结构保存。</summary>
public sealed record LyricBranchPresentation
{
    /// <summary>源分支 ID。</summary>
    public required string BranchId { get; init; }
    /// <summary>展示动画策略；空值按角色采用主歌词逐字、其他分支静态。</summary>
    public LyricBranchAnimationPolicy? AnimationPolicy { get; init; }
    /// <summary>解析角色的默认动画策略。</summary>
    public LyricBranchAnimationPolicy PolicyFor(LyricBranch branch) => AnimationPolicy
        ?? (branch.Role == LyricBranchRole.Primary ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static);
    /// <summary>容器外层位置；锚点相对于固定参考矩形。</summary>
    public ScenePlacement Placement { get; init; } = ScenePlacement.Default;
    /// <summary>固定参考尺寸，不随文本或槽位绑定变化。</summary>
    public SceneReferenceRect ReferenceRect { get; init; } = SceneReferenceRect.Canvas;
    /// <summary>普通布局的内部默认位置。</summary>
    public ScenePlacement? DefaultPlacement { get; init; }
    /// <summary>内部流式布局安全区。</summary>
    public NormalizedSafeArea SafeArea { get; init; } = NormalizedSafeArea.Full;
    /// <summary>内部最大文本宽度比例。</summary>
    public double MaxWidthRatio { get; init; } = .9;
    /// <summary>启用的槽位序列策略；关闭后保留外层位置。</summary>
    public SlotSequenceAnimationProfile? SlotSequenceProfile { get; init; }
    /// <summary>源行内部展示覆盖。</summary>
    public ImmutableArray<LyricLinePresentation> Lines { get; init; } = [];
    /// <summary>查找源行的内部展示配置。</summary>
    public LyricLinePresentation? FindLine(string lineId) => Lines.FirstOrDefault(line => line.LineId == lineId);
    /// <summary>结构与所有位置是否有效。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(BranchId) && Placement is { IsValid: true }
        && (AnimationPolicy is null || Enum.IsDefined(AnimationPolicy.Value))
        && ReferenceRect.IsValid && DefaultPlacement is not { IsValid: false } && SafeArea.IsValid
        && double.IsFinite(MaxWidthRatio) && MaxWidthRatio is > 0 and <= 1
        && SlotSequenceProfile is not { IsValid: false } && !Lines.IsDefault
        && Lines.All(line => !string.IsNullOrWhiteSpace(line.LineId) && line.LocalTransform is not { IsValid: false })
        && Lines.Select(line => line.LineId).Distinct(StringComparer.Ordinal).Count() == Lines.Length;
    /// <summary>内容相等；避免不可变数组的引用相等影响无变化命令。</summary>
    public bool Equals(LyricBranchPresentation? other) => other is not null && BranchId == other.BranchId
        && AnimationPolicy == other.AnimationPolicy && Placement == other.Placement && ReferenceRect == other.ReferenceRect && DefaultPlacement == other.DefaultPlacement
        && SafeArea == other.SafeArea && MaxWidthRatio == other.MaxWidthRatio && SlotSequenceProfile == other.SlotSequenceProfile
        && Lines.SequenceEqual(other.Lines);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(BranchId); hash.Add(AnimationPolicy); hash.Add(Placement); hash.Add(ReferenceRect);
        hash.Add(DefaultPlacement); hash.Add(SafeArea); hash.Add(MaxWidthRatio); hash.Add(SlotSequenceProfile);
        foreach (var line in Lines) hash.Add(line);
        return hash.ToHashCode();
    }
}
