using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>作者 C# 编译前的 SVG 静态门禁；不建立 Picture、GPU 资源或运行会话。</summary>
public static class PackageAuthorResourceValidator
{
    /// <summary>验证当前固定包的全部 SVG；其他资源仍由各自正式执行边界验证。</summary>
    public static Task ValidateSvgAsync(EffectPackageContents contents, CancellationToken cancellationToken = default)
        => ValidateSvgAsync(contents, null, cancellationToken);

    internal static Task ValidateSvgAsync(EffectPackageContents contents, IPackageSvgValidator? validator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents); cancellationToken.ThrowIfCancellationRequested();
        if (!contents.Files.Any(file => file.Role is "svg" or "svgResource")) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var resources = EffectPackageSvgResources.Read(contents, cancellationToken);
            PackageSvgValidation.Validate(contents, resources, validator ?? new PackageSvgValidator(), cancellationToken);
        }, cancellationToken);
    }
}
