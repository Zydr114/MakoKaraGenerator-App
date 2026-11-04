namespace MakoKaraGenerator.Scripting;

/// <summary>固定图导出的完整不可变逐帧绑定；参数和两类覆盖均整体替换。</summary>
public sealed record GraphBindingValue
{
    /// <summary>复制固定图身份与完整参数／覆盖；不合并上一绑定，也不装载原生资源。</summary>
    public GraphBindingValue(GraphHandle graph, ParameterValues parameters, GraphUniformValues uniforms, GraphTransformValues transforms)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(uniforms); ArgumentNullException.ThrowIfNull(transforms);
        Graph = graph; Parameters = parameters; Uniforms = uniforms; Transforms = transforms;
    }
    /// <summary>本实例固定包修订中的真实图导出。</summary>
    public GraphHandle Graph { get; }
    /// <summary>完整类型参数。</summary>
    public ParameterValues Parameters { get; }
    /// <summary>完整节点／uniform 覆盖。</summary>
    public GraphUniformValues Uniforms { get; }
    /// <summary>完整 Transform 节点覆盖。</summary>
    public GraphTransformValues Transforms { get; }
    /// <summary>比较全部固定身份和值，包括参数曲线的控制点内容。</summary>
    public bool Equals(GraphBindingValue? other) => ReferenceEquals(this, other) || other is not null && Graph == other.Graph
        && Parameters.ContentEquals(other.Parameters) && UniformsEqual(Uniforms, other.Uniforms) && Transforms.Equals(other.Transforms);
    // Binding identity preserves shader-observable +0/-0; leave the legacy
    // GraphUniformValues Equals contract unchanged outside the new binding path.
    internal static bool UniformsEqual(GraphUniformValues? a, GraphUniformValues? b) => ReferenceEquals(a, b) || a is not null && b is not null
        && a.Values.Count == b.Values.Count && a.Values.All(pair => b.Values.TryGetValue(pair.Key, out var value)
            && pair.Value.Kind == value.Kind && pair.Value.ArrayLength == value.ArrayLength
            && pair.Value.Numbers.Length == value.Numbers.Length
            && pair.Value.Numbers.Zip(value.Numbers).All(item => BitConverter.DoubleToInt64Bits(item.First) == BitConverter.DoubleToInt64Bits(item.Second))
            && pair.Value.Integers.SequenceEqual(value.Integers));
    internal static int UniformsHash(GraphUniformValues? values)
    {
        if (values is null) return 0;
        HashCode hash = new();
        foreach (var pair in values.Values.OrderBy(pair => pair.Key.NodeId, StringComparer.Ordinal).ThenBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            hash.Add(pair.Key); hash.Add(pair.Value.Kind); hash.Add(pair.Value.ArrayLength);
            foreach (double number in pair.Value.Numbers) hash.Add(BitConverter.DoubleToInt64Bits(number));
            foreach (int integer in pair.Value.Integers) hash.Add(integer);
        }
        return hash.ToHashCode();
    }
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Graph, Parameters.ContentHash(), UniformsHash(Uniforms), Transforms);
}
