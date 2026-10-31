using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>节点自有内容，与源歌词身份及演唱时间分离。</summary>
public abstract record SceneContent;

/// <summary>文字标记的有序倒计时组，数量由标记引用得到。</summary>
public sealed record CountdownSequenceContent : SceneContent
{
    private ImmutableArray<string> _markNodeIds;
    /// <summary>按倒计时次序排列的直接子标记。</summary>
    public ImmutableArray<string> MarkNodeIds { get => _markNodeIds.IsDefault ? [] : _markNodeIds; init => _markNodeIds = value; }
    /// <summary>所有子标记的外观基线，不作为父级透明度再次相乘。</summary>
    public SceneTextAppearance Appearance { get; init; } = SceneTextAppearance.Default;
    /// <summary>按内容比较引用序列。</summary>
    public bool Equals(CountdownSequenceContent? other) => other is not null
        && Appearance == other.Appearance && MarkNodeIds.SequenceEqual(other.MarkNodeIds);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Appearance); foreach (var id in MarkNodeIds) hash.Add(id); return hash.ToHashCode();
    }
}

/// <summary>场景文字标记，不携带源歌词元素的时间或状态。</summary>
public sealed record TextMarkContent : SceneContent
{
    /// <summary>完整整形的标记文字。</summary>
    public string Text { get; init; } = "●";
    /// <summary>相对于提示组外观的局部覆盖。</summary>
    public SceneTextAppearanceOverride? AppearanceOverride { get; init; }
}

/// <summary>提示容器的位置参考方式。</summary>
public enum SceneAttachmentMode
{
    /// <summary>使用持久父组的参考矩形。</summary>
    FixedParent,
    /// <summary>运行时附着前奏目标句，包含其槽位、微调和动画。</summary>
    CueTargetText,
}

/// <summary>运行附着声明；位置仍只存于节点 Placement。</summary>
public sealed record SceneAttachment
{
    /// <summary>位置参考方式。</summary>
    public SceneAttachmentMode Mode { get; init; }
}
