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
    /// <summary>整行唯一整形的真实绘制单元身份。</summary>
    public ImmutableArray<DrawUnitId> DrawUnits { get; init; } = [];
    /// <summary>是否所有字形完全落在所绑定源元素内；不是以 UTF-16 剪开的视觉区域。</summary>
    public bool CanTransformIndependently { get; init; }
    /// <summary>不能独立变形时的稳定诊断代码。</summary>
    public string? DiagnosticCode { get; init; }
    /// <summary>实际墨迹世界角点；空墨迹返回空，不隐式换成 Advance。</summary>
    public ImmutableArray<Point2> WorldInkCorners => InkBounds is { } ink
        ? [BaseWorldMatrix.Map(new(ink.Left, ink.Top)), BaseWorldMatrix.Map(new(ink.Right, ink.Top)),
            BaseWorldMatrix.Map(new(ink.Right, ink.Bottom)), BaseWorldMatrix.Map(new(ink.Left, ink.Bottom))] : [];
}

/// <summary>注音真实几何与身份；不拥有独立演唱进度或假 TokenId。</summary>
/// <param name="RubyId">真实注音。</param><param name="Geometry">逻辑位置与基础文字共用世界矩阵。</param>
public sealed record RubyGeometrySample(RubyId RubyId, GeometrySample Geometry);

/// <summary>一个基础元素的完整只读绘制族；基础 Advance 保持，族墨迹可包含注音。</summary>
public sealed partial record TokenGeometrySample
{
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
