using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>由实际渲染后端提供行几何，供布局编辑使用，不另建界面排版规则。</summary>
public interface ILineGeometryProvider
{
    /// <summary>取得静态行几何（不含进入动画）；坐标单位是合成画布像素。</summary>
    /// <param name="snapshot">工程快照。</param>
    /// <param name="lineId">稳定行 ID。</param>
    /// <returns>有效行的几何，或空值。</returns>
    LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId);

    /// <summary>取得共用场景帧，静态编辑时传入源行 ID。</summary>
    SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null);

    /// <summary>按稳定节点身份取得容器几何，纯组和空槽位也可以编辑。</summary>
    LineRenderGeometry? GetNodeGeometry(ProjectSnapshot snapshot, string nodeId, string? editingLineId = null)
        => SceneEditorGeometry.ForNode(snapshot, GetSceneFrame(snapshot, 0, editingLineId), nodeId);

    /// <summary>取得单句微调的拖动几何；坐标数值为相对于基础布局的偏移。</summary>
    LineRenderGeometry? GetLocalTransformGeometry(ProjectSnapshot snapshot, string lineId)
        => SceneEditorGeometry.ForLine(snapshot, GetSceneFrame(snapshot, 0, lineId), lineId);
}

/// <summary>渲染与编辑共用的静态行几何；字形边界不包含装饰效果外扩。</summary>
public sealed record LineRenderGeometry
{
    /// <summary>画布目标的稳定 ID。</summary>
    public string? ElementId { get; init; }
    /// <summary>所属容器 ID。</summary>
    public string? ContainerId { get; init; }
    /// <summary>本次槽位绑定 ID。</summary>
    public string? BindingId { get; init; }
    /// <summary>最终世界矩阵；没有提供时使用手工构造的行几何计算。</summary>
    public SceneMatrix? WorldMatrix { get; init; }
    /// <summary>编辑目标所在父坐标到画布的变换。</summary>
    public SceneMatrix EditCoordinateTransform { get; init; } = SceneMatrix.Identity;
    /// <summary>编辑参考宽度。</summary>
    public double EditReferenceWidth { get; init; }
    /// <summary>编辑参考高度。</summary>
    public double EditReferenceHeight { get; init; }
    /// <summary>行 ID。</summary>
    public required string LineId { get; init; }
    /// <summary>有效位置，未覆盖时包含默认排版换算得到的锚点。</summary>
    public required ScenePlacement Placement { get; init; }
    /// <summary>文字绘制原点横坐标。</summary>
    public double OriginX { get; init; }
    /// <summary>文字绘制基线。</summary>
    public double BaselineY { get; init; }
    /// <summary>变换支点。</summary>
    public CanvasPoint Pivot { get; init; }
    /// <summary>样式和位置共同产生的缩放。</summary>
    public double Scale { get; init; } = 1;
    /// <summary>旋转角度。</summary>
    public double RotationDegrees { get; init; }
    /// <summary>字形边界左上角，变换之前。</summary>
    public CanvasPoint TopLeft { get; init; }
    /// <summary>字形边界右下角，变换之前。</summary>
    public CanvasPoint BottomRight { get; init; }
    /// <summary>不含注音的主字墨迹左上角（变换前）；未提供时回退至 <see cref="TopLeft"/>。</summary>
    public CanvasPoint? BaseTopLeft { get; init; }
    /// <summary>不含注音的主字墨迹右下角（变换前）；未提供时回退至 <see cref="BottomRight"/>。</summary>
    public CanvasPoint? BaseBottomRight { get; init; }
    /// <summary>实际可用的主字墨迹左上角。</summary>
    public CanvasPoint EffectiveBaseTopLeft => BaseTopLeft ?? TopLeft;
    /// <summary>实际可用的主字墨迹右下角。</summary>
    public CanvasPoint EffectiveBaseBottomRight => BaseBottomRight ?? BottomRight;

    /// <summary>把未变换画布点映射为实际画布点。</summary>
    /// <param name="point">未变换的点。</param>
    /// <returns>缩放、旋转之后的点。</returns>
    public CanvasPoint Transform(CanvasPoint point)
    {
        if (WorldMatrix is { } matrix) return matrix.Map(point);
        double radians = RotationDegrees * Math.PI / 180;
        double x = (point.X - Pivot.X) * Scale;
        double y = (point.Y - Pivot.Y) * Scale;
        return new(Pivot.X + x * Math.Cos(radians) - y * Math.Sin(radians),
            Pivot.Y + x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    /// <summary>取得四个经过变换的字形边界角点，按顺时针排序。</summary>
    /// <returns>四角的画布像素坐标。</returns>
    public IReadOnlyList<CanvasPoint> Corners() =>
    [Transform(TopLeft), Transform(new(BottomRight.X, TopLeft.Y)),
        Transform(BottomRight), Transform(new(TopLeft.X, BottomRight.Y))];

    /// <summary>按逆变换检查点是否命中行边界，支持小字号点击余量。</summary>
    /// <param name="point">画布像素点。</param>
    /// <param name="padding">画布像素点击余量。</param>
    /// <returns>是否在边界内部。</returns>
    public bool Contains(CanvasPoint point, double padding = 0)
    {
        if (WorldMatrix is { } matrix)
        {
            var local = matrix.Unmap(point);
            double scale = Math.Sqrt(matrix.A * matrix.A + matrix.B * matrix.B);
            padding += 1e-8;
            return local.X >= TopLeft.X - padding / scale && local.X <= BottomRight.X + padding / scale
                && local.Y >= TopLeft.Y - padding / scale && local.Y <= BottomRight.Y + padding / scale;
        }
        double radians = -RotationDegrees * Math.PI / 180;
        double x = point.X - Pivot.X, y = point.Y - Pivot.Y;
        double localX = Pivot.X + (x * Math.Cos(radians) - y * Math.Sin(radians)) / Scale;
        double localY = Pivot.Y + (x * Math.Sin(radians) + y * Math.Cos(radians)) / Scale;
        return localX >= TopLeft.X - padding / Scale && localX <= BottomRight.X + padding / Scale
            && localY >= TopLeft.Y - padding / Scale && localY <= BottomRight.Y + padding / Scale;
    }
}
