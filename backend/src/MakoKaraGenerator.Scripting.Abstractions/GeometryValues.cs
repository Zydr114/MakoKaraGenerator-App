namespace MakoKaraGenerator.Scripting;

/// <summary>逻辑空间中的有限二维点。</summary>
public readonly record struct Point2
{
    /// <summary>创建点；拒绝非有限分量。</summary>
    /// <param name="x">横坐标。</param><param name="y">纵坐标。</param>
    public Point2(double x, double y) { Finite(x); Finite(y); X = x; Y = y; }
    /// <summary>横坐标。</summary>
    public double X { get; }
    /// <summary>纵坐标。</summary>
    public double Y { get; }
    internal static void Finite(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "几何分量必须有限。");
    }
}

/// <summary>逻辑空间中的有限二维位移或速度；单位由调用字段说明。</summary>
public readonly record struct Vector2
{
    /// <summary>创建向量。</summary>
    /// <param name="x">横向分量。</param><param name="y">纵向分量。</param>
    public Vector2(double x, double y) { Point2.Finite(x); Point2.Finite(y); X = x; Y = y; }
    /// <summary>横向分量。</summary>
    public double X { get; }
    /// <summary>纵向分量。</summary>
    public double Y { get; }
    /// <summary>零向量。</summary>
    public static Vector2 Zero => default;
}

/// <summary>有限、非负宽高的逻辑矩形；允许空 Advance 或无墨迹。</summary>
public readonly record struct Rect2
{
    /// <summary>创建矩形；不会自动交换边界。</summary>
    /// <param name="left">左端。</param><param name="top">顶端。</param>
    /// <param name="right">右端。</param><param name="bottom">底端。</param>
    public Rect2(double left, double top, double right, double bottom)
    {
        Point2.Finite(left); Point2.Finite(top); Point2.Finite(right); Point2.Finite(bottom);
        if (right < left || bottom < top || !double.IsFinite(right - left) || !double.IsFinite(bottom - top))
            throw new ArgumentException("矩形边界次序或尺寸无效。");
        Left = left; Top = top; Right = right; Bottom = bottom;
    }
    /// <summary>左端。</summary>
    public double Left { get; }
    /// <summary>顶端。</summary>
    public double Top { get; }
    /// <summary>右端。</summary>
    public double Right { get; }
    /// <summary>底端。</summary>
    public double Bottom { get; }
    /// <summary>宽度。</summary>
    public double Width => Right - Left;
    /// <summary>高度。</summary>
    public double Height => Bottom - Top;
    /// <summary>几何中心。</summary>
    public Point2 Center => new(Left + Width / 2, Top + Height / 2);
}

/// <summary>有限二维仿射矩阵；Compose(child) 为本矩阵 × 子矩阵，与后端约定无关。</summary>
public readonly record struct Affine2
{
    /// <summary>创建六分量仿射矩阵。</summary>
    /// <param name="a">横基向量 X。</param><param name="b">横基向量 Y。</param>
    /// <param name="c">纵基向量 X。</param><param name="d">纵基向量 Y。</param>
    /// <param name="tx">横平移。</param><param name="ty">纵平移。</param>
    public Affine2(double a, double b, double c, double d, double tx, double ty)
    {
        Point2.Finite(a); Point2.Finite(b); Point2.Finite(c); Point2.Finite(d); Point2.Finite(tx); Point2.Finite(ty);
        A = a; B = b; C = c; D = d; Tx = tx; Ty = ty;
    }
    /// <summary>横基向量 X。</summary>
    public double A { get; }
    /// <summary>横基向量 Y。</summary>
    public double B { get; }
    /// <summary>纵基向量 X。</summary>
    public double C { get; }
    /// <summary>纵基向量 Y。</summary>
    public double D { get; }
    /// <summary>横平移。</summary>
    public double Tx { get; }
    /// <summary>纵平移。</summary>
    public double Ty { get; }
    /// <summary>恒等矩阵；默认结构为零矩阵，不能当作恒等。</summary>
    public static Affine2 Identity => new(1, 0, 0, 1, 0, 0);
    /// <summary>映射一个逻辑点；结果溢出报告异常。</summary>
    public Point2 Map(Point2 point) => new(A * point.X + C * point.Y + Tx, B * point.X + D * point.Y + Ty);
    /// <summary>按父 × 子组合；结果溢出报告异常。</summary>
    public Affine2 Compose(Affine2 child) => new(A * child.A + C * child.B, B * child.A + D * child.B,
        A * child.C + C * child.D, B * child.C + D * child.D,
        A * child.Tx + C * child.Ty + Tx, B * child.Tx + D * child.Ty + Ty);
    /// <summary>求有限逆矩阵；不可逆、非有限行列式或溢出返回 false，不伪造逆矩阵。</summary>
    public bool TryInverse(out Affine2 inverse)
    {
        inverse = default;
        double det = A * D - B * C;
        if (det == 0 || !double.IsFinite(det)) return false;
        double a = D / det, b = -B / det, c = -C / det, d = A / det,
            tx = (C * Ty - D * Tx) / det, ty = (B * Tx - A * Ty) / det;
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)
            || !double.IsFinite(d) || !double.IsFinite(tx) || !double.IsFinite(ty)) return false;
        inverse = new(a, b, c, d, tx, ty); return true;
    }
    /// <summary>逻辑空间平移。</summary>
    public static Affine2 Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    /// <summary>绕指定逻辑支点等比视觉缩放；必须有限且为正。</summary>
    public static Affine2 ScaleAt(double factor, Point2 pivot)
    {
        Point2.Finite(factor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        return new(factor, 0, 0, factor, pivot.X * (1 - factor), pivot.Y * (1 - factor));
    }
}
