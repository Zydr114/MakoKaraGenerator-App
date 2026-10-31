using System.Collections.Immutable;

#pragma warning disable CA1720 // Float／Int 直接对应 SkSL uniform 类型，避免用含混的数值类型名。

namespace MakoKaraGenerator.Scripting;

/// <summary>中立帧值的封闭类型；是清单的投影，不保存另一套参数声明。</summary>
public enum UniformValueKind
{
    /// <summary>单分量浮点值。</summary>
    Float,
    /// <summary>两个原始数据分量。</summary>
    Float2,
    /// <summary>三个原始数据分量。</summary>
    Float3,
    /// <summary>四个原始数据分量，不是颜色。</summary>
    Float4,
    /// <summary>原生 int32 数据。</summary>
    Int,
    /// <summary>直乘 sRGB RGBA。</summary>
    Color,
    /// <summary>按行传入的九分量矩阵。</summary>
    Matrix3x3,
}

/// <summary>有限、不可变的 shader 帧值，不包含源码或原生资源。</summary>
public sealed class UniformValue
{
    private UniformValue(UniformValueKind kind, ImmutableArray<double> numbers, ImmutableArray<int> integers, int? arrayLength = null)
    {
        foreach (double value in numbers)
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(numbers), "uniform 分量必须有限。");
        Kind = kind; Numbers = numbers; Integers = integers; ArrayLength = arrayLength;
    }
    /// <summary>值的实际类型；颜色与四分量向量不互换。</summary>
    public UniformValueKind Kind { get; }
    /// <summary>显式数组长度，null 表示不是数组。</summary>
    public int? ArrayLength { get; }
    /// <summary>浮点分量；矩阵按行排列，数组按元素排列；整数值为空。</summary>
    public ImmutableArray<double> Numbers { get; }
    /// <summary>int32 分量；其他类型为空。</summary>
    public ImmutableArray<int> Integers { get; }
    /// <summary>构造单个有限浮点值。</summary>
    public static UniformValue Float(double value) => new(UniformValueKind.Float, [value], []);
    /// <summary>构造两个有限数据分量。</summary>
    public static UniformValue Float2(double x, double y) => new(UniformValueKind.Float2, [x, y], []);
    /// <summary>构造三个有限数据分量。</summary>
    public static UniformValue Float3(double x, double y, double z) => new(UniformValueKind.Float3, [x, y, z], []);
    /// <summary>构造四个有限数据分量，不进行颜色校验或转换。</summary>
    public static UniformValue Float4(double x, double y, double z, double w) => new(UniformValueKind.Float4, [x, y, z, w], []);
    /// <summary>构造原生 int32 值，不自动转换 long 或布尔参数。</summary>
    public static UniformValue Int(int value) => new(UniformValueKind.Int, [], [value]);
    /// <summary>构造已校验的直乘 sRGB RGBA 颜色。</summary>
    public static UniformValue Color(ColorRgba value) => new(UniformValueKind.Color, [value.Red, value.Green, value.Blue, value.Alpha], []);
    /// <summary>按行构造矩阵；M11／M12／M13 为第一行，允许不可逆数据矩阵。</summary>
    public static UniformValue Matrix3x3(double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33)
        => new(UniformValueKind.Matrix3x3, [m11, m12, m13, m21, m22, m23, m31, m32, m33], []);
    /// <summary>复制同类型非空数组；拒绝嵌套，长度 1 不等于非数组值。</summary>
    public static UniformValue Array(IEnumerable<UniformValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var numbers = ImmutableArray.CreateBuilder<double>(); var integers = ImmutableArray.CreateBuilder<int>();
        UniformValueKind? kind = null; int count = 0;
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.ArrayLength.HasValue || (kind.HasValue && kind.Value != value.Kind))
                throw new ArgumentException("uniform 数组必须为同类型非嵌套值。", nameof(values));
            kind = value.Kind; numbers.AddRange(value.Numbers); integers.AddRange(value.Integers); count = checked(count + 1);
        }
        if (!kind.HasValue) throw new ArgumentException("uniform 数组不能为空。", nameof(values));
        return new(kind.Value, numbers.ToImmutable(), integers.ToImmutable(), count);
    }
}

/// <summary>完整不可变的帧 uniform 值集；不持有可变字典或隐式默认值。</summary>
public sealed class UniformValues
{
    /// <summary>复制大小写敏感的值集；空白名、重复或 null 值拒绝。</summary>
    public UniformValues(IEnumerable<KeyValuePair<string, UniformValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableDictionary.CreateBuilder<string, UniformValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key); ArgumentNullException.ThrowIfNull(pair.Value);
            builder.Add(pair.Key, pair.Value);
        }
        Values = builder.ToImmutable();
    }
    /// <summary>无 uniform 的程序使用的空值集。</summary>
    public static UniformValues Empty { get; } = new([]);
    /// <summary>全部固定帧值。</summary>
    public ImmutableDictionary<string, UniformValue> Values { get; }
}
