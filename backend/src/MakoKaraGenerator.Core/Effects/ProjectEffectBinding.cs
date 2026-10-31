using System.Collections.Immutable;
using MakoKaraGenerator.Core.Internal;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>持久绑定的六类源／场景身份。</summary>
public enum ProjectEffectBindingKind
{
    /// <summary>整个工程。</summary>
    Project,
    /// <summary>分支歌词。</summary>
    Branch,
    /// <summary>明确的歌词行集合。</summary>
    Lines,
    /// <summary>明确的基础元素集合。</summary>
    Tokens,
    /// <summary>展示片段集合。</summary>
    Clips,
    /// <summary>明确的场景节点。</summary>
    Node,
}

/// <summary>不引用 SDK 的不可变绑定；存在性由工程校验器检查。</summary>
public sealed class ProjectEffectBinding : IEquatable<ProjectEffectBinding>
{
    /// <summary>创建严格结构的绑定。集合非空且唯一，节点显式指定后代策略。</summary>
    public ProjectEffectBinding(ProjectEffectBindingKind kind, string? id = null, IEnumerable<string>? ids = null, bool includeDescendants = false)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var values = ids is null ? ImmutableArray<string>.Empty : ids.ToImmutableArray();
        bool single = kind is ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node;
        bool multiple = kind is ProjectEffectBindingKind.Lines or ProjectEffectBindingKind.Tokens or ProjectEffectBindingKind.Clips;
        if (single ? string.IsNullOrWhiteSpace(id) : id is not null) throw new ArgumentException("绑定标识与类型不符。", nameof(id));
        if (multiple ? values.IsEmpty || values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length : !values.IsEmpty)
            throw new ArgumentException("绑定集合必须非空、唯一且符合类型。", nameof(ids));
        if (includeDescendants && kind != ProjectEffectBindingKind.Node) throw new ArgumentException("只有节点绑定允许后代开关。", nameof(includeDescendants));
        Kind = kind; Id = id; Ids = values; IncludeDescendants = includeDescendants;
    }
    /// <summary>绑定类型。</summary>
    public ProjectEffectBindingKind Kind { get; }
    /// <summary>分支或节点 ID。</summary>
    public string? Id { get; }
    /// <summary>行、元素或片段 ID。</summary>
    public ImmutableArray<string> Ids { get; }
    /// <summary>节点是否包含后代。</summary>
    public bool IncludeDescendants { get; }
    /// <summary>内容相等，集合顺序保留。</summary>
    public bool Equals(ProjectEffectBinding? other) => other is not null && Kind == other.Kind && Id == other.Id
        && IncludeDescendants == other.IncludeDescendants && SequenceEquality.ContentEquals(Ids, other.Ids);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ProjectEffectBinding other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() { HashCode hash = new(); hash.Add(Kind); hash.Add(Id); hash.Add(IncludeDescendants); SequenceEquality.AddContentHash(ref hash, Ids); return hash.ToHashCode(); }
}

/// <summary>订阅范围。null BranchId 为绑定源，工程范围显式标记。</summary>
public sealed record ProjectEffectSubscription
{
    /// <summary>创建三个合法范围之一：boundSources、project、branch。</summary>
    public ProjectEffectSubscription(string kind = "boundSources", string? branchId = null)
    {
        if (kind is not ("boundSources" or "project" or "branch")
            || (kind == "branch" ? string.IsNullOrWhiteSpace(branchId) : branchId is not null))
            throw new ArgumentException("订阅范围或分支 ID 无效。");
        Kind = kind; BranchId = branchId;
    }
    /// <summary>封闭的范围名。</summary>
    public string Kind { get; }
    /// <summary>仅 branch 范围存在。</summary>
    public string? BranchId { get; }
}
