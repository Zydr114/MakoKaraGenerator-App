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
        [LyricStyleProperty.VerticalCenterRatio] = new() { VerticalCenterRatio = 0.5d },
        [LyricStyleProperty.HorizontalAlignment] = new() { HorizontalAlignment = LyricHorizontalAlignment.Center },
        [LyricStyleProperty.UnsungColor] = new() { UnsungColor = new RgbaColor(0x11, 0x22, 0x33) },
        [LyricStyleProperty.SungColor] = new() { SungColor = new RgbaColor(0x44, 0x55, 0x66) },
        [LyricStyleProperty.UnsungGradientSpec] = new() { UnsungGradientSpec = GradientSpec.Create(
            [new GradientStop(0d, new RgbaColor(0x11, 0x22, 0x33)),
                new GradientStop(1d, new RgbaColor(0x44, 0x55, 0x66))]) },
        [LyricStyleProperty.SungGradientSpec] = new() { SungFill = new FillSpecOverride { Material = new MaterialSpec.Solid(RgbaColor.White) } },
        [LyricStyleProperty.StrokeColor] = new() { StrokeColor = new RgbaColor(0x55, 0x66, 0x77) },
        [LyricStyleProperty.StrokeWidthPx] = new() { StrokeWidthPx = 3f },
        [LyricStyleProperty.StrokeSpec] = new() { StrokeSpec = StrokeSpec.Create(RgbaColor.White, 3f) },
        [LyricStyleProperty.ShadowColor] = new() { ShadowColor = new RgbaColor(0x88, 0x99, 0xAA) },
        [LyricStyleProperty.ShadowBlurPx] = new() { ShadowBlurPx = 4f },
        [LyricStyleProperty.ShadowSpec] = new() { ShadowSpec = ShadowSpec.Create(RgbaColor.Black) },
        [LyricStyleProperty.Opacity] = new() { Opacity = 0.5f },
        [LyricStyleProperty.Scale] = new() { Scale = 1.5f },
        [LyricStyleProperty.GlowSpec] = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(GlowSpecOverride.FromSpec(new())) },
        [LyricStyleProperty.GlowColor] = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Color = RgbaColor.White }) },
        [LyricStyleProperty.GlowOpacity] = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Opacity = .5f }) },
        [LyricStyleProperty.GlowBlurRadiusPx] = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { BlurRadiusPx = 3f }) },
        [LyricStyleProperty.GlowSpreadPx] = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { SpreadPx = 2f }) },
    };

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

            var expected = property is LyricStyleProperty.UnsungColor or LyricStyleProperty.UnsungGradientSpec
                ? new[] { LyricStyleProperty.UnsungColor, LyricStyleProperty.UnsungGradientSpec }
                : property is LyricStyleProperty.SungColor or LyricStyleProperty.SungGradientSpec
                    ? new[] { LyricStyleProperty.SungColor, LyricStyleProperty.SungGradientSpec } : property == LyricStyleProperty.GlowSpec
                        ? new[] { LyricStyleProperty.GlowSpec, LyricStyleProperty.GlowColor, LyricStyleProperty.GlowOpacity, LyricStyleProperty.GlowBlurRadiusPx, LyricStyleProperty.GlowSpreadPx } : [property];
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
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { StrokeWidthPx = 2f }));
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { ShadowBlurPx = 2f }));
        Assert.True(TokenStyleSupport.HasDrawableOverride(new LyricStyleOverride { Opacity = 0.5f }));
    }
}
