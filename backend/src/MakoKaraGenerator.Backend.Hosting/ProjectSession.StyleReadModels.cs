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
    private static StyleMaterialDto ProjectMaterial(MaterialSpec material, ReadModelBudget budget)
    {
        budget.Item();
        switch (material)
        {
            case MaterialSpec.Solid solid: return new SolidStyleMaterialDto(StyleColor(solid.Color));
            case MaterialSpec.Gradient gradient:
                GradientSpec g = gradient.Value; budget.Item(g.Stops.Length);
                return new GradientStyleMaterialDto(new((StyleGradientKind)g.Kind, g.AngleDegrees, g.CenterX, g.CenterY,
                    g.RadiusX, g.RadiusY, (StyleGradientCoordinateSpace)g.CoordinateSpace, (StyleGradientColorSpace)g.ColorSpace,
                    (StyleGradientHuePath)g.HuePath, [.. g.Stops.Select(stop => new StyleGradientStopDto(stop.Offset, StyleColor(stop.Color)))]));
            case MaterialSpec.PackageMaterial package:
                budget.Text(package.PackageId, package.Revision, package.ExportId);
                return new PackageStyleMaterialDto(package.PackageId, package.Revision, package.ExportId);
            default: throw new InvalidOperationException("未知工程材质。");
        }
    }
    private static StyleStrokeDto? ProjectStroke(StrokeSpec? stroke, ReadModelBudget budget) => stroke is null ? null
        : new(ProjectMaterial(stroke.Material, budget), stroke.WidthPx, (StyleStrokeJoin)stroke.Join, StyleStrokePosition.Outside);
    private static StyleShadowDto? ProjectShadow(ShadowSpec? shadow) => shadow is null ? null
        : new(StyleColor(shadow.Color), shadow.Opacity, shadow.AngleDegrees, shadow.DistancePx, shadow.BlurRadiusPx);
    private static StyleGlowDto? ProjectGlow(GlowSpec? glow) => glow is null ? null
        : new(StyleColor(glow.Color), glow.Opacity, glow.BlurRadiusPx, glow.SpreadPx);
    private static CanonicalStyleDto ProjectCanonicalStyle(LyricStyle style, ReadModelBudget budget)
    {
        budget.Item(); budget.Item(style.Typography.FallbackFamilyNames.Length);
        budget.Text(style.Typography.FontFamilyName); foreach (string name in style.Typography.FallbackFamilyNames) budget.Text(name);
        var t = style.Typography;
        return new(new(t.FontFamilyName, t.FallbackFamilyNames.ToArray(), t.FontSizePx, t.FontWeight, t.IsBold, t.IsItalic, t.LetterSpacingPx),
            new(ProjectMaterial(style.UnsungFill.Material, budget)), new(ProjectMaterial(style.SungFill.Material, budget)),
            ProjectStroke(style.Stroke, budget), ProjectShadow(style.Shadow), style.VerticalCenterRatio,
            (StyleHorizontalAlignment)style.HorizontalAlignment, style.Opacity, style.Scale, ProjectGlow(style.Glow));
    }
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
        if (own.UnsungFill.IsSet && own.UnsungFill.Value?.Material.IsSet == true) values.Add(new StyleUnsungMaterialChangeDto(ProjectMaterial(own.UnsungFill.Value.Material.Value!, budget)));
        if (own.SungFill.IsSet && own.SungFill.Value?.Material.IsSet == true) values.Add(new StyleSungMaterialChangeDto(ProjectMaterial(own.SungFill.Value.Material.Value!, budget)));
        // 过渡期（P3 第 2～6 步）：描边改为整表覆盖，HTTP 仍是单层描边。本层描边按最内层展开为全部四叶（位置恒为外侧），
        // 空表报告为关闭；已唱一侧的显式描边、阴影、辉光在第 7 步随合同开放。
        if (own.UnsungStrokes is { } strokes)
        {
            if (strokes.Innermost is not { } stroke) values.Add(new StyleStrokeChangeDto(null));
            else
            {
                values.Add(new StyleStrokeMaterialChangeDto(ProjectMaterial(stroke.Material, budget)));
                values.Add(new StyleStrokeWidthChangeDto(stroke.WidthPx));
                values.Add(new StyleStrokeJoinChangeDto((StyleStrokeJoin)stroke.Join));
                values.Add(new StyleStrokePositionChangeDto(StyleStrokePosition.Outside));
            }
        }
        if (own.Shadow.IsSet)
        {
            if (own.Shadow.Value is not { } shadow) values.Add(new StyleShadowChangeDto(null));
            else
            {
                if (shadow.Color is { } color) values.Add(new StyleShadowColorChangeDto(StyleColor(color)));
                if (shadow.Opacity is { } opacity) values.Add(new StyleShadowOpacityChangeDto(opacity));
                if (shadow.AngleDegrees is { } angle) values.Add(new StyleShadowAngleChangeDto(angle));
                if (shadow.DistancePx is { } distance) values.Add(new StyleShadowDistanceChangeDto(distance));
                if (shadow.BlurRadiusPx is { } blur) values.Add(new StyleShadowBlurChangeDto(blur));
            }
        }
        if (own.Glow.IsSet)
        {
            if (own.Glow.Value is not { } glow) values.Add(new StyleGlowChangeDto(null));
            else
            {
                if (glow.Color is { } color) values.Add(new StyleGlowColorChangeDto(StyleColor(color)));
                if (glow.Opacity is { } opacity) values.Add(new StyleGlowOpacityChangeDto(opacity));
                if (glow.BlurRadiusPx is { } blur) values.Add(new StyleGlowBlurChangeDto(blur));
                if (glow.SpreadPx is { } spread) values.Add(new StyleGlowSpreadChangeDto(spread));
            }
        }
        if (own.VerticalCenterRatio is { } center) values.Add(new StyleVerticalCenterChangeDto(center));
        if (own.HorizontalAlignment is { } alignment) values.Add(new StyleAlignmentChangeDto((StyleHorizontalAlignment)alignment));
        if (own.Opacity is { } alpha) values.Add(new StyleOpacityChangeDto(alpha));
        if (own.Scale is { } scale) values.Add(new StyleScaleChangeDto(scale));
        budget.Item(values.Count); return values.ToArray();
    }

    private static MaterialSpec CoreMaterial(StyleMaterialDto material) => material switch
    {
        SolidStyleMaterialDto { Kind: "solid", Color: { } color } => new MaterialSpec.Solid(CoreColor(color)),
        GradientStyleMaterialDto { Kind: "gradient", Gradient: { } g } => new MaterialSpec.Gradient(new()
        {
            Kind = (GradientKind)g.Kind, AngleDegrees = g.AngleDegrees, CenterX = g.CenterX, CenterY = g.CenterY,
            RadiusX = g.RadiusX, RadiusY = g.RadiusY, CoordinateSpace = (GradientCoordinateSpace)g.CoordinateSpace,
            ColorSpace = (GradientColorSpace)g.ColorSpace, HuePath = (GradientHuePath)g.HuePath,
            Stops = [.. g.Stops.Select(s => new GradientStop(s.Offset, CoreColor(s.Color)))],
        }),
        _ => throw new JsonException("新材质必须是完整静态 solid 或 gradient。"),
    };
    private static StrokeSpec? CoreStroke(StyleStrokeDto? stroke) => stroke is null ? null : new()
        { Material = CoreMaterial(stroke.Material), WidthPx = stroke.WidthPx, Join = (StrokeJoin)stroke.Join };
    private static ShadowSpec? CoreShadow(StyleShadowDto? shadow) => shadow is null ? null : new()
        { Color = CoreColor(shadow.Color), Opacity = shadow.Opacity, AngleDegrees = shadow.AngleDegrees, DistancePx = shadow.DistancePx, BlurRadiusPx = shadow.BlurRadiusPx };
    private static GlowSpec? CoreGlow(StyleGlowDto? glow) => glow is null ? null : new()
        { Color = CoreColor(glow.Color), Opacity = glow.Opacity, BlurRadiusPx = glow.BlurRadiusPx, SpreadPx = glow.SpreadPx };
    private static readonly string[] StylePropertyNames =
    ["typography.fontFamilyName","typography.fallbackFamilyNames","typography.fontSizePx","typography.fontWeight","typography.isBold","typography.isItalic","typography.letterSpacingPx","unsungFill.material","sungFill.material","stroke","stroke.material","stroke.widthPx","stroke.join","stroke.position","shadow","shadow.color","shadow.opacity","shadow.angleDegrees","shadow.distancePx","shadow.blurRadiusPx","verticalCenterRatio","horizontalAlignment","opacity","scale","glow","glow.color","glow.opacity","glow.blurRadiusPx","glow.spreadPx"];
    private static StyleEditProperty CoreStyleProperty(string property)
    {
        int index = Array.IndexOf(StylePropertyNames, property);
        return index >= 0 ? (StyleEditProperty)index : throw new JsonException("未知样式属性。");
    }
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
            StyleUnsungMaterialChangeDto v => new StyleEditChange.UnsungMaterial(CoreMaterial(v.Value)),
            StyleSungMaterialChangeDto v => new StyleEditChange.SungMaterial(CoreMaterial(v.Value)),
            StyleStrokeChangeDto v => new StyleEditChange.Stroke(CoreStroke(v.Value)),
            StyleStrokeMaterialChangeDto v => new StyleEditChange.StrokeMaterial(CoreMaterial(v.Value)),
            StyleStrokeWidthChangeDto v => new StyleEditChange.StrokeWidth(v.Value),
            StyleStrokeJoinChangeDto v => new StyleEditChange.StrokeJoin((StrokeJoin)v.Value),
            StyleStrokePositionChangeDto => new StyleEditChange.StrokePosition(),
            StyleShadowChangeDto v => new StyleEditChange.Shadow(CoreShadow(v.Value)),
            StyleShadowColorChangeDto v => new StyleEditChange.ShadowColor(CoreColor(v.Value)),
            StyleShadowOpacityChangeDto v => new StyleEditChange.ShadowOpacity(v.Value),
            StyleShadowAngleChangeDto v => new StyleEditChange.ShadowAngle(v.Value),
            StyleShadowDistanceChangeDto v => new StyleEditChange.ShadowDistance(v.Value),
            StyleShadowBlurChangeDto v => new StyleEditChange.ShadowBlur(v.Value),
            StyleGlowChangeDto v => new StyleEditChange.Glow(CoreGlow(v.Value)),
            StyleGlowColorChangeDto v => new StyleEditChange.GlowColor(CoreColor(v.Value)),
            StyleGlowOpacityChangeDto v => new StyleEditChange.GlowOpacity(v.Value),
            StyleGlowBlurChangeDto v => new StyleEditChange.GlowBlur(v.Value),
            StyleGlowSpreadChangeDto v => new StyleEditChange.GlowSpread(v.Value),
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
