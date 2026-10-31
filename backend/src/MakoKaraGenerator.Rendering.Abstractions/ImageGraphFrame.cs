using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>中立图像几何描述，不持有 native 对象；颜色／覆盖编码由图端口类型约定。</summary>
/// <param name="Bounds">图像在所属空间的完整逻辑框。</param><param name="Space">所属空间。</param>
/// <param name="LocalToCanvas">真实世界变换。</param><param name="DensityX">每逻辑单位的横像素。</param><param name="DensityY">每逻辑单位的纵像素。</param>
public sealed record GraphSurfaceDescriptor(GraphRect Bounds, GraphCoordinateSpace Space, SceneMatrix LocalToCanvas, double DensityX, double DensityY);
/// <summary>固定图帧输入；整数时间和逻辑画布不读取播放器或 UI。</summary>
/// <param name="TimeUs">整数微秒。</param><param name="CanvasWidth">逻辑宽。</param><param name="CanvasHeight">逻辑高。</param>
/// <param name="TargetBounds">处理前的目标逻辑框。</param><param name="DensityX">输出横密度。</param><param name="DensityY">输出纵密度。</param>
/// <param name="Parameters">固定类型参数。</param><param name="Transparent">是否透明输出。</param>
public sealed record ImageGraphFrameContext(long TimeUs, double CanvasWidth, double CanvasHeight, GraphRect TargetBounds,
    double DensityX, double DensityY, ParameterValues Parameters, bool Transparent)
{
    /// <summary>明确节点／uniform 的本帧覆盖；默认保持图中原始绑定，不改参数真值。</summary>
    public GraphUniformValues Uniforms { get; init; } = GraphUniformValues.Empty;
}
/// <summary>输出模式造成的明确图旁路，不表示编译或资源错误。</summary>
/// <param name="GraphId">图身份。</param><param name="InputId">造成背景依赖的输入。</param><param name="Reason">结构化原因。</param>
public sealed record ImageGraphBypassDiagnostic(string GraphId, string InputId, string Reason);
