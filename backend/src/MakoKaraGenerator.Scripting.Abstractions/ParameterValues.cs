using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>参数的封闭类型值；整数时长不经过浮点转换。</summary>
public abstract record ParameterValue
{
    private ParameterValue() { }
    /// <summary>有限数值参数。</summary>
    public sealed record Number : ParameterValue
    {
        /// <summary>创建有限数值。</summary>
        public Number(double value) { Point2.Finite(value); Value = value; }
        /// <summary>实际数值。</summary>
        public double Value { get; }
    }
    /// <summary>整数参数。</summary><param name="Value">实际整数。</param>
    public sealed record Whole(long Value) : ParameterValue;
    /// <summary>布尔参数。</summary><param name="Value">实际布尔值。</param>
    public sealed record Toggle(bool Value) : ParameterValue;
    /// <summary>已由清单校验选项的枚举参数，不与普通文本互换。</summary>
    public sealed record Choice : ParameterValue
    {
        /// <summary>创建非空的选项身份。</summary>
        public Choice(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
        /// <summary>选项身份。</summary>
        public string Value { get; }
    }
    /// <summary>非负整数微秒时长。</summary>
    public sealed record Duration : ParameterValue
    {
        /// <summary>创建时长；不允许负值。</summary>
        public Duration(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
        /// <summary>微秒数。</summary>
        public long Value { get; }
    }
    /// <summary>普通文本参数。</summary>
    public sealed record Text : ParameterValue
    {
        /// <summary>创建文本；允许空串，不允许 null。</summary>
        public Text(string value) { ArgumentNullException.ThrowIfNull(value); Value = value; }
        /// <summary>实际文本。</summary>
        public string Value { get; }
    }
    /// <summary>直乘 sRGB 颜色参数，不与向量或文本互换。</summary>
    public sealed record Color : ParameterValue
    {
        /// <summary>固定已经校验的颜色。</summary>
        public Color(ColorRgba value) { Value = value; }
        /// <summary>实际颜色。</summary>
        public ColorRgba Value { get; }
    }
    /// <summary>固定类型化进度曲线，不在帧内解析表达式。</summary>
    public sealed record Curve : ParameterValue
    {
        /// <summary>固定曲线。</summary>
        public Curve(CurveValue value) { ArgumentNullException.ThrowIfNull(value); Value = value; }
        /// <summary>实际曲线。</summary>
        public CurveValue Value { get; }
    }
    /// <summary>宿主已解析的固定图片资源参数，不持有文件路径。</summary>
    public sealed record Asset : ParameterValue
    {
        /// <summary>固定图片句柄；资源范围仍由宿主校验。</summary>
        public Asset(ImageHandle value) { ArgumentNullException.ThrowIfNull(value); Value = value; }
        /// <summary>实际固定资源。</summary>
        public ImageHandle Value { get; }
    }
}

/// <summary>一次固定输入的不可变参数；缺失或类型不符报告参数名，不自动转换。</summary>
public sealed class ParameterValues
{
    private readonly ImmutableDictionary<string, ParameterValue> _values;
    private readonly ImmutableArray<string> _ids;
    /// <summary>构造已固定的参数副本；重复身份拒绝。</summary>
    public ParameterValues(IEnumerable<KeyValuePair<string, ParameterValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableDictionary.CreateBuilder<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key); ArgumentNullException.ThrowIfNull(pair.Value);
            builder.Add(pair.Key, pair.Value);
        }
        _values = builder.ToImmutable();
        _ids = [.. _values.Keys.Order(StringComparer.Ordinal)];
    }
    /// <summary>没有参数；读取任意键仍报告缺失。</summary>
    public static ParameterValues Empty { get; } = new([]);
    /// <summary>固定参数身份的 ordinal 顺序，用于完整协议校验，不公开可变值表。</summary>
    public ImmutableArray<string> Ids => _ids;
    /// <summary>读取有限数值；不接受整数、文本或时长冒充数值。</summary>
    public double GetNumber(string id) => Require<ParameterValue.Number>(id).Value;
    /// <summary>读取整数。</summary>
    public long GetInteger(string id) => Require<ParameterValue.Whole>(id).Value;
    /// <summary>读取布尔值。</summary>
    public bool GetBoolean(string id) => Require<ParameterValue.Toggle>(id).Value;
    /// <summary>读取清单选项身份。</summary>
    public string GetEnum(string id) => Require<ParameterValue.Choice>(id).Value;
    /// <summary>读取整数微秒时长。</summary>
    public long GetDuration(string id) => Require<ParameterValue.Duration>(id).Value;
    /// <summary>读取普通文本。</summary>
    public string GetText(string id) => Require<ParameterValue.Text>(id).Value;
    /// <summary>读取直乘 sRGB 颜色。</summary>
    public ColorRgba GetColor(string id) => Require<ParameterValue.Color>(id).Value;
    /// <summary>读取显式曲线。</summary>
    public CurveValue GetCurve(string id) => Require<ParameterValue.Curve>(id).Value;
    /// <summary>读取固定图片句柄，不自行加载或解析文件。</summary>
    public ImageHandle GetAsset(string id) => Require<ParameterValue.Asset>(id).Value;
    /// <summary>宿主校验资产参数属于固定包；不转换同名其他修订。</summary>
    public void ValidateResources(ResourceCatalog resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var pair in _values)
            if (pair.Value is ParameterValue.Asset asset && !resources.Contains(asset.Value))
                throw new EffectOutputException("InvalidParameterResource", "参数资源不属于固定包：" + pair.Key);
    }
    // Internal binding identity only. Preserve the public reference Equals contract.
    internal bool ContentEquals(ParameterValues other) => ReferenceEquals(this, other) || _ids.SequenceEqual(other._ids)
        && _ids.All(id => ValueEquals(_values[id], other._values[id]));
    internal int ContentHash()
    {
        HashCode hash = new();
        foreach (string id in _ids) { hash.Add(id, StringComparer.Ordinal); AddValue(ref hash, _values[id]); }
        return hash.ToHashCode();
    }
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static bool SamePoint(Point2 a, Point2 b) => Same(a.X, b.X) && Same(a.Y, b.Y);
    private static bool ValueEquals(ParameterValue a, ParameterValue b) => (a, b) switch
    {
        (ParameterValue.Number x, ParameterValue.Number y) => Same(x.Value, y.Value),
        (ParameterValue.Color x, ParameterValue.Color y) => Same(x.Value.Red, y.Value.Red) && Same(x.Value.Green, y.Value.Green)
            && Same(x.Value.Blue, y.Value.Blue) && Same(x.Value.Alpha, y.Value.Alpha),
        (ParameterValue.Curve x, ParameterValue.Curve y) => CurveEquals(x.Value, y.Value),
        // Closed scalar records, ordinal strings and full Asset ResourceIdentity.
        _ => a.Equals(b),
    };
    private static bool CurveEquals(CurveValue a, CurveValue b) => (a, b) switch
    {
        (CurveValue.Easing x, CurveValue.Easing y) => x.Kind == y.Kind,
        (CurveValue.CubicBezier x, CurveValue.CubicBezier y) => SamePoint(x.First, y.First) && SamePoint(x.Second, y.Second),
        (CurveValue.ControlPoints x, CurveValue.ControlPoints y) => x.Points.Length == y.Points.Length
            && x.Points.Zip(y.Points).All(pair => SamePoint(pair.First, pair.Second)),
        _ => false,
    };
    private static void AddValue(ref HashCode hash, ParameterValue value)
    {
        hash.Add(value.GetType());
        switch (value)
        {
            case ParameterValue.Number number: hash.Add(BitConverter.DoubleToInt64Bits(number.Value)); break;
            case ParameterValue.Color color:
                hash.Add(BitConverter.DoubleToInt64Bits(color.Value.Red)); hash.Add(BitConverter.DoubleToInt64Bits(color.Value.Green));
                hash.Add(BitConverter.DoubleToInt64Bits(color.Value.Blue)); hash.Add(BitConverter.DoubleToInt64Bits(color.Value.Alpha)); break;
            case ParameterValue.Curve curve:
                hash.Add(curve.Value.GetType());
                switch (curve.Value)
                {
                    case CurveValue.Easing easing: hash.Add(easing.Kind); break;
                    case CurveValue.CubicBezier bezier: AddPoint(ref hash, bezier.First); AddPoint(ref hash, bezier.Second); break;
                    case CurveValue.ControlPoints control:
                        foreach (var point in control.Points) AddPoint(ref hash, point); break;
                }
                break;
            default: hash.Add(value); break;
        }
    }
    private static void AddPoint(ref HashCode hash, Point2 point)
    { hash.Add(BitConverter.DoubleToInt64Bits(point.X)); hash.Add(BitConverter.DoubleToInt64Bits(point.Y)); }
    private T Require<T>(string id) where T : ParameterValue
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_values.TryGetValue(id, out var value)) throw new KeyNotFoundException("MissingParameter: " + id);
        return value as T ?? throw new InvalidOperationException("ParameterTypeMismatch: " + id + " requires " + typeof(T).Name);
    }
}

/// <summary>纯缓动函数；进度必须由有效的采样区间提供，不静默夹值。</summary>
public static class Curves
{
    /// <summary>固定线性曲线。</summary>
    public static CurveValue LinearCurve { get; } = new CurveValue.Easing(EasingKind.Linear);
    /// <summary>固定三次 ease-out 曲线。</summary>
    public static CurveValue EaseOutCurve { get; } = new CurveValue.Easing(EasingKind.EaseOut);
    /// <summary>固定 3p²−2p³ 的 ease-in-out 曲线。</summary>
    public static CurveValue EaseInOutCurve { get; } = new CurveValue.Easing(EasingKind.EaseInOut);
    /// <summary>三次 ease-out；输入为有限的 [0,1] 进度。</summary>
    public static double EaseOut(double progress)
    {
        Point2.Finite(progress);
        if (progress is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(progress));
        double rest = 1 - progress; return 1 - rest * rest * rest;
    }
}
