using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>P3 第 6 步：脚本两态写入投影回绘制描述的规则（P3 设计 §6、§9）。</summary>
public sealed class StateStyleProjectionTests
{
    private static readonly LyricStyle Source = LyricStyle.Default with
    {
        Stroke = new StrokeSpec { Color = new(255, 0, 0), WidthPx = 4, Join = StrokeJoin.Round },
        Shadow = ShadowSpec.Create(new(0, 0, 0, 200)),
        Glow = GlowSpec.Create(new(30, 80, 120, 180), .7f, 12, 3),
    };
    private static readonly Fx.StrokeValue SourceStroke = new(4, Fx.MaterialValue.Solid(new(1, 0, 0, 1)), Fx.StrokeJoin.Round);

    private static Fx.StrokesUpdate Blue(double width = 6) => new([new(width, Fx.MaterialValue.Solid(new(0, 0, 1, 1)))]);

    [Fact]
    public void 只写未唱时已唱钉成源的未唱值()
    {
        var strokes = StaticStyleProjection.Apply(Source, new(new Fx.AppearanceUpdate(strokes: Blue())));
        Assert.Equal(6, strokes.Unsung.Strokes.Innermost!.WidthPx);
        Assert.False(strokes.Sung.Strokes.FollowsUnsung);
        Assert.Equal(Source.Unsung.Strokes, strokes.ResolveSung().Strokes);

        var decorations = StaticStyleProjection.Apply(Source, new(new Fx.AppearanceUpdate(shadow: new(null), glow: new(null))));
        Assert.Null(decorations.Unsung.Shadow); Assert.Null(decorations.Unsung.Glow);
        Assert.False(decorations.Sung.Shadow.FollowsUnsung); Assert.False(decorations.Sung.Glow.FollowsUnsung);
        Assert.Same(Source.Unsung.Shadow, decorations.ResolveSung().Shadow); Assert.Same(Source.Unsung.Glow, decorations.ResolveSung().Glow);
        // 没写的成员仍跟随。
        Assert.True(decorations.Sung.Strokes.FollowsUnsung);
    }

    [Fact]
    public void 只写已唱时未唱保留源值且已唱显式()
    {
        var result = StaticStyleProjection.Apply(Source, new(sung: new Fx.AppearanceUpdate(strokes: Blue(), glow: new(null))));
        Assert.Equal(Source.Unsung, result.Unsung);
        Assert.False(result.Sung.Strokes.FollowsUnsung); Assert.Equal(6, result.ResolveSung().Strokes.Innermost!.WidthPx);
        Assert.False(result.Sung.Glow.FollowsUnsung); Assert.Null(result.ResolveSung().Glow);
        Assert.True(result.Sung.Shadow.FollowsUnsung);
    }

    [Fact]
    public void 两态写同一值保持跟随而写不同值各自显式()
    {
        var both = StaticStyleProjection.Apply(Source, Fx.StyleUpdate.Both(new(strokes: Blue(), shadow: new(null), glow: new(null))));
        Assert.True(both.Sung.Strokes.FollowsUnsung); Assert.True(both.Sung.Shadow.FollowsUnsung); Assert.True(both.Sung.Glow.FollowsUnsung);
        Assert.Equal(6, both.Unsung.Strokes.Innermost!.WidthPx); Assert.Null(both.Unsung.Shadow);

        var split = StaticStyleProjection.Apply(Source, new(new Fx.AppearanceUpdate(strokes: Blue()), new Fx.AppearanceUpdate(strokes: Blue(9))));
        Assert.Equal(6, split.Unsung.Strokes.Innermost!.WidthPx);
        Assert.False(split.Sung.Strokes.FollowsUnsung); Assert.Equal(9, split.ResolveSung().Strokes.Innermost!.WidthPx);
    }

    [Fact]
    public void 写扫字样式只改扫字且同值写入返回原实例()
    {
        var result = StaticStyleProjection.Apply(Source, new(wipe: new(.3, new(new(1, 1, 1, 1), .5, .8, Fx.WipeHighlightBlend.Add))));
        Assert.Equal(.3, result.Wipe.FeatherRatio);
        Assert.Equal(new WipeHighlightSpec { Color = RgbaColor.White, WidthRatio = .5, Opacity = .8f, Blend = WipeHighlightBlend.Add }, result.Wipe.Highlight);
        Assert.Equal(Source.Unsung, result.Unsung); Assert.Equal(Source.Sung, result.Sung);
        // 空写、与基线相同的扫字样式、两态或单态写回原值都是 no-op。
        Assert.Same(Source, StaticStyleProjection.Apply(Source, new()));
        Assert.Same(Source, StaticStyleProjection.Apply(Source, new(wipe: new())));
        Assert.Same(Source, StaticStyleProjection.Apply(Source, Fx.StyleUpdate.Both(new(strokes: new([SourceStroke])))));
        Assert.Same(Source, StaticStyleProjection.Apply(Source, new(sung: new Fx.AppearanceUpdate(strokes: new([SourceStroke])))));
    }
}
