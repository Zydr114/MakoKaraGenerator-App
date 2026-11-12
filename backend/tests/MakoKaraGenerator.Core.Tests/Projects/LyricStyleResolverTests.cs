using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>样式合并与来源追踪的测试（roadmap STYLE-01 / STYLE-02）。</summary>
public sealed class LyricStyleResolverTests
{
    [Fact]
    public void 覆盖按默认值预设行元素的顺序合并()
    {
        LyricStyle defaults = LyricStyle.Create(40f, fontFamilyName: "Default");
        LyricStylePreset preset = LyricStylePreset.Create(
            "demo",
            "演示预设",
            LyricStyle.Create(52f, fontFamilyName: "Preset"));
        LyricStyleOverride line = new() { FontSizePx = 60f };
        LyricStyleOverride token = new() { FontFamilyName = "Token" };

        LyricStyleResolution result = LyricStyleResolver.Resolve(defaults, preset, line, token);

        Assert.Equal(60f, result.Style.FontSizePx);
        Assert.Equal("Token", result.Style.FontFamilyName);
        Assert.Equal(LyricStyleSource.Line, result.SourceOf(LyricStyleProperty.FontSizePx));
        Assert.Equal(LyricStyleSource.Token, result.SourceOf(LyricStyleProperty.FontFamilyName));
        Assert.Equal(LyricStyleSource.Preset, result.SourceOf(LyricStyleProperty.SungFill));
    }

    [Fact]
    public void 空覆盖不会改变值也不会伪造来源()
    {
        LyricStyle defaults = LyricStyle.Create(40f);

        LyricStyleResolution result = LyricStyleResolver.Resolve(
            defaults,
            lineOverride: LyricStyleOverride.Empty,
            tokenOverride: LyricStyleOverride.Empty);

        Assert.Equal(defaults, result.Style);
        Assert.Equal(LyricStyleSource.Default, result.SourceOf(LyricStyleProperty.FontSizePx));
    }

    [Fact]
    public void 非法覆盖在解析时被拒绝()
    {
        LyricStyleOverride invalid = new() { FontSizePx = 0f };

        Assert.Throws<ArgumentOutOfRangeException>(() => LyricStyleResolver.Resolve(tokenOverride: invalid));
    }

    [Fact]
    public void 字体族可以覆盖回后端默认字体()
    {
        // 回归：字体族的"值"本身可以是 null（=用后端默认字体），
        // 若用可空类型表达覆盖，null 只能表示"不覆盖"，这条覆盖就写不出来。
        LyricStyle defaults = LyricStyle.Create(40f, fontFamilyName: "Default");

        LyricStyleResolution result = LyricStyleResolver.Resolve(
            defaults,
            lineOverride: new LyricStyleOverride { FontFamilyName = null });

        Assert.Null(result.Style.FontFamilyName);
        Assert.Equal(LyricStyleSource.Line, result.SourceOf(LyricStyleProperty.FontFamilyName));
    }

    [Fact]
    public void 未设置字体族不会被当成覆盖()
    {
        LyricStyle defaults = LyricStyle.Create(40f, fontFamilyName: "Default");

        // 只改字号：字体族既不该被改动，也不该被标成"行覆盖"。
        LyricStyleResolution result = LyricStyleResolver.Resolve(
            defaults,
            lineOverride: new LyricStyleOverride { FontSizePx = 48f });

        Assert.Equal("Default", result.Style.FontFamilyName);
        Assert.Equal(LyricStyleSource.Default, result.SourceOf(LyricStyleProperty.FontFamilyName));
        Assert.Equal(LyricStyleSource.Line, result.SourceOf(LyricStyleProperty.FontSizePx));
    }

    [Fact]
    public void 预设提供整份基线()
    {
        LyricStyle defaults = LyricStyle.Create(40f, fontFamilyName: "Default");
        LyricStylePreset preset = LyricStylePreset.Create(
            "demo",
            "演示预设",
            LyricStyle.Create(52f, fontFamilyName: "Preset"));

        LyricStyleResolution result = LyricStyleResolver.Resolve(defaults, preset);

        // 预设替换整份样式，因此每个属性都应当标成"来自预设"。
        Assert.Equal(preset.Style, result.Style);

        foreach (LyricStyleProperty property in LyricStylePropertyExtensions.All)
        {
            Assert.Equal(LyricStyleSource.Preset, result.SourceOf(property));
        }
    }

    [Fact]
    public void 非法值会指向产生它的那一层()
    {
        // 若统一报成"行覆盖有问题"，预设写错字号时排查方向会完全跑偏。
        LyricStyle invalid = new() { FontSizePx = 0f };
        LyricStylePreset badPreset = LyricStylePreset.Create("bad", "坏预设", invalid);

        Assert.Equal(
            "defaultStyle",
            Assert.Throws<ArgumentOutOfRangeException>(() => LyricStyleResolver.Resolve(defaultStyle: invalid)).ParamName);

        Assert.Equal(
            "preset",
            Assert.Throws<ArgumentOutOfRangeException>(() => LyricStyleResolver.Resolve(preset: badPreset)).ParamName);

        Assert.Equal(
            "lineOverride",
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LyricStyleResolver.Resolve(lineOverride: new LyricStyleOverride { VerticalCenterRatio = 1.5d })).ParamName);

        Assert.Equal(
            "tokenOverride",
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LyricStyleResolver.Resolve(tokenOverride: new LyricStyleOverride { FontSizePx = -1f })).ParamName);
    }

    [Fact]
    public void 来源查询对越界属性抛异常而不是沉默()
    {
        LyricStyleResolution result = LyricStyleResolver.Resolve();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => result.SourceOf((LyricStyleProperty)999));
    }

    [Fact]
    public void 三态值区分未设置与设置为空()
    {
        Assert.False(OptionalValue.Unset<string?>().IsSet);
        Assert.False(OptionalValue.Unset<string?>().TryGet(out string? unsetValue));
        Assert.Null(unsetValue);

        Assert.True(OptionalValue.Set<string?>(null).IsSet);
        Assert.True(OptionalValue.Set<string?>(null).TryGet(out string? nullValue));
        Assert.Null(nullValue);

        // 隐式转换：覆盖里直接写 `FontFamilyName = null` 就是这个语义（覆盖成空，而不是不覆盖）。
        OptionalValue<string?> implicitNull = null;
        Assert.True(implicitNull.IsSet);
        Assert.Null(implicitNull.Value);

        Assert.True(OptionalValue.Set("Noto Sans CJK SC").IsSet);
        Assert.Equal("Noto Sans CJK SC", OptionalValue.Set("Noto Sans CJK SC").Value);
    }

    [Fact]
    public void 基础样式属性按覆盖层合并并追踪来源()
    {
        LyricStyle resolved = LyricStyleResolver.Resolve(
            defaultStyle: new LyricStyle { Opacity = 0.8f, Scale = 1.2f },
            lineOverride: new LyricStyleOverride { Opacity = 0.5f, Stroke = StrokeSpec.Create(RgbaColor.White, 4f) }).Style;

        Assert.Equal(0.5f, resolved.Opacity);
        Assert.Equal(1.2f, resolved.Scale);
        Assert.Equal(4f, resolved.Stroke!.WidthPx);
    }

    [Fact]
    public void 基础样式范围非法时拒绝解析()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LyricStyleResolver.Resolve(
            defaultStyle: new LyricStyle { Opacity = 1.1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LyricStyleResolver.Resolve(
            lineOverride: new LyricStyleOverride { Scale = 0f }));
    }
}
