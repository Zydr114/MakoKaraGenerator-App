using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>源对象真实路径、绑定区域与固定包材质；没有伪造的脚本实例身份。</summary>
public sealed record SourceMaterialReference(string Path, string Region, MaterialSpec.PackageMaterial Material);

/// <summary>唯一的源材质引用目录；包含暂不可见对象及全部存储覆盖。</summary>
public static class SourceMaterialCatalog
{
    /// <summary>收集完整工程所有固定包材质引用，不按播放或可见性删减。</summary>
    public static ImmutableArray<SourceMaterialReference> Collect(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Collect(project.LyricStyle, project.Lyrics, project.LyricPresentation, project.PresetApplications);
    }

    /// <summary>供固定工程快照使用的完整源目录入口。</summary>
    public static ImmutableArray<SourceMaterialReference> Collect(LyricStyle style, LyricsDocument lyrics, LyricPresentationDefinition presentation,
        PresetApplications? applications = null)
    {
        ArgumentNullException.ThrowIfNull(style); ArgumentNullException.ThrowIfNull(lyrics); ArgumentNullException.ThrowIfNull(presentation);
        List<SourceMaterialReference> values = [.. Collect(style, "lyricStyle")];
        foreach (var branch in lyrics.Branches)
        {
            string path = "lyrics.branches." + branch.Id;
            Add(branch.StyleOverride, path + ".styleOverride");
            foreach (var line in branch.Lines)
            {
                string linePath = path + ".lines." + line.Id; Add(line.StyleOverride, linePath + ".styleOverride");
                foreach (var token in line.Tokens) Add(token.StyleOverride, linePath + ".tokens." + token.Id + ".styleOverride");
            }
        }
        foreach (var node in presentation.Nodes)
        {
            string path = "lyricPresentation.nodes." + node.Id + ".content";
            if (node.Content is CountdownSequenceContent sequence) values.AddRange(Collect(sequence.Appearance.ToLyricStyle(), path + ".appearance", scene: true));
            if (node.Content is TextMarkContent mark) Add(mark.AppearanceOverride?.ToStyleOverride(), path + ".appearanceOverride", scene: true);
        }
        if (applications is not null)
            foreach (var application in applications.Items)
                values.AddRange(Collect(application.Snapshot.Style, "presetApplications." + application.Id + ".snapshot.style"));
        return [.. values];
        void Add(LyricStyleOverride? overlay, string path, bool scene = false)
        {
            if (overlay is null) return;
            AddMaterial(overlay.Unsung?.Fill.Value?.Material.Value, path + Suffix(false, scene, FillMember), "fill");
            AddMaterial(overlay.Sung?.Fill.Value?.Material.Value, path + Suffix(true, scene, FillMember), "fill");
            AddMaterial(overlay.Unsung?.Strokes?.Innermost?.Material, path + Suffix(false, scene, StrokeMember), "stroke");
            if (overlay.Sung?.Strokes is { IsSet: true, Value.FollowsUnsung: false } sung)
                AddMaterial(sung.Value.Value?.Innermost?.Material, path + Suffix(true, scene, StrokeMember), "stroke");
        }
        void AddMaterial(MaterialSpec? material, string path, string region)
        { if (material is MaterialSpec.PackageMaterial package) values.Add(new(path, region, package)); }
    }

    /// <summary>模板完整样式的固定材质引用，包括未勾选的存储属性。</summary>
    /// <param name="style">完整样式。</param>
    /// <param name="path">样式所在路径。</param>
    /// <param name="scene">是否文字标记外观（两态名为 pending／elapsed）。</param>
    /// <returns>包材质引用：两态填充与两态最内层描边（已唱描边跟随时不重复列出）。</returns>
    public static ImmutableArray<SourceMaterialReference> Collect(LyricStyle style, string path = "style", bool scene = false)
    {
        ArgumentNullException.ThrowIfNull(style); List<SourceMaterialReference> values = [];
        Add(style.Unsung.Fill.Material, path + Suffix(false, scene, FillMember), "fill");
        Add(style.Sung.Fill.Material, path + Suffix(true, scene, FillMember), "fill");
        Add(style.Unsung.Strokes.Innermost?.Material, path + Suffix(false, scene, StrokeMember), "stroke");
        if (!style.Sung.Strokes.FollowsUnsung) Add(style.Sung.Strokes.Value?.Innermost?.Material, path + Suffix(true, scene, StrokeMember), "stroke");
        return [.. values];
        void Add(MaterialSpec? material, string location, string region)
        { if (material is MaterialSpec.PackageMaterial package) values.Add(new(location, region, package)); }
    }

    internal const string FillMember = "fill";
    internal const string StrokeMember = "strokes.0.material";

    /// <summary>材质引用路径的后缀，如 <c>.sung.strokes.0.material</c>；文字标记的两态名为 pending／elapsed。</summary>
    internal static string Suffix(bool sung, bool scene, string member)
        => "." + (scene ? sung ? "elapsed" : "pending" : sung ? "sung" : "unsung") + "." + member;

    private static readonly string[] SungSuffixes =
        [Suffix(true, false, FillMember), Suffix(true, true, FillMember), Suffix(true, false, StrokeMember), Suffix(true, true, StrokeMember)];

    /// <summary>引用是否属于已唱一态（文字标记为已过一态）。</summary>
    /// <param name="reference">源材质引用。</param>
    /// <returns>已唱／已过返回 <see langword="true"/>。</returns>
    public static bool IsSung(SourceMaterialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return SungSuffixes.Any(suffix => reference.Path.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>缺失完整修订不能保存或打开；参数草稿由语义校验保留。</summary>
    public static void RequireResources(Project project)
    {
        foreach (var reference in Collect(project))
        {
            if (!project.Effects.Packages.Any(p => p.PackageId == reference.Material.PackageId && p.Revision == reference.Material.Revision))
                throw new EffectPackageException("SourceMaterialPackageMissing", reference.Path, "源材质缺少完整固定包修订。");
        }
    }

    /// <summary>统一验证真实材质导出、区域及完整参数，不代替实际native编译门禁。</summary>
    public static void Validate(SourceMaterialReference reference, EffectPackageDefinition definition)
    {
        ValidateExport(reference, definition.GetExport(reference.Material.ExportId));
        _ = definition.ResolveParameters(reference.Material.ExportId, reference.Material.Parameters.Encode());
    }

    internal static void ValidateExport(SourceMaterialReference reference, EffectExportDefinition export)
    {
        if (export is not EffectMaterialDefinition material)
            throw new EffectPackageException("SourceMaterialExportInvalid", reference.Path, "源材质引用必须指向材质导出。");
        if (!material.Regions.Contains(reference.Region, StringComparer.Ordinal))
            throw new EffectPackageException("SourceMaterialRegionUnsupported", reference.Path, "材质导出不支持该源绑定区域。");
    }

}
