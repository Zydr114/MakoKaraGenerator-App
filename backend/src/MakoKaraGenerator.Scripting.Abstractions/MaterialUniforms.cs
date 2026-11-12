using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>明确选择真实目标的材质部位；标记两态对应 Pending／Elapsed。</summary>
/// <remarks>描边槽指该态最内层描边的材质（包材质只能用在最内层）。数值与渲染层的准备帧材质槽一一对应。</remarks>
public enum MaterialSlot
{
    /// <summary>未唱填充。</summary>
    UnsungFill,
    /// <summary>已唱填充。</summary>
    SungFill,
    /// <summary>未唱最内层描边。</summary>
    UnsungStroke,
    /// <summary>已唱最内层描边。</summary>
    SungStroke,
}

/// <summary>图节点及 uniform 名字共同定义的、不可变的逐帧覆盖集。</summary>
public sealed class GraphUniformValues : IEquatable<GraphUniformValues>
{
    /// <summary>复制显式节点／名字和值；拒绝空白名字、重复键和空值。</summary>
    public GraphUniformValues(IEnumerable<KeyValuePair<(string NodeId, string Name), UniformValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableDictionary.CreateBuilder<(string NodeId, string Name), UniformValue>();
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key.NodeId); ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key.Name);
            ArgumentNullException.ThrowIfNull(pair.Value); builder.Add(pair.Key, pair.Value);
        }
        Values = builder.ToImmutable();
    }
    /// <summary>没有覆盖；图原始的字面量／参数／宿主值保持。</summary>
    public static GraphUniformValues Empty { get; } = new([]);
    /// <summary>大小写敏感的完整节点／名字到值映射。</summary>
    public ImmutableDictionary<(string NodeId, string Name), UniformValue> Values { get; }
    internal GraphUniformValues Set(string nodeId, string name, UniformValue value) => new(Values.SetItem((nodeId, name), value));
    /// <summary>比较内容，避免多次准备同一描述时把不可变集合引用误认为不同材质。</summary>
    public bool Equals(GraphUniformValues? other) => ReferenceEquals(this, other) || other is not null && Values.Count == other.Values.Count
        && Values.All(pair => other.Values.TryGetValue(pair.Key, out var value) && ValueEquals(pair.Value, value));
    internal static bool ValueEquals(UniformValue a, UniformValue b) => a.Kind == b.Kind && a.ArrayLength == b.ArrayLength
        && a.Numbers.SequenceEqual(b.Numbers) && a.Integers.SequenceEqual(b.Integers);
    internal static int ValueHash(UniformValue value)
    {
        HashCode hash = new(); hash.Add(value.Kind); hash.Add(value.ArrayLength);
        foreach (var number in value.Numbers) hash.Add(number);
        foreach (var integer in value.Integers) hash.Add(integer);
        return hash.ToHashCode();
    }
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GraphUniformValues other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var pair in Values.OrderBy(pair => pair.Key.NodeId, StringComparer.Ordinal).ThenBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            hash.Add(pair.Key); hash.Add(ValueHash(pair.Value));
        }
        return hash.ToHashCode();
    }
}

/// <summary>有序的材质操作；保留中间身份校验，不能用最终值掩盖先前无效写入。</summary>
public abstract record MaterialEdit
{
    private MaterialEdit() { }
    /// <summary>建立部位的新外观并清除其先前覆盖；null 仅用于描边槽（该态无描边）。</summary>
    public sealed record ReplaceMaterial(MaterialSlot Slot, MaterialValue? Material) : MaterialEdit;
    /// <summary>修改当时匹配的固定材质、节点和 uniform。</summary>
    public sealed record Uniform(MaterialSlot Slot, MaterialHandle Handle, string NodeId, string Name, UniformValue Value) : MaterialEdit
    {
        /// <summary>比较完整身份和数值内容，不按每次求值新建值对象的引用比较。</summary>
        public bool Equals(Uniform? other) => ReferenceEquals(this, other) || other is not null && Slot == other.Slot
            && Handle == other.Handle && NodeId == other.NodeId && Name == other.Name && GraphUniformValues.ValueEquals(Value, other.Value);
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Slot, Handle, NodeId, Name, GraphUniformValues.ValueHash(Value));
    }
}
