using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>源继承外观与中立 SDK 之间的边界；返回帧值，不回写源工程。</summary>
public static class StaticStyleProjection
{
    /// <summary>固定全部源元素／行、文字标记和组的真实外观，各实例读取同一份基线。</summary>
    public static Fx.BaselineStyles CreateBaseline(ProjectSnapshot snapshot, PreparedSourceMaterials? materials = null, SceneFrame? scene = null)
    {
        var baseline = new Fx.BaselineStyles(SourceStyles(snapshot, scene)
        .Select(pair => new KeyValuePair<Fx.TokenId, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
        LineStyles(snapshot, scene).Select(pair => new KeyValuePair<Fx.LineId, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
        MarkStyles(snapshot, scene).Select(pair => new KeyValuePair<Fx.NodeId, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
        SceneDefinitionBuilder.Build(snapshot).Elements.Where(static node => node.Kind == SceneElementKind.Group)
            .Select(static node => new KeyValuePair<Fx.NodeId, Fx.GroupStyleView>(new(node.Id), new(1))));
        return scene is null ? baseline : baseline.WithLoaded(LoadedSourceStyles(snapshot, scene)
            .Select(pair => new KeyValuePair<Fx.TokenFamilyTarget, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
            LoadedLineStyles(snapshot, scene).Select(pair => new KeyValuePair<Fx.LineTarget, Fx.StyleView>(pair.Key, View(pair.Value, materials))));
    }

    /// <summary>
    /// 在会话基线（同一快照与材质、不带场景的 <see cref="CreateBaseline"/> 结果）上派生本帧基线：
    /// 不在场的行与元素和场景无关、原样共享，只重算本帧装载行及其元素并整体重算标记，
    /// 结果与带场景的 <see cref="CreateBaseline"/> 相同，成本随在场行而非全曲行数增长。
    /// 调用方已算好的本帧标记与装载元素外观可直接传入，避免重复解析。
    /// </summary>
    public static Fx.BaselineStyles CreateFrameBaseline(Fx.BaselineStyles session, ProjectSnapshot snapshot,
        PreparedSourceMaterials? materials, SceneFrame scene, ImmutableDictionary<Fx.NodeId, LyricStyle>? marks = null,
        ImmutableDictionary<Fx.TokenFamilyTarget, LyricStyle>? loadedTokens = null)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(scene);
        var resolver = PresetSceneStyleProjection.Resolver(snapshot);
        var present = scene.Lines.Select(static line => line.Frame.Line!.Id).ToHashSet(StringComparer.Ordinal);
        List<KeyValuePair<Fx.TokenId, Fx.StyleView>> tokens = [];
        List<KeyValuePair<Fx.LineId, Fx.StyleView>> lines = [];
        foreach (var line in snapshot.Lyrics.AllLines)
        {
            if (!present.Contains(line.Id)) continue;
            lines.Add(new(new(line.Id), View(PresetSceneStyleProjection.Lyrics(snapshot, line.Id, frame: scene, resolver: resolver).Style, materials)));
            foreach (var token in line.Tokens)
                tokens.Add(new(new(token.Id), View(PresetSceneStyleProjection.Lyrics(snapshot, line.Id, token.Id, scene, resolver).Style, materials)));
        }
        return session.WithSources(tokens, lines, (marks ?? MarkStyles(snapshot, scene))
                .Select(pair => new KeyValuePair<Fx.NodeId, Fx.StyleView>(pair.Key, View(pair.Value, materials))))
            .WithLoaded((loadedTokens ?? LoadedSourceStyles(snapshot, scene))
                .Select(pair => new KeyValuePair<Fx.TokenFamilyTarget, Fx.StyleView>(pair.Key, View(pair.Value, materials))),
                LoadedLineStyles(snapshot, scene).Select(pair => new KeyValuePair<Fx.LineTarget, Fx.StyleView>(pair.Key, View(pair.Value, materials))));
    }

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

    /// <summary>按真实字族装载取得外观；同一源元素的并行退出与新装载可以保留不同父路径基线。</summary>
    public static ImmutableDictionary<Fx.TokenFamilyTarget, LyricStyle> LoadedSourceStyles(ProjectSnapshot snapshot, SceneFrame scene)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(scene);
        var resolver = PresetSceneStyleProjection.Resolver(snapshot);
        return scene.Lines.SelectMany(line => line.Frame.Line!.Tokens.Select(token =>
            new KeyValuePair<Fx.TokenFamilyTarget, LyricStyle>(new(new(token.Id), new(line.BindingId!)),
                PresetSceneStyleProjection.LyricsForBinding(snapshot, line, token.Id, scene, resolver).Style))).ToImmutableDictionary();
    }

    /// <summary>按真实行装载取得冻结或当前源行外观。</summary>
    public static ImmutableDictionary<Fx.LineTarget, LyricStyle> LoadedLineStyles(ProjectSnapshot snapshot, SceneFrame scene)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(scene);
        var resolver = PresetSceneStyleProjection.Resolver(snapshot);
        return scene.Lines.Select(line => new KeyValuePair<Fx.LineTarget, LyricStyle>(new(new(line.Frame.Line!.Id), new(line.BindingId!)),
            PresetSceneStyleProjection.LyricsForBinding(snapshot, line, null, scene, resolver).Style)).ToImmutableDictionary();
    }

    /// <summary>无 Skia 类型的静态外观投影；渐变不会猜测纯色基色。</summary>
    /// <remarks>SDK 表面在 P3 第 6 步改为两态与多层；此前描边、阴影、辉光只投影未唱一态的最内层，描边位置固定为外侧。</remarks>
    public static Fx.StyleView View(LyricStyle style, PreparedSourceMaterials? materials = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new(Material(style.UnsungFill.Material, materials), Material(style.SungFill.Material, materials),
            style.Stroke is { } stroke ? new(stroke.WidthPx,
                Material(stroke.Material, materials),
                (Fx.StrokeJoin)stroke.Join, Fx.StrokePosition.Outside) : null,
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
        return ProjectDrawingAppearance(source, baseline, result, geometrySeed, sourcePackage);
    }
    /// <summary>从最终中立外观生成固定字形种子；不从父层白色种子重建包材质。</summary>
    public static LyricStyle DrawingAppearance(LyricStyle source, Fx.StyleView result, PreparedSourceMaterials? materials = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(result);
        return ProjectDrawingAppearance(source, View(source, materials), result, true, !SourceMaterialCatalog.Collect(source).IsEmpty);
    }
    private static LyricStyle ProjectDrawingAppearance(LyricStyle source, Fx.StyleView baseline, Fx.StyleView result, bool geometrySeed, bool sourcePackage)
    {
        // 未改动的成员保留源对象（含多层描边与已唱一侧的跟随）；改动的描边整表换成脚本给出的单层。
        // 含源包材质而描边未改动时只替换最内层（包材质换成几何种子），外圈各层原样保留（P3 设计 §6）。
        bool stroke = result.Stroke != baseline.Stroke, shadow = result.Shadow != baseline.Shadow, glow = result.Glow != baseline.Glow;
        TextAppearance unsung = source.Unsung with
        {
            Fill = result.UnsungMaterial == baseline.UnsungMaterial && !sourcePackage ? source.Unsung.Fill : DrawingMaterial(result.UnsungMaterial, geometrySeed),
            Strokes = stroke ? StrokeLayers.FromStroke(Stroke(result.Stroke, geometrySeed))
                : sourcePackage ? WithInnermost(source.Unsung.Strokes, Stroke(result.Stroke, geometrySeed)) : source.Unsung.Strokes,
            Shadow = shadow ? Shadow(result.Shadow) : source.Unsung.Shadow,
            Glow = glow ? Glow(result.Glow) : source.Unsung.Glow,
        };
        // 现行 SDK 的描边、阴影、辉光写入不分态（两态写入在 P3 第 6 步）：与基线（未唱一态）不同的成员两态一起变，
        // 已唱一侧改为跟随；与基线相同的写入视同未写，和整体无变化返回原实例一致。
        // 未变的成员保留源的已唱一侧，显式的包材质描边与未唱一样只留几何种子。
        var sungStrokes = source.Sung.Strokes;
        SungAppearance sung = source.Sung with
        {
            Fill = result.SungMaterial == baseline.SungMaterial && !sourcePackage ? source.Sung.Fill : DrawingMaterial(result.SungMaterial, geometrySeed),
            Strokes = stroke ? Followable.Follow<StrokeLayers>()
                : geometrySeed && sungStrokes is { FollowsUnsung: false, Value: { } explicitStrokes } ? Followable.Set(SeedStrokes(explicitStrokes)) : sungStrokes,
            Shadow = shadow ? Followable.Follow<ShadowSpec?>() : source.Sung.Shadow,
            Glow = glow ? Followable.Follow<GlowSpec?>() : source.Sung.Glow,
        };
        return source with { Unsung = unsung, Sung = sung, Opacity = Native(result.Opacity) };
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
        Unsung = style.Unsung with { Fill = SeedFill(style.Unsung.Fill), Strokes = SeedStrokes(style.Unsung.Strokes) },
        Sung = style.Sung with
        {
            Fill = SeedFill(style.Sung.Fill),
            Strokes = style.Sung.Strokes is { FollowsUnsung: false, Value: { } strokes } ? Followable.Set(SeedStrokes(strokes)) : style.Sung.Strokes,
        },
    };
    private static FillSpec SeedFill(FillSpec fill) => fill.Material is MaterialSpec.PackageMaterial ? new() { Color = RgbaColor.White } : fill;
    // 包材质只允许用在最内层（P3 设计 §10.5），其余层原样保留。
    private static StrokeLayers WithInnermost(StrokeLayers strokes, StrokeSpec? innermost) =>
        strokes.Count <= 1 || innermost is null ? StrokeLayers.FromStroke(innermost) : StrokeLayers.From([innermost, .. strokes.Skip(1)]);
    private static StrokeLayers SeedStrokes(StrokeLayers strokes) => strokes.Innermost is { Material: MaterialSpec.PackageMaterial } innermost
        ? StrokeLayers.From([innermost with { Material = new MaterialSpec.Solid(RgbaColor.White) }, .. strokes.Skip(1)]) : strokes;
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
            return StrokeSpec.Create(RgbaColor.White, Native(value.WidthPx), (StrokeJoin)value.Join);
        if (value.Material is Fx.MaterialValue.PackageMaterial)
            throw new PreparedFrameException("UnsupportedStrokeMaterial", "包描边须由材质协调器执行，不能作为普通绘制外观。");
        var material = Fill(value.Material);
        return StrokeSpec.Create(material.Gradient is null ? material.Color : null, Native(value.WidthPx),
            (StrokeJoin)value.Join) with { Gradient = material.Gradient };
    }
    private static ShadowSpec? Shadow(Fx.ShadowValue? value)
    {
        if (value is null) return null;
        Native(value.AngleDegrees * (Math.PI / 180));
        return ShadowSpec.Create(Color(value.Color), Native(value.Opacity), value.AngleDegrees, Native(value.DistancePx), Native(value.BlurRadiusPx));
    }
    private static GlowSpec? Glow(Fx.GlowValue? value) => value is null ? null
        : GlowSpec.Create(Color(value.Color), Native(value.Opacity), Native(value.BlurRadiusPx), Native(value.SpreadPx));
}
