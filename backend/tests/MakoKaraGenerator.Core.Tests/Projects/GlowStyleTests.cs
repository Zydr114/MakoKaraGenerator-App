using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class GlowStyleTests
{
    [Fact]
    public void 默认辉光与关闭条件使用颜色透明度及模糊外扩共同判断()
    {
        GlowSpec glow = new();
        Assert.Equal(RgbaColor.White, glow.Color); Assert.Equal(1f, glow.Opacity);
        Assert.Equal(8f, glow.BlurRadiusPx); Assert.Equal(0f, glow.SpreadPx); Assert.True(glow.IsEnabled);
        Assert.False((glow with { Color = RgbaColor.Transparent }).IsEnabled);
        Assert.False((glow with { Opacity = 0 }).IsEnabled);
        Assert.False((glow with { BlurRadiusPx = 0 }).IsEnabled);
        Assert.True((glow with { BlurRadiusPx = 0, SpreadPx = 1 }).IsEnabled);
        Assert.Null(LyricStyle.Default.Glow);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-1f)]
    public void 辉光及部分覆盖拒绝非有限数和负值(float invalid)
    {
        Assert.False((new GlowSpec { Opacity = invalid }).IsValid);
        Assert.False((new GlowSpec { BlurRadiusPx = invalid }).IsValid);
        Assert.False((new GlowSpec { SpreadPx = invalid }).IsValid);
        Assert.False((new GlowSpecOverride { Opacity = invalid }).IsValid);
        Assert.False((new GlowSpecOverride { BlurRadiusPx = invalid }).IsValid);
        Assert.False((new GlowSpecOverride { SpreadPx = invalid }).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => GlowSpec.Create(RgbaColor.White, spreadPx: invalid));
        Assert.Throws<ArgumentException>(() => LyricStyleResolver.Resolve(LyricStyle.Default,
            lineOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { SpreadPx = invalid }) }));
    }

    [Fact]
    public void 四层逐叶合并保留预设及正确来源且空父采用透明零扩张种子()
    {
        GlowSpec presetGlow = new() { Color = RgbaColor.Black, Opacity = .6f, BlurRadiusPx = 4, SpreadPx = 2 };
        var preset = LyricStylePreset.Create("glow", "辉光", LyricStyle.Default with { Glow = presetGlow });
        var result = LyricStyleResolver.Resolve(LyricStyle.Default, preset,
            branchOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Color = RgbaColor.White }) },
            lineOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { BlurRadiusPx = 7 }) },
            tokenOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { SpreadPx = 3 }) });
        Assert.Equal(presetGlow with { Color = RgbaColor.White, BlurRadiusPx = 7, SpreadPx = 3 }, result.Style.Glow);
        Assert.Equal(LyricStyleSource.Branch, result.SourceOf(LyricStyleProperty.UnsungGlowColor));
        Assert.Equal(LyricStyleSource.Line, result.SourceOf(LyricStyleProperty.UnsungGlowBlurRadiusPx));
        Assert.Equal(LyricStyleSource.Token, result.SourceOf(LyricStyleProperty.UnsungGlowSpreadPx));
        Assert.Equal(LyricStyleSource.Preset, result.SourceOf(LyricStyleProperty.UnsungGlowOpacity));
        var seeded = new GlowSpecOverride { Opacity = .4f }.Apply(null);
        Assert.Equal(new GlowSpec { Color = RgbaColor.Transparent, Opacity = .4f, BlurRadiusPx = 0, SpreadPx = 0 }, seeded);
        Assert.False(seeded.IsEnabled);
    }

    [Fact]
    public void 显式关闭标记全部来源而空对象保持继承及不可变无操作()
    {
        var basis = LyricStyle.Default with { Glow = new() };
        var disabled = LyricStyleResolver.Resolve(basis, tokenOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(null) });
        Assert.Null(disabled.Style.Glow);
        foreach (var key in LyricStylePropertyExtensions.UnsungGlowProperties)
        { Assert.Equal(LyricStyleSource.Token, disabled.SourceOf(key)); Assert.True(TokenStyleSupport.IsSupported(key)); }
        var empty = new LyricStyleOverride { Glow = OptionalValue.Set<GlowSpecOverride?>(new()) };
        Assert.True(empty.IsEmpty); Assert.False(empty.Glow.IsSet);
        Assert.Same(basis, LyricStyleResolver.Resolve(basis, lineOverride: empty).Style);
        Assert.Equal(basis.Glow, LyricStyleResolver.Resolve(basis, lineOverride: empty with { Opacity = .5f }).Style.Glow);
    }

    [Fact]
    public void 批量逐叶复制与恢复保留相邻字段及显式关闭()
    {
        var basis = LyricStyle.Default with { Glow = new() { Color = RgbaColor.Black, BlurRadiusPx = 12, SpreadPx = 2 } };
        var overlay = new LyricStyleOverride { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Opacity = .3f, SpreadPx = 5 }) };
        var copied = StyleBatchEditor.CopyProperties(overlay, basis, [LyricStyleProperty.UnsungGlowBlurRadiusPx]);
        Assert.Equal(new GlowSpecOverride { Opacity = .3f, SpreadPx = 5, BlurRadiusPx = 12 }, copied.Glow.Value);
        var restored = StyleBatchEditor.CopyProperties(copied, basis, [LyricStyleProperty.UnsungGlowBlurRadiusPx], true);
        Assert.Equal(overlay, restored);
        var disabled = overlay with { Glow = OptionalValue.Set<GlowSpecOverride?>(null) };
        Assert.Equal(disabled, StyleBatchEditor.CopyProperties(disabled, basis, [LyricStyleProperty.UnsungGlowOpacity], true));
        Assert.False(StyleBatchEditor.CopyProperties(copied, basis, [LyricStyleProperty.UnsungGlow], true).Glow.IsSet);
    }

    [Fact]
    public void 场景外观和效果开关保留静态辉光值及三态()
    {
        var enabled = LyricStyleEffects.Enable(LyricStyle.Default, LyricStyleEffectKind.Glow);
        Assert.True(LyricStyleEffects.IsEnabled(enabled, LyricStyleEffectKind.Glow));
        Assert.Same(enabled, LyricStyleEffects.Enable(enabled, LyricStyleEffectKind.Glow));
        Assert.Null(LyricStyleEffects.Disable(enabled, LyricStyleEffectKind.Glow).Glow);
        var appearance = SceneTextAppearance.FromStyle(enabled);
        Assert.Equal(enabled.Glow, appearance.ToLyricStyle().Glow);
        Assert.Same(appearance, appearance.Resolve(new() { Pending = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new()) } }));
        var patch = new LyricStyleOverride { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { SpreadPx = 4 }) };
        Assert.Equal(patch.Glow, SceneTextAppearanceOverride.FromStyleOverride(patch).ToStyleOverride().Glow);
        Assert.Equal(4, appearance.Resolve(SceneTextAppearanceOverride.FromStyleOverride(patch)).Glow!.SpreadPx);
        Assert.Null(appearance.Resolve(new() { Pending = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(null) } }).Glow);
    }
}
