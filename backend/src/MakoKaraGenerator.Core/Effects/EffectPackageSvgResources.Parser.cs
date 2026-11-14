using System.Collections.Immutable;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

public sealed partial class EffectPackageSvgResources
{
    /// <summary>按显式预算验证全部 SVG 描述和闭包；不调用包构造器，不建立运行资源。</summary>
    public static EffectPackageSvgResources Read(EffectPackageContents contents, SvgResourceBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents); ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested(); budget.Validate();
        var result = ImmutableDictionary.CreateBuilder<string, EffectSvgResourceDefinition>(StringComparer.Ordinal);
        var files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
        var sources = contents.Files.Where(file => file.Role == "svg").ToArray();
        var descriptors = contents.Files.Where(file => file.Role == "svgResource").ToArray();
        if (sources.Length == 0 && descriptors.Length == 0) return new(contents.PackageId, contents.Revision, result.ToImmutable());
        if (!EffectPackageProfiles.IsSupported(contents.Profile))
            throw Error("PackageSvgProfileMismatch", "manifest.json.files", "SVG 资源只属于明确的 SDK v2 包合同。");
        if (descriptors.Length > budget.MaximumResources)
            throw Budget("manifest.json.files", "SVG 描述数量超过包预算。");
        long sourceBytes = 0, descriptorBytes = 0, imageBytes = 0, mipBytes = 0; int imageReferences = 0;
        HashSet<string> covered = new(StringComparer.Ordinal), images = new(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Claim(ref sourceBytes, source.Content.Length, budget.MaximumSourceBytes, budget.MaximumTotalSourceBytes, source.Path);
            _ = source.ReadUtf8();
        }
        foreach (var file in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Claim(ref descriptorBytes, file.Content.Length, budget.MaximumDescriptorBytes, budget.MaximumTotalDescriptorBytes, file.Path);
            // ReadUtf8 使用严格解码；JsonDocument 的容错不能改变原资源字节。
            _ = file.ReadUtf8();
            using var document = ReadJson(file.Content.AsMemory(), file.Path); var root = document.RootElement;
            Keys(root, file.Path, ["svgResourceVersion", "id", "source", "profile", "mode", "viewport", "fonts", "images"], ["atlas"]);
            if (Whole(root.GetProperty("svgResourceVersion"), file.Path + ".svgResourceVersion") != CurrentVersion)
                throw Error("PackageSvgVersionMismatch", file.Path + ".svgResourceVersion", "SVG 描述版本不受支持。");
            string id = Id(root, "id", file.Path), sourcePath = RequiredText(root, "source", file.Path);
            string profile = RequiredText(root, "profile", file.Path);
            if (profile != StaticProfile) throw Error("PackageSvgProfileMismatch", file.Path + ".profile", "SVG profile 不受支持。");
            SvgResourceMode mode = RequiredText(root, "mode", file.Path) switch
            {
                "vector" => SvgResourceMode.Vector, "atlas" => SvgResourceMode.Atlas,
                _ => throw Error("PackageSvgModeUnsupported", file.Path + ".mode", "SVG mode 必须精确为 vector 或 atlas。"),
            };
            RequireFile(files, sourcePath, "svg", file.Path + ".source"); covered.Add(sourcePath);
            var viewportJson = root.GetProperty("viewport"); string viewportPath = file.Path + ".viewport";
            Keys(viewportJson, viewportPath, ["width", "height"], []);
            double width = Number(viewportJson.GetProperty("width"), viewportPath + ".width");
            double height = Number(viewportJson.GetProperty("height"), viewportPath + ".height");
            if (width <= 0 || height <= 0 || width > budget.MaximumViewportDimension || height > budget.MaximumViewportDimension)
                throw Error("PackageSvgViewportInvalid", viewportPath, "SVG viewport 必须为预算内正有限尺寸。");
            var fonts = root.GetProperty("fonts"); _ = Array(fonts, file.Path + ".fonts");
            if (fonts.GetArrayLength() != 0)
                throw Error("PackageSvgFontsUnsupported", file.Path + ".fonts", "static-1 明确不支持 SVG 文字与字体绑定，fonts 必须为空。");
            var imageJson = root.GetProperty("images"); _ = Array(imageJson, file.Path + ".images");
            if (imageJson.GetArrayLength() > budget.MaximumImageReferences - imageReferences)
                throw Budget(file.Path + ".images", "SVG 图片白名单引用数量超过包预算。");
            var imagePaths = Strings(imageJson, file.Path + ".images");
            imageReferences += imagePaths.Length;
            foreach (string imagePath in imagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested(); var image = RequireFile(files, imagePath, "image", file.Path + ".images");
                if (images.Add(imagePath)) Claim(ref imageBytes, image.Content.Length, budget.MaximumImageBytes, budget.MaximumTotalImageBytes, imagePath);
            }
            ImmutableArray<SvgAtlasVariant> variants = []; int padding = 0; bool mipmaps = false;
            if (mode == SvgResourceMode.Vector)
            {
                if (root.TryGetProperty("atlas", out _))
                    throw Error("PackageSvgAtlasUnexpected", file.Path + ".atlas", "vector 描述不得含 atlas 字段。");
            }
            else
            {
                if (!root.TryGetProperty("atlas", out var atlas))
                    throw Error("PackageSemanticMissingField", file.Path + ".atlas", "atlas 描述必须声明全部固定档位。");
                variants = ReadAtlas(atlas, file.Path + ".atlas", budget, ref mipBytes, cancellationToken, out padding);
                mipmaps = true;
            }
            string capability = mode == SvgResourceMode.Vector ? VectorCapability : AtlasCapability;
            if (!contents.Requires.Contains(capability, StringComparer.Ordinal))
                throw Error("PackageCapabilityMissing", file.Path, "SVG 资源必须在包 requires 中声明能力：" + capability);
            if (!result.TryAdd(id, new(id, file.Path, sourcePath, profile, mode, new(width, height), imagePaths, variants, padding, mipmaps)))
                throw Error("PackageSvgResourceDuplicate", file.Path + ".id", "SVG 逻辑身份在全包重复。");
        }
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!covered.Contains(source.Path))
                throw Error("PackageSvgSourceUnreferenced", source.Path, "每份 role:svg 都必须由描述覆盖并在启用前完成语义验证。");
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(contents.PackageId, contents.Revision, result.ToImmutable());
    }

    private static ImmutableArray<SvgAtlasVariant> ReadAtlas(JsonElement atlas, string path, SvgResourceBudget budget,
        ref long mipBytes, CancellationToken token, out int padding)
    {
        Keys(atlas, path, ["variants", "paddingPx", "mipmaps"], []);
        long paddingValue = Whole(atlas.GetProperty("paddingPx"), path + ".paddingPx");
        if (paddingValue <= 0 || paddingValue > budget.MaximumAtlasPaddingPx)
            throw Error("PackageSvgAtlasPaddingInvalid", path + ".paddingPx", "atlas padding 必须为预算内正整数。");
        padding = (int)paddingValue;
        if (atlas.GetProperty("mipmaps").ValueKind != JsonValueKind.True)
            throw Error("PackageSvgMipmapsRequired", path + ".mipmaps", "atlas 必须明确声明 mipmaps=true。");
        var items = atlas.GetProperty("variants"); _ = Array(items, path + ".variants");
        if (items.GetArrayLength() is 0 || items.GetArrayLength() > budget.MaximumAtlasVariants)
            throw Budget(path + ".variants", "atlas 必须具有预算内非空档位集合。");
        var result = ImmutableArray.CreateBuilder<SvgAtlasVariant>(); HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); string location = path + ".variants[" + result.Count + "]";
            Keys(item, location, ["id", "width", "height"], []); string id = Id(item, "id", location);
            if (!ids.Add(id)) throw Error("PackageSvgVariantDuplicate", location + ".id", "atlas 档位身份重复。");
            long width = Whole(item.GetProperty("width"), location + ".width"), height = Whole(item.GetProperty("height"), location + ".height");
            long pageWidth = width > 0 && width <= int.MaxValue ? width + 2L * padding : 0;
            long pageHeight = height > 0 && height <= int.MaxValue ? height + 2L * padding : 0;
            if (pageWidth <= 0 || pageHeight <= 0 || pageWidth > budget.MaximumAtlasDimension || pageHeight > budget.MaximumAtlasDimension)
                throw Error("PackageSvgAtlasSizeInvalid", location, "atlas 内容加两侧 padding 后须为预算内可寻址正整数尺寸。");
            long bytes = 0;
            while (true)
            {
                long pixels = pageWidth * pageHeight;
                if (pixels > (budget.MaximumAtlasMipBytes - bytes) / 4)
                    throw Budget(location, "atlas 完整 mip 链超出字节预算。");
                bytes += pixels * 4;
                if (pageWidth == 1 && pageHeight == 1) break;
                pageWidth = Math.Max(1, pageWidth / 2); pageHeight = Math.Max(1, pageHeight / 2);
            }
            Claim(ref mipBytes, bytes, budget.MaximumAtlasMipBytes, budget.MaximumAtlasMipBytes, location);
            result.Add(new(id, (int)width, (int)height));
        }
        return result.ToImmutable();
    }

    private static string Id(JsonElement parent, string key, string path)
    {
        string id = RequiredText(parent, key, path);
        if (id.Length > 128 || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw Error("PackageSvgIdInvalid", path + "." + key, "SVG 身份必须以 ASCII 字母或数字开头，仅含字母数字及 ._-，最多 128 字符。");
        return id;
    }

    private static EffectPackageFile RequireFile(ImmutableDictionary<string, EffectPackageFile> files, string reference, string role, string path)
    {
        EffectPackageContents.ValidatePath(reference);
        if (!files.TryGetValue(reference, out var file) || file.Role != role)
            throw Error(role == "svg" ? "PackageSvgSourceMissing" : "PackageSvgImageMissing", path, "资源必须精确引用同包 role:" + role + " 文件。");
        return file;
    }

    private static void Claim(ref long used, long amount, long maximumSingle, long maximumTotal, string path)
    {
        if (amount > maximumSingle || amount > maximumTotal - used)
            throw Budget(path, "SVG 资源字节超过明确的单项或累计预算。");
        used += amount;
    }

    private static EffectPackageException Budget(string path, string message) => Error("PackageSvgBudgetExceeded", path, message);
}
