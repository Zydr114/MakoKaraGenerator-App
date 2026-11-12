using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>测试用的样式更新简写：填充按两态分开，描边、阴影、辉光两态写同一值。</summary>
internal static class TestStyle
{
    /// <summary>描边传空数组表示关闭；不传则不写。</summary>
    internal static Fx.StyleUpdate Update(Fx.MaterialValue? unsung = null, Fx.MaterialValue? sung = null,
        Fx.StrokeValue[]? strokes = null, Fx.ShadowUpdate? shadow = null, Fx.GlowUpdate? glow = null, double opacityFactor = 1)
    {
        Fx.StrokesUpdate? layers = strokes is null ? null : new([.. strokes]);
        return new(Appearance(unsung), Appearance(sung), opacityFactor);
        Fx.AppearanceUpdate? Appearance(Fx.MaterialValue? fill) =>
            fill is null && layers is null && shadow is null && glow is null ? null : new(fill, layers, shadow, glow);
    }
}
