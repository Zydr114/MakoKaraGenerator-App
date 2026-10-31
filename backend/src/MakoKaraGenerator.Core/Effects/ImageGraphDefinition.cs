using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>逻辑图坐标空间；空间变换需显式适配。</summary>
public enum GraphCoordinateSpace
{
    /// <summary>基础字坐标。</summary>
    Token,
    /// <summary>行坐标。</summary>
    Line,
    /// <summary>节点坐标。</summary>
    Node,
    /// <summary>合成画布坐标。</summary>
    Canvas,
}
/// <summary>显式采样边缘。</summary>
public enum GraphEdgeMode
{
    /// <summary>域外透明。</summary>
    Transparent,
    /// <summary>域外重复边缘像素。</summary>
    Clamp,
    /// <summary>重复整个图像。</summary>
    Repeat,
}
/// <summary>被捕获输入的真实来源。</summary>
public enum GraphInputSource
{
    /// <summary>字形覆盖。</summary>
    GlyphCoverage,
    /// <summary>外部描边覆盖。</summary>
    StrokeCoverage,
    /// <summary>已唱覆盖。</summary>
    SungCoverage,
    /// <summary>当前填充／描边目标区域覆盖。</summary>
    TargetRegionCoverage,
    /// <summary>当前目标处理前图像。</summary>
    TargetColor,
    /// <summary>只含定位后背景媒体。</summary>
    BackgroundMedia,
    /// <summary>目标之前已合成内容，排除目标自身。</summary>
    BackdropBelow,
    /// <summary>背景底色。</summary>
    BackgroundColor,
    /// <summary>固定包图片。</summary>
    ResourceImage,
}
/// <summary>真实输入依赖；不能按文件名猜测。</summary>
public enum GraphBackgroundRequirement
{
    /// <summary>透明模式仍可执行。</summary>
    None,
    /// <summary>依赖背景媒体，透明输出须旁路。</summary>
    Media,
    /// <summary>依赖不透明画布，透明输出须旁路。</summary>
    OpaqueCanvas,
}
/// <summary>图像的语义锚点约定。</summary>
public enum GraphAnchorMode
{
    /// <summary>不改变语义位置。</summary>
    ColorOnly,
    /// <summary>由另外的共享映射提供语义位置。</summary>
    MappedWarp,
    /// <summary>没有唯一视觉位置，不能伪造映射。</summary>
    ImageOnly,
}
/// <summary>普通预乘混合。</summary>
public enum GraphBlendMode
{
    /// <summary>前景覆盖背景。</summary>
    SourceOver,
    /// <summary>分量相加。</summary>
    Plus,
    /// <summary>乘法混合。</summary>
    Multiply,
}
/// <summary>图像最终合成配方。</summary>
public enum GraphCompositeMode
{
    /// <summary>普通 source over。</summary>
    SourceOver,
    /// <summary>独立覆盖度的替换，产物已经乘过覆盖度。</summary>
    ReplaceCoveredBackdrop,
}
/// <summary>图的有限逻辑框；校验不交换边界。</summary>
/// <param name="Left">左。</param><param name="Top">上。</param><param name="Right">右。</param><param name="Bottom">下。</param>
public readonly record struct GraphRect(double Left, double Top, double Right, double Bottom)
{
    /// <summary>宽度。</summary>
    public double Width => Right - Left;
    /// <summary>高度。</summary>
    public double Height => Bottom - Top;
    /// <summary>非空、有限且次序正确。</summary>
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) && double.IsFinite(Bottom)
        && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
    /// <summary>按逻辑距离外扩，不夹值。</summary>
    public GraphRect Expand(double value) => new(Left - value, Top - value, Right + value, Bottom + value);
    /// <summary>完整包含另一框。</summary>
    public bool Contains(GraphRect other) => other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}
/// <summary>只指向显式输入或节点的边。</summary>
/// <param name="Id">真实身份。</param><param name="IsInput">true 为输入，false 为节点。</param>
public readonly record struct GraphReference(string Id, bool IsInput)
{
    /// <summary>引用输入。</summary>
    public static GraphReference Input(string id) => new(id, true);
    /// <summary>引用节点；允许指向后定义节点。</summary>
    public static GraphReference Node(string id) => new(id, false);
}
/// <summary>输入声明，不携带可变纹理。</summary>
public sealed record GraphInputDefinition
{
    /// <summary>复制来源选项；choiceParameter 为空时用固定 source。</summary>
    public GraphInputDefinition(string id, GraphInputSource source, ShaderSurfaceKind kind, bool required,
        GraphBackgroundRequirement backgroundRequirement, GraphCoordinateSpace space, string? choiceParameter = null,
        IEnumerable<KeyValuePair<string, GraphInputSource>>? choices = null, string? resourceId = null)
    {
        Id = id; Source = source; Kind = kind; Required = required; BackgroundRequirement = backgroundRequirement; Space = space;
        ChoiceParameter = choiceParameter; Choices = (choices ?? []).ToImmutableDictionary(StringComparer.Ordinal); ResourceId = resourceId;
    }
    /// <summary>输入身份。</summary>
    public string Id { get; }
    /// <summary>非条件时的明确来源。</summary>
    public GraphInputSource Source { get; }
    /// <summary>明确颜色／原始覆盖编码。</summary>
    public ShaderSurfaceKind Kind { get; }
    /// <summary>是否必需；可选缺失策略不暗中推断。</summary>
    public bool Required { get; }
    /// <summary>输出模式依赖。</summary>
    public GraphBackgroundRequirement BackgroundRequirement { get; }
    /// <summary>输入空间。</summary>
    public GraphCoordinateSpace Space { get; }
    /// <summary>已声明的 enum 参数；为空时不得带 choices。</summary>
    public string? ChoiceParameter { get; }
    /// <summary>每个 enum 选项的来源。</summary>
    public ImmutableDictionary<string, GraphInputSource> Choices { get; }
    /// <summary>ResourceImage 的受管图片身份，不是本地路径。</summary>
    public string? ResourceId { get; }
}
/// <summary>每节点必须声明的取样／外扩边界。</summary>
/// <param name="Expand">实际外扩。</param><param name="MaximumExpandPx">声明最大外扩。</param>
/// <param name="MaximumSampleOffsetPx">声明最大采样偏移。</param><param name="Edge">域外采样。</param>
public sealed record GraphNodeBounds(GraphExpansion Expand, double MaximumExpandPx, double MaximumSampleOffsetPx, GraphEdgeMode Edge);

/// <summary>封闭、版本化图操作；不限制 shader 算法名称。</summary>
public abstract class ImageGraphNode
{
    /// <summary>固定节点身份及强制边界。</summary>
    protected ImageGraphNode(string id, GraphNodeBounds bounds) { Id = id; Bounds = bounds; }
    /// <summary>唯一节点身份。</summary>
    public string Id { get; }
    /// <summary>完整边界声明。</summary>
    public GraphNodeBounds Bounds { get; }
    /// <summary>固定源码的 shader 操作。</summary>
    public sealed class RuntimeShader : ImageGraphNode
    {
        /// <summary>复制全部 uniform／child 边；程序由包固定目录提供。</summary>
        public RuntimeShader(string id, string programId, ShaderSurfaceKind outputKind, IEnumerable<KeyValuePair<string, GraphUniformBinding>> uniforms,
            IEnumerable<KeyValuePair<string, GraphReference>> children, GraphNodeBounds bounds) : base(id, bounds)
        { ProgramId = programId; OutputKind = outputKind; Uniforms = uniforms.ToImmutableDictionary(StringComparer.Ordinal); Children = children.ToImmutableDictionary(StringComparer.Ordinal); }
        /// <summary>固定程序身份。</summary>
        public string ProgramId { get; }
        /// <summary>产物类型。</summary>
        public ShaderSurfaceKind OutputKind { get; }
        /// <summary>类型明确的值绑定。</summary>
        public ImmutableDictionary<string, GraphUniformBinding> Uniforms { get; }
        /// <summary>命名输入边。</summary>
        public ImmutableDictionary<string, GraphReference> Children { get; }
    }
    /// <summary>高斯模糊，只接受颜色图。</summary>
    public sealed class Blur : ImageGraphNode
    {
        /// <summary>固定输入、逻辑 sigma 和边界。</summary>
        public Blur(string id, GraphReference input, GraphNumberBinding sigmaPx, GraphNodeBounds bounds) : base(id, bounds) { Input = input; SigmaPx = sigmaPx; }
        /// <summary>颜色输入。</summary>
        public GraphReference Input { get; }
        /// <summary>逻辑 sigma。</summary>
        public GraphNumberBinding SigmaPx { get; }
    }
    /// <summary>乘一次 raw alpha 覆盖。</summary>
    public sealed class Mask : ImageGraphNode
    {
        /// <summary>固定颜色和覆盖输入。</summary>
        public Mask(string id, GraphReference color, GraphReference coverage, GraphNodeBounds bounds) : base(id, bounds) { Color = color; Coverage = coverage; }
        /// <summary>颜色。</summary>
        public GraphReference Color { get; }
        /// <summary>覆盖。</summary>
        public GraphReference Coverage { get; }
    }
    /// <summary>有限仿射，输出类型与输入相同。</summary>
    public sealed class Transform : ImageGraphNode
    {
        /// <summary>固定源至目标矩阵和显式输出框。</summary>
        public Transform(string id, GraphReference input, SceneMatrix matrix, GraphRect outputBounds, GraphNodeBounds bounds) : base(id, bounds) { Input = input; Matrix = matrix; OutputBounds = outputBounds; }
        /// <summary>输入。</summary>
        public GraphReference Input { get; }
        /// <summary>源至目标矩阵。</summary>
        public SceneMatrix Matrix { get; }
        /// <summary>明确的有限输出框。</summary>
        public GraphRect OutputBounds { get; }
    }
    /// <summary>两张颜色图的预乘混合。</summary>
    public sealed class Blend : ImageGraphNode
    {
        /// <summary>固定前景、背景和混合方式。</summary>
        public Blend(string id, GraphReference foreground, GraphReference background, GraphBlendMode mode, GraphNodeBounds bounds) : base(id, bounds) { Foreground = foreground; Background = background; Mode = mode; }
        /// <summary>前景。</summary>
        public GraphReference Foreground { get; }
        /// <summary>背景。</summary>
        public GraphReference Background { get; }
        /// <summary>混合方式。</summary>
        public GraphBlendMode Mode { get; }
    }
}
/// <summary>固定、不可变的图定义，不改变项目源数据。</summary>
public sealed class ImageGraphDefinition
{
    /// <summary>固定图协议版本。</summary>
    public const int CurrentVersion = 1;
    /// <summary>复制图定义；验证由唯一编译器完成。</summary>
    public ImageGraphDefinition(string id, int version, IEnumerable<GraphInputDefinition> inputs, IEnumerable<string> parameterIds,
        IEnumerable<ImageGraphNode> nodes, GraphReference output, GraphCoordinateSpace space, double maximumExpandPx,
        GraphAnchorMode anchorMode, GraphCompositeMode composite, GraphReference? compositeCoverage = null, GraphReference? bypassInput = null)
    {
        Id = id; Version = version; Inputs = [.. inputs]; ParameterIds = [.. parameterIds]; Nodes = [.. nodes]; Output = output;
        Space = space; MaximumExpandPx = maximumExpandPx; AnchorMode = anchorMode; Composite = composite;
        CompositeCoverage = compositeCoverage; BypassInput = bypassInput;
    }
    /// <summary>图身份。</summary>
    public string Id { get; }
    /// <summary>格式版本。</summary>
    public int Version { get; }
    /// <summary>明确输入。</summary>
    public ImmutableArray<GraphInputDefinition> Inputs { get; }
    /// <summary>引用 owning 包参数，不复制默认值。</summary>
    public ImmutableArray<string> ParameterIds { get; }
    /// <summary>所有操作，包括暂时不可达操作也验证。</summary>
    public ImmutableArray<ImageGraphNode> Nodes { get; }
    /// <summary>最终产物边。</summary>
    public GraphReference Output { get; }
    /// <summary>图的逻辑空间。</summary>
    public GraphCoordinateSpace Space { get; }
    /// <summary>累计最大外扩。</summary>
    public double MaximumExpandPx { get; }
    /// <summary>语义锚点声明。</summary>
    public GraphAnchorMode AnchorMode { get; }
    /// <summary>最后合成规则。</summary>
    public GraphCompositeMode Composite { get; }
    /// <summary>替换合成的独立覆盖边。</summary>
    public GraphReference? CompositeCoverage { get; }
    /// <summary>背景依赖输出的明确原 TargetColor 旁路。</summary>
    public GraphReference? BypassInput { get; }
}
