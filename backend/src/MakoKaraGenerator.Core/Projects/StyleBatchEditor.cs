namespace MakoKaraGenerator.Core.Projects;

/// <summary>批量样式的写入层级。</summary>
public enum StyleTargetLevel
{
    /// <summary>工程默认。</summary>
    Project,
    /// <summary>歌词行覆盖。</summary>
    Line,
    /// <summary>元素覆盖。</summary>
    Token,
}

/// <summary>一次样式应用的目标位置；工程层忽略下标。</summary>
/// <param name="LineIndex">行下标。</param>
/// <param name="TokenIndex">元素下标；行目标为 -1。</param>
public readonly record struct StyleTarget(int LineIndex, int TokenIndex = -1);

/// <summary>原子的批量样式应用；不修改输入工程，失败时不产生部分结果。</summary>
public static class StyleBatchEditor
{
    /// <summary>应用设计稿中选定的属性，或使这些属性恢复继承。</summary>
    /// <param name="project">原工程。</param>
    /// <param name="level">写入层级。</param>
    /// <param name="targets">目标位置，重复项只处理一次。</param>
    /// <param name="style">设计稿的静态样式。</param>
    /// <param name="properties">参与此次应用的属性。</param>
    /// <param name="includeAnimation">是否同时应用动画。</param>
    /// <param name="animation">设计稿动画；行级空值表示继承。</param>
    /// <param name="restoreInheritance">是否移除目标层的选定覆盖。</param>
    /// <returns>完整结果；无变化时返回原工程。</returns>
    public static Project Apply(Project project, StyleTargetLevel level, IEnumerable<StyleTarget> targets,
        LyricStyle style, IEnumerable<LyricStyleProperty> properties, bool includeAnimation = false,
        LineAnimation? animation = null, bool restoreInheritance = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(properties);
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        LyricStyleProperty[] selected = [.. properties.Distinct()];
        StyleTarget[] positions = [.. targets.Distinct()];
        foreach (LyricStyleProperty property in selected)
        {
            if (!Enum.IsDefined(property)) throw new ArgumentOutOfRangeException(nameof(properties));
            if (level == StyleTargetLevel.Token && !TokenStyleSupport.IsSupported(property))
                throw new ArgumentException("元素不支持选定的样式属性。", nameof(properties));
        }
        if (level == StyleTargetLevel.Token && includeAnimation)
            throw new ArgumentException("元素不支持进入动画。", nameof(includeAnimation));
        if (level == StyleTargetLevel.Project && restoreInheritance)
            throw new ArgumentException("工程默认不能恢复上层继承。", nameof(restoreInheritance));
        if (level != StyleTargetLevel.Project)
        {
            if (positions.Length == 0) throw new ArgumentException("没有应用目标。", nameof(targets));
            foreach (StyleTarget target in positions)
            {
                if (target.LineIndex < 0 || target.LineIndex >= project.Lyrics.Lines.Length
                    || (level == StyleTargetLevel.Token && (target.TokenIndex < 0
                        || target.TokenIndex >= project.Lyrics.Lines[target.LineIndex].Tokens.Length)))
                    throw new ArgumentOutOfRangeException(nameof(targets), "应用目标已失效。");
            }
        }
        if (includeAnimation && !restoreInheritance) ValidateAnimation(animation);
        if (selected.Length == 0 && !includeAnimation) return project;

        LyricStyleOverride patch = CopyProperties(LyricStyleOverride.Empty, style, selected, restoreInheritance);
        // DEBUG
        Project result = project;
        if (level == StyleTargetLevel.Project)
        {
            LyricStyle baseline = LyricStyleResolver.Resolve(project.LyricStyle, lineOverride: patch).Style;
            ValidateFinite(baseline);
            if (baseline != project.LyricStyle) result = LyricStyleEditor.SetBaseline(result, baseline);
            return includeAnimation ? LineAnimationEditor.SetProjectAnimation(result, animation) : result;
        }
        foreach (StyleTarget target in positions)
        {
            var line = result.Lyrics.Lines[target.LineIndex];
            LyricStyleOverride existing = (level == StyleTargetLevel.Line
                ? line.StyleOverride : line.Tokens[target.TokenIndex].StyleOverride) ?? LyricStyleOverride.Empty;
            LyricStyleOverride updated = CopyProperties(existing, style, selected, restoreInheritance);
            LyricStyle resolved = LyricStyleResolver.Resolve(result.LyricStyle,
                lineOverride: level == StyleTargetLevel.Line ? updated : line.StyleOverride,
                tokenOverride: level == StyleTargetLevel.Token ? updated : null).Style;
            ValidateFinite(resolved);
            result = level == StyleTargetLevel.Line
                ? LyricStyleEditor.SetLineOverride(result, target.LineIndex, updated)
                : LyricStyleEditor.SetTokenOverride(result, target.LineIndex, target.TokenIndex, updated);
            if (includeAnimation)
                result = LineAnimationEditor.SetLineAnimation(result, target.LineIndex, restoreInheritance ? null : animation);
        }
        return result;
    }

    /// <summary>把选中属性写入现有覆盖；恢复继承时只清除这些属性。</summary>
    public static LyricStyleOverride CopyProperties(LyricStyleOverride overlay, LyricStyle style,
        IEnumerable<LyricStyleProperty> properties, bool restoreInheritance = false)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(properties);
        foreach (LyricStyleProperty property in properties)
        {
            overlay = property switch
            {
                LyricStyleProperty.FontFamilyName => overlay with { FontFamilyName = restoreInheritance ? default(OptionalValue<string?>) : OptionalValue.Set<string?>(style.FontFamilyName) },
                LyricStyleProperty.FallbackFamilyNames => overlay with { FallbackFamilyNames = restoreInheritance ? null : style.FallbackFamilyNames },
                LyricStyleProperty.FontSizePx => overlay with { FontSizePx = restoreInheritance ? null : style.FontSizePx },
                LyricStyleProperty.FontWeight => overlay with { FontWeight = restoreInheritance ? null : style.FontWeight },
                LyricStyleProperty.IsBold => overlay with { IsBold = restoreInheritance ? null : style.IsBold },
                LyricStyleProperty.IsItalic => overlay with { IsItalic = restoreInheritance ? null : style.IsItalic },
                LyricStyleProperty.LetterSpacingPx => overlay with { LetterSpacingPx = restoreInheritance ? null : style.LetterSpacingPx },
                LyricStyleProperty.VerticalCenterRatio => overlay with { VerticalCenterRatio = restoreInheritance ? null : style.VerticalCenterRatio },
                LyricStyleProperty.HorizontalAlignment => overlay with { HorizontalAlignment = restoreInheritance ? null : style.HorizontalAlignment },
                LyricStyleProperty.UnsungColor => overlay with { UnsungFill = restoreInheritance ? default : OptionalValue.Set<FillSpecOverride?>(new() { Material = style.UnsungFill.Material }) },
                LyricStyleProperty.SungColor => overlay with { SungFill = restoreInheritance ? default : OptionalValue.Set<FillSpecOverride?>(new() { Material = style.SungFill.Material }) },
                LyricStyleProperty.UnsungGradientSpec => overlay with { UnsungFill = restoreInheritance ? default : OptionalValue.Set<FillSpecOverride?>(new() { Material = style.UnsungFill.Material }) },
                LyricStyleProperty.SungGradientSpec => overlay with { SungFill = restoreInheritance ? default : OptionalValue.Set<FillSpecOverride?>(new() { Material = style.SungFill.Material }) },
                LyricStyleProperty.StrokeColor => overlay with { Stroke = OptionalValue.Set<StrokeSpecOverride?>((overlay.Stroke.Value ?? new StrokeSpecOverride()) with { Material = restoreInheritance ? default : OptionalValue.Set<MaterialSpec?>(style.Stroke?.Material ?? new MaterialSpec.Solid(RgbaColor.Transparent)) }) },
                LyricStyleProperty.StrokeWidthPx => overlay with { StrokeWidthPx = restoreInheritance ? null : style.StrokeWidthPx },
                LyricStyleProperty.StrokeSpec => overlay with { StrokeSpec = restoreInheritance ? default(OptionalValue<StrokeSpec?>) : OptionalValue.Set<StrokeSpec?>(LyricStyleEffects.GetStroke(style)) },
                LyricStyleProperty.ShadowColor => overlay with { ShadowColor = restoreInheritance ? default(OptionalValue<RgbaColor?>) : OptionalValue.Set<RgbaColor?>(style.ShadowColor) },
                LyricStyleProperty.ShadowBlurPx => overlay with { ShadowBlurPx = restoreInheritance ? null : style.ShadowBlurPx },
                LyricStyleProperty.ShadowSpec => overlay with { ShadowSpec = restoreInheritance ? default(OptionalValue<ShadowSpec?>) : OptionalValue.Set<ShadowSpec?>(style.ShadowSpec) },
                LyricStyleProperty.Opacity => overlay with { Opacity = restoreInheritance ? null : style.Opacity },
                LyricStyleProperty.Scale => overlay with { Scale = restoreInheritance ? null : style.Scale },
                LyricStyleProperty.GlowSpec => overlay with { Glow = restoreInheritance ? default : OptionalValue.Set<GlowSpecOverride?>(style.Glow is { } glow ? GlowSpecOverride.FromSpec(glow) : null) },
                LyricStyleProperty.GlowColor or LyricStyleProperty.GlowOpacity or LyricStyleProperty.GlowBlurRadiusPx or LyricStyleProperty.GlowSpreadPx => CopyGlowLeaf(overlay, style, property, restoreInheritance),
                _ => throw new ArgumentOutOfRangeException(nameof(properties)),
            };
        }
        return overlay;
    }

    private static LyricStyleOverride CopyGlowLeaf(LyricStyleOverride overlay, LyricStyle style, LyricStyleProperty property, bool restore)
    {
        if (overlay.Glow.Value is null && (restore || overlay.Glow.IsSet && style.Glow is null)) return overlay;
        GlowSpecOverride value = overlay.Glow.Value ?? new();
        GlowSpec basis = style.Glow ?? new() { Color = RgbaColor.Transparent, BlurRadiusPx = 0, SpreadPx = 0 };
        value = property switch
        {
            LyricStyleProperty.GlowColor => value with { Color = restore ? null : basis.Color },
            LyricStyleProperty.GlowOpacity => value with { Opacity = restore ? null : basis.Opacity },
            LyricStyleProperty.GlowBlurRadiusPx => value with { BlurRadiusPx = restore ? null : basis.BlurRadiusPx },
            LyricStyleProperty.GlowSpreadPx => value with { SpreadPx = restore ? null : basis.SpreadPx },
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };
        return overlay with { Glow = value.IsEmpty ? default : OptionalValue.Set<GlowSpecOverride?>(value) };
    }

    /// <summary>检查非有限数值；领域解析器另外负责样式范围校验。</summary>
    public static void ValidateFinite(LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!float.IsFinite(style.FontSizePx) || !float.IsFinite(style.LetterSpacingPx)
            || style.FontWeight is < 100 or > 900 || !double.IsFinite(style.VerticalCenterRatio)
            || !float.IsFinite(style.StrokeWidthPx) || !float.IsFinite(style.ShadowBlurPx)
            || !float.IsFinite(style.Opacity) || !float.IsFinite(style.Scale)
            || style.UnsungFill is not { IsValid: true } || style.SungFill is not { IsValid: true }
            || (style.StrokeSpec is { IsValid: false })
            || (style.ShadowSpec is { IsValid: false }) || (style.Glow is { IsValid: false }))
            throw new ArgumentOutOfRangeException(nameof(style), "样式数值必须是有限数。");
    }

    /// <summary>校验完整动画参数；空动画合法。</summary>
    public static void ValidateAnimation(LineAnimation? animation)
    {
        if (animation is null) return;
        if (animation.DurationUs is < 0 or > 10_000_000 || !Enum.IsDefined(animation.Easing)
            || !float.IsFinite(animation.StartOpacity) || animation.StartOpacity is < 0 or > 1
            || !float.IsFinite(animation.StartScale) || animation.StartScale is <= 0 or > 4
            || !float.IsFinite(animation.StartOffsetPx) || Math.Abs(animation.StartOffsetPx) > 10_000)
            throw new ArgumentOutOfRangeException(nameof(animation), "进入动画参数超出范围。");
    }
}
