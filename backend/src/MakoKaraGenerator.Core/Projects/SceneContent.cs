using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>节点自有内容，与源歌词身份及演唱时间分离。</summary>
public abstract record SceneContent;

/// <summary>提示容器的图形形态。</summary>
public enum CountdownShapeKind
{
    /// <summary>沿用标记自身的字符整形绘制。</summary>
    Text,
    /// <summary>矢量正圆，支持 1（单图标）或 N 个圆点。</summary>
    Circle,
    /// <summary>左侧高、右侧递减的阶梯圆角竖条（经典卡拉 OK 多拍样式）。</summary>
    SteppedBars,
    /// <summary>矢量圆角胶囊条。</summary>
    Capsule,
    /// <summary>矢量菱形。</summary>
    Diamond,
    /// <summary>矢量五角星。</summary>
    Star,
    /// <summary>自定义静态 SVG 图形，遵循 mako-svg-static 安全子集。</summary>
    CustomSvg,
}

/// <summary>自定义 SVG 的着色模式。</summary>
public enum CountdownSvgColorMode
{
    /// <summary>将 SVG 路径提取为矢量轮廓，直接套用提示组的两态填充、多层描边、阴影与辉光。</summary>
    SilhouetteStyle,
    /// <summary>保留 SVG 原始配色与细节（未经过态淡显，经过态平滑全彩显现）。</summary>
    OriginalColor,
}

/// <summary>前奏提示组的统一图形设置。</summary>
public sealed record CountdownShapeSpec
{
    /// <summary>内嵌自定义 SVG 源码的最大 UTF-8 字节数（64 KiB）。</summary>
    public const int MaximumSvgBytes = 64 * 1024;
    /// <summary>默认保持兼容旧版文字字符形态，避免改变既有确定性用例。</summary>
    public static CountdownShapeSpec Default { get; } = new();

    /// <summary>图形类型。</summary>
    public CountdownShapeKind Kind { get; init; } = CountdownShapeKind.Text;
    /// <summary>自定义 SVG XML 源码；仅 <see cref="CountdownShapeKind.CustomSvg"/> 时非空。</summary>
    public string? SvgXml { get; init; }
    /// <summary>自定义 SVG 着色模式。</summary>
    public CountdownSvgColorMode SvgColorMode { get; init; } = CountdownSvgColorMode.SilhouetteStyle;
    /// <summary><see cref="CountdownShapeKind.SteppedBars"/> 最末一条竖条的相对高度占比（0.15..1）。</summary>
    public double BarMinHeightRatio { get; init; } = 0.45;
}

/// <summary>前奏提示组的动画曲线与时间切换方式。</summary>
public enum CountdownAnimationMode
{
    /// <summary>传统等时逐个瞬时切换未经过／已经过状态。</summary>
    DiscreteSteps,
    /// <summary>先全体柔和闪烁提示，随后按倒计时顺序连续水平渐进填充过渡。</summary>
    BlinkThenWipeFill,
    /// <summary>先全体柔和闪烁提示，随后按倒计时顺序逐个平滑擦除消失。</summary>
    BlinkThenErase,
}

/// <summary>前奏提示组动画配置（模式＋3 个轻量参数）。</summary>
public sealed record CountdownAnimationSpec
{
    /// <summary>默认保持离散等分步进，兼容既有基线渲染契约；新建工作流可显式选用 <see cref="CountdownAnimationMode.BlinkThenWipeFill"/>。</summary>
    public static CountdownAnimationSpec Default { get; } = new();

    /// <summary>动画模式。</summary>
    public CountdownAnimationMode Mode { get; init; } = CountdownAnimationMode.DiscreteSteps;
    /// <summary>前段闪烁阶段占整条前奏时长的比例（0..0.85）。</summary>
    public double BlinkRatio { get; init; } = 0.35;
    /// <summary>闪烁阶段完成的呼吸循环次数（0..16）。</summary>
    public int BlinkCount { get; init; } = 2;
    /// <summary>单个标记水平渐进擦除边缘羽化占比（0..0.5）。</summary>
    public double FeatherRatio { get; init; } = 0.06;
}

/// <summary>文字标记的有序倒计时组，数量由标记引用得到。</summary>
public sealed record CountdownSequenceContent : SceneContent
{
    private ImmutableArray<string> _markNodeIds;
    /// <summary>按倒计时次序排列的直接子标记。</summary>
    public ImmutableArray<string> MarkNodeIds { get => _markNodeIds.IsDefault ? [] : _markNodeIds; init => _markNodeIds = value; }
    /// <summary>所有子标记的外观基线，不作为父级透明度再次相乘。</summary>
    public SceneTextAppearance Appearance { get; init; } = SceneTextAppearance.Default;
    /// <summary>提示标记的统一图形与自定义 SVG 配置。</summary>
    public CountdownShapeSpec Shape { get; init; } = CountdownShapeSpec.Default;
    /// <summary>提示组闪烁与平滑擦除动画配置。</summary>
    public CountdownAnimationSpec Animation { get; init; } = CountdownAnimationSpec.Default;
    /// <summary>按内容比较引用序列。</summary>
    public bool Equals(CountdownSequenceContent? other) => other is not null
        && Appearance == other.Appearance && Shape == other.Shape && Animation == other.Animation
        && MarkNodeIds.SequenceEqual(other.MarkNodeIds);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Appearance);
        hash.Add(Shape);
        hash.Add(Animation);
        foreach (var id in MarkNodeIds) hash.Add(id);
        return hash.ToHashCode();
    }
}

/// <summary>场景文字标记，不携带源歌词元素的时间或状态。</summary>
public sealed record TextMarkContent : SceneContent
{
    /// <summary>完整整形的标记文字。</summary>
    public string Text { get; init; } = "●";
    /// <summary>相对于提示组外观的局部覆盖。</summary>
    public SceneTextAppearanceOverride? AppearanceOverride { get; init; }
}

/// <summary>提示容器的位置参考方式。</summary>
public enum SceneAttachmentMode
{
    /// <summary>使用持久父组的参考矩形。</summary>
    FixedParent,
    /// <summary>运行时附着前奏目标句行首左侧并垂直居中于主字墨迹，包含其槽位、微调和动画。</summary>
    CueTargetText,
    /// <summary>运行时附着前奏目标句首字上方，水平对齐主字左边缘并自动避让注音顶边。</summary>
    CueTargetAboveStart,
}

/// <summary>运行附着声明；位置仍只存于节点 Placement。</summary>
public sealed record SceneAttachment
{
    /// <summary>位置参考方式。</summary>
    public SceneAttachmentMode Mode { get; init; }
}
