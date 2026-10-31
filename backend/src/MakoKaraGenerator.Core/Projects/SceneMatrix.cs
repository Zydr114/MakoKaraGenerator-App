namespace MakoKaraGenerator.Core.Projects;

/// <summary>画布像素中的一个点。</summary>
/// <param name="X">横坐标。</param>
/// <param name="Y">纵坐标。</param>
public readonly record struct CanvasPoint(double X, double Y);

/// <summary>与图形后端无关的二维仿射矩阵，乘法按父矩阵 × 子矩阵解释。</summary>
/// <param name="A">横向基向量 X。</param>
/// <param name="B">横向基向量 Y。</param>
/// <param name="C">纵向基向量 X。</param>
/// <param name="D">纵向基向量 Y。</param>
/// <param name="Tx">横向平移。</param>
/// <param name="Ty">纵向平移。</param>
public readonly record struct SceneMatrix(double A, double B, double C, double D, double Tx, double Ty)
{
    /// <summary>恒等变换。</summary>
    public static SceneMatrix Identity => new(1, 0, 0, 1, 0, 0);
    /// <summary>映射画布点。</summary>
    public CanvasPoint Map(CanvasPoint point) => new(A * point.X + C * point.Y + Tx, B * point.X + D * point.Y + Ty);
    /// <summary>逆变换画布点。</summary>
    public CanvasPoint Unmap(CanvasPoint point)
    {
        double determinant = A * D - B * C, x = point.X - Tx, y = point.Y - Ty;
        return new((D * x - C * y) / determinant, (-B * x + A * y) / determinant);
    }
    /// <summary>求逆；拒绝不可逆或非有限矩阵。</summary>
    public bool TryInverse(out SceneMatrix inverse)
    {
        double determinant = A * D - B * C;
        inverse = Identity;
        if (!double.IsFinite(determinant) || determinant == 0) return false;
        inverse = new(D / determinant, -B / determinant, -C / determinant, A / determinant,
            (C * Ty - D * Tx) / determinant, (B * Tx - A * Ty) / determinant);
        return double.IsFinite(inverse.Tx) && double.IsFinite(inverse.Ty);
    }
    /// <summary>父子变换组合。</summary>
    public static SceneMatrix operator *(SceneMatrix parent, SceneMatrix child) => new(
        parent.A * child.A + parent.C * child.B, parent.B * child.A + parent.D * child.B,
        parent.A * child.C + parent.C * child.D, parent.B * child.C + parent.D * child.D,
        parent.A * child.Tx + parent.C * child.Ty + parent.Tx, parent.B * child.Tx + parent.D * child.Ty + parent.Ty);
    /// <summary>平移。</summary>
    public static SceneMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    /// <summary>绕原点旋转和等比缩放。</summary>
    public static SceneMatrix RotateScale(double degrees, double scale)
    {
        double radians = degrees * Math.PI / 180, c = Math.Cos(radians) * scale, s = Math.Sin(radians) * scale;
        return new(c, s, -s, c, 0, 0);
    }
    /// <summary>固定参考矩形锚点。</summary>
    public static CanvasPoint Anchor(ScenePlacementAnchor anchor, double width, double height) => anchor switch
    {
        ScenePlacementAnchor.TopLeft => new(0, 0), ScenePlacementAnchor.TopCenter => new(width / 2, 0),
        ScenePlacementAnchor.BottomRight => new(width, height), ScenePlacementAnchor.LeftCenter => new(0, height / 2),
        ScenePlacementAnchor.RightCenter => new(width, height / 2), _ => new(width / 2, height / 2),
    };
    /// <summary>容器静态变换；枢轴由参考矩形定义。</summary>
    public static SceneMatrix Container(LyricBranchPresentation definition, Composition composition)
        => Container(definition.Placement, definition.ReferenceRect, composition);

    /// <summary>场景定义的固定容器矩阵。</summary>
    public static SceneMatrix Container(ScenePlacement placement, SceneReferenceRect referenceRect, Composition composition)
    {
        var anchor = Anchor(placement.Anchor, composition.CanvasWidth * referenceRect.WidthRatio,
            composition.CanvasHeight * referenceRect.HeightRatio);
        return Translate(placement.Position.X * composition.CanvasWidth, placement.Position.Y * composition.CanvasHeight)
            * RotateScale(placement.RotationDegrees, placement.Scale) * Translate(-anchor.X, -anchor.Y);
    }
}
