using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>一态的多重描边，由内向外排列；空表即无描边。</summary>
/// <remarks>
/// <para>第 i 层占据从内侧各层宽度之和起、自身宽度的一圈，层的位置完全由顺序决定（P3 设计 §6.2）。</para>
/// <para>
/// 按值比较：样式、覆盖与外观都是 record，若直接持有 <see cref="ImmutableArray{T}"/>，
/// 两份内容相同的样式会因数组引用不同而不相等，「两态相同只画一遍」与「改动是否生效」的判断都会出错。
/// </para>
/// </remarks>
[CollectionBuilder(typeof(StrokeLayers), nameof(Create))]
public sealed class StrokeLayers : IReadOnlyList<StrokeSpec>, IEquatable<StrokeLayers>
{
    /// <summary>每一态最多的描边层数。</summary>
    public const int MaxCount = 4;

    private readonly ImmutableArray<StrokeSpec> _items;

    private StrokeLayers(ImmutableArray<StrokeSpec> items) => _items = items;

    /// <summary>无描边。</summary>
    public static StrokeLayers Empty { get; } = new([]);

    /// <summary>按由内向外的顺序建立描边列表。</summary>
    /// <param name="items">各层描边；元素不能为 null。</param>
    /// <returns>描边列表。</returns>
    public static StrokeLayers Create(ReadOnlySpan<StrokeSpec> items)
    {
        if (items.IsEmpty) return Empty;
        foreach (StrokeSpec item in items) ArgumentNullException.ThrowIfNull(item, nameof(items));
        return new([.. items]);
    }

    /// <summary>按由内向外的顺序建立描边列表。</summary>
    /// <param name="items">各层描边；元素不能为 null。</param>
    /// <returns>描边列表。</returns>
    public static StrokeLayers From(IEnumerable<StrokeSpec> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Create([.. items]);
    }

    /// <summary>单层描边；<see langword="null"/> 或零宽得到空表（旧的单层写法里零宽本就不绘制）。</summary>
    /// <param name="stroke">唯一一层。</param>
    /// <returns>描边列表。</returns>
    public static StrokeLayers FromStroke(StrokeSpec? stroke) => stroke is null || stroke.WidthPx == 0f ? Empty : new([stroke]);

    /// <summary>层数。</summary>
    public int Count => _items.Length;

    /// <summary>第 <paramref name="index"/> 层（0 为最内层）。</summary>
    /// <param name="index">层序。</param>
    public StrokeSpec this[int index] => _items[index];

    /// <summary>最内层；无描边时为 <see langword="null"/>。</summary>
    public StrokeSpec? Innermost => _items.IsEmpty ? null : _items[0];

    /// <summary>各层宽度之和（画布像素）。</summary>
    public float TotalWidthPx
    {
        get
        {
            float total = 0f;
            foreach (StrokeSpec item in _items) total += item.WidthPx;
            return total;
        }
    }

    /// <summary>是否至少有一层会画出东西。</summary>
    public bool IsEnabled => _items.Any(static item => item.IsEnabled);

    /// <summary>
    /// 结构是否有效：至多 <see cref="MaxCount"/> 层；每层有效且宽度为正的有限数；
    /// 包材质只允许用在第 0 层（D-P3-9）。
    /// </summary>
    public bool IsValid
    {
        get
        {
            if (_items.Length > MaxCount) return false;
            for (int index = 0; index < _items.Length; index++)
            {
                StrokeSpec item = _items[index];
                if (!item.IsValid || item.WidthPx <= 0f) return false;
                if (index > 0 && item.Material is MaterialSpec.PackageMaterial) return false;
            }

            return true;
        }
    }

    /// <summary>各层描边。</summary>
    public ImmutableArray<StrokeSpec> AsImmutableArray() => _items;

    /// <inheritdoc />
    public IEnumerator<StrokeSpec> GetEnumerator() => ((IEnumerable<StrokeSpec>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(StrokeLayers? other) => other is not null && _items.SequenceEqual(other._items);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as StrokeLayers);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (StrokeSpec item in _items) hash.Add(item);
        return hash.ToHashCode();
    }

    /// <summary>按值比较。</summary>
    public static bool operator ==(StrokeLayers? left, StrokeLayers? right) => left is null ? right is null : left.Equals(right);

    /// <summary>按值比较。</summary>
    public static bool operator !=(StrokeLayers? left, StrokeLayers? right) => !(left == right);
}
