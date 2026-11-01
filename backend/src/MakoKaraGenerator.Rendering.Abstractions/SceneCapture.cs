using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定的真实捕获目标；不是任选字符串路径或可写源节点。</summary>
public abstract record SceneCaptureTarget
{
    private SceneCaptureTarget() { }
    /// <summary>整行及按配方纳入的真实运行后代。</summary><param name="Target">当前行装载。</param>
    public sealed record Line(LineTarget Target) : SceneCaptureTarget;
    /// <summary>基础字及按配方纳入的注音。</summary><param name="Target">当前元素族装载。</param>
    public sealed record Token(TokenFamilyTarget Target) : SceneCaptureTarget;
    /// <summary>当前前奏文字标记。</summary><param name="Target">节点、提示和目标装载。</param>
    public sealed record Mark(TextMarkTarget Target) : SceneCaptureTarget;
    /// <summary>真实容器组，不伪造文字材质。</summary><param name="Target">当前组节点。</param>
    public sealed record Group(GroupTarget Target) : SceneCaptureTarget;
}

/// <summary>TargetRegionCoverage 的显式区域；不含阴影。</summary>
public enum SceneCaptureRegion
{
    /// <summary>填充字形。</summary>
    Fill,
    /// <summary>外部描边。</summary>
    Stroke,
    /// <summary>填充和外部描边。</summary>
    FillAndStroke,
}

/// <summary>宿主明确的捕获配方，没有隐藏完整捕获默认值。</summary>
/// <param name="IncludeRuby">纳入基础字关联注音。</param><param name="IncludeStroke">TargetColor 纳入外部描边。</param>
/// <param name="IncludeShadow">TargetColor 纳入阴影。</param><param name="IncludeDescendantGroups">纳入运行父树中的子组后代。</param>
/// <param name="Region">目标覆盖区域；其他来源不读取此项。</param>
/// <param name="IncludeGlow">TargetColor 独立纳入外发光；覆盖通道不读取此项。</param>
public sealed record SceneCaptureRecipe(bool IncludeRuby, bool IncludeStroke, bool IncludeShadow,
    bool IncludeDescendantGroups, SceneCaptureRegion Region, bool IncludeGlow = true);

/// <summary>调用前可验证的捕获通道；背景和包资源由其他生产器提供。</summary>
public static class SceneCaptureChannels
{
    /// <summary>是否为本生产器可提供的真实颜色／覆盖通道。</summary>
    public static bool IsSupported(GraphInputSource source) => source is GraphInputSource.TargetColor or GraphInputSource.GlyphCoverage
        or GraphInputSource.StrokeCoverage or GraphInputSource.SungCoverage or GraphInputSource.TargetRegionCoverage;
}
