using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定包 SVG 的结构、静态语义与完整图片解码门禁；不授予 native 或 GPU 执行能力。</summary>
public interface IPackageSvgValidator
{
    /// <summary>检查全部描述，包括未被控制器引用的资源；失败不得发布部分资源。</summary>
    PackageSvgInspection Validate(EffectPackageContents contents, EffectPackageSvgResources resources,
        CancellationToken cancellationToken = default);
}

/// <summary>不可变的全包 SVG 预验证事实；不含 DOM、可变字节或 native 对象。</summary>
/// <param name="PackageId">包身份。</param><param name="Revision">完整内容修订。</param>
/// <param name="Profile">精确静态 profile。</param><param name="RulesVersion">应用解析规则版本。</param>
/// <param name="Resources">按 ordinal ID 排序的全部资源。</param>
/// <param name="EstimatedPrepareBytes">验证、后续 native 解码及固定 atlas 的保守准备预留。</param>
public sealed record PackageSvgInspection(string PackageId, string Revision, string Profile, string RulesVersion,
    ImmutableArray<SvgResourceInspection> Resources, long EstimatedPrepareBytes);

/// <summary>内容地址的精确依赖，不从文件名、目录或机器状态补资源。</summary>
/// <param name="Path">同包路径。</param><param name="Role">精确用途。</param><param name="Sha256">原字节摘要。</param>
public sealed record SvgDependencyInspection(string Path, string Role, string Sha256);

/// <summary>计划创建的单个 mip 层；不是已经观察到的 GPU 层。</summary>
/// <param name="Width">包含 padding 后的像素宽度。</param><param name="Height">像素高度。</param>
/// <param name="Bytes">RGBA 字节预留。</param>
public sealed record SvgMipLevelPlan(int Width, int Height, long Bytes);

/// <summary>固定 atlas 页的完整 NPOT mip 计划。</summary>
/// <param name="VariantId">资源内档位。</param><param name="Levels">基础层至 1×1 的全部层。</param>
/// <param name="MipBytes">全部层累计字节。</param>
public sealed record SvgAtlasPlan(string VariantId, ImmutableArray<SvgMipLevelPlan> Levels, long MipBytes);

/// <summary>单资源的静态预验证与完整图片解码结果；操作数为应用保守估计。</summary>
/// <param name="Id">资源 ID。</param><param name="Mode">固定路径。</param><param name="Viewport">固定 viewport。</param>
/// <param name="SourcePath">源路径。</param><param name="SourceSha256">源摘要。</param>
/// <param name="Dependencies">描述、源和声明图片的完整闭包。</param>
/// <param name="RawElements">原始元素数量。</param><param name="ExpandedElements">包含本地引用展开的元素数量。</param>
/// <param name="RawPathCommands">完整 grammar 得到的原始 path command 数量。</param>
/// <param name="ExpandedPathCommands">本地引用展开后的 command 数量。</param>
/// <param name="EstimatedPictureOperations">应用保守绘制操作预留，不是 native Picture 观察值。</param>
/// <param name="CssRules">CSS rule 数量。</param><param name="CssDeclarations">CSS declaration 数量。</param>
/// <param name="SelectorSteps">实际执行的有限 selector 匹配次数。</param>
/// <param name="ValidationDecodedImageBytes">验证期完整解码的累计字节，重复引用继续计费。</param>
/// <param name="EstimatedNativeDecodedImageBytes">后续独立 native 解码必须另行预留的累计字节。</param>
/// <param name="FilterPasses">单 SVG 静态 filter pass 数量。</param>
/// <param name="FilterSurfaceBytes">逻辑 viewport 密度下的保守 filter 工作面预留，绘制期仍须检查物理区域。</param>
/// <param name="SafeXmlBytes">重新生成的受控 UTF-8 文档字节。</param>
/// <param name="AtlasPlans">计划档位；不是 GPU mip 证据。</param>
public sealed record SvgResourceInspection(string Id, SvgResourceMode Mode, SvgViewport Viewport,
    string SourcePath, string SourceSha256, ImmutableArray<SvgDependencyInspection> Dependencies,
    int RawElements, int ExpandedElements, int RawPathCommands, int ExpandedPathCommands,
    int EstimatedPictureOperations, int CssRules, int CssDeclarations, int SelectorSteps,
    long ValidationDecodedImageBytes, long EstimatedNativeDecodedImageBytes, int FilterPasses,
    long FilterSurfaceBytes, long SafeXmlBytes, ImmutableArray<SvgAtlasPlan> AtlasPlans);
