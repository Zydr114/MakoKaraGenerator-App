using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>封闭的纯进度曲线；定义域 [0,1]，无系统钟或用户函数。</summary>
public abstract record CurveValue
{
    private CurveValue() { }
    /// <summary>在明确的有限进度上求值；不夹值。</summary>
    public double Evaluate(double progress)
    {
        ColorRgba.Unit(progress);
        double result = EvaluateCore(progress); Point2.Finite(result); return result;
    }
    private protected abstract double EvaluateCore(double progress);

    /// <summary>规范缓动曲线。</summary>
    public sealed record Easing : CurveValue
    {
        /// <summary>指定已知缓动，不接受未知枚举。</summary>
        public Easing(EasingKind kind)
        {
            if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Kind = kind;
        }
        /// <summary>固定缓动。</summary>
        public EasingKind Kind { get; }
        private protected override double EvaluateCore(double progress) => Kind switch
        {
            EasingKind.Linear => progress,
            EasingKind.EaseOut => Curves.EaseOut(progress),
            EasingKind.EaseInOut => progress * progress * (3 - 2 * progress),
            _ => throw new InvalidOperationException("未知缓动。"),
        };
    }

    /// <summary>三次贝塞尔缓动，端点固定为 (0,0)、(1,1)；Y 控制点允许显式过冲。</summary>
    public sealed record CubicBezier : CurveValue
    {
        /// <summary>建立单调 X 的曲线；控制点 X 必须在 [0,1]，Y 必须有限。</summary>
        public CubicBezier(Point2 first, Point2 second)
        {
            ColorRgba.Unit(first.X); ColorRgba.Unit(second.X); First = first; Second = second;
        }
        /// <summary>第一控制点。</summary>
        public Point2 First { get; }
        /// <summary>第二控制点。</summary>
        public Point2 Second { get; }
        private protected override double EvaluateCore(double progress)
        {
            if (progress is 0 or 1) return progress;
            double low = 0, high = 1;
            // 固定迭代次数，不以设备速度或非确定的容差驱动采样。
            for (int index = 0; index < 56; index++)
            {
                double middle = (low + high) / 2;
                if (Coordinate(middle, First.X, Second.X) < progress) low = middle;
                else high = middle;
            }
            return Coordinate((low + high) / 2, First.Y, Second.Y);
        }
        private static double Coordinate(double t, double first, double second)
        {
            double rest = 1 - t;
            return (3 * rest * rest * t) * first + (3 * rest * t * t) * second + t * t * t;
        }
    }

    /// <summary>显式控制点的分段线性曲线；按 X 严格递增，不排序或修复。</summary>
    public sealed record ControlPoints : CurveValue
    {
        /// <summary>建立曲线；首尾必须是 (0,0)、(1,1)，Y 允许有限过冲。</summary>
        public ControlPoints(ImmutableArray<Point2> points)
        {
            if (points.IsDefault || points.Length < 2 || points[0] != new Point2(0, 0) || points[^1] != new Point2(1, 1))
                throw new ArgumentException("曲线必须有首尾 (0,0)、(1,1)。", nameof(points));
            for (int index = 1; index < points.Length; index++)
                if (points[index].X <= points[index - 1].X) throw new ArgumentException("曲线 X 必须严格递增。", nameof(points));
            Points = points;
        }
        /// <summary>固定顺序的完整控制点。</summary>
        public ImmutableArray<Point2> Points { get; }
        private protected override double EvaluateCore(double progress)
        {
            if (progress is 0 or 1) return progress;
            int low = 0, high = Points.Length - 1;
            while (high - low > 1)
            {
                int middle = low + (high - low) / 2;
                if (Points[middle].X <= progress) low = middle; else high = middle;
            }
            Point2 start = Points[low], end = Points[high];
            double fraction = (progress - start.X) / (end.X - start.X);
            // 加权相加避免两有限 Y 相减时先溢出。
            return (1 - fraction) * start.Y + fraction * end.Y;
        }
    }
}

/// <summary>已版本化的规范缓动。</summary>
public enum EasingKind
{
    /// <summary>线性。</summary>
    Linear,
    /// <summary>三次 ease-out。</summary>
    EaseOut,
    /// <summary>平滑三次 ease-in-out：3p²−2p³。</summary>
    EaseInOut,
}
