using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>一个静态 SVG 资源的唯一执行方式；不会按当前缩放自动切换。</summary>
public enum SvgResourceMode
{
    /// <summary>保留矢量 Picture 的绘制路径。</summary>
    Vector,
    /// <summary>声明固定分辨率、独立页及完整 mip 的纹理路径。</summary>
    Atlas,
}

/// <summary>已验证的逻辑 viewport；不依赖设备 DPI 或当前工程画布。</summary>
public sealed record SvgViewport
{
    internal SvgViewport(double width, double height) { Width = width; Height = height; }
    /// <summary>正有限逻辑宽度。</summary>
    public double Width { get; }
    /// <summary>正有限逻辑高度。</summary>
    public double Height { get; }
}

/// <summary>已验证的固定 atlas 档位；宽高指内容区，不包括资源 padding。</summary>
public sealed record SvgAtlasVariant
{
    internal SvgAtlasVariant(string id, int width, int height) { Id = id; Width = width; Height = height; }
    /// <summary>资源内唯一且大小写敏感的档位身份。</summary>
    public string Id { get; }
    /// <summary>内容区像素宽度。</summary>
    public int Width { get; }
    /// <summary>内容区像素高度。</summary>
    public int Height { get; }
}

/// <summary>严格包描述的只读模型；不含 XML DOM、native 资源或机器路径。</summary>
public sealed class EffectSvgResourceDefinition
{
    internal EffectSvgResourceDefinition(string id, string descriptorPath, string sourcePath, string profile,
        SvgResourceMode mode, SvgViewport viewport, ImmutableArray<string> images, ImmutableArray<SvgAtlasVariant> variants,
        int paddingPx, bool mipmaps)
    { Id = id; DescriptorPath = descriptorPath; SourcePath = sourcePath; Profile = profile; Mode = mode;
        Viewport = viewport; Images = images; Variants = variants; PaddingPx = paddingPx; Mipmaps = mipmaps; }
    /// <summary>全包唯一资源身份，不等同于描述文件路径。</summary>
    public string Id { get; }
    /// <summary>同包 role:svgResource 描述路径。</summary>
    public string DescriptorPath { get; }
    /// <summary>同包 role:svg 源路径。</summary>
    public string SourcePath { get; }
    /// <summary>精确静态 SVG profile。</summary>
    public string Profile { get; }
    /// <summary>明确且固定的执行方式。</summary>
    public SvgResourceMode Mode { get; }
    /// <summary>声明的逻辑尺寸；与根 SVG 的一致性仍须由语义验证器检查。</summary>
    public SvgViewport Viewport { get; }
    /// <summary>唯一且精确指向 role:image 的包路径白名单；不按扩展名猜测。</summary>
    public ImmutableArray<string> Images { get; }
    /// <summary>atlas 的固定档位；vector 为空。</summary>
    public ImmutableArray<SvgAtlasVariant> Variants { get; }
    /// <summary>atlas 单侧 padding 像素；vector 为零。</summary>
    public int PaddingPx { get; }
    /// <summary>atlas 必须请求完整 mip；vector 为 false，不代表已观察到 GPU mip。</summary>
    public bool Mipmaps { get; }
}

/// <summary>全包 SVG 描述与文件关系的唯一解析器；结果不是 XML 语义验证或运行能力准入。</summary>
public sealed partial class EffectPackageSvgResources
{
    /// <summary>独立描述文件版本。</summary>
    public const int CurrentVersion = 1;
    /// <summary>首期静态 profile；明确不含 SVG 文字、动画及外部 IO。</summary>
    public const string StaticProfile = "mako-svg-static-1";
    /// <summary>固定矢量资源能力；只有执行器接通后才可加入 supported 集合。</summary>
    public const string VectorCapability = "resources.svg.vector";
    /// <summary>固定 atlas 资源能力；只有执行器接通后才可加入 supported 集合。</summary>
    public const string AtlasCapability = "resources.svg.atlas";
    private EffectPackageSvgResources(ImmutableDictionary<string, EffectSvgResourceDefinition> byId) { ById = byId; }
    /// <summary>按 ordinal 资源 ID 索引的全包描述；包括未被任何导出引用的项。</summary>
    public ImmutableDictionary<string, EffectSvgResourceDefinition> ById { get; }

    /// <summary>验证全包严格描述与完整文件关系；XML/CSS/path、图片解码及运行能力仍由后续门禁检查。</summary>
    public static EffectPackageSvgResources Read(EffectPackageContents contents, CancellationToken cancellationToken = default)
        => Read(contents, SvgResourceBudget.Default, cancellationToken);
}
