using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>
/// 元素级支持范围的"覆盖面"闸门（roadmap P1「元素级覆盖的界面作用范围」+ ADR-0043）。
/// </summary>
/// <remarks>
/// <para>
/// 这一套测试盯住的是<b>"属性多了会有人发现"</b>：
/// 每一种属性都有"只设这一项"的覆盖，遍历 <see cref="LyricStylePropertyExtensions.All"/> 检查
/// 它<em>能不能被看见</em>（<c>SetProperties</c> / <c>IsEmpty</c>）。
/// 新加一个样式属性却忘了在 <c>TokenStyleSupport.SetProperties</c> 里补一行时，
/// 这里会当场变红——而不是等到用户发现"界面上允许改、画面里没反应"。
/// </para>
/// <para>
/// 同理，渲染要不要为元素单独重画，也只由这一份事实派生（<c>HasDrawableOverride</c>），
/// 因此不可能出现"支持范围放开了、渲染那份清单没跟上"的组合。
/// </para>
/// </remarks>
public sealed class TokenStyleSupportTests
{
    /// <summary>每个属性一份"只设这一项"的覆盖；属性的覆盖面由测试自己检查。</summary>
    private static readonly Dictionary<LyricStyleProperty, LyricStyleOverride> Single = new()
    {
        [LyricStyleProperty.FontFamilyName] = new() { FontFamilyName = OptionalValue.Set<string?>("Noto Sans CJK SC") },
        [LyricStyleProperty.FallbackFamilyNames] = new() { FallbackFamilyNames = ["Noto Color Emoji"] },
        [LyricStyleProperty.FontSizePx] = new() { FontSizePx = 40f },
        [LyricStyleProperty.FontWeight] = new() { FontWeight = 700 },
        [LyricStyleProperty.IsBold] = new() { IsBold = true },
        [LyricStyleProperty.IsItalic] = new() { IsItalic = true },
        [LyricStyleProperty.LetterSpacingPx] = new() { LetterSpacingPx = 4f },
        [LyricStyleProperty.RubyRelativeScale] = new() { Ruby = new() { RelativeScale = OptionalValue.Set<double?>(.7) } },
        [LyricStyleProperty.RubyGapPx] = new() { Ruby = new() { GapPx = OptionalValue.Set<double?>(3) } },
        [LyricStyleProperty.VerticalCenterRatio] = new() { VerticalCenterRatio = 0.5d },
        [LyricStyleProperty.HorizontalAlignment] = new() { HorizontalAlignment = LyricHorizontalAlignment.Center },
        [LyricStyleProperty.UnsungFill] = new() { UnsungColor = new RgbaColor(0x11, 0x22, 0x33) },
        [LyricStyleProperty.SungFill] = new() { SungFill = new FillSpecOverride { Material = new MaterialSpec.Solid(RgbaColor.White) } },
        [LyricStyleProperty.UnsungStrokes] = new() { UnsungStrokes = StrokeLayers.FromStroke(StrokeSpec.Create(RgbaColor.White, 3f)) },
        [LyricStyleProperty.SungStrokes] = new() { Sung = new() { Strokes = OptionalValue.Set(Followable.Set(StrokeLayers.Empty)) } },
        [LyricStyleProperty.UnsungShadow] = new() { Shadow = OptionalValue.Set<ShadowSpecOverride?>(null) },
        [LyricStyleProperty.UnsungShadowColor] = Shadow(new() { Color = new RgbaColor(0x88, 0x99, 0xAA) }),
        [LyricStyleProperty.UnsungShadowOpacity] = Shadow(new() { Opacity = .5f }),
        [LyricStyleProperty.UnsungShadowAngleDegrees] = Shadow(new() { AngleDegrees = 30d }),
        [LyricStyleProperty.UnsungShadowDistancePx] = Shadow(new() { DistancePx = 3f }),
        [LyricStyleProperty.UnsungShadowBlurRadiusPx] = Shadow(new() { BlurRadiusPx = 4f }),
        [LyricStyleProperty.SungShadow] = new() { Sung = new() { Shadow = OptionalValue.Set(Followable.Follow<ShadowSpecOverride?>()) } },
        [LyricStyleProperty.SungShadowColor] = SungShadow(new() { Color = new RgbaColor(0x88, 0x99, 0xAA) }),
        [LyricStyleProperty.SungShadowOpacity] = SungShadow(new() { Opacity = .5f }),
        [LyricStyleProperty.SungShadowAngleDegrees] = SungShadow(new() { AngleDegrees = 30d }),
        [LyricStyleProperty.SungShadowDistancePx] = SungShadow(new() { DistancePx = 3f }),
        [LyricStyleProperty.SungShadowBlurRadiusPx] = SungShadow(new() { BlurRadiusPx = 4f }),
        [LyricStyleProperty.UnsungGlow] = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(GlowSpecOverride.FromSpec(new())) },
        [LyricStyleProperty.UnsungGlowColor] = Glow(new() { Color = RgbaColor.White }),
        [LyricStyleProperty.UnsungGlowOpacity] = Glow(new() { Opacity = .5f }),
        [LyricStyleProperty.UnsungGlowBlurRadiusPx] = Glow(new() { BlurRadiusPx = 3f }),
        [LyricStyleProperty.UnsungGlowSpreadPx] = Glow(new() { SpreadPx = 2f }),
        [LyricStyleProperty.SungGlow] = new() { Sung = new() { Glow = OptionalValue.Set(Followable.Set<GlowSpecOverride?>(null)) } },
        [LyricStyleProperty.SungGlowColor] = SungGlow(new() { Color = RgbaColor.White }),
        [LyricStyleProperty.SungGlowOpacity] = SungGlow(new() { Opacity = .5f }),
        [LyricStyleProperty.SungGlowBlurRadiusPx] = SungGlow(new() { BlurRadiusPx = 3f }),
        [LyricStyleProperty.SungGlowSpreadPx] = SungGlow(new() { SpreadPx = 2f }),
        [LyricStyleProperty.WipeFeatherRatio] = new() { Wipe = new() { FeatherRatio = .5d } },
        [LyricStyleProperty.WipeHighlight] = new() { Wipe = new() { Highlight = OptionalValue.Set<WipeHighlightOverride?>(null) } },
        [LyricStyleProperty.WipeHighlightColor] = Highlight(new() { Color = RgbaColor.White }),
        [LyricStyleProperty.WipeHighlightWidthRatio] = Highlight(new() { WidthRatio = .3d }),
        [LyricStyleProperty.WipeHighlightOpacity] = Highlight(new() { Opacity = .5f }),
        [LyricStyleProperty.WipeHighlightBlend] = Highlight(new() { Blend = WipeHighlightBlend.Screen }),
        [LyricStyleProperty.Opacity] = new() { Opacity = 0.5f },
        [LyricStyleProperty.Scale] = new() { Scale = 1.5f },
    };

    private static readonly IReadOnlyList<LyricStyleProperty>[] EffectGroups =
    [
        LyricStylePropertyExtensions.UnsungShadowProperties, LyricStylePropertyExtensions.SungShadowProperties,
        LyricStylePropertyExtensions.UnsungGlowProperties, LyricStylePropertyExtensions.SungGlowProperties,
        LyricStylePropertyExtensions.WipeHighlightProperties,
    ];

    private static LyricStyleOverride Shadow(ShadowSpecOverride value) => new() { Shadow = OptionalValue.Set<ShadowSpecOverride?>(value) };

    private static LyricStyleOverride SungShadow(ShadowSpecOverride value) => new() { Sung = new() { Shadow = OptionalValue.Set(Followable.Set<ShadowSpecOverride?>(value)) } };

    private static LyricStyleOverride Glow(GlowSpecOverride value) => new() { Glow = OptionalValue.Set<GlowSpecOverride?>(value) };

    private static LyricStyleOverride SungGlow(GlowSpecOverride value) => new() { Sung = new() { Glow = OptionalValue.Set(Followable.Set<GlowSpecOverride?>(value)) } };

    private static LyricStyleOverride Highlight(WipeHighlightOverride value) => new() { Wipe = new() { Highlight = OptionalValue.Set<WipeHighlightOverride?>(value) } };

    [Fact]
    public void 每种属性都有测试样本且覆盖全部属性()
    {
        // 加属性却忘了在这里补样本 ⇒ 这条先红，逼着人来分类。
        Assert.Equal(LyricStylePropertyExtensions.All.Count, Single.Count);

        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            Assert.True(Single.ContainsKey(property), $"缺少 {property} 的样本。");
        }
    }

    [Fact]
    public void 每种属性都能被这一层看见()
    {
        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            IReadOnlyList<LyricStyleProperty> set = TokenStyleSupport.SetProperties(Single[property]);

            // 整体项（关闭、跟随或完整替换）同时记到全部分量；其余属性只记自己。
            IReadOnlyList<LyricStyleProperty> expected = EffectGroups.FirstOrDefault(group => group[0] == property) ?? [property];
            Assert.Equal(expected, set);
        }
    }

    [Fact]
    public void 每种属性都会让覆盖不再为空()
    {
        // 盯住 IsEmpty：模型加了字段却忘了同步它，会出现"界面上设了、却被当成空覆盖丢掉"。
        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            Assert.False(Single[property].IsEmpty, $"{property} 被当成空覆盖了。");
        }
    }

    [Fact]
    public void 支持与不支持互斥且合起来正好是全部属性()
    {
        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            Assert.Equal(
                TokenStyleSupport.IsSupported(property),
                !TokenStyleSupport.UnsupportedAtTokenLevel.Contains(property));
        }

        Assert.Equal(
            LyricStylePropertyExtensions.All.Count,
            TokenStyleSupport.SupportedAtTokenLevel.Count + TokenStyleSupport.UnsupportedAtTokenLevel.Count);
    }

    [Fact]
    public void 渲染是否重画与支持范围完全一致()
    {
        // 这是最关键的一条：渲染侧不再自己列清单，因此"支持却画不出来"在结构上不可能。
        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            LyricStyleOverride overlay = Single[property];

            Assert.Equal(TokenStyleSupport.IsSupported(property), TokenStyleSupport.HasDrawableOverride(overlay));
            Assert.Equal(
                TokenStyleSupport.IsSupported(property),
                TokenStyleSupport.UnsupportedSet(overlay).Count == 0);
        }
    }

    [Fact]
    public void 空覆盖与缺失覆盖都不触发重画()
    {
        Assert.False(TokenStyleSupport.HasDrawableOverride(null));
        Assert.False(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride()));
        Assert.Empty(TokenStyleSupport.SetProperties(null));
        Assert.Empty(TokenStyleSupport.SetProperties(new LyricStyleOverride()));
        Assert.Empty(TokenStyleSupport.UnsupportedSet(null));
    }

    [Fact]
    public void 只能改整行排版属性的覆盖在元素级被拒()
    {
        LyricStyleOverride overlay = new() { FontSizePx = 40f, Scale = 1.5f };

        Assert.Equal(
            [LyricStyleProperty.FontSizePx, LyricStyleProperty.Scale],
            TokenStyleSupport.UnsupportedSet(overlay));
        Assert.False(TokenStyleSupport.HasDrawableOverride(overlay));
    }

    [Fact]
    public void 颜色族与描边阴影都算会改变画面()
    {
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { SungColor = new RgbaColor(1, 2, 3) }));
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { UnsungStrokes = StrokeLayers.FromStroke(StrokeSpec.Create(RgbaColor.White, 2f)) }));
        Assert.True(TokenStyleSupport.HasDrawableOverride(Shadow(new() { BlurRadiusPx = 2f })));
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { Wipe = new() { FeatherRatio = .2d } }));
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { Opacity = 0.5f }));
    }
}
