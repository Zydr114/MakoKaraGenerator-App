using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>图的显式浮点或整数字面量，矩阵按行存放；不保存帧服务。</summary>
public sealed class ShaderLiteral
{
    private ShaderLiteral(ShaderUniformKind kind, ImmutableArray<double> numbers, ImmutableArray<int> integers, int? length)
    { Kind = kind; Numbers = numbers; Integers = integers; ArrayLength = length; }
    /// <summary>清单类型。</summary>
    public ShaderUniformKind Kind { get; }
    /// <summary>数组长度；null 为单值。</summary>
    public int? ArrayLength { get; }
    /// <summary>浮点分量，整数类型为空。</summary>
    public ImmutableArray<double> Numbers { get; }
    /// <summary>整数分量，其他类型为空。</summary>
    public ImmutableArray<int> Integers { get; }
    /// <summary>建立完整有限浮点字面量；颜色另检查直乘 RGBA 范围。</summary>
    public static ShaderLiteral Numeric(ShaderUniformKind kind, IEnumerable<double> values, int? arrayLength = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var declaration = new ShaderUniformDeclaration("literal", kind, arrayLength);
        if (kind == ShaderUniformKind.Int) throw new ArgumentException("整数须使用 Whole。", nameof(kind));
        ImmutableArray<double> copy = [.. values];
        if (copy.Length != declaration.ScalarCount || copy.Any(value => !double.IsFinite(value))
            || (kind == ShaderUniformKind.Color && copy.Any(value => value is < 0 or > 1)))
            throw new ArgumentException("字面量分量数量、有限性或颜色范围无效。", nameof(values));
        return new(kind, copy, [], arrayLength);
    }
    /// <summary>建立完整 int32 字面量，不自动转换 long／double。</summary>
    public static ShaderLiteral Whole(IEnumerable<int> values, int? arrayLength = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var declaration = new ShaderUniformDeclaration("literal", ShaderUniformKind.Int, arrayLength);
        ImmutableArray<int> copy = [.. values];
        if (copy.Length != declaration.ScalarCount) throw new ArgumentException("整数字面量长度无效。", nameof(values));
        return new(ShaderUniformKind.Int, [], copy, arrayLength);
    }
}

/// <summary>图可读取的封闭宿主值。</summary>
public enum GraphHostValue
{
    /// <summary>整数微秒换算为秒，不驱动状态。</summary>
    TimeSeconds,
    /// <summary>合成画布逻辑宽高，非预览像素宽高。</summary>
    CanvasSize,
}

/// <summary>封闭 uniform 绑定；不解释任意表达式。</summary>
public abstract record GraphUniformBinding
{
    private GraphUniformBinding() { }
    /// <summary>固定的完整字面量。</summary><param name="Value">类型化值。</param>
    public sealed record Literal(ShaderLiteral Value) : GraphUniformBinding;
    /// <summary>读取已声明参数，不做隐式跨类型转换。</summary><param name="Id">参数身份。</param>
    public sealed record Parameter(string Id) : GraphUniformBinding;
    /// <summary>读取封闭宿主值。</summary><param name="Value">宿主值类型。</param>
    public sealed record Host(GraphHostValue Value) : GraphUniformBinding;
}

/// <summary>有限数值字段的封闭绑定。</summary>
public abstract record GraphNumberBinding
{
    private GraphNumberBinding() { }
    /// <summary>固定数值。</summary><param name="Value">有限值，由图编译器校验。</param>
    public sealed record Literal(double Value) : GraphNumberBinding;
    /// <summary>已声明 number 参数。</summary><param name="Id">参数身份。</param>
    public sealed record Parameter(string Id) : GraphNumberBinding;
}

/// <summary>节点外扩只允许常量或 ceil(k×number)，不用任意字符串表达式。</summary>
public abstract record GraphExpansion
{
    private GraphExpansion() { }
    /// <summary>非负固定外扩。</summary><param name="Value">逻辑画布像素。</param>
    public sealed record Literal(double Value) : GraphExpansion;
    /// <summary>参数乘数后向上取整。</summary><param name="ParameterId">number 参数。</param><param name="Factor">非负有限乘数。</param>
    public sealed record CeilingMultiple(string ParameterId, double Factor) : GraphExpansion;
}

/// <summary>所属包的唯一参数约束；图引用 ID，没有第二套默认值。</summary>
public abstract class EffectParameterConstraint
{
    /// <summary>固定参数身份。</summary>
    protected EffectParameterConstraint(string id) { ArgumentException.ThrowIfNullOrWhiteSpace(id); Id = id; }
    /// <summary>参数身份。</summary>
    public string Id { get; }
    /// <summary>有限 number 区间。</summary>
    public sealed class Number : EffectParameterConstraint
    {
        /// <summary>固定数值区间。</summary>
        public Number(string id, double minimum, double maximum) : base(id) { Minimum = minimum; Maximum = maximum; }
        /// <summary>下限。</summary>
        public double Minimum { get; }
        /// <summary>上限。</summary>
        public double Maximum { get; }
    }
    /// <summary>完整 long 区间，uniform 的 int32 限制另检查。</summary>
    public sealed class Whole : EffectParameterConstraint
    {
        /// <summary>固定整数区间。</summary>
        public Whole(string id, long minimum, long maximum) : base(id) { Minimum = minimum; Maximum = maximum; }
        /// <summary>下限。</summary>
        public long Minimum { get; }
        /// <summary>上限。</summary>
        public long Maximum { get; }
    }
    /// <summary>布尔参数。</summary>
    public sealed class Toggle : EffectParameterConstraint
    {
        /// <summary>固定身份。</summary>
        public Toggle(string id) : base(id) { }
    }
    /// <summary>直乘 RGBA 参数。</summary>
    public sealed class Color : EffectParameterConstraint
    {
        /// <summary>固定身份。</summary>
        public Color(string id) : base(id) { }
    }
    /// <summary>复制枚举选项，不保留调用者集合。</summary>
    public sealed class Choice : EffectParameterConstraint
    {
        /// <summary>固定选项。</summary>
        public Choice(string id, IEnumerable<string> values) : base(id) { ArgumentNullException.ThrowIfNull(values); Values = [.. values]; }
        /// <summary>全部合法选项。</summary>
        public ImmutableArray<string> Values { get; }
    }
    /// <summary>非负整数微秒。</summary>
    public sealed class Duration : EffectParameterConstraint
    {
        /// <summary>固定时长区间。</summary>
        public Duration(string id, long minimum, long maximum) : base(id) { Minimum = minimum; Maximum = maximum; }
        /// <summary>下限。</summary>
        public long Minimum { get; }
        /// <summary>上限。</summary>
        public long Maximum { get; }
    }
    /// <summary>普通文本参数。</summary>
    public sealed class Text : EffectParameterConstraint
    {
        /// <summary>固定身份。</summary>
        public Text(string id) : base(id) { }
    }
    /// <summary>类型化曲线参数。</summary>
    public sealed class Curve : EffectParameterConstraint
    {
        /// <summary>固定身份。</summary>
        public Curve(string id) : base(id) { }
    }
    /// <summary>受管图片资源参数。</summary>
    public sealed class Asset : EffectParameterConstraint
    {
        /// <summary>固定身份。</summary>
        public Asset(string id) : base(id) { }
    }
}
