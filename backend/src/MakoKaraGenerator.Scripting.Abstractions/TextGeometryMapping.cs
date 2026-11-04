using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace MakoKaraGenerator.Scripting;

/// <summary>按源到父空间顺序保存的封闭文字映射值；不持有回调、native 对象或可变 mesh。</summary>
public sealed partial class TextGeometryMapping : IEquatable<TextGeometryMapping>
{
    private readonly Affine2 _affine;

    /// <summary>创建有序映射；空数组为恒等，default、空步骤及非法 affine 明确拒绝。</summary>
    public TextGeometryMapping(ImmutableArray<Step> steps)
    {
        if (steps.IsDefault || steps.Any(static step => step is null)) throw Invalid("映射步骤不能为 default 或空引用。");
        if (steps.Length > 32) throw new EffectOutputException("WarpGeometryBudgetExceeded", "文字映射最多包含 32 个有序步骤。");
        Steps = [.. steps];
        HasWarp = steps.Any(static step => step is Grid);
        IsIdentity = steps.All(static step => step switch { Affine affine => affine.Matrix == Affine2.Identity,
            Grid grid => grid.Warp.IsIdentity, _ => false });
        Affine2 combined = Affine2.Identity;
        foreach (var step in steps)
        {
            if (step is Grid) { combined = Affine2.Identity; continue; }
            if (step is not Affine affine) throw Invalid("映射包含未支持的步骤类型。");
            try { combined = affine.Matrix.Compose(combined); }
            catch (ArgumentException) { throw Invalid("相邻 affine 组合超出有限坐标。"); }
            if (!combined.TryInverse(out _)) throw Invalid("相邻 affine 组合必须有限可逆。");
        }
        _affine = HasWarp ? Affine2.Identity : combined;
    }

    /// <summary>唯一全域恒等值；有界的恒等 Grid 仍需保留其定义域检查。</summary>
    public static TextGeometryMapping Identity { get; } = new([]);
    /// <summary>按真实叶到父顺序保存的不可变步骤；不能从最终合成矩阵反推。</summary>
    public ImmutableArray<Step> Steps { get; }
    /// <summary>是否含有 Grid 步骤；为 false 时使用预组合 affine 路径。</summary>
    public bool HasWarp { get; }
    /// <summary>每一步在其合法域内均为恒等；不因此省略 Grid 的域检查。</summary>
    public bool IsIdentity { get; }

    /// <summary>按顺序正向映射位置；每个 Grid 使用到达该步骤时的逻辑位置。</summary>
    public Point2 MapPoint(Point2 point)
    {
        if (!HasWarp) return Apply(_affine, point);
        foreach (var step in Steps)
            point = step switch { Affine affine => Apply(affine.Matrix, point), Grid grid => grid.Warp.MapPoint(point),
                _ => throw Invalid("映射包含未支持的步骤类型。") };
        return point;
    }

    /// <summary>以链式法则映射源点处的向量；每层 Jacobian 取该层实际输入点，平移不进入向量。</summary>
    public Vector2 MapVectorAt(Point2 point, Vector2 vector)
    {
        if (!HasWarp) return ApplyVector(_affine, vector);
        foreach (var step in Steps)
        {
            if (step is Affine affine)
            {
                vector = ApplyVector(affine.Matrix, vector); point = Apply(affine.Matrix, point);
            }
            else if (step is Grid grid)
            {
                vector = grid.Warp.MapVectorAt(point, vector); point = grid.Warp.MapPoint(point);
            }
            else throw Invalid("映射包含未支持的步骤类型。");
        }
        return vector;
    }

    /// <summary>比较全部有序值步骤，不因终点相同而丢弃中间有界 Grid。</summary>
    public bool Equals(TextGeometryMapping? other) => ReferenceEquals(this, other) || other is not null
        && Steps.AsSpan().SequenceEqual(other.Steps.AsSpan());
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TextGeometryMapping other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); foreach (var step in Steps) hash.Add(step); return hash.ToHashCode();
    }
    /// <summary>比较两个可能为空的有序映射值。</summary>
    public static bool operator ==(TextGeometryMapping? left, TextGeometryMapping? right) => Equals(left, right);
    /// <summary>比较两个可能为空的有序映射值是否不同。</summary>
    public static bool operator !=(TextGeometryMapping? left, TextGeometryMapping? right) => !Equals(left, right);

    /// <summary>封闭的映射步骤；外部程序集不能增加未验证的映射或回调。</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "Step is the intentionally closed mapping value name exposed by the SDK.")]
    public abstract class Step : IEquatable<Step>
    {
        private protected Step() { }
        /// <summary>比较相同封闭步骤类型的值。</summary>
        public abstract bool Equals(Step? other);
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Step other && Equals(other);
        /// <inheritdoc/>
        public abstract override int GetHashCode();
        /// <summary>比较两个可能为空的封闭步骤值。</summary>
        public static bool operator ==(Step? left, Step? right) => Equals(left, right);
        /// <summary>比较两个可能为空的封闭步骤值是否不同。</summary>
        public static bool operator !=(Step? left, Step? right) => !Equals(left, right);
    }

    /// <summary>完整有限可逆 affine，允许现有父链中的倾斜与反射。</summary>
    public sealed class Affine : Step
    {
        /// <summary>创建有限可逆 affine 步骤；default 零矩阵不是恒等。</summary>
        public Affine(Affine2 matrix)
        {
            if (!matrix.TryInverse(out _)) throw Invalid("affine 步骤必须有限可逆。");
            Matrix = matrix;
        }
        /// <summary>本步骤源到父空间的矩阵。</summary>
        public Affine2 Matrix { get; }
        /// <inheritdoc/>
        public override bool Equals(Step? other) => other is Affine affine && Matrix == affine.Matrix;
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(typeof(Affine), Matrix);
    }

    /// <summary>已完整验证的规范网格步骤。</summary>
    public sealed class Grid : Step
    {
        /// <summary>创建网格步骤，不接受空值。</summary>
        public Grid(GridWarp warp) { ArgumentNullException.ThrowIfNull(warp); Warp = warp; }
        /// <summary>CPU mapper 与 native mesh 共用的不可变网格。</summary>
        public GridWarp Warp { get; }
        /// <inheritdoc/>
        public override bool Equals(Step? other) => other is Grid grid && Warp.Equals(grid.Warp);
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(typeof(Grid), Warp);
    }

    private static Point2 Apply(Affine2 matrix, Point2 point)
    {
        double x = matrix.A * point.X + matrix.C * point.Y + matrix.Tx;
        double y = matrix.B * point.X + matrix.D * point.Y + matrix.Ty;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw Invalid("文字位置映射运算溢出。");
        return new(x, y);
    }
    private static Vector2 ApplyVector(Affine2 matrix, Vector2 vector)
    {
        double x = matrix.A * vector.X + matrix.C * vector.Y, y = matrix.B * vector.X + matrix.D * vector.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw Invalid("文字向量映射运算溢出。");
        return new(x, y);
    }
    private static EffectOutputException Invalid(string message) => new("InvalidTextGeometryMapping", message);
}
