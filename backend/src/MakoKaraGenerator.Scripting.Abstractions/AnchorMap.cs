namespace MakoKaraGenerator.Scripting;

/// <summary>正弦映射保留的驱动轴；位移作用于另一轴。</summary>
public enum WaveDriveAxis
{
    /// <summary>保持 X，沿 Y 位移。</summary>
    X,
    /// <summary>保持 Y，沿 X 位移。</summary>
    Y,
}

/// <summary>封闭的有限逻辑空间映射值；实际图像执行和绑定由渲染图接入。</summary>
public abstract record AnchorMap
{
    private protected AnchorMap(Rect2 domain)
    {
        if (domain.Width <= 0 || domain.Height <= 0) throw new ArgumentException("锚点映射需要非空有界定义域。", nameof(domain));
        Domain = domain;
    }
    /// <summary>源逻辑空间定义域；域外不静默夹值。</summary>
    public Rect2 Domain { get; }
    /// <summary>正向映射；不会单独改变生产文字或光标。</summary>
    public Point2 Map(Point2 point) { CheckDomain(point); return Forward(point); }
    /// <summary>反向映射；目标不在真实映射域时拒绝。</summary>
    public Point2 Inverse(Point2 point) { var source = Backward(point); CheckDomain(source); return source; }
    /// <summary>在源点处映射切向量；由调用方决定是否单位化。</summary>
    public Vector2 MapVectorAt(Point2 point, Vector2 vector) { CheckDomain(point); return Tangent(point, vector); }
    private protected abstract Point2 Forward(Point2 point);
    private protected abstract Point2 Backward(Point2 point);
    private protected abstract Vector2 Tangent(Point2 point, Vector2 vector);
    private void CheckDomain(Point2 point)
    {
        if (point.X < Domain.Left || point.X > Domain.Right || point.Y < Domain.Top || point.Y > Domain.Bottom)
            throw new EffectOutputException("AnchorMapOutsideDomain", "锚点超出映射的逻辑定义域。");
    }
    private static double Finite(double value)
    {
        if (!double.IsFinite(value)) throw new EffectOutputException("InvalidAnchorMap", "锚点映射运算超出有限逻辑坐标。");
        return value;
    }
    private static Point2 Point(double x, double y) => new(Finite(x), Finite(y));
    private static Vector2 Vector(double x, double y) => new(Finite(x), Finite(y));

    /// <summary>有限且可逆的仿射映射。</summary>
    public sealed record Affine : AnchorMap
    {
        private readonly Affine2 _inverse;
        /// <summary>指定有界逻辑域和可逆矩阵；零矩阵不当作恒等。</summary>
        public Affine(Rect2 domain, Affine2 matrix) : base(domain)
        {
            if (!matrix.TryInverse(out _inverse)) throw new EffectOutputException("InvalidAnchorMap", "仿射锚点映射不可逆。");
            Matrix = matrix;
            _ = Forward(new(domain.Left, domain.Top)); _ = Forward(new(domain.Right, domain.Top));
            _ = Forward(new(domain.Right, domain.Bottom)); _ = Forward(new(domain.Left, domain.Bottom));
        }
        /// <summary>源到目标的逻辑矩阵。</summary>
        public Affine2 Matrix { get; }
        private protected override Point2 Forward(Point2 point) => Apply(Matrix, point);
        private protected override Point2 Backward(Point2 point) => Apply(_inverse, point);
        private protected override Vector2 Tangent(Point2 point, Vector2 vector)
            => Vector(Matrix.A * vector.X + Matrix.C * vector.Y, Matrix.B * vector.X + Matrix.D * vector.Y);
        private static Point2 Apply(Affine2 matrix, Point2 point)
            => Point(matrix.A * point.X + matrix.C * point.Y + matrix.Tx, matrix.B * point.X + matrix.D * point.Y + matrix.Ty);
    }

    /// <summary>保留驱动轴的可逆正弦映射，没有作者 native 回调。</summary>
    public sealed record AxisWave : AnchorMap
    {
        private readonly double _frequency;
        /// <summary>指定逻辑域、驱动轴、逻辑像素幅度／波长、弧度相位及驱动轴原点。</summary>
        public AxisWave(Rect2 domain, WaveDriveAxis driveAxis, double amplitudePx, double wavelengthPx,
            double phaseRadians, double originPx) : base(domain)
        {
            if (!Enum.IsDefined(driveAxis)) throw new ArgumentOutOfRangeException(nameof(driveAxis));
            Point2.Finite(amplitudePx); Point2.Finite(wavelengthPx); Point2.Finite(phaseRadians); Point2.Finite(originPx);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wavelengthPx);
            DriveAxis = driveAxis; AmplitudePx = amplitudePx; WavelengthPx = wavelengthPx; PhaseRadians = phaseRadians; OriginPx = originPx;
            _frequency = Finite(2 * Math.PI / wavelengthPx);
            _ = Finite(amplitudePx * _frequency);
            _ = Angle(driveAxis == WaveDriveAxis.X ? domain.Left : domain.Top);
            _ = Angle(driveAxis == WaveDriveAxis.X ? domain.Right : domain.Bottom);
            _ = Finite((driveAxis == WaveDriveAxis.X ? domain.Top : domain.Left) - Math.Abs(amplitudePx));
            _ = Finite((driveAxis == WaveDriveAxis.X ? domain.Bottom : domain.Right) + Math.Abs(amplitudePx));
        }
        /// <summary>保持不变的驱动轴。</summary>
        public WaveDriveAxis DriveAxis { get; }
        /// <summary>沿另一轴的逻辑像素幅度，可为负。</summary>
        public double AmplitudePx { get; }
        /// <summary>严格正长的逻辑像素波长。</summary>
        public double WavelengthPx { get; }
        /// <summary>弧度相位。</summary>
        public double PhaseRadians { get; }
        /// <summary>驱动轴逻辑原点。</summary>
        public double OriginPx { get; }
        private double Angle(double value) => Finite(Finite(value - OriginPx) * _frequency + PhaseRadians);
        private Point2 Offset(Point2 point, double sign)
        {
            double drive = DriveAxis == WaveDriveAxis.X ? point.X : point.Y;
            double offset = sign * AmplitudePx * Math.Sin(Angle(drive));
            return DriveAxis == WaveDriveAxis.X ? Point(point.X, point.Y + offset) : Point(point.X + offset, point.Y);
        }
        private protected override Point2 Forward(Point2 point) => Offset(point, 1);
        private protected override Point2 Backward(Point2 point) => Offset(point, -1);
        private protected override Vector2 Tangent(Point2 point, Vector2 vector)
        {
            double derivative = AmplitudePx * _frequency * Math.Cos(Angle(DriveAxis == WaveDriveAxis.X ? point.X : point.Y));
            return DriveAxis == WaveDriveAxis.X ? Vector(vector.X, vector.Y + derivative * vector.X)
                : Vector(vector.X + derivative * vector.Y, vector.Y);
        }
    }
}
