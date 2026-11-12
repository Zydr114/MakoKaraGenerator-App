using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>
/// SDK v2 能力名的唯一定义：清单顶层 <c>requires</c> 与导出 <c>outputs</c> 写这些名字，SDK 写入检查与宿主准入按它们比较（区分大小写）。
/// </summary>
/// <remarks>
/// Core 的清单解析不引用 SDK 程序集，另持一份同值集合（<c>EffectPackageProfiles.SupportedV2Capabilities</c>），由测试保证两者相同。
/// </remarks>
public static class CapabilityNames
{
    /// <summary>元素族与底字的局部仿射。</summary>
    public const string TokenAffine = "token.affine";
    /// <summary>整行的局部仿射。</summary>
    public const string LineAffine = "line.affine";
    /// <summary>组的局部仿射。</summary>
    public const string GroupAffine = "group.affine";
    /// <summary>文字标记的局部仿射。</summary>
    public const string MarkAffine = "mark.affine";
    /// <summary>独立字簇的局部仿射。</summary>
    public const string ClusterAffine = "cluster.affine";
    /// <summary>元素族与底字的透明度系数。</summary>
    public const string TokenOpacity = "token.opacity";
    /// <summary>组的隔离透明度系数。</summary>
    public const string GroupOpacity = "group.opacity";
    /// <summary>按态写填充。</summary>
    public const string StyleFill = "style.fill";
    /// <summary>按态写描边各层。</summary>
    public const string StyleStroke = "style.stroke";
    /// <summary>按态写阴影。</summary>
    public const string StyleShadow = "style.shadow";
    /// <summary>按态写外发光。</summary>
    public const string StyleGlow = "style.glow";
    /// <summary>写扫字样式（羽化与扫字线高亮）。</summary>
    public const string StyleWipe = "style.wipe";
    /// <summary>声明依赖包含未来源事件的固定事件索引；当前查询没有逐方法门禁。</summary>
    public const string TimelineIndex = "timeline.index";
    /// <summary>声明隐藏后的行退场寿命（清单顶层 <c>lineExit</c>）。</summary>
    public const string LineExit = "line.exit";
    /// <summary>元素族与底字的网格变形。</summary>
    public const string TokenWarp = "token.warp";
    /// <summary>独立字簇的网格变形。</summary>
    public const string ClusterWarp = "cluster.warp";
    /// <summary>文字标记的网格变形。</summary>
    public const string MarkWarp = "mark.warp";
    /// <summary>文字目标的图绑定。</summary>
    public const string ImageGraph = "image.graph";
    /// <summary>组目标的图绑定。</summary>
    public const string ImageGraphGroup = "image.graph.group";
    /// <summary>包材质节点的逐帧 uniform。</summary>
    public const string MaterialUniform = "material.uniform";
    /// <summary>沿演唱位置的连续粒子发射。</summary>
    public const string ParticlesContinuous = "particles.continuous";
    /// <summary>按源事件的突发粒子发射。</summary>
    public const string ParticlesBurst = "particles.burst";
    /// <summary>晚期圆点图元。</summary>
    public const string VisualsDots = "visuals.dots";
    /// <summary>晚期线段图元。</summary>
    public const string VisualsSegments = "visuals.segments";
    /// <summary>晚期拖尾图元。</summary>
    public const string VisualsTrails = "visuals.trails";
    /// <summary>晚期贴图图元（含 SVG 图集）。</summary>
    public const string VisualsSprites = "visuals.sprites";
    /// <summary>晚期矢量路径图元。</summary>
    public const string VisualsPaths = "visuals.paths";
    /// <summary>SVG 矢量资源。</summary>
    public const string SvgVector = "resources.svg.vector";
    /// <summary>SVG 图集资源。</summary>
    public const string SvgAtlas = "resources.svg.atlas";
    /// <summary>媒体特征查询。</summary>
    public const string MediaFeatures = "media.features";

    /// <summary>全部能力名，按上面的声明顺序。</summary>
    public static ImmutableArray<string> All { get; } =
    [
        TokenAffine, LineAffine, GroupAffine, MarkAffine, ClusterAffine, TokenOpacity, GroupOpacity,
        StyleFill, StyleStroke, StyleShadow, StyleGlow, StyleWipe, TimelineIndex, LineExit,
        TokenWarp, ClusterWarp, MarkWarp, ImageGraph, ImageGraphGroup, MaterialUniform,
        ParticlesContinuous, ParticlesBurst, VisualsDots, VisualsSegments, VisualsTrails, VisualsSprites, VisualsPaths,
        SvgVector, SvgAtlas, MediaFeatures,
    ];
}
