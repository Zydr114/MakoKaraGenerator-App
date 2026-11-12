namespace MakoKaraGenerator.Core.Projects;

/// <summary>元素级样式覆盖能用哪些属性（roadmap P1「元素级覆盖的界面作用范围」）。</summary>
/// <remarks>
/// <para>
/// 元素级覆盖<b>不是</b>行级覆盖的缩小版：字号、字体、纵向位置、水平对齐属于"整行排版"
/// （整行只整形一次、整行一起移动），缩放属于"整体变换"（靠画布变换实现），
/// 这几项套到单个元素上没有意义，甚至会把整行一起改掉。校验器会拒绝它们
/// （<see cref="Diagnostics.DiagnosticCode.UnsupportedTokenStyleOverride" />）。
/// </para>
/// <para>
/// 这份清单是<b>界面与校验器共用的同一份事实</b>：界面据此把不能用的控件说明白，
/// 校验器据此拒绝文件里的非法覆盖。两边若各写一份，迟早会出现"界面允许、保存即报错"的组合，
/// 因此 <c>TokenStyleSupportTests</c> 会钉住两者一致。
/// </para>
/// </remarks>
public static class TokenStyleSupport
{
    /// <summary>元素级覆盖<b>不支持</b>的属性。</summary>
    public static IReadOnlyList<LyricStyleProperty> UnsupportedAtTokenLevel { get; } =
    [
        LyricStyleProperty.FontFamilyName,
        LyricStyleProperty.FallbackFamilyNames,
        LyricStyleProperty.FontSizePx,
        LyricStyleProperty.FontWeight,
        LyricStyleProperty.IsBold,
        LyricStyleProperty.IsItalic,
        LyricStyleProperty.LetterSpacingPx,
        LyricStyleProperty.VerticalCenterRatio,
        LyricStyleProperty.HorizontalAlignment,
        LyricStyleProperty.Scale,
    ];

    /// <summary>元素级覆盖<b>支持</b>的属性（不受支持那几项的补集）。</summary>
    public static IReadOnlyList<LyricStyleProperty> SupportedAtTokenLevel { get; } =
        [.. LyricStylePropertyExtensions.All.Where(IsSupported)];

    /// <summary>列出这份覆盖里<b>被设置过</b>的属性。</summary>
    /// <param name="styleOverride">样式覆盖；为 <see langword="null"/> 时返回空表。</param>
    /// <returns>被设置的属性（<see cref="LyricStylePropertyExtensions.All"/> 的顺序）。</returns>
    /// <remarks>
    /// <para>
    /// 这是"覆盖里有什么"的<b>唯一</b>入口：支持范围判断、元素级拦截、渲染是否需要重画
    /// 全部由它派生。判定与样式解析的来源追踪是同一份代码（<see cref="LyricStyleResolver"/>），
    /// 因此"新加一个样式属性"只需要在解析器的来源标记里补一处。
    /// </para>
    /// <para>
    /// 忘了补会被 <c>TokenStyleSupportTests</c> 的覆盖度检查当场抓住——它遍历
    /// <see cref="LyricStylePropertyExtensions.All"/> 里的每一项，要求这一层都能看见。
    /// （同类错误在本仓库真实发生过：工厂收了参数没写进对象、映射器漏写字段，两次都只有测试能发现。）
    /// </para>
    /// </remarks>
    public static IReadOnlyList<LyricStyleProperty> SetProperties(LyricStyleOverride? styleOverride)
        => LyricStyleResolver.SetProperties(styleOverride);

    /// <summary>列出这份覆盖里"元素级不支持"的那些属性。</summary>
    /// <param name="styleOverride">样式覆盖；为 <see langword="null"/> 时返回空表。</param>
    /// <returns>被设置且不受支持的属性（按固定顺序）。</returns>
    /// <remarks>
    /// 界面用它挡住"存盘即报错"的组合：与其让用户改完再被校验器拒绝，
    /// 不如在写入前就说清楚是哪一项、为什么不行。判定与 <c>ProjectValidator</c> 同源。
    /// </remarks>
    public static IReadOnlyList<LyricStyleProperty> UnsupportedSet(LyricStyleOverride? styleOverride) =>
        [.. SetProperties(styleOverride).Where(static property => !IsSupported(property))];

    /// <summary>元素级覆盖里有没有<b>会改变画面</b>的属性。</summary>
    /// <param name="styleOverride">样式覆盖。</param>
    /// <returns>需要为这个元素重画一遍时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 渲染器用它决定"这个元素要不要单独重画"：颜色族、描边、阴影、不透明度都算，
    /// 整行排版与整体变换（字号/字体/纵向位置/对齐/缩放）不算——那几项由
    /// <see cref="IsSupported"/> 判定为不支持，这里自动跟着排除，
    /// 因此渲染侧不需要再维护第三份清单。
    /// </remarks>
    public static bool HasDrawableOverride(LyricStyleOverride? styleOverride) =>
        SetProperties(styleOverride).Any(IsSupported);

    /// <summary>某个属性能否用在元素级覆盖上。</summary>
    /// <param name="property">样式属性。</param>
    /// <returns>能用时为 <see langword="true"/>。</returns>
    public static bool IsSupported(LyricStyleProperty property) =>
        !UnsupportedAtTokenLevel.Contains(property);
}
