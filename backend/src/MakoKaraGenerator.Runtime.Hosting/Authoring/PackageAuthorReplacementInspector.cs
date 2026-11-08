using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>作者草稿替换图片或字体时读到的一份资源：格式，以及图片宽高或字体家族名。</summary>
/// <param name="Format">图片为 Skia 识别的编码（png、jpeg 等）；字体为 truetype、opentype 或 collection。</param>
/// <param name="Width">图片宽度；字体为 null。</param>
/// <param name="Height">图片高度；字体为 null。</param>
/// <param name="FamilyNames">字体每个字形的家族名（去重）；图片为 null。</param>
public sealed record PackageAuthorResourceInfo(string Format, int? Width, int? Height, IReadOnlyList<string>? FamilyNames);

/// <summary>替换后给作者的提醒，不阻止保存。</summary>
/// <param name="Code">imageSizeChanged 或 fontFamilyChanged。</param>
/// <param name="Message">给作者看的说明。</param>
public sealed record PackageAuthorReplacementWarning(string Code, string Message);

/// <summary>一次替换的检查结果。</summary>
/// <param name="Current">原文件。</param>
/// <param name="Replacement">新文件。</param>
/// <param name="Warnings">提醒；可以为空。</param>
public sealed record PackageAuthorReplacementInspection(PackageAuthorResourceInfo Current, PackageAuthorResourceInfo Replacement,
    IReadOnlyList<PackageAuthorReplacementWarning> Warnings);

/// <summary>
/// 作者草稿整份替换包内图片或字体前的检查：用途只能是 image 或 font，新文件须与原文件同一格式，
/// 并通过与渲染相同的解码（图片）或加载（字体）。尺寸、家族名变化只作提醒。
/// </summary>
public static class PackageAuthorReplacementInspector
{
    /// <summary>imageSizeChanged：图片尺寸变了。</summary>
    public const string ImageSizeChanged = "imageSizeChanged";
    /// <summary>fontFamilyChanged：字体家族名集合变了。</summary>
    public const string FontFamilyChanged = "fontFamilyChanged";

    /// <summary>这个用途的文件能否整份替换。</summary>
    public static bool CanReplace(string role) => role is "image" or "font";

    /// <summary>在线程池上检查；失败抛具名 <see cref="EffectPackageException" />。</summary>
    public static Task<PackageAuthorReplacementInspection> InspectAsync(EffectPackageFile current, ReadOnlyMemory<byte> replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Task.Run(() => Inspect(current, replacement, cancellationToken), cancellationToken);
    }

    /// <summary>检查新文件能否替换 <paramref name="current" />；失败抛具名 <see cref="EffectPackageException" />。</summary>
    public static PackageAuthorReplacementInspection Inspect(EffectPackageFile current, ReadOnlyMemory<byte> replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanReplace(current.Role))
            throw new EffectPackageException("AuthorReplacementTargetInvalid", current.Path, "只有图片和字体可以整份替换。");
        if (replacement.IsEmpty)
            throw new EffectPackageException("AuthorReplacementFormatMismatch", current.Path, "新文件是空的。");
        new EffectPackageContentUsage().Claim(current.Path, replacement.Length);
        PackageAuthorResourceInfo before = Describe(current.Role, current.Content.AsMemory(), current.Path, cancellationToken);
        PackageAuthorResourceInfo after = Describe(current.Role, replacement, current.Path, cancellationToken);
        if (before.Format != after.Format)
            throw new EffectPackageException("AuthorReplacementFormatMismatch", current.Path,
                $"新文件是 {FormatLabel(after.Format)}，原文件是 {FormatLabel(before.Format)}；只能换成同一格式的文件。");
        List<PackageAuthorReplacementWarning> warnings = [];
        if (current.Role == "image" && (before.Width != after.Width || before.Height != after.Height))
            warnings.Add(new(ImageSizeChanged,
                $"尺寸从 {before.Width}×{before.Height} 变为 {after.Width}×{after.Height}；按像素裁切或假定尺寸的效果可能需要调整。"));
        if (current.Role == "font" && !SameFamilies(before.FamilyNames!, after.FamilyNames!))
            warnings.Add(new(FontFamilyChanged,
                $"字体家族从「{string.Join("、", before.FamilyNames!)}」变为「{string.Join("、", after.FamilyNames!)}」；"
                + "工程和模板里按原名字引用的文字不会用到新字体，需要时请改文字样式的字体。"));
        return new(before, after, warnings);
    }

    private static PackageAuthorResourceInfo Describe(string role, ReadOnlyMemory<byte> content, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (role == "image")
        {
            PackageDecodedImageInfo image = new PackageImageValidator().Inspect(content, path, cancellationToken);
            return new(image.EncodedFormat.ToLowerInvariant(), image.Width, image.Height, null);
        }
        string format = FontFormat(content.Span)
            ?? throw new EffectPackageException("AuthorReplacementFontInvalid", path, "字体文件头无法识别；只支持 TrueType、OpenType 和字体集合。");
        try
        {
            IReadOnlyList<string> families = SkiaTextLayoutEngine.ReadFixedFontFamilies(content);
            if (families.Count == 0 || families.Any(string.IsNullOrWhiteSpace))
                throw new EffectPackageException("AuthorReplacementFontInvalid", path, "字体没有可读的家族名。");
            return new(format, null, null, families);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { throw new EffectPackageException("AuthorReplacementFontInvalid", path, "字体无法加载：" + error.Message); }
    }

    private static string? FontFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12) return null;
        if (header.StartsWith((ReadOnlySpan<byte>)[0, 1, 0, 0]) || header.StartsWith("true"u8)) return "truetype";
        if (header.StartsWith("OTTO"u8)) return "opentype";
        if (header.StartsWith("ttcf"u8)) return "collection";
        return null;
    }

    private static bool SameFamilies(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static string FormatLabel(string format) => format switch
    {
        "truetype" => "TrueType 字体",
        "opentype" => "OpenType（CFF）字体",
        "collection" => "字体集合",
        _ => format.ToUpperInvariant() + " 图片",
    };
}
