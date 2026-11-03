using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>源继承外观与中立 SDK 之间的边界；返回帧值，不回写源工程。</summary>
public static class StaticStyleProjection
{
    /// <summary>固定全部源元素／行、文字标记和组的真实外观，各实例读取同一份基线。</summary>
    public static Fx.BaselineStyles CreateBaseline(ProjectSnapshot snapshot, PreparedSourceMaterials? materials = null, SceneFrame? scene = null) => new(SourceStyles(snapshot, scene)
        .Select(pair => new KeyValuePair<Fx.TokenId, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
        LineStyles(snapshot, scene).Select(pair => new KeyValuePair<Fx.LineId, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
        MarkStyles(snapshot, scene).Select(pair => new KeyValuePair<Fx.NodeId, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
        SceneDefinitionBuilder.Build(snapshot).Elements.Where(static node => node.Kind == SceneElementKind.Group)
            .Select(static node => new KeyValuePair<Fx.NodeId, Fx.GroupStyleView>(new(node.Id), new(1))));

    /// <summary>完整源行的继承外观，包含无元素／当前不可见行。</summary>
    public static ImmutableDictionary<Fx.LineId, LyricStyle> LineStyles(ProjectSnapshot snapshot, SceneFrame? scene = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var resolver = PresetSceneStyleProjection.Resolver(snapshot);
        return snapshot.Lyrics.AllLines.Select(line => new KeyValuePair<Fx.LineId, LyricStyle>(new(line.Id),
            PresetSceneStyleProjection.Lyrics(snapshot, line.Id, frame: scene, resolver: resolver).Style)).ToImmutableDictionary();
    }
    /// <summary>全部真实标记的提示组基线＋自身覆盖，不受当前 Cue 可见性限制。</summary>
    public static ImmutableDictionary<Fx.NodeId, LyricStyle> MarkStyles(ProjectSnapshot snapshot, SceneFrame? scene = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var resolver = PresetSceneStyleProjection.Resolver(snapshot);
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        return tree.Nodes.Where(static node => node.Content is TextMarkContent).Select(node =>
            new KeyValuePair<Fx.NodeId, LyricStyle>(new(node.Id),
                PresetSceneStyleProjection.Mark(snapshot, node.Id, scene, resolver).Style)).ToImmutableDictionary();
    }

    /// <summary>按工程、分支、行、元素的顺序解析完整源外观。</summary>
    public static ImmutableDictionary<Fx.TokenId, LyricStyle> SourceStyles(ProjectSnapshot snapshot, SceneFrame? scene = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var resolver = PresetSceneStyleProjection.Resolver(snapshot);
        return snapshot.Lyrics.AllLines.SelectMany(line => line.Tokens.Select(token =>
            new KeyValuePair<Fx.TokenId, LyricStyle>(new(token.Id),
                PresetSceneStyleProjection.Lyrics(snapshot, line.Id, token.Id, scene, resolver).Style))).ToImmutableDictionary();
    }

    /// <summary>无 Skia 类型的静态外观投影；渐变不会猜测纯色基色。</summary>
    public static Fx.StyleView View(LyricStyle style, PreparedSourceMaterials? materials = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new(Material(style.UnsungFill.Material, materials), Material(style.SungFill.Material, materials),
            style.Stroke is { } stroke ? new(stroke.WidthPx,
                Material(stroke.Material, materials),
                (Fx.StrokeJoin)stroke.Join, (Fx.StrokePosition)stroke.Position) : null,
            style.Shadow is { } shadow ? new(Color(shadow.Color), shadow.Opacity, shadow.AngleDegrees, shadow.DistancePx, shadow.BlurRadiusPx) : null,
            style.Opacity, style.Glow is { } glow ? new(Color(glow.Color), glow.Opacity, glow.BlurRadiusPx, glow.SpreadPx) : null);
    }

    /// <summary>将最终帧输出投影为现有绘制描述；无变化返回原实例并保留所有未写字段。</summary>
    public static LyricStyle Apply(LyricStyle source, Fx.StyleUpdate update)
    {
        return ApplyCore(source, update, false, null);
    }
    /// <summary>明确生成固定字形的绘制描述；包材质的白色字段仅作几何种子，不能当最终彩色外观。</summary>
    public static LyricStyle ApplyDrawingStyle(LyricStyle source, Fx.StyleUpdate update, PreparedSourceMaterials? materials = null) => ApplyCore(source, update, true, materials);
    private static LyricStyle ApplyCore(LyricStyle source, Fx.StyleUpdate update, bool geometrySeed, PreparedSourceMaterials? materials)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(update);
        var baseline = View(source, materials); var result = update.Apply(baseline);
        bool sourcePackage = !SourceMaterialCatalog.Collect(source).IsEmpty;
        if (result == baseline && !(geometrySeed && sourcePackage)) return source;
        return source with
        {
            UnsungFill = result.UnsungMaterial == baseline.UnsungMaterial && !sourcePackage ? source.UnsungFill : DrawingMaterial(result.UnsungMaterial, geometrySeed),
            SungFill = result.SungMaterial == baseline.SungMaterial && !sourcePackage ? source.SungFill : DrawingMaterial(result.SungMaterial, geometrySeed),
            Stroke = result.Stroke == baseline.Stroke && !sourcePackage ? source.Stroke : Stroke(result.Stroke, geometrySeed),
            Shadow = result.Shadow == baseline.Shadow ? source.Shadow : Shadow(result.Shadow),
            Opacity = Native(result.Opacity),
        };
    }
    private static Fx.ColorRgba Color(RgbaColor color) => new(color.Red / 255d, color.Green / 255d, color.Blue / 255d, color.Alpha / 255d);
    private static RgbaColor Color(Fx.ColorRgba color) => new(Byte(color.Red), Byte(color.Green), Byte(color.Blue), Byte(color.Alpha));
    private static byte Byte(double value) => checked((byte)Math.Round(value * 255));
    private static float Native(double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result) || value != 0 && result == 0)
            throw new PreparedFrameException("UnsupportedNativeStyleValues", "样式参数超出绘制后端有限精度范围。");
        return result;
    }
    private static Fx.MaterialValue Material(MaterialSpec material, PreparedSourceMaterials? materials) => material switch
    {
        MaterialSpec.Solid solid => Fx.MaterialValue.Solid(Color(solid.Color)),
        MaterialSpec.Gradient gradient => Fx.MaterialValue.Gradient(Gradient(gradient.Value)),
        MaterialSpec.PackageMaterial package => materials?.Resolve(package)
            ?? throw new PreparedFrameException("SourceMaterialResourcesRequired", "源包材质必须使用会话前固定的真实资源投影。"),
        _ => throw new PreparedFrameException("InvalidSourceMaterial", "源材质类型无效。"),
    };
    /// <summary>仅供固定字形几何／区域绘制的源种子；不提供最终颜色或材质回退。</summary>
    public static LyricStyle DrawingSeed(LyricStyle style) => style with
    {
        UnsungFill = style.UnsungFill.Material is MaterialSpec.PackageMaterial ? new() { Color = RgbaColor.White } : style.UnsungFill,
        SungFill = style.SungFill.Material is MaterialSpec.PackageMaterial ? new() { Color = RgbaColor.White } : style.SungFill,
        Stroke = style.Stroke is { Material: MaterialSpec.PackageMaterial } stroke ? stroke with { Material = new MaterialSpec.Solid(RgbaColor.White) } : style.Stroke,
    };
    private static Fx.GradientValue Gradient(GradientSpec value) => new(
        [.. value.Stops.Select(static stop => new Fx.GradientStop(stop.Offset, Color(stop.Color)))],
        (Fx.GradientKind)value.Kind, value.AngleDegrees, (Fx.GradientCoordinateSpace)value.CoordinateSpace,
        (Fx.GradientColorSpace)value.ColorSpace, (Fx.GradientHuePath)value.HuePath, value.CenterX, value.CenterY, value.RadiusX, value.RadiusY);
    private static GradientSpec Gradient(Fx.GradientValue value) => GradientSpec.Create(
        value.Stops.Select(static stop => new GradientStop(stop.Offset, Color(stop.Color))),
        (GradientKind)value.Kind, value.AngleDegrees, (GradientCoordinateSpace)value.CoordinateSpace,
        (GradientColorSpace)value.ColorSpace, (GradientHuePath)value.HuePath, value.CenterX, value.CenterY, value.RadiusX, value.RadiusY);
    private static FillSpec Fill(Fx.MaterialValue material)
    {
        if (material is Fx.MaterialValue.SolidMaterial solid) return new() { Color = Color(solid.Color) };
        if (material is Fx.MaterialValue.GradientMaterial gradient)
        {
            var value = Gradient(gradient.Value);
            Native(value.AngleDegrees * (Math.PI / 180));
            if (value.Kind == GradientKind.Radial) { Native(value.RadiusX); Native(value.RadiusY); }
            if (!GradientSupport.IsSupported(value))
                throw new PreparedFrameException("UnsupportedEffectGradient", "脚本请求的渐变尚未接入后端：" + GradientSupport.UnsupportedReason(value));
            // Core 的历史纯色字段只用于适配当前绘制描述，SDK 不提供第二个基色。
            return new() { Color = value.Stops[0].Color, Gradient = value };
        }
        throw new PreparedFrameException("UnsupportedMaterialBackend", "包材质的生产执行将在 R3 接入，不能静默使用基础外观。");
    }
    private static FillSpec DrawingMaterial(Fx.MaterialValue material, bool geometrySeed) => geometrySeed && material is Fx.MaterialValue.PackageMaterial
        ? new() { Color = RgbaColor.White } : Fill(material);
    private static StrokeSpec? Stroke(Fx.StrokeValue? value, bool geometrySeed)
    {
        if (value is null) return null;
        if (geometrySeed && value.Material is Fx.MaterialValue.PackageMaterial)
            return StrokeSpec.Create(RgbaColor.White, Native(value.WidthPx), (StrokeJoin)value.Join, (StrokePosition)value.Position);
        if (value.Material is Fx.MaterialValue.PackageMaterial)
            throw new PreparedFrameException("UnsupportedStrokeMaterial", "包描边须由材质协调器执行，不能作为普通绘制外观。");
        var material = Fill(value.Material);
        return StrokeSpec.Create(material.Gradient is null ? material.Color : null, Native(value.WidthPx),
            (StrokeJoin)value.Join, (StrokePosition)value.Position) with { Gradient = material.Gradient };
    }
    private static ShadowSpec? Shadow(Fx.ShadowValue? value)
    {
        if (value is null) return null;
        Native(value.AngleDegrees * (Math.PI / 180));
        return ShadowSpec.Create(Color(value.Color), Native(value.Opacity), value.AngleDegrees, Native(value.DistancePx), Native(value.BlurRadiusPx));
    }
}
