using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Tests;

/// <summary>测试用外观简写：两态填充分开，描边、阴影、辉光两态相同。</summary>
internal static class TestStyles
{
    internal static StyleView View(MaterialValue unsung, MaterialValue sung, StrokeValue? stroke, ShadowValue? shadow, double opacity, GlowValue? glow = null)
    {
        ImmutableArray<StrokeValue> strokes = stroke is null ? [] : [stroke];
        return new(new(unsung, strokes, shadow, glow), new(sung, strokes, shadow, glow), opacity);
    }
}
