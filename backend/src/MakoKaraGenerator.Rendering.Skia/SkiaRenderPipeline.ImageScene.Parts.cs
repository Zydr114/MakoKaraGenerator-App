using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>F3：先判空再分配；关闭时每个部件照常捕获，供回归对照。</summary>
    internal bool SkipKnownEmptyParts { get; set; } = true;

    /// <summary>本帧对象里必然没有像素的部件类别（F3）。</summary>
    /// <param name="Ruby">本行没有注音。</param>
    /// <param name="Stroke">所有可能参与绘制的样式在两态都没有启用描边。</param>
    /// <param name="Shadow">所有可能参与绘制的样式在两态都没有启用阴影。</param>
    private readonly record struct KnownEmptyParts(bool Ruby, bool Stroke, bool Shadow)
    {
        /// <summary>注音部件（含注音材质）一律可跳；描边只跳原样部件，描边材质仍按自身区域执行。</summary>
        internal bool Contains(SceneColorPart part, bool material) => Ruby && IsRuby(part)
            || Stroke && !material && part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke
            || Shadow && part is SceneColorPart.BaseShadow or SceneColorPart.RubyShadow;
    }

    private static KnownEmptyParts ObjectKnownEmptyParts(RenderRequest request, SceneDrawItem item)
    {
        bool stroke = false, shadow = false;
        foreach (var style in ObjectPartStyles(request, item))
        {
            stroke |= EitherState(style, static state => state.Stroke is { IsEnabled: true });
            shadow |= EitherState(style, static state => state.Shadow is { IsEnabled: true });
            if (stroke && shadow) break;
        }
        return new(item.Line?.Frame.Line?.RubyAnnotations.IsEmpty == true, !stroke, !shadow);
    }

    /// <summary>对象绘制时可能用到的全部样式，保守取并集：任一来源启用描边／阴影就照常捕获。</summary>
    /// <remarks>行：行基础样式与元素覆盖（原样绘制与注音所用）、本帧装载的源外观、脚本字族外观、基础字外观；
    /// 标记：其最终绘制样式。像素检查开启时，判空的部件仍按原样部件捕获并核对（见 DrawObject）。</remarks>
    private static IEnumerable<LyricStyle> ObjectPartStyles(RenderRequest request, SceneDrawItem item)
    {
        var prepared = request.PreparedFrame!;
        if (item.Mark is { } mark)
        {
            var target = prepared.FinalMarkGeometry.Single(value => value.Target.NodeId.Value == mark.NodeId && value.Target.CueId.Value == mark.CueId
                && value.Target.BindingId.Value == mark.Geometry.BindingId).Target;
            yield return prepared.MarkStyles.Any(output => output.Target == target) ? prepared.GetMarkDrawingStyle(target) : mark.Appearance.ToLyricStyle();
            yield break;
        }
        var scene = item.Line!;
        var line = scene.Frame.Line!;
        string binding = scene.BindingId!;
        var lineStyle = StaticStyleProjection.DrawingSeed(LineBaseStyle(request.Project.LyricStyle, line, scene.Branch));
        yield return lineStyle;
        foreach (var token in line.Tokens)
            if (token.StyleOverride is { } overlay) yield return ResolveElementStyle(lineStyle, overlay);
        foreach (var output in prepared.TokenStyles)
            if (output.Target.BindingId.Value == binding) yield return prepared.GetTokenDrawingStyle(output.Target);
        foreach (var output in prepared.BaseStyles)
            if (output.Target.BindingId.Value == binding) yield return prepared.GetBaseDrawingStyle(output.Target);
        foreach (var target in prepared.BaseAppearances.Keys)
            if (target.BindingId.Value == binding) yield return prepared.GetBaseDrawingStyle(target);
        var resolver = PresetSceneStyleProjection.Resolver(request.Project);
        foreach (var token in line.Tokens)
            yield return PresetSceneStyleProjection.LyricsForBinding(request.Project, scene, token.Id, prepared.HierarchyScene, resolver).Style;
    }
}
