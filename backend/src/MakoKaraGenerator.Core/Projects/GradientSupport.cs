namespace MakoKaraGenerator.Core.Projects;

/// <summary>当前渲染后端已经兑现的渐变能力矩阵。</summary>
/// <remarks>
/// <para>
/// <see cref="GradientSpec"/> 是数据合同，能够描述比当前后端更多的未来能力。
/// 这里集中声明 0.5.0 可以真正采样和绘制的组合，避免编辑器、校验器和渲染器各自猜测。
/// </para>
/// <para>不支持的描述会进入诊断并按纯色基色绘制；不会悄悄退回旧的端点渐变。</para>
/// </remarks>
public static class GradientSupport
{
    /// <summary>判断一份渐变是否属于当前首批可渲染矩阵。</summary>
    /// <param name="gradient">渐变描述。</param>
    /// <returns>当前后端能够按描述绘制时为 <see langword="true"/>。</returns>
    public static bool IsSupported(GradientSpec? gradient) =>
        gradient is { IsValid: true }
        && gradient.Kind is GradientKind.Linear or GradientKind.Radial
        && gradient.CoordinateSpace is GradientCoordinateSpace.Canvas or GradientCoordinateSpace.Line
        && gradient.ColorSpace is GradientColorSpace.LinearRgb or GradientColorSpace.Hsv;

    /// <summary>返回不可用组合的用户可见原因；可用时返回空值。</summary>
    /// <param name="gradient">渐变描述。</param>
    /// <returns>诊断原因。</returns>
    public static string? UnsupportedReason(GradientSpec? gradient)
    {
        if (gradient is null || IsSupported(gradient))
        {
            return null;
        }

        if (!gradient.IsValid)
        {
            return "渐变描述本身无效。";
        }

        if (gradient.CoordinateSpace is GradientCoordinateSpace.Glyph)
        {
            return "按单字坐标的渲染在 P3 第 4 步接入。";
        }

        if (gradient.ColorSpace == GradientColorSpace.Oklch)
        {
            return "Oklch 插值将在后续版本接入。";
        }

        return "当前渲染后端不支持这组渐变参数。";
    }
}
