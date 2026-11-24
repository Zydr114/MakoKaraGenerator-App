using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal Task<BackendOperationResult<ProjectStyleResponse>> ReadStyleAsync(string? expectedRevision,
        StyleTargetDto target, CancellationToken cancellationToken) => ReadModelAsync(expectedRevision, () =>
        {
            if (!TryStyleTarget(target, out StyleEditTarget? stable))
                return BackendOperationResult<ProjectStyleResponse>.Rejected(InvalidStyle("target"));
            if (!TryStyleLayers(Workspace.Project, stable!, out _, out _, out _, out _))
                return BackendOperationResult<ProjectStyleResponse>.Rejected(StyleRejected("TargetStale", "target"));
            try { return BoundedReadModel(ProjectStyle(Workspace.Project, stable!, RevisionText)); }
            catch (ArgumentException error)
            {
                return BackendOperationResult<ProjectStyleResponse>.Rejected(StyleRejected("InvalidValue", "style", error.Message));
            }
        }, cancellationToken, "style");

    private ProjectStyleResponse ProjectStyle(Project project, StyleEditTarget target, string revision)
    {
        if (!TryStyleLayers(project, target, out LyricStyleOverride? branch, out LyricStyleOverride? line,
                out LyricStyleOverride? token, out LyricStyleOverride? own)) throw new InvalidOperationException("固定样式目标不存在。");
        ReadModelBudget budget = new(); budget.Text(ProjectSessionId, revision, target.BranchId, target.LineId, target.TokenId);
        CanonicalStyleDto baseline = ProjectCanonicalStyle(project.LyricStyle, budget);
        StyleChangeDto[] local = ProjectLocalStyle(own, budget);
        LyricStyle resolved = LyricStyleResolver.Resolve(project.LyricStyle, branchOverride: branch,
            lineOverride: line, tokenOverride: token).Style;
        return new(ProjectSessionId, revision, StyleTarget(target), baseline, local,
            ProjectCanonicalStyle(resolved, budget), project.PresetApplications.Items.Length != 0);
    }

    private static bool TryStyleLayers(Project project, StyleEditTarget target, out LyricStyleOverride? branchStyle,
        out LyricStyleOverride? lineStyle, out LyricStyleOverride? tokenStyle, out LyricStyleOverride? own)
    {
        branchStyle = lineStyle = tokenStyle = own = null;
        if (target.Kind == StyleEditTargetKind.Project) return true;
        LyricBranch? branch = project.Lyrics.Branches.FirstOrDefault(b => b.Id == target.BranchId);
        if (branch is null) return false;
        branchStyle = own = branch.StyleOverride;
        if (target.Kind == StyleEditTargetKind.Branch) return true;
        LyricLine? line = branch.Lines.FirstOrDefault(l => l.Id == target.LineId);
        if (line is null) return false;
        lineStyle = own = line.StyleOverride;
        if (target.Kind == StyleEditTargetKind.Line) return true;
        LyricToken? token = line.Tokens.FirstOrDefault(t => t.Id == target.TokenId);
        if (token is null) return false;
        tokenStyle = own = token.StyleOverride;
        return true;
    }

    private static bool IsStyleStableId(string? id) => !string.IsNullOrWhiteSpace(id);

    private static bool TryStyleTarget(StyleTargetDto? dto, out StyleEditTarget? target)
    {
        target = dto switch
        {
            ProjectStyleTargetDto { Kind: "project" } => new(StyleEditTargetKind.Project),
            BranchStyleTargetDto { Kind: "branch" } b when IsStyleStableId(b.BranchId) => new(StyleEditTargetKind.Branch, b.BranchId),
            LineStyleTargetDto { Kind: "line" } l when IsStyleStableId(l.BranchId) && IsStyleStableId(l.LineId) => new(StyleEditTargetKind.Line, l.BranchId, l.LineId),
            TokenStyleTargetDto { Kind: "token" } t when IsStyleStableId(t.BranchId) && IsStyleStableId(t.LineId) && IsStyleStableId(t.TokenId) => new(StyleEditTargetKind.Token, t.BranchId, t.LineId, t.TokenId),
            _ => null,
        };
        return target is not null;
    }
    private static StyleTargetDto StyleTarget(StyleEditTarget target) => target.Kind switch
    {
        StyleEditTargetKind.Project => new ProjectStyleTargetDto(),
        StyleEditTargetKind.Branch => new BranchStyleTargetDto(target.BranchId!),
        StyleEditTargetKind.Line => new LineStyleTargetDto(target.BranchId!, target.LineId!),
        _ => new TokenStyleTargetDto(target.BranchId!, target.LineId!, target.TokenId!),
    };
    private static StyleColorDto StyleColor(RgbaColor color) => new(color.Red, color.Green, color.Blue, color.Alpha);
    private static RgbaColor CoreColor(StyleColorDto color) => new(color.Red, color.Green, color.Blue, color.Alpha);
    private static StyleGradientDto ProjectGradient(GradientSpec g, ReadModelBudget budget)
    {
        budget.Item(g.Stops.Length);
        return new((StyleGradientKind)g.Kind, g.AngleDegrees, g.CenterX, g.CenterY,
            g.RadiusX, g.RadiusY, (StyleGradientCoordinateSpace)g.CoordinateSpace, (StyleGradientColorSpace)g.ColorSpace,
            (StyleGradientHuePath)g.HuePath, [.. g.Stops.Select(stop => new StyleGradientStopDto(stop.Offset, StyleColor(stop.Color)))]);
    }
    private static StyleMaterialDto ProjectMaterial(MaterialSpec material, ReadModelBudget budget)
    {
        budget.Item();
        switch (material)
        {
            case MaterialSpec.Solid solid: return new SolidStyleMaterialDto(StyleColor(solid.Color));
            case MaterialSpec.Gradient gradient: return new GradientStyleMaterialDto(ProjectGradient(gradient.Value, budget));
            case MaterialSpec.PackageMaterial package:
                budget.Text(package.PackageId, package.Revision, package.ExportId);
                return new PackageStyleMaterialDto(package.PackageId, package.Revision, package.ExportId);
            default: throw new InvalidOperationException("未知工程材质。");
        }
    }
    private static StyleStrokeDto[] ProjectStrokes(StrokeLayers strokes, ReadModelBudget budget)
    {
        budget.Item(strokes.Count);
        return [.. strokes.Select(stroke => new StyleStrokeDto(ProjectMaterial(stroke.Material, budget), stroke.WidthPx, (StyleStrokeJoin)stroke.Join))];
    }
    private static StyleShadowDto? ProjectShadow(ShadowSpec? shadow, ReadModelBudget budget) => shadow is null ? null
        : new(StyleColor(shadow.Color), shadow.Opacity, shadow.AngleDegrees, shadow.DistancePx, shadow.BlurRadiusPx, shadow.SpreadPx,
            shadow.Gradient is { } gradient ? ProjectGradient(gradient, budget) : null);
    private static StyleGlowDto? ProjectGlow(GlowSpec? glow) => glow is null ? null
        : new(StyleColor(glow.Color), glow.Opacity, glow.BlurRadiusPx, glow.SpreadPx);
    private static StyleSungStrokesDto ProjectSungStrokes(Followable<StrokeLayers> strokes, ReadModelBudget budget) => strokes.FollowsUnsung
        ? new(StyleFollowKind.Follow) : new(StyleFollowKind.Set, ProjectStrokes(strokes.Value!, budget));
    private static StyleSungShadowDto ProjectSungShadow(Followable<ShadowSpec?> shadow, ReadModelBudget budget) => shadow.FollowsUnsung
        ? new(StyleFollowKind.Follow) : new(StyleFollowKind.Set, ProjectShadow(shadow.Value, budget));
    private static StyleSungGlowDto ProjectSungGlow(Followable<GlowSpec?> glow) => glow.FollowsUnsung
        ? new(StyleFollowKind.Follow) : new(StyleFollowKind.Set, ProjectGlow(glow.Value));
    private static StyleAppearanceDto ProjectAppearance(TextAppearance appearance, ReadModelBudget budget)
    {
        return new(ProjectMaterial(appearance.Fill.Material, budget), ProjectStrokes(appearance.Strokes, budget),
            ProjectShadow(appearance.Shadow, budget), ProjectGlow(appearance.Glow));
    }
    private static StyleSungAppearanceDto ProjectSungAppearance(SungAppearance appearance, ReadModelBudget budget)
    {
        return new(ProjectMaterial(appearance.Fill.Material, budget), ProjectSungStrokes(appearance.Strokes, budget),
            ProjectSungShadow(appearance.Shadow, budget), ProjectSungGlow(appearance.Glow));
    }
    private static StyleWipeHighlightDto? ProjectHighlight(WipeHighlightSpec? highlight) => highlight is null ? null
        : new(StyleColor(highlight.Color), highlight.WidthRatio, highlight.Opacity, (StyleWipeHighlightBlend)highlight.Blend);
    private static StyleTypographyDto ProjectTypography(TypographySpec t, ReadModelBudget budget)
    {
        budget.Item(t.FallbackFamilyNames.Length);
        budget.Text(t.FontFamilyName); foreach (string name in t.FallbackFamilyNames) budget.Text(name);
        return new(t.FontFamilyName, t.FallbackFamilyNames.ToArray(), t.FontSizePx, t.FontWeight, t.IsBold, t.IsItalic, t.LetterSpacingPx);
    }
    private static CanonicalStyleDto ProjectCanonicalStyle(LyricStyle style, ReadModelBudget budget)
    {
        budget.Item();
        return new(ProjectTypography(style.Typography, budget), ProjectAppearance(style.Unsung, budget), ProjectSungAppearance(style.Sung, budget),
            new(style.Wipe.FeatherRatio, ProjectHighlight(style.Wipe.Highlight)), style.VerticalCenterRatio,
            (StyleHorizontalAlignment)style.HorizontalAlignment, style.Opacity, style.Scale,
            new(style.Ruby.RelativeScale, style.Ruby.GapPx));
    }
    // 本层原始存在的成员：整体关闭、跟随与整表按整体项报告，非空的部分阴影／辉光／高亮按实际存在的分量展开。
    private static StyleChangeDto[] ProjectLocalStyle(LyricStyleOverride? own, ReadModelBudget budget)
    {
        if (own is null) return [];
        var values = new List<StyleChangeDto>();
        if (own.Typography is { } t)
        {
            if (t.FontFamilyName.IsSet) { budget.Text(t.FontFamilyName.Value); values.Add(new StyleFontFamilyChangeDto(t.FontFamilyName.Value)); }
            if (t.FallbackFamilyNames is { } fallback)
            {
                budget.Item(fallback.Length); foreach (string name in fallback) budget.Text(name);
                values.Add(new StyleFallbackFamiliesChangeDto(fallback.ToArray()));
            }
            if (t.FontSizePx is { } size) values.Add(new StyleFontSizeChangeDto(size));
            if (t.FontWeight is { } weight) values.Add(new StyleFontWeightChangeDto(weight));
            if (t.IsBold is { } bold) values.Add(new StyleBoldChangeDto(bold));
            if (t.IsItalic is { } italic) values.Add(new StyleItalicChangeDto(italic));
            if (t.LetterSpacingPx is { } spacing) values.Add(new StyleLetterSpacingChangeDto(spacing));
        }
        if (own.Ruby is { } ruby)
        {
            if (ruby.RelativeScale.IsSet) values.Add(new StyleRubyRelativeScaleChangeDto(ruby.RelativeScale.Value));
            if (ruby.GapPx.IsSet) values.Add(new StyleRubyGapChangeDto(ruby.GapPx.Value));
        }
        if (own.Unsung is { } unsung)
        {
            if (FillMaterial(unsung.Fill) is { } fill) values.Add(new StyleUnsungFillChangeDto(ProjectMaterial(fill, budget)));
            if (unsung.Strokes is { } strokes) values.Add(new StyleUnsungStrokesChangeDto(ProjectStrokes(strokes, budget)));
            if (unsung.Shadow.IsSet)
            {
                if (unsung.Shadow.Value is not { } shadow) values.Add(new StyleUnsungShadowChangeDto(null));
                else AddShadowLeaves(values, shadow, sung: false, budget);
            }
            if (unsung.Glow.IsSet)
            {
                if (unsung.Glow.Value is not { } glow) values.Add(new StyleUnsungGlowChangeDto(null));
                else AddGlowLeaves(values, glow, sung: false);
            }
        }
        if (own.Sung is { } sung)
        {
            if (FillMaterial(sung.Fill) is { } fill) values.Add(new StyleSungFillChangeDto(ProjectMaterial(fill, budget)));
            if (sung.Strokes.IsSet) values.Add(new StyleSungStrokesChangeDto(ProjectSungStrokes(sung.Strokes.Value, budget)));
            if (sung.Shadow.IsSet)
            {
                if (sung.Shadow.Value.FollowsUnsung) values.Add(new StyleSungShadowChangeDto(new(StyleFollowKind.Follow)));
                else if (sung.Shadow.Value.Value is not { } shadow) values.Add(new StyleSungShadowChangeDto(new(StyleFollowKind.Set)));
                else AddShadowLeaves(values, shadow, sung: true, budget);
            }
            if (sung.Glow.IsSet)
            {
                if (sung.Glow.Value.FollowsUnsung) values.Add(new StyleSungGlowChangeDto(new(StyleFollowKind.Follow)));
                else if (sung.Glow.Value.Value is not { } glow) values.Add(new StyleSungGlowChangeDto(new(StyleFollowKind.Set)));
                else AddGlowLeaves(values, glow, sung: true);
            }
        }
        if (own.Wipe is { } wipe)
        {
            if (wipe.FeatherRatio is { } feather) values.Add(new StyleWipeFeatherChangeDto(feather));
            if (wipe.Highlight.IsSet)
            {
                if (wipe.Highlight.Value is not { } highlight) values.Add(new StyleWipeHighlightChangeDto(null));
                else
                {
                    if (highlight.Color is { } color) values.Add(new StyleWipeHighlightColorChangeDto(StyleColor(color)));
                    if (highlight.WidthRatio is { } width) values.Add(new StyleWipeHighlightWidthChangeDto(width));
                    if (highlight.Opacity is { } opacity) values.Add(new StyleWipeHighlightOpacityChangeDto(opacity));
                    if (highlight.Blend is { } blend) values.Add(new StyleWipeHighlightBlendChangeDto((StyleWipeHighlightBlend)blend));
                }
            }
        }
        if (own.VerticalCenterRatio is { } center) values.Add(new StyleVerticalCenterChangeDto(center));
        if (own.HorizontalAlignment is { } alignment) values.Add(new StyleAlignmentChangeDto((StyleHorizontalAlignment)alignment));
        if (own.Opacity is { } alpha) values.Add(new StyleOpacityChangeDto(alpha));
        if (own.Scale is { } scale) values.Add(new StyleScaleChangeDto(scale));
        // 按样式属性的规范顺序输出，与 sources 及属性路径表一致。
        budget.Item(values.Count); return [.. values.OrderBy(value => CoreStyleProperty(value.Property))];
    }
    private static MaterialSpec? FillMaterial(OptionalValue<FillSpecOverride?> fill)
        => fill.IsSet && fill.Value?.Material.IsSet == true ? fill.Value.Material.Value : null;
    private static void AddShadowLeaves(List<StyleChangeDto> values, ShadowSpecOverride shadow, bool sung, ReadModelBudget budget)
    {
        if (shadow.Color is { } color) values.Add(sung ? new StyleSungShadowColorChangeDto(StyleColor(color)) : new StyleUnsungShadowColorChangeDto(StyleColor(color)));
        if (shadow.Gradient.IsSet)
        {
            StyleGradientDto? gradient = shadow.Gradient.Value is { } g ? ProjectGradient(g, budget) : null;
            values.Add(sung ? new StyleSungShadowGradientChangeDto(gradient) : new StyleUnsungShadowGradientChangeDto(gradient));
        }
        if (shadow.Opacity is { } opacity) values.Add(sung ? new StyleSungShadowOpacityChangeDto(opacity) : new StyleUnsungShadowOpacityChangeDto(opacity));
        if (shadow.AngleDegrees is { } angle) values.Add(sung ? new StyleSungShadowAngleChangeDto(angle) : new StyleUnsungShadowAngleChangeDto(angle));
        if (shadow.DistancePx is { } distance) values.Add(sung ? new StyleSungShadowDistanceChangeDto(distance) : new StyleUnsungShadowDistanceChangeDto(distance));
        if (shadow.BlurRadiusPx is { } blur) values.Add(sung ? new StyleSungShadowBlurChangeDto(blur) : new StyleUnsungShadowBlurChangeDto(blur));
        if (shadow.SpreadPx is { } spread) values.Add(sung ? new StyleSungShadowSpreadChangeDto(spread) : new StyleUnsungShadowSpreadChangeDto(spread));
    }
    private static void AddGlowLeaves(List<StyleChangeDto> values, GlowSpecOverride glow, bool sung)
    {
        if (glow.Color is { } color) values.Add(sung ? new StyleSungGlowColorChangeDto(StyleColor(color)) : new StyleUnsungGlowColorChangeDto(StyleColor(color)));
        if (glow.Opacity is { } opacity) values.Add(sung ? new StyleSungGlowOpacityChangeDto(opacity) : new StyleUnsungGlowOpacityChangeDto(opacity));
        if (glow.BlurRadiusPx is { } blur) values.Add(sung ? new StyleSungGlowBlurChangeDto(blur) : new StyleUnsungGlowBlurChangeDto(blur));
        if (glow.SpreadPx is { } spread) values.Add(sung ? new StyleSungGlowSpreadChangeDto(spread) : new StyleUnsungGlowSpreadChangeDto(spread));
    }

    private static GradientSpec CoreGradient(StyleGradientDto g) => new()
    {
        Kind = (GradientKind)g.Kind, AngleDegrees = g.AngleDegrees, CenterX = g.CenterX, CenterY = g.CenterY,
        RadiusX = g.RadiusX, RadiusY = g.RadiusY, CoordinateSpace = (GradientCoordinateSpace)g.CoordinateSpace,
        ColorSpace = (GradientColorSpace)g.ColorSpace, HuePath = (GradientHuePath)g.HuePath,
        Stops = [.. g.Stops.Select(s => new GradientStop(s.Offset, CoreColor(s.Color)))],
    };
    private static MaterialSpec CoreMaterial(StyleMaterialDto material) => material switch
    {
        SolidStyleMaterialDto { Kind: "solid", Color: { } color } => new MaterialSpec.Solid(CoreColor(color)),
        GradientStyleMaterialDto { Kind: "gradient", Gradient: { } g } => new MaterialSpec.Gradient(CoreGradient(g)),
        _ => throw new JsonException("新材质必须是完整静态 solid 或 gradient。"),
    };
    private static StrokeLayers CoreStrokes(StyleStrokeDto[]? strokes, Func<StyleMaterialDto, MaterialSpec> material)
    {
        if (strokes is null || strokes.Length > StyleEditingLimits.MaxStrokes || strokes.Any(stroke => stroke is null)) throw new JsonException("描边必须是至多4层的数组。");
        return StrokeLayers.From(strokes.Select(stroke => new StrokeSpec { Material = material(stroke.Material), WidthPx = stroke.WidthPx, Join = (StrokeJoin)stroke.Join }));
    }
    private static StrokeLayers CoreStrokes(StyleStrokeDto[]? strokes) => CoreStrokes(strokes, CoreMaterial);
    private static ShadowSpec? CoreShadow(StyleShadowDto? shadow) => shadow is null ? null : new()
    {
        Color = CoreColor(shadow.Color),
        Gradient = shadow.Gradient is { } g ? CoreGradient(g) : null,
        Opacity = shadow.Opacity,
        AngleDegrees = shadow.AngleDegrees,
        DistancePx = shadow.DistancePx,
        BlurRadiusPx = shadow.BlurRadiusPx,
        SpreadPx = shadow.SpreadPx,
    };
    private static GlowSpec? CoreGlow(StyleGlowDto? glow) => glow is null ? null : new()
        { Color = CoreColor(glow.Color), Opacity = glow.Opacity, BlurRadiusPx = glow.BlurRadiusPx, SpreadPx = glow.SpreadPx };
    private static WipeHighlightSpec? CoreHighlight(StyleWipeHighlightDto? highlight) => highlight is null ? null : new()
        { Color = CoreColor(highlight.Color), WidthRatio = highlight.WidthRatio, Opacity = highlight.Opacity, Blend = (WipeHighlightBlend)highlight.Blend };
    // 跟随不带值；显式取值的描边必须是数组，阴影与辉光可为 null（关闭）。
    private static bool IsFollow(StyleFollowKind kind, bool hasValue) => kind switch
    {
        StyleFollowKind.Follow when !hasValue => true,
        StyleFollowKind.Set => false,
        _ => throw new JsonException("已唱跟随项的 kind 与 value 不一致。"),
    };
    private static Followable<StrokeLayers> CoreSungStrokes(StyleSungStrokesDto? strokes, Func<StyleMaterialDto, MaterialSpec> material)
        => strokes is null ? throw new JsonException("缺少已唱描边。") : IsFollow(strokes.Kind, strokes.Value is not null)
            ? Followable.Follow<StrokeLayers>() : Followable.Set(CoreStrokes(strokes.Value, material));
    private static Followable<ShadowSpec?> CoreSungShadow(StyleSungShadowDto? shadow)
        => shadow is null ? throw new JsonException("缺少已唱阴影。") : IsFollow(shadow.Kind, shadow.Value is not null)
            ? Followable.Follow<ShadowSpec?>() : Followable.Set<ShadowSpec?>(CoreShadow(shadow.Value));
    private static Followable<GlowSpec?> CoreSungGlow(StyleSungGlowDto? glow)
        => glow is null ? throw new JsonException("缺少已唱辉光。") : IsFollow(glow.Kind, glow.Value is not null)
            ? Followable.Follow<GlowSpec?>() : Followable.Set<GlowSpec?>(CoreGlow(glow.Value));
    private static TextAppearance CoreAppearance(StyleAppearanceDto? appearance, Func<StyleMaterialDto, MaterialSpec> material)
    {
        if (appearance is null) throw new JsonException("缺少外观。");
        return new() { Fill = new() { Material = material(appearance.Fill) }, Strokes = CoreStrokes(appearance.Strokes, material),
            Shadow = CoreShadow(appearance.Shadow), Glow = CoreGlow(appearance.Glow) };
    }
    private static SungAppearance CoreSungAppearance(StyleSungAppearanceDto? appearance, Func<StyleMaterialDto, MaterialSpec> material)
    {
        if (appearance is null) throw new JsonException("缺少已唱外观。");
        return new() { Fill = new() { Material = material(appearance.Fill) }, Strokes = CoreSungStrokes(appearance.Strokes, material),
            Shadow = CoreSungShadow(appearance.Shadow), Glow = CoreSungGlow(appearance.Glow) };
    }
    private static TypographySpec CoreTypography(StyleTypographyDto? t) => t is null || t.FallbackFamilyNames is null ? throw new JsonException("缺少字形。") : new()
    {
        FontFamilyName = t.FontFamilyName, FallbackFamilyNames = [.. t.FallbackFamilyNames], FontSizePx = t.FontSizePx, FontWeight = t.FontWeight,
        IsBold = t.IsBold, IsItalic = t.IsItalic, LetterSpacingPx = t.LetterSpacingPx,
    };
    private static LyricStyleProperty CoreStyleProperty(string property)
        => LyricStylePropertyExtensions.TryParseJsonName(property, out LyricStyleProperty value) ? value : throw new JsonException("未知样式属性。");
    private static StyleEditChange CoreStyleChange(StyleChangeDto value)
    {
        StyleEditChange result = value switch
        {
            StyleFontFamilyChangeDto v => new StyleEditChange.FontFamilyName(v.Value),
            StyleFallbackFamiliesChangeDto v => new StyleEditChange.FallbackFamilyNames(ImmutableArray.CreateRange(v.Value)),
            StyleFontSizeChangeDto v => new StyleEditChange.FontSize(v.Value),
            StyleFontWeightChangeDto v => new StyleEditChange.FontWeight(v.Value),
            StyleBoldChangeDto v => new StyleEditChange.IsBold(v.Value),
            StyleItalicChangeDto v => new StyleEditChange.IsItalic(v.Value),
            StyleLetterSpacingChangeDto v => new StyleEditChange.LetterSpacing(v.Value),
            StyleRubyRelativeScaleChangeDto v => new StyleEditChange.RubyRelativeScale(v.Value),
            StyleRubyGapChangeDto v => new StyleEditChange.RubyGap(v.Value),
            StyleUnsungFillChangeDto v => new StyleEditChange.Fill(false, CoreMaterial(v.Value)),
            StyleSungFillChangeDto v => new StyleEditChange.Fill(true, CoreMaterial(v.Value)),
            StyleUnsungStrokesChangeDto v => new StyleEditChange.UnsungStrokes(CoreStrokes(v.Value)),
            StyleSungStrokesChangeDto v => new StyleEditChange.SungStrokes(CoreSungStrokes(v.Value, CoreMaterial)),
            StyleUnsungShadowChangeDto v => new StyleEditChange.UnsungShadow(CoreShadow(v.Value)),
            StyleUnsungShadowColorChangeDto v => new StyleEditChange.EffectColor(LyricStyleProperty.UnsungShadowColor, CoreColor(v.Value)),
            StyleUnsungShadowGradientChangeDto v => new StyleEditChange.EffectGradient(LyricStyleProperty.UnsungShadowGradient, v.Value is { } g ? CoreGradient(g) : null),
            StyleUnsungShadowOpacityChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowOpacity, v.Value),
            StyleUnsungShadowAngleChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowAngleDegrees, v.Value),
            StyleUnsungShadowDistanceChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowDistancePx, v.Value),
            StyleUnsungShadowBlurChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowBlurRadiusPx, v.Value),
            StyleUnsungShadowSpreadChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungShadowSpreadPx, v.Value),
            StyleSungShadowChangeDto v => new StyleEditChange.SungShadow(CoreSungShadow(v.Value)),
            StyleSungShadowColorChangeDto v => new StyleEditChange.EffectColor(LyricStyleProperty.SungShadowColor, CoreColor(v.Value)),
            StyleSungShadowGradientChangeDto v => new StyleEditChange.EffectGradient(LyricStyleProperty.SungShadowGradient, v.Value is { } g ? CoreGradient(g) : null),
            StyleSungShadowOpacityChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungShadowOpacity, v.Value),
            StyleSungShadowAngleChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungShadowAngleDegrees, v.Value),
            StyleSungShadowDistanceChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungShadowDistancePx, v.Value),
            StyleSungShadowBlurChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungShadowBlurRadiusPx, v.Value),
            StyleSungShadowSpreadChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungShadowSpreadPx, v.Value),
            StyleUnsungGlowChangeDto v => new StyleEditChange.UnsungGlow(CoreGlow(v.Value)),
            StyleUnsungGlowColorChangeDto v => new StyleEditChange.EffectColor(LyricStyleProperty.UnsungGlowColor, CoreColor(v.Value)),
            StyleUnsungGlowOpacityChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungGlowOpacity, v.Value),
            StyleUnsungGlowBlurChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungGlowBlurRadiusPx, v.Value),
            StyleUnsungGlowSpreadChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.UnsungGlowSpreadPx, v.Value),
            StyleSungGlowChangeDto v => new StyleEditChange.SungGlow(CoreSungGlow(v.Value)),
            StyleSungGlowColorChangeDto v => new StyleEditChange.EffectColor(LyricStyleProperty.SungGlowColor, CoreColor(v.Value)),
            StyleSungGlowOpacityChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungGlowOpacity, v.Value),
            StyleSungGlowBlurChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungGlowBlurRadiusPx, v.Value),
            StyleSungGlowSpreadChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.SungGlowSpreadPx, v.Value),
            StyleWipeFeatherChangeDto v => new StyleEditChange.WipeFeather(v.Value),
            StyleWipeHighlightChangeDto v => new StyleEditChange.WipeHighlight(CoreHighlight(v.Value)),
            StyleWipeHighlightColorChangeDto v => new StyleEditChange.EffectColor(LyricStyleProperty.WipeHighlightColor, CoreColor(v.Value)),
            StyleWipeHighlightWidthChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.WipeHighlightWidthRatio, v.Value),
            StyleWipeHighlightOpacityChangeDto v => new StyleEditChange.EffectNumber(LyricStyleProperty.WipeHighlightOpacity, v.Value),
            StyleWipeHighlightBlendChangeDto v => new StyleEditChange.HighlightBlend((WipeHighlightBlend)v.Value),
            StyleVerticalCenterChangeDto v => new StyleEditChange.VerticalCenter(v.Value),
            StyleAlignmentChangeDto v => new StyleEditChange.HorizontalAlignment((LyricHorizontalAlignment)v.Value),
            StyleOpacityChangeDto v => new StyleEditChange.Opacity(v.Value),
            StyleScaleChangeDto v => new StyleEditChange.Scale(v.Value),
            _ => throw new JsonException("未知样式属性 DTO。"),
        };
        if (CoreStyleProperty(value.Property) != result.Property) throw new JsonException("属性判别符与其 DTO 不一致。");
        return result;
    }

    private static BackendErrorResponse InvalidStyle(string field) => new(BackendErrorCode.InvalidRequest, "静态样式目标、属性或形状非法。", field);
    private static BackendErrorResponse StyleRejected(string code, string field, string? detail = null) => new(BackendErrorCode.CommandRejected,
        detail ?? "静态样式操作被共享领域规则拒绝。", field, FailureCode: code);
}
