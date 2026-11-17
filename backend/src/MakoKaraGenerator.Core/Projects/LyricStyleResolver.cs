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
        }

        if (overlay.Ruby is { } ruby)
            result = result with { Ruby = current.Ruby with
            {
                RelativeScale = ruby.RelativeScale.IsSet ? ruby.RelativeScale.Value : current.Ruby.RelativeScale,
                GapPx = ruby.GapPx.IsSet ? ruby.GapPx.Value : current.Ruby.GapPx,
            } };

        result = result with
        {
            VerticalCenterRatio = overlay.VerticalCenterRatio ?? current.VerticalCenterRatio,
            HorizontalAlignment = overlay.HorizontalAlignment ?? current.HorizontalAlignment,
            Opacity = overlay.Opacity ?? current.Opacity,
            Scale = overlay.Scale ?? current.Scale,
        };

        // 先叠未唱，再叠已唱：已唱的分量覆盖若落在「跟随」上，以同一层叠完的未唱值为基线（设计 §10.5）。
        if (overlay.Unsung is { } unsung) result = result with { Unsung = unsung.Apply(result.Unsung) };
        if (overlay.Sung is { } sung) result = result with { Sung = sung.Apply(result.Sung, result.Unsung) };
        if (overlay.Wipe is { } wipe) result = result with { Wipe = wipe.Apply(result.Wipe) };

        MarkSources(overlay, source, sources);
        return result;
    }

    /// <summary>列出一份覆盖<b>写了</b>的属性，判定与来源追踪完全相同。</summary>
    /// <param name="overlay">样式覆盖。</param>
    /// <returns>写了的属性，按 <see cref="LyricStylePropertyExtensions.All"/> 的顺序。</returns>
    internal static IReadOnlyList<LyricStyleProperty> SetProperties(LyricStyleOverride? overlay)
    {
        if (overlay is null) return [];
        Dictionary<LyricStyleProperty, LyricStyleSource> marked = [];
        MarkSources(overlay, LyricStyleSource.Line, marked);
        return [.. LyricStylePropertyExtensions.All.Where(marked.ContainsKey)];
    }

    private static void MarkSources(LyricStyleOverride overlay, LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (overlay.Typography is { } typography) MarkTypographySources(typography, source, sources);
        if (overlay.Ruby is { } ruby)
        {
            if (ruby.RelativeScale.IsSet) sources[LyricStyleProperty.RubyRelativeScale] = source;
            if (ruby.GapPx.IsSet) sources[LyricStyleProperty.RubyGapPx] = source;
        }
        if (overlay.Unsung is { } unsung) MarkUnsungSources(unsung, source, sources);
        if (overlay.Sung is { } sung) MarkSungSources(sung, source, sources);
        if (overlay.Wipe is { } wipe)
        {
            if (wipe.FeatherRatio is not null) sources[LyricStyleProperty.WipeFeatherRatio] = source;
            if (wipe.Highlight.IsSet)
                MarkEffectSources(LyricStylePropertyExtensions.WipeHighlightProperties, wipe.Highlight.Value is not { } highlight || highlight.IsComplete,
                    component => wipe.Highlight.Value is { } value && component switch
                    {
                        LyricStyleEffectComponent.Color => value.Color is not null,
                        LyricStyleEffectComponent.WidthRatio => value.WidthRatio is not null,
                        LyricStyleEffectComponent.Opacity => value.Opacity is not null,
                        LyricStyleEffectComponent.Blend => value.Blend is not null,
                        _ => false,
                    }, source, sources);
        }

        if (overlay.VerticalCenterRatio is not null)
            sources[LyricStyleProperty.VerticalCenterRatio] = source;
        if (overlay.HorizontalAlignment is not null)
            sources[LyricStyleProperty.HorizontalAlignment] = source;
        if (overlay.Opacity is not null)
            sources[LyricStyleProperty.Opacity] = source;
        if (overlay.Scale is not null)
            sources[LyricStyleProperty.Scale] = source;
    }

    private static void MarkUnsungSources(TextAppearanceOverride unsung, LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (unsung.Fill.Value is { Material.IsSet: true }) sources[LyricStyleProperty.UnsungFill] = source;
        if (unsung.Strokes is not null) sources[LyricStyleProperty.UnsungStrokes] = source;
        if (unsung.Shadow.IsSet)
            MarkShadowSources(LyricStylePropertyExtensions.UnsungShadowProperties, unsung.Shadow.Value, follows: false, source, sources);
        if (unsung.Glow.IsSet)
            MarkGlowSources(LyricStylePropertyExtensions.UnsungGlowProperties, unsung.Glow.Value, follows: false, source, sources);
    }

    private static void MarkSungSources(SungAppearanceOverride sung, LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        if (sung.Fill.Value is { Material.IsSet: true }) sources[LyricStyleProperty.SungFill] = source;
        if (sung.Strokes.IsSet) sources[LyricStyleProperty.SungStrokes] = source;
        if (sung.Shadow.IsSet)
            MarkShadowSources(LyricStylePropertyExtensions.SungShadowProperties, sung.Shadow.Value.Value, sung.Shadow.Value.FollowsUnsung, source, sources);
        if (sung.Glow.IsSet)
            MarkGlowSources(LyricStylePropertyExtensions.SungGlowProperties, sung.Glow.Value.Value, sung.Glow.Value.FollowsUnsung, source, sources);
    }

    private static void MarkShadowSources(IReadOnlyList<LyricStyleProperty> properties, ShadowSpecOverride? shadow, bool follows,
        LyricStyleSource source, Dictionary<LyricStyleProperty, LyricStyleSource> sources)
        => MarkEffectSources(properties, follows || shadow is null || shadow.IsComplete, component => shadow is not null && component switch
        {
            LyricStyleEffectComponent.Color => shadow.Color is not null,
            LyricStyleEffectComponent.Opacity => shadow.Opacity is not null,
            LyricStyleEffectComponent.AngleDegrees => shadow.AngleDegrees is not null,
            LyricStyleEffectComponent.DistancePx => shadow.DistancePx is not null,
            LyricStyleEffectComponent.BlurRadiusPx => shadow.BlurRadiusPx is not null,
            _ => false,
        }, source, sources);

    private static void MarkGlowSources(IReadOnlyList<LyricStyleProperty> properties, GlowSpecOverride? glow, bool follows,
        LyricStyleSource source, Dictionary<LyricStyleProperty, LyricStyleSource> sources)
        => MarkEffectSources(properties, follows || glow is null || glow.IsComplete, component => glow is not null && component switch
        {
            LyricStyleEffectComponent.Color => glow.Color is not null,
            LyricStyleEffectComponent.Opacity => glow.Opacity is not null,
            LyricStyleEffectComponent.BlurRadiusPx => glow.BlurRadiusPx is not null,
            LyricStyleEffectComponent.SpreadPx => glow.SpreadPx is not null,
            _ => false,
        }, source, sources);

    /// <summary>关闭、跟随与完整替换记到整体项与全部分量；部分覆盖只记写了的分量。</summary>
    private static void MarkEffectSources(IReadOnlyList<LyricStyleProperty> properties, bool whole,
        Func<LyricStyleEffectComponent, bool> componentSet, LyricStyleSource source,
        Dictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        foreach (LyricStyleProperty property in properties)
        {
            LyricStyleEffectComponent component = LyricStylePropertyExtensions.Component(property);
            if (whole || component != LyricStyleEffectComponent.None && componentSet(component)) sources[property] = source;
        }
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

        if (!style.Unsung.Strokes.IsValid || !style.Sung.Strokes.FollowsUnsung && style.Sung.Strokes.Value is not { IsValid: true })
        {
            throw new ArgumentException($"{layerName}产生了无效的描边列表（至多 {StrokeLayers.MaxCount} 层、宽度为正、包材质只用于最内层）。", parameterName);
        }

        if (style.Opacity is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName, style.Opacity, "不透明度必须在 0 到 1 之间。");
        }

        if (style.Scale <= 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, style.Scale, "缩放必须为正数。");
        }

        if (!style.Wipe.IsValid)
        {
            throw new ArgumentException($"{layerName}产生了无效的扫字样式。", parameterName);
        }

        if (!style.Typography.IsValid || (style.Ruby is not { IsValid: true } || !style.Ruby.IsValidForFont(style.FontSizePx)) || !style.Unsung.IsValid || !style.Sung.IsValid)
        {
            throw new ArgumentException($"{layerName}产生了无效的规范样式对象。", parameterName);
        }
    }
}
