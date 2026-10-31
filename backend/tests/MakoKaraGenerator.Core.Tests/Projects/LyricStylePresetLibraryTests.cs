using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>样式预设与预设库的测试。</summary>
public sealed class LyricStylePresetLibraryTests
{
    [Fact]
    public void 起始预设库包含默认与四个可用预设()
    {
        LyricStylePresetLibrary library = LyricStylePresetLibrary.Default;

        Assert.Equal(5, library.Count);
        Assert.True(library.TryFind("default", out LyricStylePreset fallback));
        Assert.Equal(LyricStyle.Default, fallback.Style);
        Assert.True(library.TryFind("karaoke-large", out LyricStylePreset large));
        Assert.True(large.Style.FontSizePx > LyricStyle.Default.FontSizePx);
        Assert.True(library.TryFind("karaoke-warm", out LyricStylePreset warm));
        Assert.NotEqual(LyricStyle.Default.SungColor, warm.Style.SungColor);
    }

    /// <summary>
    /// 库里必须有“未唱/已唱渐变”的那一档：它是阶段 4 出口要求的渐变样式，
    /// 也是用户在界面上能看到渐变效果的唯一入口（界面只能改颜色，不能凭空造出预设）。
    /// </summary>
    [Fact]
    public void 起始预设库提供未唱已唱渐变样式()
    {
        LyricStylePresetLibrary library = LyricStylePresetLibrary.Default;

        Assert.True(library.TryFind("karaoke-gradient", out LyricStylePreset gradient));

        // 未唱与已唱各自拥有完整的渐变描述，否则两遍画出的像素完全相同，
        // 逐字扫描就看不见了（ADR-0032）。
        Assert.NotNull(gradient.Style.UnsungFill.Gradient);
        Assert.NotNull(gradient.Style.SungFill.Gradient);
        Assert.NotEqual(
            gradient.Style.UnsungFill.Gradient!.Stops[0].Color,
            gradient.Style.SungFill.Gradient!.Stops[0].Color);
    }

    [Fact]
    public void 起始预设库提供描边与阴影样式()
    {
        Assert.True(LyricStylePresetLibrary.Default.TryFind("karaoke-outline", out LyricStylePreset outline));

        Assert.NotNull(outline.Style.StrokeColor);
        Assert.True(outline.Style.StrokeWidthPx > 0f);
        Assert.NotNull(outline.Style.ShadowColor);
        Assert.True(outline.Style.ShadowBlurPx > 0f);
    }

    [Fact]
    public void 起始预设库里的样式都是有效的()
    {
        foreach (LyricStylePreset preset in LyricStylePresetLibrary.Default.Presets)
        {
            Assert.True(preset.Style.IsValid, $"预设 '{preset.Id}' 的样式不合法。");
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
        }
    }

    [Fact]
    public void 按标识查找()
    {
        LyricStylePresetLibrary library = LyricStylePresetLibrary.Create(
        [
            LyricStylePreset.Create("a", "甲", LyricStyle.Create(40f)),
        ]);

        Assert.True(library.TryFind("a", out LyricStylePreset found));
        Assert.Equal("甲", found.Name);
        Assert.False(library.TryFind("b", out _));
        Assert.False(library.TryFind(null, out _));
        Assert.False(library.TryFind("  ", out _));
    }

    [Fact]
    public void 标识重复被拒绝()
    {
        LyricStylePreset[] duplicated =
        [
            LyricStylePreset.Create("same", "甲", LyricStyle.Create(40f)),
            LyricStylePreset.Create("same", "乙", LyricStyle.Create(48f)),
        ];

        Assert.Throws<ArgumentException>(() => LyricStylePresetLibrary.Create(duplicated));
    }

    [Fact]
    public void 空库是合法的()
    {
        Assert.Equal(0, LyricStylePresetLibrary.Empty.Count);
        Assert.Empty(LyricStylePresetLibrary.Empty.Presets);
        Assert.False(LyricStylePresetLibrary.Empty.TryFind("default", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("有 空格")]
    [InlineData("中文标识")]
    [InlineData("slash/id")]
    public void 预设标识只接受安全字符(string id)
    {
        Assert.Throws<ArgumentException>(() => LyricStylePreset.Create(id, "名字", LyricStyle.Default));
    }

    [Fact]
    public void 预设名称与样式不可为空白或空引用()
    {
        Assert.Throws<ArgumentException>(() => LyricStylePreset.Create("ok", "  ", LyricStyle.Default));
        Assert.Throws<ArgumentNullException>(() => LyricStylePreset.Create("ok", "名字", null!));
    }
}
