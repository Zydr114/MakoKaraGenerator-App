using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>真实容器或槽位，不能用源行 ID 冒充。</summary>
/// <param name="NodeId">真实组节点。</param>
public sealed record GroupTarget(NodeId NodeId);

/// <summary>真实文字标记的一次提示装载，不伪造演唱元素。</summary>
/// <param name="NodeId">标记节点。</param><param name="CueId">真实前奏。</param><param name="BindingId">目标句装载。</param>
public sealed record TextMarkTarget(NodeId NodeId, CueId CueId, BindingId BindingId);

/// <summary>当前真实标记，没有 TokenId 或独立演唱时间。</summary>
/// <param name="Target">标记与提示装载。</param><param name="TargetLine">准备中的真实源行装载。</param>
/// <param name="ParentId">本帧父提示组。</param><param name="Text">节点自有文字。</param>
/// <param name="IsElapsed">倒计时是否已经过此标记。</param><param name="AnimationOpacity">动态附着透明度，不包含自身静态样式。</param>
public sealed record FrameMark(TextMarkTarget Target, FrameLine TargetLine, NodeId ParentId, string Text, bool IsElapsed, double AnimationOpacity);

/// <summary>本帧真实组身份及父节点；持久父树仍单独保存在 SceneCatalog。</summary>
/// <param name="Target">真实组。</param><param name="ParentId">运行父节点，附着提示可指向真实文本。</param>
/// <param name="IsVisible">本帧是否有实际后代。</param><param name="AnimationOpacity">动态透明度，独立于静态组基线。</param>
public sealed record FrameGroup(GroupTarget Target, NodeId? ParentId, bool IsVisible, double AnimationOpacity);

/// <summary>真实标记整行缓存的固定字形几何，没有假 Token 进度。</summary>
/// <param name="Target">一次提示标记装载。</param><param name="Geometry">标记自己的逻辑／世界几何。</param>
public sealed record TextMarkGeometrySample(TextMarkTarget Target, GeometrySample Geometry);

/// <summary>真实组参考框与矩阵，后代墨迹不改变该组位置或枢轴。</summary>
public sealed record GroupGeometrySample
{
    /// <summary>真实组目标。</summary>
    public required GroupTarget Target { get; init; }
    /// <summary>组自己的逻辑参考框，不是后代墨迹联合。</summary>
    public required Rect2 ReferenceBounds { get; init; }
    /// <summary>同一后端的运行世界矩阵。</summary>
    public required Affine2 BaseWorldMatrix { get; init; }
    /// <summary>同一位置解析器的画布枢轴。</summary>
    public required Point2 WorldPivot { get; init; }
    /// <summary>实际可见后代的画布墨迹角点；无后代为空，不伪造矩形。</summary>
    public ImmutableArray<Point2> WorldInkCorners { get; init; } = [];
}

/// <summary>真实标记的一次局部视觉变换。</summary>
/// <param name="Target">一次标记装载。</param><param name="LocalMatrix">标记逻辑空间的有限可逆矩阵。</param>
public sealed record TextMarkTransform(TextMarkTarget Target, Affine2 LocalMatrix);

/// <summary>标记 Pending／Elapsed 的两态外观输出，没有演唱事件。</summary>
/// <param name="Target">一次标记装载。</param><param name="Update">明确两态和装饰的外观描述。</param>
public sealed record TextMarkStyle(TextMarkTarget Target, StyleUpdate Update);

/// <summary>组没有文字材质；此值只表示固定组不透明度。</summary>
public sealed record GroupStyleView
{
    /// <summary>建立有限 [0,1] 的静态组 alpha。</summary>
    public GroupStyleView(double opacity) { ColorRgba.Unit(opacity); Opacity = opacity; }
    /// <summary>静态组 alpha，不含附着动画或脚本输出。</summary>
    public double Opacity { get; }
}
