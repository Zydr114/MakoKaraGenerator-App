namespace MakoKaraGenerator.Core.Projects;

/// <summary>按固定优先级合并歌词样式。</summary>
/// <remarks>
/// <para>
/// 优先级：默认样式 → 命名预设 → 分支覆盖 → 歌词行覆盖 → 文本元素覆盖。
/// 预设提供的是<b>一整份基线</b>（它替换默认样式的全部属性），
/// 行与元素的覆盖随后在其上逐项生效；因此"预设 + 少量覆盖"是最常见的用法。
/// </para>
/// <para>
/// 每一层应用后都会立刻校验，并把出错的那一层写进异常的参数名，
/// 否则"预设里字号写错"会被报成"行覆盖有问题"，排查方向直接跑偏。
/// </para>
/// </remarks>
public static class LyricStyleResolver
{
    /// <summary>
    /// 按默认值、预设、分支覆盖、行覆盖、元素覆盖的顺序解析样式。
    /// </summary>
    /// <param name="defaultStyle">默认样式；为 <see langword="null"/> 时使用 <see cref="LyricStyle.Default"/>。</param>
    /// <param name="preset">可选预设。</param>
    /// <param name="lineOverride">可选行覆盖。</param>
    /// <param name="tokenOverride">可选元素覆盖。</param>
    /// <param name="branchOverride">可选分支覆盖，先于行覆盖生效。</param>
    /// <returns>最终样式和每个属性的来源。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任何一层产生了无效的字号或纵向位置时抛出。</exception>
    public static LyricStyleResolution Resolve(
        LyricStyle? defaultStyle = null,
        LyricStylePreset? preset = null,
        LyricStyleOverride? lineOverride = null,
        LyricStyleOverride? tokenOverride = null,
        LyricStyleOverride? branchOverride = null)
    {
        LyricStyle style = defaultStyle ?? LyricStyle.Default;

        Dictionary<LyricStyleProperty, LyricStyleSource> sources = [];

        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            sources[property] = LyricStyleSource.Default;
        }

        EnsureValid(style, nameof(defaultStyle), "默认样式");

        if (preset is not null)
        {
            style = preset.Style;

            foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
            {
                sources[property] = LyricStyleSource.Preset;
            }

            EnsureValid(style, nameof(preset), $"预设 '{preset.Name}'");
        }

        style = Apply(style, branchOverride, LyricStyleSource.Branch, sources);
        EnsureValid(style, nameof(branchOverride), "分支覆盖");

        style = Apply(style, lineOverride, LyricStyleSource.Line, sources);
        EnsureValid(style, nameof(lineOverride), "行覆盖");

        style = Apply(style, tokenOverride, LyricStyleSource.Token, sources);
        EnsureValid(style, nameof(tokenOverride), "元素覆盖");

        return new LyricStyleResolution(style, sources);
    }

    private static LyricStyle Apply(
        LyricStyle current,
        LyricStyleOverride? overlay,
        LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (overlay is null || overlay.IsEmpty)
        {
            return current;
        }

        LyricStyle result = current;

        if (overlay.Typography is { } typography)
        {
            result = result with
            {
                Typography = current.Typography with
                {
                    FontFamilyName = typography.FontFamilyName.IsSet ? typography.FontFamilyName.Value : current.Typography.FontFamilyName,
                    FallbackFamilyNames = typography.FallbackFamilyNames ?? current.Typography.FallbackFamilyNames,
                    FontSizePx = typography.FontSizePx ?? current.Typography.FontSizePx,
                    FontWeight = typography.FontWeight ?? current.Typography.FontWeight,
                    IsBold = typography.IsBold ?? current.Typography.IsBold,
                    IsItalic = typography.IsItalic ?? current.Typography.IsItalic,
                    LetterSpacingPx = typography.LetterSpacingPx ?? current.Typography.LetterSpacingPx,
                },
            };
            MarkTypographySources(typography, source, sources);
        }

        result = result with
        {
            VerticalCenterRatio = overlay.VerticalCenterRatio ?? current.VerticalCenterRatio,
            HorizontalAlignment = overlay.HorizontalAlignment ?? current.HorizontalAlignment,
            Opacity = overlay.Opacity ?? current.Opacity,
            Scale = overlay.Scale ?? current.Scale,
        };

        if (overlay.UnsungFill.IsSet)
        {
            result = SetFill(result, overlay.UnsungFill.Value
                ?? throw new ArgumentException("未唱填充覆盖不能设置为空。", nameof(overlay)), sung: false);
            MarkFillSources(overlay.UnsungFill.Value, sung: false, source, sources);
        }

        if (overlay.SungFill.IsSet)
        {
            result = SetFill(result, overlay.SungFill.Value
                ?? throw new ArgumentException("已唱填充覆盖不能设置为空。", nameof(overlay)), sung: true);
            MarkFillSources(overlay.SungFill.Value, sung: true, source, sources);
        }

        if (overlay.Stroke.IsSet)
        {
            StrokeSpec? stroke = overlay.Stroke.Value?.Apply(current.Stroke);
            result = result with { Stroke = stroke };
            MarkStrokeSources(overlay.Stroke.Value, source, sources);
        }

        if (overlay.Shadow.IsSet)
        {
            ShadowSpec? shadow = overlay.Shadow.Value?.Apply(current.Shadow);
            result = result with { Shadow = shadow };
            MarkShadowSources(overlay.Shadow.Value, source, sources);
        }

        if (overlay.VerticalCenterRatio is not null)
            sources[LyricStyleProperty.VerticalCenterRatio] = source;
        if (overlay.HorizontalAlignment is not null)
            sources[LyricStyleProperty.HorizontalAlignment] = source;
        if (overlay.Opacity is not null)
            sources[LyricStyleProperty.Opacity] = source;
        if (overlay.Scale is not null)
            sources[LyricStyleProperty.Scale] = source;

        return result;
    }

    private static void MarkTypographySources(
        TypographySpecOverride typography,
        LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (typography.FontFamilyName.IsSet) sources[LyricStyleProperty.FontFamilyName] = source;
        if (typography.FallbackFamilyNames is not null) sources[LyricStyleProperty.FallbackFamilyNames] = source;
        if (typography.FontSizePx is not null) sources[LyricStyleProperty.FontSizePx] = source;
        if (typography.FontWeight is not null) sources[LyricStyleProperty.FontWeight] = source;
        if (typography.IsBold is not null) sources[LyricStyleProperty.IsBold] = source;
        if (typography.IsItalic is not null) sources[LyricStyleProperty.IsItalic] = source;
        if (typography.LetterSpacingPx is not null) sources[LyricStyleProperty.LetterSpacingPx] = source;
    }

    private static void MarkFillSources(
        FillSpecOverride? fill,
        bool sung,
        LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (fill is null) return;
        LyricStyleProperty color = sung ? LyricStyleProperty.SungColor : LyricStyleProperty.UnsungColor;
        LyricStyleProperty gradient = sung ? LyricStyleProperty.SungGradientSpec : LyricStyleProperty.UnsungGradientSpec;
        if (fill.Material.IsSet) { sources[color] = source; sources[gradient] = source; }
    }

    private static void MarkStrokeSources(
        StrokeSpecOverride? stroke,
        LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (stroke is null)
        {
            sources[LyricStyleProperty.StrokeSpec] = source;
            return;
        }
        if (stroke.Material.IsSet) sources[LyricStyleProperty.StrokeColor] = source;
        if (stroke.WidthPx is not null) sources[LyricStyleProperty.StrokeWidthPx] = source;
        if (stroke.IsComplete || stroke.Join is not null || stroke.Position is not null)
            sources[LyricStyleProperty.StrokeSpec] = source;
    }

    private static void MarkShadowSources(
        ShadowSpecOverride? shadow,
        LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (shadow is null)
        {
            sources[LyricStyleProperty.ShadowSpec] = source;
            return;
        }
        if (shadow.Color is not null) sources[LyricStyleProperty.ShadowColor] = source;
        if (shadow.AngleDegrees is not null || shadow.DistancePx is not null)
        {
            sources[LyricStyleProperty.ShadowSpec] = source;
        }
        if (shadow.BlurRadiusPx is not null) sources[LyricStyleProperty.ShadowBlurPx] = source;
        if (shadow.IsComplete) sources[LyricStyleProperty.ShadowSpec] = source;
    }

    private static LyricStyle SetFill(LyricStyle style, FillSpecOverride overlay, bool sung)
    {
        if (!overlay.Material.IsSet) return style;
        var fill = new FillSpec { Material = overlay.Material.Value ?? throw new ArgumentException("填充材质不能null。") };
        return sung ? style with { SungFill = fill } : style with { UnsungFill = fill };
    }

    private static void EnsureValid(LyricStyle style, string parameterName, string layerName)
    {
        if (!float.IsFinite(style.FontSizePx) || style.FontSizePx <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                style.FontSizePx,
                $"{layerName}产生了非正的字号。");
        }

        if (style.FontWeight is < 100 or > 900 || !float.IsFinite(style.LetterSpacingPx))
        {
            throw new ArgumentOutOfRangeException(parameterName, "字重必须在 100 到 900 之间，字距必须是有限数。");
        }

        if (!Enum.IsDefined(style.HorizontalAlignment))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                style.HorizontalAlignment,
                "水平对齐方式不是已知取值。");
        }

        if (style.VerticalCenterRatio is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                style.VerticalCenterRatio,
                $"{layerName}产生的纵向中心比例不在 0 到 1 之间。");
        }

        if (style.StrokeWidthPx < 0f || style.ShadowBlurPx < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, "描边宽度与阴影模糊半径不能为负数。");
        }

        if (style.StrokeSpec is { IsValid: false })
        {
            throw new ArgumentException($"{layerName}产生了无效的描边描述。", parameterName);
        }

        if (style.ShadowSpec is { IsValid: false })
        {
            throw new ArgumentException($"{layerName}产生了无效的阴影描述。", parameterName);
        }

        if (style.Opacity is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName, style.Opacity, "不透明度必须在 0 到 1 之间。");
        }

        if (style.Scale <= 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, style.Scale, "缩放必须为正数。");
        }

        if (style.UnsungGradientSpec is { IsValid: false })
        {
            throw new ArgumentException(
                $"{layerName}产生了无效的未唱渐变描述。",
                parameterName);
        }

        if (style.SungGradientSpec is { IsValid: false })
        {
            throw new ArgumentException(
                $"{layerName}产生了无效的已唱渐变描述。",
                parameterName);
        }

        if (!style.Typography.IsValid || !style.UnsungFill.IsValid || !style.SungFill.IsValid
            || style.Stroke is { IsValid: false } || style.Shadow is { IsValid: false })
        {
            throw new ArgumentException($"{layerName}产生了无效的规范样式对象。", parameterName);
        }
    }
}
