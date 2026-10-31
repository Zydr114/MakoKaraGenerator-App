using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>下一轮样式、位置、歌词分支和双行动画契约测试。</summary>
public sealed class StyleAnimationContractTests
{
    [Fact]
    public void 渐变保留方向色相路径和停止点()
    {
        GradientSpec gradient = GradientSpec.Create(
            [
                GradientStop.Create(0d, new RgbaColor(255, 0, 0)),
                GradientStop.Create(0.5d, new RgbaColor(0, 255, 0)),
                GradientStop.Create(1d, new RgbaColor(0, 0, 255)),
            ],
            angleDegrees: 135d,
            coordinateSpace: GradientCoordinateSpace.Glyph,
            colorSpace: GradientColorSpace.Hsv,
            huePath: GradientHuePath.Longer);

        Assert.True(gradient.IsValid);
        Assert.Equal(135d, gradient.AngleDegrees);
        Assert.Equal(GradientCoordinateSpace.Glyph, gradient.CoordinateSpace);
        Assert.Equal(GradientColorSpace.Hsv, gradient.ColorSpace);
        Assert.Equal(GradientHuePath.Longer, gradient.HuePath);
        Assert.Equal(3, gradient.Stops.Length);
        Assert.Equal(0.5d, gradient.Stops[1].Offset);
    }

    [Fact]
    public void 描边描述固定为外部语义并校验连接方式()
    {
        StrokeSpec stroke = StrokeSpec.Create(new RgbaColor(255, 32, 64), 6f, StrokeJoin.Bevel);

        Assert.True(stroke.IsValid);
        Assert.True(stroke.IsEnabled);
        Assert.Equal(StrokeJoin.Bevel, stroke.Join);
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokeSpec.Create(RgbaColor.White, -1f));
        Assert.False((stroke with { Join = (StrokeJoin)99 }).IsValid);
    }

    [Fact]
    public void 阴影描述由角度和距离求出偏移且拒绝非法参数()
    {
        ShadowSpec shadow = ShadowSpec.Create(
            new RgbaColor(0, 0, 0, 180),
            opacity: 0.75f,
            angleDegrees: 90d,
            distancePx: 12f,
            blurRadiusPx: 5f);

        Assert.True(shadow.IsValid);
        Assert.True(shadow.IsEnabled);
        Assert.Equal(0f, shadow.OffsetXPx, precision: 5);
        Assert.Equal(12f, shadow.OffsetYPx, precision: 5);
        Assert.False((shadow with { Opacity = 2f }).IsValid);
    }

    [Fact]
    public void 渐变不会静默排序无序停止点()
    {
        Assert.Throws<ArgumentException>(() => GradientSpec.Create(
            [
                GradientStop.Create(0.8d, RgbaColor.White),
                GradientStop.Create(0.2d, RgbaColor.Black),
            ]));
    }

    [Fact]
    public void 位置使用父参考坐标允许画布外并拒绝非有限值()
    {
        ScenePlacement placement = ScenePlacement.Create(
            NormalizedPosition.Create(0.25d, 0.75d),
            ScenePlacementAnchor.BottomRight,
            rotationDegrees: -12d,
            scale: 1.25d);

        Assert.True(placement.IsValid);
        Assert.Equal(0.25d, placement.Position.X);
        Assert.Equal(ScenePlacementAnchor.BottomRight, placement.Anchor);
        Assert.Equal(new(1.1, -.5), NormalizedPosition.Create(1.1, -.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedPosition.Create(double.NaN, 0.5d));
    }

    [Fact]
    public void 翻译分支可以拥有独立行级时间且默认静态()
    {
        LyricLine line = LyricLine.Create(
            "translation-1",
            "译文",
            TimeRange.Create(500_000, 1_500_000),
            TimingStatus.Edited);

        LyricBranch branch = LyricBranch.Create(
            "translation",
            "翻译",
            LyricBranchRole.Translation,
            [line],
            language: "zh-CN",
            timingMode: LyricBranchTimingMode.Independent);

        Assert.True(branch.IsValid);
        Assert.Equal(LyricBranchTimingMode.Independent, branch.TimingMode);
        Assert.Equal(LyricBranchAnimationPolicy.Static, LyricPresentationDefinition.Empty.Resolve(branch.Id).PolicyFor(branch));
        Assert.Single(branch.Lines);
    }

    [Fact]
    public void 注音接口关联基础字且不携带独立时间()
    {
        RubyAnnotation ruby = RubyAnnotation.Create(
            "ruby-1",
            "token-kanji",
            "かんじ",
            language: "ja-JP",
            relativeScale: 0.45d,
            gapPx: 3d);

        Assert.True(ruby.IsValid);
        Assert.Equal("token-kanji", ruby.BaseTokenId);
        Assert.Equal("ja-JP", ruby.Language);
        Assert.Equal(0.45d, ruby.RelativeScale);
    }

    [Fact]
    public void 默认双行在阈值后显示下一句并按槽位轮换()
    {
        LyricLine[] lines =
        [
            TimedLine("line-1", 0, 3_000_000),
            TimedLine("line-2", 2_000_000, 5_000_000),
            TimedLine("line-3", 4_000_000, 7_000_000),
        ];

        Assert.Equal(["line-1"], IdsAt(lines, 1_000_000));
        Assert.Equal(["line-1", "line-2"], IdsAt(lines, 2_000_000));

        ImmutableArray<SlotSequenceSample> atThird = SlotSequenceAnimationScheduler.Sample(lines, 4_000_000);

        Assert.Equal(["line-2", "line-3"], atThird.Select(sample => sample.LineId));
        Assert.Equal(1, atThird[0].SlotIndex);
        Assert.Equal(0, atThird[1].SlotIndex);
    }

    [Fact]
    public void 保留到自身结束时槽位冲突会排队而不会凭空显示第三句()
    {
        LyricLine[] lines =
        [
            TimedLine("line-1", 0, 3_000_000),
            TimedLine("line-2", 1_000_000, 4_000_000),
            TimedLine("line-3", 2_000_000, 5_000_000),
        ];

        SlotSequenceAnimationProfile profile = SlotSequenceAnimationProfile.Default with
        {
            RevealProgress = 0.2d,
        };

        Assert.Equal(["line-1", "line-2"], IdsAt(lines, 2_500_000, profile));
        Assert.Equal(["line-2", "line-3"], IdsAt(lines, 3_000_000, profile));
    }

    [Fact]
    public void 允许溢出时可以按配置显示三句()
    {
        LyricLine[] lines =
        [
            TimedLine("line-1", 0, 4_000_000),
            TimedLine("line-2", 1_000_000, 5_000_000),
            TimedLine("line-3", 2_000_000, 6_000_000),
        ];

        SlotSequenceAnimationProfile profile = SlotSequenceAnimationProfile.Default with
        {
            RevealProgress = 0.2d,
            ConflictPolicy = SlotConflictPolicy.AllowOverflow,
            MaxVisibleLines = 3,
        };

        Assert.Equal(3, SlotSequenceAnimationScheduler.Sample(lines, 2_500_000, profile).Length);
    }

    [Fact]
    public void 配置可以选择按固定时间触发()
    {
        LyricLine[] lines =
        [
            TimedLine("line-1", 0, 3_000_000),
            TimedLine("line-2", 2_000_000, 5_000_000),
        ];

        SlotSequenceAnimationProfile profile = SlotSequenceAnimationProfile.Default with
        {
            RevealMode = SlotRevealMode.TimeAfterPreviousStart,
            RevealDelayUs = 1_000_000,
        };

        Assert.Equal(["line-1"], IdsAt(lines, 999_999, profile));
        Assert.Equal(["line-1", "line-2"], IdsAt(lines, 2_500_000, profile));
    }

    [Fact]
    public void 配置可以在下一句开始前按提前量显示()
    {
        LyricLine[] lines =
        [
            TimedLine("line-1", 0, 4_000_000),
            TimedLine("line-2", 4_000_000, 7_000_000),
        ];

        SlotSequenceAnimationProfile profile = SlotSequenceAnimationProfile.Default with
        {
            RevealMode = SlotRevealMode.LeadBeforeNextStart,
            RevealLeadUs = 1_000_000,
        };

        Assert.Equal(["line-1"], IdsAt(lines, 2_999_999, profile));
        Assert.Equal(["line-1", "line-2"], IdsAt(lines, 3_000_000, profile));
    }

    [Fact]
    public void 无效配置在采样时拒绝()
    {
        SlotSequenceAnimationProfile profile = SlotSequenceAnimationProfile.Default with
        {
            Slots = [],
        };

        Assert.False(profile.IsValid);
        Assert.Throws<ArgumentException>(() => SlotSequenceAnimationScheduler.Sample([], 0, profile));
    }

    [Fact]
    public void 未知枚举值不会被当作有效配置()
    {
        GradientSpec gradient = GradientSpec.Create(
            [
                GradientStop.Create(0d, RgbaColor.Black),
                GradientStop.Create(1d, RgbaColor.White),
            ]) with
        {
            ColorSpace = (GradientColorSpace)99,
        };

        Assert.False(gradient.IsValid);
    }

    [Fact]
    public void 样式效果开关集中清除全部参数并能恢复初始值()
    {
        LyricStyle style = LyricStyle.Default;
        LyricStyle enabled = LyricStyleEffects.Enable(style, LyricStyleEffectKind.Shadow);

        Assert.True(LyricStyleEffects.IsEnabled(enabled, LyricStyleEffectKind.Shadow));
        Assert.Equal(
            [LyricStyleProperty.ShadowColor, LyricStyleProperty.ShadowBlurPx,
                LyricStyleProperty.ShadowSpec],
            LyricStyleEffects.Properties(LyricStyleEffectKind.Shadow));

        LyricStyle disabled = LyricStyleEffects.Disable(enabled, LyricStyleEffectKind.Shadow);
        Assert.False(LyricStyleEffects.IsEnabled(disabled, LyricStyleEffectKind.Shadow));
        Assert.Null(disabled.ShadowColor);
        Assert.Equal(0f, disabled.ShadowBlurPx);
        Assert.Null(disabled.Shadow);
    }

    [Fact]
    public void 关闭效果也会清除新版描边和阴影描述()
    {
        LyricStyle style = LyricStyle.Default with
        {
            StrokeSpec = StrokeSpec.Create(RgbaColor.White, 4f),
            ShadowSpec = ShadowSpec.Create(RgbaColor.Black, distancePx: 6f),
        };

        LyricStyle disabled = LyricStyleEffects.Disable(
            LyricStyleEffects.Disable(style, LyricStyleEffectKind.Stroke),
            LyricStyleEffectKind.Shadow);

        Assert.Null(disabled.StrokeSpec);
        Assert.Null(disabled.ShadowSpec);
        Assert.False(LyricStyleEffects.IsEnabled(disabled, LyricStyleEffectKind.Stroke));
        Assert.False(LyricStyleEffects.IsEnabled(disabled, LyricStyleEffectKind.Shadow));
    }

    [Fact]
    public void 渐变采样支持多停止点和方向投影()
    {
        GradientSpec gradient = GradientSpec.Create(
            [
                GradientStop.Create(0d, RgbaColor.Black),
                GradientStop.Create(0.5d, RgbaColor.White),
                GradientStop.Create(1d, RgbaColor.Black),
            ],
            angleDegrees: 90d);

        Assert.Equal(RgbaColor.Black, GradientSampler.Sample(gradient, -1d));
        Assert.Equal(RgbaColor.White, GradientSampler.Sample(gradient, 0.5d));
        Assert.Equal(RgbaColor.Black, GradientSampler.Sample(gradient, 2d));
        Assert.Equal(0.5d, GradientSampler.ProjectLinearPosition(gradient, 0.5d, 0.5d));
        Assert.Equal(1d, GradientSampler.ProjectLinearPosition(gradient, 0.5d, 1d), precision: 12);
    }

    [Fact]
    public void 径向渐变按中心和椭圆半径采样()
    {
        GradientSpec gradient = GradientSpec.Create(
            [new GradientStop(0d, RgbaColor.Black), new GradientStop(1d, RgbaColor.White)],
            kind: GradientKind.Radial,
            coordinateSpace: GradientCoordinateSpace.Line,
            centerX: 0.25d,
            centerY: 0.5d,
            radiusX: 0.25d,
            radiusY: 0.5d);

        Assert.Equal(RgbaColor.Black, GradientSampler.SampleAt(gradient, 0.25d, 0.5d));
        Assert.Equal(RgbaColor.White, GradientSampler.SampleAt(gradient, 0.5d, 0.5d));
        Assert.Equal(1d, GradientSampler.ProjectRadialPosition(gradient, 0.25d, 1d), precision: 12);
        Assert.Throws<ArgumentException>(() => GradientSpec.Create(
            [new GradientStop(0d, RgbaColor.Black), new GradientStop(1d, RgbaColor.White)],
            kind: GradientKind.Radial, radiusX: 0d));
    }

    [Fact]
    public void 渐变采样支持色相路径并拒绝未实现的色彩空间()
    {
        GradientSpec gradient = GradientSpec.Create(
            [
                GradientStop.Create(0d, new RgbaColor(255, 0, 0)),
                GradientStop.Create(1d, new RgbaColor(0, 0, 255)),
            ],
            colorSpace: GradientColorSpace.Hsv,
            huePath: GradientHuePath.Shorter);

        RgbaColor middle = GradientSampler.Sample(gradient, 0.5d);
        Assert.True(middle.Red > 200 && middle.Blue > 200);
        Assert.Throws<NotSupportedException>(() => GradientSampler.Sample(
            gradient with { ColorSpace = GradientColorSpace.Oklch }, 0.5d));
    }

    private static LyricLine TimedLine(string id, long startUs, long endUs) =>
        LyricLine.Create(id, id, TimeRange.Create(startUs, endUs), TimingStatus.Edited);

    private static IEnumerable<string> IdsAt(
        IReadOnlyList<LyricLine> lines,
        long timeUs,
        SlotSequenceAnimationProfile? profile = null) =>
        SlotSequenceAnimationScheduler.Sample(lines, timeUs, profile).Select(sample => sample.LineId);
}
