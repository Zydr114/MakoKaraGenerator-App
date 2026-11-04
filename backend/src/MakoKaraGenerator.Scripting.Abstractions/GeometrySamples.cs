using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>一个真实装载中的基础字与全部关联注音；不是持久字号／排版。</summary>
/// <param name="TokenId">真实源元素。</param><param name="BindingId">当前装载身份。</param>
public sealed record TokenFamilyTarget(TokenId TokenId, BindingId BindingId);

/// <summary>一个真实装载中的基础字；显式不含注音。</summary>
/// <param name="TokenId">真实源元素。</param><param name="BindingId">当前装载身份。</param>
public sealed record BaseTokenTarget(TokenId TokenId, BindingId BindingId);

/// <summary>固定排版的只读文字几何；逻辑坐标由 BaseWorldMatrix 一次映射到画布。</summary>
public sealed record GeometrySample
{
    /// <summary>真实 Advance 矩形，含实际字体 ascent／descent；族缩放枢轴只取基础字此框。</summary>
    public required Rect2 AdvanceBounds { get; init; }
    /// <summary>实际墨迹联合，不含描边／阴影；空白元素为空，不以 Advance 伪造墨迹。</summary>
    public Rect2? InkBounds { get; init; }
    /// <summary>基础文字的逻辑基线起点。</summary>
    public required Point2 Baseline { get; init; }
    /// <summary>已有父组／槽位／单句／进入动画最终矩阵，不再乘一次父组。</summary>
    public required Affine2 BaseWorldMatrix { get; init; }
    /// <summary>含网格时的源逻辑到画布有序闭包；为空时保留原 affine 快路。</summary>
    public TextGeometryMapping? Mapping { get; init; }
    /// <summary>已经受全帧剪交账约束的最终墨迹投影；default 表示按原逻辑区域求值。</summary>
    public ImmutableArray<Point2> MappedInkVertices { get; init; }
    /// <summary>将源逻辑位置映射到实际画布；网格与 affine 共用此入口。</summary>
    public Point2 MapPoint(Point2 point) => Mapping?.MapPoint(point) ?? BaseWorldMatrix.Map(point);
    /// <summary>映射源点处的方向，不加入平移；网格使用同三角 Jacobian。</summary>
    public Vector2 MapVectorAt(Point2 point, Vector2 vector) => Mapping?.MapVectorAt(point, vector)
        ?? new(BaseWorldMatrix.A * vector.X + BaseWorldMatrix.C * vector.Y,
            BaseWorldMatrix.B * vector.X + BaseWorldMatrix.D * vector.Y);
    /// <summary>整行唯一整形的真实绘制单元身份。</summary>
    public ImmutableArray<DrawUnitId> DrawUnits { get; init; } = [];
    /// <summary>是否所有字形完全落在所绑定源元素内；不是以 UTF-16 剪开的视觉区域。</summary>
    public bool CanTransformIndependently { get; init; }
    /// <summary>不能独立变形时的稳定诊断代码。</summary>
    public string? DiagnosticCode { get; init; }
    /// <summary>实际墨迹世界角点；空墨迹返回空，不隐式换成 Advance。</summary>
    public ImmutableArray<Point2> WorldInkCorners => !MappedInkVertices.IsDefault ? MappedInkVertices
        : InkBounds is { } ink && Mapping is { } mapping ? mapping.MapRectangle(ink)
        : InkBounds is { } plain
        ? [BaseWorldMatrix.Map(new(plain.Left, plain.Top)), BaseWorldMatrix.Map(new(plain.Right, plain.Top)),
            BaseWorldMatrix.Map(new(plain.Right, plain.Bottom)), BaseWorldMatrix.Map(new(plain.Left, plain.Bottom))] : [];
}

/// <summary>注音真实几何与身份；不拥有独立演唱进度或假 TokenId。</summary>
/// <param name="RubyId">真实注音。</param><param name="Geometry">逻辑位置与基础文字共用世界矩阵。</param>
public sealed record RubyGeometrySample(RubyId RubyId, GeometrySample Geometry);

/// <summary>一个基础元素的完整只读绘制族；基础 Advance 保持，族墨迹可包含注音。</summary>
public sealed partial record TokenGeometrySample
{
    /// <summary>按原 Advance 的半开横向范围选择真实基础字簇；末端归最后单元，空白仍走基础字映射。</summary>
    public GeometrySample GeometryAt(Point2 point)
    {
        var clusters = Clusters.Where(static cluster => cluster.Target.RubyId is null && cluster.OwnerTokens.Length == 1)
            .OrderBy(static cluster => cluster.Geometry.AdvanceBounds.Left).ThenBy(static cluster => cluster.Target.UnitIndex).ToArray();
        var selected = clusters.FirstOrDefault(cluster => point.X >= cluster.Geometry.AdvanceBounds.Left
            && point.X < cluster.Geometry.AdvanceBounds.Right);
        if (selected is null && clusters.Length > 0 && point.X == clusters[^1].Geometry.AdvanceBounds.Right) selected = clusters[^1];
        return selected?.Geometry ?? BaseGeometry;
    }
    /// <summary>与最终 cursor 和字簇像素使用同一叶映射的位置。</summary>
    public Point2 MapPoint(Point2 point) => GeometryAt(point).MapPoint(point);
    /// <summary>与最终 cursor 和字簇像素使用同一叶映射的向量。</summary>
    public Vector2 MapVectorAt(Point2 point, Vector2 vector) => GeometryAt(point).MapVectorAt(point, vector);
    /// <summary>真实源分支。</summary>
    public required BranchId BranchId { get; init; }
    /// <summary>真实源行。</summary>
    public required LineId LineId { get; init; }
    /// <summary>基础字及其注音的合法目标。</summary>
    public required TokenFamilyTarget WithRuby { get; init; }
    /// <summary>只修改基础字的显式目标。</summary>
    public required BaseTokenTarget BaseOnly { get; init; }
    /// <summary>基础字的真实 Advance／墨迹。</summary>
    public required GeometrySample BaseGeometry { get; init; }
    /// <summary>真实高亮边界路径；文本映射不可用时为 null，不用墨迹框伪造。</summary>
    public TextProgressPath? ProgressPath { get; init; }
    /// <summary>基础字与注音的联合墨迹；Advance／Baseline 仍取基础字。</summary>
    public required GeometrySample FamilyGeometry { get; init; }
    /// <summary>各注音单独几何，不用联合框推测中心。</summary>
    public ImmutableArray<RubyGeometrySample> Ruby { get; init; } = [];
}
