using System.Collections.Immutable;

namespace MakoKaraGenerator.Application.Editing;

// 文案键与合并身份分开：普通操作按稳定目标比较，每个拖动手势则使用唯一对象。
internal sealed class ProjectEditMergeIdentity(
    string operation, ImmutableArray<ProjectEditTarget> targets, object? gesture = null)
    : IEquatable<ProjectEditMergeIdentity>
{
    internal bool IgnoreTimeWindow => gesture is not null;

    public bool Equals(ProjectEditMergeIdentity? other) => other is not null
        && operation == other.Operation && ReferenceEquals(gesture, other.Gesture)
        && targets.SequenceEqual(other.Targets);

    public override bool Equals(object? obj) => obj is ProjectEditMergeIdentity other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(operation); hash.Add(gesture);
        foreach (ProjectEditTarget target in targets) hash.Add(target);
        return hash.ToHashCode();
    }

    private string Operation => operation;
    private ImmutableArray<ProjectEditTarget> Targets => targets;
    private object? Gesture => gesture;
}

// 内部身份只保存稳定键，不让一个用例的公开请求成为另一个用例的合同。
internal readonly record struct ProjectEditTarget(string BranchId, string LineId, string? TokenId)
{
    public static implicit operator ProjectEditTarget(LyricTimingEditTarget target) =>
        new(target.BranchId, target.LineId, target.TokenId);

    public static implicit operator ProjectEditTarget(LyricContentEditTarget target) =>
        new(target.BranchId, target.LineId, target.TokenId);
}
