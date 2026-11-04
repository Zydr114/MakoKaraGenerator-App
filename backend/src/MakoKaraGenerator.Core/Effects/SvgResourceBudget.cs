namespace MakoKaraGenerator.Core.Effects;

/// <summary>静态 SVG 的固定准入上限；描述预算不代表 XML、图片或 native 执行已经通过验证。</summary>
public sealed record SvgResourceBudget
{
    /// <summary>mako-svg-static-1 的初始有限预算。</summary>
    public static SvgResourceBudget Default { get; } = new();
    /// <summary>全包 SVG 描述数量。</summary>
    public int MaximumResources { get; init; } = 128;
    /// <summary>单份描述的 UTF-8 字节。</summary>
    public long MaximumDescriptorBytes { get; init; } = 256 * 1024;
    /// <summary>全包描述的 UTF-8 字节。</summary>
    public long MaximumTotalDescriptorBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>单份 SVG 源字节。</summary>
    public long MaximumSourceBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>全包不同 SVG 源的累计字节。</summary>
    public long MaximumTotalSourceBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>单份受控 XML 的 UTF-8 输出字节；生成期间逐次检查，仍受准备活动字节预算约束。</summary>
    public long MaximumSafeXmlBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>全包受控 XML 的累计 UTF-8 输出字节；不放宽原始源、图片或准备活动字节预算。</summary>
    public long MaximumTotalSafeXmlBytes { get; init; } = 128L * 1024 * 1024;
    /// <summary>声明 viewport 的最大逻辑边长。</summary>
    public double MaximumViewportDimension { get; init; } = 1_000_000;
    /// <summary>全包描述中的图片白名单引用数量；重复使用也计引用。</summary>
    public int MaximumImageReferences { get; init; } = 1024;
    /// <summary>单份 CSS 的 UTF-8 字节。</summary>
    public long MaximumCssBytes { get; init; } = 256 * 1024;
    /// <summary>XML 最大嵌套深度。</summary>
    public int MaximumXmlDepth { get; init; } = 64;
    /// <summary>全包原始 SVG 元素数量。</summary>
    public int MaximumElements { get; init; } = 20_000;
    /// <summary>单元素属性数量。</summary>
    public int MaximumAttributesPerElement { get; init; } = 64;
    /// <summary>全包原始 path command 数量。</summary>
    public int MaximumPathCommands { get; init; } = 100_000;
    /// <summary>全包展开后的 path command 数量。</summary>
    public int MaximumExpandedPathCommands { get; init; } = 200_000;
    /// <summary>本地引用依赖最大深度。</summary>
    public int MaximumReferenceDepth { get; init; } = 32;
    /// <summary>全包 use 展开后的元素数量。</summary>
    public int MaximumExpandedElements { get; init; } = 40_000;
    /// <summary>全包 CSS rule 数量。</summary>
    public int MaximumCssRules { get; init; } = 2048;
    /// <summary>全包 CSS declaration 数量。</summary>
    public int MaximumCssDeclarations { get; init; } = 8192;
    /// <summary>全包 CSS selector 求值步数。</summary>
    public int MaximumSelectorSteps { get; init; } = 16_384;
    /// <summary>单图片编码字节；包图片和 data image 都须检查。</summary>
    public long MaximumImageBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>全包 SVG 图片编码字节。</summary>
    public long MaximumTotalImageBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>单图片最大像素边长。</summary>
    public int MaximumImageDimension { get; init; } = 4096;
    /// <summary>单图片最大像素数。</summary>
    public long MaximumImagePixels { get; init; } = 16L * 1024 * 1024;
    /// <summary>累计完整图片解码字节。</summary>
    public long MaximumDecodedImageBytes { get; init; } = 128L * 1024 * 1024;
    /// <summary>单 SVG 的 filter pass 数量。</summary>
    public int MaximumFilterPasses { get; init; } = 16;
    /// <summary>SVG filter 工作表面的累计字节，另受工程图预算约束。</summary>
    public long MaximumFilterSurfaceBytes { get; init; } = 128L * 1024 * 1024;
    /// <summary>单 atlas 资源的固定档位数量。</summary>
    public int MaximumAtlasVariants { get; init; } = 8;
    /// <summary>包含两侧 padding 的单 atlas 页最大边长。</summary>
    public int MaximumAtlasDimension { get; init; } = 4096;
    /// <summary>单侧 padding 上限；static-1 同时要求至少一个像素。</summary>
    public int MaximumAtlasPaddingPx { get; init; } = 64;
    /// <summary>全包 atlas 完整 RGBA mip 链字节；包含基础层与 padding。</summary>
    public long MaximumAtlasMipBytes { get; init; } = 128L * 1024 * 1024;
    /// <summary>包准备活动字节；CPU 构建副本与固定 native 资源分别计账。</summary>
    public long MaximumPrepareBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>全包 Picture 记录操作数量。</summary>
    public int MaximumPictureOperations { get; init; } = 200_000;

    /// <summary>拒绝无效宿主预算；本检查不会验证 SVG 或分配任何 native 对象。</summary>
    public void Validate()
    {
        if (MaximumResources <= 0 || MaximumDescriptorBytes <= 0 || MaximumTotalDescriptorBytes <= 0
            || MaximumSourceBytes <= 0 || MaximumTotalSourceBytes <= 0
            || MaximumSafeXmlBytes <= 0 || MaximumTotalSafeXmlBytes <= 0
            || !double.IsFinite(MaximumViewportDimension) || MaximumViewportDimension <= 0
            || MaximumImageReferences <= 0 || MaximumCssBytes <= 0 || MaximumXmlDepth <= 0 || MaximumElements <= 0
            || MaximumAttributesPerElement <= 0 || MaximumPathCommands <= 0 || MaximumExpandedPathCommands <= 0
            || MaximumReferenceDepth <= 0 || MaximumExpandedElements <= 0 || MaximumCssRules <= 0
            || MaximumCssDeclarations <= 0 || MaximumSelectorSteps <= 0 || MaximumImageBytes <= 0
            || MaximumTotalImageBytes <= 0 || MaximumImageDimension <= 0 || MaximumImagePixels <= 0
            || MaximumDecodedImageBytes <= 0 || MaximumFilterPasses <= 0 || MaximumFilterSurfaceBytes <= 0
            || MaximumAtlasVariants <= 0 || MaximumAtlasDimension <= 0 || MaximumAtlasPaddingPx <= 0
            || MaximumAtlasMipBytes <= 0 || MaximumPrepareBytes <= 0 || MaximumPictureOperations <= 0)
            throw new ArgumentException("SVG 预算必须具有正有限上限。");
    }
}
