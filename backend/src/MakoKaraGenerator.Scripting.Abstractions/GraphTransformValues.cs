using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>明确 Transform 节点的一次完整覆盖；不修改文字几何或语义锚点。</summary>
public sealed record GraphTransformValue
{
    /// <summary>复制有限、原生 float32 下可逆的矩阵和非空输出框；不修复或交换边界。</summary>
    public GraphTransformValue(Affine2 matrix, Rect2 outputBounds)
    {
        if (!matrix.TryInverse(out _)) throw new ArgumentException("图矩阵必须有限且可逆。", nameof(matrix));
        double a = Native(matrix.A), b = Native(matrix.B), c = Native(matrix.C), d = Native(matrix.D),
            tx = Native(matrix.Tx), ty = Native(matrix.Ty);
        double determinant = a * d - b * c;
        if (!double.IsFinite(determinant) || determinant == 0)
            throw new ArgumentException("图矩阵在 float32 精度下不可逆。", nameof(matrix));
        _ = Native(d / determinant); _ = Native(-b / determinant); _ = Native(-c / determinant); _ = Native(a / determinant);
        _ = Native((c * ty - d * tx) / determinant); _ = Native((b * tx - a * ty) / determinant);
        float left = Native(outputBounds.Left), top = Native(outputBounds.Top), right = Native(outputBounds.Right), bottom = Native(outputBounds.Bottom);
        if (outputBounds.Width <= 0 || outputBounds.Height <= 0 || right <= left || bottom <= top)
            throw new ArgumentException("图输出框必须非空且在 float32 精度下保留尺寸。", nameof(outputBounds));
        Matrix = matrix; OutputBounds = outputBounds;
    }
    /// <summary>完整的源至目标矩阵；不是增量。</summary>
    public Affine2 Matrix { get; }
    /// <summary>所属图坐标空间的完整语义输出框；像素对齐由实际执行密度确定。</summary>
    public Rect2 OutputBounds { get; }
    private static float Native(double value)
    {
        float converted = (float)value;
        if (!double.IsFinite(value) || !float.IsFinite(converted) || value != 0 && converted == 0)
            throw new ArgumentException("图变换分量超出 float32 范围或非零下溢。");
        return converted;
    }
    /// <summary>逐分量按位比较；不同覆盖副本不会因对象引用而误判。</summary>
    public bool Equals(GraphTransformValue? other) => ReferenceEquals(this, other) || other is not null
        && Components().Zip(other.Components()).All(pair => BitConverter.DoubleToInt64Bits(pair.First) == BitConverter.DoubleToInt64Bits(pair.Second));
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); foreach (double value in Components()) hash.Add(BitConverter.DoubleToInt64Bits(value)); return hash.ToHashCode();
    }
    private IEnumerable<double> Components()
    {
        yield return Matrix.A; yield return Matrix.B; yield return Matrix.C; yield return Matrix.D; yield return Matrix.Tx; yield return Matrix.Ty;
        yield return OutputBounds.Left; yield return OutputBounds.Top; yield return OutputBounds.Right; yield return OutputBounds.Bottom;
    }
}

/// <summary>大小写敏感、不可变的 Transform 节点覆盖集；每次绑定整体替换。</summary>
public sealed class GraphTransformValues : IEquatable<GraphTransformValues>
{
    /// <summary>复制所有键值；空白身份、重复节点和空值均拒绝。</summary>
    public GraphTransformValues(IEnumerable<KeyValuePair<string, GraphTransformValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableDictionary.CreateBuilder<string, GraphTransformValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key); ArgumentNullException.ThrowIfNull(pair.Value);
            if (builder.ContainsKey(pair.Key)) throw new ArgumentException("Transform 节点覆盖身份不能重复。", nameof(values));
            builder.Add(pair.Key, pair.Value);
        }
        Values = builder.ToImmutable();
    }
    /// <summary>无覆盖；保留图中固定 Transform 值。</summary>
    public static GraphTransformValues Empty { get; } = new([]);
    /// <summary>固定节点身份到完整矩阵与框的映射。</summary>
    public ImmutableDictionary<string, GraphTransformValue> Values { get; }
    /// <summary>按键及值内容比较，不依赖集合或值对象引用。</summary>
    public bool Equals(GraphTransformValues? other) => ReferenceEquals(this, other) || other is not null && Values.Count == other.Values.Count
        && Values.All(pair => other.Values.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GraphTransformValues other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var pair in Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(pair.Key, StringComparer.Ordinal); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }
}
